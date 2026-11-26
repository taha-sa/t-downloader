using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

public class YtDlpParsingTests
{
    // Sahte yt-dlp -J çıktısı (kısaltılmış, uydurma veri).
    private const string SampleJson = """
    { "id":"x1", "title":"Örnek Video",
      "formats":[
        {"format_id":"sb0","ext":"mhtml","vcodec":"none","acodec":"none","height":null},
        {"format_id":"140","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","tbr":129,"filesize":3145728},
        {"format_id":"251","ext":"webm","vcodec":"none","acodec":"opus","tbr":135,"filesize":3300000},
        {"format_id":"137","ext":"mp4","vcodec":"avc1.640028","acodec":"none","height":1080,"fps":30,"tbr":4000,"filesize":52428800},
        {"format_id":"248","ext":"webm","vcodec":"vp9","acodec":"none","height":1080,"fps":30,"tbr":2500,"filesize":30000000},
        {"format_id":"136","ext":"mp4","vcodec":"avc1.4d401f","acodec":"none","height":720,"fps":60,"tbr":2000,"filesize":20971520},
        {"format_id":"18","ext":"mp4","vcodec":"avc1.42001E","acodec":"mp4a.40.2","height":360,"fps":30,"tbr":500,"filesize_approx":10485760}
      ] }
    """;

    [Fact]
    public void ParseInfo_SimplifiesFormats()
    {
        var info = YtDlpParsing.ParseInfo(SampleJson);
        Assert.Equal("Örnek Video", info.Title);
        Assert.Equal(4, info.Options.Count);
        var o = info.Options;
        Assert.StartsWith("1080p mp4", o[0].Label);
        Assert.Equal("137+bestaudio[ext=m4a]/137+bestaudio", o[0].Format);   // mp4 video-only tercih edildi (webm değil)
        Assert.StartsWith("720p60 mp4", o[1].Label);
        Assert.StartsWith("360p mp4", o[2].Label);
        Assert.Equal("18", o[2].Format);                                      // birleşik format, ses eklenmeden
        Assert.Equal("Yalnızca ses (m4a) (3 MB)", o[3].Label);
        Assert.True(o[3].AudioOnly);
        Assert.Equal("m4a", o[3].Ext);
        Assert.Contains("MB", o[0].Label);
    }

    [Fact]
    public void ParseInfo_LimitsTo25_AndFallsBack()
    {
        var fmts = string.Join(",", Enumerable.Range(1, 40).Select(h => $"{{\"format_id\":\"f{h}\",\"ext\":\"mp4\",\"vcodec\":\"avc\",\"acodec\":\"aac\",\"height\":{h * 10}}}"));
        var info = YtDlpParsing.ParseInfo("{\"title\":\"t\",\"formats\":[" + fmts + "]}");
        Assert.Equal(25, info.Options.Count);
        Assert.Equal(400, info.Options[0].Height);

        var empty = YtDlpParsing.ParseInfo("{\"title\":\"t\",\"formats\":[]}");
        Assert.Single(empty.Options);
        Assert.Equal("best", empty.Options[0].Format);

        var playlist = YtDlpParsing.ParseInfo("{\"_type\":\"playlist\",\"entries\":[{\"title\":\"ilk\",\"formats\":[{\"format_id\":\"a\",\"ext\":\"mp4\",\"vcodec\":\"h\",\"acodec\":\"a\",\"height\":480}]}]}");
        Assert.Equal("ilk", playlist.Title);
        Assert.StartsWith("480p", playlist.Options[0].Label);
    }

    [Fact]
    public void PickDefault_RespectsSetting()
    {
        var o = YtDlpParsing.ParseInfo(SampleJson).Options;
        Assert.Equal(0, YtDlpParsing.PickDefault(o, "best"));
        Assert.Equal(1, YtDlpParsing.PickDefault(o, "720"));
        Assert.Equal(2, YtDlpParsing.PickDefault(o, "480"));
        Assert.Equal(2, YtDlpParsing.PickDefault(o, "240")); // hiçbiri uymazsa en düşük video
    }

    [Fact]
    public void ParseProgressLine_ParsesFields()
    {
        var p = YtDlpParsing.ParseProgressLine("TDL_PROG:downloading|1048576|10485760|NA|2097152.5|4");
        Assert.NotNull(p);
        Assert.Equal("downloading", p!.Status);
        Assert.Equal(1048576, p.Downloaded);
        Assert.Equal(10485760, p.Total);
        Assert.Equal(2097152.5, p.Speed);
        Assert.Equal(4, p.EtaSeconds);

        var est = YtDlpParsing.ParseProgressLine("TDL_PROG:downloading|500|NA|2000|NA|NA");
        Assert.Equal(2000, est!.Total);
        Assert.Equal(0, est.Speed);
        Assert.Null(est.EtaSeconds);

        Assert.Null(YtDlpParsing.ParseProgressLine("[download] Destination: x.mp4"));
        Assert.Null(YtDlpParsing.ParseProgressLine("TDL_PROG:bozuk"));
        Assert.Null(YtDlpParsing.ParseProgressLine(null));
        Assert.Equal("finished", YtDlpParsing.ParseProgressLine("TDL_PROG:finished|100|100|NA|NA|NA")!.Status);
    }

    [Fact]
    public void ParseFilePathLine()
    {
        Assert.Equal(@"C:\v\Örnek Video.mp4", YtDlpParsing.ParseFilePathLine(@"TDL_FILE:C:\v\Örnek Video.mp4"));
        Assert.Null(YtDlpParsing.ParseFilePathLine("TDL_FILE:"));
        Assert.Null(YtDlpParsing.ParseFilePathLine("başka satır"));
    }

    [Theory]
    [InlineData("ERROR: [youtube] abc: This video is DRM protected", "DRM")]
    [InlineData("ERROR: Unsupported URL: https://x.test/", "desteklemiyor")]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you are not a bot", "oturum")]
    [InlineData("ERROR: [youtube] abc: Video unavailable", "kullanılamıyor")]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden", "403")]
    [InlineData("ERROR: Postprocessing: ffprobe and ffmpeg not found. Please install", "ffmpeg bulunamadı")]
    [InlineData("ERROR: something odd happened", "something odd happened")]
    public void MapError_GivesTurkishMessage(string stderr, string expectedPart)
        => Assert.Contains(expectedPart, YtDlpParsing.MapError(stderr), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void BuildDownloadArgs_ContainsRequiredOptions()
    {
        var a = YtDlpClient.BuildDownloadArgs("https://x.test/v", "137+bestaudio", @"C:\o\a.%(ext)s", "https://x.test/", @"C:\ff\ffmpeg.exe");
        Assert.Equal("-f", a[0]); Assert.Equal("137+bestaudio", a[1]);
        Assert.Contains("--merge-output-format", a); Assert.Contains("mp4", a);
        Assert.Contains("--newline", a); Assert.Contains("--progress-template", a);
        Assert.Contains("--ffmpeg-location", a); Assert.Contains("--referer", a);
        Assert.Equal("https://x.test/v", a[^1]); Assert.Equal("--", a[^2]);
    }
}

