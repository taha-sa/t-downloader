using System.Globalization;
using System.Text.Json;

namespace TDownloader.Core.Video;

/// <summary>Menüde gösterilen sadeleştirilmiş kalite seçeneği.</summary>
/// <summary>Other: ana videoya ait olmayan (kısa/önizleme/reklam olabilecek) satır; menüde en altta "Diğer dosyalar" altında gösterilir.</summary>
public sealed record QualityOption(string Label, string Format, string Ext, int Height, long Size, bool AudioOnly, bool Other = false);

public sealed record VideoInfo(string Title, List<QualityOption> Options);

public sealed record YtDlpProgress(string Status, long Downloaded, long Total, double Speed, double? EtaSeconds);

/// <summary>yt-dlp çıktılarını (JSON, ilerleme satırları, hata metni) ayrıştıran saf fonksiyonlar.</summary>
public static class YtDlpParsing
{
    public const string ProgressPrefix = "TDL_PROG:";
    public const string FilePrefix = "TDL_FILE:";
    public const int MaxOptions = 25;

    /// <summary>--progress-template değeri; ProgressPrefix ile başlar, alanlar | ile ayrılır.</summary>
    public const string ProgressTemplate =
        "download:" + ProgressPrefix + "%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s";

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
        return new YtDlpProgress(parts[0].Trim(), dl ?? 0, total ?? est ?? -1, speed ?? 0, eta);
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
            options.Add(new QualityOption("En iyi kalite", "bv*+ba/b", "mp4", 0, 0, false)); // yükseklik bilinmiyor (bazı HLS)
        if (audio.Count > 0)
        {
            var a = audio.FirstOrDefault(x => x.Ext == "m4a") ?? audio.OrderByDescending(x => x.Tbr ?? 0).First();
            string sel = a.Ext == "m4a" ? "bestaudio[ext=m4a]/bestaudio" : "bestaudio";
            options.Add(new QualityOption($"Sadece ses ({a.Ext}){SizeText(a.Size)}", sel, a.Ext, -1, a.Size, true));
        }
        if (options.Count == 0)
            options.Add(new QualityOption("En iyi kalite", "best", "mp4", 0, 0, false));
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

    private static string SizeText(long b) => b <= 0 ? "" : $" ({b / 1048576.0:0.#} MB)";

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
        if (lower.Contains("drm")) return "Bu içerik DRM korumalı olduğu için indirilemez.";
        if (lower.Contains("ffmpeg") && (lower.Contains("not found") || lower.Contains("not installed") || lower.Contains("no such file")))
            return "ffmpeg bulunamadı. Video ve ses birleştirilemiyor; Seçenekler > Video sekmesinden ffmpeg yolunu belirtin.";
        if (lower.Contains("unsupported url")) return "Bu adres yt-dlp tarafından desteklenmiyor.";
        if (lower.Contains("sign in") || lower.Contains("not a bot") || lower.Contains("login required") || lower.Contains("age-restricted") || lower.Contains("members-only"))
            return "Bu video için oturum açmak gerekiyor (çerezler bu sürümde yt-dlp'ye aktarılmıyor).";
        if (lower.Contains("video unavailable") || lower.Contains("private video") || lower.Contains("has been removed") || lower.Contains("not available in your country"))
            return "Video kullanılamıyor (özel, silinmiş veya bölgenize kapalı olabilir).";
        if (lower.Contains("http error 403")) return "Sunucu erişimi reddetti (HTTP 403).";
        if (lower.Contains("http error 404")) return "Video bulunamadı (HTTP 404).";
        if (lower.Contains("getaddrinfo") || lower.Contains("unable to download") || lower.Contains("timed out") || lower.Contains("connection"))
            return "Ağ hatası: sunucuya ulaşılamadı.";
        var err = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
        if (err != null)
        {
            err = err[6..].Trim();
            return err.Length > 300 ? err[..300] + "…" : err;
        }
        var last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return last == null ? "yt-dlp başarısız oldu." : last.Length > 300 ? last[..300] + "…" : last;
    }
}
