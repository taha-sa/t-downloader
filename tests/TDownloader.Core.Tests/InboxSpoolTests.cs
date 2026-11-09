using TDownloader.Core.Ipc;

namespace TDownloader.Core.Tests;

public class InboxSpoolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tdl-inbox-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DownloadMessage Msg(string id, string url = "https://x/a.zip") =>
        new(id, url, "https://ref/", "a=b", "UA", "a.zip", "application/zip", 1234);

    [Fact]
    public void WriteThenDrain_DeliversAndDeletes()
    {
        InboxSpool.Write(_dir, Msg("r1"));
        InboxSpool.Write(_dir, Msg("r2", "https://x/b.zip"));
        var got = new List<DownloadMessage>();
        int n = InboxSpool.Drain(_dir, m => { got.Add(m); return true; });
        Assert.Equal(2, n);
        Assert.Equal(new[] { "r1", "r2" }, got.Select(m => m.RequestId).OrderBy(x => x));
        Assert.Equal("a=b", got[0].Cookies);                       // çerez/referrer/UA taşınır
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Drain_KeepsFileWhenHandlerFails_AndIgnoresTmp()
    {
        var path = InboxSpool.Write(_dir, Msg("r1"));
        File.WriteAllText(Path.Combine(_dir, "half.tmp"), "{ yarım");
        Assert.Equal(0, InboxSpool.Drain(_dir, _ => false));
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_dir, "half.tmp")));
        Assert.Equal(1, InboxSpool.Drain(_dir, _ => true));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Drain_RemovesStaleAndCorrupt()
    {
        InboxSpool.Write(_dir, Msg("old"));
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "not json");
        var seen = new List<string>();
        int n = InboxSpool.Drain(_dir, m => { seen.Add(m.RequestId); return true; }, DateTime.UtcNow.AddDays(2));
        Assert.Equal(0, n);
        Assert.Empty(seen);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Drain_MissingDirectory_IsNoop() => Assert.Equal(0, InboxSpool.Drain(_dir + "-none", _ => true));
}
