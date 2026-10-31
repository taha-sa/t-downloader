using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using TDownloader.Core.Video;

namespace TDownloader.App.Ui;

public enum ButtonRegion { None, Main, Help, Close }

/// <summary>
/// "Bu videoyu indir" paneli: üst düzey, her zaman üstte, etkinleşmeyen, piksel başına alfa kullanan (layered)
/// pencere. Tüm çizim GDI+ ile kodlanır (görsel kaynak yok). Fare olayları normal WndProc üzerinden gelir.
/// </summary>
public sealed class VideoButtonWindow : Form
{
    public const byte AlphaNormal = 0xE6;   // ~%90
    public const byte AlphaFaded = 0x6F;    // ~%43
    private const int FadeDelayMs = 10_000;

    // ---- Win32
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    private const int ULW_ALPHA = 2;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

    [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SZ { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] private struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst, ref SZ psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    // ---- durum
    private readonly System.Windows.Forms.Timer _fadeDelay = new() { Interval = FadeDelayMs };
    private readonly System.Windows.Forms.Timer _fadeStep = new() { Interval = 30 };
    private readonly ToolTip _tip = new() { InitialDelay = 400, ShowAlways = true };
    private int _alpha = AlphaNormal;
    private int _x, _y;
    private bool _compact;
    private ButtonRegion _hover, _down;
    private bool _dragging;
    private Point _dragStartCursor, _dragStartLoc;
    private string _mainText = "Bu videoyu indir";
    private PanelLayout _layout;

    public event Action? MainClicked;
    public event Action? HelpClicked;
    public event Action? CloseClicked;
    public event Action? ResetRequested;
    /// <summary>Sürükleme bitti: panelin yeni sol-üst ekran konumu.</summary>
    public event Action<Point>? DragCompleted;

    public Size PanelSize => new(_layout.W, _layout.H);
    public Rectangle MainRect => _layout.Main;
    public bool Compact => _compact;
    /// <summary>Menü açıkken solma/gizlenme davranışını askıya almak için.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MenuOpen { get; set; }
    public bool Dragging => _dragging;

    private readonly float _scale;

    public VideoButtonWindow(float dpiScale)
    {
        _scale = dpiScale;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.Selectable, false);
        _layout = ComputeLayout(false);
        _fadeDelay.Tick += (_, _) => { _fadeDelay.Stop(); _fadeStep.Start(); };
        _fadeStep.Tick += (_, _) =>
        {
            if (_alpha <= AlphaFaded) { _alpha = AlphaFaded; _fadeStep.Stop(); }
            else _alpha = Math.Max(AlphaFaded, _alpha - 10);
            Present();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------ yerleşim

    private readonly record struct PanelLayout(int W, int H, float U, Rectangle Main, Rectangle Help, Rectangle Close, int PlayD, int TextX, int DividerX, float FontPx, int TextW);

    private PanelLayout ComputeLayout(bool compact)
    {
        int h = ButtonPlacement.PanelHeight(_scale);
        float u = h / 26f;
        float fontPx = 13f * u;
        int tw;
        using (var f = new Font("Arial", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            tw = (int)Math.Ceiling(g.MeasureString(_mainText, f, 2000, StringFormat.GenericTypographic).Width);
        }
        int playD = (int)Math.Round(18 * u);
        int textX = (int)Math.Round(30 * u);
        int dividerX = textX + tw + (int)Math.Round(10 * u);
        int box = (int)Math.Round(16 * u);
        int x = dividerX + (int)Math.Round(7 * u);
        Rectangle help = Rectangle.Empty, close;
        if (!compact) { help = new Rectangle(x, (h - box) / 2, box, box); x += box + (int)Math.Round(2 * u); }
        close = new Rectangle(x, (h - box) / 2, box, box);
        int w = x + box + (int)Math.Round(8 * u);
        return new PanelLayout(w, h, u, new Rectangle(0, 0, dividerX, h), help, close, playD, textX, dividerX, fontPx, tw);
    }

    /// <summary>Kompakt mod ("?" gizli) — dar videolarda. Boyut değişirse true döner.</summary>
    public bool SetCompact(bool compact)
    {
        if (compact == _compact) return false;
        _compact = compact;
        _layout = ComputeLayout(compact);
        return true;
    }

    /// <summary>Kompakt olmayan tam panel genişliği.</summary>
    private int _fullWidth = -1;
    public int FullWidth => _fullWidth >= 0 ? _fullWidth : (_fullWidth = ComputeLayout(false).W);

    // ------------------------------------------------------------ çizim

    private ButtonRegion HitTest(Point p)
    {
        if (_layout.Close.Contains(p)) return ButtonRegion.Close;
        if (!_layout.Help.IsEmpty && _layout.Help.Contains(p)) return ButtonRegion.Help;
        if (_layout.Main.Contains(p)) return ButtonRegion.Main;
        return ButtonRegion.None;
    }

    /// <summary>Panelin konumunu günceller ve yeniden çizer.</summary>
    public void PlaceAt(int x, int y)
    {
        _x = x; _y = y;
        if (!IsHandleCreated) CreateHandle();
        Present();
    }

    public void ShowPanel()
    {
        if (!IsHandleCreated) CreateHandle();
        if (!Visible) { ResetAlpha(); Present(); Show(); }
        Present();
    }

    public void HidePanel()
    {
        _fadeDelay.Stop(); _fadeStep.Stop();
        _tip.Hide(this);
        if (Visible) Hide();
    }

    private void ResetAlpha()
    {
        _alpha = AlphaNormal;
        _fadeStep.Stop();
        _fadeDelay.Stop();
        if (!MenuOpen) { _fadeDelay.Start(); }
    }

    /// <summary>Çizilmiş bitmap'i UpdateLayeredWindow ile sunar.</summary>
    public void Present()
    {
        if (!IsHandleCreated) return;
        int w = _layout.W, h = _layout.H;
        var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr dib = CreateDIBSection(screen, ref bi, 0, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(bmp))
                Render(g);
            var dst = new PT { X = _x, Y = _y };
            var sz = new SZ { W = w, H = h };
            var src = new PT();
            var blend = new BLEND { Op = AC_SRC_OVER, Flags = 0, Alpha = (byte)_alpha, Format = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private void Render(Graphics g)
    {
        var L = _layout;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        bool mainHot = _hover == ButtonRegion.Main || _down == ButtonRegion.Main;
        bool mainDown = _down == ButtonRegion.Main && _hover == ButtonRegion.Main;
        float pen = Math.Max(1f, L.U);
        var body = new RectangleF(pen / 2, pen / 2, L.W - pen, L.H - pen);
        // sarı-siyah: siyah gövde; ana bölge üzerindeyken sarı zemin + siyah metin
        Color fgMain = mainHot ? Theme.Black : Theme.Text;
        Color glyph = mainHot ? Theme.TextDim : Theme.TextDim;
        using (var path = Capsule(body))
        {
            Color top = mainDown ? Theme.YellowDark : mainHot ? Theme.YellowLight : Color.FromArgb(0x26, 0x26, 0x26);
            Color bottom = mainDown ? Theme.YellowDark : mainHot ? Theme.Yellow : Theme.Black;
            using (var br = new LinearGradientBrush(new RectangleF(0, 0, L.W, L.H), top, bottom, LinearGradientMode.Vertical))
                g.FillPath(br, path);
            // sağ bölüm ("?" ve "X") hafif koyu zemin
            var clip = g.Clip;
            g.SetClip(new RectangleF(L.DividerX, 0, L.W - L.DividerX, L.H));
            using (var br = new SolidBrush(Color.FromArgb(0xB0, Theme.Black))) g.FillPath(br, path);
            g.Clip = clip;
            using var bp = new Pen(Theme.Yellow, pen);
            g.DrawPath(bp, path);
        }
        using (var dp = new Pen(Color.FromArgb(0x90, Theme.Yellow), pen))
            g.DrawLine(dp, L.DividerX, L.H * 0.2f, L.DividerX, L.H * 0.8f);

        // oynat simgesi: sarı daire + siyah üçgen (üzerindeyken siyah daire + sarı üçgen)
        float pd = L.PlayD, px = 6 * L.U, py = (L.H - pd) / 2f;
        using (var br = new SolidBrush(mainHot ? Theme.Black : Theme.Yellow)) g.FillEllipse(br, px, py, pd, pd);
        float cx = px + pd / 2f + pd * 0.06f, cy = py + pd / 2f, s = pd * 0.26f;
        using (var br = new SolidBrush(mainHot ? Theme.Yellow : Theme.Black))
            g.FillPolygon(br, new[] { new PointF(cx - s * 0.8f, cy - s), new PointF(cx + s, cy), new PointF(cx - s * 0.8f, cy + s) });

        // metin
        using (var f = new Font("Arial", L.FontPx, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var br = new SolidBrush(fgMain))
        {
            var sf = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(_mainText, f, br, new RectangleF(L.TextX, 0, L.TextW + 4, L.H), sf);
        }

        // "?" ve "X"
        if (!L.Help.IsEmpty) DrawHelp(g, L.Help, _hover == ButtonRegion.Help ? Theme.Yellow : glyph, _hover == ButtonRegion.Help, pen);
        DrawClose(g, L.Close, _hover == ButtonRegion.Close ? Theme.Danger : glyph, _hover == ButtonRegion.Close, pen);
    }

    private static GraphicsPath Capsule(RectangleF r)
    {
        var p = new GraphicsPath();
        float d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 90, 180);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        p.CloseFigure();
        return p;
    }

    private static void DrawHelp(Graphics g, Rectangle r, Color c, bool hot, float pen)
    {
        if (hot) using (var b = new SolidBrush(Color.FromArgb(0x30, c))) g.FillEllipse(b, r);
        using var p = new Pen(c, pen * 1.2f);
        var inner = Rectangle.Inflate(r, -(int)(r.Width * 0.08), -(int)(r.Height * 0.08));
        g.DrawEllipse(p, inner);
        using var f = new Font("Arial", r.Height * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var br = new SolidBrush(c);
        var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("?", f, br, new RectangleF(r.X, r.Y + r.Height * 0.04f, r.Width, r.Height), sf);
    }

    private static void DrawClose(Graphics g, Rectangle r, Color c, bool hot, float pen)
    {
        if (hot) using (var b = new SolidBrush(Color.FromArgb(0x30, c))) g.FillEllipse(b, r);
        using var p = new Pen(c, pen * 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float m = r.Width * 0.32f;
        g.DrawLine(p, r.X + m, r.Y + m, r.Right - m, r.Bottom - m);
        g.DrawLine(p, r.Right - m, r.Y + m, r.X + m, r.Bottom - m);
    }

    // ------------------------------------------------------------ fare

    private string? TipFor(ButtonRegion r) => r switch
    {
        ButtonRegion.Main => "Bu videoyu T-Downloader ile indir",
        ButtonRegion.Help => "Bu panel hakkında",
        ButtonRegion.Close => "Kapat (bu sekmede gizle)",
        _ => null,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_down != ButtonRegion.None && MouseButtons == MouseButtons.Left)
        {
            var cur = Cursor.Position;
            if (!_dragging && (Math.Abs(cur.X - _dragStartCursor.X) > 4 || Math.Abs(cur.Y - _dragStartCursor.Y) > 4))
            {
                _dragging = true;
                _tip.Hide(this);
            }
            if (_dragging)
            {
                _x = _dragStartLoc.X + cur.X - _dragStartCursor.X;
                _y = _dragStartLoc.Y + cur.Y - _dragStartCursor.Y;
                Present();
                return;
            }
        }
        var r = HitTest(e.Location);
        if (r != _hover)
        {
            _hover = r;
            Present();
            var t = TipFor(r);
            _tip.Hide(this);
            if (t != null) _tip.Show(t, this, e.X, _layout.H + 4, 2500);
        }
        if (_alpha != AlphaNormal || _fadeStep.Enabled) { _alpha = AlphaNormal; _fadeStep.Stop(); _fadeDelay.Stop(); Present(); }
        else _fadeDelay.Stop();
        Cursor = _hover == ButtonRegion.None ? Cursors.Default : Cursors.Hand;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_down != ButtonRegion.None) return;
        _hover = ButtonRegion.None;
        _tip.Hide(this);
        Present();
        _fadeStep.Stop();
        _fadeDelay.Stop();
        if (!MenuOpen) _fadeDelay.Start();   // 10 sn sonra soluklaşma
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _down = HitTest(e.Location);
        _dragging = false;
        _dragStartCursor = Cursor.Position;
        _dragStartLoc = new Point(_x, _y);
        Capture = true;
        _tip.Hide(this);
        Present();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var was = _down;
        bool dragged = _dragging;
        _down = ButtonRegion.None; _dragging = false;
        Capture = false;
        var up = HitTest(e.Location);
        _hover = up;
        Present();
        if (dragged) { DragCompleted?.Invoke(new Point(_x, _y)); return; }
        if (was == ButtonRegion.None || was != up) return;
        switch (was)
        {
            case ButtonRegion.Main: MainClicked?.Invoke(); break;
            case ButtonRegion.Help: HelpClicked?.Invoke(); break;
            case ButtonRegion.Close: CloseClicked?.Invoke(); break;
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left && HitTest(e.Location) is ButtonRegion.Main or ButtonRegion.None) ResetRequested?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fadeDelay.Dispose(); _fadeStep.Dispose(); _tip.Dispose(); }
        base.Dispose(disposing);
    }
}
