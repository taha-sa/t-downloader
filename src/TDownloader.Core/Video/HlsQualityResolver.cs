using System.Globalization;
using System.Text.RegularExpressions;

namespace TDownloader.Core.Video;

/// <summary>Kalite aday adresi. Label/Height oynatıcı yapılandırmasından gelen ipuçlarıdır; FromPlayer oynatıcıdan okunduğunu belirtir.</summary>
public sealed record HlsCandidate(string Url, string? Label = null, int Height = 0, bool FromPlayer = false);

/// <summary>
/// Bir sekme/çerçevede yakalanan TÜM m3u8 adreslerinden (master ve/veya medya playlist'leri) ve oynatıcı yapılandırmasından
/// kalite listesi kurar. Ağ erişimi enjekte edilir (test edilebilir). Kalite ÖNCEDEN alınmaz: yalnızca kullanıcı düğmeye
/// tıklayınca çağrılır.
/// <list type="number">
/// <item>Master varsa (yakalanmış) onun varyantları.</item>
/// <item>Yoksa ve izin varsa: aynı dizinde yaygın adlarla master aranır (en fazla <see cref="MaxMasterProbes"/> küçük istek).</item>
/// <item>Bulunamazsa her medya playlist'i ayrı kalitedir: çözünürlük ipucu → URL kalıbı → playlist RESOLUTION →
///       ilk parçanın bit hızı tahmini sırası ("Kalite 1/2/3"). Süresi çok kısa olanlar "Diğer"e düşer.</item>
/// </list>
/// </summary>
public sealed class HlsQualityResolver
{
    public delegate Task<string> TextFetcher(string url, CancellationToken ct);
    /// <summary>Uzak dosyanın bayt uzunluğu; bilinmiyorsa -1.</summary>
    public delegate Task<long> LengthFetcher(string url, CancellationToken ct);

    public const int MaxCandidates = 10;
    public const int MaxMasterProbes = 4;
    public static readonly string[] ProbeNames = { "master.m3u8", "playlist.m3u8", "index.m3u8" };

    private readonly TextFetcher _text;
    private readonly LengthFetcher _len;
    private readonly Action<string>? _log;

    public HlsQualityResolver(TextFetcher text, LengthFetcher len, Action<string>? log = null)
    {
        _text = text; _len = len; _log = log;
    }

    private void Log(string s) { try { _log?.Invoke(s); } catch (Exception) { } }
    private static string S(string url) => Util.MediaLog.Sanitize(url);

    private sealed record Loaded(HlsCandidate C, string? Text, string? Error);
    private sealed record MediaPl(HlsCandidate C, HlsMedia M, string Text);

    private static bool IsHttp(string u) => Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == "http" || x.Scheme == "https");

    private async Task<Loaded> LoadAsync(HlsCandidate c, CancellationToken ct)
    {
        try { return new Loaded(c, await _text(c.Url, ct).ConfigureAwait(false), null); }
        catch (Exception ex) when (ex is HttpRequestException or HlsException or UriFormatException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new Loaded(c, null, ex.Message);
        }
    }

    public async Task<VideoInfo> ResolveAsync(IReadOnlyList<HlsCandidate> input, string title, bool probeMaster, CancellationToken ct = default)
    {
        var cands = input.Where(c => IsHttp(c.Url)).GroupBy(c => c.Url, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(x => x.FromPlayer) ?? g.First()).Take(MaxCandidates).ToList();
        if (cands.Count == 0) throw new HlsException("Akış adresi yok.");

        var loaded = await Task.WhenAll(cands.Select(c => LoadAsync(c, ct))).ConfigureAwait(false);
        var medias = new List<MediaPl>();
        HlsMaster? master = null; string? masterUrl = null;
        string? drm = null, lastErr = null;
        foreach (var l in loaded)
        {
            if (l.Text == null) { lastErr = l.Error; Log($"fetch-fail url={S(l.C.Url)} err={Short(l.Error)}"); continue; }
            if (!HlsParser.LooksLikeHls(l.Text)) { Log($"not-hls url={S(l.C.Url)}"); continue; }
            if (HlsParser.IsMaster(l.Text))
            {
                var m = HlsParser.ParseMaster(l.Text, l.C.Url);
                Log($"master url={S(l.C.Url)} variants={m.Variants.Count} res=[{string.Join(",", m.Variants.Select(v => v.Height > 0 ? v.Width + "x" + v.Height : "-"))}]");
                if (master == null && m.Variants.Count > 0) { master = m; masterUrl = l.C.Url; }
                continue;
            }
            var media = HlsParser.ParseMedia(l.Text, l.C.Url);
            string kind = media.Segments.Count == 0 ? "empty" : !media.EndList ? "live" : IsSubtitles(media) ? "subtitles" : "ok";
            Log($"media url={S(l.C.Url)} segs={media.Segments.Count} dur={(int)media.TotalDuration}s kind={kind} res={Res(l)}");
            if (kind != "ok") continue;
            if (HlsParser.DrmReason(media) is { } reason) { drm = reason; continue; }
            medias.Add(new MediaPl(l.C, media, l.Text));
        }

        // 1) yakalanmış master
        if (master != null) return await FromMasterAsync(master, masterUrl!, title, ct).ConfigureAwait(false);

        // 2) master bulmayı dene (yalnızca izinliyse; küçük, sınırlı istekler)
        if (probeMaster && medias.Count > 0)
        {
            var found = await ProbeMasterAsync(medias[0].C.Url, cands.Select(c => c.Url).ToList(), ct).ConfigureAwait(false);
            if (found != null) return await FromMasterAsync(found.Value.Master, found.Value.Url, title, ct).ConfigureAwait(false);
        }

        if (medias.Count == 0)
        {
            // Doğrulanamayan ama oynatıcının bildirdiği çözünürlüklü adresler (ör. çerez/başlık yüzünden 403): yine de sun.
            var unverified = loaded.Where(l => l.Text == null && l.C.FromPlayer && QualityHeight(l.C, null) > 0).ToList();
            if (unverified.Count > 0) return new VideoInfo(title, UnverifiedOptions(unverified));
            if (drm != null) throw new HlsException("Bu içerik DRM korumalı olduğu için indirilemez (" + drm + ").", true);
            throw new HlsException(lastErr ?? "Kullanılabilir (VOD) HLS playlist'i bulunamadı.");
        }

        // 3) her medya playlist'i ayrı kalite
        return new VideoInfo(title, await FromMediaListAsync(medias, loaded, ct).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- master

    private async Task<VideoInfo> FromMasterAsync(HlsMaster master, string url, string title, CancellationToken ct)
    {
        double duration = 0;
        try
        {
            var top = master.Variants.OrderByDescending(v => v.Bandwidth).First();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var m = HlsParser.ParseMedia(await _text(top.Url, cts.Token).ConfigureAwait(false), top.Url);
            if (m.EndList) duration = m.TotalDuration;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or HlsException or UriFormatException)
        {
            if (ct.IsCancellationRequested) throw;
        }
        var opts = HlsClient.BuildOptions(master, duration);
        Log($"using-master url={S(url)} options={opts.Count}");
        return new VideoInfo(title, opts);
    }

    /// <summary>Medya playlist'inin dizininde (ve bir üstünde) master arar. Ağ yükü sınırlı: en fazla <see cref="MaxMasterProbes"/> istek.</summary>
    private async Task<(HlsMaster Master, string Url)?> ProbeMasterAsync(string mediaUrl, List<string> known, CancellationToken ct)
    {
        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var u)) return null;
        string query = u.Query;                                  // imzalı adreslerde sorgu aynen taşınır (günlüğe yazılmaz)
        var dir = new Uri(new Uri(u.GetLeftPart(UriPartial.Path)), ".");
        var parent = new Uri(dir, "..");
        var urls = new List<string>();
        foreach (var n in ProbeNames) urls.Add(new Uri(dir, n).ToString() + query);
        urls.Add(new Uri(parent, "master.m3u8").ToString() + query);
        string Key(string x) => x.Split('?')[0];
        var knownKeys = known.Select(Key).ToHashSet(StringComparer.Ordinal);
        urls = urls.Where(x => !knownKeys.Contains(Key(x))).Distinct().Take(MaxMasterProbes).ToList();
        if (urls.Count == 0) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        var results = await Task.WhenAll(urls.Select(async x =>
        {
            try { return (Url: x, Text: await _text(x, cts.Token).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is HttpRequestException or HlsException or UriFormatException or OperationCanceledException) { return (Url: x, Text: (string?)null); }
        })).ConfigureAwait(false);
        foreach (var r in results)
        {
            bool ok = r.Text != null && HlsParser.LooksLikeHls(r.Text) && HlsParser.IsMaster(r.Text);
            Log($"probe-master url={S(r.Url)} -> {(r.Text == null ? "yok" : ok ? "master" : "master-degil")}");
            if (!ok) continue;
            var m = HlsParser.ParseMaster(r.Text!, r.Url);
            if (m.Variants.Count > 0) return (m, r.Url);
        }
        return null;
    }

    // ---------------------------------------------------------------- master'sız: her playlist ayrı kalite

    private static readonly HashSet<string> SubExt = new(StringComparer.OrdinalIgnoreCase) { ".vtt", ".webvtt", ".srt", ".ttml", ".dfxp" };
    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase) { ".aac", ".m4a", ".mp3", ".ac3", ".eac3", ".opus" };

    private static string ExtOf(string url)
    {
        string p = url.Split('?', '#')[0];
        return Path.GetExtension(p);
    }
    private static bool IsSubtitles(HlsMedia m) => m.Segments.Count > 0 && m.Segments.All(s => SubExt.Contains(ExtOf(s.Url)));
    private static bool IsAudioOnly(HlsMedia m) => m.Segments.Count > 0 && m.Segments.All(s => AudioExt.Contains(ExtOf(s.Url)));

    private static string Res(Loaded l)
    {
        int h = HlsParser.ResolutionHeight(l.Text);
        return h > 0 ? h + "p" : "-";
    }
    private static string Short(string? s) => string.IsNullOrEmpty(s) ? "-" : (s.Length > 60 ? s[..60] : s).Replace('\n', ' ');

    /// <summary>İpucu etiketi → URL kalıbı → playlist RESOLUTION sırasıyla yükseklik.</summary>
    private static int QualityHeight(HlsCandidate c, string? text)
    {
        if (c.Height > 0) return c.Height;
        int h = HlsParser.HeightFromLabel(c.Label);
        if (h > 0) return h;
        h = HlsParser.HeightFromUrl(c.Url);
        return h > 0 ? h : HlsParser.ResolutionHeight(text);
    }

    private sealed class Entry
    {
        public MediaPl P = null!;
        public int Height;
        public bool Audio;
        public long BytesPerSec;      // 0 = bilinmiyor
        public int Order;
    }

    private async Task<List<QualityOption>> FromMediaListAsync(List<MediaPl> medias, Loaded[] loaded, CancellationToken ct)
    {
        double maxDur = medias.Max(m => m.M.TotalDuration);
        var main = new List<Entry>(); var other = new List<MediaPl>();
        int order = 0;
        foreach (var m in medias)
        {
            if (m.M.TotalDuration >= maxDur * 0.9) main.Add(new Entry { P = m, Height = QualityHeight(m.C, m.Text), Audio = IsAudioOnly(m.M), Order = order++ });
            else other.Add(m);
        }
        // Aynı yükseklikte yinelenenleri ele (ilk gelen kalır)
        main = main.GroupBy(e => e.Height > 0 && !e.Audio ? "h" + e.Height : "u" + e.Order).Select(g => g.First()).ToList();

        // Çözünürlüğü bilinmeyenleri ilk parçanın bit hızı tahminiyle sırala (yalnızca ≥2 ise gerekir); boyut tahmini de buradan.
        var unknown = main.Where(e => e.Height == 0).ToList();
        if (unknown.Count >= 2)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            await Task.WhenAll(unknown.Select(async e =>
            {
                try
                {
                    var seg = e.P.M.Segments[0];
                    long len = seg.ByteLength > 0 ? seg.ByteLength : await _len(seg.Url, cts.Token).ConfigureAwait(false);
                    if (len > 0 && seg.Duration > 0) e.BytesPerSec = (long)(len / seg.Duration);
                }
                catch (Exception ex) when (ex is HttpRequestException or HlsException or UriFormatException or OperationCanceledException) { }
            })).ConfigureAwait(false);
            foreach (var e in unknown) Log($"bitrate url={S(e.P.C.Url)} Bps={e.BytesPerSec}");
        }

        var ordered = main.Where(e => !e.Audio && e.Height > 0).OrderByDescending(e => e.Height).ThenBy(e => e.Order).ToList();
        ordered.AddRange(main.Where(e => !e.Audio && e.Height == 0).OrderByDescending(e => e.BytesPerSec).ThenBy(e => e.Order));
        var audios = main.Where(e => e.Audio).OrderBy(e => e.Order).ToList();

        var options = new List<QualityOption>();
        int unlabeled = 0;
        bool sole = ordered.Count + audios.Count == 1;
        foreach (var e in ordered)
        {
            double dur = e.P.M.TotalDuration;
            long size = e.BytesPerSec > 0 ? (long)(e.BytesPerSec * dur) : 0;
            string sz = size > 0 ? $" (~{size / 1048576.0:0.#} MB)" : "";
            string label;
            if (e.Height > 0) label = $"{e.Height}p hls{sz}" + (sole && dur > 0 ? " (" + HlsClient.FormatDuration(dur) + ")" : "");
            else if (sole) label = "Varsayılan kalite" + (dur > 0 ? " (" + HlsClient.FormatDuration(dur) + ")" : "");
            else label = $"Kalite {++unlabeled} hls{sz}";
            options.Add(new QualityOption(label, HlsClient.MakeFormat(e.P.C.Url, null, e.Height), "mp4", e.Height, size, false));
        }
        int ai = 0;
        foreach (var e in audios)
            options.Add(new QualityOption($"Ses {++ai} hls", HlsClient.MakeFormat(e.P.C.Url, null, 0), "m4a", 0, 0, true));

        // Doğrulanamayan ama oynatıcının bildirdiği çözünürlükler (henüz listede olmayanlar)
        var haveHeights = options.Select(o => o.Height).ToHashSet();
        var unv = loaded.Where(l => l.Text == null && l.C.FromPlayer).Where(l => { int h = QualityHeight(l.C, null); return h > 0 && !haveHeights.Contains(h); }).ToList();
        if (unv.Count > 0) options.AddRange(UnverifiedOptions(unv));

        options = options.OrderByDescending(o => !o.AudioOnly ? 1 : 0).ThenByDescending(o => o.Height > 0 ? o.Height : 1).ToList();
        int k = 0;
        foreach (var m in other.OrderByDescending(m => m.M.TotalDuration))
            options.Add(new QualityOption($"Diğer akış {++k} ({HlsClient.FormatDuration(m.M.TotalDuration)}) hls", HlsClient.MakeFormat(m.C.Url, null, QualityHeight(m.C, m.Text)), "mp4", 0, 0, false, Other: true));
        if (options.Count > YtDlpParsing.MaxOptions) options = options.Take(YtDlpParsing.MaxOptions).ToList();
        Log($"using-media-list main={main.Count} other={other.Count} options={options.Count}");
        return options;
    }

    private static List<QualityOption> UnverifiedOptions(List<Loaded> ls) =>
        ls.Select(l => (L: l, H: QualityHeight(l.C, null))).GroupBy(x => x.H).Select(g => g.First()).OrderByDescending(x => x.H)
          .Select(x => new QualityOption($"{x.H}p hls (doğrulanamadı)", HlsClient.MakeFormat(x.L.C.Url, null, x.H), "mp4", x.H, 0, false)).ToList();
}
