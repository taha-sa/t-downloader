using System.Text.RegularExpressions;

namespace TDownloader.Core.Util;

/// <summary>Silinen/iptal edilen yt-dlp indirmelerinin hedef klasörde bıraktığı yarım dosyaları temizler.</summary>
public static class ExternalCleanup
{
    // yt-dlp: "ad.mp4.part", "ad.f137.mp4.part", "ad.mp4.ytdl", "ad.mp4.part-Frag12", "ad.mp4.part-Frag12.part"
    private static readonly Regex Leftover = new(@"\.(part|ytdl)$|\.part-Frag\d+(\.part)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="savePath"/> ile aynı klasörde, aynı ada (uzantısız) ait .part/.ytdl/.part-FragN dosyalarını siler.
    /// Tamamlanmış çıktı dosyasına (ör. ad.mp4) dokunmaz. Silinen dosya sayısını döner.
    /// </summary>
    public static int DeleteYtDlpLeftovers(string savePath)
    {
        int n = 0;
        try
        {
            var dir = Path.GetDirectoryName(savePath);
            var stem = Path.GetFileNameWithoutExtension(savePath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir)) return 0;
            string prefix = stem + ".";
            foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
            {
                var name = Path.GetFileName(f);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Leftover.IsMatch(name)) continue;
                try { File.Delete(f); n++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return n;
    }
}
