using System.Collections.Concurrent;
using TDownloader.Core.Ipc;

namespace TDownloader.Host.Tests;

/// <summary>
/// Onay penceresi x eklenti zaman aşımı (çift indirme) senaryosu: sahte App = IpcServer + DownloadCaptureGate + bloklayan
/// "onay penceresi". Görünür pencere yok; benzersiz pipe adı (gerçek TDownloader.Ipc'e dokunulmaz).
/// </summary>
public class CaptureFlowTests
{
    private static string NewPipe() => "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");

    private static DownloadMessage Dl(string id, string url = "https://x/a.zip") =>
        new(id, url, null, null, null, "a.zip", "application/zip", 5);

    /// <summary>Sahte onay penceresi: aynı anda tek pencere (gerçek uygulamadaki sıra kapısı gibi), Release verilene kadar bloklar.</summary>
    private sealed class FakeWindow
    {
        private readonly SemaphoreSlim _one = new(1, 1);
        public readonly ConcurrentQueue<string> Shown = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Concurrent, MaxConcurrent;
        public async Task Show(DownloadMessage d)
        {
            await _one.WaitAsync();
            try
            {
                int c = Interlocked.Increment(ref Concurrent);
                MaxConcurrent = Math.Max(MaxConcurrent, c);
                Shown.Enqueue(d.Url);
                await Release.Task;          // kullanıcı pencereyi kapatana kadar
                await Task.Delay(2);
                Interlocked.Decrement(ref Concurrent);
            }
            finally { _one.Release(); }
        }
    }

    /// <summary>Tek bağlantıda yanıtları requestId/tür ile toplayan istemci.</summary>
    private sealed class Probe : IDisposable
    {
        public readonly IpcClient Client;
        public readonly ConcurrentDictionary<string, DownloadResultMessage> Results = new();
        public readonly TaskCompletionSource Pong = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Probe(string pipe)
        {
            Client = new IpcClient(pipe);
            Assert.True(Client.ConnectAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await Client.ReceiveAsync() is { } m)
                    {
                        if (m is DownloadResultMessage r) Results[r.RequestId] = r;
                        else if (m is PongMessage) Pong.TrySetResult();
                    }
                }
                catch (Exception) { }
            });
        }
        public void Dispose() => Client.Dispose();
    }

    private static IpcServer MakeApp(DownloadCaptureGate gate, Func<IpcMessage, Task<IpcMessage?>>? extra = null) =>
        new(NewPipe())
        {
            MessageHandler = async (m, _) => m switch
            {
                PingMessage => new PongMessage("1.0.0"),
                DownloadMessage d => gate.Handle(d),
                _ => extra != null ? await extra(m) : null,
            }
        };

    [Fact]
    public async Task Gate_RulesAndLimits()
    {
        var shown = new ConcurrentBag<string>();
        var gate = new DownloadCaptureGate(d => !d.Url.Contains("blocked"), d => { shown.Add(d.Url); return Task.CompletedTask; });
        Assert.True(gate.Handle(Dl("1")).Accepted);
        Assert.False(gate.Handle(Dl("2", "https://x/blocked.zip")).Accepted);     // kural reddi: hemen false
        Assert.False(gate.Handle(Dl("3", "ftp://x/a.zip")).Accepted);              // http(s) değil
        Assert.False(gate.Handle(Dl("4", "")).Accepted);
        Assert.True(SpinWait.SpinUntil(() => shown.Count == 1, 3000));
        Assert.Equal("https://x/a.zip", shown.Single());

        // Pencere hata verse bile sayaç düşer.
        var bad = new DownloadCaptureGate(_ => true, _ => throw new InvalidOperationException("pencere patladı"));
        Assert.True(bad.Handle(Dl("5")).Accepted);
        Assert.True(SpinWait.SpinUntil(() => bad.Pending == 0, 3000));

        // Üst sınır: sınırı aşan istek tarayıcıya bırakılır.
        var hold = new TaskCompletionSource();
        var small = new DownloadCaptureGate(_ => true, _ => hold.Task) { MaxPending = 2 };
        Assert.True(small.Handle(Dl("a")).Accepted);
        Assert.True(small.Handle(Dl("b")).Accepted);
        Assert.False(small.Handle(Dl("c")).Accepted);
        hold.SetResult();
        Assert.True(SpinWait.SpinUntil(() => small.Pending == 0, 3000));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Ipc_50ConcurrentDownloads_AllAnsweredWithin15s_WhileWindowIsOpen_PingStillFlows()
    {
        var win = new FakeWindow();
        var gate = new DownloadCaptureGate(_ => true, win.Show);
        await using var app = MakeApp(gate);
        app.Start();
        using var p = new Probe(app.PipeNameForTests);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) await p.Client.SendAsync(Dl("r" + i, $"https://x/{i}.zip"));
        Assert.True(SpinWait.SpinUntil(() => p.Results.Count == 50, 15000), $"Yanıt gelen: {p.Results.Count}/50");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        Assert.All(p.Results.Values, r => Assert.True(r.Accepted));

        // Onay penceresi (sahte, bloklayan) hâlâ açık: ping yine de yanıtlanır.
        Assert.True(SpinWait.SpinUntil(() => win.Shown.Count == 1, 3000));
        Assert.False(win.Release.Task.IsCompleted);
        await p.Client.SendAsync(new PingMessage());
        await p.Pong.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Pencereler sırayla açılır (aynı anda tek), her URL tam bir kez (çift indirme yok).
        win.Release.SetResult();
        Assert.True(SpinWait.SpinUntil(() => gate.Pending == 0, 15000));
        Assert.Equal(50, win.Shown.Count);
        Assert.Equal(50, win.Shown.Distinct().Count());
        Assert.Equal(1, win.MaxConcurrent);
    }

    [Fact]
    public async Task SlowHandler_DoesNotBlock_OtherMessages_OnSameConnection()
    {
        var gate = new DownloadCaptureGate(_ => true, _ => Task.CompletedTask);
        var slowGate = new TaskCompletionSource();
        await using var app = MakeApp(gate, async m =>
        {
            if (m is QualitiesMessage q) { await slowGate.Task; return new QualitiesResultMessage(q.RequestId, true, null, null, "T", "s", new()); }
            return null;
        });
        app.Start();
        using var p = new Probe(app.PipeNameForTests);

        await p.Client.SendAsync(new QualitiesMessage("q1", 1, 0, null, null, null, null));   // yt-dlp gibi yavaş
        await p.Client.SendAsync(new PingMessage());
        await p.Pong.Task.WaitAsync(TimeSpan.FromSeconds(3));                                   // yavaş iş beklenmeden yanıt
        await p.Client.SendAsync(Dl("d1"));
        Assert.True(SpinWait.SpinUntil(() => p.Results.ContainsKey("d1"), 3000));
        Assert.False(slowGate.Task.IsCompleted);
        slowGate.SetResult();
    }

    [Fact]
    public async Task Handler_RunsInOrder_ForSynchronousMessages()
    {
        var seen = new ConcurrentQueue<int>();
        await using var app = new IpcServer(NewPipe())
        {
            MessageHandler = (m, _) => { if (m is HideButtonMessage h) seen.Enqueue(h.TabId); return Task.FromResult<IpcMessage?>(null); }
        };
        app.Start();
        using var client = new IpcClient(app.PipeNameForTests);
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(5)));
        for (int i = 0; i < 200; i++) await client.SendAsync(new HideButtonMessage(i));
        Assert.True(SpinWait.SpinUntil(() => seen.Count == 200, 5000));
        Assert.Equal(Enumerable.Range(0, 200), seen.ToArray());
    }

    [Fact]
    public async Task Host_50ConcurrentDownloads_AllAnswered_WithinTimeout()
    {
        var win = new FakeWindow();
        var gate = new DownloadCaptureGate(_ => true, win.Show);
        await using var app = MakeApp(gate);
        app.Start();
        using var host = new HostProcessTests.HostProc(app.PipeNameForTests);

        for (int i = 0; i < 50; i++) await host.Send(Dl("h" + i, $"https://x/{i}.zip"));
        await host.Send(new PingMessage());
        var got = new HashSet<string>();
        bool pong = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (got.Count < 50 || !pong)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Zaman aşımı: {got.Count}/50 pong={pong}");
            var m = await host.Receive(15000);
            if (m is DownloadResultMessage r) { Assert.True(r.Accepted); Assert.True(got.Add(r.RequestId)); }
            else if (m is PongMessage) pong = true;
        }
        Assert.False(win.Release.Task.IsCompleted);   // pencere hâlâ açıkken hepsi yanıtlandı
        win.Release.SetResult();
        Assert.True(SpinWait.SpinUntil(() => gate.Pending == 0, 15000));
        Assert.Equal(50, win.Shown.Distinct().Count());
    }
}
