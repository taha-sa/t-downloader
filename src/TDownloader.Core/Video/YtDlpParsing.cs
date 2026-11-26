using TDownloader.Core.Localization;
using System.Globalization;
using System.Text.Json;

namespace TDownloader.Core.Video;

/// <summary>Menüde gösterilen sadeleştirilmiş kalite seçeneği.</summary>
/// <summary>Other: ana videoya ait olmayan (kısa/önizleme/reklam olabilecek) satır; menüde en altta "Diğer dosyalar" altında gösterilir.</summary>
public sealed record QualityOption(string Label, string Format, string Ext, int Height, long Size, bool AudioOnly, bool Other = false);

public sealed record VideoInfo(string Title, List<QualityOption> Options);

public sealed record YtDlpProgress(string Status, long Downloaded, long Total, double Speed, double? EtaSeconds, string? StreamId = null);

/// <summary>yt-dlp çıktılarını (JSON, ilerleme satırları, hata metni) ayrıştıran saf fonksiyonlar.</summary>
public static class YtDlpParsing
{
    public const string ProgressPrefix = "TDL_PROG:";
    public const string FilePrefix = "TDL_FILE:";
    public const int MaxOptions = 25;

    /// <summary>--progress-template değeri; ProgressPrefix ile başlar, alanlar | ile ayrılır.</summary>
    public const string ProgressTemplate =
        "download:" + ProgressPrefix + "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s";

    // ---------------------------------------------------------------- ilerleme

    public static YtDlpProgress? ParseProgressLine(string? line)
    {
        if (line == null) return null;
        int i = line.IndexOf(ProgressPrefix, StringComparison.Ordinal);
        if (i < 0) return null;
        var parts = line[(i + ProgressPrefix.Length)..].Trim().Split('|');
        if (parts.Length < 6) return null;
        long? dl = L(parts[1]), total = L(parts[2]), est = L(parts[3]);
        double? speed = D(parts[4]), eta = D(parts[5]);
        string? sid = parts.Length > 6 && parts[6].Trim() is { Length: > 0 } x && x is not ("NA" or "None") ? x : null;
        return new YtDlpProgress(parts[0].Trim(), dl ?? 0, total ?? est ?? -1, speed ?? 0, eta, sid);
    }

    /// <summary>--print after_move:TDL_FILE:... satırından nihai dosya yolu.</summary>
    public static string? ParseFilePathLine(string? line)
    {
        if (line == null) return null;
        int i = line.IndexOf(FilePrefix, StringComparison.Ordinal);
        if (i < 0) return null;
        var p = line[(i + FilePrefix.Length)..].Trim();
        return p.Length == 0 ? null : p;
    }

    private static long? L(string s) => D(s) is double d ? (long)d : null;

    private static double? D(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s is "NA" or "None" or "null") return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? d : null;
    }

    // ---------------------------------------------------------------- biçim listesi

    private sealed record F(string Id, string Ext, double? Height, double? Fps, string V, string A, double? Tbr, long Size);

    /// <summary>yt-dlp -J çıktısından başlık ve sadeleştirilmiş kalite listesi (en fazla 25).</summary>
    public static VideoInfo ParseInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("formats", out _) &&
            root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entries.EnumerateArray()) { root = e; break; }
        }
        string title = Str(root, "title") ?? "video";
        var video = new List<F>();
        var audio = new List<F>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fmts.EnumerateArray())
            {
                var x = new F(
                    Str(f, "format_id") ?? "", Str(f, "ext") ?? "", Num(f, "height"), Num(f, "fps"),
                    Str(f, "vcodec") ?? "none", Str(f, "acodec") ?? "none", Num(f, "tbr"),
                    (long)(Num(f, "filesize") ?? Num(f, "filesize_approx") ?? 0));
                if (x.Id.Length == 0 || x.Ext == "mhtml" || x.Id.StartsWith("sb", StringComparison.Ordinal)) continue;
                bool hasV = x.V != "none" && x.V.Length > 0, hasA = x.A != "none" && x.A.Length > 0;
                if (hasV) video.Add(x);
                else if (hasA) audio.Add(x);
            }
        }

        var options = new List<QualityOption>();
        // Yüksekliğe göre grupla: mp4'ü, sonra yüksek bit hızını tercih et.
        foreach (var grp in video.Where(v => v.Height is > 0).GroupBy(v => (int)v.Height!.Value).OrderByDescending(g => g.Key))
        {
            var best = grp.OrderByDescending(v => v.Ext == "mp4" ? 1 : 0).ThenByDescending(v => v.Tbr ?? 0).First();
            bool combined = best.A != "none" && best.A.Length > 0;
            string fps = best.Fps is >= 50 ? ((int)Math.Round(best.Fps.Value)).ToString(CultureInfo.InvariantCulture) : "";
            string fmt = combined ? best.Id : best.Ext == "mp4" ? $"{best.Id}+bestaudio[ext=m4a]/{best.Id}+bestaudio" : $"{best.Id}+bestaudio";
            long size = best.Size;
            if (!combined && size > 0) size += audio.Select(a => a.Size).DefaultIfEmpty(0).Max();
            options.Add(new QualityOption($"{grp.Key}p{fps} {best.Ext}{SizeText(size)}", fmt, "mp4", grp.Key, size, false));
        }
        if (options.Count == 0 && video.Count > 0)
            options.Add(new QualityOption(Strings.BestQuality, "bv*+ba/b", "mp4", 0, 0, false)); // yükseklik bilinmiyor (bazı HLS)
        if (audio.Count > 0)
        {
            var a = audio.FirstOrDefault(x => x.Ext == "m4a") ?? audio.OrderByDescending(x => x.Tbr ?? 0).First();
            string sel = a.Ext == "m4a" ? "bestaudio[ext=m4a]/bestaudio" : "bestaudio";
            options.Add(new QualityOption(Strings.AudioOnly(a.Ext, SizeText(a.Size)), sel, a.Ext, -1, a.Size, true));
        }
        if (options.Count == 0)
            options.Add(new QualityOption(Strings.BestQuality, "best", "mp4", 0, 0, false));
        if (options.Count > MaxOptions) options = options.Take(MaxOptions).ToList();
        return new VideoInfo(title, options);
    }

    /// <summary>Varsayılan kalite ayarına ("best"|"1080"|...) en uygun seçeneğin indeksi.</summary>
    public static int PickDefault(IReadOnlyList<QualityOption> options, string? setting)
    {
        if (options.Count == 0) return -1;
        var vids = options.Where(o => !o.AudioOnly).ToList();
        if (vids.Count == 0) return 0;
        if (!int.TryParse(setting, out int cap) || cap <= 0) return options.ToList().IndexOf(vids[0]);
        var ok = vids.FirstOrDefault(o => o.Height <= cap && o.Height > 0) ?? vids[^1];
        return options.ToList().IndexOf(ok);
    }

    private static string SizeText(long b) => b <= 0 ? "" : $" ({b / 1048576.0:0.#}\u00a0MB)";

    private static string? Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    // ---------------------------------------------------------------- hata

    /// <summary>yt-dlp stderr metnini kullanıcıya gösterilecek Türkçe mesaja çevirir.</summary>
    public static string MapError(string? stderr)
    {
        var text = stderr ?? "";
        string lower = text.ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\bdrm\b")) return Strings.DRMProtectedContentCannotBeDownloadedChooseUnprotected2;
        if (lower.Contains("ffmpeg") && (lower.Contains("not found") || lower.Contains("not installed") || lower.Contains("no such file")))
            return Strings.FfmpegNotFoundSetItsPathInSettings;
        if (lower.Contains("unsupported url")) return Strings.YtDlpDoesNotSupportThisURLChoose;
        if (lower.Contains("sign in") || lower.Contains("not a bot") || lower.Contains("login required") || lower.Contains("age-restricted") || lower.Contains("members-only"))
            return Strings.ThisVideoRequiresSignInSignInThrough;
        if (lower.Contains("video unavailable") || lower.Contains("private video") || lower.Contains("has been removed") || lower.Contains("not available in your country"))
            return Strings.ThisVideoIsUnavailablePrivateRemovedOrRegion;
        if (lower.Contains("http error 403")) return Strings.TheServerDeniedAccessHTTP403SignIn;
        if (lower.Contains("http error 404")) return Strings.VideoNotFoundHTTP404CheckTheURL;
        if (lower.Contains("getaddrinfo") || lower.Contains("unable to download") || lower.Contains("timed out") || lower.Contains("connection"))
            return Strings.CouldNotReachTheServerCheckYourNetwork;
        var err = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
        if (err != null)
        {
            err = err[6..].Trim();
            return err.Length > 300 ? err[..300] + "…" : err;
        }
        var last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return last == null ? Strings.YtDlpFailedUpdateYtDlpAndTry : last.Length > 300 ? last[..300] + "…" : last;
    }
}

/// <summary>
/// Video+ses gibi ayrı akışların ilerleme satırlarını tek toplam ilerlemeye çevirir (yüzde ikinci akışta sıfırlanmaz,
/// asla geri gitmez) ve hızı kayan ortalamadan geçirir. Beklenen akış sayısı biçim dizgesinden ("137+140") bulunur.
/// </summary>
public sealed class YtDlpProgressAggregator
{
    private sealed class Stream { public string Key = ""; public long Downloaded, Total = -1; public bool Done; }
    private readonly List<Stream> _streams = new();
    private readonly int _expected;
    private readonly Util.SlidingSpeed _win = new(16, 4.0);
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private long _shownDl, _shownTotal = -1;

    public YtDlpProgressAggregator(int expectedStreams = 1) { _expected = Math.Max(1, expectedStreams); }

    /// <summary>"bv*+ba/b", "137+140" gibi biçimden ilk seçeneğin akış sayısı.</summary>
    public static int ExpectedStreams(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return 1;
        var first = format.Split('/')[0];
        return 1 + first.Count(c => c == '+');
    }

    public Queue.ExternalProgress Add(YtDlpProgress p, double? nowSeconds = null)
    {
        double now = nowSeconds ?? _sw.Elapsed.TotalSeconds;
        var cur = _streams.Count > 0 ? _streams[^1] : null;
        bool isNew = cur == null
            || (p.StreamId != null && cur.Key != p.StreamId)
            || (p.StreamId == null && (cur.Done || (cur.Downloaded > 0 && p.Downloaded < cur.Downloaded / 2)));
        if (isNew) { cur = new Stream { Key = p.StreamId ?? "#" + _streams.Count }; _streams.Add(cur); }
        cur!.Downloaded = Math.Max(cur.Downloaded, p.Downloaded);
        if (p.Total > 0) cur.Total = Math.Max(cur.Total, p.Total);
        if (string.Equals(p.Status, "finished", StringComparison.OrdinalIgnoreCase))
        {
            cur.Done = true;
            if (cur.Total < cur.Downloaded) cur.Total = cur.Downloaded;
        }

        long dl = 0, total = 0; bool known = true; long knownSum = 0;
        foreach (var s in _streams)
        {
            dl += s.Downloaded;
            if (s.Total > 0) { total += s.Total; knownSum += s.Total; } else known = false;
        }
        if (known && _streams.Count < _expected && knownSum > 0)
            total += (long)(knownSum / (double)_streams.Count * 0.1 * (_expected - _streams.Count));   // henüz başlamayan akış için ~%10 tahmin
        if (!known) total = -1;
        if (total > 0 && total < dl) total = dl;

        if (dl > _shownDl) _shownDl = dl;
        if (total > 0 && total < _shownTotal) total = _shownTotal;   // toplam küçülüp yüzdeyi oynatmasın
        if (total > 0) _shownTotal = total;
        double speed = _win.Add(now, _shownDl);
        double? eta = speed > 1 && total > 0 ? Math.Max(0, (total - _shownDl) / speed) : null;
        return new Queue.ExternalProgress(_shownDl, total, speed, eta);
    }
}
