using System.Diagnostics;
using System.Text;
using TDownloader.Core.Ipc;
using TDownloader.Host;

namespace TDownloader.Host.Tests;

/// <summary>Host'u alt süreç olarak çalıştırıp native çerçeve üzerinden uçtan uca doğrular.</summary>
public class HostProcessTests
{
    private static string HostDll()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "TDownloader.Host.dll");
        Assert.True(File.Exists(p), "Host dll test çıktısında yok: " + p);
        return p;
    }

    private sealed class HostProc : IDisposable
    {
        public Process P { get; }
        public string LogPath { get; }

        public HostProc(string pipe, string? appExe = null, int startTimeoutMs = 10000)
        {
            LogPath = Path.Combine(Path.GetTempPath(), "tdl-host-" + Guid.NewGuid().ToString("N") + ".log");
            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add(HostDll());
            psi.ArgumentList.Add("chrome-extension://lgceenlgaaekpbeifoookmmkjipmcboc/");
            psi.ArgumentList.Add("--pipe"); psi.ArgumentList.Add(pipe);
            psi.ArgumentList.Add("--log"); psi.ArgumentList.Add(LogPath);
            psi.ArgumentList.Add("--start-timeout-ms"); psi.ArgumentList.Add(startTimeoutMs.ToString());
            if (appExe != null) { psi.ArgumentList.Add("--app"); psi.ArgumentList.Add(appExe); }
            P = Process.Start(psi)!;
        }

        public Task Send(IpcMessage m) =>
            NativeMessaging.WriteAsync(P.StandardInput.BaseStream, Encoding.UTF8.GetBytes(IpcSerializer.Serialize(m)));

        public async Task<IpcMessage?> Receive(int timeoutMs = 10000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var raw = await NativeMessaging.ReadAsync(P.StandardOutput.BaseStream, cts.Token);
            return raw == null ? null : IpcSerializer.Deserialize(Encoding.UTF8.GetString(raw));
        }

        public void Dispose()
        {
            try { if (!P.HasExited) P.Kill(true); } catch { }
            P.Dispose();
            try { File.Delete(LogPath); } catch { }
        }
    }

    [Fact]
    public async Task Ping_GoesToServer_PongComesBackAsNativeFrame_ThenCleanExitOnStdinClose()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        IpcMessage? seen = null;
        await using var server = new IpcServer(pipe)
        {
            MessageHandler = (m, _) => { seen = m; return Task.FromResult<IpcMessage?>(m is PingMessage ? new PongMessage("9.9.9") : null); }
        };
        server.Start();

        using var host = new HostProc(pipe);
        await host.Send(new PingMessage());
        var reply = await host.Receive();

        Assert.IsType<PingMessage>(seen);
        Assert.Equal("9.9.9", Assert.IsType<PongMessage>(reply).Version);

        host.P.StandardInput.Close();
        Assert.True(host.P.WaitForExit(10000), "Host stdin kapanınca çıkmadı.");
        Assert.Equal(0, host.P.ExitCode);
        Assert.Null(await host.Receive()); // stdout'ta başka hiçbir şey yok
        Assert.Contains("chrome-extension://lgceenlgaaekpbeifoookmmkjipmcboc/", File.ReadAllText(host.LogPath));
    }

    [Fact]
    public async Task Download_And_ServerPush_BothDirections()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        await using var server = new IpcServer(pipe)
        {
            MessageHandler = (m, _) => Task.FromResult<IpcMessage?>(
                m is DownloadMessage d ? new DownloadResultMessage(d.RequestId, true) : null)
        };
        server.Start();

        using var host = new HostProc(pipe);
        await host.Send(new DownloadMessage("r1", "https://x/a.zip", null, null, null, "a.zip", "application/zip", 5));
        var res = Assert.IsType<DownloadResultMessage>(await host.Receive());
        Assert.Equal("r1", res.RequestId);
        Assert.True(res.Accepted);

        // App -> eklenti yönü (istek olmadan itilen mesaj)
        await server.BroadcastAsync(new SettingsMessage(true, 100, new() { "a.com" }, new() { "zip" }));
        var s = Assert.IsType<SettingsMessage>(await host.Receive());
        Assert.Equal(100, s.MinFileSizeKb);
    }

    [Fact]
    public async Task NoApp_Download_IsRejected_AndLaunchWasAttempted_PingGetsNoReply()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "tdl-fakeapp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var marker = Path.Combine(dir, "launched.txt");
        var fake = Path.Combine(dir, "FakeApp.cmd");
        File.WriteAllText(fake, $"@echo %* > \"{marker}\"\r\n");
        try
        {
            using var host = new HostProc(pipe, fake, startTimeoutMs: 800);
            await host.Send(new PingMessage());
            await host.Send(new DownloadMessage("r2", "https://x/b.zip", null, null, null, null, null, 0));

            // Yalnızca download yanıtı gelmeli (ping sessiz düşer).
            var reply = Assert.IsType<DownloadResultMessage>(await host.Receive());
            Assert.Equal("r2", reply.RequestId);
            Assert.False(reply.Accepted);

            Assert.True(SpinWait.SpinUntil(() => File.Exists(marker), 5000), "Sahte App başlatılmadı.");
            Assert.Contains("--background", File.ReadAllText(marker));

            host.P.StandardInput.Close();
            Assert.True(host.P.WaitForExit(10000));
            Assert.Equal(0, host.P.ExitCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task NoApp_MissingExe_DoesNotHang_RejectsDownload()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        using var host = new HostProc(pipe, Path.Combine(Path.GetTempPath(), "yok-" + Guid.NewGuid().ToString("N") + ".exe"));
        await host.Send(new DownloadMessage("r3", "https://x/c.zip", null, null, null, null, null, 0));
        var reply = Assert.IsType<DownloadResultMessage>(await host.Receive(5000));
        Assert.False(reply.Accepted);
    }

    [Fact]
    public async Task OversizedInput_IsRejected_HostKeepsWorking()
    {
        var pipe = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        await using var server = new IpcServer(pipe)
        {
            MessageHandler = (m, _) => Task.FromResult<IpcMessage?>(m is PingMessage ? new PongMessage("1.0.0") : null)
        };
        server.Start();

        using var host = new HostProc(pipe);
        var stdin = host.P.StandardInput.BaseStream;
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)NativeMessaging.MaxMessageBytes + 1);
        await stdin.WriteAsync(header);
        await stdin.WriteAsync(new byte[NativeMessaging.MaxMessageBytes + 1]);
        await stdin.FlushAsync();

        await host.Send(new PingMessage());
        Assert.IsType<PongMessage>(await host.Receive());
    }
}
