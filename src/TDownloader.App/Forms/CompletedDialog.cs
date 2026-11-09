using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using TDownloader.App.Ui;

namespace TDownloader.App.Forms;

/// <summary>
/// "İndirme tamamlandı" penceresi (IDM'nin tamamlanma penceresi gibi işlev, T-Downloader teması): kenarlıksız 460x264, kendi başlık çubuğu,
/// 12 px köşe, gölge, 150 ms fade-in, sağ-alt köşede, odak ÇALMAZ. Birden çok tamamlanma tek pencerede "1/3" sayfalayıcıyla birikir.
/// </summary>
public sealed class CompletedDialog : Form
{
    private const int W = 460, H = 264, Pad = 16, Radius = 12;
    private const int CS_DROPSHADOW = 0x20000;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    private readonly CompletedQueue _queue = new();
    private readonly Action<bool> _onNeverShow;
    private readonly Font _fTitle = new("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _fBody = new("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _fBold = new("Segoe UI Semibold", 9f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _flashTimer = new() { Interval = 1300 };
    private readonly ToolTip _tip = new() { InitialDelay = 400, ReshowDelay = 100 };
    private readonly Dictionary<string, Rectangle> _hit = new();
    private readonly string[] _cacheKey = new string[2];
    private readonly string[] _cacheVal = new string[2];
    private readonly int[] _cacheW = new int[2];
    private Bitmap? _icon;
    private DateTime _fadeStart;
    private bool _exists = true;
    private bool _neverShow;
    private string _hover = "", _down = "";
    private string _flash = "";        // "url" | "path": kopyalandı geri bildirimi

    /// <summary>Gelecekteki eylemler (örn. "Telefonda aç"). Boşsa yuva çizilmez.</summary>
    public List<ICompletedAction> ExtraActions { get; } = new();
    private const int ExtraRowH = 40;
    private int _h = H;   // telefon eylem satırı görünürse yükselir; boyut paint dışında uygulanır
    private bool _resizePending;

    private void ApplyHeight(int h)
    {
        _resizePending = false;
        if (IsDisposed || h == _h) return;
        int bottom = Location.Y + _h;
        _h = h;
        ClientSize = new Size(W, _h);
        Location = new Point(Location.X, bottom - _h);
        using (var gp = Round(new Rectangle(0, 0, W, _h), Radius)) Region = new Region(gp);
        Invalidate();
    }

    public CompletedDialog(Action<bool> onNeverShowChanged)
    {
        _onNeverShow = onNeverShowChanged;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        ClientSize = new Size(W, H);
        BackColor = Theme.Bg;
        Opacity = 0;
        Text = "İndirme tamamlandı";
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(wa.Right - W - 16, wa.Bottom - H - 16);
        using (var gp = Round(new Rectangle(0, 0, W, H), Radius)) Region = new Region(gp);

        _fade.Tick += (_, _) =>
        {
            double p = Math.Min(1, (DateTime.UtcNow - _fadeStart).TotalMilliseconds / 150.0);
            Opacity = p;
            if (p >= 1) { _fade.Stop(); }
        };
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash = ""; Invalidate(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ClassStyle |= CS_DROPSHADOW; return cp; }
    }

    // ------------------------------------------------------------ dışarıdan kullanım

    /// <summary>Kayıt ekler (varsa yeni sayfa) ve pencereyi gösterir/öne alır; odak çalmaz.</summary>
    public void Present(CompletedEntry e)
    {
        if (!_queue.Add(e)) return;
        OnEntryChanged();
        if (!Visible)
        {
            Opacity = 0; _fadeStart = DateTime.UtcNow;
            Show();
            _fade.Start();
        }
        else { Invalidate(); }
    }

    private void OnEntryChanged()
    {
        _icon?.Dispose();
        _icon = ShellIcons.Large(_queue.Current?.FileName ?? "");
        _exists = true;
        _cacheKey[0] = _cacheKey[1] = "";
        Invalidate();
        // Dosya var mı? UI thread'ini bloklamamak için arka planda (ağ sürücüsü olabilir).
        var cur = _queue.Current;
        if (cur == null) return;
        _ = Task.Run(() =>
        {
            bool ok;
            try { ok = File.Exists(cur.Path); } catch { ok = false; }
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => { if (_queue.Current == cur && _exists != ok) { _exists = ok; Invalidate(); } }); } catch (InvalidOperationException) { }
        });
    }

    // ------------------------------------------------------------ çizim

    private static GraphicsPath Round(Rectangle r, int rad)
    {
        int d = rad * 2; var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);
        using (var gp = Round(new Rectangle(0, 0, W - 1, _h - 1), Radius))
        using (var pen = new Pen(Theme.Border, 1)) g.DrawPath(pen, gp);
        _hit.Clear();

        var cur = _queue.Current;
        if (cur == null) return;

        // başlık çubuğu: marka + sayfalayıcı + kapat
        TextRenderer.DrawText(g, "T-Downloader", _fBody, new Rectangle(Pad, 8, 150, 20), Theme.TextDim, Flags);
        var closeR = new Rectangle(W - 44, 4, 36, 26);
        bool closeHot = _hover == "close";
        if (closeHot) { using var b = new SolidBrush(Color.FromArgb(40, Theme.Danger)); using var gp = Round(closeR, 6); g.FillPath(b, gp); }
        TextRenderer.DrawText(g, "✕", _fBody, closeR, closeHot ? Theme.Danger : Theme.TextDim, Flags | TextFormatFlags.HorizontalCenter);
        _hit["close"] = closeR;
        if (_queue.Count > 1)
        {
            var prev = new Rectangle(W - 44 - 90, 4, 24, 26);
            var next = new Rectangle(W - 44 - 24, 4, 24, 26);
            TextRenderer.DrawText(g, "‹", _fTitle, prev, _queue.Index > 0 ? (_hover == "prev" ? Theme.Yellow : Theme.Text) : Theme.Border, Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, _queue.Pager, _fBody, new Rectangle(prev.Right, 4, 42, 26), Theme.TextDim, Flags | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "›", _fTitle, next, _queue.Index + 1 < _queue.Count ? (_hover == "next" ? Theme.Yellow : Theme.Text) : Theme.Border, Flags | TextFormatFlags.HorizontalCenter);
            _hit["prev"] = prev; _hit["next"] = next;
        }

        // üst bilgi: 32 px simge + başlık + alt başlık
        if (_icon != null) g.DrawImage(_icon, new Rectangle(Pad, 42, 32, 32));
        TextRenderer.DrawText(g, "İndirme tamamlandı", _fTitle, new Rectangle(Pad + 44, 38, 330, 24), Theme.Text, Flags);
        var sub = new Rectangle(Pad + 44, 62, 360, 18);
        TextRenderer.DrawText(g, "✓", _fBold, new Rectangle(sub.X, sub.Y, 14, sub.Height), Theme.Ok, Flags);
        TextRenderer.DrawText(g, CompletedText.SizeLine(cur.Size), _fBody, new Rectangle(sub.X + 16, sub.Y, sub.Width - 16, sub.Height), Theme.TextDim, Flags);

        // alanlar
        DrawField(g, 0, "url", "Adres", cur.Url, 90);
        DrawField(g, 1, "path", "Kaydedildiği yer", cur.Path, 138);

        // düğmeler
        int by = 190, bx = Pad;
        bx = DrawButton(g, "open", "Aç", bx, by, 84, primary: true, enabled: _exists);
        bx = DrawButton(g, "openwith", "Birlikte aç…", bx, by, 112, false, _exists);
        bx = DrawButton(g, "folder", "Klasörü aç", bx, by, 100, false, _exists);
        var closeBtn = new Rectangle(W - Pad - 72, by, 72, 32);
        DrawButtonAt(g, "closebtn", "Kapat", closeBtn, false, true);

        // telefon eylemleri (Telefona gönder / Telefonda aç): yalnızca uygunsa, ikinci satırda
        int shift = 0, ex = Pad;
        for (int k = 0; k < ExtraActions.Count; k++)
        {
            var act = ExtraActions[k];
            if (!act.IsAvailable(cur)) continue;
            shift = ExtraRowH;
            ex = DrawButton(g, "extra" + k, act.Text, ex, by + ExtraRowH, 132, false, true);
        }
        int want = H + shift;
        if (want != _h && !_resizePending && IsHandleCreated) { _resizePending = true; BeginInvoke(() => ApplyHeight(want)); }

        // alt satır: onay kutusu + not
        var cb = new Rectangle(Pad, 238 + shift, 16, 16);
        using (var gp = Round(cb, 4))
        {
            using (var b = new SolidBrush(_neverShow ? Theme.Yellow : Theme.Field)) g.FillPath(b, gp);
            using var pen = new Pen(_neverShow ? Theme.Yellow : (_hover == "never" ? Theme.TextDim : Theme.Border), 1);
            g.DrawPath(pen, gp);
        }
        if (_neverShow)
        {
            using var pen = new Pen(Theme.Black, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(cb.X + 3.5f, cb.Y + 8.5f), new PointF(cb.X + 6.8f, cb.Y + 11.5f), new PointF(cb.X + 12.5f, cb.Y + 4.5f) });
        }
        var cbText = new Rectangle(cb.Right + 8, 236 + shift, 250, 20);
        TextRenderer.DrawText(g, "Bu pencereyi bir daha gösterme", _fBody, cbText, Theme.TextDim, Flags);
        _hit["never"] = new Rectangle(cb.X, 236 + shift, cbText.Right - cb.X, 20);
        if (!_exists)
            TextRenderer.DrawText(g, "Dosya taşınmış veya silinmiş", _fBody, new Rectangle(W - Pad - 170, 236 + shift, 170, 20), Theme.TextDim, Flags | TextFormatFlags.Right);
    }

    private void DrawField(Graphics g, int slot, string id, string label, string value, int y)
    {
        bool flashed = _flash == id;
        TextRenderer.DrawText(g, flashed ? label + "  · kopyalandı" : label, _fBody, new Rectangle(Pad, y, W - 2 * Pad, 16), flashed ? Theme.Ok : Theme.TextDim, Flags);
        var box = new Rectangle(Pad, y + 18, W - 2 * Pad, 28);
        using (var gp = Round(box, 6))
        {
            using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, gp);
            using var pen = new Pen(_hover == id ? Theme.TextDim : Theme.Border, 1); g.DrawPath(pen, gp);
        }
        int maxW = box.Width - 20;
        if (_cacheKey[slot] != value || _cacheW[slot] != maxW)
        {
            _cacheKey[slot] = value; _cacheW[slot] = maxW;
            _cacheVal[slot] = CompletedText.MiddleEllipsis(value, s => TextRenderer.MeasureText(s, _fBody, new Size(int.MaxValue, 20), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width <= maxW);
        }
        TextRenderer.DrawText(g, _cacheVal[slot], _fBody, new Rectangle(box.X + 10, box.Y, maxW, box.Height), Theme.Text, Flags);
        _hit[id] = box;
    }

    private int DrawButton(Graphics g, string id, string text, int x, int y, int w, bool primary, bool enabled)
    {
        DrawButtonAt(g, id, text, new Rectangle(x, y, w, 32), primary, enabled);
        return x + w + 8;
    }

    private void DrawButtonAt(Graphics g, string id, string text, Rectangle r, bool primary, bool enabled)
    {
        bool hot = enabled && _hover == id, down = hot && _down == id;
        Color fill, fg, border;
        if (!enabled) { fill = primary ? Theme.Surface : Theme.Bg; fg = Theme.TextDim; border = Theme.Border; }
        else if (primary) { fill = down ? Theme.YellowDark : hot ? Theme.YellowLight : Theme.Yellow; fg = Theme.Black; border = fill; }
        else { fill = down ? Theme.Pressed : hot ? Theme.Hover : Theme.Bg; fg = Theme.Text; border = Theme.Border; }
        using (var gp = Round(r, 6))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, gp);
            using var pen = new Pen(border, 1); g.DrawPath(pen, gp);
        }
        TextRenderer.DrawText(g, text, _fBold, r, fg, Flags | TextFormatFlags.HorizontalCenter);
        if (enabled) _hit[id] = r;
    }

    // ------------------------------------------------------------ fare / klavye

    private string HitTest(Point p)
    {
        foreach (var kv in _hit) if (kv.Value.Contains(p)) return kv.Key;
        return "";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            Cursor = h is "" ? Cursors.Default : Cursors.Hand;
            Invalidate();
            var cur = _queue.Current;
            if (cur != null && h == "url") _tip.Show(cur.Url, this, e.X + 12, e.Y + 20, 6000);
            else if (cur != null && h == "path") _tip.Show(cur.Path, this, e.X + 12, e.Y + 20, 6000);
            else _tip.Hide(this);
        }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = ""; _down = ""; _tip.Hide(this); Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _down = HitTest(e.Location);
        if (_down == "" && e.Button == MouseButtons.Left && e.Y < 36)       // başlık çubuğundan sürükle
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
            _down = "";
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var hit = HitTest(e.Location); var was = _down; _down = "";
        Invalidate();
        if (hit == "" || hit != was) return;
        Activate_(hit);
    }

    private void Activate_(string id)
    {
        var cur = _queue.Current;
        if (cur == null) return;
        switch (id)
        {
            case "close": case "closebtn": CloseAll(); break;
            case "open": OpenFile(cur); break;
            case "openwith": OpenWith(cur); break;
            case "folder": ShowInFolder(cur); break;
            case string x when x.StartsWith("extra") && int.TryParse(x.AsSpan(5), out var xi) && xi >= 0 && xi < ExtraActions.Count:
                ExtraActions[xi].Execute(cur); break;
            case "prev": if (_queue.Prev()) OnEntryChanged(); break;
            case "next": if (_queue.Next()) OnEntryChanged(); break;
            case "url": Copy(cur.Url, "url"); break;
            case "path": Copy(cur.Path, "path"); break;
            case "never":
                _neverShow = !_neverShow;
                _onNeverShow(_neverShow);         // ayara yazılır (Seçenekler'den de değiştirilebilir)
                Invalidate();
                break;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter) { if (_queue.Current is { } c && _exists) OpenFile(c); return true; }
        if (keyData == Keys.Escape) { CloseAll(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ------------------------------------------------------------ eylemler

    private void Copy(string text, string id)
    {
        try { Clipboard.SetText(text); } catch (ExternalException) { return; }
        _flash = id; _flashTimer.Stop(); _flashTimer.Start(); Invalidate();
    }

    private void OpenFile(CompletedEntry c)
    {
        if (!_exists) return;
        try { Process.Start(new ProcessStartInfo(c.Path) { UseShellExecute = true })?.Dispose(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        CloseAll();
    }

    private void OpenWith(CompletedEntry c)
    {
        if (!_exists) return;
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{c.Path}\"") { UseShellExecute = false })?.Dispose(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void ShowInFolder(CompletedEntry c)
    {
        if (!_exists) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{c.Path}\"") { UseShellExecute = false })?.Dispose(); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        CloseAll();
    }

    private void CloseAll() { _queue.Clear(); Hide(); }

    /// <summary>Pencere gizlenip yeniden kullanılırken sıfırlanır.</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { _fade.Stop(); _hover = ""; _down = ""; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fade.Dispose(); _flashTimer.Dispose(); _tip.Dispose();
            _icon?.Dispose();
            _fTitle.Dispose(); _fBody.Dispose(); _fBold.Dispose();
        }
        base.Dispose(disposing);
    }
}
