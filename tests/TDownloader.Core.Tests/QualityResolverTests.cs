using System.Text;
using TDownloader.Core.Ipc;
using TDownloader.Core.Util;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

public class HlsUrlHeightTests
{
    [Theory]
    [InlineData("https://c.test/v/720/index.m3u8", 720)]
    [InlineData("https://c.test/v/1080p.m3u8", 1080)]
    [InlineData("https://c.test/hls/video_480p_0.m3u8?token=1080", 480)]      // sorgu sayılmaz
    [InlineData("https://c.test/x/1280x720/i.m3u8", 720)]
    [InlineData("https://c.test/1080/sub/360/index.m3u8", 360)]               // yolun sonundaki kazanır
    [InlineData("https://c.test/seg/12345.m3u8", 0)]
    [InlineData("https://c.test/a1080b/index.m3u8", 0)]                       // sözcük içinde sayı
    [InlineData("https://720.cdn.test/a/index.m3u8", 0)]                      // host adı sayılmaz
    [InlineData("", 0)]
    public void HeightFromUrl(string url, int expected) => Assert.Equal(expected, HlsParser.HeightFromUrl(url));

    [Theory]
    [InlineData("1080p", 1080)]
    [InlineData("720P HD", 720)]
    [InlineData("1920x1080", 1080)]
    [InlineData("4K", 2160)]
    [InlineData("480", 480)]
    [InlineData("Auto", 0)]
    [InlineData(null, 0)]
    public void HeightFromLabel(string? label, int expected) => Assert.Equal(expected, HlsParser.HeightFromLabel(label));

    [Fact]
    public void ResolutionHeight_FromPlaylistText() =>
        Assert.Equal(1080, HlsParser.ResolutionHeight("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1,RESOLUTION=1920x1080\nx.m3u8"));

    [Fact]
    public void MinorFile_SmallOrTinyComparedToMain()
    {
        Assert.True(Services_IsMinor(500_000, 0));
        Assert.True(Services_IsMinor(30_000_000, 900_000_000));      // ana videonun %5'inden küçük
        Assert.False(Services_IsMinor(0, 900_000_000));              // boyut bilinmiyor: gizleme
        Assert.False(Services_IsMinor(300_000_000, 900_000_000));
    }
    // App projesi test projesinden referanslanmaz; kural aynı formülle burada doğrulanır (App'teki IsMinorFile ile birebir).
    private static bool Services_IsMinor(long size, long main) =>
        size > 0 && (size < 2L * 1024 * 1024 || (main > 0 && size < main * 0.05));
}

public class QualityResolverTests : IDisposable
{
    private readonly HlsTestServer _s = new();
    public void Dispose() => _s.Dispose();

    private static readonly MediaContext Ctx = new("https://player.film.test/embed/1", null, null, "TestUA");

    /// <summary>n parçalı VOD media playlist; segByte parça başına bayt.</summary>
    private void Media(string path, int segs, int segPackets = 5, string ext = ".ts", double dur = 4.0)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        string dir = path[..path.LastIndexOf('/')];
        for (int i = 0; i < segs; i++)
        {
            _s.Bin($"{dir}/s{i}{ext}", HlsTestServer.Ts(segPackets, i + 1));
            sb.Append($"#EXTINF:{dur.ToString(System.Globalization.CultureInfo.InvariantCulture)},\ns{i}{ext}\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        _s.Text(path, sb.ToString());
    }

    private Task<VideoInfo> Resolve(IEnumerable<HlsCandidate> c, bool probe, List<string>? log = null) =>
        new HlsClient().GetInfoSetAsync(c.ToList(), Ctx, "Dizi 1", probe, log == null ? null : log.Add);

    private HlsCandidate C(string path, string? label = null, int h = 0, bool player = false) => new(_s.Base + path, label, h, player);
    private int Hits(Func<string, bool> f) { lock (_s.Log) return _s.Log.Count(e => f(e.Path)); }

    [Fact]
    public async Task MultipleMediaPlaylists_BecomeSeparateQualities_FromUrlPattern()
    {
        foreach (var h in new[] { 1080, 720, 480 }) Media($"/s/{h}/index.m3u8", 10);
        var info = await Resolve(new[] { C("/s/480/index.m3u8"), C("/s/1080/index.m3u8"), C("/s/720/index.m3u8") }, probe: false);
        Assert.Equal(new[] { 1080, 720, 480 }, info.Options.Select(o => o.Height).ToArray());
        Assert.StartsWith("1080p hls", info.Options[0].Label);
        Assert.All(info.Options, o => Assert.True(HlsClient.IsHlsFormat(o.Format)));
        Assert.Equal(_s.Base + "/s/1080/index.m3u8", HlsClient.ParseFormat(info.Options[0].Format)!.Value.Variant);
    }

    [Fact]
    public async Task UnlabeledPlaylists_OrderedByBitrate_AsKaliteN()
    {
        Media("/u/aa/index.m3u8", 6, segPackets: 10);     // en küçük
        Media("/u/bb/index.m3u8", 6, segPackets: 80);     // en büyük
        Media("/u/cc/index.m3u8", 6, segPackets: 30);
        var info = await Resolve(new[] { C("/u/aa/index.m3u8"), C("/u/bb/index.m3u8"), C("/u/cc/index.m3u8") }, probe: false);
        Assert.Equal(3, info.Options.Count);
        Assert.StartsWith("Kalite 1", info.Options[0].Label);
        Assert.EndsWith("/u/bb/index.m3u8", HlsClient.ParseFormat(info.Options[0].Format)!.Value.Variant);
        Assert.EndsWith("/u/cc/index.m3u8", HlsClient.ParseFormat(info.Options[1].Format)!.Value.Variant);
        Assert.EndsWith("/u/aa/index.m3u8", HlsClient.ParseFormat(info.Options[2].Format)!.Value.Variant);
        Assert.StartsWith("Kalite 3", info.Options[2].Label);
        Assert.All(info.Options, o => Assert.True(o.Size > 0));      // ilk parça boyutundan tahmin
    }

    [Fact]
    public async Task SinglePlaylist_NoInfo_IsDefaultQuality_NotTekKalite()
    {
        Media("/one/index.m3u8", 4);
        var info = await Resolve(new[] { C("/one/index.m3u8") }, probe: false);
        Assert.Single(info.Options);
        Assert.StartsWith("Varsayılan kalite", info.Options[0].Label);
        Assert.DoesNotContain("Tek kalite", info.Options[0].Label);
    }

    [Fact]
    public async Task ShortPlaylists_GoToOther_SubtitleAndLivePlaylistsSkipped()
    {
        Media("/m/720/index.m3u8", 20);
        Media("/m/480/index.m3u8", 20);
        Media("/ad/index.m3u8", 1);                                       // reklam: çok kısa
        Media("/sub/index.m3u8", 20, ext: ".vtt");                        // altyazı
        _s.Text("/live/index.m3u8", "#EXTM3U\n#EXTINF:4,\na.ts\n");      // canlı
        var info = await Resolve(new[] { C("/m/720/index.m3u8"), C("/m/480/index.m3u8"), C("/ad/index.m3u8"), C("/sub/index.m3u8"), C("/live/index.m3u8") }, probe: false);
        Assert.Equal(3, info.Options.Count);
        Assert.Equal(new[] { false, false, true }, info.Options.Select(o => o.Other).ToArray());
        Assert.StartsWith("Diğer akış", info.Options[2].Label);
    }

    [Fact]
    public async Task MasterProbe_OnlyWhenAllowed_FindsMasterInSameDir_AtMostFourRequests()
    {
        Media("/p/720/index.m3u8", 10);
        _s.Text("/p/master.m3u8",
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360\n360/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720\n720/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080\n1080/index.m3u8\n");
        Media("/p/1080/index.m3u8", 10);

        // izin yok: master'a hiç istek gitmez, tek kalite
        var none = await Resolve(new[] { C("/p/720/index.m3u8") }, probe: false);
        Assert.Single(none.Options);
        Assert.Equal(0, Hits(p => p.EndsWith("master.m3u8") || p.EndsWith("playlist.m3u8")));

        // izinli (tıklama): master bulunur, 3 kalite
        var log = new List<string>();
        var info = await Resolve(new[] { C("/p/720/index.m3u8") }, probe: true, log);
        Assert.Equal(new[] { 1080, 720, 360 }, info.Options.Select(o => o.Height).ToArray());
        Assert.Contains(log, l => l.StartsWith("probe-master"));
        Assert.InRange(Hits(p => p is "/p/master.m3u8" or "/p/playlist.m3u8" or "/p/index.m3u8" or "/master.m3u8"), 1, HlsQualityResolver.MaxMasterProbes);
    }

    [Fact]
    public async Task MasterProbe_NotFound_FallsBackSafely_BoundedRequests()
    {
        Media("/q/a/index.m3u8", 6);
        var log = new List<string>();
        var info = await Resolve(new[] { C("/q/a/index.m3u8") }, probe: true, log);
        Assert.Single(info.Options);
        int probes = log.Count(l => l.StartsWith("probe-master"));
        Assert.InRange(probes, 1, HlsQualityResolver.MaxMasterProbes);
        Assert.DoesNotContain(log, l => l.Contains("-> master") && !l.Contains("master-degil"));
    }

    [Fact]
    public async Task PlayerHints_LabelPlaylists_AndUnverifiedAreMarked()
    {
        Media("/z/a/index.m3u8", 8);
        Media("/z/b/index.m3u8", 8);
        _s.Forbid = p => p.StartsWith("/z/c/");
        var info = await Resolve(new[]
        {
            C("/z/a/index.m3u8", "1080p", 1080, true), C("/z/b/index.m3u8", "720p", 720, true), C("/z/c/index.m3u8", "480p", 480, true),
        }, probe: false);
        Assert.Equal(new[] { 1080, 720, 480 }, info.Options.Select(o => o.Height).ToArray());
        Assert.Contains("doğrulanamadı", info.Options[2].Label);
    }

    [Fact]
    public async Task CapturedMaster_WinsOverMediaPlaylists()
    {
        Media("/c/360/index.m3u8", 6); Media("/c/720/index.m3u8", 6);
        _s.Text("/c/master.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360\n360/index.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720\n720/index.m3u8\n");
        var info = await Resolve(new[] { C("/c/360/index.m3u8"), C("/c/master.m3u8") }, probe: true);
        Assert.Equal(new[] { 720, 360 }, info.Options.Select(o => o.Height).ToArray());
    }

    [Fact]
    public async Task NothingUsable_ThrowsHlsException_DrmFlagged()
    {
        _s.Text("/drm/i.m3u8", "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://k\"\n#EXTINF:4,\na.ts\n#EXT-X-ENDLIST\n");
        var drm = await Assert.ThrowsAsync<HlsException>(() => Resolve(new[] { C("/drm/i.m3u8") }, probe: false));
        Assert.True(drm.IsDrm);
        await Assert.ThrowsAsync<HlsException>(() => Resolve(new[] { C("/yok/i.m3u8") }, probe: false));
        await Assert.ThrowsAsync<HlsException>(() => Resolve(Array.Empty<HlsCandidate>(), probe: false));
    }
}

public class MediaLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tdl-log-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Sanitize_KeepsHostAndPathOnly_MasksTokens()
    {
        Assert.Equal("cdn.film.test/hls/720/index.m3u8",
            MediaLog.Sanitize("https://user:pw@cdn.film.test/hls/720/index.m3u8?token=SECRET&sig=abc#frag"));
        Assert.Equal("cdn.test/v/*/index.m3u8", MediaLog.Sanitize("https://cdn.test/v/aB3dEf7hIj1kLm5nOp9qRs2tUv4wXy6Z0123/index.m3u8"));
        Assert.Equal("cdn.test/a/b.mp4", MediaLog.Sanitize("https://cdn.test/a/b.mp4;jsessionid=XYZ?x=1"));
        Assert.Equal("blob:", MediaLog.Sanitize("blob:https://a/b"));
        Assert.DoesNotContain("SECRET", MediaLog.Sanitize("https://cdn.test/x.m3u8?token=SECRET"));
    }

    [Fact]
    public void Write_AppendsAndRolls_UnderLimit()
    {
        var path = Path.Combine(_dir, "logs", "media.log");
        var log = new MediaLog(path, 4096);
        for (int i = 0; i < 300; i++) log.Write($"CLICK tab=1 item {i} cdn.test/some/path/segment-{i}.m3u8");
        Assert.True(new FileInfo(path).Length <= 4096);
        var lines = File.ReadAllLines(path);
        Assert.Contains("item 299", lines[^1]);                       // en yeni satır korunur
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ CLICK", lines[0]);
    }

    [Fact]
    public void QualitiesMessage_CarriesPlayers_AndMediaItemSize()
    {
        var q = new QualitiesMessage("r", 1, 2, "https://p/", "T", "https://f/", "", new List<PlayerSource> { new("https://c/1.m3u8", "1080p", 1080) });
        var back = (QualitiesMessage)IpcSerializer.Deserialize(IpcSerializer.Serialize(q))!;
        Assert.Equal(1080, back.Players![0].Height);
        var old = (QualitiesMessage)IpcSerializer.Deserialize("{\"type\":\"qualities\",\"requestId\":\"r\",\"tabId\":1,\"frameId\":0,\"pageUrl\":\"\",\"pageTitle\":\"\",\"frameUrl\":\"\",\"src\":\"\"}")!;
        Assert.Null(old.Players);                                       // eski eklenti: alan yok
        var m = (MediaMessage)IpcSerializer.Deserialize("{\"type\":\"media\",\"tabId\":1,\"frameId\":0,\"items\":[{\"url\":\"https://a/b.mp4\",\"mime\":\"video/mp4\",\"kind\":\"file\",\"size\":734003200},{\"url\":\"https://a/c.mp4\",\"mime\":\"\",\"kind\":\"file\"}]}")!;
        Assert.Equal(734003200, m.Items[0].Size);
        Assert.Equal(-1, m.Items[1].Size);
    }
}
