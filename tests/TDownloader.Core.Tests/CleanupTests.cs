using System.Diagnostics;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;
using TDownloader.Core.Util;

namespace TDownloader.Core.Tests;

/// <summary>Silinen indirmelerin artıkları ve yt-dlp/ffmpeg süreçlerinin Job Object'e bağlanması.</summary>
public sealed class CleanupTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tdl-clean-" + Guid.NewGuid().ToString("N"));
    public CleanupTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public async Task RemoveAsync_DeletesDwnlDataAndYtDlpLeftovers_ButNotOtherFiles()
    {
        var paths = new AppPaths(Path.Combine(_tmp, "app"));
        var mgr = new DownloadManager(paths, new AppSettings { SaveFolder = Path.Combine(_tmp, "dl") });
        var folder = Path.Combine(_tmp, "dl", "Video");
        Directory.CreateDirectory(folder);
        var item = mgr.AddExternal(new AddExternalRequest("http://127.0.0.1:1/v", "film.mp4", "best", SaveFolder: folder, StartNow: false));

        string[] leftovers = { "film.mp4.part", "film.f137.mp4.part", "film.mp4.ytdl", "film.f251.webm.part-Frag7", "FILM.mp4.PART" };
        string[] keep = { "baska.mp4.part", "film (1).mp4.part", "film.notlar.txt" };
        foreach (var n in leftovers.Concat(keep)) File.WriteAllText(Path.Combine(folder, n), "x");
        var data = Path.Combine(paths.DwnlDataDir, item.Id, "hls");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "00001.seg"), "x");

        await mgr.RemoveAsync(item.Id);

        Assert.False(Directory.Exists(Path.Combine(paths.DwnlDataDir, item.Id)));
        foreach (var n in leftovers) Assert.False(File.Exists(Path.Combine(folder, n)), n + " silinmeliydi");
        foreach (var n in keep) Assert.True(File.Exists(Path.Combine(folder, n)), n + " korunmalıydı");
        await mgr.ShutdownAsync();
    }

    [Fact]
    public void ExternalCleanup_DoesNotTouchCompletedOutput()
    {
        var f = Path.Combine(_tmp, "x.mp4");
        File.WriteAllText(f, "tamam"); File.WriteAllText(f + ".part", "yarim");
        Assert.Equal(1, ExternalCleanup.DeleteYtDlpLeftovers(f));
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void ProcessJob_AssignsChildToKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = new ProcessStartInfo("ping.exe", "-n 8 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        try
        {
            Assert.False(ProcessJob.Contains(p));
            Assert.True(ProcessJob.Assign(p));
            Assert.True(ProcessJob.Contains(p));
        }
        finally { try { p.Kill(true); } catch { } }
    }
}
