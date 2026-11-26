using TDownloader.App.Ui;

namespace TDownloader.Host.Tests;

public class AppUiLogicTests
{
    [Theory]
    [InlineData(0.2, "1 sn")]
    [InlineData(9, "9 sn")]
    [InlineData(59, "59 sn")]
    [InlineData(60, "1 dk 0 sn")]
    [InlineData(125, "2 dk 5 sn")]
    [InlineData(3599, "59 dk 59 sn")]
    [InlineData(3600, "1 sa 0 dk")]
    [InlineData(5400, "1 sa 30 dk")]
    [InlineData(90000, "25 sa 0 dk")]
    public void Eta_Formats(double sec, string expected) => Assert.Equal(expected, Fmt.Eta(TimeSpan.FromSeconds(sec)));

    [Fact] public void Eta_Null_Empty() => Assert.Equal("", Fmt.Eta(null));

    [Fact]
    public void UpdateGate_AllowsOncePerSecond()
    {
        var g = new UpdateGate();
        Assert.True(g.Due(1000));
        Assert.False(g.Due(1250));
        Assert.False(g.Due(1999));
        Assert.True(g.Due(2000));
        g.Reset();
        Assert.True(g.Due(2100));
    }

    [Fact]
    public void DiskDelete_SumsExistingOnly()
    {
        var sizes = new Dictionary<string, long> { ["a"] = 1024, ["b"] = 2048 };
        long total = DiskDeletePlan.TotalExistingSize(new string?[] { "a", "b", "missing", null, "" }, p => sizes.TryGetValue(p, out var v) ? v : null);
        Assert.Equal(3072, total);
    }

    [Fact]
    public void DiskDelete_ConfirmText()
    {
        var t = DiskDeletePlan.ConfirmText(3, 5L * 1024 * 1024);
        Assert.Equal("3 dosya diskten silinecek (toplam 5 MB). Dosyalar Geri Dönüşüm Kutusu'na gönderilecek; kutuya gönderilemeyen dosya için ayrıca sorulur (sessizce kalıcı silinmez). Emin misiniz?", t);
    }
}
