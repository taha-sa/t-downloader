using System.Diagnostics;
using System.Text;
using TDownloader.Core.Ipc;

namespace TDownloader.Host;

public sealed record HostOptions(
    string PipeName,
    string AppExePath,
    TimeSpan AppStartTimeout,
    TimeSpan RetryCooldown);

/// <summary>
/// Native messaging (stdin/stdout) ile App named pipe'ı arasında iki yönlü köprü.
/// App yoksa başlatmayı dener; olmazsa download isteklerine accepted:false döner, diğerlerini sessizce düşürür.
/// </summary>
public sealed class HostRunner
{
    private readonly HostOptions _opt;
    private readonly HostLog _log;
    private readonly Stream _stdin;
    private readonly Stream _stdout;
    private readonly SemaphoreSlim _outLock = new(1, 1);
    private readonly SemaphoreSlim _connLock = new(1, 1);
    private readonly object _clientLock = new();
    private IpcClient? _client;
    private DateTime _lastLaunchFailure = DateTime.MinValue;

    public HostRunner(HostOptions opt, Stream stdin, Stream stdout, HostLog log)
    {
        _opt = opt; _stdin = stdin; _stdout = stdout; _log = log;
    }

    /// <summary>stdin kapanana kadar çalışır; temiz çıkışta 0.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? raw;
                try { raw = await NativeMessaging.ReadAsync(_stdin, ct).ConfigureAwait(false); }
                catch (MessageTooLargeException ex) { _log.Write("Gelen mesaj reddedildi: " + ex.Message); continue; }
                catch (EndOfStreamException ex) { _log.Write("stdin yarım kapandı: " + ex.Message); break; }
                if (raw == null) break;
                await HandleFromBrowserAsync(raw, ct).ConfigureAwait(false);
            }
            _log.Write("stdin kapandı, çıkılıyor.");
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex) { _log.Write("Beklenmeyen hata: " + ex); return 1; }
        finally { DropClient(); }
    }

    private async Task HandleFromBrowserAsync(byte[] raw, CancellationToken ct)
    {
        var msg = IpcSerializer.TryDeserialize(Encoding.UTF8.GetString(raw));
        if (msg == null) { _log.Write("Anlaşılmayan tarayıcı mesajı atlandı."); return; }

        // En fazla iki deneme: mevcut bağlantı kopmuşsa yeniden bağlanıp bir kez daha yaz.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var client = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            if (client == null) break;
            try { await client.SendAsync(msg, ct).ConfigureAwait(false); return; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _log.Write("Pipe'a yazılamadı: " + ex.Message);
                DropClient();
            }
        }

        // App'e ulaşılamadı.
        if (msg is DownloadMessage d)
        {
            _log.Write($"App yok; download {d.RequestId} reddedildi (tarayıcı kendi indirsin).");
            await WriteToBrowserAsync(new DownloadResultMessage(d.RequestId, false), ct).ConfigureAwait(false);
        }
        else
        {
            // ping dahil: cevap verilmez; eklenti zaman aşımıyla "App yok" sonucuna varır.
            _log.Write($"App yok; '{msg.GetType().Name}' düşürüldü.");
        }
    }

    private async Task<IpcClient?> EnsureConnectedAsync(CancellationToken ct)
    {
        await _connLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_clientLock) { if (_client is { IsConnected: true }) return _client; }
            DropClient();

            var c = new IpcClient(_opt.PipeName);
            if (await c.ConnectAsync(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false))
                return Adopt(c);

            // Önceki başlatma yakın zamanda başarısız olduysa her mesajda 10 sn bekleme.
            if (DateTime.UtcNow - _lastLaunchFailure < _opt.RetryCooldown)
            {
                c.Dispose();
                return null;
            }

            if (!TryLaunchApp()) { _lastLaunchFailure = DateTime.UtcNow; c.Dispose(); return null; }

            if (await c.ConnectAsync(_opt.AppStartTimeout, ct).ConfigureAwait(false))
                return Adopt(c);

            _log.Write($"App başlatıldı ama pipe {_opt.AppStartTimeout.TotalSeconds:0.#} sn içinde açılmadı.");
            _lastLaunchFailure = DateTime.UtcNow;
            c.Dispose();
            return null;
        }
        finally { _connLock.Release(); }
    }

    private IpcClient Adopt(IpcClient c)
    {
        lock (_clientLock) _client = c;
        _log.Write("Pipe'a bağlanıldı.");
        _ = Task.Run(() => PipeReaderAsync(c));
        return c;
    }

    private bool TryLaunchApp()
    {
        try
        {
            if (!File.Exists(_opt.AppExePath)) { _log.Write("App bulunamadı: " + _opt.AppExePath); return false; }
            _log.Write("App başlatılıyor: " + _opt.AppExePath + " --background");
            var psi = new ProcessStartInfo(_opt.AppExePath, "--background")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_opt.AppExePath)!,
                // Chrome'un stdio'sunu miras alıp App tarayıcıya bağlı kalmasın.
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex) { _log.Write("App başlatılamadı: " + ex.Message); return false; }
    }

    private async Task PipeReaderAsync(IpcClient c)
    {
        try
        {
            while (true)
            {
                var msg = await c.ReceiveAsync().ConfigureAwait(false);
                if (msg == null) break;
                await WriteToBrowserAsync(msg, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex) { _log.Write("Pipe okuyucu hatası: " + ex.Message); }
        finally
        {
            _log.Write("Pipe bağlantısı kapandı.");
            lock (_clientLock) { if (ReferenceEquals(_client, c)) _client = null; }
            c.Dispose();
        }
    }

    private async Task WriteToBrowserAsync(IpcMessage msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(IpcSerializer.Serialize(msg));
        await _outLock.WaitAsync(ct).ConfigureAwait(false);
        try { await NativeMessaging.WriteAsync(_stdout, bytes, ct).ConfigureAwait(false); }
        catch (MessageTooLargeException ex) { _log.Write("Giden mesaj reddedildi: " + ex.Message); }
        catch (IOException ex) { _log.Write("stdout yazılamadı: " + ex.Message); }
        finally { _outLock.Release(); }
    }

    private void DropClient()
    {
        IpcClient? c;
        lock (_clientLock) { c = _client; _client = null; }
        c?.Dispose();
    }
}
