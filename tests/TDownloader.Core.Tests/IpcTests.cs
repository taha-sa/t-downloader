using TDownloader.Core.Ipc;

namespace TDownloader.Core.Tests;

public class IpcTests
{
    public static IEnumerable<object[]> Messages() => new[]
    {
        new object[] { new PingMessage() },
        new object[] { new PongMessage("1.2.3") },
        new object[] { new DownloadMessage("r1", "https://x/a.zip", "https://x/", "a=b", "UA", "a.zip", "application/zip", 12345) },
        new object[] { new DownloadResultMessage("r1", true) },
        new object[] { new MediaMessage(5, 0, "https://p/", "Başlık", new() { new MediaItem("https://m/v.m3u8", "application/x-mpegURL", "hls") }) },
        new object[] { new VideoGeometryMessage(5, 9, true, false, new RectInfo(1, 2, 3, 4), 1.5, new SizeInfo(800, 600),
            new WindowRectInfo(0, 0, 1280, 720), "https://p/", "T") },
        new object[] { new HideButtonMessage(7) },
        new object[] { new SettingsMessage(true, 100, new() { "a.com" }, new() { "zip", "mp4" }) },
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Message_RoundTrips(IpcMessage m)
    {
        var json = IpcSerializer.Serialize(m);
        var back = IpcSerializer.Deserialize(json);
        Assert.Equal(m.GetType(), back.GetType());
        // Liste alanları kayıt eşitliğinde referans karşılaştırıldığından JSON üzerinden karşılaştır.
        Assert.Equal(json, IpcSerializer.Serialize(back));
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void Json_UsesCamelCaseAndTypeDiscriminator()
    {
        var json = IpcSerializer.Serialize(new DownloadMessage("r", "u", null, null, null, "f", "m", 5));
        Assert.Contains("\"type\":\"download\"", json);
        Assert.Contains("\"requestId\":\"r\"", json);
        Assert.Contains("\"fileSize\":5", json);
        var res = IpcSerializer.Serialize(new DownloadResultMessage("r", false));
        Assert.Contains("\"type\":\"downloadResult\"", res);
        var geo = IpcSerializer.Serialize(new VideoGeometryMessage(1, 2, true, false, new(1, 2, 3, 4), 1, new(1, 1), new(0, 0, 1, 1), null, null));
        Assert.Contains("\"devicePixelRatio\"", geo);
        Assert.Contains("\"browserWindow\"", geo);
    }

    [Fact]
    public void Deserialize_ContractSample_Works()
    {
        var m = IpcSerializer.Deserialize("{\"type\":\"download\",\"requestId\":\"a\",\"url\":\"https://x\",\"fileSize\":9,\"filename\":\"f.zip\"}");
        var d = Assert.IsType<DownloadMessage>(m);
        Assert.Equal("f.zip", d.Filename);
        Assert.Equal(9, d.FileSize);
        Assert.Null(IpcSerializer.TryDeserialize("{\"type\":\"nope\"}"));
    }

    [Fact]
    public async Task Pipe_ServerClient_PingPong()
    {
        var name = "TDownloader.Ipc.Test." + Guid.NewGuid().ToString("N");
        await using var server = new IpcServer(name)
        {
            MessageHandler = (m, _) => Task.FromResult<IpcMessage?>(m is PingMessage ? new PongMessage("9.9.9") : null)
        };
        server.Start();
        using var client = new IpcClient(name);
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(5)));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = await client.RequestAsync(new PingMessage(), cts.Token);
        Assert.Equal("9.9.9", Assert.IsType<PongMessage>(reply).Version);
    }
}
