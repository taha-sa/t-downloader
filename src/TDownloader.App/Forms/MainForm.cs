using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TDownloader.App.Services;
using TDownloader.App.Ui;
using TDownloader.Core.Downloads;
using TDownloader.Core.Ipc;
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
    private readonly DarkTree _tree = new();
    private readonly BufferedListView _list = new();
    private readonly ContextMenuStrip _ctx = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 400 };
    private ToolStripMenuItem? _viewCats;

    // Büyük liste: satır önbelleği (tüm öğeler, bir kez üretilir) + o an listede görünenler; kategori değişince satırlar
    // yeniden üretilmez, önbellekten süzülür. _items: App tarafı O(1) arama (DownloadManager.Get O(n)); sayaçlar artımlı.
    private readonly Dictionary<string, ListViewItem> _rows = new();
    private readonly HashSet<string> _shown = new();
    private readonly Dictionary<string, DownloadItem> _items = new();
    private readonly DownloadStats _stats = new();
    private readonly List<string> _removeQueue = new();
    private bool _removePosted;
    private bool _bulk;
    private readonly ConcurrentDictionary<string, DownloadProgress> _progress = new();
    private readonly HashSet<string> _dirty = new();
    private readonly object _dirtyLock = new();
    private readonly Dictionary<string, ProgressDialog> _dialogs = new();
    private string _filter = "all";
    private int _sortCol = -1;
    private bool _sortAsc = true;
    private bool _exiting;
    private bool _startHidden;
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
        BackColor = Theme.Bg; ForeColor = Theme.Text;
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
        _sched.Notify += msg => UiPost(() => _stMain.Text = msg);   // balon yok: durum çubuğunda göster
        _sched.ExitRequested += ExitApp;

        _extensionDir = FindExtensionDir();
        RestoreUiState();
        foreach (var i in _mgr.Items) { _items[i.Id] = i; _stats.Set(i.Id, i.Status == DownloadStatus.Downloading, 0); }
        RebuildList();

        _mgr.ItemAdded += (_, i) => UiPost(() => AddRow(i));
        _mgr.ItemRemoved += (_, i) => QueueRemove(i.Id);
        _mgr.ItemChanged += (_, i) => MarkDirty(i.Id);
        _mgr.ProgressChanged += (_, p) => { _progress[p.Id] = p; MarkDirty(p.Id); };
        _mgr.DownloadCompleted += (_, i) => UiPost(() => OnCompleted(i));
        _mgr.DownloadFailed += (_, i) => UiPost(() => MarkDirty(i.Id));   // hata ana listede durum olarak görünür; balon yok
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
        _ = Handle; // gizli başlangıçta bile tutamaç (pano dinleyici, BeginInvoke) hazır olsun
    }

    private int Scaled(int px) => (int)Math.Round(px * _s);

    // ------------------------------------------------------------ gizli başlangıç / öne getirme

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dark.Window(Handle);
        Dark.ApplyTree(this);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden)
        {
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

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
        _menu.BackColor = Theme.Surface; _menu.ForeColor = Theme.Text;
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
        _tool.BackColor = Theme.Surface; _tool.ForeColor = Theme.Text;

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
        _split.BackColor = Theme.Border;
        _split.Panel1.BackColor = Theme.Bg; _split.Panel2.BackColor = Theme.Bg;

        // kategori paneli
        _catPanel.Dock = DockStyle.Fill;
        var caption = new Panel { Dock = DockStyle.Top, Height = Scaled(20), BackColor = Theme.Black };
        var cl = new Label { Text = "Kategoriler", ForeColor = Theme.Yellow, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Scaled(6), 0, 0, 0) };
        var close = new Button { Text = "×", Dock = DockStyle.Right, Width = Scaled(20), FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, TabStop = false };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Theme.Hover;
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
        _list.BorderStyle = BorderStyle.None;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.AllowColumnReorder = true;
        _list.HideSelection = false;
        _list.GridLines = false;
        _list.SmallImageList = _shell.Images;
        foreach (var c in Cols) _list.Columns.Add(new ColumnHeader { Name = c.Key, Text = c.Title, Width = Scaled(c.Width), TextAlign = c.Align });
        _list.ColumnClick += (_, e) => SortBy(e.Column, _sortCol != e.Column || !_sortAsc);
        _list.DoubleClick += (_, _) => OnListDoubleClick();
        _list.SelectedIndexChanged += (_, _) => { if (!_bulk) UpdateStatus(); };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.A || !e.Control) return;
            // Toplu seçim: öğe başına SelectedIndexChanged/güncelleme yok; tek BeginUpdate + tek durum güncellemesi.
            _bulk = true; _list.BeginUpdate();
            try { foreach (ListViewItem i in _list.Items) i.Selected = true; }
            finally { _list.EndUpdate(); _bulk = false; }
            UpdateStatus();
            e.Handled = true;
        };

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
        _status.BackColor = Theme.Surface; _status.ForeColor = Theme.Text;
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
        var n = new NotifyIcon { Icon = Icons.TrayIcon(), Text = "T-Downloader", ContextMenuStrip = m, Visible = true };
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

    private ListViewItem RowFor(DownloadItem i)
    {
        if (_rows.TryGetValue(i.Id, out var li)) return li;
        li = new ListViewItem(new string[Cols.Length]) { Tag = i, Name = i.Id };
        _rows[i.Id] = li;
        FillRow(li, i);
        return li;
    }

    private void RebuildList()
    {
        _list.BeginUpdate();
        _list.Items.Clear(); _shown.Clear();
        var rows = new List<ListViewItem>();
        foreach (var i in _items.Values.OrderBy(x => x.AddedAt))
            if (Match(i)) { rows.Add(RowFor(i)); _shown.Add(i.Id); }
        _list.Items.AddRange(rows.ToArray());
        ApplySort();
        _list.EndUpdate();
        UpdateStatus();
    }

    private void AddRow(DownloadItem i)
    {
        _items[i.Id] = i;
        _stats.Set(i.Id, i.Status == DownloadStatus.Downloading, 0);
        if (Match(i) && _shown.Add(i.Id)) _list.Items.Add(RowFor(i));
        UpdateStatus();
    }

    // Silmeler toplanır: tek UI geçişinde işlenir; çoğunlukla büyük toplu silmede liste tek seferde yeniden kurulur (O(n)).
    private void QueueRemove(string id)
    {
        bool post;
        lock (_removeQueue) { _removeQueue.Add(id); post = !_removePosted; _removePosted = true; }
        if (post) UiPost(DrainRemovals);
    }

    private void DrainRemovals()
    {
        string[] ids;
        lock (_removeQueue) { ids = _removeQueue.ToArray(); _removeQueue.Clear(); _removePosted = false; }
        bool bulk = ids.Length >= 20;
        foreach (var id in ids)
        {
            _items.Remove(id); _stats.Remove(id);
            if (_rows.Remove(id, out var li) && _shown.Remove(id) && !bulk) li.Remove();
            _progress.TryRemove(id, out _);
            if (_dialogs.TryGetValue(id, out var d)) d.Close();
        }
        if (bulk) RebuildList(); else UpdateStatus();
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
        int img = _shell.IndexFor(it.FileName);
        if (li.ImageIndex != img) li.ImageIndex = img;
        void Set(int c, string v) { var s = li.SubItems[c]; if (s.Text != v) s.Text = v; }
        Set(0, it.FileName);
        Set(1, it.Status is DownloadStatus.Queued or DownloadStatus.Downloading ? "•" : "");
        Set(2, it.TotalSize > 0 ? Fmt.Size(it.TotalSize) : "");
        Set(3, StatusText(it, true));
        Set(4, active && p != null ? Fmt.Eta(p.Eta) : "");
        Set(5, active ? Fmt.Speed(p?.SpeedBytesPerSec ?? it.SpeedBytesPerSec) : "");
        Set(6, Fmt.Date(it.LastTryAt));
        Set(7, Fmt.Date(it.AddedAt));
        Set(8, it.Description ?? "");
        var fc = it.Status == DownloadStatus.Failed ? Theme.Danger : Theme.Text;
        if (li.ForeColor != fc) li.ForeColor = fc;
    }

    private void Flush()
    {
        string[] ids;
        lock (_dirtyLock) { ids = _dirty.ToArray(); _dirty.Clear(); }
        if (ids.Length > 0)
        {
            // BeginUpdate/EndUpdate yok: EndUpdate tüm listeyi geçersiz kılar (titreme); yalnızca değişen hücreler yeniden çizilir.
            foreach (var id in ids)
            {
                if (!_items.TryGetValue(id, out var it)) continue;   // eklenme/silinme UI kuyruğunda; sonra işlenir
                bool match = Match(it);
                if (_rows.TryGetValue(id, out var li))
                {
                    FillRow(li, it);
                    bool shown = _shown.Contains(id);
                    if (match && !shown) { _list.Items.Add(li); _shown.Add(id); }
                    else if (!match && shown) { li.Remove(); _shown.Remove(id); }
                }
                else if (match) { _shown.Add(id); _list.Items.Add(RowFor(it)); }
                bool active = it.Status == DownloadStatus.Downloading;
                _progress.TryGetValue(id, out var p);
                _stats.Set(id, active, p?.SpeedBytesPerSec ?? 0);
                if (_dialogs.TryGetValue(id, out var dlg))
                {
                    dlg.RefreshItem(it);
                    if (p != null && active) dlg.ApplyProgress(p);
                }
                if (!active) _progress.TryRemove(id, out _);   // biten/duran indirmenin ilerleme kaydı tutulmaz
            }
        }
        UpdateStatus();
    }

    // Sayaçlar artımlı tutulur (_stats): her çağrıda Items kopyası / LINQ yok -> O(1).
    private void UpdateStatus()
    {
        static void T(ToolStripStatusLabel l, string v) { if (l.Text != v) l.Text = v; }
        T(_stActive, $"Aktif: {_stats.Active}");
        T(_stSpeed, "Hız: " + (_stats.Speed > 0 ? Fmt.Speed(_stats.Speed) : "0 B/sn"));
        T(_stTotal, $"Toplam: {_stats.Total}");
        int sel = _list.SelectedIndices.Count;
        T(_stMain, sel > 0 ? $"{sel} öğe seçili" : "Hazır");
    }

    private void OnCompleted(DownloadItem i)
    {
        MarkDirty(i.Id);
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
        if (StaCall.TryRun(ReadClipboardText, ClipboardTimeoutMs, out var clip) && clip != null && IsHttp(clip)) initial = clip;
        using var d = new AddUrlDialog(initial);
        if (d.ShowDialog(Visible ? this : null) == DialogResult.OK) _ = PromptDownloadAsync(d.Url, null, null, null, null, -1, force: true);
    }

    private void AddFromClipboard()
    {
        if (StaCall.TryRun(ReadClipboardText, ClipboardTimeoutMs, out var clip) && clip != null && IsHttp(clip))
            _ = PromptDownloadAsync(clip, null, null, null, null, -1, force: true);
        else MessageBox.Show(this, "Panoda geçerli bir http/https bağlantısı yok.", "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static bool IsHttp(string t) => Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && !t.Contains('\n');

    // Birden çok istek aynı anda gelirse pencereler üst üste binmez: sırayla (kuyruk) gösterilir.
    private readonly SemaphoreSlim _promptGate = new(1, 1);

    /// <summary>
    /// Tarayıcı/video/pano/URL Ekle yolu: "Dosya İndirme Bilgisi" penceresi açılır; kullanıcı onaylamadan indirme başlamaz.
    /// true = eklendi (başlat veya daha sonra), false = iptal. UI thread dışından da çağrılabilir.
    /// </summary>
    // Aynı bağlantı için bekleyen/az önce sorulmuş pencere varsa ikincisi açılmaz (tarayıcı tekrar gönderebilir).
    private readonly Dictionary<string, DateTime> _recentPrompts = new(StringComparer.Ordinal);
    private static readonly TimeSpan PromptDedupWindow = TimeSpan.FromSeconds(15);

    private bool TryClaimPrompt(string url)
    {
        lock (_recentPrompts)
        {
            var now = DateTime.UtcNow;
            foreach (var k in _recentPrompts.Where(p => now - p.Value > PromptDedupWindow).Select(p => p.Key).ToList())
                _recentPrompts.Remove(k);
            if (_recentPrompts.ContainsKey(url)) return false;
            _recentPrompts[url] = now;
            BoundedDictionary.TrimOldest(_recentPrompts, 500, v => v);   // sınırsız büyümesin
            return true;
        }
    }

    public async Task<bool> PromptDownloadAsync(string url, string? referrer, string? cookies, string? userAgent, string? fileName, long fileSize, bool force = false)
    {
        if (!IsHttp(url)) return false;
        if (!force && !TryClaimPrompt(url)) return false;
        return await RunPromptAsync(() =>
        {
            if (!force && !_mgr.Settings.ShowFileInfoOnCapture)
            {
                var item = _mgr.Add(new AddDownloadRequest(url, fileName, null, referrer, cookies, userAgent, null, fileSize, true));
                OpenProgress(item);
                return true;
            }
            return ShowFileInfo(url, referrer, cookies, userAgent, fileName, fileSize);
        });
    }

    /// <summary>yt-dlp (External) indirmesi: kalite seçildikten sonra aynı pencereyi gösterir.</summary>
    public Task<bool> PromptExternalAsync(string source, string? referer, string title, TDownloader.Core.Video.QualityOption opt, TDownloader.Core.Video.MediaContext? ctx = null)
    {
        string stem = DownloadManager.SanitizeFileName(title.Length <= 120 ? title : title[..119] + "…");
        string name = stem + "." + opt.Ext;
        return RunPromptAsync(() =>
        {
            using var dlg = new FileInfoDialog(_mgr, source, name, opt.Size, probeSize: false, referrer: referer, cookies: ctx?.Cookies, userAgent: ctx?.UserAgent, description: "Video: " + opt.Label);
            var r = dlg.ShowDialog();
            if (r is not (DialogResult.Yes or DialogResult.No)) return false;
            try
            {
                _mgr.AddExternal(new AddExternalRequest(dlg.Url, dlg.FileName, opt.Format, dlg.SaveFolder, referer, dlg.Description, dlg.StartNow, ctx?.Cookies, ctx?.UserAgent, ctx?.EffectiveOrigin));
                return true;
            }
            catch (ArgumentException ex)
            {
                MessageBox.Show(ex.Message, "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        });
    }

    private async Task<bool> RunPromptAsync(Func<bool> show)
    {
        await _promptGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            UiPost(() =>
            {
                try { tcs.SetResult(show()); }
                catch (Exception) { tcs.TrySetResult(false); }
            });
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { _promptGate.Release(); }
    }

    private bool ShowFileInfo(string url, string? referrer, string? cookies, string? userAgent, string? fileName, long fileSize)
    {
        // Sahipsiz (owner yok): ana pencere öne gelmez; pencere TopMost + ön plana zorlanır.
        using var dlg = new FileInfoDialog(_mgr, url, fileName, fileSize, true, referrer, cookies, userAgent);
        var r = dlg.ShowDialog();
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
            MessageBox.Show(ex.Message, "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    // Pano başka bir uygulamada meşgulken Clipboard.* saniyelerce bloklayabilir: okuma ayrı STA iş parçacığında, kısa süre sınırıyla.
    private const int ClipboardTimeoutMs = 400;

    private static string? ReadClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText().Trim() : null; }
        catch (ExternalException) { return null; }
    }

    private void CheckClipboard()
    {
        if (_clipBusy || !_mgr.Settings.ClipboardMonitor) return;
        _clipBusy = true;
        _ = Task.Run(() =>
        {
            StaCall.TryRun(ReadClipboardText, ClipboardTimeoutMs, out var text);
            UiPost(() =>
            {
                try
                {
                    if (text == null || !IsHttp(text) || text == _lastClip || !Downloadable(text)) return;
                    _lastClip = text;
                    _ = PromptDownloadAsync(text, null, null, null, null, -1, force: true);   // pencerenin kendisi onay penceresidir
                }
                finally { _clipBusy = false; }
            });
        });
    }
}
