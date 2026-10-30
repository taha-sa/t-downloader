using System.Text.Json;

namespace TDownloader.Core.Settings;

public sealed class AppSettings
{
    /// <summary>İndirme başına bağlantı sayısı (1–32).</summary>
    public int Connections { get; set; } = 8;
    /// <summary>Aynı anda aktif indirme sayısı.</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;
    /// <summary>Global hız sınırı KB/s; 0 = sınırsız.</summary>
    public int SpeedLimitKBps { get; set; } = 0;
    /// <summary>Kök kayıt klasörü; kategori alt klasörleri altına açılır.</summary>
    public string SaveFolder { get; set; } = AppPaths.DefaultSaveFolder;
    /// <summary>Kategori adı → özel klasör (isteğe bağlı).</summary>
    public Dictionary<string, string> CategoryFolders { get; set; } = new();
    public bool BrowserCaptureEnabled { get; set; } = true;
    public int MinFileSizeKb { get; set; } = 100;
    public List<string> ExcludedSites { get; set; } = new();
    public List<string> FileTypes { get; set; } = new()
    {
        "zip","rar","7z","gz","tar","bz2","xz","iso","exe","msi","msix","apk",
        "pdf","doc","docx","xls","xlsx","ppt","pptx","mp3","flac","wav","m4a",
        "mp4","mkv","avi","mov","wmv","webm","flv","m4v"
    };

    public int EffectiveConnections => Math.Clamp(Connections, 1, 32);

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new();
        }
        catch (Exception) { /* bozuk dosya: varsayılanlar */ }
        return new AppSettings();
    }

    public void Save(string path) =>
        JsonDefaults.WriteAtomic(path, JsonSerializer.Serialize(this, JsonDefaults.Indented));
}
