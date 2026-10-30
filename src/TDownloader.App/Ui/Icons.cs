using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TDownloader.App.Ui;

/// <summary>Kodla çizilen vektör simgeler (24x24 tasarım ızgarası, istenen boyuta ölçeklenir).</summary>
public static class Icons
{
    private static readonly Dictionary<string, Bitmap> Cache = new();

    public static Bitmap Get(string name, int size)
    {
        var key = name + size;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var b)) return b;
            b = new Bitmap(size, size);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.ScaleTransform(size / 24f, size / 24f);
                Draw(g, name);
            }
            Cache[key] = b;
            return b;
        }
    }

    static Pen N(float w = 2f) => new(Theme.Navy, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    static Pen T(float w = 2.2f) => new(Theme.Teal, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
    static SolidBrush NB => new(Theme.Navy);
    static SolidBrush TB => new(Theme.Teal);

    static void Draw(Graphics g, string name)
    {
        switch (name)
        {
            case "add":
                using (var p = N()) g.DrawEllipse(p, 3, 3, 18, 18);
                using (var p = T(2.6f)) { g.DrawLine(p, 12, 7.5f, 12, 16.5f); g.DrawLine(p, 7.5f, 12, 16.5f, 12); }
                break;
            case "resume":
                g.FillPolygon(TB, new PointF[] { new(7, 4), new(20, 12), new(7, 20) });
                using (var p = N(1.6f)) g.DrawPolygon(p, new PointF[] { new(7, 4), new(20, 12), new(7, 20) });
                break;
            case "stop":
                g.FillRectangle(NB, 5, 5, 14, 14);
                using (var p = T(2f)) g.DrawRectangle(p, 5, 5, 14, 14);
                break;
            case "stopall":
                using (var p = N(1.8f)) g.DrawRectangle(p, 3, 3, 12, 12);
                g.FillRectangle(NB, 9, 9, 12, 12);
                using (var p = T(1.8f)) g.DrawRectangle(p, 9, 9, 12, 12);
                break;
            case "delete":
                DrawTrash(g, 0, 0, 1f);
                break;
            case "deletedone":
                DrawTrash(g, -2, 0, 0.82f);
                using (var p = T(2.8f)) g.DrawLines(p, new PointF[] { new(13, 15), new(16.5f, 18.5f), new(22, 10.5f) });
                break;
            case "options":
                DrawGear(g);
                break;
            case "scheduler":
                using (var p = N()) g.DrawEllipse(p, 3, 3, 18, 18);
                using (var p = T()) g.DrawLines(p, new PointF[] { new(12, 7), new(12, 12), new(16, 14) });
                break;
            case "queuestart":
                DrawList(g);
                g.FillPolygon(TB, new PointF[] { new(14, 12), new(22, 17), new(14, 22) });
                break;
            case "queuestop":
                DrawList(g);
                g.FillRectangle(TB, 14.5f, 13.5f, 7, 7);
                break;
            case "addon":
                using (var p = N(1.9f)) g.DrawRectangle(p, 4, 9, 16, 11);
                g.FillEllipse(TB, 9.5f, 3.5f, 6, 6);
                using (var p = N(1.9f)) g.DrawEllipse(p, 9.5f, 3.5f, 6, 6);
                g.FillRectangle(TB, 8, 12, 8, 5);
                break;
            case "about":
                using (var p = N()) g.DrawEllipse(p, 3, 3, 18, 18);
                g.FillEllipse(TB, 10.8f, 6.2f, 2.6f, 2.6f);
                using (var p = T(2.6f)) g.DrawLine(p, 12, 10.5f, 12, 17);
                break;
            case "folder":
                g.FillPolygon(new SolidBrush(Theme.NavyLight), new PointF[] { new(2, 6), new(9, 6), new(11, 8.5f), new(22, 8.5f), new(22, 20), new(2, 20) });
                g.FillRectangle(TB, 2, 11, 20, 9);
                break;
            case "unfinished":
                using (var p = N()) g.DrawEllipse(p, 3, 3, 18, 18);
                g.FillPie(TB, 4, 4, 16, 16, -90, 220);
                break;
            case "finished":
                g.FillEllipse(TB, 2.5f, 2.5f, 19, 19);
                using (var p = new Pen(Color.White, 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new PointF[] { new(7, 12.5f), new(10.5f, 16), new(17, 8.5f) });
                break;
            case "queues":
                DrawList(g);
                break;
            case "all":
                g.FillRectangle(NB, 3, 4, 18, 4);
                g.FillRectangle(TB, 3, 10, 18, 4);
                g.FillRectangle(NB, 3, 16, 18, 4);
                break;
            case "app":
                using (var path = Rounded(1, 1, 22, 22, 5))
                using (var br = new LinearGradientBrush(new RectangleF(0, 0, 24, 24), Theme.NavyLight, Theme.Navy, 90f))
                    g.FillPath(br, path);
                using (var p = new Pen(Theme.Teal, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLine(p, 12, 4.5f, 12, 14.5f);
                    g.DrawLines(p, new PointF[] { new(7.5f, 10.5f), new(12, 15), new(16.5f, 10.5f) });
                }
                using (var p = new Pen(Color.White, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(p, 5.5f, 19f, 18.5f, 19f);
                break;
        }
    }

    static void DrawTrash(Graphics g, float dx, float dy, float s)
    {
        var st = g.Save();
        g.TranslateTransform(dx + 12 * (1 - s), dy + 12 * (1 - s));
        g.ScaleTransform(s, s);
        using (var p = N(2f))
        {
            g.DrawLine(p, 4, 6.5f, 20, 6.5f);
            g.DrawLine(p, 9.5f, 6.5f, 9.5f, 4);
            g.DrawLine(p, 9.5f, 4, 14.5f, 4);
            g.DrawLine(p, 14.5f, 4, 14.5f, 6.5f);
            g.DrawLines(p, new PointF[] { new(6, 6.5f), new(7, 20), new(17, 20), new(18, 6.5f) });
        }
        using (var p = T(1.8f)) { g.DrawLine(p, 10, 10, 10, 17); g.DrawLine(p, 14, 10, 14, 17); }
        g.Restore(st);
    }

    static void DrawGear(Graphics g)
    {
        var pts = new List<PointF>();
        for (int i = 0; i < 16; i++)
        {
            double a = i * Math.PI / 8;
            float r = (i % 2 == 0) ? 10.2f : 7.8f;
            pts.Add(new PointF(12 + (float)(Math.Cos(a - 0.12) * r), 12 + (float)(Math.Sin(a - 0.12) * r)));
            pts.Add(new PointF(12 + (float)(Math.Cos(a + 0.12) * r), 12 + (float)(Math.Sin(a + 0.12) * r)));
        }
        g.FillPolygon(NB, pts.ToArray());
        g.FillEllipse(TB, 8, 8, 8, 8);
        g.FillEllipse(Brushes.White, 10.4f, 10.4f, 3.2f, 3.2f);
    }

    static void DrawList(Graphics g)
    {
        using var p = N(2f);
        g.DrawLine(p, 3, 6, 19, 6);
        g.DrawLine(p, 3, 12, 11, 12);
        g.DrawLine(p, 3, 18, 11, 18);
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private static Icon? _app;
    public static Icon AppIcon()
    {
        if (_app != null) return _app;
        using var bmp = Get("app", 32);
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        _app = (Icon)tmp.Clone();
        DestroyIcon(h);
        return _app;
    }
}
