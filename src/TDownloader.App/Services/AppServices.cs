using System.ComponentModel;
using TDownloader.Core.Ipc;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.App.Services;

/// <summary>
/// Video düğmesi (sonraki agent) için genişleme noktası. IPC'den gelen "media" ve "videoGeometry"
/// mesajları, çağrı hangi thread'den gelirse gelsin bu arabirime iletilir (UI thread'ine geçiş sink'in işidir;
/// <see cref="IAppServices.UiThread"/> kullanılabilir).
/// </summary>
public interface IMediaSink
{
    void Attach(IAppServices services);
    void OnMedia(MediaMessage message);
    void OnVideoGeometry(VideoGeometryMessage message);
    /// <summary>Sayfa içi düğme kalite listesi ister.</summary>
    Task<QualitiesResultMessage> GetQualitiesAsync(QualitiesMessage message);
    /// <summary>Menüden seçilen satır: indirme onay penceresini açar.</summary>
    void OnQualitySelect(QualitySelectMessage message);
    /// <summary>Sekme kapandı: o sekmenin medya durumu ve bekleyen kalite istekleri temizlenir.</summary>
    void OnTabClosed(int tabId);
    /// <summary>Menü kapandı/sekme değişti: bekleyen kalite isteği iptal edilir (yt-dlp süreci öldürülür).</summary>
    void OnQualitiesCancel(string requestId);
    void Detach();
}

/// <summary>Varsayılan: hiçbir şey yapmaz.</summary>
public sealed class NullMediaSink : IMediaSink
{
    public void Attach(IAppServices services) { }
    public void OnMedia(MediaMessage message) { }
    public void OnVideoGeometry(VideoGeometryMessage message) { }
    public Task<QualitiesResultMessage> GetQualitiesAsync(QualitiesMessage m) =>
        Task.FromResult(new QualitiesResultMessage(m.RequestId, false, "Video desteği yok.", "error", null, null, null));
    public void OnQualitySelect(QualitySelectMessage message) { }
    public void OnTabClosed(int tabId) { }
    public void OnQualitiesCancel(string requestId) { }
    public void Detach() { }
}

/// <summary>Sink'lerin uygulamadan kullanabileceği hizmetler.</summary>
public interface IAppServices
{
    DownloadManager Manager { get; }
    AppSettings Settings { get; }
    /// <summary>UI thread'ine Invoke/BeginInvoke için.</summary>
    ISynchronizeInvoke UiThread { get; }
    /// <summary>Bağlı tarayıcı eklentilerine mesaj yayınlar (ör. hideButton).</summary>
    Task SendToExtensionAsync(IpcMessage message);
    /// <summary>"Dosya İndirme Bilgisi" penceresini açar; kullanıcı kabul ederse indirmeyi ekler ve true döner.</summary>
    Task<bool> PromptDownloadAsync(string url, string? referrer = null, string? cookies = null, string? userAgent = null,
        string? fileName = null, long fileSize = -1);
    /// <summary>yt-dlp kalite seçimi sonrası aynı pencereyi açar; onaylanırsa External indirme ekler.</summary>
    Task<bool> PromptExternalAsync(string source, string? referer, string title, TDownloader.Core.Video.QualityOption option, TDownloader.Core.Video.MediaContext? ctx = null);
}

public sealed class AppServices : IAppServices
{
    public required DownloadManager Manager { get; init; }
    public required AppPaths Paths { get; init; }
    public IpcServer? Ipc { get; set; }
    public IMediaSink Media { get; set; } = new NullMediaSink();
    public Forms.MainForm? Main { get; set; }

    public AppSettings Settings => Manager.Settings;
    public ISynchronizeInvoke UiThread => Main ?? throw new InvalidOperationException("Ana pencere yok.");

    public Task SendToExtensionAsync(IpcMessage message) => Ipc?.BroadcastAsync(message) ?? Task.CompletedTask;

    public Task<bool> PromptDownloadAsync(string url, string? referrer = null, string? cookies = null, string? userAgent = null,
        string? fileName = null, long fileSize = -1)
        => Main?.PromptDownloadAsync(url, referrer, cookies, userAgent, fileName, fileSize) ?? Task.FromResult(false);

    public Task<bool> PromptExternalAsync(string source, string? referer, string title, TDownloader.Core.Video.QualityOption option, TDownloader.Core.Video.MediaContext? ctx = null)
        => Main?.PromptExternalAsync(source, referer, title, option, ctx) ?? Task.FromResult(false);

    /// <summary>Ayarları uygular, diske yazar ve eklentiye yayınlar.</summary>
    public void ApplySettings(AppSettings s)
    {
        Manager.UpdateSettings(s);
        try { s.Save(Paths.SettingsFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _ = Ipc?.BroadcastAsync(SettingsMessage.From(s));
    }

    public void SaveSettings()
    {
        try { Manager.Settings.Save(Paths.SettingsFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static string Version => typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
