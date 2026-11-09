using System.Net;
using System.Net.Sockets;

namespace TDownloader.Core.Tests;

/// <summary>Yerel HttpListener tabanlı test sunucusu: GET /file, isteğe bağlı Range desteği ve gecikme.</summary>
public sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    public byte[] Data { get; }
    public bool SupportRange { get; set; } = true;
    /// <summary>İstek Range başlangıç ofseti → 16 KB parça başına gecikme (ms).</summary>
    public Func<long, int> ChunkDelayMs { get; set; } = _ => 0;
    public int RequestCount;
    public string Url { get; }

    public TestHttpServer(byte[] data)
    {
        Data = data;
        int port = 0;
        for (int attempt = 0; ; attempt++)
        {
            port = TestPorts.Next();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _listener.Start(); break; } catch (HttpListenerException) when (attempt < 20) { }
        }
        Url = $"http://127.0.0.1:{port}/file";
        _loop = Task.Run(AcceptLoop);
    }

    public static byte[] RandomData(int size, int seed = 42)
    {
        var b = new byte[size]; new Random(seed).NextBytes(b); return b;
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref RequestCount);
        var resp = ctx.Response;
        try
        {
            long start = 0, end = Data.Length - 1;
            bool partial = false;
            var range = ctx.Request.Headers["Range"];
            if (SupportRange && range != null && range.StartsWith("bytes="))
            {
                var parts = range[6..].Split('-');
                start = long.Parse(parts[0]);
                if (parts.Length > 1 && parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), Data.Length - 1);
                partial = true;
                resp.StatusCode = 206;
                resp.AddHeader("Content-Range", $"bytes {start}-{end}/{Data.Length}");
            }
            if (SupportRange) resp.AddHeader("Accept-Ranges", "bytes");
            long len = end - start + 1;
            resp.ContentLength64 = len;
            resp.SendChunked = false;
            int delay = ChunkDelayMs(start);
            long pos = start;
            var stream = resp.OutputStream;
            while (pos <= end)
            {
                int n = (int)Math.Min(16 * 1024, end - pos + 1);
                await stream.WriteAsync(Data.AsMemory((int)pos, n));
                pos += n;
                if (delay > 0) await Task.Delay(delay);
            }
            _ = partial;
            resp.Close();
        }
        catch
        {
            try { resp.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch { }
    }
}
