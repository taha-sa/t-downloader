using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

public sealed class ExternalDownloadTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-ext-" + Guid.NewGuid().ToString("N"));
    public ExternalDownloadTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private sealed class FakeRunner : IExternalRunner
    {
        public Func<DownloadItem, Action<ExternalProgress>, CancellationToken, Task<ExternalResult>> Body { get; set; } = null!;
        public Task<ExternalResult> RunAsync(DownloadItem item, Action<ExternalProgress> report, CancellationToken ct) => Body(item, report, ct);
    }

    private DownloadManager Mgr(FakeRunner r) => new(new AppPaths(Path.Combine(_tmp, "ad")),
        new AppSettings { SaveFolder = Path.Combine(_tmp, "dl") }) { ExternalRunner = r };

    [Fact]
    public async Task External_Completes_WithProgress_AndFinalPath()
    {
        var progress = new List<DownloadProgress>();
        var r = new FakeRunner
        {
            Body = async (item, report, ct) =>
            {
                report(new ExternalProgress(500, 1000, 100, 5));
                report(new ExternalProgress(1000, 1000, 100, 0));
                var path = Path.ChangeExtension(item.SavePath, ".m4a");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, new byte[1234], ct);
                return new ExternalResult(true, null, path);
            }
        };
        var mgr = Mgr(r);
        mgr.ProgressChanged += (_, p) => progress.Add(p);
        var done = new TaskCompletionSource<DownloadItem>();
        mgr.DownloadCompleted += (_, i) => done.TrySetResult(i);
        var item = mgr.AddExternal(new AddExternalRequest("https://ornek.test/v", "Klip.mp4", "best"));
        Assert.Equal(DownloadKind.External, item.Kind);
        Assert.Equal(Categories.Video, item.Category);
        var fin = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Completed, fin.Status);
        Assert.EndsWith("Klip.m4a", fin.SavePath);
        Assert.Equal(Categories.Music, fin.Category);
        Assert.Equal(1234, fin.TotalSize);
        Assert.Equal(2, progress.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), progress[0].Eta);
    }

    [Fact]
    public async Task External_Failure_SetsErrorMessage()
    {
        var r = new FakeRunner { Body = (_, _, _) => Task.FromResult(new ExternalResult(false, "Bu içerik DRM korumalı.", null)) };
        var mgr = Mgr(r);
        var failed = new TaskCompletionSource<DownloadItem>();
        mgr.DownloadFailed += (_, i) => failed.TrySetResult(i);
        mgr.AddExternal(new AddExternalRequest("https://ornek.test/v", "k.mp4", "best"));
        var it = await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DownloadStatus.Failed, it.Status);
        Assert.Contains("DRM", it.Error);
    }

    [Fact]
    public async Task External_Pause_CancelsRunner_AndResumeRestarts()
    {
        int runs = 0;
        var started = new SemaphoreSlim(0);
        var r = new FakeRunner
        {
            Body = async (item, report, ct) =>
            {
                int n = Interlocked.Increment(ref runs);
                started.Release();
                if (n == 1) await Task.Delay(Timeout.Infinite, ct);
                return new ExternalResult(true, null, null);
            }
        };
        var mgr = Mgr(r);
        var item = mgr.AddExternal(new AddExternalRequest("https://ornek.test/v", "k.mp4", "best"));
        await started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Downloading, item.Status);
        mgr.Pause(item.Id);
        for (int i = 0; i < 100 && item.Status != DownloadStatus.Paused; i++) await Task.Delay(50);
        Assert.Equal(DownloadStatus.Paused, item.Status);
        mgr.Start(item.Id);
        for (int i = 0; i < 100 && item.Status != DownloadStatus.Completed; i++) await Task.Delay(50);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task ExternalItem_PersistsKindAndFormat()
    {
        var r = new FakeRunner { Body = (_, _, ct) => Task.FromResult(new ExternalResult(false, null, null)) };
        var mgr = Mgr(r);
        mgr.AddExternal(new AddExternalRequest("https://ornek.test/v", "k.mp4", "137+bestaudio", StartNow: false));
        await mgr.ShutdownAsync();
        var mgr2 = new DownloadManager(new AppPaths(Path.Combine(_tmp, "ad")), new AppSettings());
        mgr2.Load();
        var it = Assert.Single(mgr2.Items);
        Assert.Equal(DownloadKind.External, it.Kind);
        Assert.Equal("137+bestaudio", it.ExternalFormat);
    }

    [Fact]
    public void ToolLocator_SearchOrder()
    {
        var a = Path.Combine(_tmp, "cfg"); var b = Path.Combine(_tmp, "appdata-tools"); var c = Path.Combine(_tmp, "app", "tools"); var d = Path.Combine(_tmp, "path");
        var app = Path.Combine(_tmp, "app");
        foreach (var x in new[] { a, b, c, d }) Directory.CreateDirectory(x);
        Assert.Null(ToolLocator.Find("yt-dlp", null, b, app, new[] { d }));
        File.WriteAllText(Path.Combine(d, "yt-dlp.exe"), "");
        Assert.Equal(Path.Combine(d, "yt-dlp.exe"), ToolLocator.Find("yt-dlp", null, b, app, new[] { d }));
        File.WriteAllText(Path.Combine(c, "yt-dlp.exe"), "");
        Assert.Equal(Path.Combine(c, "yt-dlp.exe"), ToolLocator.Find("yt-dlp", null, b, app, new[] { d }));
        File.WriteAllText(Path.Combine(b, "yt-dlp.exe"), "");
        Assert.Equal(Path.Combine(b, "yt-dlp.exe"), ToolLocator.Find("yt-dlp", null, b, app, new[] { d }));
        File.WriteAllText(Path.Combine(a, "yt-dlp.exe"), "");
        Assert.Equal(Path.Combine(a, "yt-dlp.exe"), ToolLocator.Find("yt-dlp", a, b, app, new[] { d }));
        Assert.Equal(Path.Combine(a, "yt-dlp.exe"), ToolLocator.Find("yt-dlp", Path.Combine(a, "yt-dlp.exe"), b, app, new[] { d }));
    }
}
