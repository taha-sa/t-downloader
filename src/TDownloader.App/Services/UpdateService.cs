using System.Drawing;
using TSuite.Update;

namespace TDownloader.App.Services;

/// <summary>T-Update: açılıştan 30 sn sonra arka planda sürüm kontrolü; güncelleme varsa T-Downloader temalı UpdateCard.</summary>
public sealed class UpdateService
{
    public const string AppId = "t-downloader";
    /// <summary>t-downloader.pub (ECDSA P-256 SPKI, base64). Özel anahtar repo dışında: C:\Users\hp\.t-suite\keys\</summary>
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgkFr2/so1JGpFOhkm9gIbx3VKPZ/+YPezxI8C5v2IlYcVmwkYiMld9iSuwHVTroEyVGrwx+HXNeajZp7HK4SvA==";

    public static UpdateTheme Theme { get; } = new(
        Bg: C(0x1E, 0x1E, 0x1E), Surface: C(0x2A, 0x2A, 0x2A), Border: C(0x4A, 0x4A, 0x4A),
        Accent: C(0xFF, 0xC1, 0x07), AccentHover: C(0xFF, 0xD5, 0x4F),
        Text: C(0xF5, 0xF5, 0xF5), TextDim: C(0xB0, 0xB0, 0xB0)) { OnAccent = C(0x11, 0x11, 0x11) };

    private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    private readonly AppServices _svc;
    private readonly Action<UpdateInfo>? _onAvailable;

    public UpdateService(AppServices svc, Action<UpdateInfo>? onAvailable = null) { _svc = svc; _onAvailable = onAvailable; }

    public void ScheduleCheck(TimeSpan? delay = null)
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var checker = new UpdateChecker(new UpdateOptions { AppId = AppId, CurrentVersion = AppServices.Version, PublicKeyBase64 = PublicKey });
                var ctl = new UpdateController(checker);
                if (!await ctl.CheckAsync().ConfigureAwait(false) || ctl.Info == null) return;
                _onAvailable?.Invoke(ctl.Info);
                ctl.ExitRequested += () => PostToUi(() => _svc.Main?.ExitApp());
                PostToUi(() => new UpdateCard(ctl, Theme).Show());
            }
            catch { /* ağ yoksa sessizce geç */ }
        });
    }

    private void PostToUi(Action a)
    {
        var main = _svc.Main;
        if (main == null || main.IsDisposed) return;
        try { main.BeginInvoke(a); } catch (InvalidOperationException) { }
    }
}
