using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TDownloader.Core.Video;

/// <summary>Bir HLS izinin (görüntü/ses) devam için saklanan üst verisi. Tamamlanan parçaların kaynak gerçeği dosyalardır.</summary>
public sealed class HlsTrackState
{
    public string Prefix { get; set; } = "";
    public string PlaylistUrl { get; set; } = "";
    public int Count { get; set; }
    /// <summary>Segment URL listesinin (+ bayt aralığı) SHA256 özeti. Farklıysa URL'ler değişmiştir (ör. imzalı adres).</summary>
    public string UrlsHash { get; set; } = "";
    /// <summary>Segment sürelerinin özeti; sayı + süre aynıysa farklı URL'lerle de mevcut parçalar korunur.</summary>
    public string DurationsHash { get; set; } = "";
    public bool Fmp4 { get; set; }
    public bool HasMap { get; set; }
    public string? KeyMethod { get; set; }
    public string? KeyUri { get; set; }
    public string? KeyIv { get; set; }
    /// <summary>Yalnızca bilgi amaçlı; devamda dosyalar sayılır.</summary>
    public int Completed { get; set; }
}

public sealed class HlsStateFile
{
    public int Version { get; set; } = 1;
    public string Variant { get; set; } = "";
    public string? Audio { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<HlsTrackState> Tracks { get; set; } = new();
}

/// <summary>
/// DwnlData\&lt;id&gt;\hls\ klasöründeki hls-state.json ve parça dosyaları (00042.seg; ses için a-00042.seg).
/// Dosyalar önce ".tmp" olarak yazılır, tamamlanınca atomik yeniden adlandırılır.
/// </summary>
internal sealed class HlsStateStore
{
    public const string FileName = "hls-state.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _dir;
    private readonly object _lk = new();
    private readonly object _fileLk = new();
    private readonly HlsStateFile _state;
    private readonly System.Diagnostics.Stopwatch _sinceSave = System.Diagnostics.Stopwatch.StartNew();
    private HlsStateFile? _previous;

    public HlsStateStore(string dir, string variant, string? audio)
    {
        _dir = dir;
        _state = new HlsStateFile { Variant = variant, Audio = audio };
        try
        {
            string f = Path.Combine(dir, FileName);
            if (File.Exists(f))
            {
                var loaded = JsonSerializer.Deserialize<HlsStateFile>(File.ReadAllText(f));
                // Farklı varyant/ses: eski durum güvenilmez.
                if (loaded is { Version: 1 } && loaded.Variant == variant && loaded.Audio == audio) _previous = loaded;
                else if (loaded != null) { WipeTrack(dir, ""); WipeTrack(dir, "a-"); }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { _previous = null; }
    }

    public HlsTrackState? TryLoadTrack(string prefix) => _previous?.Tracks.FirstOrDefault(t => t.Prefix == prefix);

    public void SetTrack(HlsTrackState t)
    {
        lock (_lk) { _state.Tracks.RemoveAll(x => x.Prefix == t.Prefix); _state.Tracks.Add(t); }
    }

    public void SetCompleted(string prefix, int n)
    {
        lock (_lk) { var t = _state.Tracks.FirstOrDefault(x => x.Prefix == prefix); if (t != null) t.Completed = n; }
    }

    /// <summary>Bir parça bitti; en çok ~2 sn'de bir diske yazar.</summary>
    public void SegmentDone(string prefix)
    {
        bool save;
        lock (_lk)
        {
            var t = _state.Tracks.FirstOrDefault(x => x.Prefix == prefix);
            if (t != null) t.Completed++;
            save = _sinceSave.ElapsedMilliseconds >= 2000;
        }
        if (save) Save();
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lk)
            {
                _state.UpdatedUtc = DateTime.UtcNow;
                json = JsonSerializer.Serialize(_state, Json);
                _sinceSave.Restart();
            }
            Directory.CreateDirectory(_dir);
            string f = Path.Combine(_dir, FileName), tmp = f + ".tmp";
            lock (_fileLk) { File.WriteAllText(tmp, json); File.Move(tmp, f, overwrite: true); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* durum dosyası yalnızca meta */ }
    }

    // ------------------------------------------------------------ yardımcılar

    public static string SegPath(string dir, string prefix, int index) =>
        Path.Combine(dir, prefix + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".seg");

    public static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken ct)
    {
        string tmp = path + ".tmp";
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await fs.WriteAsync(data, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception) { }
            throw;
        }
    }

    private static bool Owns(string prefix, string name)
    {
        bool isAudio = name.StartsWith("a-", StringComparison.Ordinal);
        if (prefix == "" ? isAudio : !isAudio) return false;
        return name.EndsWith(".seg", StringComparison.Ordinal) || name.EndsWith(".seg.tmp", StringComparison.Ordinal)
            || name.EndsWith("init.map", StringComparison.Ordinal) || name.EndsWith("init.map.tmp", StringComparison.Ordinal);
    }

    public static void WipeTrack(string dir, string prefix)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
                if (Owns(prefix, Path.GetFileName(f))) { try { File.Delete(f); } catch (IOException) { } }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void DeleteTemps(string dir, string prefix)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                var n = Path.GetFileName(f);
                if (n == FileName + ".tmp" || Owns(prefix, n)) { try { File.Delete(f); } catch (IOException) { } }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string ShortHash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    public static HlsTrackState Describe(string prefix, string playlistUrl, HlsMedia media, bool fmp4)
    {
        var urls = new StringBuilder();
        var durs = new StringBuilder();
        foreach (var s in media.Segments)
        {
            urls.Append(s.Url).Append('|').Append(s.ByteOffset).Append('|').Append(s.ByteLength).Append('\n');
            durs.Append(Math.Round(s.Duration * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        var key = media.Segments.Select(s => s.Key).FirstOrDefault(k => k != null);
        return new HlsTrackState
        {
            Prefix = prefix, PlaylistUrl = playlistUrl, Count = media.Segments.Count,
            UrlsHash = Hex(urls.ToString()), DurationsHash = Hex(durs.ToString()),
            Fmp4 = fmp4, HasMap = media.Map != null,
            KeyMethod = key?.Method, KeyUri = key?.Uri, KeyIv = key?.Iv != null ? Convert.ToHexString(key.Iv) : null,
        };
    }

    private static string Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
