using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;

namespace TDownloader.Core.Video;

public class HlsException : Exception
{
    public bool IsDrm { get; }
    /// <summary>Hataya yol açan HTTP durum kodu (varsa); 401/403 imzalı adres süresi dolmuş olabileceğini gösterir.</summary>
    public int? HttpStatus { get; }
    public HlsException(string message, bool drm = false, int? httpStatus = null) : base(message) { IsDrm = drm; HttpStatus = httpStatus; }
}

/// <summary>Hedef/çalışma sürücüsünde yeterli boş alan yok. Başka yolla (yt-dlp) yeniden denemek anlamsızdır.</summary>
public sealed class HlsDiskSpaceException : HlsException
{
    public HlsDiskSpaceException(string message) : base(message) { }
}

/// <summary>
/// yt-dlp'siz HLS: kalite listesini doğrudan master playlist'ten okur (birkaç yüz ms) ve parçaları kendi
/// HTTP istemcimizle (Referer/Origin/Cookie/UA ile) indirip birleştirir. Şifresiz ve AES-128 akışları destekler;
/// DRM'li (SAMPLE-AES, FairPlay, Widevine) akışlar reddedilir.
/// </summary>
public sealed class HlsClient
{
    public const string FormatPrefix = "hls\t";
    private readonly HttpClient _http;
    private readonly TimeSpan _playlistTimeout;

    /// <param name="playlistTimeout">Playlist isteği başına zaman aşımı (varsayılan 20 sn; test için değiştirilebilir).</param>
    public HlsClient(HttpClient? http = null, TimeSpan? playlistTimeout = null)
    {
        _http = http ?? EngineHttp.Shared;
        _playlistTimeout = playlistTimeout ?? TimeSpan.FromSeconds(20);
    }

    public static string MakeFormat(string variantUrl, string? audioUrl, int height) =>
        FormatPrefix + variantUrl + "\t" + (audioUrl ?? "") + "\t" + height.ToString(CultureInfo.InvariantCulture);

    public static bool IsHlsFormat(string? format) => format != null && format.StartsWith(FormatPrefix, StringComparison.Ordinal);

    public static (string Variant, string? Audio, int Height)? ParseFormat(string? format)
    {
        if (!IsHlsFormat(format)) return null;
        var p = format!.Split('\t');
        if (p.Length < 4) return null;
        int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h);
        return (p[1], string.IsNullOrEmpty(p[2]) ? null : p[2], h);
    }

    public static bool LooksLikeHlsUrl(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- ağ yardımcıları

    private async Task<string> GetTextAsync(string url, MediaContext ctx, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_playlistTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ctx.Apply(req);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new HlsException($"Sunucu playlist'i vermedi (HTTP {(int)resp.StatusCode}).", false, (int)resp.StatusCode);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (bytes.Length > 8 * 1024 * 1024) throw new HlsException("Playlist beklenenden büyük.");
            return Encoding.UTF8.GetString(bytes);
        }
        // İç zaman aşımı (dış iptal DEĞİL): çağıranın "kullanıcı duraklattı" sanmaması için gerçek hataya çevrilir.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HlsException("Sunucu playlist'e zamanında yanıt vermedi (zaman aşımı).");
        }
    }

    // ---------------------------------------------------------------- kalite listesi

    /// <summary>
    /// Master playlist'ten kalite listesi. Playlist HLS değilse <see cref="HlsException"/> fırlatır
    /// (çağıran yt-dlp'ye düşebilir). Medya playlist'i (tek kalite) verilmişse tek seçenek döner.
    /// </summary>
    public async Task<VideoInfo> GetInfoAsync(string url, MediaContext? ctx, string? title, CancellationToken ct = default)
    {
        ctx ??= MediaContext.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) throw new HlsException("Geçersiz adres.");
        string text = await GetTextAsync(url, ctx, ct).ConfigureAwait(false);
        if (!HlsParser.LooksLikeHls(text)) throw new HlsException("Adres bir HLS playlist'i değil.");
        string name = string.IsNullOrWhiteSpace(title) ? TitleFromUrl(url) : title!;

        if (!HlsParser.IsMaster(text))
        {
            var media = HlsParser.ParseMedia(text, url);
            if (media.Segments.Count == 0) throw new HlsException("Playlist'te parça yok.");
            if (!media.EndList) throw new HlsException("Canlı yayınlar desteklenmiyor.");
            if (HlsParser.DrmReason(media) is { } drm) throw new HlsException("Bu içerik DRM korumalı olduğu için indirilemez (" + drm + ").", true);
            int h = HlsParser.HeightFromUrl(url);
            if (h == 0) h = HlsParser.ResolutionHeight(text);
            string dur = media.TotalDuration > 0 ? " (" + FormatDuration(media.TotalDuration) + ")" : "";
            string label = h > 0 ? $"{h}p hls{dur}" : "Varsayılan kalite" + dur;
            return new VideoInfo(name, new List<QualityOption> { new(label, MakeFormat(url, null, h), "mp4", h, 0, false) });
        }

        var master = HlsParser.ParseMaster(text, url);
        if (master.Variants.Count == 0) throw new HlsException("Master playlist'te kalite yok.");

        // Süre: en yüksek varyantın playlist'inden (boyut tahmini için); başarısız olursa boyut gösterilmez.
        double duration = 0;
        try
        {
            var top = master.Variants.OrderByDescending(v => v.Bandwidth).First();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var m = HlsParser.ParseMedia(await GetTextAsync(top.Url, ctx, cts.Token).ConfigureAwait(false), top.Url);
            if (m.EndList) duration = m.TotalDuration;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or HlsException or UriFormatException)
        {
            if (ct.IsCancellationRequested) throw;
        }

        return new VideoInfo(name, BuildOptions(master, duration));
    }

    /// <summary>Uzak dosyanın bayt uzunluğu (HEAD; olmazsa Range 0-0 ile Content-Range). Bilinmiyorsa -1.</summary>
    private async Task<long> GetLengthAsync(string url, MediaContext ctx, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        using (var head = new HttpRequestMessage(HttpMethod.Head, url))
        {
            ctx.Apply(head);
            try
            {
                using var r = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (r.IsSuccessStatusCode && r.Content.Headers.ContentLength is > 0 and var l) return l;
            }
            catch (HttpRequestException) { }
        }
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        ctx.Apply(get);
        get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var resp = await _http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return -1;
        return resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? -1;
    }

    /// <summary>
    /// Bir sekme/çerçevede yakalanan TÜM m3u8 adreslerinden ve oynatıcı yapılandırmasından kalite listesi
    /// (bkz. <see cref="HlsQualityResolver"/>). <paramref name="probeMaster"/> yalnızca kullanıcı tıklamasında true olmalı.
    /// Hiçbir adres kullanılamazsa <see cref="HlsException"/> (çağıran yt-dlp'ye düşebilir).
    /// </summary>
    public Task<VideoInfo> GetInfoSetAsync(IReadOnlyList<HlsCandidate> candidates, MediaContext? ctx, string? title, bool probeMaster,
        Action<string>? log = null, CancellationToken ct = default)
    {
        ctx ??= MediaContext.Empty;
        var r = new HlsQualityResolver((u, c) => GetTextAsync(u, ctx, c), (u, c) => GetLengthAsync(u, ctx, c), log);
        string name = string.IsNullOrWhiteSpace(title) ? (candidates.Count > 0 ? TitleFromUrl(candidates[0].Url) : "video") : title!;
        return r.ResolveAsync(candidates, name, probeMaster, ct);
    }

    public static List<QualityOption> BuildOptions(HlsMaster master, double duration)
    {
        var options = new List<QualityOption>();
        // Çözünürlüğü olmayan varyantlar için URL'den (/720/, _480p) tahmin
        master = master with { Variants = master.Variants.Select(v => v.Height > 0 ? v : v with { Height = HlsParser.HeightFromUrl(v.Url) }).ToList() };
        // Aynı yükseklikte en yüksek bit hızlı varyant; yükseklik bilinmiyorsa bit hızına göre ayrı satır.
        var groups = master.Variants.GroupBy(v => v.Height > 0 ? "h" + v.Height : "b" + v.Bandwidth)
            .Select(g => g.OrderByDescending(v => v.Bandwidth).First())
            .OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).ToList();
        foreach (var v in groups)
        {
            string? audio = null;
            if (!string.IsNullOrEmpty(v.AudioGroup))
            {
                var cand = master.Audios.Where(a => a.GroupId == v.AudioGroup && a.Uri != null).ToList();
                audio = (cand.FirstOrDefault(a => a.Default) ?? cand.FirstOrDefault())?.Uri;
            }
            long size = duration > 0 && v.Bandwidth > 0 ? (long)(v.Bandwidth / 8.0 * duration) : 0;
            string fps = v.Fps >= 50 ? ((int)Math.Round(v.Fps)).ToString(CultureInfo.InvariantCulture) : "";
            string res = v.Height > 0 ? $"{v.Height}p{fps}" : $"{Math.Max(1, v.Bandwidth / 1000)} kb/sn";
            string sz = size > 0 ? $" (~{size / 1048576.0:0.#} MB)" : "";
            options.Add(new QualityOption($"{res} hls{sz}", MakeFormat(v.Url, audio, v.Height), "mp4", v.Height, size, false));
            if (options.Count >= YtDlpParsing.MaxOptions) break;
        }
        return options;
    }

    internal static string FormatDuration(double s)
    {
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static string TitleFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var seg = u.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 0 && !s.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)).LastOrDefault();
            if (!string.IsNullOrEmpty(seg) && seg.Length > 3) return Uri.UnescapeDataString(seg);
            return u.Host;
        }
        return "video";
    }

    // ---------------------------------------------------------------- indirme

    /// <param name="WorkDir">Kalıcı çalışma klasörü (ör. DwnlData\&lt;id&gt;\hls). Verilirse parçalar burada saklanır ve
    /// duraklat/yarıda kalma sonrası yalnızca eksik parçalar indirilir; yalnızca başarıda silinir. Verilmezse
    /// outBase + ".hlstmp" kullanılır ve her durumda silinir.</param>
    /// <param name="Log">İsteğe bağlı tanı günlüğü (devam/baştan başlama kararları).</param>
    /// <param name="FreeSpace">İsteğe bağlı (test): yol → boş bayt (bilinmiyorsa -1). Verilmezse DriveInfo kullanılır.</param>
    public sealed record Options(string? FfmpegPath, int Parallel = 6, int Retries = 4, TimeSpan? RetryDelay = null,
        string? WorkDir = null, Action<string>? Log = null, Func<string, long>? FreeSpace = null);

    private sealed class Progress
    {
        private readonly Action<ExternalProgress> _report;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly Util.SlidingSpeed _win = new(128, 4.0);
        private long _lastEmitMs = -1000, _shown;
        private double _speed;
        public const int MinReportMs = 250;
        /// <summary>Tamamlanmış parçaların baytı (gerçek toplam).</summary>
        public long Bytes;
        /// <summary>Şu an inmekte olan parçalardan okunan bayt (parça bitince/başarısız olunca düşülür).</summary>
        public long Live;
        public int Total;
        public double DoneDur, TotalDur;
        public Progress(Action<ExternalProgress> report) { _report = report; }
        /// <summary>Devamda önceden inmiş parçaları hız penceresini bozmadan sayar ve ilk ilerlemeyi bildirir.</summary>
        public void Seed(long bytes, double dur)
        {
            lock (this) { Bytes += bytes; DoneDur += dur; }
            if (bytes > 0) Report(0, 0);
        }
        public void AddBytes(long add, long releaseLive = 0) { lock (this) { Bytes += add; Live -= releaseLive; } }
        public (long Bytes, double DoneDur, double TotalDur) Snapshot() { lock (this) return (Bytes, DoneDur, TotalDur); }

        /// <summary>Parça indirilirken okunan baytlar (negatif: yeniden deneme için geri alma). En çok 250 ms'de bir bildirir.</summary>
        public void AddLive(long delta, bool countTotals)
        {
            lock (this)
            {
                Live += delta;
                if (!countTotals || delta <= 0) return;
                if (_sw.ElapsedMilliseconds - _lastEmitMs >= MinReportMs) Emit();
            }
        }

        public void Report(long add, double dur, long releaseLive = 0)
        {
            lock (this) { Bytes += add; DoneDur += dur; Live -= releaseLive; Emit(); }
        }

        // Kilit altında çağrılır.
        private void Emit()
        {
            long now = _sw.ElapsedMilliseconds;
            _lastEmitMs = now;
            long cur = Bytes + Live;
            // Gösterilen değer geri gitmez (gerçek toplam Bytes'ta bozulmadan durur).
            if (cur > _shown) _shown = cur;
            _speed = _win.Add(now / 1000.0, _shown);
            long est = DoneDur > 0 && TotalDur > 0 ? (long)(Bytes / DoneDur * TotalDur) : -1;
            if (est > 0 && est < _shown) est = _shown;
            double? eta = _speed > 1 && est > 0 ? Math.Max(0, (est - _shown) / _speed) : null;
            _report(new ExternalProgress(_shown, est, _speed, eta));
        }
        /// <summary>İndirme bitti, birleştirme/dönüştürme sürüyor: çubuk %100'de kalır, Phase aşamayı söyler.</summary>
        public void ReportPhase(string phase)
        {
            lock (this) _report(new ExternalProgress(Bytes, Bytes, 0, null, phase));
        }
    }

    /// <summary>
    /// Format = <see cref="MakeFormat"/>. Çıktı: outBase + ".mp4" (ffmpeg varsa) veya ".ts"/".mp4" ham birleşim.
    /// Dönen yol son dosyadır. Parçalar çalışma klasöründe ayrı dosyalar olarak durur (bkz. <see cref="Options.WorkDir"/>);
    /// birleştirilirken tek tek silinir (disk tepesi ~2x). Ayrı ses izi inemezse hata verilir (sessiz film üretilmez).
    /// </summary>
    public async Task<string> DownloadAsync(string variantUrl, string? audioUrl, MediaContext? ctx, string outBase, Options opt,
        Action<ExternalProgress> report, CancellationToken ct)
    {
        ctx ??= MediaContext.Empty;
        if (audioUrl != null && opt.FfmpegPath == null)
            throw new HlsException("Bu video ses ve görüntüyü ayrı akışlarda sunuyor; birleştirmek için ffmpeg gerekli ama bulunamadı.");
        bool persistent = opt.WorkDir != null;
        string tmpDir = opt.WorkDir ?? (outBase + ".hlstmp");
        if (Path.GetDirectoryName(Path.GetFullPath(outBase)) is { Length: > 0 } outDir) Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(tmpDir);
        var prog = new Progress(report);
        var store = new HlsStateStore(tmpDir, variantUrl, audioUrl);
        bool success = false;
        try
        {
            var videoRaw = await DownloadTrackAsync(variantUrl, ctx, tmpDir, "", outBase, opt, prog, store, ct, countTotals: true).ConfigureAwait(false);
            (string path, bool fmp4)? audioRaw = null;
            if (audioUrl != null)
            {
                try { audioRaw = await DownloadTrackAsync(audioUrl, ctx, tmpDir, "a-", outBase, opt, prog, store, ct, countTotals: false).ConfigureAwait(false); }
                catch (HlsException ex) when (!ex.IsDrm && ex is not HlsDiskSpaceException)
                {
                    // Sessiz film üretme: ses izi inmediyse tüm indirme hata verir (inen parçalar korunur).
                    throw new HlsException("Ses izi indirilemedi: " + ex.Message, false, ex.HttpStatus);
                }
            }

            string ffmpeg = opt.FfmpegPath != null ? FfmpegExe(opt.FfmpegPath) : "";
            if (opt.FfmpegPath != null)
            {
                // Nihai ad ffmpeg'den ÖNCE belirlenir: kullanıcının mevcut dosyası asla ezilmez/silinmez (C4).
                string final = Util.UniqueName.Path(outBase + ".mp4");
                // ffmpeg geçici adla yazar; yalnızca başarıda atomik yeniden adlandırılır, iptal/hatada geçici dosya silinir (C3).
                string partOut = final[..^4] + ".part.mp4";
                double totalDur = prog.Snapshot().TotalDur;
                Action<double> onTime = sec =>
                {
                    int pct = totalDur > 0 ? (int)Math.Clamp(sec / totalDur * 100, 0, 100) : 0;
                    prog.ReportPhase($"Dönüştürülüyor %{pct}");
                };
                prog.ReportPhase("Dönüştürülüyor %0");
                // 1. deneme: hızlı başlangıç (faststart) + gerekirse ADTS->ASC; 2. deneme: bitstream filtresiz, düz kopya.
                foreach (bool plain in new[] { false, true })
                {
                    ct.ThrowIfCancellationRequested();
                    TryDelete(partOut);   // önceki yarım çalışmadan artık
                    var args = FfmpegArgs(videoRaw.path, audioRaw?.path, partOut, bsf: !plain && (!videoRaw.fmp4 || audioRaw != null), faststart: !plain);
                    bool ok;
                    try { ok = await RunFfmpegAsync(ffmpeg, args, onTime, ct).ConfigureAwait(false); }
                    catch { TryDelete(partOut); throw; }   // iptal/beklenmeyen hata: yarım çıktı bırakılmaz
                    if (ok && File.Exists(partOut) && new FileInfo(partOut).Length > 0)
                    {
                        final = MoveNoOverwrite(partOut, final);
                        // Ara dosyalar hemen silinir: kalıcı klasör silinene kadar diskte durmasınlar.
                        TryDelete(videoRaw.path);
                        if (audioRaw != null) TryDelete(audioRaw.Value.path);
                        success = true;
                        return final;
                    }
                    TryDelete(partOut);
                }
                if (audioRaw != null)
                    throw new HlsException("ffmpeg ses ve görüntüyü birleştiremedi; sessiz video üretilmedi (inen parçalar korundu).");
            }
            // Mevcut dosya silinmez/ezilmez: çakışırsa "ad (1).uzantı" (C4).
            string raw = MoveNoOverwrite(videoRaw.path, Util.UniqueName.Path(outBase + (videoRaw.fmp4 ? ".mp4" : ".ts")));
            success = true;
            return raw;
        }
        finally
        {
            // Kalıcı kipte yalnızca başarıda silinir: duraklat/hata sonrası inmiş parçalar devam için saklanır.
            if (!persistent || success)
            {
                try { Directory.Delete(tmpDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else store.Save();
        }
    }

    /// <summary>src'yi dest'e taşır; hedef bu arada oluştuysa (yarış) yeni benzersiz ad seçer, asla ezmez. Gerçek hedefi döner.</summary>
    private static string MoveNoOverwrite(string src, string dest)
    {
        for (int i = 0; ; i++)
        {
            try { File.Move(src, dest, overwrite: false); return dest; }
            catch (IOException) when (File.Exists(dest) && i < 100) { dest = Util.UniqueName.Path(dest); }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static List<string> FfmpegArgs(string video, string? audio, string final, bool bsf, bool faststart)
    {
        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-i", video };
        if (audio != null) { args.Add("-i"); args.Add(audio); }
        args.AddRange(new[] { "-c", "copy" });
        if (bsf) { args.Add("-bsf:a"); args.Add("aac_adtstoasc"); }
        if (audio != null) args.AddRange(new[] { "-map", "0:v:0?", "-map", "1:a:0?" });
        if (faststart) args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(final);
        return args;
    }

    private static string FfmpegExe(string loc) =>
        Directory.Exists(loc) ? Path.Combine(loc, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg") : loc;

    private static async Task<bool> RunFfmpegAsync(string exe, List<string> args, Action<double>? onTime, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            Util.ProcessJob.Assign(p);   // uygulama çökerse ffmpeg yetim kalmasın
            try { p.StandardInput.Close(); } catch (IOException) { }
            // -progress pipe:1 satırları: out_time_us=<mikrosaniye> (eski sürümlerde out_time_ms de mikrosaniye).
            var outTask = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await p.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                    {
                        if ((line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                            && long.TryParse(line.AsSpan(line.IndexOf('=') + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
                            onTime?.Invoke(us / 1_000_000.0);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }, CancellationToken.None);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (Exception) { } });
            try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Süreç tamamen kapanmadan çağıran geçici çıktıyı silmeye çalışırsa dosya kilitli kalır.
                try { if (!p.HasExited) p.Kill(true); p.WaitForExit(5000); } catch (Exception) { }
                throw;
            }
            await outTask.ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    // ---- boş alan

    private static long DefaultFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { return -1; }
    }

    private static string RootOf(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; } catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return ""; }
    }

    private static string Mb(long b) => (b / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Hedef sürücüde boş alan kontrolü. estTotal: parçaların tahmini toplam boyutu, onDisk: şu an diskte olanı.
    /// Çalışma sürücüsü kalan indirme (×1.1), hedef sürücü son dosya (×1.1) için; aynı sürücüyse ikisi toplanır.
    /// Birleştirme parçaları tek tek sildiği için ek bir birleşik kopya payı gerekmez.
    /// </summary>
    private static void CheckSpace(Options opt, string workDir, string outBase, long estTotal, long onDisk, string stage)
    {
        if (estTotal <= 0) return;
        var free = opt.FreeSpace ?? DefaultFreeSpace;
        long needWork = (long)(Math.Max(0, estTotal - onDisk) * 1.1), needOut = (long)(estTotal * 1.1);
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outBase + ".x")) ?? outBase;
        var checks = string.Equals(RootOf(workDir), RootOf(outDir), StringComparison.OrdinalIgnoreCase)
            ? new[] { (workDir, needWork + needOut) }
            : new[] { (workDir, needWork), (outDir, needOut) };
        foreach (var (path, need) in checks)
        {
            long f = free(path);
            if (f >= 0 && f < need)
                throw new HlsDiskSpaceException($"Diskte yeterli boş alan yok ({stage}): {Mb(f)} boş, yaklaşık {Mb(need)} gerekli ({Path.GetPathRoot(Path.GetFullPath(path))}).");
        }
    }

    private async Task<(string path, bool fmp4)> DownloadTrackAsync(string playlistUrl, MediaContext ctx, string workDir, string prefix, string outBase, Options opt,
        Progress prog, HlsStateStore store, CancellationToken ct, bool countTotals)
    {
        // Devamda playlist HER ZAMAN yeniden çekilir: imzalı eski segment URL'leri kullanılmaz.
        async Task<(HlsMedia media, string mediaUrl)> LoadMediaAsync(CancellationToken tk)
        {
            string text = await GetTextAsync(playlistUrl, ctx, tk).ConfigureAwait(false);
            if (!HlsParser.LooksLikeHls(text)) throw new HlsException("Adres bir HLS playlist'i değil.");
            string url = playlistUrl;
            if (HlsParser.IsMaster(text))
            {
                // Master verilmişse en yüksek kaliteyi al.
                var m = HlsParser.ParseMaster(text, playlistUrl);
                var best = m.Variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).FirstOrDefault()
                    ?? throw new HlsException("Master playlist'te kalite yok.");
                url = best.Url;
                text = await GetTextAsync(url, ctx, tk).ConfigureAwait(false);
            }
            return (HlsParser.ParseMedia(text, url), url);
        }

        var (media, mediaUrl) = await LoadMediaAsync(ct).ConfigureAwait(false);
        if (media.Segments.Count == 0) throw new HlsException("Playlist'te parça yok.");
        if (!media.EndList) throw new HlsException("Canlı yayınlar desteklenmiyor.");
        if (HlsParser.DrmReason(media) is { } drm) throw new HlsException("Bu içerik DRM korumalı olduğu için indirilemez (" + drm + ").", true);

        bool fmp4 = media.Map != null || media.Segments[0].Url.Split('?')[0].EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
                    || media.Segments[0].Url.Split('?')[0].EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        int count = media.Segments.Count;
        if (countTotals) { lock (prog) { prog.Total = count; prog.TotalDur = media.TotalDuration; } }

        // ---- önceki durumla karşılaştır
        var cur = HlsStateStore.Describe(prefix, playlistUrl, media, fmp4);
        var prev = store.TryLoadTrack(prefix);
        bool sameList = false;
        if (prev != null)
        {
            sameList = prev.Count == cur.Count && prev.DurationsHash == cur.DurationsHash && prev.Fmp4 == cur.Fmp4
                       && prev.PlaylistUrl == cur.PlaylistUrl && prev.HasMap == cur.HasMap;
            if (!sameList)
            {
                opt.Log?.Invoke($"HLS devam: playlist uyuşmuyor (parça {prev.Count}->{cur.Count}); önceki parçalar silindi, baştan başlanıyor.");
                HlsStateStore.WipeTrack(workDir, prefix);
            }
            else if (prev.UrlsHash != cur.UrlsHash)
                opt.Log?.Invoke("HLS devam: parça adresleri değişmiş (imzalı URL), sayı/süre aynı; mevcut parçalar korunuyor.");
            else
                opt.Log?.Invoke("HLS devam: playlist aynı; yalnızca eksik parçalar indirilecek.");
        }
        else HlsStateStore.WipeTrack(workDir, prefix);   // durum yok: artık/yetim dosyalara güvenme
        HlsStateStore.DeleteTemps(workDir, prefix);
        store.SetTrack(cur);
        store.Save();

        string outPath = Path.Combine(workDir, prefix == "" ? "merged.v" : "merged.a");
        string okPath = outPath + ".ok";
        string marker = $"{cur.Count}|{cur.DurationsHash}|{(cur.Fmp4 ? 1 : 0)}";
        // Önceki çalışmada birleştirme tamamlandıysa (parçalar birleştirilirken silindi) yeniden indirme/birleştirme yok.
        if (sameList && File.Exists(outPath) && File.Exists(okPath) && TryReadText(okPath) == marker)
        {
            long len = new FileInfo(outPath).Length;
            opt.Log?.Invoke("HLS devam: parçalar zaten birleştirilmiş; birleşik dosya kullanılıyor.");
            if (countTotals) prog.Seed(len, media.TotalDuration); else prog.AddBytes(len);
            store.SetCompleted(prefix, count);
            return (outPath, fmp4);
        }
        TryDelete(okPath);

        var keyCache = new Dictionary<string, byte[]>();
        var keyLock = new SemaphoreSlim(1, 1);
        // Çerez sınırı ctx.CookieHost'tur (çerezin alındığı host); isteğin kendi host'undan türetilmez.

        // Parça adresleri imzalı olabilir: 401/403'te playlist yenilenir, yeni adresler bu dizide durur.
        var segs = media.Segments.ToArray();
        int generation = 0;
        var refreshLock = new SemaphoreSlim(1, 1);

        async Task<bool> RefreshAsync(int seenGeneration, CancellationToken tk)
        {
            await refreshLock.WaitAsync(tk).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref generation) != seenGeneration) return true;   // başka işçi yeniledi
                try
                {
                    var (fresh, _) = await LoadMediaAsync(tk).ConfigureAwait(false);
                    if (fresh.Segments.Count != count) return false;
                    Volatile.Write(ref segs, fresh.Segments.ToArray());
                    Interlocked.Increment(ref generation);
                    opt.Log?.Invoke("HLS: parça 401/403 verdi; playlist yenilendi (imzalı adres), parça yeniden denenecek.");
                    return true;
                }
                catch (HlsException) { return false; }
                catch (HttpRequestException) { return false; }
                catch (OperationCanceledException) when (!tk.IsCancellationRequested) { return false; }   // iç zaman aşımı: yenileme başarısız
            }
            finally { refreshLock.Release(); }
        }

        async Task<byte[]> GetKey(string uri, CancellationToken tk)
        {
            await keyLock.WaitAsync(tk).ConfigureAwait(false);
            try
            {
                if (keyCache.TryGetValue(uri, out var k)) return k;
                string cacheFile = Path.Combine(workDir, "key-" + HlsStateStore.ShortHash(uri) + ".key");
                try
                {
                    k = await FetchBytesAsync(uri, ctx, 0, -1, opt, tk).ConfigureAwait(false);
                    if (k.Length != 16) throw new HlsException("AES anahtarı geçersiz (" + k.Length + " bayt).");
                    if (opt.WorkDir != null) { try { File.WriteAllBytes(cacheFile, k); } catch (IOException) { } }
                }
                catch (HlsException) when (opt.WorkDir != null && File.Exists(cacheFile) && new FileInfo(cacheFile).Length == 16)
                {
                    k = File.ReadAllBytes(cacheFile);   // anahtar adresi süresi dolmuş olabilir: önbellek
                }
                return keyCache[uri] = k;
            }
            finally { keyLock.Release(); }
        }

        async Task<byte[]> Fetch(HlsSegment s, CancellationToken tk, Action<long>? onBytes)
        {
            var data = await FetchBytesAsync(s.Url, ctx, s.ByteOffset, s.ByteLength, opt, tk, onBytes).ConfigureAwait(false);
            if (s.Key is { Method: "AES-128" } key && key.Uri != null)
            {
                var k = await GetKey(key.Uri, tk).ConfigureAwait(false);
                var iv = key.Iv ?? SequenceIv(s.Sequence);
                data = Decrypt(data, k, iv);
            }
            return fmp4 ? data : StripFakeHeader(data);
        }

        async Task<byte[]> FetchSegment(int idx, CancellationToken tk, Action<long>? onBytes)
        {
            int refreshes = 0;
            while (true)
            {
                int gen = Volatile.Read(ref generation);
                var seg = Volatile.Read(ref segs)[idx];
                try { return await Fetch(seg, tk, onBytes).ConfigureAwait(false); }
                catch (HlsException ex) when (ex.HttpStatus is 401 or 403 && refreshes < 2)
                {
                    refreshes++;
                    if (!await RefreshAsync(gen, tk).ConfigureAwait(false)) throw;
                }
            }
        }

        // init segment (fMP4)
        string initFile = Path.Combine(workDir, prefix + "init.map");
        if (media.Map != null && !(File.Exists(initFile) && new FileInfo(initFile).Length > 0))
        {
            var init = await FetchBytesAsync(media.Map.Url, ctx, media.Map.ByteOffset, media.Map.ByteLength, opt, ct).ConfigureAwait(false);
            await HlsStateStore.WriteAtomicAsync(initFile, init, ct).ConfigureAwait(false);
        }

        // ---- inmiş parçalar: kaynak gerçeği dosya varlığı + boyut > 0
        var missing = new List<int>();
        long seedBytes = 0; double seedDur = 0; int done = 0;
        for (int i = 0; i < count; i++)
        {
            var fi = new FileInfo(HlsStateStore.SegPath(workDir, prefix, i));
            if (fi.Exists && fi.Length > 0) { seedBytes += fi.Length; seedDur += media.Segments[i].Duration; done++; }
            else missing.Add(i);
        }
        if (done > 0) opt.Log?.Invoke($"HLS: {done}/{count} parça zaten inmiş, {missing.Count} parça indirilecek.");
        if (countTotals) prog.Seed(seedBytes, seedDur); else prog.AddBytes(seedBytes);
        store.SetCompleted(prefix, done);

        // Başlarken boş alan: devamda tahmin hemen yapılabilir; yoksa ilk birkaç parçadan sonra (aşağıda).
        int spaceChecked = 0;
        if (countTotals && missing.Count > 0 && seedDur > 0 && media.TotalDuration > 0)
        {
            spaceChecked = 1;
            CheckSpace(opt, workDir, outBase, (long)(seedBytes / seedDur * media.TotalDuration), seedBytes, "indirme başlarken");
        }

        // ---- eksikleri sınırlı paralellikle indir
        if (missing.Count > 0)
        {
            int par = Math.Clamp(opt.Parallel, 1, 16);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int cursor = -1, finished = 0;
            Exception? firstError = null;
            async Task Worker()
            {
                try
                {
                    while (true)
                    {
                        int k = Interlocked.Increment(ref cursor);
                        if (k >= missing.Count) return;
                        linked.Token.ThrowIfCancellationRequested();
                        int idx = missing[k];
                        var duration = Volatile.Read(ref segs)[idx].Duration;
                        long counted = 0;
                        void OnBytes(long n) { Interlocked.Add(ref counted, n); prog.AddLive(n, countTotals); }
                        byte[] data;
                        try { data = await FetchSegment(idx, linked.Token, OnBytes).ConfigureAwait(false); }
                        catch { prog.AddBytes(0, Interlocked.Read(ref counted)); throw; }
                        long released = Interlocked.Read(ref counted);
                        await HlsStateStore.WriteAtomicAsync(HlsStateStore.SegPath(workDir, prefix, idx), data, linked.Token).ConfigureAwait(false);
                        store.SegmentDone(prefix);
                        if (countTotals) prog.Report(data.Length, duration, released); else prog.AddBytes(data.Length, released);
                        if (countTotals && Volatile.Read(ref spaceChecked) == 0
                            && Interlocked.Increment(ref finished) >= Math.Min(4, missing.Count)
                            && Interlocked.CompareExchange(ref spaceChecked, 1, 0) == 0)
                        {
                            var (b, d, t) = prog.Snapshot();
                            if (d > 0 && t > 0) CheckSpace(opt, workDir, outBase, (long)(b / d * t), b, "indirme");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Yalnızca DIŞ iptal (kullanıcı) hata sayılmaz; iç kaynaklı iptal/zaman aşımı gerçek hatadır.
                    if (!(ex is OperationCanceledException && ct.IsCancellationRequested)) Interlocked.CompareExchange(ref firstError, ex, null);
                    try { linked.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(par, missing.Count)).Select(_ => Task.Run(Worker))).ConfigureAwait(false);
            store.Save();
            ct.ThrowIfCancellationRequested();
            if (firstError != null)
            {
                // Zaman aşımı gibi iç iptaller HlsException'a çevrilir: çağıran "duraklatıldı" sanmasın.
                if (firstError is OperationCanceledException) throw new HlsException("İndirme zaman aşımı nedeniyle durdu.", false, null);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
            }
            // Hata kaydı olmadan işçiler durduysa (bir iptal kaçtıysa) eksik parçalarla birleştirmeye ASLA geçilmez.
            if (linked.IsCancellationRequested)
                throw new HlsException("İndirme beklenmedik şekilde yarıda kesildi (eksik parça).");
        }

        // ---- sırayla birleştir: parça dosyaları birleştikçe silinir (disk tepesi ~2x yerine ~1x + son dosya)
        long onDisk = 0;
        for (int i = 0; i < count; i++) { var fi = new FileInfo(HlsStateStore.SegPath(workDir, prefix, i)); if (fi.Exists) onDisk += fi.Length; }
        CheckSpace(opt, workDir, outBase, onDisk, onDisk, "birleştirmeden önce");
        int lastPct = -1;
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
        {
            if (media.Map != null) await CopyFileAsync(initFile, fs, ct).ConfigureAwait(false);
            for (int i = 0; i < count; i++)
            {
                string sp = HlsStateStore.SegPath(workDir, prefix, i);
                await CopyFileAsync(sp, fs, ct).ConfigureAwait(false);
                TryDelete(sp);
                int pct = (i + 1) * 100 / count;
                if (pct != lastPct) { lastPct = pct; prog.ReportPhase($"Birleştiriliyor %{pct}"); }
            }
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        // Birleşik dosya tamam: devamda (ffmpeg aşamasında duraklatılırsa) parçalar yeniden indirilmesin.
        await File.WriteAllTextAsync(okPath, marker, CancellationToken.None).ConfigureAwait(false);   // iptal gelse bile işaretçi yazılsın
        if (media.Map != null) TryDelete(initFile);
        return (outPath, fmp4);
    }

    private static string? TryReadText(string path)
    {
        try { return File.ReadAllText(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task CopyFileAsync(string path, Stream dest, CancellationToken ct)
    {
        await using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        await src.CopyToAsync(dest, 1 << 16, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> FetchBytesAsync(string url, MediaContext ctx, long offset, long length, Options opt, CancellationToken ct, Action<long>? onBytes = null)
    {
        int attempt = 0;
        var delay = opt.RetryDelay ?? TimeSpan.FromMilliseconds(500);
        while (true)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ctx.Apply(req);
                if (length > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    // 401/403: imzalı adres süresi dolmuş olabilir (çağıran playlist'i yenileyip dener); 404/410: kalıcı.
                    if (code is 401 or 403 or 404 or 410) throw new HlsException($"Sunucu parçayı vermedi (HTTP {code}).", false, code);
                    throw HttpStatusException.From(resp);
                }
                if (onBytes == null) return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                // Akış okuma: okunan baytlar anında bildirilir; hata olursa bu denemenin baytları geri alınır.
                long got = 0;
                var rented = ArrayPool<byte>.Shared.Rent(32 * 1024);
                try
                {
                    long cl = resp.Content.Headers.ContentLength ?? 0;
                    using var ms = new MemoryStream(cl > 0 && cl < int.MaxValue ? (int)cl : 0);
                    await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    int n;
                    while ((n = await stream.ReadAsync(rented.AsMemory(), cts.Token).ConfigureAwait(false)) > 0)
                    {
                        ms.Write(rented, 0, n); got += n; onBytes(n);
                    }
                    return ms.ToArray();
                }
                catch { if (got > 0) onBytes(-got); throw; }
                finally { ArrayPool<byte>.Shared.Return(rented); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HlsException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                if (ex is HttpStatusException hs) retryAfter = hs.RetryAfter;
                if (++attempt > opt.Retries) throw new HlsException("Parça indirilemedi: " + ex.Message);
                // Üstel bekleme (gecikme x 2^(deneme-1), en çok 30 sn) + jitter; Retry-After varsa alt sınır.
                double ms = Math.Min(delay.TotalMilliseconds * Math.Pow(2, attempt - 1), 30_000) * (1.0 + Random.Shared.NextDouble() * 0.25);
                if (retryAfter is { } ra) ms = Math.Max(ms, Math.Min(ra.TotalMilliseconds, 30_000));
                await Task.Delay(TimeSpan.FromMilliseconds(ms), ct).ConfigureAwait(false);
            }
        }
    }

    public static byte[] SequenceIv(int seq)
    {
        var iv = new byte[16];
        iv[12] = (byte)(seq >> 24); iv[13] = (byte)(seq >> 16); iv[14] = (byte)(seq >> 8); iv[15] = (byte)seq;
        return iv;
    }

    public static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        try { return aes.DecryptCbc(data, iv, PaddingMode.PKCS7); }
        catch (CryptographicException) { return aes.DecryptCbc(data, iv, PaddingMode.None); }
    }

    /// <summary>
    /// Bazı siteler TS parçalarının başına sahte bir resim (PNG/JPG/GIF) başlığı ekler. MPEG-TS senkron baytı (0x47)
    /// 188 bayt aralıkla tekrar eden ilk konumdan başlanarak fazlalık atılır. TS gibi görünmüyorsa veri aynen döner.
    /// </summary>
    public static byte[] StripFakeHeader(byte[] d)
    {
        if (d.Length < 188 * 3 || (d[0] == 0x47 && d[188] == 0x47)) return d;
        int max = Math.Min(d.Length - 188 * 2 - 1, 16384);
        for (int i = 0; i < max; i++)
            if (d[i] == 0x47 && d[i + 188] == 0x47 && d[i + 376] == 0x47)
                return i == 0 ? d : d[i..];
        return d;
    }
}
