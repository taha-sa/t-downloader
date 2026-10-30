using System.Diagnostics;
using System.Security.Cryptography;
using TDownloader.Core.Downloads;

namespace TDownloader.Core.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-test-" + Guid.NewGuid().ToString("N"));

    public EngineTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private DownloadItem NewItem(string url) => new()
    {
        Url = url, FileName = "out.bin", SavePath = Path.Combine(_tmp, "save", "out.bin")
    };

    private DownloadEngine NewEngine(DownloadItem item, Action<EngineOptions>? cfg = null, RateLimiter? lim = null)
    {
        var o = new EngineOptions
        {
            Connections = 4, MinSplitSize = 64 * 1024, RetryBaseDelay = TimeSpan.FromMilliseconds(50),
            ProgressInterval = TimeSpan.FromMilliseconds(50), StateSaveInterval = TimeSpan.FromMilliseconds(100)
        };
        cfg?.Invoke(o);
        return new DownloadEngine(item, Path.Combine(_tmp, "DwnlData"), o, lim);
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));
    private static string ShaFile(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));

    [Fact]
    public async Task MultiConnection_Download_MatchesSha256()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(5 * 1024 * 1024 + 123));
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item);
        DownloadProgress? last = null;
        engine.ProgressChanged += (_, p) => last = p;

        var st = await engine.RunAsync();

        Assert.Equal(DownloadStatus.Completed, st);
        Assert.Equal(Sha(srv.Data), ShaFile(item.SavePath));
        Assert.True(engine.SupportsRanges);
        Assert.NotNull(last);
        Assert.Equal(srv.Data.Length, item.TotalSize);
        Assert.False(Directory.Exists(Path.Combine(_tmp, "DwnlData", item.Id)));
    }

    [Fact]
    public async Task NoRangeSupport_FallsBackToSingleConnection()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(2 * 1024 * 1024 + 7)) { SupportRange = false };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item);
        DownloadProgress? maxConn = null;
        engine.ProgressChanged += (_, p) => { if (maxConn == null || p.Connections.Count > maxConn.Connections.Count) maxConn = p; };

        var st = await engine.RunAsync();

        Assert.Equal(DownloadStatus.Completed, st);
        Assert.False(engine.SupportsRanges);
        Assert.Equal(Sha(srv.Data), ShaFile(item.SavePath));
        Assert.Equal(0, engine.SplitCount);
        if (maxConn != null) Assert.Single(maxConn.Connections);
    }

    [Fact]
    public async Task Pause_Then_ResumeWithNewEngine_ProducesCorrectFile()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(8 * 1024 * 1024 + 99)) { ChunkDelayMs = _ => 10 };
        var item = NewItem(srv.Url);

        var e1 = NewEngine(item);
        var run1 = e1.RunAsync();
        var sw = Stopwatch.StartNew();
        while (item.Downloaded < 1024 * 1024 && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(10);
        e1.Pause();
        var s1 = await run1;
        Assert.Equal(DownloadStatus.Paused, s1);
        long atPause = item.Downloaded;
        Assert.InRange(atPause, 1, srv.Data.Length - 1);
        Assert.True(File.Exists(Path.Combine(_tmp, "DwnlData", item.Id, "state.json")));

        srv.ChunkDelayMs = _ => 0;
        using var e2 = NewEngine(item);
        var s2 = await e2.RunAsync();

        Assert.Equal(DownloadStatus.Completed, s2);
        Assert.Equal(Sha(srv.Data), ShaFile(item.SavePath));
    }

    [Fact]
    public async Task DynamicSplit_HappensWhenAConnectionFinishesEarly()
    {
        // Başlangıç ofseti 0 olan (ilk parça) istek yavaş; diğeri erken bitip onu bölmeli.
        using var srv = new TestHttpServer(TestHttpServer.RandomData(4 * 1024 * 1024))
        {
            ChunkDelayMs = start => start == 0 ? 8 : 0
        };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, o => { o.Connections = 2; o.MinSplitSize = 128 * 1024; });
        int events = 0;
        engine.SegmentSplit += (_, _) => Interlocked.Increment(ref events);

        var st = await engine.RunAsync();

        Assert.Equal(DownloadStatus.Completed, st);
        Assert.True(engine.SplitCount >= 1, "Dinamik bölme gerçekleşmedi.");
        Assert.True(events >= 1);
        Assert.Equal(Sha(srv.Data), ShaFile(item.SavePath));
    }

    [Fact]
    public async Task SpeedLimit_IsRoughlyRespected()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(1024 * 1024));
        var item = NewItem(srv.Url);
        var limiter = new RateLimiter(256 * 1024); // 256 KB/s -> 1 MB için ~4 sn
        using var engine = NewEngine(item, lim: limiter);

        var sw = Stopwatch.StartNew();
        var st = await engine.RunAsync();
        sw.Stop();

        Assert.Equal(DownloadStatus.Completed, st);
        Assert.Equal(Sha(srv.Data), ShaFile(item.SavePath));
        Assert.InRange(sw.Elapsed.TotalSeconds, 2.0, 12.0); // sınırsız olsa <0.5 sn
    }

    [Fact]
    public async Task UnreachableServer_Fails_WithError()
    {
        var item = NewItem("http://127.0.0.1:1/x");
        using var engine = NewEngine(item, o => o.MaxRetries = 1);
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Failed, st);
        Assert.False(string.IsNullOrEmpty(item.Error));
    }
}
