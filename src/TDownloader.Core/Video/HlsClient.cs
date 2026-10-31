using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;

namespace TDownloader.Core.Video;

public sealed class HlsException : Exception
{
    public bool IsDrm { get; }
    public HlsException(string message, bool drm = false) : base(message) { IsDrm = drm; }
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

    public HlsClient(HttpClient? http = null) { _http = http ?? EngineHttp.Shared; }

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
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ctx.Apply(req, new Uri(url).Host);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HlsException($"Sunucu playlist'i vermedi (HTTP {(int)resp.StatusCode}).");
        var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        if (bytes.Length > 8 * 1024 * 1024) throw new HlsException("Playlist beklenenden büyük.");
        return Encoding.UTF8.GetString(bytes);
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
            ctx.Apply(head, new Uri(url).Host);
            try
            {
                using var r = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (r.IsSuccessStatusCode && r.Content.Headers.ContentLength is > 0 and var l) return l;
            }
            catch (HttpRequestException) { }
        }
        using var get = new HttpRequestMessage(HttpMethod.Get, url);
        ctx.Apply(get, new Uri(url).Host);
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
    public sealed record Options(string? FfmpegPath, int Parallel = 6, int Retries = 4, TimeSpan? RetryDelay = null,
        string? WorkDir = null, Action<string>? Log = null);

    private sealed class Progress
    {
        private readonly Action<ExternalProgress> _report;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly Queue<(long t, long b)> _win = new();
        public long Bytes;
        public int Total;
        public double DoneDur, TotalDur;
        public Progress(Action<ExternalProgress> report) { _report = report; }
        /// <summary>Devamda önceden inmiş parçaları hız penceresini bozmadan sayar ve ilk ilerlemeyi bildirir.</summary>
        public void Seed(long bytes, double dur)
        {
            lock (this) { Bytes += bytes; DoneDur += dur; }
            if (bytes > 0) Report(0, 0);
        }
        public void AddBytes(long add) { lock (this) Bytes += add; }
        public void Report(long add, double dur)
        {
            lock (this)
            {
                Bytes += add; DoneDur += dur;
                long now = _sw.ElapsedMilliseconds;
                _win.Enqueue((now, Bytes));
                while (_win.Count > 1 && now - _win.Peek().t > 4000) _win.Dequeue();
                var first = _win.Peek();
                double speed = now > first.t ? (Bytes - first.b) * 1000.0 / (now - first.t) : 0;
                long est = DoneDur > 0 && TotalDur > 0 ? (long)(Bytes / DoneDur * TotalDur) : -1;
                double? eta = speed > 1 && est > 0 ? Math.Max(0, (est - Bytes) / speed) : null;
                _report(new ExternalProgress(Bytes, est, speed, eta));
            }
        }
    }

    /// <summary>
    /// Format = <see cref="MakeFormat"/>. Çıktı: outBase + ".mp4" (ffmpeg varsa) veya ".ts"/".mp4" ham birleşim.
    /// Dönen yol son dosyadır. Parçalar çalışma klasöründe ayrı dosyalar olarak durur (bkz. <see cref="Options.WorkDir"/>).
    /// </summary>
    public async Task<string> DownloadAsync(string variantUrl, string? audioUrl, MediaContext? ctx, string outBase, Options opt,
        Action<ExternalProgress> report, CancellationToken ct)
    {
        ctx ??= MediaContext.Empty;
        bool persistent = opt.WorkDir != null;
        string tmpDir = opt.WorkDir ?? (outBase + ".hlstmp");
        Directory.CreateDirectory(tmpDir);
        var prog = new Progress(report);
        var store = new HlsStateStore(tmpDir, variantUrl, audioUrl);
        bool success = false;
        try
        {
            var videoRaw = await DownloadTrackAsync(variantUrl, ctx, tmpDir, "", opt, prog, store, ct, countTotals: true).ConfigureAwait(false);
            (string path, bool fmp4)? audioRaw = null;
            if (audioUrl != null && opt.FfmpegPath != null)
            {
                try { audioRaw = await DownloadTrackAsync(audioUrl, ctx, tmpDir, "a-", opt, prog, store, ct, countTotals: false).ConfigureAwait(false); }
                catch (HlsException) { audioRaw = null; }   // ayrı ses alınamazsa görüntüyle yetin
            }

            string ffmpeg = opt.FfmpegPath != null ? FfmpegExe(opt.FfmpegPath) : "";
            if (opt.FfmpegPath != null)
            {
                string final = outBase + ".mp4";
                var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", videoRaw.path };
                if (audioRaw != null) { args.Add("-i"); args.Add(audioRaw.Value.path); }
                args.AddRange(new[] { "-c", "copy" });
                if (!videoRaw.fmp4 || audioRaw != null) { args.Add("-bsf:a"); args.Add("aac_adtstoasc"); }
                if (audioRaw != null) { args.AddRange(new[] { "-map", "0:v:0?", "-map", "1:a:0?" }); }
                args.AddRange(new[] { "-movflags", "+faststart", final });
                if (await RunFfmpegAsync(ffmpeg, args, ct).ConfigureAwait(false) && File.Exists(final) && new FileInfo(final).Length > 0)
                { success = true; return final; }
                // ffmpeg başarısız (ör. ses bsf'i uymadı): ham birleşimi koru
                if (File.Exists(final)) { try { File.Delete(final); } catch (IOException) { } }
                args = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", videoRaw.path, "-c", "copy", final };
                if (await RunFfmpegAsync(ffmpeg, args, ct).ConfigureAwait(false) && File.Exists(final) && new FileInfo(final).Length > 0)
                { success = true; return final; }
            }
            string raw = outBase + (videoRaw.fmp4 ? ".mp4" : ".ts");
            if (File.Exists(raw)) File.Delete(raw);
            File.Move(videoRaw.path, raw);
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

    private static string FfmpegExe(string loc) =>
        Directory.Exists(loc) ? Path.Combine(loc, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg") : loc;

    private static async Task<bool> RunFfmpegAsync(string exe, List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            Util.ProcessJob.Assign(p);   // uygulama çökerse ffmpeg yetim kalmasın
            try { p.StandardInput.Close(); } catch (IOException) { }
            _ = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (Exception) { } });
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    private async Task<(string path, bool fmp4)> DownloadTrackAsync(string playlistUrl, MediaContext ctx, string workDir, string prefix, Options opt,
        Progress prog, HlsStateStore store, CancellationToken ct, bool countTotals)
    {
        // Devamda playlist HER ZAMAN yeniden çekilir: imzalı eski segment URL'leri kullanılmaz.
        string text = await GetTextAsync(playlistUrl, ctx, ct).ConfigureAwait(false);
        if (!HlsParser.LooksLikeHls(text)) throw new HlsException("Adres bir HLS playlist'i değil.");
        string mediaUrl = playlistUrl;
        if (HlsParser.IsMaster(text))
        {
            // Master verilmişse en yüksek kaliteyi al.
            var m = HlsParser.ParseMaster(text, playlistUrl);
            var best = m.Variants.OrderByDescending(v => v.Height).ThenByDescending(v => v.Bandwidth).FirstOrDefault()
                ?? throw new HlsException("Master playlist'te kalite yok.");
            mediaUrl = best.Url;
            text = await GetTextAsync(mediaUrl, ctx, ct).ConfigureAwait(false);
        }
        var media = HlsParser.ParseMedia(text, mediaUrl);
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
        if (prev != null)
        {
            bool sameList = prev.Count == cur.Count && prev.DurationsHash == cur.DurationsHash && prev.Fmp4 == cur.Fmp4
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

        var keyCache = new Dictionary<string, byte[]>();
        var keyLock = new SemaphoreSlim(1, 1);
        string cookieHost = new Uri(mediaUrl).Host;

        async Task<byte[]> GetKey(string uri, CancellationToken tk)
        {
            await keyLock.WaitAsync(tk).ConfigureAwait(false);
            try
            {
                if (keyCache.TryGetValue(uri, out var k)) return k;
                string cacheFile = Path.Combine(workDir, "key-" + HlsStateStore.ShortHash(uri) + ".key");
                try
                {
                    k = await FetchBytesAsync(uri, ctx, cookieHost, 0, -1, opt, tk).ConfigureAwait(false);
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

        async Task<byte[]> Fetch(HlsSegment s, CancellationToken tk)
        {
            var data = await FetchBytesAsync(s.Url, ctx, cookieHost, s.ByteOffset, s.ByteLength, opt, tk).ConfigureAwait(false);
            if (s.Key is { Method: "AES-128" } key && key.Uri != null)
            {
                var k = await GetKey(key.Uri, tk).ConfigureAwait(false);
                var iv = key.Iv ?? SequenceIv(s.Sequence);
                data = Decrypt(data, k, iv);
            }
            return fmp4 ? data : StripFakeHeader(data);
        }

        // init segment (fMP4)
        string initFile = Path.Combine(workDir, prefix + "init.map");
        if (media.Map != null && !(File.Exists(initFile) && new FileInfo(initFile).Length > 0))
        {
            var init = await FetchBytesAsync(media.Map.Url, ctx, cookieHost, media.Map.ByteOffset, media.Map.ByteLength, opt, ct).ConfigureAwait(false);
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

        // ---- eksikleri sınırlı paralellikle indir
        if (missing.Count > 0)
        {
            int par = Math.Clamp(opt.Parallel, 1, 16);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int cursor = -1;
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
                        var seg = media.Segments[idx];
                        var data = await Fetch(seg, linked.Token).ConfigureAwait(false);
                        await HlsStateStore.WriteAtomicAsync(HlsStateStore.SegPath(workDir, prefix, idx), data, linked.Token).ConfigureAwait(false);
                        store.SegmentDone(prefix);
                        if (countTotals) prog.Report(data.Length, seg.Duration); else prog.AddBytes(data.Length);
                    }
                }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException) Interlocked.CompareExchange(ref firstError, ex, null);
                    try { linked.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            await Task.WhenAll(Enumerable.Range(0, Math.Min(par, missing.Count)).Select(_ => Task.Run(Worker))).ConfigureAwait(false);
            store.Save();
            ct.ThrowIfCancellationRequested();
            if (firstError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
        }

        // ---- sırayla birleştir
        string outPath = Path.Combine(workDir, prefix == "" ? "merged.v" : "merged.a");
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
        {
            if (media.Map != null) await CopyFileAsync(initFile, fs, ct).ConfigureAwait(false);
            for (int i = 0; i < count; i++)
                await CopyFileAsync(HlsStateStore.SegPath(workDir, prefix, i), fs, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        return (outPath, fmp4);
    }

    private static async Task CopyFileAsync(string path, Stream dest, CancellationToken ct)
    {
        await using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        await src.CopyToAsync(dest, 1 << 16, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> FetchBytesAsync(string url, MediaContext ctx, string cookieHost, long offset, long length, Options opt, CancellationToken ct)
    {
        int attempt = 0;
        var delay = opt.RetryDelay ?? TimeSpan.FromMilliseconds(500);
        while (true)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ctx.Apply(req, cookieHost);
                if (length > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    if (code is 401 or 403 or 404 or 410) throw new HlsException($"Sunucu parçayı vermedi (HTTP {code}).");
                    throw new HttpRequestException($"HTTP {code}");
                }
                return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (HlsException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                if (++attempt > opt.Retries) throw new HlsException("Parça indirilemedi: " + ex.Message);
                await Task.Delay(TimeSpan.FromMilliseconds(delay.TotalMilliseconds * attempt), ct).ConfigureAwait(false);
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
