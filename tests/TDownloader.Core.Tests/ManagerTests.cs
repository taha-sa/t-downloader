using System.Security.Cryptography;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Tests;

public sealed class ManagerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-mgr-" + Guid.NewGuid().ToString("N"));
    public ManagerTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public async Task Manager_Downloads_Categorizes_AndPersists()
    {
        using var srv = new TestHttpServer(TestHttpServer.RandomData(600_000));
        var paths = new AppPaths(Path.Combine(_tmp, "appdata"));
        var settings = new AppSettings { SaveFolder = Path.Combine(_tmp, "dl"), Connections = 3, MaxConcurrentDownloads = 2 };
        var mgr = new DownloadManager(paths, settings) { ConfigureEngine = o => o.MinSplitSize = 64 * 1024 };
        var done = new TaskCompletionSource<DownloadItem>();
        mgr.DownloadCompleted += (_, i) => done.TrySetResult(i);

        var item = mgr.Add(new AddDownloadRequest(srv.Url, "ornek.zip"));
        Assert.Equal(Categories.Compressed, item.Category);
        var finished = await done.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(DownloadStatus.Completed, finished.Status);
        Assert.Contains(Path.Combine("dl", Categories.Compressed), finished.SavePath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(srv.Data)), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(finished.SavePath))));

        await mgr.ShutdownAsync();
        var mgr2 = new DownloadManager(paths, settings);
        mgr2.Load();
        var loaded = Assert.Single(mgr2.Items);
        Assert.Equal(DownloadStatus.Completed, loaded.Status);
        Assert.Equal(item.Id, loaded.Id);
    }

    [Theory]
    [InlineData("a.mp4", "Video")]
    [InlineData("a.pdf", "Belgeler")]
    [InlineData("a.mp3", "Müzik")]
    [InlineData("a.exe", "Programlar")]
    [InlineData("a.png", "Resimler")]
    [InlineData("photo.jpg", "Resimler")]
    [InlineData("a.xyz", "Genel")]
    public void Category_ByExtension(string name, string cat) => Assert.Equal(cat, Categories.FromFileName(name));
}
