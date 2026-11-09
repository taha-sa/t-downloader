using System.Collections.Concurrent;
using TDownloader.App.Ui;
using TDownloader.Core.Ipc;
using TDownloader.Core.Video;

namespace TDownloader.Host.Tests;

/// <summary>docs/review/02-app-ipc-eklenti.md düzeltmeleri (A1, A3, A4, A5, A7, A11). Pencere açılmaz; benzersiz pipe adları.</summary>
public class ReviewFixTests
{
    // ---------------------------------------------------------------- A4: iptal sonrası tekrar sorulma

    [Fact]
    public void PromptClaims_PendingBlocksRegardlessOfTime_CancelReleasesImmediately()
    {
        var now = DateTime.UtcNow;
        var c = new PromptClaims(() => now);
        Assert.True(c.TryClaim("https://x/a.zip"));
        now += TimeSpan.FromMinutes(5);                           // kuyrukta çok uzun bekledi: talep süresi DOLMAZ
        Assert.False(c.TryClaim("https://x/a.zip"));              // hâlâ bekleyen/açık pencere var: ikinci pencere yok
        Assert.Equal(1, c.ActiveCount);

        c.Complete("https://x/a.zip", added: false);              // kullanıcı İptal dedi
        Assert.Equal(0, c.ActiveCount);
        Assert.True(c.TryClaim("https://x/a.zip"));               // aynı bağlantıya tekrar tıklama: yeniden sorulur
    }

    [Fact]
    public void PromptClaims_AddedIsRememberedOnlyForShortWindow()
    {
        var now = DateTime.UtcNow;
        var c = new PromptClaims(() => now);
        Assert.True(c.TryClaim("u"));
        c.Complete("u", added: true);
        now += TimeSpan.FromSeconds(1);
        Assert.False(c.TryClaim("u"));                            // tarayıcının çift gönderimi (aynı URL + aynı sayfa, 2 sn içinde)
        Assert.True(c.TryClaim("u", "https://other/"));           // farklı sayfadan gelen aynı URL gerçek yineleme değil
        now += TimeSpan.FromSeconds(2);
        Assert.True(c.TryClaim("u"));                             // pencere doldu
    }

    [Fact]
    public async Task Gate_ClickAfterCancel_AsksAgain_BrowserCopyIsNotLost()
    {
        // Gerçek MainForm.PromptDownloadAsync akışının aynısı: TryClaim -> pencere -> Complete(added).
        var claims = new PromptClaims();
        var windows = new ConcurrentQueue<string>();
        bool userCancels = true;
        Task Prompt(DownloadMessage d)
        {
            if (!claims.TryClaim(d.Url)) return Task.CompletedTask;
            windows.Enqueue(d.Url);                               // pencere açıldı
            bool added = !userCancels;
            claims.Complete(d.Url, added);
            return Task.CompletedTask;
        }
        var gate = new DownloadCaptureGate(_ => true, Prompt);
        var m = new DownloadMessage("r1", "https://x/a.zip", null, null, null, "a.zip", "application/zip", 5);

        Assert.True(gate.Handle(m).Accepted);
        Assert.True(SpinWait.SpinUntil(() => windows.Count == 1, 3000));
        // kullanıcı İptal etti; 15 sn içinde aynı bağlantıya tekrar tıklar
        Assert.True(gate.Handle(m with { RequestId = "r2" }).Accepted);
        Assert.True(SpinWait.SpinUntil(() => windows.Count == 2, 3000), "ikinci tıklamada pencere yeniden açılmalı");
        await Task.Delay(10);
    }

    // ---------------------------------------------------------------- A1: çerez yalnızca alındığı host'a

    private static MediaItem Item(string url, string? cookies = null) => new(url, "", "hls", "https://film.test/embed", null, cookies, "UA");

    [Fact]
    public void ContextOf_BindsCookieToItemsOwnHost_AndCookieNeverReachesPageSuppliedHost()
    {
        var ctx = MediaContexts.ContextOf(Item("https://film.test/master.m3u8", "sid=S"), "https://film.test/watch");
        Assert.Equal("film.test", ctx.CookieHost);
        Assert.True(ctx.CookieAppliesTo("film.test"));
        Assert.True(ctx.CookieAppliesTo("cdn.film.test"));         // alt alan adı: tarayıcı kapsamıyla uyumlu
        Assert.False(ctx.CookieAppliesTo("saldirgan.example"));    // sayfanın players ile verdiği adres

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://saldirgan.example/a.m3u8");
        ctx.Apply(req, "saldirgan.example");                       // eski tip çağrı (cookieHost = isteğin kendi host'u) da çerez EKLEMEZ
        Assert.False(req.Headers.Contains("Cookie"));
        using var ok = new HttpRequestMessage(HttpMethod.Get, "https://film.test/a.m3u8");
        ctx.Apply(ok);
        Assert.True(ok.Headers.Contains("Cookie"));
    }

    [Fact]
    public void ContextOf_NoCookie_NoCookieHost_AndForSourceStripsMismatchedHost()
    {
        Assert.Null(MediaContexts.ContextOf(Item("https://a.test/x.m3u8"), null).CookieHost);
        var ctx = MediaContexts.ContextOf(Item("https://film.test/m.m3u8", "sid=S"), null);
        Assert.Equal("sid=S", MediaContexts.ForSource(ctx, "https://film.test/other.m3u8").Cookies);
        Assert.Null(MediaContexts.ForSource(ctx, "https://saldirgan.example/x.m3u8").Cookies);
    }

    [Fact]
    public void MediaCookies_Apply_RequiresDeclaredHostToMatchItemHost()
    {
        var items = new List<MediaItem> { Item("https://film.test/a.m3u8"), Item("https://baska.test/b.m3u8") };
        var cookies = new Dictionary<string, string> { ["https://film.test"] = "k=v", ["https://baska.test"] = "z=y" };
        // yalnızca film.test için okundu bildirimi: baska.test'e çerez yazılmaz
        var applied = MediaCookies.Apply(items, cookies, new() { ["https://film.test"] = "film.test" });
        Assert.Equal("k=v", applied[0].Cookies);
        Assert.Null(applied[1].Cookies);
        // bildirilen host öğenin host'uyla uyuşmuyorsa uygulanmaz
        Assert.Null(MediaCookies.Apply(items, cookies, new() { ["https://film.test"] = "baska.test" })[0].Cookies);
        // eski eklenti (host bilgisi yok): yalnızca origin eşleşmesi
        Assert.Equal("k=v", MediaCookies.Apply(items, cookies)[0].Cookies);
    }

    [Fact]
    public void QualitiesMessage_CarriesCookieHosts()
    {
        var q = Assert.IsType<QualitiesMessage>(IpcSerializer.Deserialize(
            "{\"type\":\"qualities\",\"requestId\":\"r\",\"tabId\":3,\"frameId\":0,\"cookies\":{\"https://c.test\":\"k=v\"},\"cookieHosts\":{\"https://c.test\":\"c.test\"}}"));
        Assert.Equal("c.test", q.CookieHosts!["https://c.test"]);
    }

    // ---------------------------------------------------------------- A11: günlükte imza kalmaz

    [Fact]
    public void Scrub_RemovesQueryAndSignatureFromUrlsInFreeText()
    {
        var s = MediaContexts.Scrub("ERROR: unable to download https://cdn.x/hls/a.m3u8?sig=SECRET123&token=ABC: HTTP 403\nsonraki satır");
        Assert.DoesNotContain("SECRET123", s);
        Assert.DoesNotContain("sig=", s);
        Assert.Contains("cdn.x/hls/a.m3u8", s);
        Assert.DoesNotContain("\n", s);
    }

    // ---------------------------------------------------------------- A3: Geri Dönüşüm Kutusu

    [Theory]
    [InlineData(0, false, false, RecycleOutcome.Recycled)]
    [InlineData(0, false, true, RecycleOutcome.Failed)]            // başarılı denildi ama dosya duruyor
    [InlineData(0, true, true, RecycleOutcome.Skipped)]            // kullanıcı "kalıcı silinsin mi?" uyarısını reddetti: dosya yerinde
    [InlineData(0x75, false, true, RecycleOutcome.Skipped)]
    [InlineData(0x7C, false, true, RecycleOutcome.Failed)]
    public void RecycleOutcome_NeverTreatsRefusedPermanentDeleteAsDeleted(int code, bool aborted, bool exists, RecycleOutcome expected) =>
        Assert.Equal(expected, DiskDeletePlan.Interpret(code, aborted, exists));

    [Fact]
    public void DiskDeleteConfirmText_PromisesSeparateQuestionForPermanentDelete() =>
        Assert.Contains("ayrıca sorulur", DiskDeletePlan.ConfirmText(2, 1024));

    // ---------------------------------------------------------------- A5: yalnızca kullanıcı eylemi App'i başlatır

    [Fact]
    public void MayLaunchApp_OnlyDownloadAndQualities()
    {
        Assert.True(HostRunner.MayLaunchApp(new DownloadMessage("r", "https://x/a", null, null, null, null, null, 0)));
        Assert.True(HostRunner.MayLaunchApp(new QualitiesMessage("r", 1, 0, null, null, null, null)));
        Assert.False(HostRunner.MayLaunchApp(new PingMessage()));
        Assert.False(HostRunner.MayLaunchApp(new MediaMessage(1, 0, null, null, new())));
        Assert.False(HostRunner.MayLaunchApp(new TabClosedMessage(1)));
        Assert.False(HostRunner.MayLaunchApp(new QualitiesCancelMessage("r")));
        Assert.False(HostRunner.MayLaunchApp(new QualitySelectMessage("s", 0)));
        Assert.False(HostRunner.MayLaunchApp(new VideoGeometryMessage(1, 1, true, false, new(0, 0, 1, 1), 1, new(1, 1), new(0, 0, 1, 1), null, null)));
    }

    // ---------------------------------------------------------------- A7: kabul döngüsü sağ kalır

    [Fact]
    public async Task Server_AcceptsClients_WithCurrentUserOnlyPipe()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        await using var server = new IpcServer(pipe)
        {
            MessageHandler = (m, _) => Task.FromResult<IpcMessage?>(m is PingMessage ? new PongMessage("1.0.0") : null)
        };
        server.Start();
        using var c = new IpcClient(pipe);
        Assert.True(await c.ConnectAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<PongMessage>(await c.RequestAsync(new PingMessage()));
    }
}

public class CaptureCompletionTests
{
    [Fact]
    public async Task Gate_Completed_ReportsFailureWhenPromptThrows_AndOkOtherwise()
    {
        var res = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        var bad = new DownloadCaptureGate(_ => true, _ => throw new InvalidOperationException("x"));
        var good = new DownloadCaptureGate(_ => true, _ => Task.CompletedTask);
        var m = new DownloadMessage("r", "https://x/a.zip", null, null, null, "a.zip", null, 1);
        Assert.True(bad.Handle(m, res.Enqueue).Accepted);
        Assert.True(good.Handle(m, res.Enqueue).Accepted);
        Assert.True(SpinWait.SpinUntil(() => res.Count == 2, 3000));
        Assert.Equal(new[] { false, true }, res.OrderBy(x => x));
        await Task.CompletedTask;
    }

    [Fact]
    public void Gate_Rejected_DoesNotInvokeCompleted()
    {
        bool called = false;
        var g = new DownloadCaptureGate(_ => false, _ => Task.CompletedTask);
        Assert.False(g.Handle(new DownloadMessage("r", "https://x/a", null, null, null, "a", null, 1), _ => called = true).Accepted);
        Assert.False(called);
    }
}
