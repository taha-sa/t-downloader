using TDownloader.App.Ui;

namespace TDownloader.Host.Tests;

public class CompletedLogicTests
{
    private static CompletedEntry E(string id) => new(id, id + ".zip", "https://x/" + id, @"C:\d\" + id + ".zip", 1000);

    [Fact]
    public void Queue_SinglePage_NoPager_NewestShown_DuplicatesIgnored()
    {
        var q = new CompletedQueue();
        Assert.True(q.Add(E("a")));
        Assert.Equal("", q.Pager);
        Assert.False(q.Add(E("a")));
        Assert.True(q.Add(E("b")));
        Assert.True(q.Add(E("c")));
        Assert.Equal("3/3", q.Pager);
        Assert.Equal("c", q.Current!.Id);
        Assert.True(q.Prev()); Assert.Equal("2/3", q.Pager);
        Assert.True(q.Prev()); Assert.False(q.Prev());
        Assert.Equal("a", q.Current!.Id);
        Assert.True(q.Next()); Assert.True(q.Next()); Assert.False(q.Next());
        q.Clear();
        Assert.Null(q.Current); Assert.Equal(0, q.Count);
    }

    [Fact]
    public void SizeLine_TwoDecimals_AndThousandsSeparatedBytes()
    {
        Assert.Equal("İndirildi: 9.90 MB (10.385.264 bayt)", CompletedText.SizeLine(10_385_264));
        Assert.Equal("İndirildi: 512 B (512 bayt)", CompletedText.SizeLine(512));
        Assert.Equal("İndirildi", CompletedText.SizeLine(-1));
    }

    [Theory]
    [InlineData(@"C:\Users\hp\Downloads\T-Downloader\Sıkıştırılmış\cok-uzun-bir-dosya-adi-ornek.zip", 30)]
    [InlineData("https://example.com/a/very/long/path/file.zip?token=abcdefghijklmnop", 24)]
    public void MiddleEllipsis_FitsAndKeepsHeadAndTail(string text, int max)
    {
        var r = CompletedText.MiddleEllipsis(text, s => s.Length <= max);
        Assert.True(r.Length <= max);
        Assert.Contains("…", r);
        Assert.StartsWith(text[..3], r);
        Assert.EndsWith(text[^3..], r);
    }

    [Fact]
    public void MiddleEllipsis_ShortTextUnchanged_AndNeverEmpty()
    {
        Assert.Equal("abc", CompletedText.MiddleEllipsis("abc", s => s.Length <= 10));
        Assert.Equal("…", CompletedText.MiddleEllipsis("abcdefgh", s => s.Length <= 1));
    }

    private sealed class Item { public string Id = ""; public int N; }

    [Fact]
    public void ViewIndex_FilterSortAppend_IndexOf()
    {
        var items = Enumerable.Range(1, 6).Select(i => new Item { Id = "i" + i, N = i }).ToList();
        var v = new ViewIndex<Item>(x => x.Id);
        v.Rebuild(items, x => x.N % 2 == 0, Comparer<Item>.Create((a, b) => b.N.CompareTo(a.N)));
        Assert.Equal(3, v.Count);
        Assert.Equal("i6", v[0].Id);
        Assert.Equal(2, v.IndexOf("i2"));
        Assert.Equal(-1, v.IndexOf("i1"));
        Assert.True(v.Append(items[0]));
        Assert.False(v.Append(items[0]));
        Assert.Equal(3, v.IndexOf("i1"));
        v.Resort(Comparer<Item>.Create((a, b) => a.N.CompareTo(b.N)));
        Assert.Equal(0, v.IndexOf("i1"));
        Assert.True(v.Contains("i4"));
    }
}
