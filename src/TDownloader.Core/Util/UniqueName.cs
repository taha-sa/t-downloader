namespace TDownloader.Core.Util;

/// <summary>Var olan (kullanıcı) dosyalarının üzerine yazmamak için çıktı adı seçimi.</summary>
public static class UniqueName
{
    /// <summary>path boşsa path, doluysa "ad (1).uzantı", "ad (2).uzantı"... Yalnızca dosya sistemine bakar.</summary>
    public static string Path(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = System.IO.Path.GetDirectoryName(path) ?? "";
        string stem = System.IO.Path.GetFileNameWithoutExtension(path), ext = System.IO.Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string p = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    /// <summary>
    /// yt-dlp gibi çıktı uzantısını sonradan belirleyen araçlar için: <c>dir\stem.&lt;herhangi uzantı&gt;</c> adıyla tamamlanmış
    /// bir dosya varsa "stem (1)"... döner. Devam için kalan ara dosyalar (.part, .ytdl, stem.f137.mp4) çakışma sayılmaz.
    /// </summary>
    public static string Stem(string dir, string stem)
    {
        for (int i = 0; ; i++)
        {
            string cand = i == 0 ? stem : $"{stem} ({i})";
            if (!StemTaken(dir, cand)) return cand;
        }
    }

    private static bool StemTaken(string dir, string stem)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
            {
                string name = System.IO.Path.GetFileName(f);
                string ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                if (ext is "part" or "ytdl" or "temp" or "tmp") continue;
                if (!string.Equals(System.IO.Path.GetFileNameWithoutExtension(name), stem, StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
