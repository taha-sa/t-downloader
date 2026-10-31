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
}
