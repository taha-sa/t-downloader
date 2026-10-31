using TDownloader.Core.Downloads;
using TDownloader.Core.Settings;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

/// <summary>Sahte yt-dlp (bkz. <see cref="FakeTool"/>) ile YtDlpClient.RunAsync: C5 (report istisnası), C4 (kullanıcı dosyasının üzerine yazmama), C8 (çerez argv'de yok).</summary>
public sealed class YtDlpRunTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-ytr-" + Guid.NewGuid().ToString("N")[..8]);
    public YtDlpRunTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private string Fake(string cfg) => FakeTool.Install(Path.Combine(_tmp, "fake"), cfg);
    private string Args() => File.ReadAllText(Path.Combine(_tmp, "fake", "args.txt"));

    private YtDlpClient Client(string exe) =>
        new(() => new AppSettings { YtDlpPath = exe, FfmpegPath = Path.Combine(_tmp, "yok.exe") }, new AppPaths(Path.Combine(_tmp, "ad")), _tmp);

    private DownloadItem Item(string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Url = "https://x.test/v", SavePath = Path.Combine(_tmp, "dl", name), Kind = DownloadKind.External,
        ExternalFormat = "best",
    };

    [Fact]
    public async Task C5_ReportThrowing_DoesNotStallStdout()
    {
        string final = Path.Combine(_tmp, "dl", "A.mp4");
        // 20000 satır (> 64 KB pipe) ilerleme basar; stdout tüketilmezse süreç takılır.
        string exe = Fake("spam=20000\nfile=" + final);
        int calls = 0;
        var task = Client(exe).RunAsync(Item("A.mp4"), _ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("abone patladı"); }, CancellationToken.None);
        var res = await task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(res.Success, res.Error);
        Assert.Equal(final, res.FinalPath);
        Assert.True(calls > 1000, "okuma döngüsü istisnadan sonra da sürmeli: " + calls);
    }

    [Fact]
    public async Task C4_ExistingUserFileWithOtherExtension_NotUsedAsOutputName()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "dl"));
        File.WriteAllText(Path.Combine(_tmp, "dl", "Film.mp4"), "kullanicinin dosyasi");
        File.WriteAllText(Path.Combine(_tmp, "dl", "Film.mp4.part"), "devam artigi, cakisma degil");
        await Client(Fake("")).RunAsync(Item("Film.m4a"), _ => { }, CancellationToken.None);
        string args = Args();
        Assert.Contains("Film (1).%(ext)s", args);
        Assert.DoesNotContain("Film.%(ext)s", args);
        Assert.Equal("kullanicinin dosyasi", File.ReadAllText(Path.Combine(_tmp, "dl", "Film.mp4")));
    }

    [Fact]
    public async Task C4_OnlyPartLeftover_KeepsOriginalName()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "dl"));
        File.WriteAllText(Path.Combine(_tmp, "dl", "Film.mp4.part"), "x");
        File.WriteAllText(Path.Combine(_tmp, "dl", "Film.f137.mp4"), "x");
        await Client(Fake("")).RunAsync(Item("Film.mp4"), _ => { }, CancellationToken.None);
        Assert.Contains("Film.%(ext)s", Args());
    }

    [Fact]
    public async Task C8_CookieNotInArgv_ButInTempFile_DeletedAfterwards()
    {
        var item = Item("C.mp4");
        item.Cookie = "sid=GIZLI123; t=9"; item.CookieHost = "film.test";
        await Client(Fake("")).RunAsync(item, _ => { }, CancellationToken.None);
        string args = Args();
        Assert.DoesNotContain("GIZLI123", args);
        Assert.Contains("--cookies", args);
        string seen = File.ReadAllText(Path.Combine(_tmp, "fake", "seen-cookies.txt"));
        Assert.Contains(".film.test\tTRUE\t/\tFALSE\t2147483647\tsid\tGIZLI123", seen);
        int ci = args.Split('\n').ToList().IndexOf("--cookies");
        Assert.False(File.Exists(args.Split('\n')[ci + 1]), "geçici çerez dosyası iş bitince silinmeli");
    }

    [Fact]
    public async Task C8_NoCookieHost_NoCookiesArgAtAll()
    {
        var item = Item("D.mp4");
        item.Cookie = "sid=GIZLI123";
        await Client(Fake("")).RunAsync(item, _ => { }, CancellationToken.None);
        string args = Args();
        Assert.DoesNotContain("--cookies", args);
        Assert.DoesNotContain("GIZLI123", args);
    }
}
