using UpdateCard = TDownloader.App.Sdk.UpdateCard;
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
        Bg: Ui.Theme.Bg, Surface: Ui.Theme.Surface, Border: Ui.Theme.Border,
        Accent: Ui.Theme.Accent, AccentHover: Ui.Theme.AccentHover,
        Text: Ui.Theme.Text, TextDim: Ui.Theme.TextDim) { OnAccent = Ui.Theme.OnAccent };

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
