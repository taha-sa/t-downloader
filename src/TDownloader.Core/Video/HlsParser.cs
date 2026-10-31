using System.Globalization;
using System.Text.RegularExpressions;

namespace TDownloader.Core.Video;

public sealed record HlsVariant(string Url, long Bandwidth, int Width, int Height, double Fps, string? Codecs, string? AudioGroup);
public sealed record HlsAudio(string GroupId, string? Uri, string? Name, bool Default);
public sealed record HlsMaster(List<HlsVariant> Variants, List<HlsAudio> Audios);

public sealed record HlsKey(string Method, string? Uri, byte[]? Iv, string? KeyFormat);
public sealed record HlsSegment(string Url, double Duration, int Sequence, long ByteOffset, long ByteLength, HlsKey? Key);
public sealed record HlsMap(string Url, long ByteOffset, long ByteLength);
public sealed record HlsMedia(List<HlsSegment> Segments, HlsMap? Map, bool EndList, double TotalDuration);

/// <summary>M3U8 (HLS) ayrıştırıcı: master (kalite listesi) ve medya playlist'i. Ağ erişimi yok; saf fonksiyonlar.</summary>
public static class HlsParser
{
    private static readonly Regex AttrRe = new("([A-Z0-9-]+)=(\"[^\"]*\"|[^,]*)", RegexOptions.Compiled);

    public static bool LooksLikeHls(string? text) => text != null && text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);
    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static Dictionary<string, string> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRe.Matches(s))
        {
            var v = m.Groups[2].Value;
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
            d[m.Groups[1].Value] = v;
        }
        return d;
    }

    public static string Resolve(string baseUrl, string rel)
    {
        rel = rel.Trim();
        if (Uri.TryCreate(rel, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https")) return rel;
        return Uri.TryCreate(new Uri(baseUrl), rel, out var u) ? u.ToString() : rel;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim('\r', ' ', '\t', '﻿')).Where(l => l.Length > 0);

    public static HlsMaster ParseMaster(string text, string baseUrl)
    {
        var variants = new List<HlsVariant>();
        var audios = new List<HlsAudio>();
        Dictionary<string, string>? pending = null;
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) { pending = ParseAttrs(line[18..]); continue; }
            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = ParseAttrs(line[13..]);
                if (a.TryGetValue("TYPE", out var t) && t.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) && a.TryGetValue("GROUP-ID", out var g))
                    audios.Add(new HlsAudio(g, a.TryGetValue("URI", out var u) ? Resolve(baseUrl, u) : null,
                        a.GetValueOrDefault("NAME"), a.GetValueOrDefault("DEFAULT", "NO").Equals("YES", StringComparison.OrdinalIgnoreCase)));
                continue;
            }
            if (line[0] == '#') continue;
            if (pending == null) continue;
            long avg = ParseLong(pending.GetValueOrDefault("AVERAGE-BANDWIDTH"));
            long bw = avg > 0 ? avg : ParseLong(pending.GetValueOrDefault("BANDWIDTH"));
            int w = 0, h = 0;
            if (pending.TryGetValue("RESOLUTION", out var res))
            {
                var p = res.Split('x', 'X');
                if (p.Length == 2) { int.TryParse(p[0], out w); int.TryParse(p[1], out h); }
            }
            double fps = double.TryParse(pending.GetValueOrDefault("FRAME-RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
            variants.Add(new HlsVariant(Resolve(baseUrl, line), bw, w, h, fps, pending.GetValueOrDefault("CODECS"), pending.GetValueOrDefault("AUDIO")));
            pending = null;
        }
        return new HlsMaster(variants, audios);
    }

    private static long ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static HlsMedia ParseMedia(string text, string baseUrl)
    {
        var segs = new List<HlsSegment>();
        HlsMap? map = null;
        HlsKey? key = null;
        int seq = 0;
        double dur = 0, total = 0;
        long lastEnd = 0, brLen = -1, brOff = -1;
        bool end = false;
        foreach (var line in Lines(text))
        {
            if (line[0] == '#')
            {
                if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal)) seq = (int)ParseLong(line[22..]);
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var d = line[8..]; int c = d.IndexOf(',');
                    if (c >= 0) d = d[..c];
                    double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                {
                    var p = line[17..].Split('@');
                    brLen = ParseLong(p[0]);
                    brOff = p.Length > 1 ? ParseLong(p[1]) : lastEnd;
                    lastEnd = brOff + brLen;
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    var a = ParseAttrs(line[11..]);
                    string method = a.GetValueOrDefault("METHOD", "NONE").ToUpperInvariant();
                    key = method == "NONE" ? null : new HlsKey(method, a.TryGetValue("URI", out var u) ? Resolve(baseUrl, u) : null,
                        a.TryGetValue("IV", out var iv) ? ParseIv(iv) : null, a.GetValueOrDefault("KEYFORMAT"));
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    var a = ParseAttrs(line[11..]);
                    if (a.TryGetValue("URI", out var u))
                    {
                        long ml = -1, mo = 0;
                        if (a.TryGetValue("BYTERANGE", out var br)) { var p = br.Split('@'); ml = ParseLong(p[0]); mo = p.Length > 1 ? ParseLong(p[1]) : 0; }
                        map = new HlsMap(Resolve(baseUrl, u), mo, ml);
                    }
                }
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) end = true;
                continue;
            }
            segs.Add(new HlsSegment(Resolve(baseUrl, line), dur, seq++, brLen >= 0 ? brOff : 0, brLen, key));
            total += dur; dur = 0; brLen = -1; brOff = -1;
        }
        return new HlsMedia(segs, map, end, total);
    }

    private static byte[]? ParseIv(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0 || s.Length % 2 != 0) return null;
        s = s.PadLeft(32, '0');
        try { return Convert.FromHexString(s[^32..]); } catch (FormatException) { return null; }
    }

    /// <summary>DRM (Widevine/FairPlay/PlayReady/SAMPLE-AES) işaretleri var mı? Varsa açıklama, yoksa null.</summary>
    public static string? DrmReason(HlsMedia m)
    {
        foreach (var k in m.Segments.Select(s => s.Key).Where(k => k != null).Distinct())
        {
            if (k!.Method.StartsWith("SAMPLE-AES", StringComparison.Ordinal)) return "SAMPLE-AES şifreli akış";
            string kf = (k.KeyFormat ?? "identity").ToLowerInvariant();
            if (kf != "identity") return "DRM anahtarı (" + kf + ")";
            if (k.Uri != null && k.Uri.StartsWith("skd:", StringComparison.OrdinalIgnoreCase)) return "FairPlay DRM";
        }
        return null;
    }
}
