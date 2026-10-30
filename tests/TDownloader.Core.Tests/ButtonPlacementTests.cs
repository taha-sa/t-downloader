using TDownloader.Core.Ipc;
using TDownloader.Core.Video;

namespace TDownloader.Core.Tests;

public class ButtonPlacementTests
{
    // 1200x800 pencere, 8 px görünmez kenarlık: viewport 1184x700 CSS (dpr 1) -> üst çubuk 800-700-8 = 92 px.
    private static VideoGeometryMessage G(double dpr, WindowRectInfo win, SizeInfo vp, RectInfo rect, bool visible = true, bool fs = false) =>
        new(1, 1, visible, fs, rect, dpr, vp, win, "https://ornek.test/v", "t");

    [Fact]
    public void Dpi100_RightAlignedOneAboveVideoTop()
    {
        var g = G(1.0, new(100, 50, 1200, 800), new(1184, 700), new(100, 200, 740, 560));
        var c = ButtonPlacement.ClientArea(g.BrowserWindow, g.Viewport, 1.0);
        Assert.Equal(108, c.Left); Assert.Equal(142, c.Top); Assert.Equal(1292, c.Right); Assert.Equal(842, c.Bottom);
        var p = ButtonPlacement.Compute(g, 120, 26);
        Assert.True(p.Visible);
        // video sağ = 108+740 = 848, üst = 142+200 = 342
        Assert.Equal(848 - 120, p.X);
        Assert.Equal(342 - 26 - 1, p.Y);
    }

    [Fact]
    public void Dpi150_ScalesRectAndBorders()
    {
        // viewport 1184x700 CSS * 1.5 = 1776x1050; pencere 1800x1200 -> kenarlık 12, üst çubuk 1200-1050-12 = 138
        var g = G(1.5, new(150, 75, 1800, 1200), new(1184, 700), new(100, 200, 740, 560));
        var p = ButtonPlacement.Compute(g, 117, 39);
        Assert.True(p.Visible);
        double clientLeft = 162, clientTop = 75 + 138;
        Assert.Equal((int)(clientLeft + 740 * 1.5) - 117, p.X);
        Assert.Equal((int)(clientTop + 200 * 1.5) - 39 - 1, p.Y);
    }

    [Fact]
    public void Dpi200_ScalesRectAndBorders()
    {
        // 1184x700 * 2 = 2368x1400; pencere 2400x1500 -> kenarlık 16, üst çubuk 1500-1400-16 = 84
        var g = G(2.0, new(0, 0, 2400, 1500), new(1184, 700), new(100, 200, 740, 560));
        var p = ButtonPlacement.Compute(g, 156, 52);
        Assert.True(p.Visible);
        Assert.Equal(16 + 1480 - 156, p.X);
        Assert.Equal(84 + 400 - 52 - 1, p.Y);
    }

    [Fact]
    public void NoRoomAbove_ClampsToViewportTop()
    {
        // video üstü viewport üstüne 10 px mesafede: panel (26+1) yukarı sığmaz -> kıstır, video üstü görünür -> kabul
        var g = G(1.0, new(100, 50, 1200, 800), new(1184, 700), new(100, 10, 740, 400));
        var p = ButtonPlacement.Compute(g, 120, 26);
        Assert.True(p.Visible);
        Assert.Equal(142, p.Y); // istemci üstü
    }

    [Fact]
    public void VideoScrolledAbove_PanelInUpperHalf_ClampedAndShown()
    {
        // video üstü viewport'un 100 px üstünde, yüksekliği 400 -> panel (26) üst yarıya sığar
        var g = G(1.0, new(100, 50, 1200, 800), new(1184, 700), new(100, -100, 740, 300));
        var p = ButtonPlacement.Compute(g, 120, 26);
        Assert.True(p.Visible);
        Assert.Equal(142, p.Y);
    }

    [Fact]
    public void DoesNotFit_Hides()
    {
        // video üstü viewport üstünün 350 px üstünde; görünür kısım yalnız alt 50 px -> panel üst yarıya sığmaz
        var g = G(1.0, new(100, 50, 1200, 800), new(1184, 700), new(100, -350, 740, 50));
        var p = ButtonPlacement.Compute(g, 120, 26);
        Assert.False(p.Visible);
    }

    [Fact]
    public void FullscreenInvisibleOrOffscreen_Hide()
    {
        Assert.False(ButtonPlacement.Compute(G(1, new(0, 0, 1200, 800), new(1184, 700), new(100, 200, 740, 560), fs: true), 120, 26).Visible);
        Assert.False(ButtonPlacement.Compute(G(1, new(0, 0, 1200, 800), new(1184, 700), new(100, 200, 740, 560), visible: false), 120, 26).Visible);
        Assert.False(ButtonPlacement.Compute(G(1, new(0, 0, 1200, 800), new(1184, 700), new(100, 900, 740, 1200)), 120, 26).Visible);
        Assert.False(ButtonPlacement.Compute(G(1, new(0, 0, 1200, 800), new(1184, 700), new(100, 200, 130, 260)), 120, 26).Visible); // çok dar
    }

    [Fact]
    public void Offset_AndDragRoundTrip()
    {
        var g = G(1.0, new(100, 50, 1200, 800), new(1184, 700), new(100, 200, 740, 560));
        var def = ButtonPlacement.Compute(g, 120, 26);
        var moved = ButtonPlacement.Compute(g, 120, 26, -40, 30);
        Assert.Equal(def.X - 40, moved.X);
        Assert.Equal(def.Y + 30, moved.Y);
        var (ox, oy) = ButtonPlacement.OffsetFromDrag(g, moved.X, moved.Y, 120, 26);
        Assert.Equal(-40, ox); Assert.Equal(30, oy);
    }

    [Fact]
    public void RightEdgeClampedToViewport_AndLeftClamp()
    {
        var g = G(1.0, new(100, 50, 1200, 800), new(1184, 700), new(600, 200, 1500, 560)); // video viewport sağını aşıyor
        var p = ButtonPlacement.Compute(g, 120, 26);
        Assert.Equal(1292 - 120, p.X);
        var g2 = G(1.0, new(100, 50, 1200, 800), new(1184, 700), new(0, 200, 200, 560));
        Assert.Equal(108, ButtonPlacement.Compute(g2, 300, 26, 0, 0).X); // sol sınıra kıstırma
    }

    [Theory]
    [InlineData(1.0, 26)]
    [InlineData(1.25, 26)]
    [InlineData(1.5, 39)]
    [InlineData(1.75, 39)]
    [InlineData(2.0, 52)]
    [InlineData(3.0, 52)]
    public void PanelHeight_Buckets(double scale, int h) => Assert.Equal(h, ButtonPlacement.PanelHeight(scale));
}
