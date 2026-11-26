using TDownloader.Core.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Ipc;

/// <summary>CONTRACT.md IPC mesajları. JSON'da "type" ayırıcı alanı ve camelCase özellik adları kullanılır.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(DownloadMessage), "download")]
[JsonDerivedType(typeof(DownloadResultMessage), "downloadResult")]
[JsonDerivedType(typeof(MediaMessage), "media")]
[JsonDerivedType(typeof(VideoGeometryMessage), "videoGeometry")]
[JsonDerivedType(typeof(HideButtonMessage), "hideButton")]
[JsonDerivedType(typeof(SettingsMessage), "settings")]
[JsonDerivedType(typeof(QualitiesMessage), "qualities")]
[JsonDerivedType(typeof(QualitiesResultMessage), "qualitiesResult")]
[JsonDerivedType(typeof(QualitySelectMessage), "qualitySelect")]
[JsonDerivedType(typeof(QualitiesCancelMessage), "qualitiesCancel")]
[JsonDerivedType(typeof(TabClosedMessage), "tabClosed")]
public abstract record IpcMessage;

public sealed record PingMessage : IpcMessage;

public sealed record PongMessage(string Version) : IpcMessage;

public sealed record DownloadMessage(
    string RequestId, string Url, string? Referrer, string? Cookies, string? UserAgent,
    string? Filename, string? Mime, long FileSize, bool Explicit = false) : IpcMessage;

public sealed record DownloadResultMessage(string RequestId, bool Accepted) : IpcMessage;

/// <summary>
/// Sniff edilen medya. Referer/Origin/Cookies/UserAgent isteğe bağlıdır (eski eklentiler göndermez): iframe
/// oynatıcıdaki akışlar için istek başlıklarını taşır.
/// </summary>
public sealed record MediaItem(string Url, string? Mime, string Kind, string? Referer = null, string? Origin = null, string? Cookies = null, string? UserAgent = null, long Size = -1);

/// <summary>
/// Append=true: Items yalnızca YENİ öğelerdir, App mevcut çerçeve listesine ekler; false: App çerçeve listesini değiştirir
/// (boş liste = temizle). Eski eklentiler Append göndermez (= değiştir).
/// </summary>
public sealed record MediaMessage(int TabId, int FrameId, string? PageUrl, string? PageTitle, List<MediaItem> Items, string? FrameUrl = null, bool Append = false) : IpcMessage;

public sealed record RectInfo(double Left, double Top, double Right, double Bottom);
public sealed record SizeInfo(double Width, double Height);
public sealed record WindowRectInfo(double Left, double Top, double Width, double Height);

public sealed record VideoGeometryMessage(
    int TabId, int WindowId, bool Visible, bool Fullscreen, RectInfo Rect, double DevicePixelRatio,
    SizeInfo Viewport, WindowRectInfo BrowserWindow, string? PageUrl, string? PageTitle) : IpcMessage;

/// <summary>
/// Sayfa içi düğme kalite listesi ister (eklenti -> App). Src: videonun currentSrc'si (http/https ise).
/// Cookies: origin -> çerez başlığı; her çerez o origin'in kendi adresi için okunmuştur. CookieHosts: origin -> çerezin okunduğu host.
/// App çerezi YALNIZCA o host'a (ve alt alan adlarına) gönderir; oynatıcı (Players) adresleri için çerez okunmaz.
/// </summary>
public sealed record QualitiesMessage(string RequestId, int TabId, int FrameId, string? PageUrl, string? PageTitle, string? FrameUrl, string? Src,
    List<PlayerSource>? Players = null, Dictionary<string, string>? Cookies = null, Dictionary<string, string>? CookieHosts = null) : IpcMessage;

/// <summary>Kalite listesi isteğini iptal eder (menü kapandı/sekme değişti); App yt-dlp sürecini öldürür (eklenti -> App).</summary>
public sealed record QualitiesCancelMessage(string RequestId) : IpcMessage;

/// <summary>Sekme kapandı (eklenti -> App): App o sekmenin medya durumunu temizler.</summary>
public sealed record TabClosedMessage(int TabId) : IpcMessage;

/// <summary>Düğme tıklanınca eklentinin oynatıcı yapılandırmasından okuduğu kaynak (jwplayer/video.js/HLS.js/Playerjs/&lt;source&gt;).</summary>
public sealed record PlayerSource(string Url, string? Label = null, int Height = 0);

/// <summary>Menüde gösterilen satır. Kind: "video" | "audio" | "file" | "other" (Diğer dosyalar).</summary>
public sealed record QualityOptionInfo(string Label, string Kind, int Height, long Size, bool AudioOnly, bool IsDefault);

/// <summary>Kalite listesi yanıtı (App -> eklenti). ErrorCode: "no_ytdlp" | "no_media" | "error". SelectionId seçimde geri gönderilir.</summary>
public sealed record QualitiesResultMessage(string RequestId, bool Ok, string? Error, string? ErrorCode, string? Title, string? SelectionId,
    List<QualityOptionInfo>? Options) : IpcMessage;

/// <summary>Kullanıcı menüden bir satır seçti (eklenti -> App); App "Dosya İndirme Bilgisi" penceresini açar.</summary>
public sealed record QualitySelectMessage(string SelectionId, int Index) : IpcMessage;

public sealed record HideButtonMessage(int TabId) : IpcMessage;

public sealed record SettingsMessage(bool InterceptEnabled, int MinFileSizeKb, List<string> ExcludedSites, List<string> FileTypes,
    bool ShowVideoButton = true, bool NativeButton = false, string Language = "") : IpcMessage
{
    public static SettingsMessage From(AppSettings s) =>
        new(s.BrowserCaptureEnabled, s.MinFileSizeKb, s.ExcludedSites.ToList(), s.FileTypes.ToList(), s.ShowVideoButton, s.UseNativeVideoButton, System.Globalization.CultureInfo.CurrentUICulture.Name);
}

public static class IpcSerializer
{
    public const string PipeName = "TDownloader.Ipc";
    /// <summary>Test/yalıtım: TDOWNLOADER_PIPE ortam değişkeni verilmişse sunucu o adla dinler.</summary>
    public static string ActivePipeName => Environment.GetEnvironmentVariable("TDOWNLOADER_PIPE") is { Length: > 0 } p ? p : PipeName;

    public static string Serialize(IpcMessage message) =>
        JsonSerializer.Serialize<IpcMessage>(message, JsonDefaults.Options);

    public static IpcMessage Deserialize(string json) =>
        JsonSerializer.Deserialize<IpcMessage>(json, JsonDefaults.Options)
        ?? throw new JsonException(Strings.TheMessageIsEmptyTryAgain);

    /// <summary>Bilinmeyen/bozuk mesajlarda null döner (istisna fırlatmaz).</summary>
    public static IpcMessage? TryDeserialize(string json)
    {
        try { return JsonSerializer.Deserialize<IpcMessage>(json, JsonDefaults.Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return null; }
    }
}
