using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using TDownloader.Core.Downloads;

namespace TDownloader.Core.Tests;

/// <summary>Motor dayanıklılığı: işçi emekliliği, Retry-After, async yazma, zaman aşımı, bağlantı tavanı, Pause yarışı.</summary>
public sealed class EngineResilienceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-res-" + Guid.NewGuid().ToString("N"));
    public EngineResilienceTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private DownloadItem NewItem(string url) => new() { Url = url, FileName = "o.bin", SavePath = Path.Combine(_tmp, "save", "o.bin") };

    private DownloadEngine NewEngine(DownloadItem item, Action<EngineOptions>? cfg = null, RateLimiter? lim = null)
    {
        var o = new EngineOptions
        {
            Connections = 4, MinSplitSize = 64 * 1024, MaxRetries = 2, RetryBaseDelay = TimeSpan.FromMilliseconds(30),
            ProgressInterval = TimeSpan.FromMilliseconds(50), StateSaveInterval = TimeSpan.FromMilliseconds(100)
        };
        cfg?.Invoke(o);
        return new DownloadEngine(item, Path.Combine(_tmp, "DwnlData"), o, lim, EngineHttp.Create());
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    [Fact]
    public async Task OneWorkerExhaustingRetries_DoesNotFailDownload_OthersTakeOver()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(1024 * 1024));
        int failedStart0 = 0;
        srv.Custom = async (c, s, e, probe) =>
        {
            // İlk parçanın (start==0) ilk MaxRetries+1 isteği hemen kopar: o işçi bütçesini doldurur.
            if (!probe && s == 0 && Interlocked.Increment(ref failedStart0) <= 3) { c.Response.Abort(); return; }
            await srv.Serve(c, s, e);
        };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item);

        var st = await engine.RunAsync();

        Assert.Equal(DownloadStatus.Completed, st);
        Assert.Equal(Sha(srv.Data), Sha(File.ReadAllBytes(item.SavePath)));
        Assert.True(Volatile.Read(ref failedStart0) >= 4);   // 3 başarısız + devralan işçinin başarılı isteği
    }

    [Fact]
    public async Task AllWorkersFailingTogether_FailsDownload()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(512 * 1024));
        srv.Custom = async (c, s, e, probe) =>
        {
            if (probe) { await srv.Serve(c, s, e); return; }
            c.Response.Abort();
        };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, o => o.MaxRetries = 1);
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Failed, st);
        Assert.False(string.IsNullOrEmpty(item.Error));
    }

    [Fact]
    public async Task Permanent403_FailsImmediately_WithoutRetries()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(512 * 1024));
        srv.Custom = async (c, s, e, probe) =>
        {
            if (probe) { await srv.Serve(c, s, e); return; }
            c.Response.StatusCode = 403; c.Response.Close();
        };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, o => { o.MaxRetries = 5; o.RetryBaseDelay = TimeSpan.FromSeconds(5); });
        var sw = Stopwatch.StartNew();
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Failed, st);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), "403 yeniden denenmemeli");
        Assert.True(srv.Requests <= 1 + 4);   // probe + işçi başına en çok 1
    }

    [Fact]
    public async Task RetryAfter_On429_IsHonored()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(200 * 1024));
        int hits = 0;
        srv.Custom = async (c, s, e, probe) =>
        {
            if (!probe && Interlocked.Increment(ref hits) == 1)
            {
                c.Response.StatusCode = 429; c.Response.AddHeader("Retry-After", "1"); c.Response.Close(); return;
            }
            await srv.Serve(c, s, e);
        };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, o => o.Connections = 1);
        var sw = Stopwatch.StartNew();
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Completed, st);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(900), $"Retry-After beklenmeli ({sw.ElapsedMilliseconds} ms)");
        Assert.Equal(Sha(srv.Data), Sha(File.ReadAllBytes(item.SavePath)));
    }

    [Fact]
    public async Task DiskHandle_IsAsynchronous_AndFileIsCorrect()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(2 * 1024 * 1024 + 5));
        srv.Custom = (c, s, e, probe) => srv.Serve(c, s, e, chunkDelayMs: probe ? 0 : 2);
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item);
        var field = typeof(DownloadEngine).GetField("_handle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool? isAsync = null;
        engine.ProgressChanged += (_, _) =>
        {
            if (field.GetValue(engine) is Microsoft.Win32.SafeHandles.SafeFileHandle h && !h.IsClosed) isAsync ??= h.IsAsync;
        };
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Completed, st);
        Assert.True(isAsync, "kısmi dosya FileOptions.Asynchronous ile açılmalı");
        Assert.Equal(Sha(srv.Data), Sha(File.ReadAllBytes(item.SavePath)));
    }
}
