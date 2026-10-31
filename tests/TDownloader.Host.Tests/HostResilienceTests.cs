using TDownloader.Core.Ipc;

namespace TDownloader.Host.Tests;

public class HostResilienceTests
{
    [Fact]
    public async Task NoApp_ManyDownloads_AreRejectedFast_BecauseCooldownIsCheckedBeforeConnecting()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        using var host = new HostProcessTests.HostProc(pipe, Path.Combine(Path.GetTempPath(), "yok-" + Guid.NewGuid().ToString("N") + ".exe"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 40; i++) await host.Send(new DownloadMessage("n" + i, $"https://x/{i}.zip", null, null, null, null, null, 0));
        for (int i = 0; i < 40; i++)
        {
            var r = Assert.IsType<DownloadResultMessage>(await host.Receive(15000));
            Assert.False(r.Accepted);
        }
        // Eski davranış: her mesaj için 300 ms bağlanma denemesi (>= 12 sn). Şimdi cooldown'da ucuz deneme.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"Çok yavaş: {sw.Elapsed}");
    }

    [Fact]
    public async Task AppStartedManually_DuringCooldown_IsStillFound()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        using var host = new HostProcessTests.HostProc(pipe, Path.Combine(Path.GetTempPath(), "yok-" + Guid.NewGuid().ToString("N") + ".exe"));
        await host.Send(new DownloadMessage("a", "https://x/a.zip", null, null, null, null, null, 0));
        Assert.False(Assert.IsType<DownloadResultMessage>(await host.Receive(10000)).Accepted);   // cooldown başladı

        await using var server = new IpcServer(pipe)
        {
            MessageHandler = (m, _) => Task.FromResult<IpcMessage?>(m is PingMessage ? new PongMessage("2.0.0") : null)
        };
        server.Start();
        await host.Send(new PingMessage());
        Assert.IsType<PongMessage>(await host.Receive(10000));
    }

    [Fact]
    public void HostLog_RotatesBySize_WhileRunning()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tdl-log-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "host.log");
        try
        {
            var log = new HostLog(path, maxBytes: 4096);
            for (int i = 0; i < 400; i++) log.Write("satır " + i + " " + new string('x', 60));
            Assert.True(File.Exists(path + ".old"), "döndürülmedi");
            Assert.True(new FileInfo(path).Length <= 4096 + 200);
            Assert.True(new FileInfo(path + ".old").Length <= 4096 + 200);
            Assert.Contains("satır 399", File.ReadAllText(path));      // en yeni kayıt kaybolmaz
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void HostLog_RotatesOversizedFileAtStartup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tdl-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "host.log");
        try
        {
            File.WriteAllText(path, new string('a', 10_000));
            var log = new HostLog(path, maxBytes: 4096);
            log.Write("yeni");
            Assert.True(File.Exists(path + ".old"));
            Assert.True(new FileInfo(path).Length < 200);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
