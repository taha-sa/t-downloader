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

    [Fact]
    public async Task RateLimiterWait_DoesNotCountTowardsReadTimeout_NoReconnects()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(100 * 1024));
        // 40 KB/s sınır, 250 ms okuma zaman aşımı: bekleme zaman aşımına sayılsaydı sürekli yeniden bağlanırdı.
        var lim = new RateLimiter(40 * 1024);
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, o => { o.Connections = 1; o.ReadTimeout = TimeSpan.FromMilliseconds(250); o.MaxRetries = 20; }, lim);
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Completed, st);
        Assert.Equal(2, srv.Requests);   // 1 probe + 1 segment isteği; yeniden bağlanma yok
        Assert.Equal(Sha(srv.Data), Sha(File.ReadAllBytes(item.SavePath)));
    }

    [Fact]
    public async Task HostConnectionCap_LimitsConcurrency_AndQueuedWorkersDoNotTimeOut()
    {
        // Parça başına 3 x 300 ms = ~900 ms; tavan 2 => 3 dalga (~2,7 sn). ReadTimeout 1,5 sn: son dalga ~1,8 sn kuyrukta bekler
        // (ReadTimeout'tan uzun) ama tek okuma aralığı yalnızca 300 ms; bekleyenler zaman aşımına düşmemeli. Marjlar iki yönde de >=300 ms.
        using var srv = new ScriptServer(TestHttpServer.RandomData(6 * 64 * 1024));
        srv.Custom = (c, s, e, probe) => srv.Serve(c, s, e, chunkDelayMs: probe ? 0 : 300);
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, o => { o.Connections = 6; o.MaxConnectionsPerHost = 2; o.ReadTimeout = TimeSpan.FromSeconds(1.5); });
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Completed, st);
        Assert.True(srv.MaxInFlight <= 2, $"eşzamanlı bağlantı {srv.MaxInFlight}");
        Assert.Equal(1 + 6, srv.Requests);   // probe + 6 parça; zaman aşımıyla yeniden bağlanma yok
        Assert.Equal(Sha(srv.Data), Sha(File.ReadAllBytes(item.SavePath)));
    }

    [Fact]
    public async Task Overload429_HalvesHostLimit()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(256 * 1024));
        int hits = 0;
        srv.Custom = async (c, s, e, probe) =>
        {
            if (!probe && Interlocked.Increment(ref hits) == 1) { c.Response.StatusCode = 503; c.Response.AddHeader("Retry-After", "0"); c.Response.Close(); return; }
            await srv.Serve(c, s, e);
        };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, o => { o.Connections = 4; o.MaxConnectionsPerHost = 8; });
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Completed, st);
        var gate = HostGate.Peek(new Uri(srv.Url).Authority)!;
        Assert.Equal(4, gate.Limit);   // 8 -> 4
    }

    [Fact]
    public async Task HostGate_QueuesFifo_ThrottlesAndRestores()
    {
        // Statik HostGate.RestoreAfter'a dokunulmaz (testler arası sızıntı olmasın): süre örnek başına verilir.
        {
            var g = new HostGate(2) { RestoreAfterOverride = TimeSpan.FromMilliseconds(200) };
            var a = await g.AcquireAsync(CancellationToken.None);
            var b = await g.AcquireAsync(CancellationToken.None);
            var third = g.AcquireAsync(CancellationToken.None).AsTask();
            await Task.Delay(50);
            Assert.False(third.IsCompleted);
            a.Dispose();
            var l3 = await third.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, g.Active);
            // iptal edilen bekleyen yer tutmaz
            using var cts = new CancellationTokenSource();
            var fourth = g.AcquireAsync(cts.Token).AsTask();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fourth);
            b.Dispose(); l3.Dispose();
            Assert.Equal(0, g.Active);

            g.Throttle();
            Assert.Equal(1, g.Limit);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (g.Limit != 2 && DateTime.UtcNow < until) await Task.Delay(20);   // koşul beklemesi: sabit süre yok
            Assert.Equal(2, g.Limit);
        }
    }
}
