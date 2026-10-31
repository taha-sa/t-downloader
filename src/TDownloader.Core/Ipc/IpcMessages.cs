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
public abstract record IpcMessage;

public sealed record PingMessage : IpcMessage;

public sealed record PongMessage(string Version) : IpcMessage;

public sealed record DownloadMessage(
    string RequestId, string Url, string? Referrer, string? Cookies, string? UserAgent,
    string? Filename, string? Mime, long FileSize) : IpcMessage;

public sealed record DownloadResultMessage(string RequestId, bool Accepted) : IpcMessage;

public sealed record MediaItem(string Url, string? Mime, string Kind);

public sealed record MediaMessage(int TabId, int FrameId, string? PageUrl, string? PageTitle, List<MediaItem> Items) : IpcMessage;

public sealed record RectInfo(double Left, double Top, double Right, double Bottom);
public sealed record SizeInfo(double Width, double Height);
public sealed record WindowRectInfo(double Left, double Top, double Width, double Height);

public sealed record VideoGeometryMessage(
    int TabId, int WindowId, bool Visible, bool Fullscreen, RectInfo Rect, double DevicePixelRatio,
    SizeInfo Viewport, WindowRectInfo BrowserWindow, string? PageUrl, string? PageTitle) : IpcMessage;

public sealed record HideButtonMessage(int TabId) : IpcMessage;

public sealed record SettingsMessage(bool InterceptEnabled, int MinFileSizeKb, List<string> ExcludedSites, List<string> FileTypes) : IpcMessage
{
    public static SettingsMessage From(AppSettings s) =>
        new(s.BrowserCaptureEnabled, s.MinFileSizeKb, s.ExcludedSites.ToList(), s.FileTypes.ToList());
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
        ?? throw new JsonException("Boş mesaj.");

    /// <summary>Bilinmeyen/bozuk mesajlarda null döner (istisna fırlatmaz).</summary>
    public static IpcMessage? TryDeserialize(string json)
    {
        try { return JsonSerializer.Deserialize<IpcMessage>(json, JsonDefaults.Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { return null; }
    }
}
