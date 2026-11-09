using TDownloader.Core.Util;

namespace TDownloader.Core.Tests;

public class DebouncerTests
{
    private static async Task Until(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!cond() && DateTime.UtcNow < until) await Task.Delay(10);
    }

    [Fact]
    public async Task ManyTriggers_RunOnce_OffCallerThread()
    {
        int runs = 0; int? thread = null;
        var d = new Debouncer(TimeSpan.FromMilliseconds(60), () => { Interlocked.Increment(ref runs); thread = Environment.CurrentManagedThreadId; });
        for (int i = 0; i < 100; i++) d.Trigger();
        Assert.True(d.IsPending);
        await Until(() => Volatile.Read(ref runs) >= 1);
        Assert.Equal(1, runs);
        Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
        d.Trigger();
        await Until(() => Volatile.Read(ref runs) >= 2);
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Flush_RunsPendingImmediately_ThenTimerDoesNotRunAgain()
    {
        int runs = 0;
        var d = new Debouncer(TimeSpan.FromMilliseconds(80), () => Interlocked.Increment(ref runs));
        d.Trigger();
        d.Flush();
        Assert.Equal(1, runs);
        Thread.Sleep(250);
        Assert.Equal(1, runs);
        d.Flush();                                   // bekleyen yok: çalışmaz
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ActionException_IsSwallowed()
    {
        var d = new Debouncer(TimeSpan.FromMilliseconds(10), () => throw new IOException("disk"));
        d.Trigger();
        await Until(() => !d.IsPending);
        Assert.False(d.IsPending);
    }
}
