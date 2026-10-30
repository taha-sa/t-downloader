using TDownloader.App.Ui;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.App.Forms;

/// <summary>"Zamanlayıcı" basit sürümü (IDM Scheduler: 434x286 DLU).</summary>
public sealed class SchedulerDialog : DluForm
{
    private readonly AppSettings _s;
    private readonly Action<AppSettings> _apply;
    private readonly DownloadManager _mgr;
    private readonly CheckBox _enable;
    private readonly DateTimePicker _time;
    private readonly CheckBox[] _days = new CheckBox[7];
    private readonly ComboBox _finish;
    private readonly ListView _files;
    private readonly Action _queueStarted;

    private static readonly (string Name, int Dow)[] DayList =
        { ("Pazartesi", 1), ("Salı", 2), ("Çarşamba", 3), ("Perşembe", 4), ("Cuma", 5), ("Cumartesi", 6), ("Pazar", 0) };

    public SchedulerDialog(DownloadManager mgr, Action<AppSettings> apply, Action queueStarted)
    {
        _mgr = mgr; _s = mgr.Settings; _apply = apply; _queueStarted = queueStarted;
        Text = "Zamanlayıcı";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        SetClientDlu(434, 286);

        MakeLabel("Kuyruklar", 7, 14, 115, 10);
        var tree = Add(new TreeView { HideSelection = false, ShowLines = false }, 7, 27, 115, 233);
        tree.Nodes.Add("Ana kuyruk");
        tree.SelectedNode = tree.Nodes[0];

        var grp = Add(new GroupBox { Text = "Ana kuyruk" }, 133, 8, 294, 252);
        T Put<T>(T c, double x, double y, double w, double h) where T : Control { c.Bounds = Dlu.R(x, y, w, h); grp.Controls.Add(c); return c; }

        _enable = Put(new CheckBox { Text = "Şu zamanda başlat:", AutoEllipsis = true }, 8, 14, 110, 11);
        _time = Put(new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true }, 120, 12, 50, 14);
        Put(new Label { Text = "Günler:" }, 8, 34, 60, 10);
        for (int i = 0; i < 7; i++)
        {
            _days[i] = Put(new CheckBox { Text = DayList[i].Name, AutoEllipsis = true }, 8 + (i % 4) * 68, 46 + (i / 4) * 13, 66, 11);
            _days[i].Checked = _s.Scheduler.Days.Contains(DayList[i].Dow);
        }
        Put(new Label { Text = "Kuyruk bitince:" }, 8, 78, 70, 10);
        _finish = Put(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }, 80, 76, 130, 64);
        _finish.Items.AddRange(new object[] { "Hiçbir şey yapma", "Bildirim göster", "Uygulamadan çık" });
        _finish.SelectedIndex = _s.Scheduler.OnFinished switch { "exit" => 2, "notify" => 1, _ => 0 };
        Put(new Label { Text = "Kuyruktaki dosyalar:" }, 8, 98, 120, 10);
        _files = Put(new BufferedListView { View = View.Details, FullRowSelect = true }, 8, 110, 278, 134);
        _files.Columns.Add("Dosya adı", Dlu.X(130));
        _files.Columns.Add("Boyut", Dlu.X(50));
        _files.Columns.Add("Durum", Dlu.X(70));

        if (TimeSpan.TryParse(_s.Scheduler.StartTime, out var ts)) _time.Value = DateTime.Today + ts;
        _enable.Checked = _s.Scheduler.Enabled;
        foreach (var it in _mgr.Items.Where(i => i.Status != DownloadStatus.Completed))
            _files.Items.Add(new ListViewItem(new[] { it.FileName, Fmt.Size(it.TotalSize), ProgressDialog.StatusText(it) }));

        var startNow = MakeButton("Şimdi başlat", 133, 266, 50, 14);
        var stop = MakeButton("Durdur", 187, 266, 50, 14);
        var apply2 = MakeButton("Uygula", 321, 266, 50, 14);
        var close = MakeButton("Kapat", 375, 266, 50, 14, DialogResult.Cancel);
        CancelButton = close;
        startNow.Click += (_, _) => { _queueStarted(); _mgr.StartAll(); };
        stop.Click += (_, _) => _mgr.PauseAll();
        apply2.Click += (_, _) => Save();
    }

    private void Save()
    {
        var sc = _s.Scheduler;
        sc.Enabled = _enable.Checked;
        sc.StartTime = _time.Value.ToString("HH:mm");
        sc.Days = DayList.Where((d, i) => _days[i].Checked).Select(d => d.Dow).ToList();
        sc.OnFinished = _finish.SelectedIndex switch { 2 => "exit", 1 => "notify", _ => "none" };
        _apply(_s);
    }
}
