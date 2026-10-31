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
