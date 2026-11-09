using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Tests;

/// <summary>Kuyruk dayanıklılığı: abone istisnası, Pause yarışı.</summary>
public sealed class ManagerResilienceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-mres-" + Guid.NewGuid().ToString("N"));
    public ManagerResilienceTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public async Task ThrowingCompletedSubscriber_DoesNotStopQueue()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(300_000));
        var settings = new AppSettings { SaveFolder = Path.Combine(_tmp, "dl"), Connections = 2, MaxConcurrentDownloads = 1 };
        var mgr = new DownloadManager(new AppPaths(Path.Combine(_tmp, "app")), settings) { ConfigureEngine = o => o.MinSplitSize = 64 * 1024 };
        int completed = 0;
        var both = new TaskCompletionSource();
        mgr.DownloadCompleted += (_, _) => throw new InvalidOperationException("abone patladı");
        mgr.DownloadCompleted += (_, _) => { if (Interlocked.Increment(ref completed) == 2) both.TrySetResult(); };

        mgr.Add(new AddDownloadRequest(srv.Url, "a.zip", StartNow: false));
        mgr.Add(new AddDownloadRequest(srv.Url, "b.zip", StartNow: false));
        foreach (var i in mgr.Items) mgr.Start(i.Id);

        await both.Task.WaitAsync(TimeSpan.FromSeconds(30));   // ikinci indirme, birincinin olayı patlasa da başladı
        Assert.All(mgr.Items, i => Assert.Equal(DownloadStatus.Completed, i.Status));
        await mgr.ShutdownAsync();
    }

    [Fact]
    public async Task Pause_BeforeRunAsyncCreatesCts_IsNotLost()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(500_000));
        var item = new DownloadItem { Url = srv.Url, FileName = "o.bin", SavePath = Path.Combine(_tmp, "o.bin") };
        using var engine = new DownloadEngine(item, Path.Combine(_tmp, "DwnlData"), new EngineOptions { Connections = 2, MinSplitSize = 64 * 1024 });
        engine.Pause();                       // RunAsync henüz başlamadı
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Paused, st);
        Assert.Equal(0, srv.RequestCount);    // hiç istek atılmadı
        Assert.False(File.Exists(item.SavePath));
    }

    [Fact]
    public async Task Resume_ImmediatelyAfterPause_IsNotSwallowed()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(400_000)) { ChunkDelayMs = _ => 40 };
        var settings = new AppSettings { SaveFolder = Path.Combine(_tmp, "dl"), Connections = 1, MaxConcurrentDownloads = 1 };
        var mgr = new DownloadManager(new AppPaths(Path.Combine(_tmp, "app")), settings) { ConfigureEngine = o => o.MinSplitSize = 64 * 1024 };
        var done = new TaskCompletionSource();
        mgr.DownloadCompleted += (_, _) => done.TrySetResult();
        mgr.Add(new AddDownloadRequest(srv.Url, "r.bin", StartNow: false));
        var id = mgr.Items[0].Id;
        mgr.Start(id);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (mgr.ActiveCount == 0 && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.Equal(1, mgr.ActiveCount);

        mgr.Pause(id);
        mgr.Start(id);                        // Duraklat bitmeden Devam: yutulmamalı

        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(DownloadStatus.Completed, mgr.Items[0].Status);
        await mgr.ShutdownAsync();
    }

    [Fact]
    public async Task StartGate_False_KeepsItemPaused_NoRequest()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(100_000));
        var settings = new AppSettings { SaveFolder = Path.Combine(_tmp, "dl"), Connections = 1, MaxConcurrentDownloads = 1 };
        var mgr = new DownloadManager(new AppPaths(Path.Combine(_tmp, "app")), settings);
        mgr.Add(new AddDownloadRequest(srv.Url, "g.bin", StartNow: false));
        var id = mgr.Items[0].Id;
        mgr.StartGate = _ => false;
        mgr.Start(id);
        Assert.Equal(DownloadStatus.Paused, mgr.Items[0].Status);
        Assert.Equal(0, mgr.ActiveCount);
        Assert.Equal(0, srv.RequestCount);
        await mgr.ShutdownAsync();
    }
}
