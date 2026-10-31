using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Tests;

/// <summary>downloads.json: girintisiz, çerezsiz, atomik + .bak, bozuk dosyada yedekten yükleme.</summary>
public sealed class PersistenceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-pers-" + Guid.NewGuid().ToString("N"));
    public PersistenceTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private DownloadManager NewMgr() =>
        new(new AppPaths(Path.Combine(_tmp, "app")), new AppSettings { SaveFolder = Path.Combine(_tmp, "dl") });

    [Fact]
    public async Task Save_IsCompact_HasNoCookies_AndKeepsBackup()
    {
        var mgr = NewMgr();
        var a = mgr.Add(new AddDownloadRequest("http://127.0.0.1:1/a.zip", "a.zip", Cookie: "sid=GIZLI-COOKIE-DEGERI", StartNow: false));
        mgr.Add(new AddDownloadRequest("http://127.0.0.1:1/b.zip", "b.zip", StartNow: false));
        mgr.Save();
        string file = mgr.Paths.DownloadsFile;
        string text = File.ReadAllText(file);
        Assert.DoesNotContain("GIZLI-COOKIE-DEGERI", text);
        Assert.DoesNotContain("cookie", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n", text);                 // girintisiz tek satır
        Assert.False(File.Exists(file + ".bak"));          // ilk yazımda yedek yok

        mgr.Add(new AddDownloadRequest("http://127.0.0.1:1/c.zip", "c.zip", StartNow: false));
        mgr.Save();
        Assert.True(File.Exists(file + ".bak"));
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(File.ReadAllText(file + ".bak")).RootElement.GetArrayLength());   // bir önceki sürüm
        Assert.Equal(3, System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetArrayLength());
        Assert.False(File.Exists(file + ".tmp"));
        Assert.Equal("sid=GIZLI-COOKIE-DEGERI", a.Cookie);  // bellekte durur
        await mgr.ShutdownAsync();
    }

    [Fact]
    public async Task Load_CorruptMainFile_RestoresFromBackup()
    {
        var mgr = NewMgr();
        mgr.Add(new AddDownloadRequest("http://127.0.0.1:1/a.zip", "a.zip", StartNow: false));
        mgr.Save();
        mgr.Add(new AddDownloadRequest("http://127.0.0.1:1/b.zip", "b.zip", StartNow: false));
        mgr.Save();                                   // .bak = 1 kayıt, ana = 2 kayıt
        File.WriteAllText(mgr.Paths.DownloadsFile, "{ bozuk json ][");

        var mgr2 = NewMgr();
        mgr2.Load();
        Assert.Single(mgr2.Items);                    // yedekten (önceki sürüm)
        Assert.Equal("a.zip", mgr2.Items[0].FileName);
        Assert.NotNull(mgr2.LoadWarning);
        Assert.True(File.Exists(mgr.Paths.DownloadsFile + ".corrupt"));
        await mgr.ShutdownAsync();
    }

    [Fact]
    public void Load_OldFileWithCookie_IgnoresCookie_AndRewritesWithoutIt()
    {
        var mgr = NewMgr();
        Directory.CreateDirectory(mgr.Paths.Root);
        File.WriteAllText(mgr.Paths.DownloadsFile,
            "[{\"id\":\"x1\",\"url\":\"http://127.0.0.1:1/a.zip\",\"fileName\":\"a.zip\",\"savePath\":\"C:\\\\x\\\\a.zip\",\"status\":\"paused\",\"cookie\":\"eski=cerez\"}]");
        mgr.Load();
        var it = Assert.Single(mgr.Items);
        Assert.Null(it.Cookie);
        mgr.Save();
        Assert.DoesNotContain("eski=cerez", File.ReadAllText(mgr.Paths.DownloadsFile));
    }

    [Fact]
    public void Load_NoFiles_StartsEmptyWithoutWarning()
    {
        var mgr = NewMgr();
        mgr.Load();
        Assert.Empty(mgr.Items);
        Assert.Null(mgr.LoadWarning);
    }

    [Fact]
    public void Load_SkipsNullEntries_AndIdsThatCouldEscapeDwnlData()
    {
        var mgr = NewMgr();
        Directory.CreateDirectory(mgr.Paths.Root);
        File.WriteAllText(mgr.Paths.DownloadsFile,
            "[null,{\"id\":\"../../Windows\",\"url\":\"http://127.0.0.1:1/a\",\"fileName\":\"a\",\"savePath\":\"C:/x/a\",\"status\":\"paused\"}," +
            "{\"id\":\"abc123\",\"url\":\"http://127.0.0.1:1/b\",\"fileName\":\"b\",\"savePath\":\"C:/x/b\",\"status\":\"downloading\"}]");
        mgr.Load();
        var it = Assert.Single(mgr.Items);
        Assert.Equal("abc123", it.Id);
        Assert.Equal(DownloadStatus.Paused, it.Status);
    }

    [Theory]
    [InlineData("ERROR: addrmore failed", false)]
    [InlineData("ERROR: This video is DRM protected", true)]
    public void MapError_DrmMatchesWholeWordOnly(string stderr, bool drm)
    {
        Assert.Equal(drm, TDownloader.Core.Video.YtDlpParsing.MapError(stderr).Contains("DRM korumalı"));
    }
}
