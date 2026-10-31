using TDownloader.Core.Util;
using Xunit;

namespace TDownloader.Core.Tests;

public class LatestCoalescerTests
{
    [Fact]
    public void Flood_collapses_to_one_schedule_and_latest_value_per_key()
    {
        var c = new LatestCoalescer<int, string>();
        int schedules = 0;
        for (int i = 0; i < 100_000; i++)
            if (c.Offer(i % 2, "v" + i)) schedules++;
        Assert.Equal(1, schedules);
        Assert.Equal(2, c.Pending);
        var got = c.Drain();
        Assert.Equal(new[] { "v99998", "v99999" }, got);
        Assert.Equal(0, c.Pending);
    }

    [Fact]
    public void After_drain_next_offer_schedules_again()
    {
        var c = new LatestCoalescer<int, int>();
        Assert.True(c.Offer(1, 1));
        Assert.False(c.Offer(1, 2));
        Assert.Equal(new[] { 2 }, c.Drain());
        Assert.True(c.Offer(1, 3));
        Assert.Equal(new[] { 3 }, c.Drain());
    }

    [Fact]
    public void Drain_on_empty_returns_empty()
    {
        Assert.Empty(new LatestCoalescer<int, int>().Drain());
    }
}
