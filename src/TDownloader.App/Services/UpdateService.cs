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

    /// Tek denetleyici: hem gecikmeli arka plan kontrolü hem de T-Link standart komutları (app.update.check/install) bunu kullanır.
    public UpdateController Controller { get; }
    private UpdateCard? _card;

    public static UpdateOptions MakeOptions() => new() { AppId = AppId, CurrentVersion = AppServices.Version, PublicKeyBase64 = PublicKey };

    public UpdateService(AppServices svc, Action<UpdateInfo>? onAvailable = null)
    {
        _svc = svc; _onAvailable = onAvailable;
        Controller = new UpdateController(new UpdateChecker(MakeOptions()));
        Controller.ExitRequested += () => PostToUi(() => _svc.Main?.ExitApp());
        Controller.StateChanged += () => { if (Controller.State == UpdateState.Downloading) PostToUi(EnsureCard); };
    }

    private void EnsureCard()
    {
        if (Controller.Info == null) return;
        if (_card == null || _card.IsDisposed) { _card = new UpdateCard(Controller, Theme); _card.Show(); }
    }

    public void ScheduleCheck(TimeSpan? delay = null)
    {
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay ?? TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                var ctl = Controller;
                if (!await ctl.CheckAsync().ConfigureAwait(false) || ctl.Info == null) return;
                _onAvailable?.Invoke(ctl.Info);
                PostToUi(EnsureCard);
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
