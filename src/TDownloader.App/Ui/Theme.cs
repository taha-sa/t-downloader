using TSuite.Design;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace TDownloader.App.Ui;

/// <summary>T-Suite neutral palette with T-Downloader's sky accent.</summary>
public static class Theme
{
    public static readonly Color Accent = TSuiteTokens.ColorAccentTDownloaderDarkBase;
    public static readonly Color AccentPressed = TSuiteTokens.ColorAccentTDownloaderDarkPressed;
    public static readonly Color AccentHover = TSuiteTokens.ColorAccentTDownloaderDarkHover;
    public static readonly Color OnAccent = TSuiteTokens.ColorAccentTDownloaderDarkOnAccent;
    public static readonly Color Black = TSuiteTokens.ColorNeutralDarkBg;
    public static readonly Color Bg = TSuiteTokens.ColorNeutralDarkBg;
    public static readonly Color Surface = TSuiteTokens.ColorNeutralDarkSurface1;
    public static readonly Color Field = TSuiteTokens.ColorNeutralDarkSurface2;
    public static readonly Color Border = TSuiteTokens.ColorNeutralDarkBorder;
    public static readonly Color Hover = TSuiteTokens.ColorNeutralDarkSurface3;
    public static readonly Color Pressed = Composite(TSuiteTokens.ColorAccentTDownloaderDarkSubtle, Surface);
    public static readonly Color Text = TSuiteTokens.ColorNeutralDarkText;
    public static readonly Color TextDim = TSuiteTokens.ColorNeutralDarkTextMuted;
    public static readonly Color Disabled = TSuiteTokens.ColorNeutralDarkTextFaint;
    public static readonly Color Danger = TSuiteTokens.ColorStateDarkDangerBase;
    public static readonly Color Ok = TSuiteTokens.ColorStateDarkSuccessBase;
    public static readonly Color IconLine = TSuiteTokens.ColorNeutralDarkText;
    public static readonly Color[] SegmentColors =
    {
        Accent, AccentHover, AccentPressed, Text, TextDim,
        TSuiteTokens.ColorNeutralDarkBorderStrong, Disabled, IconLine,
    };
    private static Color Composite(Color tint, Color background)
    {
        int Blend(byte foreground, byte back) => (foreground * tint.A + back * (255 - tint.A) + 127) / 255;
        return Color.FromArgb(Blend(tint.R, background.R), Blend(tint.G, background.G), Blend(tint.B, background.B));
    }

    public static Font UiFont(float pt = 8f, FontStyle st = FontStyle.Regular) => new("Tahoma", pt, st);
}

/// <summary>Koyu temayı yerel pencerelere uygulayan Win32 yardımcıları.</summary>
public static class Dark
{
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr h, string? app, string? ids);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

    private static int Rgb(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>Koyu başlık çubuğu (Win10 20H1+/Win11); desteklenmeyen sistemlerde sessizce yok sayılır.</summary>
    public static void Window(IntPtr h)
    {
        try
        {
            int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4);
            int cap = Rgb(Theme.Black), txt = Rgb(Theme.Text), bdr = Rgb(Theme.Border);
            DwmSetWindowAttribute(h, 35, ref cap, 4);
            DwmSetWindowAttribute(h, 36, ref txt, 4);
            DwmSetWindowAttribute(h, 34, ref bdr, 4);
        }
        catch (Exception) { }
    }

    public static void SetTheme(IntPtr h, string name) { try { SetWindowTheme(h, name, null); } catch (Exception) { } }
    public static void Explorer(IntPtr h) { try { SetWindowTheme(h, "DarkMode_Explorer", null); } catch (Exception) { } }

    private static void WithHandle(Control c, Action<IntPtr> a)
    {
        if (c.IsHandleCreated) a(c.Handle);
        else c.HandleCreated += (_, _) => a(c.Handle);
    }

    /// <summary>Denetim ağacındaki standart denetimlerin renklerini koyu temaya çevirir.</summary>
    public static void ApplyTree(Control root)
    {
        foreach (Control c in root.Controls)
        {
            Style(c);
            if (c.HasChildren) ApplyTree(c);
        }
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case Button b when b.FlatStyle != FlatStyle.Flat || b.FlatAppearance.BorderSize != 0:
                b.FlatStyle = FlatStyle.Flat; b.UseVisualStyleBackColor = false;
                b.BackColor = Theme.Field; b.ForeColor = Theme.Text;
                var fa = b.FlatAppearance;
                fa.BorderSize = 1; fa.BorderColor = Theme.Border;
                fa.MouseOverBackColor = Theme.Hover; fa.MouseDownBackColor = Theme.Pressed;
                Form? form = b.FindForm();
                if (form != null && form.AcceptButton == b) fa.BorderColor = Theme.Accent;
                break;
            case TextBox t:
                t.BackColor = Theme.Field; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle;
                WithHandle(t, Explorer);
                break;
            case NumericUpDown n:
                n.BackColor = Theme.Field; n.ForeColor = Theme.Text; n.BorderStyle = BorderStyle.FixedSingle;
                foreach (Control k in n.Controls) { var kk = k; WithHandle(kk, Explorer); }
                break;
            case DateTimePicker d:
                d.CalendarForeColor = Theme.Text; d.CalendarMonthBackground = Theme.Field; d.CalendarTitleBackColor = Theme.Black;
                d.CalendarTitleForeColor = Theme.Accent; d.CalendarTrailingForeColor = Theme.Disabled;
                d.BackColor = Theme.Field; d.ForeColor = Theme.Text;
                WithHandle(d, Explorer);
                foreach (Control k in d.Controls) { var kk = k; WithHandle(kk, Explorer); }
                break;
            case LinkLabel l:
                l.LinkColor = Theme.Accent; l.ActiveLinkColor = Theme.AccentHover; l.VisitedLinkColor = Theme.AccentPressed;
                break;
            case TabPage p:
                p.UseVisualStyleBackColor = false; p.BackColor = Theme.Bg;
                break;
        }
    }

}

public sealed class ThemeColors : ProfessionalColorTable
{
    public ThemeColors() { UseSystemColors = false; }
    public override Color ButtonSelectedHighlight => Theme.Hover;
    public override Color ButtonSelectedHighlightBorder => Theme.Accent;
    public override Color ButtonSelectedGradientBegin => Theme.Hover;
    public override Color ButtonSelectedGradientMiddle => Theme.Hover;
    public override Color ButtonSelectedGradientEnd => Theme.Hover;
    public override Color ButtonSelectedBorder => Theme.Accent;
    public override Color ButtonPressedHighlight => Theme.Pressed;
    public override Color ButtonPressedHighlightBorder => Theme.Accent;
    public override Color ButtonPressedGradientBegin => Theme.Pressed;
    public override Color ButtonPressedGradientMiddle => Theme.Pressed;
    public override Color ButtonPressedGradientEnd => Theme.Pressed;
    public override Color ButtonPressedBorder => Theme.Accent;
    public override Color ButtonCheckedHighlight => Theme.Pressed;
    public override Color ButtonCheckedGradientBegin => Theme.Pressed;
    public override Color ButtonCheckedGradientMiddle => Theme.Pressed;
    public override Color ButtonCheckedGradientEnd => Theme.Pressed;
    public override Color CheckBackground => Theme.Pressed;
    public override Color CheckSelectedBackground => Theme.Pressed;
    public override Color CheckPressedBackground => Theme.Pressed;
    public override Color ToolStripGradientBegin => Theme.Surface;
    public override Color ToolStripGradientMiddle => Theme.Surface;
    public override Color ToolStripGradientEnd => Theme.Surface;
    public override Color ToolStripContentPanelGradientBegin => Theme.Surface;
    public override Color ToolStripContentPanelGradientEnd => Theme.Surface;
    public override Color ToolStripPanelGradientBegin => Theme.Surface;
    public override Color ToolStripPanelGradientEnd => Theme.Surface;
    public override Color MenuStripGradientBegin => Theme.Surface;
    public override Color MenuStripGradientEnd => Theme.Surface;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;
    public override Color MenuItemPressedGradientBegin => Theme.Bg;
    public override Color MenuItemPressedGradientMiddle => Theme.Bg;
    public override Color MenuItemPressedGradientEnd => Theme.Bg;
    public override Color MenuItemBorder => Theme.Accent;
    public override Color MenuBorder => Theme.Border;
    public override Color ToolStripDropDownBackground => Theme.Bg;
    public override Color ImageMarginGradientBegin => Theme.Bg;
    public override Color ImageMarginGradientMiddle => Theme.Bg;
    public override Color ImageMarginGradientEnd => Theme.Bg;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color ToolStripBorder => Theme.Border;
    public override Color StatusStripGradientBegin => Theme.Surface;
    public override Color StatusStripGradientEnd => Theme.Surface;
    public override Color GripDark => Theme.Disabled;
    public override Color GripLight => Theme.Surface;
    public override Color OverflowButtonGradientBegin => Theme.Surface;
    public override Color OverflowButtonGradientMiddle => Theme.Surface;
    public override Color OverflowButtonGradientEnd => Theme.Surface;
    public override Color RaftingContainerGradientBegin => Theme.Surface;
    public override Color RaftingContainerGradientEnd => Theme.Surface;
}

public static class Dlu
{
    /// <summary>Tahoma 8pt, %100: 1 DLU yatay = 1.5 px, dikey = 1.625 px. Değerler 96 dpi tasarım pikselidir; form AutoScaleMode.Dpi ile ölçeklenir.</summary>
    public static readonly float Scale = Probe();
    private static float Probe() { using var g = Graphics.FromHwnd(IntPtr.Zero); return g.DpiX / 96f; }
    public static int X(double d) => (int)Math.Round(d * 1.5 * Scale);
    public static int Y(double d) => (int)Math.Round(d * 1.625 * Scale);
    public static Rectangle R(double x, double y, double w, double h) => new(X(x), Y(y), X(w), Y(h));
}

/// <summary>Tahoma 8pt + Dpi ölçekli, koyu temalı taban pencere.</summary>
public class DluForm : Form
{
    public DluForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.UiFont();
        Icon = Icons.AppIcon();
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dark.Window(Handle);
        Dark.ApplyTree(this);
    }

    /// <summary>İçerik alanını DLU olarak ayarlar.</summary>
    protected void SetClientDlu(double w, double h) => ClientSize = new Size(Dlu.X(w), Dlu.Y(h));

    protected T Add<T>(T c, double x, double y, double w, double h) where T : Control
    {
        c.Bounds = Dlu.R(x, y, w, h);
        Controls.Add(c);
        return c;
    }

    protected Button MakeButton(string text, double x, double y, double w, double h, DialogResult dr = DialogResult.None)
    {
        var b = new Button { Text = text, DialogResult = dr };
        return Add(b, x, y, w, h);
    }

    protected Label MakeLabel(string text, double x, double y, double w, double h, ContentAlignment a = ContentAlignment.TopLeft)
        => Add(new Label { Text = text, TextAlign = a, AutoEllipsis = true }, x, y, w, h);
}

/// <summary>Koyu tema, vurgu rengi seçim; owner-draw liste (sistem mavisi seçim rengi kullanılmaz, hover'da alt sütunlar asla silinmez).</summary>
public sealed class BufferedListView : ListView
{
    /// <summary>Hücreler arasına ince ızgara çizgisi çizer.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Grid { get; set; }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SortColumn { get; set; } = -1;

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SortAscending { get; set; } = true;

    private HeaderFiller? _filler;
    private readonly SolidBrush _brSurface = new(Theme.Surface);
    private readonly SolidBrush _brBg = new(Theme.Bg);
    private readonly SolidBrush _brAccent = new(Theme.Accent);
    private readonly SolidBrush _brHover = new(Theme.Hover);
    private readonly Pen _penBorder = new(Theme.Border);
    private readonly Pen _penGrid = new(Theme.Surface);

    public BufferedListView()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.FixedSingle;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(Handle, 0x1036 /*LVM_SETEXTENDEDSTYLE*/, (IntPtr)0x10000, (IntPtr)0x10000); // LVS_EX_DOUBLEBUFFER
        // Not: Explorer koyu teması uygulanmaz; fare altındaki satıra mavimsi sistem tonu çizdiği için (yan etki: açık renkli kaydırma çubuğu).
        _filler?.ReleaseHandle();
        var hdr = SendMessage(Handle, 0x101F /*LVM_GETHEADER*/, IntPtr.Zero, IntPtr.Zero);
        if (hdr != IntPtr.Zero) { _filler = new HeaderFiller(); _filler.AssignHandle(hdr); }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _filler?.ReleaseHandle(); _filler = null;
        base.OnHandleDestroyed(e);
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref RECT l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Son sütundan sonra kalan başlık boşluğunu (sistem çizer) koyu renkle boyar.</summary>
    private sealed class HeaderFiller : NativeWindow
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x000F /*WM_PAINT*/) return;
            int count = (int)SendMessage(Handle, 0x1200 /*HDM_GETITEMCOUNT*/, IntPtr.Zero, IntPtr.Zero);
            int right = 0;
            for (int i = 0; i < count; i++)
            {
                var r = new RECT();
                SendMessage(Handle, 0x1207 /*HDM_GETITEMRECT*/, (IntPtr)i, ref r);
                right = Math.Max(right, r.Right);
            }
            GetClientRect(Handle, out var cr);
            if (right >= cr.Right) return;
            IntPtr dc = GetDC(Handle);
            try
            {
                using var g = Graphics.FromHdc(dc);
                using var br = new SolidBrush(Theme.Surface);
                g.FillRectangle(br, right, 0, cr.Right - right, cr.Bottom);
                using var p = new Pen(Theme.Border);
                g.DrawLine(p, right, cr.Bottom - 1, cr.Right, cr.Bottom - 1);
            }
            finally { ReleaseDC(Handle, dc); }
        }
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var g = e.Graphics;
        g.FillRectangle(_brSurface, e.Bounds);
        g.DrawLine(_penBorder, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        g.DrawLine(_penBorder, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
        var r = Rectangle.Inflate(e.Bounds, -(int)(5 * Dlu.Scale), 0);
        string title = e.Header?.Text ?? "";
        if (e.ColumnIndex == SortColumn)
            title = SortAscending ? title + " ▲" : title + " ▼";
        TextRenderer.DrawText(g, title, Font, r, e.ColumnIndex == SortColumn ? Theme.Accent : Theme.Text, Flags(e.Header?.TextAlign ?? HorizontalAlignment.Left));
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        if (View != View.Details) { e.DrawDefault = true; return; }
        // KRİTİK: Sanal+OwnerDraw modda fare satırın üstüne gelince Windows yalnızca OnDrawItem + sütun 0 çizebilir.
        // Bu yüzden satırın tamamını burada boyamak sütun 1..N'i siler! Burada YALNIZCA son sütundan sonraki boş sağ alan boyanır;
        // her hücre kendi arka planını OnDrawSubItem içinde boyar.
        int colsRight = e.Bounds.Left;
        foreach (ColumnHeader col in Columns) colsRight += col.Width;
        int rightEdge = Math.Max(ClientSize.Width, e.Bounds.Right);
        if (colsRight < rightEdge)
        {
            var tail = new Rectangle(colsRight, e.Bounds.Y, rightEdge - colsRight, e.Bounds.Height);
            e.Graphics.FillRectangle(RowBackBrush(e.Item, e.ItemIndex), tail);
            if (Grid) e.Graphics.DrawLine(_penGrid, tail.Left, e.Bounds.Bottom - 1, tail.Right, e.Bounds.Bottom - 1);
        }
        e.DrawDefault = false;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (View != View.Details) { e.DrawDefault = true; return; }
        var g = e.Graphics;
        int col = e.ColumnIndex;
        var cell = e.Bounds;
        if (col == 0 && Columns.Count > 0) cell = new Rectangle(cell.X, cell.Y, Columns[0].Width, cell.Height);
        if (cell.Width <= 0 || cell.Height <= 0) return;

        g.FillRectangle(RowBackBrush(e.Item!, e.ItemIndex), cell);

        var sel = IsSel(e.Item!, e.ItemIndex);
        var fg = sel && Focused ? Theme.OnAccent : (e.Item!.ForeColor == SystemColors.WindowText ? Theme.Text : e.Item!.ForeColor);
        int pad = (int)(4 * Dlu.Scale);
        int x = cell.X + pad;
        if (col == 0 && SmallImageList != null && e.Item!.ImageIndex >= 0 && e.Item!.ImageIndex < SmallImageList.Images.Count)
        {
            var sz = SmallImageList.ImageSize;
            g.DrawImage(SmallImageList.Images[e.Item.ImageIndex], x, cell.Y + (cell.Height - sz.Height) / 2, sz.Width, sz.Height);
            x += sz.Width + pad;
        }
        var tr = new Rectangle(x, cell.Y, Math.Max(0, cell.Right - x - pad), cell.Height);
        var align = col < Columns.Count ? Columns[col].TextAlign : HorizontalAlignment.Left;
        if (tr.Width > 0)
            TextRenderer.DrawText(g, e.SubItem?.Text ?? "", Font, tr, fg, Flags(align));
        if (Grid)
        {
            g.DrawLine(_penGrid, cell.Left, cell.Bottom - 1, cell.Right, cell.Bottom - 1);
            g.DrawLine(_penGrid, cell.Right - 1, cell.Top, cell.Right - 1, cell.Bottom);
        }
    }

    // Sanal modda ListViewItem.Selected guvenilir degildir: secim dizinden okunur.
    private bool IsSel(ListViewItem it, int index) => VirtualMode ? SelectedIndices.Contains(index) : it.Selected;

    private SolidBrush RowBackBrush(ListViewItem it, int index) => IsSel(it, index) ? (Focused ? _brAccent : _brHover) : _brBg;

    private static TextFormatFlags Flags(HorizontalAlignment a) =>
        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
        (a == HorizontalAlignment.Right ? TextFormatFlags.Right : a == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _brSurface.Dispose(); _brBg.Dispose(); _brAccent.Dispose(); _brHover.Dispose();
            _penBorder.Dispose(); _penGrid.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Koyu temalı ağaç: sarı seçim (odaktayken), gri seçim (odak yokken).</summary>
public sealed class DarkTree : TreeView
{
    public DarkTree()
    {
        BackColor = Theme.Bg; ForeColor = Theme.Text; BorderStyle = BorderStyle.None;
        DrawMode = TreeViewDrawMode.OwnerDrawText;
        LineColor = Theme.Border;
    }

    

    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        if (e.Node == null || e.Bounds.IsEmpty) { e.DrawDefault = false; return; }
        bool sel = (e.State & TreeNodeStates.Selected) != 0;
        var back = sel ? (Focused ? Theme.Accent : Theme.Hover) : BackColor;
        var fg = sel && Focused ? Theme.OnAccent : Theme.Text;
        var r = Rectangle.Inflate(e.Bounds, 2, 0);
        using (var br = new SolidBrush(back)) e.Graphics.FillRectangle(br, r);
        TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? Font, e.Bounds, fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>Koyu açılır liste (yalnızca DropDownList): koyu alan, sarı seçim.</summary>
public sealed class DarkCombo : ComboBox
{
    public DarkCombo()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
        BackColor = Theme.Field; ForeColor = Theme.Text;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var br = new SolidBrush(Theme.Field)) g.FillRectangle(br, ClientRectangle);
        using (var p = new Pen(Focused || DroppedDown ? Theme.Accent : Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        string txt = SelectedItem is { } si ? GetItemText(si) ?? "" : "";
        int aw = (int)(16 * Dlu.Scale);
        TextRenderer.DrawText(g, txt, Font, new Rectangle(4, 0, Width - aw - 6, Height), Enabled ? Theme.Text : Theme.Disabled,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        float cx = Width - aw / 2f - 1, cy = Height / 2f, s = 3.5f * Dlu.Scale;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var ap = new Pen(Enabled ? Theme.Accent : Theme.Disabled, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(ap, new[] { new PointF(cx - s, cy - s / 2), new PointF(cx, cy + s / 2), new PointF(cx + s, cy - s / 2) });
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var br = new SolidBrush(sel ? Theme.Accent : Theme.Field)) e.Graphics.FillRectangle(br, e.Bounds);
        if (e.Index < 0) return;
        var r = new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 3, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, r, sel ? Theme.OnAccent : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Koyu onay kutusu: koyu kutu, sarı işaret.</summary>
public sealed class DarkCheck : CheckBox
{
    private bool _hot;
    public DarkCheck()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        int box = (int)Math.Round(13 * Dlu.Scale);
        int by = (Height - box) / 2;
        var br = new Rectangle(0, by, box - 1, box - 1);
        using (var f = new SolidBrush(Enabled ? Theme.Field : Theme.Bg)) g.FillRectangle(f, br);
        using (var p = new Pen(!Enabled ? Theme.Border : (_hot || Focused) ? Theme.Accent : Theme.TextDim)) g.DrawRectangle(p, br);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var mark = Enabled ? Theme.Accent : Theme.Disabled;
        if (CheckState == CheckState.Checked)
        {
            using var p = new Pen(mark, Math.Max(2f, 2f * Dlu.Scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(p, new[] { new PointF(br.X + box * 0.22f, by + box * 0.52f), new PointF(br.X + box * 0.42f, by + box * 0.72f), new PointF(br.X + box * 0.78f, by + box * 0.28f) });
        }
        else if (CheckState == CheckState.Indeterminate)
        {
            using var f = new SolidBrush(mark); g.FillRectangle(f, br.X + 3, by + 3, box - 7, box - 7);
        }
        var tr = new Rectangle(box + (int)(5 * Dlu.Scale), 0, Math.Max(0, Width - box - (int)(5 * Dlu.Scale)), Height);
        TextRenderer.DrawText(g, Text, Font, tr, Enabled ? Theme.Text : Theme.Disabled,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.HidePrefix);
    }
}

/// <summary>Koyu grup kutusu: ince gri çerçeve, sarı başlık.</summary>
public sealed class DarkGroup : GroupBox
{
    public DarkGroup() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var bg = Parent?.BackColor ?? Theme.Bg;
        g.Clear(bg);
        var sz = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        int top = sz.Height / 2;
        using (var p = new Pen(Theme.Border))
            g.DrawRectangle(p, 0, top, Width - 1, Height - top - 1);
        int tx = (int)(7 * Dlu.Scale);
        using (var br = new SolidBrush(bg)) g.FillRectangle(br, tx - 2, 0, sz.Width + 4, sz.Height);
        TextRenderer.DrawText(g, Text, Font, new Point(tx, 0), Theme.Accent, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

/// <summary>Koyu sekmeli denetim: sarı üst çizgili seçili sekme.</summary>
public sealed class DarkTabs : TabControl
{
    public DarkTabs()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        var page = DisplayRectangle; page.Inflate(3, 3);
        using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, page.X, page.Y, page.Width - 1, page.Height - 1);
        for (int i = 0; i < TabCount; i++)
        {
            var r = GetTabRect(i);
            bool sel = i == SelectedIndex;
            using (var br = new SolidBrush(sel ? Theme.Surface : Theme.Bg)) g.FillRectangle(br, r);
            using (var p = new Pen(Theme.Border)) { g.DrawLine(p, r.Left, r.Top, r.Left, r.Bottom); g.DrawLine(p, r.Right - 1, r.Top, r.Right - 1, r.Bottom); g.DrawLine(p, r.Left, r.Top, r.Right, r.Top); }
            if (sel) { using var br = new SolidBrush(Theme.Accent); g.FillRectangle(br, r.Left, r.Top, r.Width, 3); }
            TextRenderer.DrawText(g, TabPages[i].Text, Font, r, sel ? Theme.Accent : Theme.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>Düz, sarı dolgulu ilerleme çubuğu.</summary>
public sealed class DarkProgress : ProgressBar
{
    public DarkProgress() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var br = new SolidBrush(Theme.Field)) g.FillRectangle(br, r);
        double f = Maximum > Minimum ? (double)(Value - Minimum) / (Maximum - Minimum) : 0;
        int w = (int)Math.Round((r.Width - 1) * Math.Clamp(f, 0, 1));
        if (w > 0) { using var br = new SolidBrush(Theme.Accent); g.FillRectangle(br, 1, 1, w, r.Height - 1); }
        using var p = new Pen(Theme.Border);
        g.DrawRectangle(p, r);
    }
}

public sealed class ThemeRenderer : ToolStripProfessionalRenderer
{
    public ThemeRenderer() : base(new ThemeColors()) { RoundedEdges = false; }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var p = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var fc = e.Item.ForeColor;
        e.TextColor = !e.Item.Enabled ? Theme.Disabled
            : (fc != SystemColors.ControlText && fc != Color.Empty && fc != Theme.Text) ? fc
            : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? Theme.Disabled : Theme.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        if (e.Vertical) { int x = e.Item.Width / 2; e.Graphics.DrawLine(p, x, 4, x, e.Item.Height - 4); }
        else { int y = e.Item.Height / 2; e.Graphics.DrawLine(p, 4, y, e.Item.Width - 4, y); }
    }
}
