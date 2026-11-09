using TDownloader.Core.Ipc;

namespace TDownloader.App.Services;

/// <summary>IpcServer'ı başlatır, gelen mesajları yönlendirir.</summary>
public sealed class IpcCoordinator : IAsyncDisposable
{
    private readonly AppServices _svc;
    private readonly IpcServer _server = new();
    private readonly DownloadCaptureGate _capture;

    public IpcCoordinator(AppServices svc)
    {
        _svc = svc;
        // IDM tarzı: kurallar uygunsa hemen accepted:true, onay penceresi arka planda sırayla (pencere eklentiyi bekletmez).
        _capture = new DownloadCaptureGate(
            enabled: d => _svc.Main != null && (d.Explicit || _svc.Settings.BrowserCaptureEnabled),
            showPrompt: d => _svc.PromptDownloadAsync(d.Url, d.Referrer, d.Cookies, d.UserAgent, d.Filename, d.FileSize));
        svc.Ipc = _server;
        _server.MessageHandler = HandleAsync;
        _server.ClientConnected += (_, c) =>
        {
            // Bağlanan eklenti güncel ayarları alsın.
            _ = c.SendAsync(SettingsMessage.From(_svc.Settings));
        };
    }

    private InboxService? _inbox;

    public void Start()
    {
        _server.Start();
        // Host, App kapalıyken aldığı istekleri gelen kutusuna yazar: açılışta boşalt, çalışırken izle.
        // Explicit: tarayıcı zaten iptal edildi; "yakalama kapalı" ayarı bu istekleri düşürmemeli.
        _inbox = new InboxService(InboxSpool.DirOf(_svc.Paths.Root), d => { _capture.Handle(d with { Explicit = true }); return true; });
        _inbox.Start();
    }

    private Task<IpcMessage?> HandleAsync(IpcMessage msg, IpcConnection conn)
    {
        switch (msg)
        {
            case PingMessage:
                return Task.FromResult<IpcMessage?>(new PongMessage(AppServices.Version));

            case DownloadMessage d:
                return Task.FromResult<IpcMessage?>(_capture.Handle(d));

            case MediaMessage m:
                try { _svc.Media.OnMedia(m); } catch (Exception) { }
                return Task.FromResult<IpcMessage?>(null);

            case QualitiesMessage q:
                // Uzun sürebilir (yt-dlp): bağlantının mesaj döngüsünü bloke etme, yanıtı ayrıca gönder.
                _ = Task.Run(async () =>
                {
                    QualitiesResultMessage r;
                    try { r = await _svc.Media.GetQualitiesAsync(q).ConfigureAwait(false); }
                    catch (Exception ex) { r = new QualitiesResultMessage(q.RequestId, false, ex.Message, "error", null, null, null); }
                    try { await conn.SendAsync(r).ConfigureAwait(false); } catch (Exception) { }
                });
                return Task.FromResult<IpcMessage?>(null);

            case QualitiesCancelMessage qc:
                try { _svc.Media.OnQualitiesCancel(qc.RequestId); } catch (Exception) { }
                return Task.FromResult<IpcMessage?>(null);

            case TabClosedMessage tc:
                try { _svc.Media.OnTabClosed(tc.TabId); } catch (Exception) { }
                return Task.FromResult<IpcMessage?>(null);

            case QualitySelectMessage qs:
                try { _svc.Media.OnQualitySelect(qs); } catch (Exception) { }
                return Task.FromResult<IpcMessage?>(null);

            case VideoGeometryMessage v:
                try { _svc.Media.OnVideoGeometry(v); } catch (Exception) { }
                return Task.FromResult<IpcMessage?>(null);
        }
        return Task.FromResult<IpcMessage?>(null);
    }

    public ValueTask DisposeAsync() { _inbox?.Dispose(); return _server.DisposeAsync(); }
}
