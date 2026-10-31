using System.Net;
using System.Security.Cryptography;
using TDownloader.Core.Downloads;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

/// <summary>01-core düzeltmeleri (HTTP motoru): C6 abone istisnası, C9 yönlendirmede çerez, C10 çerez ipucu, C11 başlık değerleri, C13 aralık doğrulaması, C14 parça sınırı.</summary>
public sealed class EngineFixTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-efx-" + Guid.NewGuid().ToString("N")[..8]);
    public EngineFixTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private DownloadItem NewItem(string url) => new() { Url = url, FileName = "o.bin", SavePath = Path.Combine(_tmp, "save", "o.bin") };

    private DownloadEngine NewEngine(DownloadItem item, int connections = 4)
    {
        var o = new EngineOptions
        {
            Connections = connections, MinSplitSize = 64 * 1024, MaxRetries = 1, RetryBaseDelay = TimeSpan.FromMilliseconds(20),
            ProgressInterval = TimeSpan.FromMilliseconds(30), StateSaveInterval = TimeSpan.FromMilliseconds(60)
        };
        return new DownloadEngine(item, Path.Combine(_tmp, "DwnlData"), o, null, EngineHttp.Create());
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    [Fact]
    public async Task C6_ThrowingSubscribers_DoNotFailDownload_OrKillProgressLoop()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(768 * 1024));
        srv.Custom = async (c, s, e, probe) => await srv.Serve(c, s, e, chunkDelayMs: 4);
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item);
        int progressCalls = 0, statusCalls = 0;
        engine.StatusChanged += (_, _) => { Interlocked.Increment(ref statusCalls); throw new InvalidOperationException("UI kapandı"); };
        engine.ProgressChanged += (_, _) => { Interlocked.Increment(ref progressCalls); throw new InvalidOperationException("UI kapandı"); };
        var st = await engine.RunAsync();
        Assert.Equal(DownloadStatus.Completed, st);
        Assert.Equal(Sha(srv.Data), Sha(File.ReadAllBytes(item.SavePath)));
        Assert.True(progressCalls > 2, "ilerleme döngüsü ilk istisnadan sonra da çalışmalı: " + progressCalls);
        Assert.True(statusCalls >= 2);
    }

    [Fact]
    public async Task C9_RedirectToOtherHost_DropsCookie_SameHostKeepsIt()
    {
        using var s = new HlsTestServer();
        var data = TestHttpServer.RandomData(200 * 1024);
        s.Bin("/real.bin", data, "application/octet-stream");
        s.Redirects["/start.bin"] = s.AltBase + "/real.bin";            // 127.0.0.1 -> localhost
        var item = NewItem(s.Base + "/start.bin");
        item.Cookie = "sid=GIZLI";
        using var engine = NewEngine(item, 1);
        Assert.Equal(DownloadStatus.Completed, await engine.RunAsync());
        Assert.Equal(Sha(data), Sha(File.ReadAllBytes(item.SavePath)));
        lock (s.Log)
        {
            Assert.Contains(s.Log, e => e.Host == "127.0.0.1" && e.Cookie == "sid=GIZLI");          // özgün host çerezi alır
            var other = s.Log.Where(e => e.Host == "localhost").ToList();
            Assert.NotEmpty(other);
            Assert.All(other, e => Assert.Null(e.Cookie));                                         // yönlendirilen host almaz
        }
    }

    [Fact]
    public async Task C10_Forbidden_WithoutCookie_ExplainsMissingSession()
    {
        using var s = new HlsTestServer();
        s.Forbid = _ => true;
        var item = NewItem(s.Base + "/x.bin");
        using var engine = NewEngine(item, 1);
        Assert.Equal(DownloadStatus.Failed, await engine.RunAsync());
        Assert.Contains("oturum çerezi", item.Error);
        var withCookie = NewItem(s.Base + "/x.bin"); withCookie.Id = Guid.NewGuid().ToString("N"); withCookie.Cookie = "a=b";
        using var e2 = NewEngine(withCookie, 1);
        Assert.Equal(DownloadStatus.Failed, await e2.RunAsync());
        Assert.DoesNotContain("oturum çerezi", withCookie.Error);   // çerez varsa ipucu verilmez
    }

    [Fact]
    public async Task C11_NonAsciiCookieAndUserAgent_StillDownloads_NoRetryStorm()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(300 * 1024));
        var item = NewItem(srv.Url);
        item.Cookie = "ad=şğüıİ; t=1"; item.UserAgent = "Tarayıcı/1.0 çğ";
        using var engine = NewEngine(item);
        Assert.Equal(DownloadStatus.Completed, await engine.RunAsync());
        Assert.Equal(Sha(srv.Data), Sha(File.ReadAllBytes(item.SavePath)));
    }

    [Fact]
    public async Task C11_CrLfInHeader_FailsImmediately_NoRequestSent()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(100 * 1024));
        var item = NewItem(srv.Url);
        item.Cookie = "a=b\r\nX-Evil: 1";
        using var engine = NewEngine(item);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(DownloadStatus.Failed, await engine.RunAsync());
        Assert.Equal(0, srv.Requests);
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.Contains("geçersiz", item.Error);
    }

    [Fact]
    public async Task C13_WrongContentRangeStart_FailsInsteadOfCorruptingFile()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(256 * 1024));
        srv.Custom = async (c, s, e, probe) =>
        {
            if (probe || s == 0) { await srv.Serve(c, s, e); return; }
            // Sunucu/proxy istenen aralık yerine başka aralığın başlığını döner (veri 0'dan başlıyormuş gibi).
            c.Response.StatusCode = 206;
            c.Response.AddHeader("Content-Range", $"bytes 0-{e - s}/{srv.Data.Length}");
            c.Response.ContentLength64 = e - s + 1;
            await c.Response.OutputStream.WriteAsync(srv.Data.AsMemory(0, (int)(e - s + 1)));
            c.Response.Close();
        };
        var item = NewItem(srv.Url);
        using var engine = NewEngine(item, 2);
        Assert.Equal(DownloadStatus.Failed, await engine.RunAsync());
        Assert.False(File.Exists(item.SavePath));
    }

    [Fact]
    public async Task C13_IfRangeSent_AndChangedFileDetected()
    {
        using var srv = new ScriptServer(TestHttpServer.RandomData(256 * 1024));
        var ifRanges = new List<string?>();
        bool changed = false;
        srv.Custom = async (c, s, e, probe) =>
        {
            c.Response.AddHeader("ETag", "\"v1\"");
            if (!probe)
            {
                lock (ifRanges) ifRanges.Add(c.Request.Headers["If-Range"]);
                if (changed) { c.Response.StatusCode = 200; c.Response.ContentLength64 = srv.Data.Length; c.Response.OutputStream.Write(srv.Data); c.Response.Close(); return; }
            }
            await srv.Serve(c, s, e);
        };
        var item = NewItem(srv.Url);
        using var ok = NewEngine(item, 2);
        Assert.Equal(DownloadStatus.Completed, await ok.RunAsync());
        lock (ifRanges) { Assert.NotEmpty(ifRanges); Assert.All(ifRanges, v => Assert.Equal("\"v1\"", v)); }

        changed = true;   // dosya indirme sırasında değişti: sunucu If-Range'i karşılamayıp 200 döner
        var item2 = NewItem(srv.Url); item2.Id = Guid.NewGuid().ToString("N");
        using var bad = NewEngine(item2, 2);
        Assert.Equal(DownloadStatus.Failed, await bad.RunAsync());
        Assert.Contains("değişti", item2.Error);
    }

    [Fact]
    public async Task C14_HugeContentLength_RejectedWithoutAllocating()
    {
        // Ham TCP: HttpListener gövdesiz 3 GB Content-Length başlığı gönderemez.
        var t = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); t.Start();
        int port = ((IPEndPoint)t.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                System.Net.Sockets.TcpClient cl;
                try { cl = await t.AcceptTcpClientAsync(); } catch { break; }
                _ = Task.Run(async () =>
                {
                    using var c = cl;
                    var st = c.GetStream();
                    var buf = new byte[4096]; int n = await st.ReadAsync(buf);
                    string req = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    if (req.Contains(".m3u8"))
                    {
                        var body = System.Text.Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nbig.ts\n#EXT-X-ENDLIST\n");
                        await st.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                        await st.WriteAsync(body);
                    }
                    else await st.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 3221225472\r\nConnection: close\r\n\r\n"));
                    await Task.Delay(300);
                });
            }
        });
        var ex = await Assert.ThrowsAsync<HlsException>(() => new HlsClient().DownloadAsync($"http://127.0.0.1:{port}/a.m3u8", null, null,
            Path.Combine(_tmp, "o", "f"), new HlsClient.Options(null, WorkDir: Path.Combine(_tmp, "w"), Retries: 0), _ => { }, CancellationToken.None));
        Assert.Contains("büyük", ex.Message); t.Stop();
    }
}
