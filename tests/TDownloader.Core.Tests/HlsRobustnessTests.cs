using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

/// <summary>HLS: imzalı adres yenileme, parça başına deneme, devam, birleştirirken silme, boş alan, ses izi hatası.</summary>
public class HlsRobustnessTests : IDisposable
{
    private readonly HlsTestServer _s = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-hlsx-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly MediaContext Ctx = MediaContext.Empty;

    public HlsRobustnessTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { _s.Dispose(); try { Directory.Delete(_tmp, true); } catch { } }

    private static byte[][] Plain(int n, int seed = 100) => Enumerable.Range(0, n).Select(i => HlsTestServer.Ts(20, seed + i)).ToArray();

    /// <summary>/dir/index.m3u8 + parça rotaları (prefix + i + ".ts"). publishSegs=false: yalnızca playlist.</summary>
    private void Publish(string dir, byte[][] segs, string prefix = "s", bool segsToo = true)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < segs.Length; i++)
        {
            if (segsToo) _s.Bin($"/{dir}/{prefix}{i}.ts", segs[i]);
            sb.Append("#EXTINF:4.0,\n").Append($"{prefix}{i}.ts\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        _s.Text($"/{dir}/index.m3u8", sb.ToString());
    }

    private string Url(string dir) => $"{_s.Base}/{dir}/index.m3u8";

    private static IEnumerable<string> SegFiles(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.seg") : Enumerable.Empty<string>();

    // ------------------------------------------------------------------ madde 5

    [Fact]
    public async Task Segment403_RefreshesPlaylist_AndRetriesWithNewUrls()
    {
        const int N = 30;
        var plain = Plain(N);
        Publish("rf", plain, "n");                       // yeni adresli parçalar var...
        Publish("rf", plain, "s");                       // ...ama playlist başlangıçta eski (s) adresleri gösteriyor
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        for (int i = 0; i < N; i++) sb.Append("#EXTINF:4.0,\n").Append($"n{i}.ts\n");
        sb.Append("#EXT-X-ENDLIST\n");
        int flipped = 0;
        _s.Forbid = p =>
        {
            var m = Regex.Match(p, @"^/rf/s(\d+)\.ts$");
            if (!m.Success || int.Parse(m.Groups[1].Value) < 8) return false;
            if (Interlocked.Exchange(ref flipped, 1) == 0) _s.Text("/rf/index.m3u8", sb.ToString());   // imzalı adresler döndü
            return true;                                                                          // eskileri artık 403
        };
        var logs = new List<string>();
        var opt = new HlsClient.Options(null, Parallel: 4, RetryDelay: TimeSpan.FromMilliseconds(10), WorkDir: Path.Combine(_tmp, "w1"), Log: m => { lock (logs) logs.Add(m); });
        var path = await new HlsClient().DownloadAsync(Url("rf"), null, Ctx, Path.Combine(_tmp, "out", "film"), opt, _ => { }, CancellationToken.None);
        Assert.Equal(plain.SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
        lock (logs) Assert.Contains(logs, l => l.Contains("playlist yenilendi"));
        lock (_s.Log) Assert.Contains(_s.Log, e => Regex.IsMatch(e.Path, @"^/rf/n\d+\.ts$"));
    }

    [Fact]
    public async Task PerSegment_FiveAttempts_ThenGivesUp()
    {
        Publish("ra", Plain(4));
        int hits = 0;
        _s.Transient = p => p == "/ra/s2.ts" && Interlocked.Increment(ref hits) <= 4;     // 4 hata + 5. deneme başarılı
        var opt = new HlsClient.Options(null, RetryDelay: TimeSpan.FromMilliseconds(5));
        var path = await new HlsClient().DownloadAsync(Url("ra"), null, Ctx, Path.Combine(_tmp, "ra"), opt, _ => { }, CancellationToken.None);
        Assert.Equal(Plain(4).SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
        Assert.Equal(5, hits);

        int hits2 = 0;
        Publish("rb", Plain(4));
        _s.Transient = p => p == "/rb/s2.ts" && Interlocked.Increment(ref hits2) <= 99;
        await Assert.ThrowsAsync<HlsException>(() => new HlsClient().DownloadAsync(Url("rb"), null, Ctx, Path.Combine(_tmp, "rb"), opt, _ => { }, CancellationToken.None));
        Assert.Equal(5, hits2);        // tam 5 deneme
    }

    [Fact]
    public async Task Failure_AfterSomeSegments_DoesNotFallBackToYtDlp_AndKeepsSegments()
    {
        string tools = Path.Combine(_tmp, "tools"); Directory.CreateDirectory(tools);
        string fakeYt = Path.Combine(tools, "yt-dlp.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), fakeYt);     // var olan, zararsız bir exe
        string root = Path.Combine(_tmp, "app");
        var paths = new AppPaths(root);
        var yt = new YtDlpClient(() => new AppSettings { YtDlpPath = fakeYt, FfmpegPath = Path.Combine(root, "yok.exe") }, paths, root);
        var runner = new VideoRunner(yt, new HlsClient());
        Publish("nf", Plain(12));
        _s.Forbid = p => p == "/nf/s9.ts";
        var item = new DownloadItem
        {
            Id = "nfid", Url = Url("nf"), SavePath = Path.Combine(_tmp, "out", "nf.mp4"), Kind = DownloadKind.External,
            ExternalFormat = HlsClient.MakeFormat(Url("nf"), null, 720),
        };
        var res = await runner.RunAsync(item, _ => { }, CancellationToken.None);
        Assert.False(res.Success);
        Assert.Contains("korundu", res.Error);                                     // yt-dlp'ye geçilmedi
        var hls = Path.Combine(root, "DwnlData", "nfid", "hls");
        Assert.InRange(SegFiles(hls).Count(), 1, 11);                                  // inmiş parçalar duruyor

        // Hiç parça inmediyse yt-dlp'ye geçilir (hata metni artık 'korundu' içermez)
        Publish("nz", Plain(3));
        _s.Forbid = p => p.StartsWith("/nz/s");
        var item2 = new DownloadItem
        {
            Id = "nzid", Url = Url("nz"), SavePath = Path.Combine(_tmp, "out", "nz.mp4"), Kind = DownloadKind.External,
            ExternalFormat = HlsClient.MakeFormat(Url("nz"), null, 720),
        };
        var res2 = await runner.RunAsync(item2, _ => { }, CancellationToken.None);
        Assert.False(res2.Success);
        Assert.DoesNotContain("korundu", res2.Error ?? "");
    }

    // ------------------------------------------------------------------ madde 6

    [Fact]
    public async Task Merge_DeletesSegmentsAsItGoes_ReportsPhase_AndResultIsIdentical()
    {
        const int N = 25;
        var plain = Plain(N);
        Publish("mg", plain);
        string work = Path.Combine(_tmp, "w2");
        var counts = new List<int>(); var phases = new List<string>();
        var opt = new HlsClient.Options(null, WorkDir: work);
        var path = await new HlsClient().DownloadAsync(Url("mg"), null, Ctx, Path.Combine(_tmp, "out", "mg"), opt, p =>
        {
            if (p.Phase != null) { lock (counts) { phases.Add(p.Phase); counts.Add(SegFiles(work).Count()); } }
        }, CancellationToken.None);
        Assert.Equal(plain.SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
        lock (counts)
        {
            Assert.NotEmpty(phases);
            Assert.All(phases, ph => Assert.StartsWith("Birleştiriliyor %", ph));
            Assert.True(counts[0] < N, "ilk birleştirme raporunda en az bir parça silinmiş olmalı");
            Assert.Equal(0, counts[^1]);
            for (int i = 1; i < counts.Count; i++) Assert.True(counts[i] <= counts[i - 1]);   // tek tek azalır
            Assert.Equal("Birleştiriliyor %100", phases[^1]);
        }
    }

    [Fact]
    public async Task InsufficientDiskSpace_FailsWithClearMessage_AndKeepsSegments()
    {
        Publish("ds", Plain(12));
        string work = Path.Combine(_tmp, "w3");
        var opt = new HlsClient.Options(null, Parallel: 1, WorkDir: work, FreeSpace: _ => 1000);
        var ex = await Assert.ThrowsAsync<HlsDiskSpaceException>(() =>
            new HlsClient().DownloadAsync(Url("ds"), null, Ctx, Path.Combine(_tmp, "out", "ds"), opt, _ => { }, CancellationToken.None));
        Assert.Contains("boş alan", ex.Message);
        Assert.True(SegFiles(work).Any());                       // inenler korunur
        // yeterli alan: sorunsuz
        var ok = new HlsClient.Options(null, WorkDir: work, FreeSpace: _ => long.MaxValue / 4);
        var path = await new HlsClient().DownloadAsync(Url("ds"), null, Ctx, Path.Combine(_tmp, "out", "ds"), ok, _ => { }, CancellationToken.None);
        Assert.Equal(Plain(12).SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Resume_AfterMergeCompleted_DoesNotRedownloadSegments()
    {
        const int N = 15;
        var plain = Plain(N);
        Publish("rm", plain);
        string work = Path.Combine(_tmp, "w4");
        using var cts = new CancellationTokenSource();
        // ffmpeg aşamasında (birleştirme bitti, parçalar silindi) duraklat
        var opt1 = new HlsClient.Options("ffmpeg-yok.exe", WorkDir: work);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HlsClient().DownloadAsync(Url("rm"), null, Ctx, Path.Combine(_tmp, "out", "rm"), opt1,
            p => { if (p.Phase != null && p.Phase.StartsWith("Dönüştürülüyor")) cts.Cancel(); }, cts.Token));
        Assert.Empty(SegFiles(work));
        Assert.True(File.Exists(Path.Combine(work, "merged.v")));
        int mark; lock (_s.Log) mark = _s.Log.Count;
        var logs = new List<string>();
        var opt2 = new HlsClient.Options(null, WorkDir: work, Log: logs.Add);
        var path = await new HlsClient().DownloadAsync(Url("rm"), null, Ctx, Path.Combine(_tmp, "out", "rm"), opt2, _ => { }, CancellationToken.None);
        Assert.Equal(plain.SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
        lock (_s.Log) Assert.DoesNotContain(_s.Log.Skip(mark), e => Regex.IsMatch(e.Path, @"\.ts$"));   // parça yeniden istenmedi
        Assert.Contains(logs, l => l.Contains("zaten birleştirilmiş"));
    }

    // ------------------------------------------------------------------ madde 7

    [Fact]
    public async Task SeparateAudioFailure_FailsDownload_InsteadOfSilentVideo()
    {
        Publish("vv", Plain(4));
        Publish("aa", Plain(4, 900));
        _s.Forbid = p => p.StartsWith("/aa/s");
        var opt = new HlsClient.Options("ffmpeg-yok.exe", WorkDir: Path.Combine(_tmp, "w5"), RetryDelay: TimeSpan.FromMilliseconds(5));
        var ex = await Assert.ThrowsAsync<HlsException>(() =>
            new HlsClient().DownloadAsync(Url("vv"), Url("aa"), Ctx, Path.Combine(_tmp, "out", "vv"), opt, _ => { }, CancellationToken.None));
        Assert.Contains("Ses izi indirilemedi", ex.Message);
        Assert.False(File.Exists(Path.Combine(_tmp, "out", "vv.mp4")));
    }

    [Fact]
    public async Task SeparateAudio_WithoutFfmpeg_FailsUpFront()
    {
        Publish("v2", Plain(3));
        Publish("a2", Plain(3, 700));
        var ex = await Assert.ThrowsAsync<HlsException>(() =>
            new HlsClient().DownloadAsync(Url("v2"), Url("a2"), Ctx, Path.Combine(_tmp, "out", "v2"), new HlsClient.Options(null), _ => { }, CancellationToken.None));
        Assert.Contains("ffmpeg", ex.Message);
        lock (_s.Log) Assert.DoesNotContain(_s.Log, e => e.Path.EndsWith(".ts"));    // hiçbir şey indirilmedi
    }

    // ------------------------------------------------------------------ gerçek ffmpeg (yoksa atlanır)

    private static string? FindFfmpeg()
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var f = Path.Combine(d.Trim().Trim('"'), "ffmpeg.exe"); if (File.Exists(f)) return f; } catch (ArgumentException) { }
        }
        return null;
    }

    private static byte[]? MakeTs(string ffmpeg, string tmp, string name, params string[] inputArgs)
    {
        var f = Path.Combine(tmp, name);
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error" }.Concat(inputArgs).Concat(new[] { f })) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return p.ExitCode == 0 && File.Exists(f) ? File.ReadAllBytes(f) : null;
    }

    [Fact]
    public async Task RealFfmpeg_Remux_ReportsPhase_DeletesIntermediates_AndKeepsAudio()
    {
        var ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return;   // ffmpeg yok: atla
        var vts = MakeTs(ffmpeg, _tmp, "v.ts", "-f", "lavfi", "-i", "testsrc=duration=3:size=128x96:rate=10", "-c:v", "mpeg2video", "-f", "mpegts");
        var ats = MakeTs(ffmpeg, _tmp, "a.ts", "-f", "lavfi", "-i", "sine=duration=3", "-c:a", "aac", "-f", "mpegts");
        if (vts == null || ats == null) return;   // bu ffmpeg derlemesi üretemiyor: atla
        byte[][] Split(byte[] d, int n)
        {
            int per = (d.Length / 188 / n + 1) * 188;
            return Enumerable.Range(0, n).Select(i => d.Skip(i * per).Take(per).ToArray()).Where(x => x.Length > 0).ToArray();
        }
        Publish("rv", Split(vts, 5));
        Publish("ra2", Split(ats, 3));
        string work = Path.Combine(_tmp, "w6");
        var phases = new List<string>();
        var opt = new HlsClient.Options(ffmpeg, WorkDir: work);
        var path = await new HlsClient().DownloadAsync(Url("rv"), Url("ra2"), Ctx, Path.Combine(_tmp, "out", "film"), opt, p =>
        {
            if (p.Phase != null) lock (phases) phases.Add(p.Phase);
        }, CancellationToken.None);
        Assert.EndsWith(".mp4", path);
        Assert.True(new FileInfo(path).Length > 1000);
        lock (phases) Assert.Contains(phases, ph => ph.StartsWith("Dönüştürülüyor"));
        Assert.False(Directory.Exists(work));                // ara dosyalar + klasör silindi

        // çıktıda hem görüntü hem ses akışı olmalı
        var psi = new ProcessStartInfo(ffmpeg, $"-hide_banner -i \"{path}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        using var pr = Process.Start(psi)!;
        string info = pr.StandardError.ReadToEnd(); pr.WaitForExit(15000);
        Assert.Contains("Video:", info);
        Assert.Contains("Audio:", info);
    }
}
