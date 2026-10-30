using TDownloader.Core.Ipc;

namespace TDownloader.App.Services;

/// <summary>IpcServer'ı başlatır, gelen mesajları yönlendirir.</summary>
public sealed class IpcCoordinator : IAsyncDisposable
{
    private readonly AppServices _svc;
    private readonly IpcServer _server = new();
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public IpcCoordinator(AppServices svc)
    {
        _svc = svc;
        svc.Ipc = _server;
        _server.MessageHandler = HandleAsync;
        _server.ClientConnected += (_, c) =>
        {
            // Bağlanan eklenti güncel ayarları alsın.
            _ = c.SendAsync(SettingsMessage.From(_svc.Settings));
        };
    }

    public void Start() => _server.Start();

    private async Task<IpcMessage?> HandleAsync(IpcMessage msg, IpcConnection conn)
    {
        switch (msg)
        {
            case PingMessage:
                return new PongMessage(AppServices.Version);

            case DownloadMessage d:
                bool accepted;
                await _dialogGate.WaitAsync().ConfigureAwait(false);
                try { accepted = await _svc.PromptDownloadAsync(d.Url, d.Referrer, d.Cookies, d.UserAgent, d.Filename, d.FileSize).ConfigureAwait(false); }
                catch (Exception) { accepted = false; }
                finally { _dialogGate.Release(); }
                return new DownloadResultMessage(d.RequestId, accepted);

            case MediaMessage m:
                try { _svc.Media.OnMedia(m); } catch (Exception) { }
                return null;

            case VideoGeometryMessage v:
                try { _svc.Media.OnVideoGeometry(v); } catch (Exception) { }
                return null;
        }
        return null;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
