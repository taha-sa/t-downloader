using TDownloader.Core.Ipc;

namespace TDownloader.App.Services;

/// <summary>
/// Gelen kutusu (inbox spool) tüketicisi: App açılışında kutuyu boşaltır, çalışırken FileSystemWatcher + yedek zamanlayıcı
/// ile izler. Host, App kapalıyken aldığı indirme isteklerini buraya atomik yazar (kayıp indirme olmaz).
/// </summary>
public sealed class InboxService : IDisposable
{
    private readonly string _dir;
    private readonly Func<DownloadMessage, Action<bool>, DownloadResultMessage> _submit;
    private readonly Action<string>? _onFailed;
    // Aynı dosyanın (guid) birden çok boşaltma turunda ikinci kez işlenmesini önler (idempotent); dosya iş bitene dek diskte kalır.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inflight = new();
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce, _fallback;
    private int _draining;

    /// <param name="submit">İsteği işleme alır; Accepted=true ise iş bitince completed(true: hatasız, false: hata) çağrılır.</param>
    /// <param name="onFailed">Bir dosya failed\ altına taşındığında (neden: hata/süre aşımı/bozuk) kullanıcıya bildirmek için.</param>
    public InboxService(string inboxDir, Func<DownloadMessage, Action<bool>, DownloadResultMessage> submit, Action<string>? onFailed = null)
    { _dir = inboxDir; _submit = submit; _onFailed = onFailed; }

    public int InFlight => _inflight.Count;

    private DrainAction Handle(DownloadMessage msg, string path)
    {
        string id = Path.GetFileNameWithoutExtension(path);
        if (!_inflight.TryAdd(id, 0)) return DrainAction.Pending;       // zaten işleniyor
        bool started = false;
        try
        {
            var r = _submit(msg, ok =>
            {
                try
                {
                    // Dosya YALNIZ iş hatasız bittiğinde (eklendi / kullanıcı iptal etti) silinir; hata olursa saklanır ve bildirilir.
                    if (ok) { try { File.Delete(path); } catch (IOException) { } }
                    else if (InboxSpool.MoveToFailed(path, "Ekleme/pencere hatası") is { } dest) Notify(dest);
                }
                finally { _inflight.TryRemove(id, out _); }
            });
            started = r.Accepted;
            if (started) return DrainAction.Pending;
            // Reddedildi: http değilse kalıcı hata; aksi halde (ana pencere hazır değil, kuyruk dolu) sonraki turda yeniden denenir.
            return DownloadCaptureGate.IsHttp(msg.Url) ? DrainAction.Keep : DrainAction.Failed;
        }
        finally { if (!started) _inflight.TryRemove(id, out _); }
    }

    private void Notify(string failedPath) { try { _onFailed?.Invoke(failedPath); } catch (Exception) { } }

    /// <summary>failed\ altındaki dosyaları gelen kutusuna geri alır ve hemen boşaltır ("Tekrar dene").</summary>
    public int RetryFailed()
    {
        int n = InboxSpool.RetryFailed(_dir);
        if (n > 0) Drain();
        return n;
    }

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
        try { return InboxSpool.Drain(_dir, Handle, null, (p, _) => Notify(p)); }
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
