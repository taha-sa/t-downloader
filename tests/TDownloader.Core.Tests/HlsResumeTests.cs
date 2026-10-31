using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

/// <summary>HLS duraklat/devam: inmiş parçalar saklanır, devamda yalnızca eksik parçalar istenir.</summary>
public class HlsResumeTests : IDisposable
{
    private const int N = 40;
    private const int SegBytes = 20 * 188;
    private readonly HlsTestServer _s = new();
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-hlsr-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly Regex SegRe = new(@"^/(?<d>[a-z]+)/(?<p>[a-z])(?<i>\d+)\.ts$", RegexOptions.Compiled);

    public HlsResumeTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { _s.Dispose(); try { Directory.Delete(_tmp, true); } catch { } }

    private static byte[][] Plain(int n, int seedBase = 1000) => Enumerable.Range(0, n).Select(i => HlsTestServer.Ts(20, seedBase + i)).ToArray();

    private static byte[] Enc(byte[] key, byte[] data, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(data, iv, PaddingMode.PKCS7);
    }

    /// <summary>Playlist + parçalar yayımlar. namePrefix parça dosya adı harfi, query adres sonuna eklenir.</summary>
    private void Publish(string dir, byte[][] plain, bool encrypted, string namePrefix = "s", string query = "", bool publishSegs = true)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes("key-" + dir))[..16];
        if (encrypted)
        {
            _s.Bin($"/{dir}/key.bin", key, "application/octet-stream");
            sb.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n");
        }
        for (int i = 0; i < plain.Length; i++)
        {
            if (publishSegs) _s.Bin($"/{dir}/{namePrefix}{i}.ts", encrypted ? Enc(key, plain[i], HlsClient.SequenceIv(i)) : plain[i]);
            sb.Append("#EXTINF:4.0,\n").Append($"{namePrefix}{i}.ts{query}\n");
        }
        sb.Append("#EXT-X-ENDLIST\n");
        _s.Text($"/{dir}/index.m3u8", sb.ToString());
    }

    private VideoRunner NewRunner(string root)
    {
        var paths = new AppPaths(root);
        var yt = new YtDlpClient(() => new AppSettings { YtDlpPath = Path.Combine(root, "yok.exe"), FfmpegPath = Path.Combine(root, "yok-ffmpeg.exe") }, paths, root);
        return new VideoRunner(yt, new HlsClient());
    }

    private DownloadItem NewItem(string dir, string name, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Url = $"{_s.Base}/{dir}/index.m3u8",
        SavePath = Path.Combine(_tmp, "out", name + ".mp4"),
        Kind = DownloadKind.External,
        ExternalFormat = HlsClient.MakeFormat($"{_s.Base}/{dir}/index.m3u8", null, 720),
    };

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Tek seferde (kesintisiz) indirilen referans çıktı.</summary>
    private async Task<string> BaselineAsync(string dir)
    {
        var res = await NewRunner(Path.Combine(_tmp, "base-" + dir)).RunAsync(NewItem(dir, "base-" + dir), _ => { }, CancellationToken.None);
        Assert.True(res.Success, res.Error);
        return Sha(res.FinalPath!);
    }

    private string HlsDir(string root, DownloadItem item) => Path.Combine(root, "DwnlData", item.Id, "hls");

    /// <summary>Verilen eşikten sonra iptal ederek (duraklat) başlatır; iptalden sonra diskte kalan parça numaralarını döner.</summary>
    private async Task<HashSet<int>> PauseMidwayAsync(VideoRunner runner, DownloadItem item, string root, int afterSegments)
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(item, p =>
        {
            // Bayt bazlı ilerleme parça bitmeden de artar; eşik için diskte tamamlanmış parça sayısına bakılır.
            var d = HlsDir(root, item);
            if (Directory.Exists(d) && Directory.GetFiles(d, "*.seg").Length >= afterSegments) cts.Cancel();
        }, cts.Token));
        var hls = HlsDir(root, item);
        Assert.True(Directory.Exists(hls), "duraklatınca hls klasörü korunmalı");
        Assert.True(File.Exists(Path.Combine(hls, "hls-state.json")));
        var have = Directory.GetFiles(hls, "*.seg").Select(f => int.Parse(Path.GetFileNameWithoutExtension(f))).ToHashSet();
        Assert.InRange(have.Count, afterSegments, N - 1);
        Assert.Empty(Directory.GetFiles(hls, "*.tmp"));          // yarım dosya bırakılmaz
        return have;
    }

    /// <summary>Log'daki [from..) isteklerinden parça numarası → istek sayısı.</summary>
    private Dictionary<int, int> SegRequestsSince(int from, string dir)
    {
        var d = new Dictionary<int, int>();
        lock (_s.Log)
            foreach (var e in _s.Log.Skip(from))
            {
                var m = SegRe.Match(e.Path);
                if (m.Success && m.Groups["d"].Value == dir) { int i = int.Parse(m.Groups["i"].Value); d[i] = d.GetValueOrDefault(i) + 1; }
            }
        return d;
    }

    private int LogCount() { lock (_s.Log) return _s.Log.Count; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseThenResume_RequestsOnlyMissingSegments_AndOutputIsIdentical(bool encrypted)
    {
        string dir = encrypted ? "aes" : "ts";
        Publish(dir, Plain(N), encrypted);
        string baseline = await BaselineAsync(dir);

        string root = Path.Combine(_tmp, "app");
        var item = NewItem(dir, "film");
        var have = await PauseMidwayAsync(NewRunner(root), item, root, afterSegments: 15);
        int mark = LogCount();

        // yeni VideoRunner/HlsClient örneği ile devam
        var res = await NewRunner(root).RunAsync(item, _ => { }, CancellationToken.None);
        Assert.True(res.Success, res.Error);
        Assert.Equal(baseline, Sha(res.FinalPath!));

        var second = SegRequestsSince(mark, dir);
        int requestedAgain = have.Count(i => second.ContainsKey(i));       // KANIT: zaten inmiş parçalar ikinci kez istenen sayısı
        Assert.Equal(0, requestedAgain);
        Assert.Equal(Enumerable.Range(0, N).Except(have).OrderBy(i => i), second.Keys.OrderBy(i => i));
        Assert.All(second.Values, c => Assert.Equal(1, c));
        // playlist devamda yeniden çekildi
        lock (_s.Log) Assert.Contains(_s.Log.Skip(mark), e => e.Path == $"/{dir}/index.m3u8");
        // başarıdan sonra hls klasörü silinir
        Assert.False(Directory.Exists(HlsDir(root, item)));
    }

    [Fact]
    public async Task Resume_ProgressStartsFromDoneSegments_NotZero()
    {
        Publish("pr", Plain(N), false);
        string root = Path.Combine(_tmp, "app");
        var item = NewItem("pr", "film");
        var have = await PauseMidwayAsync(NewRunner(root), item, root, 20);
        var seen = new List<ExternalProgress>();
        var res = await NewRunner(root).RunAsync(item, p => { lock (seen) seen.Add(p); }, CancellationToken.None);
        Assert.True(res.Success, res.Error);
        lock (seen)
        {
            Assert.True(seen[0].Downloaded >= (long)have.Count * SegBytes);   // 0'dan değil, inmiş parçalardan
            Assert.Equal((long)N * SegBytes, seen[^1].Downloaded);
        }
    }

    [Fact]
    public async Task Resume_WithChangedSegmentUrls_SameCount_KeepsFilesAndUsesNewUrls()
    {
        var plain = Plain(N);
        Publish("sg", plain, true);
        string baseline = await BaselineAsync("sg");
        string root = Path.Combine(_tmp, "app");
        var item = NewItem("sg", "film");
        var have = await PauseMidwayAsync(NewRunner(root), item, root, 12);

        // İmzalı adresler değişti: yeni dosya adı + sorgu parametresi; eski adresler artık 403.
        Publish("sg", plain, true, namePrefix: "n", query: "?sig=yeni&exp=999");
        _s.Forbid = p => Regex.IsMatch(p, @"^/sg/s\d+\.ts$");
        int mark = LogCount();
        var res = await NewRunner(root).RunAsync(item, _ => { }, CancellationToken.None);
        Assert.True(res.Success, res.Error);
        Assert.Equal(baseline, Sha(res.FinalPath!));
        lock (_s.Log)
        {
            var after = _s.Log.Skip(mark).Select(e => e.Path).ToList();
            Assert.DoesNotContain(after, p => Regex.IsMatch(p, @"^/sg/s\d+\.ts$"));      // eski URL'ler hiç kullanılmadı
            var reqs = after.Select(p => Regex.Match(p, @"^/sg/n(\d+)\.ts$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            Assert.Equal(Enumerable.Range(0, N).Except(have).OrderBy(i => i), reqs.OrderBy(i => i));
            Assert.Empty(reqs.Intersect(have));
        }
    }

    [Fact]
    public async Task Resume_WithDifferentSegmentCount_RestartsFromScratch_AndLogs()
    {
        Publish("df", Plain(N), false);
        string root = Path.Combine(_tmp, "app");
        var item = NewItem("df", "film");
        var have = await PauseMidwayAsync(NewRunner(root), item, root, 12);

        var changed = Plain(N + 1, 5000);              // sayı ve içerik farklı: eski parçalara güvenilmez
        Publish("df", changed, false);
        int mark = LogCount();
        var logs = new List<string>();
        var opt = new HlsClient.Options(null, WorkDir: HlsDir(root, item), Log: logs.Add);
        var path = await new HlsClient().DownloadAsync(item.Url, null, null, Path.Combine(_tmp, "out", "film"), opt, _ => { }, CancellationToken.None);
        Assert.Equal(changed.SelectMany(x => x).ToArray(), File.ReadAllBytes(path));
        Assert.Contains(logs, l => l.Contains("baştan"));
        Assert.Equal(N + 1, SegRequestsSince(mark, "df").Count);
    }

    [Fact]
    public async Task Resume_WithLeftoverTmpFile_RedownloadsThatSegment_NoCorruptData()
    {
        Publish("tm", Plain(N), false);
        string baseline = await BaselineAsync("tm");
        string root = Path.Combine(_tmp, "app");
        var item = NewItem("tm", "film");
        var have = await PauseMidwayAsync(NewRunner(root), item, root, 15);
        var hls = HlsDir(root, item);

        // Çökme benzetimi: bir eksik parçanın yarım .tmp'si, bir de tam inmiş bir parçanın yanında artık .tmp
        int missingIdx = Enumerable.Range(0, N).First(i => !have.Contains(i));
        File.WriteAllBytes(Path.Combine(hls, missingIdx.ToString("D5") + ".seg.tmp"), new byte[] { 1, 2, 3, 4, 5 });
        int doneIdx = have.First();
        File.WriteAllBytes(Path.Combine(hls, doneIdx.ToString("D5") + ".seg.tmp"), new byte[] { 9, 9 });
        int mark = LogCount();

        var res = await NewRunner(root).RunAsync(item, _ => { }, CancellationToken.None);
        Assert.True(res.Success, res.Error);
        Assert.Equal(baseline, Sha(res.FinalPath!));
        var second = SegRequestsSince(mark, "tm");
        Assert.True(second.ContainsKey(missingIdx));           // yarım parça yeniden indirildi
        Assert.False(second.ContainsKey(doneIdx));             // tamamı olan parça tekrar istenmedi
    }

    [Fact]
    public async Task Resume_DoesNotTrustZeroByteSegmentFile()
    {
        Publish("zb", Plain(N), false);
        string baseline = await BaselineAsync("zb");
        string root = Path.Combine(_tmp, "app");
        var item = NewItem("zb", "film");
        var have = await PauseMidwayAsync(NewRunner(root), item, root, 15);
        int bad = have.First();
        File.WriteAllBytes(Path.Combine(HlsDir(root, item), bad.ToString("D5") + ".seg"), Array.Empty<byte>());
        int mark = LogCount();
        var res = await NewRunner(root).RunAsync(item, _ => { }, CancellationToken.None);
        Assert.True(res.Success, res.Error);
        Assert.Equal(baseline, Sha(res.FinalPath!));
        Assert.True(SegRequestsSince(mark, "zb").ContainsKey(bad));
    }

    [Fact]
    public async Task Failure_KeepsSegments_AndSuccessRemovesHlsFolder()
    {
        Publish("fl", Plain(10), false);
        string root = Path.Combine(_tmp, "app");
        var item = NewItem("fl", "film");
        _s.Forbid = p => p == "/fl/s7.ts";
        var runner = NewRunner(root);
        var bad = await runner.RunAsync(item, _ => { }, CancellationToken.None);   // yt-dlp yok: HLS hatası döner
        Assert.False(bad.Success);
        Assert.True(Directory.GetFiles(HlsDir(root, item), "*.seg").Length is > 0 and < 10);
        _s.Forbid = null;
        var ok = await NewRunner(root).RunAsync(item, _ => { }, CancellationToken.None);
        Assert.True(ok.Success, ok.Error);
        Assert.False(Directory.Exists(Path.Combine(root, "DwnlData", item.Id)));
    }
}
