namespace TDownloader.Core.Settings;

/// <summary>Veri konumları (CONTRACT.md). Testlerde farklı kök verilebilir.</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DownloadsFile => Path.Combine(Root, "downloads.json");
    public string DwnlDataDir => Path.Combine(Root, "DwnlData");

    public AppPaths(string root) { Root = root; }

    /// <summary>Araçların (yt-dlp, ffmpeg) aranacağı veri klasörü altı.</summary>
    public string ToolsDir => Path.Combine(Root, "tools");

    /// <summary>TDOWNLOADER_DATA_DIR ortam değişkeni verilmişse (test/yalıtım) o klasör kullanılır.</summary>
    public static AppPaths Default { get; } = new(
        Environment.GetEnvironmentVariable("TDOWNLOADER_DATA_DIR") is { Length: > 0 } o ? o :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "T-Downloader"));

    public static string DefaultSaveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "T-Downloader");
}
