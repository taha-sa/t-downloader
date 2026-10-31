using System.Text;
using System.Text.RegularExpressions;

namespace TDownloader.Core.Util;

/// <summary>
/// Tanı günlüğü (%APPDATA%\T-Downloader\logs\media.log): düğmeye tıklanınca bir sekme/çerçevede neyin yakalandığını gösterir.
/// Adresler yalnızca host+yol olarak yazılır (sorgu, parça, kullanıcı bilgisi KALDIRILIR; uzun rastgele yol parçaları '*' olur).
/// Çerez/imza/token ASLA yazılmaz. Dosya büyüklüğü sınırı aşınca eski yarısı atılır (döner).
/// </summary>
public sealed class MediaLog
{
    public const long DefaultMaxBytes = 200 * 1024;
    private readonly string _path;
    private readonly long _max;
    private readonly object _lock = new();

    public MediaLog(string path, long maxBytes = DefaultMaxBytes) { _path = path; _max = maxBytes; }

    private static readonly Regex TokenLike = new(@"^[A-Za-z0-9_=+\-]{32,}$", RegexOptions.Compiled);

    /// <summary>"https://cdn.x/hls/TOKEN/720/index.m3u8?sig=..." → "cdn.x/hls/*/720/index.m3u8". Geçersizse "(?)".</summary>
    public static string Sanitize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "-";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "(?)";
        if (u.Scheme != "http" && u.Scheme != "https") return u.Scheme + ":";
        var segs = u.AbsolutePath.Split('/').Select(s =>
        {
            int semi = s.IndexOf(';');
            if (semi >= 0) s = s[..semi];
            return TokenLike.IsMatch(s) ? "*" : s;
        });
        string r = u.Host + string.Join("/", segs);
        return r.Length > 140 ? r[..139] + "…" : r;
    }

    public void Write(string line)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string text = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + " " + line.Replace('\r', ' ').Replace('\n', ' ') + "\n";
                Roll(Encoding.UTF8.GetByteCount(text));
                File.AppendAllText(_path, text, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* günlük yazılamıyorsa sessiz */ }
    }

    private void Roll(int incoming)
    {
        var fi = new FileInfo(_path);
        if (!fi.Exists || fi.Length + incoming <= _max) return;
        var lines = File.ReadAllLines(_path, Encoding.UTF8);
        var keep = new List<string>();
        long size = 0;
        for (int i = lines.Length - 1; i >= 0; i--)                  // sondan başa: en yeni satırlar kalır (sınırın yarısı)
        {
            size += Encoding.UTF8.GetByteCount(lines[i]) + 1;
            if (size > _max / 2) break;
            keep.Add(lines[i]);
        }
        keep.Reverse();
        File.WriteAllLines(_path, keep, new UTF8Encoding(false));
    }
}
