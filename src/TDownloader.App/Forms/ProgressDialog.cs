using TDownloader.Core.Localization;
using TDownloader.App.Ui;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;

namespace TDownloader.App.Forms;

/// <summary>Parçaların ilerlemesini bağlantı rengiyle gösteren yatay segment çubuğu (önbellekli GDI fırçalarıyla 0 takılma).</summary>
public sealed class SegmentBar : Control
{
    private const int Cells = 2048;
    private readonly byte[] _cells = new byte[Cells];
    private readonly SolidBrush _brField = new(Theme.Field);
    private readonly SolidBrush _brAccent = new(Theme.Accent);
    private readonly SolidBrush[] _brSegs = Theme.SegmentColors.Select(c => new SolidBrush(c)).ToArray();
    private readonly Pen _penBorder = new(Theme.Border);
    private bool _all;
    private bool _drawn;

    public SegmentBar() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }

    public void Apply(DownloadProgress p, bool completed)
    {
        bool changed = !_drawn || _all != completed;
        _all = completed; _drawn = true;
        if (p.TotalSize > 0)
        {
            if (p.Connections.Count > 0)
            {
                foreach (var c in p.Connections)
                {
                    if (c.Position <= c.SegmentStart) continue;
                    int a = (int)Math.Clamp(c.SegmentStart * (long)Cells / p.TotalSize, 0, Cells);
                    int b = (int)Math.Clamp(c.Position * (long)Cells / p.TotalSize, 0, Cells);
                    byte col = (byte)(c.Index % _brSegs.Length + 1);
                    for (int i = a; i < b; i++) if (_cells[i] != col) { _cells[i] = col; changed = true; }
                }
            }
            else if (p.Downloaded > 0)
            {
                // Tek akışlı / yt-dlp / HLS indirmeler için tek segment ilerlemesi
                int b = (int)Math.Clamp(p.Downloaded * (long)Cells / p.TotalSize, 0, Cells);
                for (int i = 0; i < b; i++) if (_cells[i] != 1) { _cells[i] = 1; changed = true; }
            }
        }
        if (changed) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle; r.Width -= 1; r.Height -= 1;
        g.FillRectangle(_brField, r);
        if (_all) g.FillRectangle(_brAccent, r);
        else
        {
            int i = 0;
            while (i < Cells)
            {
                if (_cells[i] == 0) { i++; continue; }
                int j = i; byte v = _cells[i];
                while (j < Cells && _cells[j] == v) j++;
                float x0 = r.X + r.Width * i / (float)Cells, x1 = r.X + r.Width * j / (float)Cells;
                g.FillRectangle(_brSegs[v - 1], x0, r.Y, Math.Max(1f, x1 - x0), r.Height);
                i = j;
            }
        }
        g.DrawRectangle(_penBorder, r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _brField.Dispose(); _brAccent.Dispose(); _penBorder.Dispose();
            foreach (var b in _brSegs) b.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// "İndirme ilerleme" penceresi (IDM Download progress tasarım ve 353x258 DLU boyutlarıyla birebir, T-Downloader koyu teması).
/// Üç sekme: İndirme durumu | Hız sınırlayıcı | Seçenekler; altında ilerleme çubuğu, segment şeridi ve bağlantı listesi.
/// </summary>
public sealed class ProgressDialog : DluForm
{
    private readonly DownloadManager _mgr;
    private readonly Action? _saveSettings;
    private readonly string _id;
    private readonly Label _url, _status, _size, _done, _speed, _eta, _resume, _connCaption;
    private readonly NumericUpDown _speedLimit;
    private readonly DarkCheck _chkCompletedDlg, _chkSound;
    private readonly ProgressBar _bar;
    private readonly SegmentBar _seg;
    private readonly BufferedListView _list;
    private readonly Button _toggle, _pause, _cancel;
    private System.Windows.Forms.Timer? _autoClose;
    private bool _detailsShown = true;
    private int _maxConn;
    private readonly UpdateGate _textGate = new();

    public string DownloadId => _id;

    public ProgressDialog(DownloadManager mgr, DownloadItem item, Action? saveSettings = null)
    {
        _mgr = mgr; _id = item.Id; _saveSettings = saveSettings;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = true; ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        SetClientDlu(353, 258);

        // IDM 3 sekmeli üst panel (338x112 DLU)
        var tabs = Add(new DarkTabs(), 7, 4, 338, 112);

        // Sekme 1: İndirme durumu
        var tabStatus = new TabPage(Strings.DownloadStatus) { BackColor = Theme.Bg, ForeColor = Theme.Text };
        _url = new Label { AutoEllipsis = true, ForeColor = Theme.TextDim, Bounds = Dlu.R(6, 5, 320, 10) };
        tabStatus.Controls.Add(_url);
        string[] names = { Strings.Status, Strings.FileSize, Strings.Downloaded, Strings.TransferSpeed2, Strings.TimeRemaining2, Strings.ResumeSupport };
        var vals = new Label[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            double y = 18 + i * 12;
            tabStatus.Controls.Add(new Label { Text = names[i], ForeColor = Theme.TextDim, Bounds = Dlu.R(6, y, 74, 10) });
            vals[i] = new Label { AutoEllipsis = true, ForeColor = Theme.Text, Bounds = Dlu.R(82, y, 244, 10) };
            tabStatus.Controls.Add(vals[i]);
        }
        (_status, _size, _done, _speed, _eta, _resume) = (vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]);

        // Sekme 2: Hız sınırlayıcı (IDM Speed Limiter)
        var tabLimit = new TabPage(Strings.TransferSpeed) { BackColor = Theme.Bg, ForeColor = Theme.Text };
        tabLimit.Controls.Add(new Label { Text = Strings.SpeedLimitKBS0Unlimited, ForeColor = Theme.Text, Bounds = Dlu.R(10, 14, 240, 12) });
        _speedLimit = new NumericUpDown
        {
            Minimum = 0, Maximum = 1_000_000, Increment = 100,
            Value = Math.Clamp(_mgr.Settings.SpeedLimitKBps, 0, 1_000_000),
            Bounds = Dlu.R(10, 30, 86, 14)
        };
        var btnApplySpeed = new Button { Text = Strings.Apply, Bounds = Dlu.R(104, 29, 60, 15) };
        btnApplySpeed.Click += (_, _) =>
        {
            _mgr.Settings.SpeedLimitKBps = (int)_speedLimit.Value;
            _saveSettings?.Invoke();
        };
        tabLimit.Controls.Add(_speedLimit);
        tabLimit.Controls.Add(btnApplySpeed);

        // Sekme 3: Tamamlanma seçenekleri (IDM Options on completion)
        var tabOpts = new TabPage(Strings.Settings2) { BackColor = Theme.Bg, ForeColor = Theme.Text };
        _chkCompletedDlg = new DarkCheck
        {
            Text = Strings.ShowTheCompletedDialogAfterADownload,
            Checked = _mgr.Settings.ShowCompletedDialog,
            Bounds = Dlu.R(10, 14, 310, 14)
        };
        _chkSound = new DarkCheck
        {
            Text = Strings.PlayASoundOnCompletion,
            Checked = _mgr.Settings.PlayCompletionSound,
            Bounds = Dlu.R(10, 34, 310, 14)
        };
        _chkCompletedDlg.CheckedChanged += (_, _) => { _mgr.Settings.ShowCompletedDialog = _chkCompletedDlg.Checked; _saveSettings?.Invoke(); };
        _chkSound.CheckedChanged += (_, _) => { _mgr.Settings.PlayCompletionSound = _chkSound.Checked; _saveSettings?.Invoke(); };
        tabOpts.Controls.Add(_chkCompletedDlg);
        tabOpts.Controls.Add(_chkSound);

        tabs.TabPages.Add(tabStatus);
        tabs.TabPages.Add(tabLimit);
        tabs.TabPages.Add(tabOpts);

        // İlerleme çubuğu + düğmeler (IDM düzeni)
        _bar = Add(new DarkProgress { Minimum = 0, Maximum = 1000 }, 7, 120, 338, 10);
        _toggle = MakeButton(Strings.HideDetails, 12, 134, 96, 15);
        _pause = MakeButton(Strings.Pause, 203, 134, 58, 15);
        _cancel = MakeButton(Strings.Cancel, 275, 134, 56, 15);

        // Bağlantı segment şeridi + bağlantı tablosu
        _connCaption = MakeLabel(Strings.StartPositionsAndDownloadProgressByConnection, 7, 152, 338, 9);
        _connCaption.ForeColor = Theme.TextDim;
        _seg = Add(new SegmentBar(), 7, 163, 338, 10);
        _list = Add(new BufferedListView { View = View.Details, FullRowSelect = true, Grid = true, HeaderStyle = ColumnHeaderStyle.Nonclickable }, 7, 177, 338, 76);
        _list.Columns.Add(Strings.ConnectionNumber, Dlu.X(24));
        _list.Columns.Add(Strings.Downloaded, Dlu.X(100));
        _list.Columns.Add(Strings.Information, Dlu.X(200));

        _toggle.Click += (_, _) => ToggleDetails();
        _pause.Click += (_, _) => TogglePause();
        _cancel.Click += (_, _) => { _mgr.Pause(_id); Close(); };
        RefreshItem(item);
    }

    public void ScheduleAutoClose(int delayMs)
    {
        if (IsDisposed) return;
        _autoClose?.Stop();
        _autoClose?.Dispose();
        _autoClose = new System.Windows.Forms.Timer { Interval = Math.Max(100, delayMs) };
        _autoClose.Tick += (_, _) =>
        {
            _autoClose?.Stop();
            if (!IsDisposed) Close();
        };
        _autoClose.Start();
    }

    private static void SetText(Control c, string v) { if (c.Text != v) c.Text = v; }

    private void ToggleDetails()
    {
        _detailsShown = !_detailsShown;
        _toggle.Text = _detailsShown ? Strings.HideDetails : Strings.ShowDetails;
        foreach (Control c in new Control[] { _connCaption, _seg, _list }) c.Visible = _detailsShown;
        ClientSize = new Size(ClientSize.Width, Dlu.Y(_detailsShown ? 258 : 154));
    }

    private void TogglePause()
    {
        var it = _mgr.Get(_id);
        if (it == null) return;
        if (it.Status == DownloadStatus.Completed) { Close(); return; }
        if (it.Status is DownloadStatus.Downloading or DownloadStatus.Queued) _mgr.Pause(_id);
        else if (it.Status is DownloadStatus.Paused or DownloadStatus.Failed) _mgr.Start(_id);
    }

    public static string StatusText(DownloadItem it) => it.Status switch
    {
        DownloadStatus.Queued => Strings.Queued,
        DownloadStatus.Downloading => Strings.Downloading,
        DownloadStatus.Paused => Strings.Paused2,
        DownloadStatus.Completed => Strings.Completed3,
        DownloadStatus.Failed => string.IsNullOrEmpty(it.Error) ? Strings.Error : Strings.ActionFailedTryAgainDetails + it.Error,
        _ => ""
    };

    public void RefreshItem(DownloadItem it)
    {
        SetText(this, $"{Percent(it)}% {it.FileName}".TrimStart());
        SetText(_url, it.Url);
        SetText(_status, StatusText(it));
        long total = it.TotalSize > 0 ? it.TotalSize : (it.Status == DownloadStatus.Completed ? it.Downloaded : 0);
        SetText(_size, total > 0 ? Fmt.SizeFull(total) : Strings.Unknown);
        SetText(_done, total > 0 ? $"{Fmt.Size(it.Downloaded)}  ({Percent(it)}%)" : Fmt.Size(it.Downloaded));
        SetBar(total > 0 ? (int)Math.Clamp(it.Downloaded * 1000 / total, 0, 1000) : (it.Status == DownloadStatus.Completed ? 1000 : 0));
        if (it.Status != DownloadStatus.Downloading) { SetText(_speed, ""); SetText(_eta, it.Status == DownloadStatus.Completed ? "-" : ""); _textGate.Reset(); }
        SetText(_pause, it.Status is DownloadStatus.Downloading or DownloadStatus.Queued ? Strings.Pause : (it.Status == DownloadStatus.Completed ? Strings.Close : Strings.Resume));
        _pause.Enabled = true;
        if (it.Status == DownloadStatus.Completed)
        {
            _seg.Apply(new DownloadProgress { Id = it.Id, Downloaded = Math.Max(1, total), TotalSize = Math.Max(1, total) }, true);
            _cancel.Enabled = false;
            if (_maxConn > 1) SetText(_resume, Strings.Yes);
        }
        else _cancel.Enabled = true;
    }

    private void SetBar(int v) { if (_bar.Value != v) _bar.Value = v; }   // aynı değerde yeniden çizim yok

    private static bool SetSub(ListViewItem li, int i, string v)
    {
        if (li.SubItems[i].Text == v) return false;
        li.SubItems[i].Text = v;
        return true;
    }

    private static int Percent(DownloadItem it) => it.TotalSize > 0 ? (int)Math.Clamp(it.Downloaded * 100 / it.TotalSize, 0, 100) : (it.Status == DownloadStatus.Completed ? 100 : 0);

    public void ApplyProgress(DownloadProgress p)
    {
        var it = _mgr.Get(_id);
        int pct = p.TotalSize > 0 ? (int)Math.Clamp(p.Downloaded * 100 / p.TotalSize, 0, 100) : 0;
        SetText(_size, p.TotalSize > 0 ? Fmt.SizeFull(p.TotalSize) : Strings.Unknown);
        SetText(_done, p.TotalSize > 0 ? $"{Fmt.Size(p.Downloaded)}  ({pct}%)" : Fmt.Size(p.Downloaded));
        bool textDue = _textGate.Due(Environment.TickCount64);   // hız / kalan süre en fazla saniyede 1
        if (textDue)
        {
            SetText(_speed, Fmt.Speed(p.SpeedBytesPerSec));
            SetText(_eta, Fmt.Eta(p.Eta));
        }
        SetBar(p.TotalSize > 0 ? (int)Math.Clamp(p.Downloaded * 1000 / p.TotalSize, 0, 1000) : 0);
        if (it != null) SetText(this, $"{pct}% {it.FileName}");
        _maxConn = Math.Max(_maxConn, p.Connections.Count);
        SetText(_resume, p.TotalSize <= 0 ? Strings.No : _maxConn > 1 ? Strings.Yes : Strings.Unknown);
        if (p.Phase is { Length: > 0 } phase) SetText(_status, phase);
        else if (it != null) SetText(_status, StatusText(it));
        _seg.Apply(p, false);

        // Harici / tek akışlı (yt-dlp/HLS) indirmelerde bağlantı satırı yoksa tek özet satırı göster
        int wantCount = p.Connections.Count > 0 ? p.Connections.Count : 1;
        bool listDirty = _list.Items.Count != wantCount;
        if (listDirty)
        {
            _list.BeginUpdate();
            while (_list.Items.Count < wantCount) _list.Items.Add(new ListViewItem(new[] { "", "", "" }));
            while (_list.Items.Count > wantCount) _list.Items.RemoveAt(_list.Items.Count - 1);
            _list.EndUpdate();
        }

        if (p.Connections.Count > 0)
        {
            for (int i = 0; i < p.Connections.Count; i++)
            {
                var c = p.Connections[i];
                var li = _list.Items[i];
                var no = (c.Index + 1).ToString(); if (li.Text != no) li.Text = no;
                SetSub(li, 1, Fmt.Size(Math.Max(0, c.Position - c.SegmentStart)));
                string info = c.State switch
                {
                    ConnectionState.Receiving => Strings.Downloading2(Fmt.Speed(c.SpeedBytesPerSec)),
                    ConnectionState.Connecting => Strings.Connecting,
                    ConnectionState.Retrying => Strings.Retrying(c.Retries),
                    ConnectionState.Done => Strings.Completed3,
                    ConnectionState.Failed => Strings.Failed,
                    _ => Strings.Idle
                };
                if (textDue || c.State != ConnectionState.Receiving || !li.SubItems[2].Text.StartsWith(Strings.Downloading))
                    SetSub(li, 2, info);
                var fc = c.State == ConnectionState.Failed ? Theme.Danger : Theme.Text;
                if (li.ForeColor != fc) li.ForeColor = fc;
            }
        }
        else if (_list.Items.Count > 0)
        {
            var li = _list.Items[0];
            if (li.Text != "1") li.Text = "1";
            SetSub(li, 1, Fmt.Size(p.Downloaded));
            string info = p.Phase is { Length: > 0 } ph ? ph : Strings.Downloading2(Fmt.Speed(p.SpeedBytesPerSec));
            if (textDue || p.Phase is { Length: > 0 }) SetSub(li, 2, info);
            if (li.ForeColor != Theme.Text) li.ForeColor = Theme.Text;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoClose?.Stop();
            _autoClose?.Dispose();
        }
        base.Dispose(disposing);
    }
}
