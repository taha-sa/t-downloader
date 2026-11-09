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
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 160 };   // ~6 Hz: ilerleme birleştirilir, tek zamanlayıcı, yalnız görünen değişen satırlar yeniden çizilir
    private readonly Dictionary<string, UpdateGate> _textGates = new();   // satır başına hız/kalan süre metin kısıtlaması (1 sn)
    private ToolStripButton? _btnDiskDelete;
    private ToolStripMenuItem? _viewCats;

    // Büyük liste: satır önbelleği (tüm öğeler, bir kez üretilir) + o an listede görünenler; kategori değişince satırlar
    // yeniden üretilmez, önbellekten süzülür. _items: App tarafı O(1) arama (DownloadManager.Get O(n)); sayaçlar artımlı.
    private sealed class Row : ListViewItem
    {
        public bool Stale = true;                // metinler bir sonraki RetrieveVirtualItem'da yenilenir (yalnız görünen satırlar)
        public readonly DownloadItem Item;
        public Row(DownloadItem it, int cols) : base(new string[cols]) { Item = it; Tag = it; Name = it.Id; }
    }

    private readonly Dictionary<string, Row> _rows = new();
    private readonly ViewIndex<Row> _view = new(r => r.Name);       // süzülmüş + sıralanmış görünüm (VirtualMode)
    private readonly ListViewItem _blankRow = new(new string[Cols.Length]);
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
        MinimumSize = new Size(Scaled(56 * 13 + 40), Scaled(320));   // 13 araç çubuğu düğmesi sığsın
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
        Disposed += (_, _) => _uiGone.TrySetResult();
        _mgr.StartGate = DiskStartGate;
        _sched = new SchedulerService(svc);
        _sched.Notify += msg => UiPost(() =>
        {
            _stMain.Text = msg;
            try
            {
                if (_tray.Visible)
                    _tray.ShowBalloonTip(3000, "T-Downloader", msg, ToolTipIcon.Info);
            }
            catch { }
        });
        _sched.ExitRequested += () => UiPost(ExitApp);
        _sched.ExitCountdownChanged += n => UiPost(() => { if (n >= 0) _stMain.Text = $"Çıkış: {n} sn (iptal için kuyruğu durdurun)"; });

        _extensionDir = FindExtensionDir();
        RestoreUiState();
        foreach (var i in _mgr.Items) { _items[i.Id] = i; _stats.Set(i.Id, i.Status == DownloadStatus.Downloading, 0); }
        _shell.IconsChanged += () => { foreach (var r in _rows.Values) r.Stale = true; _list.Invalidate(); };
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

    /// <summary>UI iş parçacığına gönderir; gönderilemediyse (handle yok / form atılmış) false döner.</summary>
    private bool TryUiPost(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        try { BeginInvoke(a); return true; } catch (InvalidOperationException) { return false; }
    }

    private readonly TaskCompletionSource _uiGone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _balloonRetry;

    /// <summary>Tarayıcıdan gelen ve spool'dan işlenen isteklerde ekleme başarısız olursa "Tekrar dene" bağlantısı (balon tıklaması / tepsi menüsü).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? RetryFailedCaptures { get; set; }

    /// <summary>Başarısız tarayıcı indirmesi bildirimi (tepsi balonu + durum çubuğu). Balona tıklamak yeniden dener.</summary>
    public void NotifyCaptureFailed(string what)
    {
        string msg = "Tarayıcıdan gelen indirme eklenemedi: " + what;
        TryUiPost(() =>
        {
            _stMain.Text = msg + " (Tekrar dene: tepsi simgesi menüsü)";
            _balloonRetry = RetryFailedCaptures;
            try { if (_tray.Visible) _tray.ShowBalloonTip(8000, "T-Downloader - Tekrar dene", msg + ". Tekrar denemek için tıklayın.", ToolTipIcon.Warning); } catch { }
        });
    }

    /// <summary>Gerçek yinelenen istek (aynı bağlantı + aynı sayfa) yutulduğunda sessiz kalmamak için bilgi.</summary>
    public void NotifyDuplicate(string url)
    {
        TryUiPost(() =>
        {
            _stMain.Text = "Aynı bağlantı zaten ekleniyor/eklendi: " + url;
            try { if (_tray.Visible) _tray.ShowBalloonTip(3000, "T-Downloader", "Aynı bağlantı zaten ekleniyor/eklendi.", ToolTipIcon.Info); } catch { }
        });
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
            Mi("Çı&kış", ExitAppByUser),
        });

        var file = new ToolStripMenuItem("&Dosya");
        file.DropDownItems.AddRange(new ToolStripItem[]
        {
            Mi("&Durdur", StopSelected, Keys.None, "stop"),
            Mi("&Şimdi indir / Devam et", ResumeSelected, Keys.None, "resume"),
            Mi("&Sil", DeleteSelected, Keys.Delete, "delete"),
            Mi("&Diskten Sil", DeleteSelectedFromDisk, Keys.Shift | Keys.Delete, "deletedisk"),
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
        B("Diskten Sil", "Seçilenleri diskten sil (Geri Dönüşüm Kutusu)", "deletedisk", DeleteSelectedFromDisk);
        _btnDiskDelete = (ToolStripButton)_tool.Items[^1];
        _btnDiskDelete.Enabled = false;
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
        foreach (var c in new[] { Categories.Compressed, Categories.Documents, Categories.Music, Categories.Programs, Categories.Video, Categories.Pictures })
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
        _list.VirtualMode = true;
        _list.RetrieveVirtualItem += (_, e) =>
        {
            if (e.ItemIndex >= 0 && e.ItemIndex < _view.Count)
            {
                var r = _view[e.ItemIndex];
                if (r.Stale) { FillRow(r, r.Item); r.Stale = false; }
                e.Item = r;
            }
            else e.Item = _blankRow;
        };
        _list.FullRowSelect = true;
        _list.AllowColumnReorder = true;
        _list.HideSelection = false;
        _list.GridLines = false;
        _list.SmallImageList = _shell.Images;
        foreach (var c in Cols) _list.Columns.Add(new ColumnHeader { Name = c.Key, Text = c.Title, Width = Scaled(c.Width), TextAlign = c.Align });
        _list.ColumnClick += (_, e) => SortBy(e.Column, _sortCol != e.Column || !_sortAsc);
        _list.DoubleClick += (_, _) => OnListDoubleClick();
        _list.ItemDrag += (_, e) =>
        {
            var sel = Selected();
            if (e.Item is ListViewItem lvi && lvi.Tag is DownloadItem di && !sel.Any(s => s.Id == di.Id))
            {
                sel = new List<DownloadItem> { di };
            }

            var paths = sel
                .Where(i => i.Status == DownloadStatus.Completed && !string.IsNullOrEmpty(i.SavePath) && File.Exists(i.SavePath))
                .Select(i => i.SavePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0) return;

            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { (byte)DragDropEffects.Copy, 0, 0, 0 }));
            _list.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        };
        _list.SelectedIndexChanged += (_, _) => { if (!_bulk) UpdateStatus(); };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.A && e.Control)
            {
                // Toplu seçim: öğe başına SelectedIndexChanged/güncelleme yok; tek BeginUpdate + tek durum güncellemesi.
                _bulk = true; _list.BeginUpdate();
                try { for (int i = 0; i < _view.Count; i++) _list.SelectedIndices.Add(i); }
                finally { _list.EndUpdate(); _bulk = false; }
                UpdateStatus();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                if (e.Shift) DeleteSelectedFromDisk();
                else DeleteSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                OnListDoubleClick();
                e.Handled = true;
            }
        };

        var ctxOpen = Mi("Aç", OpenSelectedFile);
        var ctxFolder = Mi("Klasörde göster", ShowSelectedInFolder);
        var ctxHash = Mi("Sağlama Toplamı (Hash)...", ShowSelectedHash, Keys.None, "hash");
        var ctxSep1 = new ToolStripSeparator();
        var ctxResume = Mi("Devam et", ResumeSelected, Keys.None, "resume");
        var ctxStop = Mi("Durdur", StopSelected, Keys.None, "stop");
        var ctxProgress = Mi("İlerleme penceresi", ShowSelectedProgress);
        var ctxSep2 = new ToolStripSeparator();
        var ctxDelete = Mi("Sil", DeleteSelected, Keys.None, "delete");
        var ctxDisk = Mi("Diskten Sil", DeleteSelectedFromDisk, Keys.None, "deletedisk");
        ctxDisk.ShortcutKeyDisplayString = "Shift+Del";

        _ctx.Items.AddRange(new ToolStripItem[]
        {
            ctxOpen, ctxFolder, ctxHash, ctxSep1,
            ctxResume, ctxStop, ctxProgress, ctxSep2,
            ctxDelete, ctxDisk
        });
        _ctx.Opening += (_, e) =>
        {
            var sel = Selected();
            if (sel.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            bool allCompleted = sel.All(i => i.Status == DownloadStatus.Completed);
            bool canResume = sel.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed);
            bool canStop = sel.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
            bool hasIncomplete = sel.Any(i => i.Status != DownloadStatus.Completed);

            ctxHash.Visible = allCompleted && sel.Count == 1;
            ctxResume.Visible = canResume;
            ctxStop.Visible = canStop;
            ctxProgress.Visible = hasIncomplete;

            bool middleGroupVisible = ctxResume.Visible || ctxStop.Visible || ctxProgress.Visible;
            ctxSep1.Visible = true;
            ctxSep2.Visible = middleGroupVisible;
        };
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
        m.Items.Add(Mi("Tarayıcıdan gelip eklenemeyenleri tekrar &dene", () => RetryFailedCaptures?.Invoke()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Mi("Çı&kış", ExitAppByUser));
        var n = new NotifyIcon { Icon = Icons.TrayIcon(), Text = "T-Downloader", ContextMenuStrip = m, Visible = true };
        n.DoubleClick += (_, _) => ShowMain();
        n.BalloonTipClicked += (_, _) =>
        {
            var retry = _balloonRetry; _balloonRetry = null;
            if (retry != null) { try { retry(); } catch (Exception) { } }
            ShowMain();
        };
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

    private Row RowFor(DownloadItem i)
    {
        if (_rows.TryGetValue(i.Id, out var li)) return li;
        li = new Row(i, Cols.Length);
        _rows[i.Id] = li;
        return li;
    }

    private HashSet<string> SelectedIds()
    {
        var set = new HashSet<string>();
        foreach (int ix in _list.SelectedIndices) if (ix >= 0 && ix < _view.Count) set.Add(_view[ix].Name);
        return set;
    }

    private IComparer<Row> CurrentComparer() => new Cmp { Asc = _sortCol < 0 || _sortAsc, Key = _sortCol < 0 ? "added" : Cols[_sortCol].Key };

    // Görünümü (süz + sırala) yeniden kurar; seçim kimliklere göre korunur. Toplu değişiklik: tek BeginUpdate/EndUpdate.
    private void RebuildList()
    {
        var keep = SelectedIds();
        _bulk = true;
        _list.BeginUpdate();
        try
        {
            foreach (var i in _items.Values) RowFor(i);
            _view.Rebuild(_rows.Values, r => Match(r.Item), CurrentComparer());
            _list.VirtualListSize = _view.Count;
            _list.SelectedIndices.Clear();
            foreach (var id in keep) { int ix = _view.IndexOf(id); if (ix >= 0) _list.SelectedIndices.Add(ix); }
        }
        finally { _list.EndUpdate(); _bulk = false; }
        _list.Invalidate();
        UpdateStatus();
    }

    private void AddRow(DownloadItem i)
    {
        _items[i.Id] = i;
        _stats.Set(i.Id, i.Status == DownloadStatus.Downloading, 0);
        var row = RowFor(i);
        if (Match(i) && !_view.Contains(i.Id))
        {
            if (_sortCol < 0) { _view.Append(row); _list.VirtualListSize = _view.Count; }   // sıralama yok: yalnız sona ekle
            else RebuildList();
        }
        UpdateStatus();
    }

    // Silmeler toplanır: tek UI geçişinde işlenir; görünüm bir kez yeniden kurulur.
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
        bool inView = false;
        foreach (var id in ids)
        {
            _items.Remove(id); _stats.Remove(id); _textGates.Remove(id);
            if (_rows.Remove(id)) inView |= _view.Contains(id);
            _progress.TryRemove(id, out _);
            if (_dialogs.TryGetValue(id, out var d)) d.Close();
        }
        if (inView) RebuildList(); else UpdateStatus();
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
        // Birleştirme/dönüştürme aşaması ("Birleştiriliyor %40") varsa yüzde yerine o gösterilir (A14).
        Set(3, active && p?.Phase is { Length: > 0 } phase ? phase : StatusText(it, true));
        // Hız / kalan süre metni satır başına en fazla saniyede 1 güncellenir; yüzde/boyut her turda.
        if (!active) { _textGates.Remove(it.Id); Set(4, ""); Set(5, ""); }
        else
        {
            if (!_textGates.TryGetValue(it.Id, out var gate)) _textGates[it.Id] = gate = new UpdateGate();
            if (gate.Due(Environment.TickCount64))
            {
                Set(4, p != null ? Fmt.Eta(p.Eta) : "");
                Set(5, Fmt.Speed(p?.SpeedBytesPerSec ?? it.SpeedBytesPerSec));
            }
        }
        Set(6, Fmt.Date(it.LastTryAt));
        Set(7, Fmt.Date(it.AddedAt));
        Set(8, it.Description ?? "");
        var fc = it.Status == DownloadStatus.Failed ? Theme.Danger : Theme.Text;
        if (li.ForeColor != fc) li.ForeColor = fc;
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    private const int LVM_GETTOPINDEX = 0x1027, LVM_GETCOUNTPERPAGE = 0x1028;

    // Motor olayları tek zamanlayıcıda (~6 Hz) birleşir: kirli kimlikler satırı "bayat" işaretler; metin yalnız görünen satırlar
    // için (RetrieveVirtualItem) üretilir ve yalnız görünen + değişen satırlar RedrawItems ile yeniden çizilir.
    private void Flush()
    {
        string[] ids;
        lock (_dirtyLock) { ids = _dirty.ToArray(); _dirty.Clear(); }
        if (ids.Length > 0)
        {
            int top = (int)SendMessage(_list.Handle, LVM_GETTOPINDEX, IntPtr.Zero, IntPtr.Zero);
            int last = top + (int)SendMessage(_list.Handle, LVM_GETCOUNTPERPAGE, IntPtr.Zero, IntPtr.Zero) + 1;
            bool rebuild = false;
            foreach (var id in ids)
            {
                if (!_items.TryGetValue(id, out var it)) continue;   // eklenme/silinme UI kuyruğunda; sonra işlenir
                bool match = Match(it);
                var row = RowFor(it);
                row.Stale = true;
                int ix = _view.IndexOf(id);
                if (match != (ix >= 0)) rebuild = true;                    // süzgeçten çıktı / girdi
                else if (ix >= top && ix <= last) _list.RedrawItems(ix, ix, false);

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
            if (rebuild) RebuildList();
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
        if (_btnDiskDelete != null && _btnDiskDelete.Enabled != (sel > 0)) _btnDiskDelete.Enabled = sel > 0;
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
        try
        {
            if (_mgr.Settings.ShowCompletedDialog) ShowCompleted(i);              // bildirim balonunun yerine tamamlanma penceresi
            else if (_tray.Visible)   // tek tek tamamlanma balonu, zamanlayıcı "bitince" ayarından bağımsız
                _tray.ShowBalloonTip(3000, "İndirme Tamamlandı", $"{i.FileName} başarıyla indirildi.", ToolTipIcon.Info);
        }
        catch { }
    }

    private CompletedDialog? _completed;

    private void ShowCompleted(DownloadItem i)
    {
        if (_completed == null || _completed.IsDisposed)
        {
            var dlg = new CompletedDialog(never => { _mgr.Settings.ShowCompletedDialog = !never; _svc.SaveSettings(); });
            var act = new Services.PhoneSendAction();   // "Telefona gönder": T-Bridge + bağlı telefon varsa görünür
            act.Changed += () => { try { dlg.BeginInvoke(dlg.Invalidate); } catch (InvalidOperationException) { } };
            dlg.ExtraActions.Add(act);
            var actOpen = new Services.PhoneSendAction(open: true);   // "Telefonda aç": aynı uygunluk kuralı, file.send open:true
            actOpen.Changed += () => { try { dlg.BeginInvoke(dlg.Invalidate); } catch (InvalidOperationException) { } };
            dlg.ExtraActions.Add(actOpen);
            _completed = dlg;
        }
        _completed.Present(new CompletedEntry(i.Id, i.FileName, i.Url, i.SavePath, i.TotalSize > 0 ? i.TotalSize : i.Downloaded));
    }

    // ------------------------------------------------------------ sıralama

    private sealed class Cmp : IComparer<Row>
    {
        public bool Asc; public string Key = "";
        public int Compare(Row? a, Row? b)
        {
            var x = a!.Item; var y = b!.Item;
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
            if (r == 0) r = x.AddedAt.CompareTo(y.AddedAt);              // kararlı sıra
            return Asc ? r : -r;
        }
    }

    private void SortBy(int col, bool asc)
    {
        if (col < 0) return;
        _sortCol = col; _sortAsc = asc;
        ApplySort();
    }

    private void ApplySort() => RebuildList();

    // ------------------------------------------------------------ eylemler

    private List<DownloadItem> Selected()
    {
        var res = new List<DownloadItem>(_list.SelectedIndices.Count);
        foreach (int ix in _list.SelectedIndices) if (ix >= 0 && ix < _view.Count) res.Add(_view[ix].Item);
        return res;
    }

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
    // Aynı bağlantı için BEKLEYEN/AÇIK pencere varsa ikincisi açılmaz (tarayıcı tekrar gönderebilir). Tekilleştirme durumla yapılır
    // (kuyruk ne kadar uzun olursa olsun); pencere İptal ile kapanınca talep hemen bırakılır: aynı bağlantıya tekrar tıklanınca yeniden sorulur (A4).
    private readonly PromptClaims _claims = new();

    /// <summary>Onay bekleyen/açık indirme penceresi sayısı (çıkışta uyarı için).</summary>
    public int PendingPromptCount => _claims.ActiveCount;

    public async Task<bool> PromptDownloadAsync(string url, string? referrer, string? cookies, string? userAgent, string? fileName, long fileSize, bool force = false)
    {
        try { return await PromptCaptureAsync(url, referrer, cookies, userAgent, fileName, fileSize, force).ConfigureAwait(false); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// PromptDownloadAsync ile aynı akış, ama pencere/ekleme HATASINI istisna olarak fırlatır (tarayıcı spool'u dosyayı saklayıp bildirsin).
    /// false = kullanıcı iptal etti ya da yinelenen istek atlandı (hata değil).
    /// </summary>
    public async Task<bool> PromptCaptureAsync(string url, string? referrer, string? cookies, string? userAgent, string? fileName, long fileSize, bool force = false)
    {
        if (!IsHttp(url)) return false;
        bool claimed = false;
        if (!force)
        {
            if (!_claims.TryClaim(url, referrer)) { NotifyDuplicate(url); return false; }
            claimed = true;
        }
        bool added = false;
        try
        {
            added = await RunPromptAsync(() =>
            {
                if (!force && !_mgr.Settings.ShowFileInfoOnCapture)
                {
                    if (!ConfirmDiskSpace(null, fileSize, fileName ?? url)) return false;
                    var item = _mgr.Add(new AddDownloadRequest(url, fileName, null, referrer, cookies, userAgent, null, fileSize, true));
                    OpenProgress(item);
                    return true;
                }
                return ShowFileInfo(url, referrer, cookies, userAgent, fileName, fileSize);
            });
            return added;
        }
        finally { if (claimed) _claims.Complete(url, added, referrer); }
    }

    /// <summary>yt-dlp (External) indirmesi: kalite seçildikten sonra aynı pencereyi gösterir.</summary>
    public async Task<bool> PromptExternalAsync(string source, string? referer, string title, TDownloader.Core.Video.QualityOption opt, TDownloader.Core.Video.MediaContext? ctx = null)
    {
        try { return await PromptExternalCoreAsync(source, referer, title, opt, ctx).ConfigureAwait(false); }
        catch (Exception) { return false; }
    }

    private Task<bool> PromptExternalCoreAsync(string source, string? referer, string title, TDownloader.Core.Video.QualityOption opt, TDownloader.Core.Video.MediaContext? ctx)
    {
        string stem = DownloadManager.SanitizeFileName(title.Length <= 120 ? title : title[..119] + "…");
        string name = stem + "." + opt.Ext;
        return RunPromptAsync(() =>
        {
            using var dlg = new FileInfoDialog(_mgr, source, name, opt.Size, probeSize: false, referrer: referer, cookies: ctx?.Cookies, userAgent: ctx?.UserAgent, description: "Video: " + opt.Label);
            var r = dlg.ShowDialog();
            if (r is not (DialogResult.Yes or DialogResult.No)) return false;
            if (!ConfirmDiskSpace(dlg.SaveFolder, opt.Size, dlg.FileName)) return false;
            try
            {
                _mgr.AddExternal(new AddExternalRequest(dlg.Url, dlg.FileName, opt.Format, dlg.SaveFolder, referer, dlg.Description, dlg.StartNow, ctx?.Cookies, ctx?.UserAgent, ctx?.EffectiveOrigin, ctx?.CookieHost));
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
            // Arka plan açılışında IPC/inbox pencere handle'ından önce çalışabilir: kısa süre handle'ı bekle.
            for (int w = 0; w < 200 && !IsHandleCreated && !IsDisposed; w++) await Task.Delay(100).ConfigureAwait(false);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Pencere henüz yok / kapanmış: sessizce dönüp kapıyı kilitli bırakma, hata olarak bildir (çağıran isteği saklar).
            if (!TryUiPost(() =>
            {
                try { tcs.TrySetResult(show()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            })) throw new InvalidOperationException("Ana pencere hazır değil.");
            // Gönderilen iş hiç çalışmadan form atılırsa (kapanış) kapı sonsuza dek beklemesin.
            var first = await Task.WhenAny(tcs.Task, _uiGone.Task).ConfigureAwait(false);
            if (first != tcs.Task) throw new InvalidOperationException("Ana pencere kapandı.");
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { _promptGate.Release(); }
    }

    /// <summary>Hedef sürücüde yer yoksa (boyut bilinmiyorsa 1 GB payın altındaysa) temalı uyarı: Yine de indir / İptal. Tek karar noktası: DiskSpaceGuard.Evaluate.</summary>
    private bool ConfirmDiskSpace(string? folder, long size, string? fileName = null)
    {
        var v = TDownloader.Core.Util.DiskSpaceGuard.Evaluate(_mgr.TargetFolder(folder, fileName), size);
        if (!v.Warn || v.Free is null) return true;
        using var w = new DiskWarnDialog(size, v.Free.Value);
        return w.ShowDialog() == DialogResult.Yes;
    }

    /// <summary>Devam/kuyruk/zamanlayıcı yolları: kalan boyuta göre denetim. Sessiz bağlamda (zamanlayıcı, T-Link) pencere açmadan atlar.</summary>
    private bool DiskStartGate(DownloadItem i)
    {
        long remaining = i.TotalSize > 0 ? Math.Max(0, i.TotalSize - i.Downloaded) : 0;
        string? folder = string.IsNullOrEmpty(i.SavePath) ? null : Path.GetDirectoryName(i.SavePath);
        if (DiskGate.IsSilent)
            return !TDownloader.Core.Util.DiskSpaceGuard.Evaluate(_mgr.TargetFolder(folder, i.FileName), remaining).Warn;
        if (!IsHandleCreated || IsDisposed) return true;
        if (InvokeRequired) { try { return (bool)Invoke(new Func<bool>(() => ConfirmDiskSpace(folder, remaining, i.FileName))); } catch (Exception) { return true; } }
        return ConfirmDiskSpace(folder, remaining, i.FileName);
    }

    private bool ShowFileInfo(string url, string? referrer, string? cookies, string? userAgent, string? fileName, long fileSize)
    {
        // Sahipsiz (owner yok): ana pencere öne gelmez; pencere TopMost + ön plana zorlanır.
        using var dlg = new FileInfoDialog(_mgr, url, fileName, fileSize, true, referrer, cookies, userAgent);
        var r = dlg.ShowDialog();
        if (r is not (DialogResult.Yes or DialogResult.No)) return false;
        if (!ConfirmDiskSpace(dlg.SaveFolder, dlg.FileSize > 0 ? dlg.FileSize : fileSize, dlg.FileName)) return false;
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

    private bool _diskDeleting;

    /// <summary>Seçilenleri durdurur, listeden kaldırır ve dosyaları Geri Dönüşüm Kutusu'na gönderir (arka planda).</summary>
    private async void DeleteSelectedFromDisk()
    {
        if (_diskDeleting) return;
        var sel = Selected();
        if (sel.Count == 0) return;
        _diskDeleting = true;
        try
        {
            var targets = sel.Select(i => (i.Id, Path: i.SavePath)).ToList();
            long total = await Task.Run(() => DiskDeletePlan.TotalExistingSize(targets.Select(t => t.Path)));
            if (MessageBox.Show(this, DiskDeletePlan.ConfirmText(targets.Count, total), "Diskten Sil", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            int failed = 0, skipped = 0; string? firstError = null;
            var owner = Handle;
            foreach (var (id, path) in targets)
            {
                await _mgr.RemoveAsync(id, false);   // süren indirmeyi durdurur, geçici parçaları temizler
                if (string.IsNullOrEmpty(path)) continue;
                var res = await Task.Run(() => (Outcome: RecycleBin.Send(path, owner, out var err), Err: err));
                if (res.Outcome == RecycleOutcome.Failed) { failed++; firstError ??= res.Err; }
                else if (res.Outcome == RecycleOutcome.Skipped) skipped++;
            }
            if (skipped > 0) MessageBox.Show(this, DiskDeletePlan.SkippedSummary(skipped), "Diskten Sil", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (failed > 0) MessageBox.Show(this, DiskDeletePlan.ErrorSummary(failed, firstError), "Diskten Sil", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _diskDeleting = false; }
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

    private void ShowSelectedHash()
    {
        var i = Selected().FirstOrDefault();
        if (i == null) return;
        if (!File.Exists(i.SavePath))
        {
            MessageBox.Show(this, "Dosya bulunamadı:\n" + i.SavePath, "T-Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        using var d = new HashDialog(i.SavePath, i.FileName);
        d.ShowDialog(Visible ? this : null);
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
        using var d = new AboutDialog(_svc.Paths.Root);
        d.ShowDialog(Visible ? this : null);
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

    /// <summary>Kullanıcı çıkışı: onay bekleyen tarayıcı indirmeleri varsa uyarır (tarayıcı kopyası zaten silindi; çıkışta bu istekler kaybolur) (A13).</summary>
    private void ExitAppByUser()
    {
        int n = PendingPromptCount;
        if (n > 0 && MessageBox.Show(this, $"{n} indirme onay penceresi bekliyor. Şimdi çıkarsanız bu indirmeler kaybolur (tarayıcı kopyaları silinmişti).\n\nYine de çıkılsın mı?",
                "Çıkış", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        ExitApp();
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
