namespace TDownloader.Core.Video;

/// <summary>yt-dlp / ffmpeg arama sırası: ayarlardaki yol, %APPDATA%\T-Downloader\tools, uygulama klasörü\tools, PATH.</summary>
public static class ToolLocator
{
    public static string? Find(string exeBaseName, string? configured, string appDataToolsDir, string appBaseDir, IEnumerable<string>? pathDirs = null)
    {
        string exe = exeBaseName + ".exe";
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var c = configured.Trim().Trim('"');
            if (File.Exists(c)) return c;
            if (Directory.Exists(c))
            {
                var f = Path.Combine(c, exe);
                if (File.Exists(f)) return f;
                f = Path.Combine(c, "bin", exe);
                if (File.Exists(f)) return f;
            }
        }
        foreach (var dir in new[] { appDataToolsDir, Path.Combine(appBaseDir, "tools") })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var f = Path.Combine(dir, exe);
            if (File.Exists(f)) return f;
        }
        pathDirs ??= (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var d in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(d)) continue;
            try
            {
                var f = Path.Combine(d.Trim().Trim('"'), exe);
                if (File.Exists(f)) return f;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
