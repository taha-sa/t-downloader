using System.Drawing.Drawing2D;

namespace TDownloader.App.Ui;

/// <summary>Renk ailesi: koyu lacivert + turkuaz vurgu.</summary>
public static class Theme
{
    public static readonly Color Navy = Color.FromArgb(0x12, 0x2B, 0x4A);
    public static readonly Color NavyLight = Color.FromArgb(0x2A, 0x4A, 0x72);
    public static readonly Color Teal = Color.FromArgb(0x00, 0xB3, 0xA4);
    public static readonly Color TealDark = Color.FromArgb(0x00, 0x8C, 0x82);
    public static readonly Color Surface = Color.FromArgb(0xF3, 0xF6, 0xFA);
    public static readonly Color Border = Color.FromArgb(0xC5, 0xD0, 0xDE);
    public static readonly Color Hover = Color.FromArgb(0xD8, 0xF1, 0xEF);
    public static readonly Color Pressed = Color.FromArgb(0xB4, 0xE6, 0xE2);

    public static readonly Color[] SegmentColors =
    {
        Color.FromArgb(0x00, 0xB3, 0xA4), Color.FromArgb(0x2A, 0x6F, 0xB5), Color.FromArgb(0x7B, 0x61, 0xC9),
        Color.FromArgb(0xE0, 0x8A, 0x1E), Color.FromArgb(0x3F, 0xA3, 0x4D), Color.FromArgb(0xD1, 0x4B, 0x6A),
        Color.FromArgb(0x1C, 0x9F, 0xD6), Color.FromArgb(0x8A, 0x9B, 0x2C),
    };

    public static Font UiFont(float pt = 8f, FontStyle st = FontStyle.Regular) => new("Tahoma", pt, st);
}

public sealed class ThemeColors : ProfessionalColorTable
{
    public override Color ButtonSelectedHighlight => Theme.Hover;
    public override Color ButtonSelectedGradientBegin => Theme.Hover;
    public override Color ButtonSelectedGradientMiddle => Theme.Hover;
    public override Color ButtonSelectedGradientEnd => Theme.Hover;
    public override Color ButtonSelectedBorder => Theme.Teal;
    public override Color ButtonPressedGradientBegin => Theme.Pressed;
    public override Color ButtonPressedGradientMiddle => Theme.Pressed;
    public override Color ButtonPressedGradientEnd => Theme.Pressed;
    public override Color ButtonPressedBorder => Theme.TealDark;
    public override Color ToolStripGradientBegin => Theme.Surface;
    public override Color ToolStripGradientMiddle => Theme.Surface;
    public override Color ToolStripGradientEnd => Theme.Surface;
    public override Color MenuStripGradientBegin => Theme.Surface;
    public override Color MenuStripGradientEnd => Theme.Surface;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;
    public override Color MenuItemBorder => Theme.Teal;
    public override Color MenuBorder => Theme.Border;
    public override Color ToolStripDropDownBackground => Color.White;
    public override Color ImageMarginGradientBegin => Theme.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Surface;
    public override Color ImageMarginGradientEnd => Theme.Surface;
    public override Color SeparatorDark => Theme.Border;
    public override Color ToolStripBorder => Theme.Border;
    public override Color StatusStripGradientBegin => Theme.Surface;
    public override Color StatusStripGradientEnd => Theme.Surface;
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

/// <summary>Tahoma 8pt + Dpi ölçekli taban pencere.</summary>
public class DluForm : Form
{
    public DluForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.UiFont();
        Icon = Icons.AppIcon();
        BackColor = SystemColors.Control;
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
        var b = new Button { Text = text, DialogResult = dr, FlatStyle = FlatStyle.System, UseVisualStyleBackColor = true };
        return Add(b, x, y, w, h);
    }

    protected Label MakeLabel(string text, double x, double y, double w, double h, ContentAlignment a = ContentAlignment.TopLeft)
        => Add(new Label { Text = text, TextAlign = a, AutoEllipsis = true }, x, y, w, h);
}

public sealed class BufferedListView : ListView
{
    public BufferedListView() { DoubleBuffered = true; }
}

public sealed class ThemeRenderer : ToolStripProfessionalRenderer
{
    public ThemeRenderer() : base(new ThemeColors()) { RoundedEdges = false; }
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var p = new Pen(Theme.Border);
        if (e.Vertical) { int x = e.Item.Width / 2; e.Graphics.DrawLine(p, x, 4, x, e.Item.Height - 4); }
        else { int y = e.Item.Height / 2; e.Graphics.DrawLine(p, 28, y, e.Item.Width - 4, y); }
    }
}
