using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TDownloader.App.Services;
using TDownloader.App.Ui;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.App.Forms;

/// <summary>Ana pencere: menü, 12 düğmeli araç çubuğu, kategori ağacı, indirme listesi, durum çubuğu, tepsi simgesi.</summary>
public sealed class MainForm : Form
{
    private sealed record Col(string Key, string Title, int Width, HorizontalAlignment Align = HorizontalAlignment.Left);

    private static readonly Col[] Cols =
    {
        new("name", "Dosya Adı", 230), new("q", "K", 24, HorizontalAlignment.Center), new("size", "Boyut", 72, HorizontalAlignment.Right),
        new("status", "Durum", 100), new("eta", "Kalan Süre", 82), new("speed", "Aktarım Hızı", 86),
        new("last", "Son Deneme Tarihi", 118), new("added", "Eklenme Tarihi", 118), new("desc", "Açıklama", 160),
    };

    private readonly AppServices _svc;
    private readonly DownloadManager _mgr;
    private readonly SchedulerService _sched;
    private readonly float _s;
    private readonly ShellIcons _shell = new();
    private readonly NotifyIcon _tray;
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _tool = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _stMain = new("Hazır") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _stActive = new("Aktif: 0") { AutoSize = false, Width = 90, BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _stSpeed = new("Hız: 0 B/sn") { AutoSize = false, Width = 130, BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _stTotal = new("Toplam: 0") { AutoSize = false, Width = 90, BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly SplitContainer _split = new();
    private readonly Panel _catPanel = new();
    private readonly TreeView _tree = new();
    private readonly BufferedListView _list = new();
    private readonly ContextMenuStrip _ctx = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private ToolStripMenuItem? _viewCats;

    private readonly Dictionary<string, ListViewItem> _rows = new();
    private readonly ConcurrentDictionary<string, DownloadProgress> _progress = new();
    private readonly HashSet<string> _dirty = new();
    private readonly object _dirtyLock = new();
    private readonly Dictionary<string, ProgressDialog> _dialogs = new();
    private string _filter = "all";
    private int _sortCol = -1;
    private bool _sortAsc = true;
    private bool _exiting;
    private bool _startHidden;
    private bool _trayHintShown;
    private string _lastClip = "";
    private bool _clipBusy;
    private string? _extensionDir;

    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    public MainForm(AppServices svc, bool startHidden)
    {
        _svc = svc; _mgr = svc.Manager; _startHidden = startHidden;
        AutoScaleMode = AutoScaleMode.None;
        _s = Dlu.Scale;
        Font = Theme.UiFont();
        Text = "T-Downloader";
        Icon = Icons.AppIcon();
        // IDM ana pencere: 532x209 DLU liste+ağaç alanı (798 px genişlik); yükseklik pratik amaçlı büyütüldü.
        ClientSize = new Size(Dlu.X(532), Scaled(500));
        MinimumSize = new Size(Scaled(560), Scaled(320));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildBody();
        BuildStatus();
        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_tool);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _tray = BuildTray();
        _sched = new SchedulerService(svc);
        _sched.Notify += msg => _tray.ShowBalloonTip(4000, "Zamanlayıcı", msg, ToolTipIcon.Info);
        _sched.ExitRequested += ExitApp;

        _extensionDir = FindExtensionDir();
        RestoreUiState();
        RebuildList();

        _mgr.ItemAdded += (_, i) => UiPost(() => AddRow(i));
        _mgr.ItemRemoved += (_, i) => UiPost(() => RemoveRow(i.Id));
        _mgr.ItemChanged += (_, i) => MarkDirty(i.Id);
        _mgr.ProgressChanged += (_, p) => { _progress[p.Id] = p; MarkDirty(p.Id); };
        _mgr.DownloadCompleted += (_, i) => UiPost(() => OnCompleted(i));
        _mgr.DownloadFailed += (_, i) => UiPost(() => { MarkDirty(i.Id); _tray.ShowBalloonTip(4000, "İndirme başarısız", $"{i.FileName}\n{i.Error}", ToolTipIcon.Error); });
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
        _ = Handle; // gizli başlangıçta bile tutamaç (pano dinleyici, BeginInvoke) hazır olsun
    }

    private int Scaled(int px) => (int)Math.Round(px * _s);

    // ------------------------------------------------------------ gizli başlangıç / öne getirme

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    public void Notify(string title, string message) => UiPost(() => _tray.ShowBalloonTip(3000, title, message, ToolTipIcon.Info));

    public void ShowMain()
    {
        _startHidden = false;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        ForceForeground(Handle);
        Activate();
    }

    public static void ForceForeground(IntPtr h)
    {
        ShowWindow(h, 9);
        SetForegroundWindow(h);
    }

    private void UiPost(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void MarkDirty(string id) { lock (_dirtyLock) _dirty.Add(id); }

    // ------------------------------------------------------------ menü

    private ToolStripMenuItem Mi(string text, Action click, Keys keys = Keys.None, string? icon = null)
    {
        var m = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        if (icon != null) m.Image = Icons.Get(icon, Scaled(16));
        m.Click += (_, _) => click();
        return m;
    }

    private void BuildMenu()
    {
        _menu.Renderer = new ThemeRenderer();
        _menu.ImageScalingSize = new Size(Scaled(16), Scaled(16));

        var tasks = new ToolStripMenuItem("&Görevler");
        tasks.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("Yeni &URL ekle...", AddUrl, Keys.Control | Keys.N, "add"),
            Mi("&Panodan URL ekle", AddFromClipboard, Keys.Control | Keys.V),
            new ToolStripSeparator(),
            Mi("&Tarayıcı eklentisi klasörünü aç", OpenExtensionFolder, Keys.None, "addon"),
            new ToolStripSeparator(),
            Mi("Çı&kış", ExitApp),
        });

        var file = new ToolStripMenuItem("&Dosya");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("&Durdur", StopSelected, Keys.None, "stop"),
            Mi("&Şimdi indir / Devam et", ResumeSelected, Keys.None, "resume"),
            Mi("&Sil", DeleteSelected, Keys.Delete, "delete"),
            new ToolStripSeparator(),
            Mi("Dosyayı &aç", OpenSelectedFile),
            Mi("&Klasörü aç", ShowSelectedInFolder),
            Mi("İ&lerleme penceresi", ShowSelectedProgress),
        });

        var dl = new ToolStripMenuItem("İ&ndirmeler");
        dl.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("&Tümünü durdur", _mgr.PauseAll, Keys.None, "stopall"),
            new ToolStripSeparator(),
            Mi("Tamamlananları &sil", DeleteCompleted, Keys.None, "deletedone"),
            new ToolStripSeparator(),
            Mi("&Zamanlayıcı...", ShowScheduler, Keys.None, "scheduler"),
            Mi("Kuyruğu &başlat", StartQueue, Keys.None, "queuestart"),
            Mi("Kuyruğu d&urdur", StopQueue, Keys.None, "queuestop"),
            new ToolStripSeparator(),
            Mi("&Seçenekler...", ShowOptions, Keys.None, "options"),
        });

        var view = new ToolStripMenuItem("G&örünüm");
        _viewCats = Mi("Kategorileri &gizle", ToggleCategories);
        var sort = new ToolStripMenuItem("Dosyaları &sırala");
        string[] keys = { "added", "name", "size", "status", "eta", "speed", "last", "desc" };
        string[] titles = { "Eklenme sırasına göre", "Dosya adına göre", "Boyuta göre", "Duruma göre", "Kalan süreye göre", "Aktarım hızına göre", "Son deneme tarihine göre", "Açıklamaya göre" };
        for (int i = 0; i < keys.Length; i++)
        {
            int ci = Array.FindIndex(Cols, c => c.Key == keys[i]);
            sort.DropDownItems.Add(Mi(titles[i], () => SortBy(ci, true)));
        }
        view.DropDownItems.AddRange(new ToolStripItem[] { _viewCats, sort });

        var help = new ToolStripMenuItem("&Yardım");
        help.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("&Tarayıcı eklentisi", OpenExtensionFolder, Keys.None, "addon"),
            new ToolStripSeparator(),
            Mi("T-Downloader &hakkında", ShowAbout, Keys.None, "about"),
        });

        _menu.Items.AddRange(new ToolStripItem[] { tasks, file, dl, view, help });
    }

    // ------------------------------------------------------------ araç çubuğu (12 düğme, 56x42)

    private void BuildToolbar()
    {
        _tool.Renderer = new ThemeRenderer();
        _tool.GripStyle = ToolStripGripStyle.Hidden;
        _tool.AutoSize = false;
        _tool.Height = Scaled(42) + 2;
        _tool.ImageScalingSize = new Size(Scaled(24), Scaled(24));
        _tool.Padding = new Padding(2, 0, 2, 0);
        _tool.BackColor = Theme.Surface;

        void B(string text, string tip, string icon, Action click)
        {
            var b = new ToolStripButton(text, Icons.Get(icon, Scaled(24)))
            {
                AutoSize = false, Size = new Size(Scaled(56), Scaled(42)),
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                TextImageRelation = TextImageRelation.ImageAboveText,
                ImageAlign = ContentAlignment.TopCenter, TextAlign = ContentAlignment.BottomCenter,
                ToolTipText = tip, Margin = new Padding(0), ImageScaling = ToolStripItemImageScaling.None,
                Padding = new Padding(0, 3, 0, 1),
            };
            b.Click += (_, _) => click();
            _tool.Items.Add(b);
        }
        B("URL Ekle", "Yeni indirme ekle (Ctrl+N)", "add", AddUrl);
        B("Devam", "Seçili indirmeleri başlat / devam ettir", "resume", ResumeSelected);
        B("Durdur", "Seçili indirmeleri durdur", "stop", StopSelected);
        B("Tümü Dur.", "Tüm indirmeleri durdur", "stopall", _mgr.PauseAll);
        B("Sil", "Seçili indirmeleri listeden sil", "delete", DeleteSelected);
        B("Temizle", "Tamamlananları listeden sil", "deletedone", DeleteCompleted);
        B("Ayarlar", "Seçenekler", "options", ShowOptions);
        B("Zamanla", "Zamanlayıcı ve indirme kuyruğu", "scheduler", ShowScheduler);
        B("K. Başlat", "Kuyruğu başlat", "queuestart", StartQueue);
        B("K. Durdur", "Kuyruğu durdur (bekleyenleri)", "queuestop", StopQueue);
        B("Eklenti", "Tarayıcı eklentisi", "addon", OpenExtensionFolder);
        B("Hakkında", "T-Downloader hakkında", "about", ShowAbout);
    }

    // ------------------------------------------------------------ gövde

    private void BuildBody()
    {
        _split.Dock = DockStyle.Fill;
        _split.FixedPanel = FixedPanel.Panel1;
        _split.SplitterWidth = Scaled(4);
        _split.Panel1MinSize = Scaled(60);

        // kategori paneli
        _catPanel.Dock = DockStyle.Fill;
        var caption = new Panel { Dock = DockStyle.Top, Height = Scaled(20), BackColor = Theme.Navy };
        var cl = new Label { Text = "Kategoriler", ForeColor = Color.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Scaled(6), 0, 0, 0) };
        var close = new Button { Text = "×", Dock = DockStyle.Right, Width = Scaled(20), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, TabStop = false };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Theme.NavyLight;
        close.Click += (_, _) => ToggleCategories();
        caption.Controls.Add(cl); caption.Controls.Add(close);

        var il = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(Scaled(16), Scaled(16)) };
        foreach (var n in new[] { "all", "folder", "unfinished", "finished", "queues" }) il.Images.Add(n, Icons.Get(n, Scaled(16)));
        _tree.ImageList = il;
        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.BorderStyle = BorderStyle.None;
        _tree.ItemHeight = Scaled(20);
        TreeNode N(string t, string tag, string icon) => new(t) { Tag = tag, ImageKey = icon, SelectedImageKey = icon };
        var all = N("Tüm İndirmeler", "all", "all");
        foreach (var c in new[] { Categories.Compressed, Categories.Documents, Categories.Music, Categories.Programs, Categories.Video })
            all.Nodes.Add(N(c, "cat:" + c, "folder"));
        _tree.Nodes.Add(all);
        _tree.Nodes.Add(N("Tamamlanmayan", "unfinished", "unfinished"));
        _tree.Nodes.Add(N("Tamamlanan", "finished", "finished"));
        var q = N("Kuyruklar", "queue", "queues");
        q.Nodes.Add(N("Ana kuyruk", "queue", "queues"));
        _tree.Nodes.Add(q);
        all.Expand(); q.Expand();
        _tree.SelectedNode = all;
        _tree.AfterSelect += (_, e) => { _filter = (string)(e.Node?.Tag ?? "all"); RebuildList(); };
        _catPanel.Controls.Add(_tree);
        _catPanel.Controls.Add(caption);
        _split.Panel1.Controls.Add(_catPanel);

        // liste
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.AllowColumnReorder = true;
        _list.HideSelection = false;
        _list.GridLines = false;
        _list.SmallImageList = _shell.Images;
        foreach (var c in Cols) _list.Columns.Add(new ColumnHeader { Name = c.Key, Text = c.Title, Width = Scaled(c.Width), TextAlign = c.Align });
        _list.ColumnClick += (_, e) => SortBy(e.Column, _sortCol != e.Column || !_sortAsc);
        _list.DoubleClick += (_, _) => OnListDoubleClick();
        _list.SelectedIndexChanged += (_, _) => UpdateStatus();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.A && e.Control) foreach (ListViewItem i in _list.Items) i.Selected = true; };

        _ctx.Items.AddRange(new ToolStripItem[]
        {
            Mi("Aç", OpenSelectedFile), Mi("Klasörde göster", ShowSelectedInFolder), new ToolStripSeparator(),
            Mi("Devam et", ResumeSelected, Keys.None, "resume"), Mi("Durdur", StopSelected, Keys.None, "stop"),
            Mi("İlerleme penceresi", ShowSelectedProgress), new ToolStripSeparator(), Mi("Sil", DeleteSelected, Keys.None, "delete"),
        });
        _ctx.Renderer = new ThemeRenderer();
        _list.ContextMenuStrip = _ctx;
        _split.Panel2.Controls.Add(_list);
        _split.SplitterDistance = Dlu.X(102) - Scaled(6);
    }

    private void BuildStatus()
    {
        _status.Renderer = new ThemeRenderer();
        _status.SizingGrip = true;
        foreach (var s in new[] { _stActive, _stSpeed, _stTotal }) { s.Width = Scaled(s.Width); s.TextAlign = ContentAlignment.MiddleLeft; }
        _status.Items.AddRange(new ToolStripItem[] { _stMain, _stActive, _stSpeed, _stTotal });
    }

    private NotifyIcon BuildTray()
    {
        var m = new ContextMenuStrip { Renderer = new ThemeRenderer() };
        m.Items.Add(Mi("&Aç", ShowMain));
        m.Items.Add(Mi("&URL Ekle...", () => { ShowMain(); AddUrl(); }, Keys.None, "add"));
        m.Items.Add(Mi("&Tümünü Durdur", _mgr.PauseAll, Keys.None, "stopall"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Mi("Çı&kış", ExitApp));
        var n = new NotifyIcon { Icon = Icons.AppIcon(), Text = "T-Downloader", ContextMenuStrip = m, Visible = true };
        n.DoubleClick += (_, _) => ShowMain();
        return n;
    }

    // ------------------------------------------------------------ liste yönetimi

    private bool Match(DownloadItem i) => _filter switch
    {
        "all" => true,
        "unfinished" => i.Status != DownloadStatus.Completed,
        "finished" => i.Status == DownloadStatus.Completed,
        "queue" => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading,
        var f when f.StartsWith("cat:") => i.Category == f[4..],
        _ => true,
    };

    private void RebuildList()
    {
        _list.BeginUpdate();
        _list.Items.Clear(); _rows.Clear();
        foreach (var i in _mgr.Items.OrderBy(x => x.AddedAt)) if (Match(i)) AddRowCore(i);
        ApplySort();
        _list.EndUpdate();
        UpdateStatus();
    }

    private void AddRow(DownloadItem i) { if (Match(i) && !_rows.ContainsKey(i.Id)) { AddRowCore(i); UpdateStatus(); } }

    private void AddRowCore(DownloadItem i)
    {
        var li = new ListViewItem(new string[Cols.Length]) { Tag = i, Name = i.Id };
        _rows[i.Id] = li;
        FillRow(li, i);
        _list.Items.Add(li);
    }

    private void RemoveRow(string id)
    {
        if (_rows.Remove(id, out var li)) li.Remove();
        _progress.TryRemove(id, out _);
        if (_dialogs.TryGetValue(id, out var d)) d.Close();
        UpdateStatus();
    }

    private static string StatusText(DownloadItem it, bool withPct)
    {
        string pct = it.TotalSize > 0 ? $"{it.Downloaded * 100.0 / it.TotalSize:0.##}%" : "";
        return it.Status switch
        {
            DownloadStatus.Completed => "Tamamlandı",
            DownloadStatus.Downloading => pct.Length > 0 ? pct : "Alınıyor",
            DownloadStatus.Queued => "Sırada",
            DownloadStatus.Paused => it.Downloaded > 0 && pct.Length > 0 ? "Duraklatıldı " + pct : "Duraklatıldı",
            DownloadStatus.Failed => "Hata",
            _ => "",
        };
    }

    private void FillRow(ListViewItem li, DownloadItem it)
    {
        _progress.TryGetValue(it.Id, out var p);
        bool active = it.Status == DownloadStatus.Downloading;
        li.ImageIndex = _shell.IndexFor(it.FileName);
        li.SubItems[0].Text = it.FileName;
        li.SubItems[1].Text = it.Status is DownloadStatus.Queued or DownloadStatus.Downloading ? "•" : "";
        li.SubItems[2].Text = it.TotalSize > 0 ? Fmt.Size(it.TotalSize) : "";
        li.SubItems[3].Text = StatusText(it, true);
        li.SubItems[4].Text = active && p != null ? Fmt.Eta(p.Eta) : "";
        li.SubItems[5].Text = active ? Fmt.Speed(p?.SpeedBytesPerSec ?? it.SpeedBytesPerSec) : "";
        li.SubItems[6].Text = Fmt.Date(it.LastTryAt);
        li.SubItems[7].Text = Fmt.Date(it.AddedAt);
        li.SubItems[8].Text = it.Description ?? "";
        li.ForeColor = it.Status == DownloadStatus.Failed ? Color.Firebrick : SystemColors.WindowText;
    }

    private void Flush()
    {
        string[] ids;
        lock (_dirtyLock) { ids = _dirty.ToArray(); _dirty.Clear(); }
        if (ids.Length > 0)
        {
            _list.BeginUpdate();
            foreach (var id in ids)
            {
                var it = _mgr.Get(id);
                if (it == null) continue;
                if (_rows.TryGetValue(id, out var li))
                {
                    if (!Match(it)) { li.Remove(); _rows.Remove(id); }
                    else FillRow(li, it);
                }
                else if (Match(it)) AddRowCore(it);
                if (_dialogs.TryGetValue(id, out var dlg))
                {
                    dlg.RefreshItem(it);
                    if (_progress.TryGetValue(id, out var p) && it.Status == DownloadStatus.Downloading) dlg.ApplyProgress(p);
                }
            }
            if (_sortCol == Array.FindIndex(Cols, c => c.Key == "status") || _sortCol >= 0) { /* sıralama yalnızca kullanıcı isteyince */ }
            _list.EndUpdate();
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var items = _mgr.Items;
        int active = items.Count(i => i.Status == DownloadStatus.Downloading);
        double speed = items.Where(i => i.Status == DownloadStatus.Downloading)
            .Sum(i => _progress.TryGetValue(i.Id, out var p) ? p.SpeedBytesPerSec : 0);
        _stActive.Text = $"Aktif: {active}";
        _stSpeed.Text = "Hız: " + (speed > 0 ? Fmt.Speed(speed) : "0 B/sn");
        _stTotal.Text = $"Toplam: {items.Count}";
        _stMain.Text = _list.SelectedItems.Count > 0 ? $"{_list.SelectedItems.Count} öğe seçili" : "Hazır";
    }

    private void OnCompleted(DownloadItem i)
    {
        MarkDirty(i.Id);
        _tray.ShowBalloonTip(3000, "İndirme tamamlandı", i.FileName, ToolTipIcon.Info);
        if (_dialogs.TryGetValue(i.Id, out var d))
        {
            d.RefreshItem(i);
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (!d.IsDisposed) d.Close(); };
            t.Start();
        }
    }

    // ------------------------------------------------------------ sıralama

    private sealed class Cmp : System.Collections.IComparer
    {
        public int Col; public bool Asc; public string Key = "";
        public int Compare(object? a, object? b)
        {
            var x = (DownloadItem)((ListViewItem)a!).Tag!; var y = (DownloadItem)((ListViewItem)b!).Tag!;
            int r = Key switch
            {
                "name" => string.Compare(x.FileName, y.FileName, StringComparison.CurrentCultureIgnoreCase),
                "size" => x.TotalSize.CompareTo(y.TotalSize),
                "status" => x.Status.CompareTo(y.Status),
                "speed" => x.SpeedBytesPerSec.CompareTo(y.SpeedBytesPerSec),
                "last" => Nullable.Compare(x.LastTryAt, y.LastTryAt),
                "desc" => string.Compare(x.Description, y.Description, StringComparison.CurrentCultureIgnoreCase),
                "q" => x.Status.CompareTo(y.Status),
                _ => x.AddedAt.CompareTo(y.AddedAt),
            };
            return Asc ? r : -r;
        }
    }

    private void SortBy(int col, bool asc)
    {
        if (col < 0) return;
        _sortCol = col; _sortAsc = asc;
        ApplySort();
    }

    private void ApplySort()
    {
        if (_sortCol < 0) { _list.ListViewItemSorter = null; return; }
        _list.ListViewItemSorter = new Cmp { Col = _sortCol, Asc = _sortAsc, Key = Cols[_sortCol].Key };
        _list.Sort();
        _list.ListViewItemSorter = null;
    }

    // ------------------------------------------------------------ eylemler

    private List<DownloadItem> Selected() => _list.SelectedItems.Cast<ListViewItem>().Select(l => (DownloadItem)l.Tag!).ToList();

    private void AddUrl()
    {
        string? initial = null;
        try
        {
            if (Clipboard.ContainsText()) { var t = Clipboard.GetText().Trim(); if (IsHttp(t)) initial = t; }
        }
        catch (ExternalException) { }
        using var d = new AddUrlDialog(initial);
        if (d.ShowDialog(Visible ? this : null) == DialogResult.OK) ShowFileInfo(d.Url, null, null, null, null, -1);
    }

    private void AddFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText() && IsHttp(Clipboard.GetText().Trim())) ShowFileInfo(Clipboard.GetText().Trim(), null, null, null, null, -1);
            else MessageBox.Show(this, "Panoda geçerli bir http/https bağlantısı yok.", "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (ExternalException) { }
    }

    private static bool IsHttp(string t) => Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && !t.Contains('\n');

    /// <summary>Tarayıcı yakalama yolu (IPC): ayarlara göre diyaloğu gösterir. UI thread dışından çağrılabilir.</summary>
    public Task<bool> PromptDownloadAsync(string url, string? referrer, string? cookies, string? userAgent, string? fileName, long fileSize)
    {
        var tcs = new TaskCompletionSource<bool>();
        UiPost(() =>
        {
            try
            {
                if (!IsHttp(url)) { tcs.SetResult(false); return; }
                if (!_mgr.Settings.ShowFileInfoOnCapture)
                {
                    var item = _mgr.Add(new AddDownloadRequest(url, fileName, null, referrer, cookies, userAgent, null, fileSize, true));
                    OpenProgress(item);
                    tcs.SetResult(true);
                }
                else tcs.SetResult(ShowFileInfo(url, referrer, cookies, userAgent, fileName, fileSize));
            }
            catch (Exception) { tcs.TrySetResult(false); }
        });
        return tcs.Task;
    }

    private bool ShowFileInfo(string url, string? referrer, string? cookies, string? userAgent, string? fileName, long fileSize)
    {
        using var dlg = new FileInfoDialog(_mgr, url, fileName, fileSize) { TopMost = true };
        dlg.Shown += (_, _) => { dlg.Activate(); ForceForeground(dlg.Handle); };
        var r = dlg.ShowDialog(Visible ? this : null);
        if (r is not (DialogResult.Yes or DialogResult.No)) return false;
        try
        {
            var item = _mgr.Add(new AddDownloadRequest(dlg.Url, dlg.FileName, dlg.SaveFolder, referrer, cookies, userAgent,
                dlg.Description, dlg.FileSize > 0 ? dlg.FileSize : fileSize, dlg.StartNow));
            if (dlg.StartNow) OpenProgress(item);
            return true;
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private void OpenProgress(DownloadItem item)
    {
        if (item.Kind == DownloadKind.External) return;   // yt-dlp öğeleri ana listede izlenir
        if (_dialogs.TryGetValue(item.Id, out var ex)) { ex.Show(); ex.Activate(); return; }
        var d = new ProgressDialog(_mgr, item);
        _dialogs[item.Id] = d;
        d.FormClosed += (_, _) => _dialogs.Remove(item.Id);
        d.Show();
        d.Activate();
    }

    private void ShowSelectedProgress() { foreach (var i in Selected().Take(3)) OpenProgress(i); }

    private void ResumeSelected() { foreach (var i in Selected()) { _mgr.Start(i.Id); if (i.Status != DownloadStatus.Completed) OpenProgress(i); } }
    private void StopSelected() { foreach (var i in Selected()) _mgr.Pause(i.Id); }

    private async void DeleteSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (MessageBox.Show(this, $"{sel.Count} indirme listeden silinsin mi?\n(Diskteki dosyalar silinmez.)", "Sil", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var i in sel) await _mgr.RemoveAsync(i.Id, false);
    }

    private async void DeleteCompleted()
    {
        var done = _mgr.Items.Where(i => i.Status == DownloadStatus.Completed).ToList();
        if (done.Count == 0) { MessageBox.Show(this, "Tamamlanmış indirme yok.", "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (MessageBox.Show(this, $"{done.Count} tamamlanmış indirme listeden silinsin mi?\n(Diskteki dosyalar silinmez.)", "Tamamlananları sil", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var i in done) await _mgr.RemoveAsync(i.Id, false);
    }

    private void StartQueue() { _sched.WatchQueue(); _mgr.StartAll(); }
    private void StopQueue()
    {
        _sched.StopWatching();
        foreach (var i in _mgr.Items.Where(i => i.Status == DownloadStatus.Queued)) _mgr.Pause(i.Id);
    }

    private void OnListDoubleClick()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (sel[0].Status == DownloadStatus.Completed) OpenSelectedFile(); else OpenProgress(sel[0]);
    }

    private void OpenSelectedFile()
    {
        var i = Selected().FirstOrDefault();
        if (i == null) return;
        if (!File.Exists(i.SavePath)) { MessageBox.Show(this, "Dosya bulunamadı:\n" + i.SavePath, "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        try { Process.Start(new ProcessStartInfo(i.SavePath) { UseShellExecute = true }); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void ShowSelectedInFolder()
    {
        var i = Selected().FirstOrDefault();
        if (i == null) return;
        try
        {
            if (File.Exists(i.SavePath)) Process.Start("explorer.exe", $"/select,\"{i.SavePath}\"");
            else { var dir = Path.GetDirectoryName(i.SavePath); if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\""); }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void ShowOptions()
    {
        using var d = new OptionsDialog(_mgr.Settings, _svc.Paths.Root, _svc.ApplySettings);
        if (d.ShowDialog(Visible ? this : null) == DialogResult.OK) _svc.ApplySettings(d.Result);
        UpdateClipboardListener();
    }

    private void ShowScheduler()
    {
        using var d = new SchedulerDialog(_mgr, _svc.ApplySettings, _sched.WatchQueue);
        d.ShowDialog(Visible ? this : null);
    }

    private void ShowAbout()
    {
        MessageBox.Show(this, $"T-Downloader {AppServices.Version}\n\nÇok bağlantılı, devam ettirilebilir indirme yöneticisi.\nVeri klasörü: {_svc.Paths.Root}",
            "T-Downloader hakkında", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string? FindExtensionDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
        {
            var e = Path.Combine(d.FullName, "extension");
            if (File.Exists(Path.Combine(e, "manifest.json"))) return e;
        }
        return null;
    }

    private void OpenExtensionFolder()
    {
        if (_extensionDir == null)
        {
            MessageBox.Show(this, "Tarayıcı eklentisi klasörü bulunamadı.\nChrome/Edge'de \"Uzantılar > Geliştirici modu > Paketlenmemiş yükle\" ile 'extension' klasörünü seçin.",
                "Tarayıcı eklentisi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        MessageBox.Show(this, "Eklenti klasörü açılıyor. Chrome/Edge'de \"Uzantılar > Geliştirici modu > Paketlenmemiş yükle\" ile bu klasörü seçin.",
            "Tarayıcı eklentisi", MessageBoxButtons.OK, MessageBoxIcon.Information);
        try { Process.Start("explorer.exe", $"\"{_extensionDir}\""); } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    // ------------------------------------------------------------ kategori paneli

    private void ToggleCategories()
    {
        _split.Panel1Collapsed = !_split.Panel1Collapsed;
        _mgr.Settings.ShowCategories = !_split.Panel1Collapsed;
        if (_viewCats != null) _viewCats.Text = _split.Panel1Collapsed ? "Kategorileri &göster" : "Kategorileri &gizle";
    }

    // ------------------------------------------------------------ ayar kalıcılığı

    private void RestoreUiState()
    {
        var st = _mgr.Settings;
        foreach (ColumnHeader h in _list.Columns)
            if (st.ColumnWidths.TryGetValue(h.Name!, out var w) && w > 8) h.Width = Scaled(w);
        if (st.ColumnOrder.Count == Cols.Length)
        {
            for (int i = 0; i < st.ColumnOrder.Count; i++)
            {
                var h = _list.Columns[st.ColumnOrder[i]];
                if (h != null && i < _list.Columns.Count) h.DisplayIndex = i;
            }
        }
        if (st.CategoriesWidth > 40) _split.SplitterDistance = Scaled(st.CategoriesWidth);
        if (!st.ShowCategories) ToggleCategories();
        if (st.WindowWidth > 300 && st.WindowHeight > 200)
        {
            var r = new Rectangle(st.WindowLeft, st.WindowTop, st.WindowWidth, st.WindowHeight);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r)))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = r;
            }
        }
        if (st.WindowMaximized) WindowState = FormWindowState.Maximized;
        UpdateClipboardListener();
    }

    private void SaveUiState()
    {
        var st = _mgr.Settings;
        st.ColumnWidths = _list.Columns.Cast<ColumnHeader>().ToDictionary(h => h.Name!, h => (int)Math.Round(h.Width / _s));
        st.ColumnOrder = _list.Columns.Cast<ColumnHeader>().OrderBy(h => h.DisplayIndex).Select(h => h.Name!).ToList();
        if (!_split.Panel1Collapsed) st.CategoriesWidth = (int)Math.Round(_split.SplitterDistance / _s);
        st.WindowMaximized = WindowState == FormWindowState.Maximized;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        st.WindowLeft = b.Left; st.WindowTop = b.Top; st.WindowWidth = b.Width; st.WindowHeight = b.Height;
        _svc.SaveSettings();
    }

    // ------------------------------------------------------------ kapanış / tepsi

    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _mgr.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            SaveUiState();
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3000, "T-Downloader", "Uygulama sistem tepsisinde çalışmaya devam ediyor.", ToolTipIcon.Info);
            }
            return;
        }
        SaveUiState();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _sched.Dispose();
        foreach (var d in _dialogs.Values.ToList()) d.Close();
        if (IsHandleCreated) RemoveClipboardFormatListener(Handle);
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------ pano takibi

    private bool _clipOn;
    private void UpdateClipboardListener()
    {
        bool want = _mgr.Settings.ClipboardMonitor;
        if (want && !_clipOn) { _clipOn = AddClipboardFormatListener(Handle); }
        else if (!want && _clipOn) { RemoveClipboardFormatListener(Handle); _clipOn = false; }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE && _clipOn) UiPost(CheckClipboard);
        base.WndProc(ref m);
    }

    private bool Downloadable(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var name = DownloadManager.NameFromUri(u);
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return false;
        return _mgr.Settings.FileTypes.Contains(ext, StringComparer.OrdinalIgnoreCase) || Categories.FromFileName(name) != Categories.General;
    }

    private void CheckClipboard()
    {
        if (_clipBusy || !_mgr.Settings.ClipboardMonitor) return;
        string text;
        try { if (!Clipboard.ContainsText()) return; text = Clipboard.GetText().Trim(); }
        catch (ExternalException) { return; }
        if (!IsHttp(text) || text == _lastClip || !Downloadable(text)) return;
        _lastClip = text;
        _clipBusy = true;
        try
        {
            var r = MessageBox.Show(Visible ? this : null, $"Panoda indirilebilir bir bağlantı bulundu:\n\n{text}\n\nİndirmek ister misiniz?",
                "T-Downloader", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (r == DialogResult.Yes) ShowFileInfo(text, null, null, null, null, -1);
        }
        finally { _clipBusy = false; }
    }
}
