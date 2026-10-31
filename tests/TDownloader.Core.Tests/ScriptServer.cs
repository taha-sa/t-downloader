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
    public byte[] Data { get; }
    public string Url { get; }
    public int Requests => Volatile.Read(ref _requests);
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);
    public Handler? Custom { get; set; }
    public List<(DateTime At, long Start, long End)> Log { get; } = new();

    public ScriptServer(byte[] data)
    {
        Data = data;
        var t = new TcpListener(IPAddress.Loopback, 0); t.Start();
        int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
        Url = $"http://127.0.0.1:{port}/file";
        _l.Prefixes.Add($"http://127.0.0.1:{port}/");
        _l.Start();
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
            await s.WriteAsync(Data.AsMemory((int)pos, n));
            pos += n; sent += n;
            if (stopAfterBytes >= 0 && sent >= stopAfterBytes) { resp.Abort(); return; }
            if (chunkDelayMs > 0) await Task.Delay(chunkDelayMs);
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
        finally { if (!probe) Interlocked.Decrement(ref _inFlight); }
    }

    public void Dispose() { try { _l.Close(); } catch { } }
}
