using System.Net;
using System.Net.Sockets;

namespace TDownloader.Core.Tests;

/// <summary>
/// Görünmez yerel HttpListener: istek başına yazılan işleyiciyle (Range dahil) dayanıklılık senaryoları için.
/// Aynı anda açık istek sayısını (probe 0-0 istekleri hariç) izler.
/// </summary>
public sealed class ScriptServer : IDisposable
{
    public delegate Task Handler(HttpListenerContext ctx, long start, long end, bool isProbe);

    private readonly HttpListener _l = new();
    private int _inFlight, _maxInFlight, _requests;
    // İstek başına "uçuşta" işareti (Serve içinde son parça yazılmadan ÖNCE düşürülür): istemci gövdeyi bitirip HostGate yerini
    // bırakınca sonraki istek sunucuda hâlâ bitmemiş işleyiciyle çakışıp MaxInFlight'ı şişirmesin.
    private readonly AsyncLocal<int[]?> _flight = new();
    private void EndFlight() { var f = _flight.Value; if (f != null && Interlocked.Exchange(ref f[0], 0) == 1) Interlocked.Decrement(ref _inFlight); }
    public byte[] Data { get; }
    public string Url { get; }
    public int Requests => Volatile.Read(ref _requests);
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);
    public Handler? Custom { get; set; }
    public List<(DateTime At, long Start, long End)> Log { get; } = new();

    public ScriptServer(byte[] data)
    {
        Data = data;
        // Boş port seçip dinleyiciyi açma arasında başka test aynı portu alabilir: birkaç kez dene.
        int port = 0;
        for (int attempt = 0; ; attempt++)
        {
            port = TestPorts.Next();
            _l.Prefixes.Clear();
            _l.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _l.Start(); break; } catch (HttpListenerException) when (attempt < 20) { }
        }
        Url = $"http://127.0.0.1:{port}/file";
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { break; }
            _ = Task.Run(() => Handle(c));
        }
    }

    /// <summary>Normal (Range destekli) yanıt; chunkDelayMs 16 KB parça başına gecikme.</summary>
    public async Task Serve(HttpListenerContext c, long start, long end, int chunkDelayMs = 0, int stopAfterBytes = -1)
    {
        var resp = c.Response;
        if (c.Request.Headers["Range"] != null)
        {
            resp.StatusCode = 206;
            resp.AddHeader("Content-Range", $"bytes {start}-{end}/{Data.Length}");
        }
        resp.AddHeader("Accept-Ranges", "bytes");
        resp.ContentLength64 = end - start + 1;
        long pos = start; long sent = 0;
        var s = resp.OutputStream;
        while (pos <= end)
        {
            int n = (int)Math.Min(16 * 1024, end - pos + 1);
            if (pos + n > end) EndFlight();        // son parça: istemci bitirip yerini bırakabilir
            await s.WriteAsync(Data.AsMemory((int)pos, n));
            pos += n; sent += n;
            if (stopAfterBytes >= 0 && sent >= stopAfterBytes) { resp.Abort(); return; }
            if (chunkDelayMs > 0 && pos <= end) await Task.Delay(chunkDelayMs);
        }
        resp.Close();
    }

    private async Task Handle(HttpListenerContext c)
    {
        Interlocked.Increment(ref _requests);
        long start = 0, end = Data.Length - 1;
        var range = c.Request.Headers["Range"];
        if (range != null && range.StartsWith("bytes="))
        {
            var p = range[6..].Split('-');
            start = long.Parse(p[0]);
            if (p.Length > 1 && p[1].Length > 0) end = Math.Min(long.Parse(p[1]), Data.Length - 1);
        }
        bool probe = range != null && start == 0 && end == 0;
        lock (Log) Log.Add((DateTime.UtcNow, start, end));
        if (!probe)
        {
            _flight.Value = new[] { 1 };
            int cur = Interlocked.Increment(ref _inFlight);
            int m;
            while (cur > (m = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, cur, m) != m) { }
        }
        try
        {
            if (Custom != null) await Custom(c, start, end, probe);
            else await Serve(c, start, end);
        }
        catch { try { c.Response.Abort(); } catch { } }
        finally { if (!probe) EndFlight(); }
    }

    public void Dispose() { try { _l.Close(); } catch { } }
}
