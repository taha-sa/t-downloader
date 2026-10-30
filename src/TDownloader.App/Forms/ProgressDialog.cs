using TDownloader.App.Ui;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;

namespace TDownloader.App.Forms;

/// <summary>Parçaların ilerlemesini bağlantı rengiyle gösteren yatay segment çubuğu.</summary>
public sealed class SegmentBar : Control
{
    private const int Cells = 2048;
    private readonly byte[] _cells = new byte[Cells];
    private bool _all;

    public SegmentBar() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); }

    public void Apply(DownloadProgress p, bool completed)
    {
        _all = completed;
        if (p.TotalSize > 0)
        {
            foreach (var c in p.Connections)
            {
                if (c.Position <= c.SegmentStart) continue;
                int a = (int)Math.Clamp(c.SegmentStart * (long)Cells / p.TotalSize, 0, Cells);
                int b = (int)Math.Clamp(c.Position * (long)Cells / p.TotalSize, 0, Cells);
                byte col = (byte)(c.Index % Theme.SegmentColors.Length + 1);
                for (int i = a; i < b; i++) _cells[i] = col;
            }
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle; r.Width -= 1; r.Height -= 1;
        using (var bg = new SolidBrush(Color.FromArgb(0xE6, 0xEB, 0xF2))) g.FillRectangle(bg, r);
        if (_all) { using var b = new SolidBrush(Theme.Teal); g.FillRectangle(b, r); }
        else
        {
            int i = 0;
            while (i < Cells)
            {
                if (_cells[i] == 0) { i++; continue; }
                int j = i; byte v = _cells[i];
                while (j < Cells && _cells[j] == v) j++;
                float x0 = r.X + r.Width * i / (float)Cells, x1 = r.X + r.Width * j / (float)Cells;
                using var b = new SolidBrush(Theme.SegmentColors[v - 1]);
                g.FillRectangle(b, x0, r.Y, Math.Max(1f, x1 - x0), r.Height);
                i = j;
            }
        }
        using var p = new Pen(Theme.Border);
        g.DrawRectangle(p, r);
    }
}

/// <summary>"İndirme ilerleme" penceresi (IDM Download progress: 353x250 DLU).</summary>
public sealed class ProgressDialog : DluForm
{
    private readonly DownloadManager _mgr;
    private readonly string _id;
    private readonly Label _url, _status, _size, _done, _speed, _eta, _resume, _connCaption;
    private readonly ProgressBar _bar;
    private readonly SegmentBar _seg;
    private readonly ListView _list;
    private readonly Button _toggle, _pause, _cancel;
    private bool _detailsShown = true;
    private int _maxConn;
    private readonly int _fullHeight, _shortHeight;

    public string DownloadId => _id;

    public ProgressDialog(DownloadManager mgr, DownloadItem item)
    {
        _mgr = mgr; _id = item.Id;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = true; ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        SetClientDlu(353, 250);
        _fullHeight = ClientSize.Height;
        _shortHeight = Dlu.Y(146);

        var grp = Add(new GroupBox { Text = "İndirme durumu" }, 7, 3, 338, 104);
        _url = new Label { AutoEllipsis = true, Bounds = new Rectangle(Dlu.X(6), Dlu.Y(11), Dlu.X(325), Dlu.Y(10)) };
        grp.Controls.Add(_url);
        string[] names = { "Durum", "Dosya boyutu", "İndirilen", "Aktarım hızı", "Kalan süre", "Devam edebilir" };
        var vals = new Label[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            double y = 25 + i * 12;
            grp.Controls.Add(new Label { Text = names[i], Bounds = Dlu.R(6, y, 70, 10) });
            vals[i] = new Label { AutoEllipsis = true, Bounds = Dlu.R(80, y, 250, 10) };
            grp.Controls.Add(vals[i]);
        }
        (_status, _size, _done, _speed, _eta, _resume) = (vals[0], vals[1], vals[2], vals[3], vals[4], vals[5]);

        _bar = Add(new ProgressBar { Minimum = 0, Maximum = 1000 }, 7, 111, 338, 10);
        _toggle = MakeButton("<< Detayları gizle", 15, 126, 93, 15);
        _pause = MakeButton("Duraklat", 203, 126, 56, 15);
        _cancel = MakeButton("İptal", 277, 126, 50, 15);
        _connCaption = MakeLabel("Bağlantılara göre başlangıç konumları ve indirme ilerlemesi", 7, 144, 338, 9);
        _seg = Add(new SegmentBar(), 7, 155, 338, 10);
        _list = Add(new BufferedListView { View = View.Details, FullRowSelect = true, GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable }, 7, 169, 338, 77);
        _list.Columns.Add("N.", Dlu.X(24));
        _list.Columns.Add("İndirilen", Dlu.X(100));
        _list.Columns.Add("Bilgi", Dlu.X(200));

        _toggle.Click += (_, _) => ToggleDetails();
        _pause.Click += (_, _) => TogglePause();
        _cancel.Click += (_, _) => { _mgr.Pause(_id); Close(); };
        RefreshItem(item);
    }

    private void ToggleDetails()
    {
        _detailsShown = !_detailsShown;
        _toggle.Text = _detailsShown ? "<< Detayları gizle" : "Detayları göster >>";
        foreach (Control c in new Control[] { _connCaption, _seg, _list }) c.Visible = _detailsShown;
        ClientSize = new Size(ClientSize.Width, Dlu.Y(_detailsShown ? 250 : 146));
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
        DownloadStatus.Queued => "Sırada",
        DownloadStatus.Downloading => "Alınıyor",
        DownloadStatus.Paused => "Duraklatıldı",
        DownloadStatus.Completed => "Tamamlandı",
        DownloadStatus.Failed => string.IsNullOrEmpty(it.Error) ? "Hata" : "Hata: " + it.Error,
        _ => ""
    };

    public void RefreshItem(DownloadItem it)
    {
        Text = $"{Percent(it)}% {it.FileName}".TrimStart();
        _url.Text = it.Url;
        _status.Text = StatusText(it);
        _size.Text = it.TotalSize > 0 ? Fmt.SizeFull(it.TotalSize) : "Bilinmiyor";
        _done.Text = it.TotalSize > 0 ? $"{Fmt.Size(it.Downloaded)}  ({Percent(it)}%)" : Fmt.Size(it.Downloaded);
        _bar.Value = it.TotalSize > 0 ? (int)Math.Clamp(it.Downloaded * 1000 / it.TotalSize, 0, 1000) : 0;
        if (it.Status != DownloadStatus.Downloading) { _speed.Text = ""; _eta.Text = it.Status == DownloadStatus.Completed ? "-" : ""; }
        _pause.Text = it.Status is DownloadStatus.Downloading or DownloadStatus.Queued ? "Duraklat" : (it.Status == DownloadStatus.Completed ? "Kapat" : "Devam");
        _pause.Enabled = true;
        if (it.Status == DownloadStatus.Completed)
        {
            _seg.Apply(new DownloadProgress { Id = it.Id, Downloaded = it.TotalSize, TotalSize = it.TotalSize }, true);
            _cancel.Enabled = false;
            _resume.Text = _maxConn > 1 ? "Evet" : _resume.Text;
        }
        else _cancel.Enabled = true;
    }

    private static int Percent(DownloadItem it) => it.TotalSize > 0 ? (int)Math.Clamp(it.Downloaded * 100 / it.TotalSize, 0, 100) : 0;

    public void ApplyProgress(DownloadProgress p)
    {
        var it = _mgr.Get(_id);
        _size.Text = p.TotalSize > 0 ? Fmt.SizeFull(p.TotalSize) : "Bilinmiyor";
        _done.Text = p.TotalSize > 0 ? $"{Fmt.Size(p.Downloaded)}  ({(int)(p.Downloaded * 100 / p.TotalSize)}%)" : Fmt.Size(p.Downloaded);
        _speed.Text = Fmt.Speed(p.SpeedBytesPerSec);
        _eta.Text = Fmt.Eta(p.Eta);
        _bar.Value = p.TotalSize > 0 ? (int)Math.Clamp(p.Downloaded * 1000 / p.TotalSize, 0, 1000) : 0;
        if (it != null) Text = $"{(p.TotalSize > 0 ? (int)(p.Downloaded * 100 / p.TotalSize) : 0)}% {it.FileName}";
        _maxConn = Math.Max(_maxConn, p.Connections.Count);
        _resume.Text = p.TotalSize <= 0 ? "Hayır" : _maxConn > 1 ? "Evet" : "Bilinmiyor";
        if (it != null) _status.Text = StatusText(it);
        _seg.Apply(p, false);

        _list.BeginUpdate();
        while (_list.Items.Count < p.Connections.Count) _list.Items.Add(new ListViewItem(new[] { "", "", "" }));
        while (_list.Items.Count > p.Connections.Count) _list.Items.RemoveAt(_list.Items.Count - 1);
        for (int i = 0; i < p.Connections.Count; i++)
        {
            var c = p.Connections[i];
            var li = _list.Items[i];
            li.Text = (c.Index + 1).ToString();
            li.SubItems[1].Text = Fmt.Size(Math.Max(0, c.Position - c.SegmentStart));
            li.SubItems[2].Text = c.State switch
            {
                ConnectionState.Receiving => $"Alınıyor  {Fmt.Speed(c.SpeedBytesPerSec)}",
                ConnectionState.Connecting => "Bağlanıyor...",
                ConnectionState.Retrying => $"Yeniden deneniyor ({c.Retries})",
                ConnectionState.Done => "Tamamlandı",
                ConnectionState.Failed => "Başarısız",
                _ => "Boşta"
            };
            li.ForeColor = c.State == ConnectionState.Failed ? Color.Firebrick : SystemColors.WindowText;
        }
        _list.EndUpdate();
    }
}
