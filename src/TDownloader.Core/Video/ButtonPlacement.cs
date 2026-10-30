using TDownloader.Core.Ipc;

namespace TDownloader.Core.Video;

public readonly record struct PxRect(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>Düğme konumu sonucu (ekran piksel koordinatı). Visible=false ise gizle.</summary>
public readonly record struct PanelPlacement(bool Visible, int X, int Y, string Reason = "")
{
    public static PanelPlacement Hidden(string why) => new(false, 0, 0, why);
}

/// <summary>
/// "Bu videoyu indir" panelinin ekran konumunu hesaplayan saf fonksiyonlar (UI bağımlılığı yok).
/// Eklentiden gelen rect: sayfa-istemci CSS pikseli; browserWindow: ekran pikseli; viewport: CSS pikseli.
/// </summary>
public static class ButtonPlacement
{
    /// <summary>
    /// Tarayıcı istemci (sayfa) alanının ekrandaki dikdörtgeni. Yatay kenarlıklar simetrik varsayılır
    /// (pencere genişliği - viewport*dpr)/2; üst çubuk (sekme+adres) = pencere yüksekliği - viewport*dpr - alt kenarlık.
    /// </summary>
    public static PxRect ClientArea(WindowRectInfo win, SizeInfo viewport, double dpr)
    {
        if (dpr <= 0) dpr = 1;
        double vw = viewport.Width * dpr, vh = viewport.Height * dpr;
        double border = (win.Width - vw) / 2;
        // Yan panel (devtools) vb. durumlarda tahmin bozulur: makul olmayan değerde varsayılan görünmez kenarlık.
        if (border < 0) border = 0;
        if (border > 24 * dpr) border = 0;
        double left = win.Left + border;
        double top = win.Top + Math.Max(0, win.Height - vh - border);
        return new PxRect(left, top, left + vw, top + vh);
    }

    public static PxRect VideoOnScreen(PxRect client, RectInfo rect, double dpr)
    {
        if (dpr <= 0) dpr = 1;
        return new PxRect(client.Left + rect.Left * dpr, client.Top + rect.Top * dpr,
                          client.Left + rect.Right * dpr, client.Top + rect.Bottom * dpr);
    }

    /// <summary>
    /// Varsayılan: panelin sağ kenarı videonun sağ kenarında, alt kenarı videonun üst kenarının 1 px üstünde.
    /// offX/offY: kullanıcının sürüklemeyle verdiği ofset (px). Üstte yer yoksa viewport üstüne kıstırılır;
    /// kıstırılmış panel videonun üst yarısına sığmıyorsa (ve video üstü görünür alanın üstündeyse) gizlenir.
    /// </summary>
    public static PanelPlacement Compute(VideoGeometryMessage g, double panelW, double panelH, double offX = 0, double offY = 0)
    {
        if (!g.Visible) return PanelPlacement.Hidden("görünür değil");
        if (g.Fullscreen) return PanelPlacement.Hidden("tam ekran");
        double dpr = g.DevicePixelRatio > 0 ? g.DevicePixelRatio : 1;
        var c = ClientArea(g.BrowserWindow, g.Viewport, dpr);
        var v = VideoOnScreen(c, g.Rect, dpr);
        if (v.Width <= 0 || v.Height <= 0) return PanelPlacement.Hidden("boş video");
        if (v.Right <= c.Left || v.Left >= c.Right || v.Bottom <= c.Top || v.Top >= c.Bottom)
            return PanelPlacement.Hidden("video görünür alan dışında");
        if (v.Width < panelW * 0.5) return PanelPlacement.Hidden("video çok dar");

        double right = Math.Min(v.Right + offX, c.Right);
        double x = right - panelW;
        if (x < c.Left) x = c.Left;

        double y = v.Top - panelH - 1 + offY;
        if (y < c.Top)
        {
            y = c.Top;
            bool videoTopInView = v.Top >= c.Top;
            bool fitsUpperHalf = y + panelH <= v.Top + v.Height / 2;
            if (!videoTopInView && !fitsUpperHalf) return PanelPlacement.Hidden("sığmıyor");
        }
        if (y + panelH > c.Bottom) y = c.Bottom - panelH;
        if (y < c.Top) return PanelPlacement.Hidden("sığmıyor");

        return new PanelPlacement(true, (int)Math.Round(x), (int)Math.Round(y));
    }

    /// <summary>Sürükleme sonrası ofset: offX = panelSağ - videoSağ, offY = panelAlt + 1 - videoÜst.</summary>
    public static (int X, int Y) OffsetFromDrag(VideoGeometryMessage g, int panelX, int panelY, int panelW, int panelH)
    {
        double dpr = g.DevicePixelRatio > 0 ? g.DevicePixelRatio : 1;
        var v = VideoOnScreen(ClientArea(g.BrowserWindow, g.Viewport, dpr), g.Rect, dpr);
        return ((int)Math.Round(panelX + panelW - v.Right), (int)Math.Round(panelY + panelH + 1 - v.Top));
    }

    /// <summary>Panel boyutu: yükseklik 26/39/52 (%100/150/200 DPI).</summary>
    public static int PanelHeight(double dpiScale) => dpiScale < 1.5 ? 26 : dpiScale < 2.0 ? 39 : 52;
}
