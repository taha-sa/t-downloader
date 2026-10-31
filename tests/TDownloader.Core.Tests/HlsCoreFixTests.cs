using System.Text;
using TDownloader.Core.Queue;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

/// <summary>01-core incelemesi düzeltmeleri (C1, C2, C3/C4, C7, C14): HLS zaman aşımı, birleştirme devamı, ffmpeg geçici dosya.</summary>
public class HlsCoreFixTests : IDisposable
{
    private readonly HlsTestServer _s = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-hlsfix-" + Guid.NewGuid().ToString("N")[..8]);

    public HlsCoreFixTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { _s.Dispose(); try { Directory.Delete(_tmp, true); } catch { } }

    private byte[][] Publish(string dir, int n, int seedBase = 1)
    {
        var plain = Enumerable.Range(0, n).Select(i => HlsTestServer.Ts(20, seedBase + i)).ToArray();
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < n; i++)
        {
            _s.Bin($"/{dir}/s{i}.ts", plain[i]);
            sb.Append("#EXTINF:4.0,\n").Append($"s{i}.ts\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        _s.Text($"/{dir}/index.m3u8", sb.ToString());
        return plain;
    }

    private string Url(string dir) => $"{_s.Base}/{dir}/index.m3u8";

    // ------------------------------------------------------------ C7

    [Fact]
    public async Task C7_PlaylistTimeout_IsHlsException_NotCancellation()
    {
        Publish("to", 4);
        _s.DelayMs = p => p.EndsWith("index.m3u8") ? 2500 : 0;
        var hls = new HlsClient(playlistTimeout: TimeSpan.FromMilliseconds(300));
        var ex = await Assert.ThrowsAsync<HlsException>(() => hls.DownloadAsync(Url("to"), null, null, Path.Combine(_tmp, "o", "f"),
            new HlsClient.Options(null, WorkDir: Path.Combine(_tmp, "w")), _ => { }, CancellationToken.None));
        Assert.Contains("zaman aşımı", ex.Message);
    }

    [Fact]
    public async Task C7_ExternalCancel_StillPropagatesAsCancellation()
    {
        Publish("ec", 4);
        _s.DelayMs = p => p.EndsWith("index.m3u8") ? 2500 : 0;
        var hls = new HlsClient(playlistTimeout: TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hls.DownloadAsync(Url("ec"), null, null, Path.Combine(_tmp, "o", "f"),
            new HlsClient.Options(null, WorkDir: Path.Combine(_tmp, "w2")), _ => { }, cts.Token));
    }

    // ------------------------------------------------------------ C1

    [Fact]
    public async Task C1_SlowPlaylistDuringRefresh_FailsCleanly_NoMergeWithMissingSegments()
    {
        Publish("rf", 20);
        int playlistHits = 0;
        _s.Forbid = p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^/rf/s(\d+)\.ts$") && int.Parse(p[5..^3]) >= 5;   // 5. parçadan sonrası 403
        _s.DelayMs = p => p.EndsWith("index.m3u8") && Interlocked.Increment(ref playlistHits) > 1 ? 2500 : 0;   // yenileme yavaş
        var hls = new HlsClient(playlistTimeout: TimeSpan.FromMilliseconds(300));
        string work = Path.Combine(_tmp, "w3");
        var ex = await Assert.ThrowsAsync<HlsException>(() => hls.DownloadAsync(Url("rf"), null, null, Path.Combine(_tmp, "o", "f"),
            new HlsClient.Options(null, WorkDir: work, Retries: 0), _ => { }, CancellationToken.None));
        Assert.DoesNotContain("bulunamadı", ex.Message); Assert.True(ex.HttpStatus == 403, ex.Message);
        Assert.Equal(403, ex.HttpStatus);                                 // gerçek neden
        Assert.False(File.Exists(Path.Combine(work, "merged.v")), "eksik parçayla birleştirmeye geçilmemeli");
        Assert.True(Directory.GetFiles(work, "*.seg").Length > 0, "inmiş parçalar korunmalı");
    }
}

/// <summary>A1 (Core) / C8: çerez yalnızca çerezin alındığı host'a gider; yt-dlp'ye geçici dosyayla verilir.</summary>
public class CookieScopeTests : IDisposable
{
    private readonly HlsTestServer _s = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-ck-" + Guid.NewGuid().ToString("N")[..8]);
    public CookieScopeTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { _s.Dispose(); try { Directory.Delete(_tmp, true); } catch { } }

    [Theory]
    [InlineData("film.test", "film.test", true)]
    [InlineData("film.test", "cdn.film.test", true)]
    [InlineData(".film.test", "FILM.test", true)]
    [InlineData("film.test", "evilfilm.test", false)]
    [InlineData("film.test", "film.test.evil.com", false)]
    [InlineData(null, "film.test", false)]
    [InlineData("", "film.test", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    public void CookieAppliesTo_DomainMatch(string? cookieHost, string reqHost, bool expected)
    {
        var ctx = new MediaContext(null, null, "sid=1", null, cookieHost);
        Assert.Equal(expected, ctx.CookieAppliesTo(reqHost));
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://" + reqHost + "/x");
        ctx.Apply(req);
        Assert.Equal(expected, req.Headers.Contains("Cookie"));
    }

    private void PublishWithKey(string dir)
    {
        // Parçalar ve anahtar localhost (başka host) adresinde; playlist 127.0.0.1'de.
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        var key = new byte[16];
        _s.Bin($"/{dir}/key.bin", key, "application/octet-stream");
        sb.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"{_s.AltBase}/{dir}/key.bin\"\n");
        for (int i = 0; i < 3; i++)
        {
            using var aes = System.Security.Cryptography.Aes.Create(); aes.Key = key;
            _s.Bin($"/{dir}/s{i}.ts", aes.EncryptCbc(HlsTestServer.Ts(20, i + 1), HlsClient.SequenceIv(i), System.Security.Cryptography.PaddingMode.PKCS7));
            sb.Append("#EXTINF:4.0,\n").Append($"{_s.AltBase}/{dir}/s{i}.ts\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        _s.Text($"/{dir}/index.m3u8", sb.ToString());
    }

    private async Task Run(string dir, string? cookieHost)
    {
        PublishWithKey(dir);
        var ctx = new MediaContext(null, null, "sid=abc", null, cookieHost);
        await new HlsClient().DownloadAsync($"{_s.Base}/{dir}/index.m3u8", null, ctx, Path.Combine(_tmp, dir, "f"),
            new HlsClient.Options(null, WorkDir: Path.Combine(_tmp, "w-" + dir)), _ => { }, CancellationToken.None);
    }

    [Fact]
    public async Task SegmentAndKeyOnOtherHost_GetNoCookie_SameHostGetsIt()
    {
        await Run("c1", "127.0.0.1");
        lock (_s.Log)
        {
            Assert.All(_s.Log.Where(e => e.Host == "127.0.0.1"), e => Assert.Equal("sid=abc", e.Cookie));          // playlist: aynı host
            var other = _s.Log.Where(e => e.Host == "localhost").ToList();
            Assert.Contains(other, e => e.Path.EndsWith("key.bin"));
            Assert.Contains(other, e => e.Path.EndsWith(".ts"));
            Assert.All(other, e => Assert.Null(e.Cookie));                                                          // anahtar + parça: başka host
        }
    }

    [Fact]
    public async Task EmptyCookieHost_NeverSendsCookie()
    {
        await Run("c2", null);
        lock (_s.Log) Assert.All(_s.Log, e => Assert.Null(e.Cookie));
    }

    [Fact]
    public void CookieJarFile_NetscapeFormat_OnlyWithHost_AndDeletedOnDispose()
    {
        Assert.Null(CookieJarFile.Create(new MediaContext(null, null, "a=1", null, null)));
        Assert.Null(CookieJarFile.Create(new MediaContext(null, null, null, null, "film.test")));
        string path;
        using (var jar = CookieJarFile.Create(new MediaContext(null, null, "a=1; b=x y", null, "film.test"))!)
        {
            path = jar.Path;
            Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), path);
            var lines = File.ReadAllLines(path);
            Assert.Equal("# Netscape HTTP Cookie File", lines[0]);
            Assert.Equal(".film.test\tTRUE\t/\tFALSE\t2147483647\ta\t1", lines[1]);
            Assert.Equal(".film.test\tTRUE\t/\tFALSE\t2147483647\tb\tx y", lines[2]);
        }
        Assert.False(File.Exists(path));
    }
}
