using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TDownloader.Core.Queue;
using TDownloader.Core.Util;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

/// <summary>Yerel HLS sunucusu: yol → (içerik tipi, gövde). Gelen isteklerin başlıklarını kaydeder.</summary>
public sealed class HlsTestServer : IDisposable
{
    private readonly HttpListener _l = new();
    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}";
    public string AltBase => $"http://localhost:{Port}";
    public Dictionary<string, (string Type, byte[] Body)> Routes { get; } = new();
    public List<(string Path, string? Referer, string? Origin, string? Cookie, string Host, string? Range)> Log { get; } = new();
    public Func<string, bool>? Forbid { get; set; }
    public Func<string, bool>? Transient { get; set; }   // 500 döndür
    public Func<string, int>? DelayMs { get; set; }       // yanıttan önce bekle (zaman aşımı testleri)

    public HlsTestServer()
    {
        var t = new TcpListener(IPAddress.Loopback, 0); t.Start();
        Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Prefixes.Add($"http://localhost:{Port}/");
        _l.Start();
        _ = Task.Run(Loop);
    }

    public void Text(string path, string body, string type = "application/vnd.apple.mpegurl") => Routes[path] = (type, Encoding.UTF8.GetBytes(body));
    public void Bin(string path, byte[] body, string type = "video/mp2t") => Routes[path] = (type, body);

    private async Task Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { break; }
            _ = Task.Run(() =>
            {
                var path = c.Request.Url!.AbsolutePath;
                lock (Log) Log.Add((path, c.Request.Headers["Referer"], c.Request.Headers["Origin"], c.Request.Headers["Cookie"], c.Request.Url.Host, c.Request.Headers["Range"]));
                try
                {
                    if (DelayMs?.Invoke(path) is > 0 and var dly) Thread.Sleep(dly);
                    if (Transient?.Invoke(path) == true) { c.Response.StatusCode = 500; c.Response.Close(); return; }
                    if (Forbid?.Invoke(path) == true) { c.Response.StatusCode = 403; c.Response.Close(); return; }
                    if (!Routes.TryGetValue(path, out var r)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
                    var body = r.Body;
                    var range = c.Request.Headers["Range"];
                    long total = body.Length;
                    if (range != null && range.StartsWith("bytes="))
                    {
                        var p = range[6..].Split('-');
                        long a = long.Parse(p[0]), b = Math.Min(long.Parse(p[1]), body.Length - 1);
                        body = body[(int)a..(int)(b + 1)];
                        c.Response.StatusCode = 206;
                        c.Response.AddHeader("Content-Range", $"bytes {a}-{b}/{total}");
                    }
                    c.Response.ContentType = r.Type;
                    c.Response.ContentLength64 = body.Length;
                    if (c.Request.HttpMethod != "HEAD") c.Response.OutputStream.Write(body);
                    c.Response.Close();
                }
                catch { try { c.Response.Abort(); } catch { } }
            });
        }
    }

    public void Dispose() { try { _l.Close(); } catch { } }

    /// <summary>n × 188 baytlık sahte MPEG-TS (her paket 0x47 ile başlar), içerik seed'e bağlı.</summary>
    public static byte[] Ts(int packets, int seed)
    {
        var b = new byte[packets * 188];
        new Random(seed).NextBytes(b);
        for (int i = 0; i < packets; i++) b[i * 188] = 0x47;
        return b;
    }
}

public class HlsParserTests
{
    private const string Master =
        "#EXTM3U\n#EXT-X-VERSION:3\n" +
        "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"TR\",DEFAULT=YES,URI=\"audio/tr.m3u8\"\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",AUDIO=\"aud\"\nv360/index.m3u8\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=2800000,AVERAGE-BANDWIDTH=2500000,RESOLUTION=1280x720,FRAME-RATE=59.94,AUDIO=\"aud\"\nhttps://cdn.test/v720/index.m3u8?t=1\n" +
        "#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\nv1080/index.m3u8\n" +
        "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=90000,URI=\"iframe.m3u8\"\n";

    [Fact]
    public void ParseMaster_ReadsVariantsAndAudio()
    {
        var m = HlsParser.ParseMaster(Master, "https://site.test/a/master.m3u8");
        Assert.Equal(3, m.Variants.Count);
        Assert.Equal("https://site.test/a/v360/index.m3u8", m.Variants[0].Url);
        Assert.Equal(360, m.Variants[0].Height);
        Assert.Equal(2500000, m.Variants[1].Bandwidth);          // AVERAGE-BANDWIDTH tercih edilir
        Assert.Equal("https://cdn.test/v720/index.m3u8?t=1", m.Variants[1].Url);
        Assert.Equal(59.94, m.Variants[1].Fps, 2);
        Assert.Equal("aud", m.Variants[0].AudioGroup);
        Assert.Single(m.Audios);
        Assert.Equal("https://site.test/a/audio/tr.m3u8", m.Audios[0].Uri);
    }

    [Fact]
    public void BuildOptions_OnePerHeight_HighestFirst_WithAudioAndSize()
    {
        var m = HlsParser.ParseMaster(Master, "https://site.test/a/master.m3u8");
        var o = HlsClient.BuildOptions(m, 600);
        Assert.Equal(new[] { 1080, 720, 360 }, o.Select(x => x.Height).ToArray());
        Assert.Contains("720p60", o[1].Label);
        Assert.Contains("MB", o[1].Label);
        var f = HlsClient.ParseFormat(o[1].Format)!.Value;
        Assert.Equal("https://cdn.test/v720/index.m3u8?t=1", f.Variant);
        Assert.Equal("https://site.test/a/audio/tr.m3u8", f.Audio);
        Assert.Equal(720, f.Height);
        Assert.Null(HlsClient.ParseFormat(o[0].Format)!.Value.Audio);   // 1080p grubu sesi kendi içinde
        Assert.Null(HlsClient.ParseFormat("bv*+ba/b"));
    }

    [Fact]
    public void ParseMedia_SegmentsKeysMapByteRanges()
    {
        const string t =
            "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-MAP:URI=\"init.mp4\"\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"k.bin\",IV=0x000000000000000000000000000000FF\n" +
            "#EXTINF:4.5,\nseg1.m4s\n#EXT-X-BYTERANGE:100@50\n#EXTINF:5,\nbig.m4s\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:3,\nseg3.m4s\n#EXT-X-ENDLIST\n";
        var m = HlsParser.ParseMedia(t, "https://x.test/p/index.m3u8");
        Assert.True(m.EndList);
        Assert.Equal(3, m.Segments.Count);
        Assert.Equal(7, m.Segments[0].Sequence);
        Assert.Equal(12.5, m.TotalDuration, 3);
        Assert.Equal("https://x.test/p/init.mp4", m.Map!.Url);
        Assert.Equal("AES-128", m.Segments[0].Key!.Method);
        Assert.Equal(0xFF, m.Segments[0].Key!.Iv![15]);
        Assert.Equal(50, m.Segments[1].ByteOffset); Assert.Equal(100, m.Segments[1].ByteLength);
        Assert.Null(m.Segments[2].Key);
        Assert.Null(HlsParser.DrmReason(m));
    }

    [Theory]
    [InlineData("#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\",KEYFORMAT=\"com.apple.streamingkeydelivery\"")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"data:text/plain;base64,AAAA\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"")]
    public void DrmReason_FlagsProtectedStreams(string keyLine)
    {
        var m = HlsParser.ParseMedia("#EXTM3U\n" + keyLine + "\n#EXTINF:4,\ns.ts\n#EXT-X-ENDLIST\n", "https://x.test/i.m3u8");
        Assert.NotNull(HlsParser.DrmReason(m));
    }

    [Fact]
    public void StripFakeHeader_RemovesImagePrefix()
    {
        var ts = HlsTestServer.Ts(10, 1);
        var fake = Encoding.ASCII.GetBytes("\u0089PNG\r\n\u001a\n" + new string('x', 70)).Concat(ts).ToArray();
        Assert.Equal(ts, HlsClient.StripFakeHeader(fake));
        Assert.Same(ts, HlsClient.StripFakeHeader(ts));
    }

    [Theory]
    [InlineData("attachment; filename=\"a b.zip\"", "a b.zip")]
    [InlineData("attachment; filename=plain.pdf", "plain.pdf")]
    [InlineData("attachment; filename=\"x.zip\"; filename*=UTF-8''%C5%9Eark%C4%B1%20Klip.mp4", "Şarkı Klip.mp4")]
    [InlineData("attachment; filename=\"..\\..\\evil.exe\"", "evil.exe")]
    [InlineData("inline", null)]
    public void ContentDisposition_Parses(string header, string? expected) =>
        Assert.Equal(expected, ContentDispositionParser.Parse(header));
}

public class HlsClientTests : IDisposable
{
    private readonly HlsTestServer _s = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-hls-" + Guid.NewGuid().ToString("N")[..8]);
    public HlsClientTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { _s.Dispose(); try { Directory.Delete(_tmp, true); } catch { } }

    private static readonly MediaContext Ctx = new("https://player.film.test/embed/42", null, "sid=abc", "TestUA/9", "127.0.0.1");

    private void Variant(string path, byte[][] segs, string? keyLine = null, string? absHost = null)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        if (keyLine != null) sb.Append(keyLine).Append('\n');
        for (int i = 0; i < segs.Length; i++)
        {
            string seg = $"{path.Replace("/index.m3u8", "")}/s{i}.ts";
            _s.Bin(seg, segs[i]);
            sb.Append("#EXTINF:4.0,\n").Append(i == 1 && absHost != null ? absHost + seg : "s" + i + ".ts").Append('\n');
        }
        sb.Append("#EXT-X-ENDLIST\n");
        _s.Text(path, sb.ToString());
    }

    [Fact]
    public async Task GetInfo_ReadsQualitiesFast_AndSendsHeaders()
    {
        _s.Text("/m/master.m3u8",
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360\n360/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720\n720/index.m3u8\n");
        Variant("/m/360/index.m3u8", new[] { HlsTestServer.Ts(5, 1) });
        Variant("/m/720/index.m3u8", new[] { HlsTestServer.Ts(5, 2), HlsTestServer.Ts(5, 3) });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var info = await new HlsClient().GetInfoAsync(_s.Base + "/m/master.m3u8", Ctx, "Film Adı");
        Assert.True(sw.ElapsedMilliseconds < 3000);
        Assert.Equal("Film Adı", info.Title);
        Assert.Equal(new[] { 720, 360 }, info.Options.Select(o => o.Height).ToArray());
        Assert.All(info.Options, o => Assert.True(HlsClient.IsHlsFormat(o.Format)));
        lock (_s.Log)
        {
            var first = _s.Log[0];
            Assert.Equal("https://player.film.test/embed/42", first.Referer);
            Assert.Equal("https://player.film.test", first.Origin);     // Referer'dan türetilir
            Assert.Equal("sid=abc", first.Cookie);
        }
    }

    [Fact]
    public async Task GetInfo_NotHls_Throws()
    {
        _s.Text("/x.html", "<html>merhaba</html>", "text/html");
        await Assert.ThrowsAsync<HlsException>(() => new HlsClient().GetInfoAsync(_s.Base + "/x.html", Ctx, null));
    }

    [Fact]
    public async Task GetInfo_MediaPlaylist_SingleOption_LiveRejected_DrmRejected()
    {
        Variant("/one/index.m3u8", new[] { HlsTestServer.Ts(3, 1), HlsTestServer.Ts(3, 2) });
        var info = await new HlsClient().GetInfoAsync(_s.Base + "/one/index.m3u8", Ctx, null);
        Assert.Single(info.Options);
        _s.Text("/live/index.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n");
        var live = await Assert.ThrowsAsync<HlsException>(() => new HlsClient().GetInfoAsync(_s.Base + "/live/index.m3u8", Ctx, null));
        Assert.Contains("Canlı", live.Message);
        _s.Text("/drm/index.m3u8", "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n");
        var drm = await Assert.ThrowsAsync<HlsException>(() => new HlsClient().GetInfoAsync(_s.Base + "/drm/index.m3u8", Ctx, null));
        Assert.True(drm.IsDrm);
    }

    [Fact]
    public async Task Download_ConcatenatesSegmentsInOrder_WithHeaders_CookieOnlyToSameHost()
    {
        var segs = Enumerable.Range(0, 7).Select(i => HlsTestServer.Ts(40 + i, i)).ToArray();
        Variant("/v/index.m3u8", segs, absHost: _s.AltBase);   // 2. parça başka host'ta (localhost)
        var prog = new List<ExternalProgress>();
        var path = await new HlsClient().DownloadAsync(_s.Base + "/v/index.m3u8", null, Ctx, Path.Combine(_tmp, "film"),
            new HlsClient.Options(null, Parallel: 3), p => { lock (prog) prog.Add(p); }, CancellationToken.None);
        Assert.EndsWith(".ts", path);
        Assert.Equal(segs.SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(_tmp, "film.hlstmp")));
        lock (prog) Assert.True(prog.Count >= 7 && prog[^1].Downloaded == segs.Sum(x => x.Length));
        lock (_s.Log)
        {
            Assert.All(_s.Log, e => Assert.Equal("https://player.film.test/embed/42", e.Referer));
            Assert.All(_s.Log, e => Assert.Equal("https://player.film.test", e.Origin));
            Assert.All(_s.Log.Where(e => e.Host == "127.0.0.1"), e => Assert.Equal("sid=abc", e.Cookie));
            var other = _s.Log.Where(e => e.Host == "localhost").ToList();
            Assert.Single(other);
            Assert.Null(other[0].Cookie);                          // çerez başka sunucuya sızmaz
        }
    }

    [Fact]
    public async Task Download_Aes128_DecryptsWithKeyAndSequenceIv()
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var plain = Enumerable.Range(0, 4).Select(i => HlsTestServer.Ts(30, 100 + i)).ToArray();
        var enc = plain.Select((p, i) => EncryptWith(key, p, HlsClient.SequenceIv(i))).ToArray();
        _s.Bin("/enc/key.bin", key, "application/octet-stream");
        Variant("/enc/index.m3u8", enc, "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"");
        var path = await new HlsClient().DownloadAsync(_s.Base + "/enc/index.m3u8", null, Ctx, Path.Combine(_tmp, "enc"),
            new HlsClient.Options(null), _ => { }, CancellationToken.None);
        Assert.Equal(plain.SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
    }

    private static byte[] EncryptWith(byte[] key, byte[] data, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
    }

    [Fact]
    public async Task Download_RetriesTransientFailure_ButNotForbidden()
    {
        var segs = new[] { HlsTestServer.Ts(20, 1), HlsTestServer.Ts(20, 2) };
        Variant("/r/index.m3u8", segs);
        int hits = 0;
        _s.Transient = p => p == "/r/s1.ts" && Interlocked.Increment(ref hits) <= 1;   // ilk istek 500
        var ok = await new HlsClient().DownloadAsync(_s.Base + "/r/index.m3u8", null, Ctx, Path.Combine(_tmp, "r"),
            new HlsClient.Options(null, Retries: 2, RetryDelay: TimeSpan.FromMilliseconds(10)), _ => { }, CancellationToken.None);
        Assert.Equal(segs.SelectMany(x => x).ToArray(), File.ReadAllBytes(ok));
        Assert.True(hits >= 2);
        _s.Transient = null;

        _s.Forbid = p => p == "/r/s1.ts";
        var ex = await Assert.ThrowsAsync<HlsException>(() => new HlsClient().DownloadAsync(_s.Base + "/r/index.m3u8", null, Ctx, Path.Combine(_tmp, "r2"),
            new HlsClient.Options(null, Retries: 2, RetryDelay: TimeSpan.FromMilliseconds(10)), _ => { }, CancellationToken.None));
        Assert.Contains("403", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_tmp, "r2.hlstmp")));
    }

    [Fact]
    public async Task Download_Cancel_CleansTempAndThrows()
    {
        var segs = Enumerable.Range(0, 5).Select(i => HlsTestServer.Ts(20, i)).ToArray();
        Variant("/c/index.m3u8", segs);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HlsClient().DownloadAsync(_s.Base + "/c/index.m3u8", null, Ctx,
            Path.Combine(_tmp, "c"), new HlsClient.Options(null), _ => { }, cts.Token));
        Assert.False(Directory.Exists(Path.Combine(_tmp, "c.hlstmp")));
    }

    [Fact]
    public async Task VideoRunner_HlsFormat_UsesNativeDownloaderWithoutYtDlp()
    {
        var segs = Enumerable.Range(0, 3).Select(i => HlsTestServer.Ts(25, 50 + i)).ToArray();
        Variant("/vr/index.m3u8", segs);
        var paths = new TDownloader.Core.Settings.AppPaths(_tmp);
        var yt = new YtDlpClient(() => new TDownloader.Core.Settings.AppSettings { YtDlpPath = Path.Combine(_tmp, "yok.exe") }, paths, _tmp);
        var runner = new VideoRunner(yt);
        var item = new TDownloader.Core.Downloads.DownloadItem
        {
            Url = _s.Base + "/vr/index.m3u8", SavePath = Path.Combine(_tmp, "out", "Film.mp4"), Referrer = Ctx.Referer, Cookie = Ctx.Cookies, UserAgent = Ctx.UserAgent,
            Kind = TDownloader.Core.Downloads.DownloadKind.External,
            ExternalFormat = HlsClient.MakeFormat(_s.Base + "/vr/index.m3u8", null, 720),
        };
        var res = await runner.RunAsync(item, _ => { }, CancellationToken.None);
        Assert.True(res.Success, res.Error);
        Assert.Equal(segs.SelectMany(x => x).ToArray(), File.ReadAllBytes(res.FinalPath!));

        // 403 -> doğrudan indirme başarısız; (makinede yt-dlp varsa o da aynı 403'ü alır) -> sonuç başarısız
        _s.Forbid = p => p.EndsWith(".ts");
        item.SavePath = Path.Combine(_tmp, "out2", "Film2.mp4");
        var bad = await runner.RunAsync(item, _ => { }, CancellationToken.None);
        Assert.False(bad.Success);
    }
}

public class YtDlpContextArgsTests
{
    [Fact]
    public void DownloadArgs_CarryRefererOriginCookieUserAgent()
    {
        var ctx = new MediaContext("https://player.film.test/embed/1", null, "a=1; b=2", "UA/1");
        var a = YtDlpClient.BuildDownloadArgs("https://cdn.test/m.m3u8", "best", @"C:\o\a.%(ext)s", null, null, ctx);
        Assert.Equal("https://player.film.test/embed/1", a[a.IndexOf("--referer") + 1]);
        Assert.Contains("Origin:https://player.film.test", a);
        Assert.DoesNotContain(a, x => x.Contains("a=1"));              // çerez komut satırında YOK (C8)
        Assert.DoesNotContain("--add-header", a.Where(x => x.StartsWith("Cookie", StringComparison.OrdinalIgnoreCase)));
        var withJar = YtDlpClient.BuildDownloadArgs("https://cdn.test/m.m3u8", "best", "o", null, null, ctx, false, @"C:	\ck.txt");
        Assert.Equal(@"C:	\ck.txt", withJar[withJar.IndexOf("--cookies") + 1]);
        Assert.Equal("UA/1", a[a.IndexOf("--user-agent") + 1]);
        Assert.Contains("--concurrent-fragments", a); Assert.Contains("--retries", a);
        Assert.Equal("--", a[^2]); Assert.Equal("https://cdn.test/m.m3u8", a[^1]);
    }

    [Fact]
    public void InfoArgs_ReferrerOnly_StillAddsOrigin_AndNoContextIsClean()
    {
        var a = YtDlpClient.BuildInfoArgs("https://x.test/p", "https://x.test/page");
        Assert.Contains("--referer", a); Assert.Contains("Origin:https://x.test", a);
        var b = YtDlpClient.BuildInfoArgs("https://x.test/p", null);
        Assert.DoesNotContain("--referer", b); Assert.DoesNotContain("--add-header", b);
    }

    [Fact]
    public void MediaItem_LegacyJsonWithoutHeaders_StillDeserializes_AndNewFieldsRoundTrip()
    {
        var legacy = IpcSerializerRoundTrip("{\"type\":\"media\",\"tabId\":1,\"frameId\":2,\"pageUrl\":\"p\",\"pageTitle\":\"t\",\"items\":[{\"url\":\"https://m/v.m3u8\",\"mime\":\"\",\"kind\":\"hls\"}]}");
        Assert.Null(legacy.Items[0].Referer); Assert.Null(legacy.FrameUrl);
        var json = TDownloader.Core.Ipc.IpcSerializer.Serialize(new TDownloader.Core.Ipc.MediaMessage(1, 2, "p", "t",
            new() { new("https://m/v.m3u8", "", "hls", "https://pl/e/1", "https://pl", "a=b", "UA") }, "https://pl/e/1"));
        var back = IpcSerializerRoundTrip(json);
        Assert.Equal("https://pl/e/1", back.Items[0].Referer);
        Assert.Equal("a=b", back.Items[0].Cookies);
        Assert.Equal("https://pl/e/1", back.FrameUrl);
    }

    private static TDownloader.Core.Ipc.MediaMessage IpcSerializerRoundTrip(string json) =>
        (TDownloader.Core.Ipc.MediaMessage)TDownloader.Core.Ipc.IpcSerializer.Deserialize(json);
}
