using TDownloader.Core.Ipc;

namespace TDownloader.Core.Tests;

public class InboxSpoolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tdl-inbox-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DownloadMessage Msg(string id, string url = "https://x/a.zip") =>
        new(id, url, "https://ref/", "a=b", "UA", "a.zip", "application/zip", 1234);

    [Fact]
    public void Write_ProtectsCookiesOnDisk_AndDrainStillReadsThem()
    {
        var path = InboxSpool.Write(_dir, Msg("r1") with { Cookies = "sid=SECRETVALUE" });
        var raw = File.ReadAllText(path);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(InboxSpool.ProtectedPrefix, raw);
            Assert.DoesNotContain("SECRETVALUE", raw);
        }
        DownloadMessage? got = null;
        Assert.Equal(1, InboxSpool.Drain(_dir, m => { got = m; return true; }));
        Assert.Equal("sid=SECRETVALUE", got!.Cookies);
    }

    [Fact]
    public void Drain_ReadsLegacyPlainJsonFiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "old.json"), IpcSerializer.Serialize(Msg("old") with { Cookies = "k=v" }));
        DownloadMessage? got = null;
        Assert.Equal(1, InboxSpool.Drain(_dir, m => { got = m; return true; }));
        Assert.Equal("k=v", got!.Cookies);
    }

    [Fact]
    public void Drain_MovesUndecryptableFileToFailed()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bad.json"), InboxSpool.ProtectedPrefix + "AAAA");
        Assert.Equal(0, InboxSpool.Drain(_dir, _ => true));
        Assert.True(File.Exists(Path.Combine(InboxSpool.FailedDirOf(_dir), "bad.json")));
    }

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
        var notified = new List<string>();
        int n = InboxSpool.Drain(_dir, m => { seen.Add(m.RequestId); return true; }, DateTime.UtcNow.AddDays(2), (p, _) => notified.Add(p));
        Assert.Equal(0, n);
        Assert.Empty(seen);
        Assert.Empty(Directory.GetFiles(_dir));                       // sessizce silinmez: failed\ altına taşınır
        Assert.Equal(2, Directory.GetFiles(InboxSpool.FailedDirOf(_dir), "*.json").Length);
        Assert.Equal(2, notified.Count);
    }

    [Fact]
    public void Drain_KeepsFileWhilePending_AndIsIdempotent()
    {
        var path = InboxSpool.Write(_dir, Msg("r1"));
        int calls = 0;
        // Pending (iş sürüyor): dosya diskte kalır, silinmez.
        Assert.Equal(0, InboxSpool.Drain(_dir, (_, _) => { calls++; return DrainAction.Pending; }));
        Assert.True(File.Exists(path));
        // Çökme sonrası yeniden boşaltma: aynı dosya tekrar sunulur, sonunda bir kez silinir.
        Assert.Equal(1, InboxSpool.Drain(_dir, (_, _) => { calls++; return DrainAction.Done; }));
        Assert.Equal(0, InboxSpool.Drain(_dir, (_, _) => { calls++; return DrainAction.Done; }));
        Assert.Equal(2, calls);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void MoveToFailed_KeepsFileWithReason_AndRetryBringsItBack()
    {
        var path = InboxSpool.Write(_dir, Msg("r1"));
        var dest = InboxSpool.MoveToFailed(path, "ekleme hatası");
        Assert.NotNull(dest);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(dest));
        Assert.Contains("ekleme hatası", File.ReadAllText(Path.ChangeExtension(dest!, ".reason.txt")));
        Assert.Equal(1, InboxSpool.RetryFailed(_dir));
        Assert.True(File.Exists(path));
        Assert.Equal(1, InboxSpool.Drain(_dir, _ => true, DateTime.UtcNow.AddHours(1)));   // yaş sayacı sıfırlandı
    }

    [Fact]
    public void Drain_FailedAction_MovesToFailed()
    {
        var path = InboxSpool.Write(_dir, Msg("r1"));
        Assert.Equal(0, InboxSpool.Drain(_dir, (_, _) => DrainAction.Failed));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(InboxSpool.FailedDirOf(_dir), "*.json"));
    }

    [Fact]
    public void Drain_MissingDirectory_IsNoop() => Assert.Equal(0, InboxSpool.Drain(_dir + "-none", _ => true));
}
