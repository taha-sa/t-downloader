using TDownloader.Core.Localization;
using System.IO.Pipes;
using System.Text;

namespace TDownloader.Core.Ipc;

/// <summary>Satır-JSON çerçeveli (UTF-8 + \n) çift yönlü pipe bağlantısı.</summary>
public sealed class IpcConnection : IDisposable
{
    private readonly PipeStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    internal IpcConnection(PipeStream pipe)
    {
        _pipe = pipe;
        var enc = new UTF8Encoding(false);
        _reader = new StreamReader(pipe, enc, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, enc, 4096, leaveOpen: true) { NewLine = "\n", AutoFlush = false };
    }

    public bool IsConnected => _pipe.IsConnected;

    public async Task SendAsync(IpcMessage message, CancellationToken ct = default)
    {
        var line = IpcSerializer.Serialize(message);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    private const int MaxLineChars = 2 * 1024 * 1024;

    /// <summary>Sıradaki geçerli mesajı okur; bağlantı kapanınca veya satır sınırı aşılınca null. Anlaşılmayan satırlar atlanır.</summary>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var ch = new char[1];
        while (true)
        {
            sb.Clear();
            bool eof = false;
            while (true)
            {
                int n = await _reader.ReadAsync(ch.AsMemory(), ct).ConfigureAwait(false);
                if (n == 0) { eof = true; break; }
                char c = ch[0];
                if (c == '\n') break;
                if (c != '\r')
                {
                    if (sb.Length >= MaxLineChars) return null;
                    sb.Append(c);
                }
            }
            if (eof && sb.Length == 0) return null;
            if (sb.Length == 0) continue;
            var msg = IpcSerializer.TryDeserialize(sb.ToString());
            if (msg != null) return msg;
            if (eof) return null;
        }
    }

    public void Dispose()
    {
        try { _pipe.Dispose(); } catch { }
        _writeLock.Dispose();
    }
}

/// <summary>\\.\pipe\TDownloader.Ipc üzerinde dinleyen sunucu (App tarafı). Çoklu eşzamanlı istemci.</summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IpcConnection> _connections = new();
    private readonly object _lock = new();
    private Task? _acceptLoop;

    /// <summary>
    /// Gelen mesaj işleyicisi. Döndürdüğü yanıt (null değilse) aynı bağlantıya gönderilir. İşleyiciler bağlantı başına
    /// birbirini BEKLEMEZ (yavaş biri diğerlerini tıkamaz); yanıtlar tamamlanma sırasıyla yazılır, eşleme requestId iledir.
    /// Çağrılar arka plan thread'lerinden gelir; UI'ya Invoke ile geçin.
    /// </summary>
    public Func<IpcMessage, IpcConnection, Task<IpcMessage?>>? MessageHandler { get; set; }

    /// <summary>Teşhis günlüğü (kabul döngüsü hataları).</summary>
    public Action<string>? Log { get; set; }

    public event EventHandler<IpcConnection>? ClientConnected;
    public event EventHandler<IpcConnection>? ClientDisconnected;

    /// <summary>Dinlenen pipe adı (testler istemciyi bununla bağlar).</summary>
    public string PipeNameForTests => _pipeName;

    public IpcServer(string? pipeName = null) { _pipeName = pipeName ?? IpcSerializer.ActivePipeName; }

    public void Start()
    {
        if (_acceptLoop != null) return;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);   // başka kullanıcı oturumu bağlanamaz / sunucuyu taklit edemez
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var conn = new IpcConnection(pipe);
                lock (_lock) _connections.Add(conn);
                _ = Task.Run(() => ClientLoopAsync(conn, ct));
            }
            catch (OperationCanceledException) { pipe?.Dispose(); break; }
            catch (Exception ex)
            {
                // UnauthorizedAccessException (başka oturum aynı adı tutuyor) dahil HİÇBİR hata kabul döngüsünü öldürmez: günlüğe yaz, bekle, yeniden dene.
                pipe?.Dispose();
                Log?.Invoke("Pipe kabul hatası: " + ex.GetType().Name + ": " + ex.Message);
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ClientLoopAsync(IpcConnection conn, CancellationToken ct)
    {
        try
        {
            ClientConnected?.Invoke(this, conn);
            while (!ct.IsCancellationRequested)
            {
                var msg = await conn.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg == null) break;
                var h = MessageHandler;
                if (h == null) continue;
                // Mesajlar SERİ BEKLENMEZ: işleyici okuma döngüsünde başlatılır (eşzamanlı tamamlanan işleyiciler için sıra
                // korunur), uzun süren işleyici ise arka planda bitip yanıtını requestId ile kendisi yazar. Böylece onay
                // penceresi/yt-dlp gibi yavaş işler ping/media/qualities akışını tıkamaz.
                Task<IpcMessage?> t;
                try { t = h(msg, conn); }
                catch (Exception) { continue; }
                if (t.IsCompleted) await ReplyAsync(t, conn, ct).ConfigureAwait(false);
                else _ = ReplyAsync(t, conn, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            lock (_lock) _connections.Remove(conn);
            ClientDisconnected?.Invoke(this, conn);
            conn.Dispose();
        }
    }

    private static async Task ReplyAsync(Task<IpcMessage?> handler, IpcConnection conn, CancellationToken ct)
    {
        try
        {
            var reply = await handler.ConfigureAwait(false);
            if (reply != null) await conn.SendAsync(reply, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception) { /* işleyici hatası bağlantıyı düşürmez */ }
    }

    /// <summary>Bağlı tüm istemcilere gönderir (ör. settings, hideButton).</summary>
    public async Task BroadcastAsync(IpcMessage message)
    {
        IpcConnection[] list;
        lock (_lock) list = _connections.ToArray();
        foreach (var c in list)
        {
            try { await c.SendAsync(message, _cts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        IpcConnection[] list;
        lock (_lock) list = _connections.ToArray();
        foreach (var c in list) c.Dispose();
        if (_acceptLoop != null) { try { await _acceptLoop.ConfigureAwait(false); } catch { } }
        _cts.Dispose();
    }
}

/// <summary>Pipe istemcisi (Host tarafı).</summary>
public sealed class IpcClient : IDisposable
{
    private readonly string _pipeName;
    private IpcConnection? _conn;

    public IpcClient(string pipeName = IpcSerializer.PipeName) { _pipeName = pipeName; }

    public bool IsConnected => _conn?.IsConnected == true;

    /// <summary>Bağlanır; bağlanamazsa timeout'a kadar kısa aralıklarla dener. Başarısızsa false.</summary>
    public async Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                int ms = (int)Math.Clamp(remaining.TotalMilliseconds, 1, 500);
                await pipe.ConnectAsync(ms, ct).ConfigureAwait(false);
                _conn = new IpcConnection(pipe);
                return true;
            }
            catch (TimeoutException) { pipe.Dispose(); }
            catch (IOException) { pipe.Dispose(); }
            catch { pipe.Dispose(); throw; }
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public Task SendAsync(IpcMessage m, CancellationToken ct = default) =>
        (_conn ?? throw new InvalidOperationException(Strings.DisconnectedOpenTBridgeAndTryAgain)).SendAsync(m, ct);

    public Task<IpcMessage?> ReceiveAsync(CancellationToken ct = default) =>
        (_conn ?? throw new InvalidOperationException(Strings.DisconnectedOpenTBridgeAndTryAgain)).ReceiveAsync(ct);

    /// <summary>Bir mesaj gönderip ilk yanıtı bekler.</summary>
    public async Task<IpcMessage?> RequestAsync(IpcMessage m, CancellationToken ct = default)
    {
        await SendAsync(m, ct).ConfigureAwait(false);
        return await ReceiveAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() { _conn?.Dispose(); _conn = null; }
}
