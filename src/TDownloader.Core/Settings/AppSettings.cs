using System.Text.Json;

namespace TDownloader.Core.Settings;

public sealed class AppSettings
{
    /// <summary>İndirme başına bağlantı sayısı (1–32).</summary>
    public int Connections { get; set; } = 8;
    /// <summary>Aynı sunucuya tüm indirmelerin toplam etkin bağlantı tavanı (1–64, varsayılan 32).</summary>
    public int MaxConnectionsPerHost { get; set; } = 32;
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


    // ---- Arayüz (App) ayarları: eski settings.json dosyalarıyla uyumlu, eksikse varsayılan ----
    /// <summary>Sütun anahtarı → genişlik (px).</summary>
    public Dictionary<string, int> ColumnWidths { get; set; } = new();
    /// <summary>Sütun anahtarlarının görüntü sırası.</summary>
    public List<string> ColumnOrder { get; set; } = new();
    public bool ShowCategories { get; set; } = true;
    public int CategoriesWidth { get; set; } = 138;
    public int WindowLeft { get; set; } = int.MinValue;
    public int WindowTop { get; set; } = int.MinValue;
    public int WindowWidth { get; set; } = 0;
    public int WindowHeight { get; set; } = 0;
    public bool WindowMaximized { get; set; }
    /// <summary>Pencereyi kapatınca tepsiye küçült.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;
    /// <summary>Panodaki indirilebilir bağlantıları izle (varsayılan kapalı).</summary>
    public bool ClipboardMonitor { get; set; }
    /// <summary>Tarayıcıdan gelen indirmede "Dosya bilgisi" penceresini göster.</summary>
    public bool ShowFileInfoOnCapture { get; set; } = true;
    public SchedulerSettings Scheduler { get; set; } = new();
    /// <summary>Kullanıcının "+" ile eklediği kategoriler (klasörleri CategoryFolders'ta tutulur).</summary>
    public List<string> CustomCategories { get; set; } = new();
    /// <summary>Ayar şeması sürümü; 1: "dosya bilgisi penceresini göster" bir kerelik açık yapıldı.</summary>
    public int SettingsVersion { get; set; }
    public const int CurrentSettingsVersion = 1;

    // ---- Video (yt-dlp) ----
    /// <summary>Videoların üstünde "Bu videoyu indir" düğmesini göster.</summary>
    public bool ShowVideoButton { get; set; } = true;
    /// <summary>Eski yerel (native) düğme penceresi. Varsayılan kapalı: düğme sayfa içinde (eklenti) çizilir.</summary>
    public bool UseNativeVideoButton { get; set; }
    /// <summary>yt-dlp.exe yolu (boşsa otomatik aranır).</summary>
    public string YtDlpPath { get; set; } = "";
    /// <summary>ffmpeg.exe yolu veya klasörü (boşsa otomatik aranır).</summary>
    public string FfmpegPath { get; set; } = "";
    /// <summary>"best" | "1080" | "720" | "480" | "360"</summary>
    public string DefaultVideoQuality { get; set; } = "best";
    /// <summary>Site (host) → sürükleyerek verilen düğme ofseti (px).</summary>
    public Dictionary<string, ButtonOffset> VideoButtonOffsets { get; set; } = new();

    public int EffectiveConnections => Math.Clamp(Connections, 1, 32);

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonDefaults.Options) ?? new();
                if (s.SettingsVersion < 1)
                {
                    // Bir kerelik göç: IDM gibi her indirmede dosya bilgisi penceresi açılsın.
                    s.ShowFileInfoOnCapture = true;
                    s.SettingsVersion = CurrentSettingsVersion;
                    try { s.Save(path); } catch (Exception) { }
                }
                return s;
            }
        }
        catch (Exception) { /* bozuk dosya: varsayılanlar */ }
        return new AppSettings { SettingsVersion = CurrentSettingsVersion };
    }

    public void Save(string path) =>
        JsonDefaults.WriteAtomic(path, JsonSerializer.Serialize(this, JsonDefaults.Indented));
}

public sealed class ButtonOffset
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class SchedulerSettings
{
    public bool Enabled { get; set; }
    /// <summary>"HH:mm"</summary>
    public string StartTime { get; set; } = "02:00";
    /// <summary>System.DayOfWeek sayı değerleri (0 = Pazar).</summary>
    public List<int> Days { get; set; } = new() { 0, 1, 2, 3, 4, 5, 6 };
    /// <summary>"none" | "notify" | "exit"</summary>
    public string OnFinished { get; set; } = "notify";
}
