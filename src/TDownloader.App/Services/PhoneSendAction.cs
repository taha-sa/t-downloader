using TDownloader.Core.Localization;
using System.Text.Json;
using TDownloader.App.Ui;
using TSuite.Link;

namespace TDownloader.App.Services;

/// <summary>
/// T-Bridge + telefon bağlantı durumu: TEK paylaşılan yoklayıcı (boyamadan bağımsız, zamanlayıcıyla; en çok 3 sn'de bir).
/// Yalnızca en az bir pencere <see cref="Acquire"/> ettiyse çalışır; son pencere <see cref="Release"/> edince durur.
/// </summary>
public static class PhoneStatus
{
    private static readonly object Gate = new();
    private static System.Threading.Timer? _timer;
    private static int _refs;
    private static int _busy;
    private static volatile bool _available;

    public static bool Available => _available;
    private static volatile bool _installed;
    public static bool Installed => _installed;
    public static bool RefreshInstallation() => _installed = DetectInstalled();

    private static bool DetectInstalled()
    {
        var manifest = new AppRegistry().Read("t-bridge");
        if (manifest != null && File.Exists(manifest.ExePath)) return true;
        return new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "T-Bridge", "T-Bridge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "T-Bridge", "T-Bridge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "T-Bridge", "T-Bridge.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "T-Bridge", "T-Bridge.exe")
        }.Any(File.Exists);
    }
    public static event Action? Changed;

    public static void Acquire()
    {
        lock (Gate)
        {
            if (_refs++ == 0) _timer = new System.Threading.Timer(_ => Poll(), null, 0, 3000);
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (_refs == 0) return;
            if (--_refs == 0) { _timer?.Dispose(); _timer = null; _available = false; }
        }
    }

    private static async void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            bool installed = DetectInstalled();
            bool installationChanged = installed != _installed;
            _installed = installed;
            bool v = await QueryAsync().ConfigureAwait(false);
            bool live; lock (Gate) live = _refs > 0;
            if (live && (v != _available || installationChanged)) { _available = v; Changed?.Invoke(); }
        }
        catch { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static async Task<bool> QueryAsync()
    {
        try
        {
            var m = new AppRegistry().Read("t-bridge");
            if (m == null || !AppRegistry.IsAlive(m.Pid)) return false;
            await using var c = new LinkClient("t-bridge", TimeSpan.FromSeconds(5));
            c.Start();
            if (!await c.WaitConnectedAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)) return false;
            var r = await c.SendCommandAsync("phone.status", null, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return r.Data is { } d && d.TryGetProperty("result", out var res)
                && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("connected", out var cn) && cn.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
}

/// <summary>
/// Phone actions stay visible; missing T-Bridge opens a contextual invitation.
/// Gönderme: T-Bridge'e T-Link komutu file.send {path}. Sonuç (başarı/başarısızlık) <see cref="Feedback"/> ile bildirilir.
/// </summary>
public sealed class PhoneSendAction(bool open = false) : ICompletedAction
{
    /// <summary>Kullanıcıya gösterilecek kısa durum metni (UI thread'inde olmayabilir).</summary>
    public event Action<string>? Feedback;
    public string Text => open ? Strings.OpenOnPhone : Strings.SendToPhone;

    public bool IsAvailable(CompletedEntry entry) => true;

    public void Execute(CompletedEntry entry)
    {
        if (!PhoneStatus.RefreshInstallation())
        {
            using var invitation = new Forms.BridgeInvitationDialog();
            invitation.ShowDialog(Form.ActiveForm);
            return;
        }
        if (!PhoneStatus.Available) { Feedback?.Invoke(Strings.PhoneDisconnected); return; }
        _ = SendAsync(entry);
    }

    private async Task SendAsync(CompletedEntry entry)
    {
        try
        {
            if (!File.Exists(entry.Path)) { Feedback?.Invoke(Strings.FileNotFoundCheckItsLocationAndTry); return; }
            await using var c = new LinkClient("t-bridge", TimeSpan.FromSeconds(5));
            c.Start();
            if (!await c.WaitConnectedAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false)) { Feedback?.Invoke(Strings.CouldNotConnectToTBridgeOpenT); return; }
            var reply = await c.SendCommandAsync("file.send", open ? LinkJson.Obj(("path", entry.Path), ("open", true)) : LinkJson.Obj(("path", entry.Path)), TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            Feedback?.Invoke(PhoneTransferReply.Accepted(reply.Data) ? Strings.QueuedForPhone : Strings.CouldNotSendToYourPhoneCheckThe);
        }
        catch { Feedback?.Invoke(Strings.CouldNotSendToYourPhoneCheckThe); }
    }
}
