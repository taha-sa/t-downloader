using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Tests;

public class SchedulerCoreTests
{
    private readonly AppSettings _set = new() { Scheduler = new SchedulerSettings { Enabled = true, StartTime = "02:00", OnFinished = "notify" } };
    private readonly List<DownloadItem> _items = new();
    private int _startAll;
    private readonly List<string> _notes = new();
    private int _exit;

    private SchedulerCore Make()
    {
        var c = new SchedulerCore(() => _set, () => _items, () => _startAll++);
        c.Notify += _notes.Add;
        c.ExitRequested += () => _exit++;
        return c;
    }
    private static DownloadItem Item(DownloadStatus st) => new() { Url = "http://x", FileName = "f", Status = st };
    private static DateTime D(int day, int h, int m, int s = 0) => new(2026, 3, day, h, m, s);   // 2026-03-02 Pazartesi

    [Fact]
    public void FiresAtExactMinute_OnlyOnce()
    {
        _items.Add(Item(DownloadStatus.Paused));
        var c = Make();
        c.Tick(D(2, 1, 59));
        c.Tick(D(2, 2, 0, 5));
        c.Tick(D(2, 2, 0, 20));
        Assert.Equal(1, _startAll);
    }

    [Fact]
    public void MissedStart_DuringSleep_IsCompensated()
    {
        _items.Add(Item(DownloadStatus.Paused));
        var c = Make();
        c.Tick(D(2, 1, 0));
        c.Tick(D(2, 5, 30));      // uykudan uyanış
        Assert.Equal(1, _startAll);
    }

    [Fact]
    public void MissedStart_AppStartedShortlyAfter_IsCompensated_ButNotLongAfter()
    {
        _items.Add(Item(DownloadStatus.Paused));
        var c = Make(); c.Tick(D(2, 2, 10));
        Assert.Equal(1, _startAll);

        var c2 = Make(); c2.Tick(D(3, 9, 0));
        Assert.Equal(1, _startAll);
    }

    [Fact]
    public void NothingToRun_DoesNotStartWatching()
    {
        _items.Add(Item(DownloadStatus.Completed));
        var c = Make();
        c.Tick(D(2, 1, 59)); c.Tick(D(2, 2, 0, 1));
        Assert.Equal(0, _startAll);
        Assert.False(c.Watching);
        c.Tick(D(2, 2, 1));
        Assert.Empty(_notes);
    }

    [Fact]
    public void OldCompletedItems_AreNotCounted()
    {
        var old = Item(DownloadStatus.Completed);
        var p = Item(DownloadStatus.Paused);
        _items.AddRange(new[] { old, p });
        var c = Make();
        c.WatchQueue();
        p.Status = DownloadStatus.Failed;      // hiçbiri yeni tamamlanmadı
        c.Tick(D(2, 12, 0));
        Assert.Empty(_notes);

        p.Status = DownloadStatus.Paused; c.WatchQueue();
        p.Status = DownloadStatus.Completed;
        c.Tick(D(2, 12, 1));
        Assert.Single(_notes);
    }

    [Fact]
    public void ExitAction_CountsDown_AndCanBeCancelled()
    {
        _set.Scheduler.OnFinished = "exit";
        var p = Item(DownloadStatus.Paused); _items.Add(p);
        var c = Make(); c.WatchQueue();
        p.Status = DownloadStatus.Completed;
        c.Tick(D(2, 12, 0));
        Assert.True(c.ExitPending);
        c.Tick(D(2, 12, 0, 10));
        Assert.Equal(0, _exit);
        c.CancelExit();
        c.Tick(D(2, 12, 1));
        Assert.Equal(0, _exit);

        var q = Item(DownloadStatus.Paused); _items.Add(q);
        c.WatchQueue(); q.Status = DownloadStatus.Completed;
        c.Tick(D(2, 13, 0));
        c.Tick(D(2, 13, 0, 31));
        Assert.Equal(1, _exit);
    }
}
