using TDownloader.Core.Ipc;

namespace TDownloader.Host.Tests;

public class AppSupportTests
{
    // ---------------------------------------------------------------- mesajlar

    [Fact]
    public void NewMessages_RoundTrip_AndStayBackwardCompatible()
    {
        var media = new MediaMessage(5, 2, "https://p/", "T", new() { new MediaItem("https://m/v.mp4", "video/mp4", "file") }, "https://f/", Append: true);
        var json = IpcSerializer.Serialize(media);
        Assert.Contains("\"append\":true", json);
        Assert.True(Assert.IsType<MediaMessage>(IpcSerializer.Deserialize(json)).Append);

        // Eski eklenti: append yok -> değiştir (false); çerezli eski öğe de okunur.
        var old = Assert.IsType<MediaMessage>(IpcSerializer.Deserialize(
            "{\"type\":\"media\",\"tabId\":1,\"frameId\":0,\"items\":[{\"url\":\"https://a/x.mp4\",\"kind\":\"file\",\"cookies\":\"a=b\"}]}"));
        Assert.False(old.Append);
        Assert.Equal("a=b", old.Items[0].Cookies);

        var q = Assert.IsType<QualitiesMessage>(IpcSerializer.Deserialize(
            "{\"type\":\"qualities\",\"requestId\":\"r\",\"tabId\":3,\"frameId\":0,\"cookies\":{\"https://c.test\":\"k=v\"}}"));
        Assert.Equal("k=v", q.Cookies!["https://c.test"]);

        Assert.Equal("c1", Assert.IsType<QualitiesCancelMessage>(IpcSerializer.Deserialize("{\"type\":\"qualitiesCancel\",\"requestId\":\"c1\"}")).RequestId);
        Assert.Equal(9, Assert.IsType<TabClosedMessage>(IpcSerializer.Deserialize("{\"type\":\"tabClosed\",\"tabId\":9}")).TabId);
        Assert.True(Assert.IsType<DownloadMessage>(IpcSerializer.Deserialize("{\"type\":\"download\",\"requestId\":\"a\",\"url\":\"https://x\",\"fileSize\":1,\"explicit\":true}")).Explicit);
        Assert.False(Assert.IsType<DownloadMessage>(IpcSerializer.Deserialize("{\"type\":\"download\",\"requestId\":\"a\",\"url\":\"https://x\",\"fileSize\":1}")).Explicit);
    }

    [Fact]
    public void MediaMerge_AppendAddsOnlyNew_ReplaceOverwrites_AndIsBounded()
    {
        MediaItem I(string u) => new(u, "video/mp4", "file");
        var first = MediaMerge.Merge(null, new MediaMessage(1, 0, null, null, new() { I("a"), I("b") }, Append: true));
        Assert.Equal(new[] { "a", "b" }, first.Select(i => i.Url));
        var second = MediaMerge.Merge(first, new MediaMessage(1, 0, null, null, new() { I("b"), I("c") }, Append: true));
        Assert.Equal(new[] { "a", "b", "c" }, second.Select(i => i.Url));
        var replaced = MediaMerge.Merge(second, new MediaMessage(1, 0, null, null, new() { I("z") }));
        Assert.Equal(new[] { "z" }, replaced.Select(i => i.Url));
        Assert.Empty(MediaMerge.Merge(second, new MediaMessage(1, 0, null, null, new())));   // temizleme

        var many = Enumerable.Range(0, 500).Select(n => I("u" + n)).ToList();
        Assert.Equal(MediaMerge.MaxItemsPerFrame, MediaMerge.Merge(null, new MediaMessage(1, 0, null, null, many)).Count);
        Assert.Equal(MediaMerge.MaxItemsPerFrame, MediaMerge.Merge(second, new MediaMessage(1, 0, null, null, many, Append: true)).Count);
    }

    [Fact]
    public void MediaCookies_AppliedPerOrigin_OnlyWhereMissing()
    {
        var items = new List<MediaItem>
        {
            new("https://cdn1.test/a.mp4", null, "file"),
            new("https://cdn2.test:8443/x.m3u8", null, "hls"),
            new("https://other.test/y.mp4", null, "file"),
            new("https://cdn1.test/own.mp4", null, "file", Cookies: "own=1"),
        };
        var map = new Dictionary<string, string> { ["https://cdn1.test"] = "k=v", ["https://cdn2.test:8443"] = "p=q" };
        var r = MediaCookies.Apply(items, map);
        Assert.Equal(new string?[] { "k=v", "p=q", null, "own=1" }, r.Select(i => i.Cookies));
        Assert.Same(items, MediaCookies.Apply(items, null));
    }

    [Fact]
    public void Host_OversizedQualitiesResult_IsTrimmed_NotSilentlyDropped()
    {
        var opts = Enumerable.Range(0, 400).Select(i => new QualityOptionInfo(new string('x', 4000) + i, "video", 720, 1, false, i == 0)).ToList();
        var big = new QualitiesResultMessage("r", true, null, null, "T", "s", opts);
        var bytes = HostRunner.FitForBrowser(big, out var note);
        Assert.NotNull(bytes);
        Assert.True(bytes!.Length <= NativeMessaging.MaxMessageBytes);
        Assert.Contains("kırpıldı", note);
        var back = Assert.IsType<QualitiesResultMessage>(IpcSerializer.Deserialize(System.Text.Encoding.UTF8.GetString(bytes)));
        Assert.True(back.Options!.Count is > 0 and < 400);
        Assert.Equal("r", back.RequestId);

        var small = HostRunner.FitForBrowser(new PongMessage("1"), out var none);
        Assert.NotNull(small); Assert.Null(none);
        var huge = HostRunner.FitForBrowser(new SettingsMessage(true, 1, new() { new string('a', 2_000_000) }, new()), out var dropped);
        Assert.Null(huge); Assert.Contains("düşürüldü", dropped);
    }

    // ---------------------------------------------------------------- kalite önbelleği

    [Fact]
    public void QualityCacheKey_DropsSignaturesKeepsIdentity()
    {
        Assert.Equal(QualityCacheKey.For("https://c.test/v/master.m3u8?id=5&token=AAA&expires=1"),
                     QualityCacheKey.For("https://c.test/v/master.m3u8?expires=999&id=5&token=BBB"));
        Assert.NotEqual(QualityCacheKey.For("https://c.test/v.m3u8?id=5"), QualityCacheKey.For("https://c.test/v.m3u8?id=6"));
        Assert.Equal("https://c.test/v.m3u8", QualityCacheKey.For("https://c.test/v.m3u8?X-Amz-Signature=abc&X-Amz-Date=1"));
        Assert.Equal("set|https://a/x.m3u8|https://b/y.m3u8", QualityCacheKey.ForSet(new[] { "https://a/x.m3u8?sig=1", "https://b/y.m3u8?sig=2" }));
        Assert.Equal("not a url", QualityCacheKey.For("not a url"));
    }

    [Fact]
    public async Task SharedTaskCache_SharesInflightWork()
    {
        var cache = new SharedTaskCache<int>(10, TimeSpan.FromMinutes(1));
        int started = 0;
        var gate = new TaskCompletionSource<int>();
        Task<int> Start() => cache.GetOrStart("k", _ => { Interlocked.Increment(ref started); return gate.Task; });
        var a = Start(); var b = Start();
        gate.SetResult(7);
        Assert.Equal(new[] { 7, 7 }, await Task.WhenAll(a, b));
        Assert.Equal(7, await Start());
        Assert.Equal(1, started);
        Assert.True(cache.TryGetCompleted("k", out var v)); Assert.Equal(7, v);
    }

    [Fact]
    public async Task SharedTaskCache_LastWaiterCancel_CancelsWork_ButOtherWaiterKeepsIt()
    {
        var cache = new SharedTaskCache<int>(10, TimeSpan.FromMinutes(1));
        CancellationToken workToken = default;
        var started = new TaskCompletionSource();
        Task<int> Work(CancellationToken ct) { workToken = ct; started.TrySetResult(); return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => 1, CancellationToken.None); }

        using var c1 = new CancellationTokenSource(); using var c2 = new CancellationTokenSource();
        var w1 = cache.GetOrStart("k", Work, c1.Token);
        var w2 = cache.GetOrStart("k", Work, c2.Token);
        await started.Task;
        c1.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w1);
        Assert.False(workToken.IsCancellationRequested, "diğer bekleyen hâlâ var: iş (yt-dlp) öldürülmemeli");
        c2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w2);
        Assert.True(SpinWait.SpinUntil(() => workToken.IsCancellationRequested, 3000), "tüm bekleyenler vazgeçti: iş iptal edilmeli (süreç öldürülür)");
        Assert.False(cache.Contains("k"));
    }

    [Fact]
    public async Task SharedTaskCache_IsBounded_Lru_Ttl_AndRestartsFailures()
    {
        var now = DateTime.UtcNow;
        var cache = new SharedTaskCache<string>(3, TimeSpan.FromMinutes(10), () => now);
        for (int i = 0; i < 6; i++) { await cache.GetOrStart("k" + i, _ => Task.FromResult("v"), default); now = now.AddSeconds(1); }
        Assert.Equal(3, cache.Count);
        Assert.False(cache.Contains("k0")); Assert.True(cache.Contains("k5"));

        now = now.AddMinutes(11);                                   // TTL doldu
        await cache.GetOrStart("fresh", _ => Task.FromResult("v"), default);
        Assert.Equal(1, cache.Count);

        int calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrStart("bad", _ => { calls++; return Task.FromException<string>(new InvalidOperationException()); }, default));
        Assert.Equal("ok", await cache.GetOrStart("bad", _ => { calls++; return Task.FromResult("ok"); }, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SharedTaskCache_ConcurrentAccess_IsSafe()
    {
        var cache = new SharedTaskCache<int>(20, TimeSpan.FromMinutes(1));
        int starts = 0;
        await Task.WhenAll(Enumerable.Range(0, 400).Select(i => Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource();
            var t = cache.GetOrStart("k" + (i % 40), async _ => { Interlocked.Increment(ref starts); await Task.Delay(5); return i; }, i % 3 == 0 ? cts.Token : default);
            if (i % 7 == 0) cts.Cancel();
            try { await t; } catch (OperationCanceledException) { }
        })));
        Assert.True(cache.Count <= 20);
        Assert.True(starts >= 20);
    }

    // ---------------------------------------------------------------- sayaçlar / sınırlar / pano

    [Fact]
    public void DownloadStats_IncrementalCounters()
    {
        var s = new DownloadStats();
        s.Set("a", true, 100); s.Set("b", true, 50); s.Set("c", false, 999);
        Assert.Equal((2, 150.0, 3), (s.Active, s.Speed, s.Total));
        s.Set("a", true, 30);                                   // hız güncellemesi
        Assert.Equal(80.0, s.Speed);
        s.Set("b", false, 0);                                   // tamamlandı
        Assert.Equal((1, 30.0), (s.Active, s.Speed));
        s.Remove("a"); s.Remove("zzz");
        Assert.Equal((0, 0.0, 2), (s.Active, s.Speed, s.Total));

        // 100 bin öğe: artımlı güncelleme hızlı (O(1))
        var big = new DownloadStats();
        for (int i = 0; i < 100_000; i++) big.Set("i" + i, i % 10 == 0, 10);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100_000; i++) big.Set("i" + (i % 1000), true, i);
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.Equal(100_000, big.Total);
    }

    [Fact]
    public void BoundedDictionary_DropsOldest()
    {
        var d = new Dictionary<string, DateTime>();
        var t0 = DateTime.UtcNow;
        for (int i = 0; i < 10; i++) d["k" + i] = t0.AddSeconds(i);
        BoundedDictionary.TrimOldest(d, 4, v => v);
        Assert.Equal(4, d.Count);
        Assert.False(d.ContainsKey("k0")); Assert.True(d.ContainsKey("k9"));
    }

    [Fact]
    public void StaCall_ReturnsValue_TimesOutWithoutBlocking_SwallowsErrors()
    {
        Assert.True(StaCall.TryRun(() => "pano", 2000, out var ok)); Assert.Equal("pano", ok);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(StaCall.TryRun(() => { Thread.Sleep(3000); return "gec"; }, 150, out var late));
        Assert.True(sw.ElapsedMilliseconds < 1500, "UI thread süre sınırından fazla beklememeli");
        Assert.Null(late);

        Assert.False(StaCall.TryRun<string>(() => throw new InvalidOperationException("pano meşgul"), 2000, out _));
        if (OperatingSystem.IsWindows())
            Assert.True(StaCall.TryRun(() => Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, 2000, out var sta) && sta);
    }
}
