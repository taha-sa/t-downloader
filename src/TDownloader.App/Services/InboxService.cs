using TDownloader.Core.Ipc;

namespace TDownloader.App.Services;

/// <summary>
/// Gelen kutusu (inbox spool) tüketicisi: App açılışında kutuyu boşaltır, çalışırken FileSystemWatcher + yedek zamanlayıcı
/// ile izler. Host, App kapalıyken aldığı indirme isteklerini buraya atomik yazar (kayıp indirme olmaz).
/// </summary>
public sealed class InboxService : IDisposable
{
    private readonly string _dir;
    private readonly Func<DownloadMessage, bool> _handler;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce, _fallback;
    private int _draining;

    public InboxService(string inboxDir, Func<DownloadMessage, bool> handler) { _dir = inboxDir; _handler = handler; }

    public void Start()
    {
        try { Directory.CreateDirectory(_dir); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        try
        {
            _watcher = new FileSystemWatcher(_dir, "*" + InboxSpool.Extension) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime };
            _watcher.Created += (_, _) => Kick();
            _watcher.Renamed += (_, _) => Kick();      // .tmp -> .json taşıması
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception) { /* izleyici kurulamazsa yedek zamanlayıcı yeter */ }
        _debounce = new System.Threading.Timer(_ => Drain(), null, Timeout.Infinite, Timeout.Infinite);
        _fallback = new System.Threading.Timer(_ => Drain(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    }

    private void Kick() { lock (_gate) _debounce?.Change(150, Timeout.Infinite); }

    public int Drain()
    {
        if (Interlocked.Exchange(ref _draining, 1) == 1) return 0;
        try { return InboxSpool.Drain(_dir, _handler); }
        catch (Exception) { return 0; }
        finally { Interlocked.Exchange(ref _draining, 0); }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_gate) { _debounce?.Dispose(); _debounce = null; }
        _fallback?.Dispose();
    }
}
