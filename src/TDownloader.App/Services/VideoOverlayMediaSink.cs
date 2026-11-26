using TDownloader.Core.Localization;
using System.Diagnostics;
using TDownloader.App.Ui;
using TDownloader.Core.Ipc;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;
using TDownloader.Core.Util;
using TDownloader.Core.Video;

namespace TDownloader.App.Services;

/// <summary>
/// Tarayıcıdaki videoların üstünde yerel "Bu videoyu indir" düğmesini yönetir: eklentiden gelen `media` ve
/// `videoGeometry` mesajlarını alır, konumu hesaplar (ButtonPlacement), menüyü kurar ve indirmeyi başlatır.
/// IPC thread'inden gelen çağrılar UI thread'ine aktarılır; tüm durum yalnızca UI thread'inde değişir.
/// </summary>
public sealed class VideoOverlayMediaSink : IMediaSink
{
    private sealed class TabState
    {
        public string? PageUrl, PageTitle;
        public readonly Dictionary<int, List<MediaItem>> Frames = new();
        public readonly Dictionary<string, long> Seen = new(StringComparer.Ordinal);   // url -> geliş sırası
        public long Counter;
        public DateTime LastUsed = DateTime.UtcNow;
        public IEnumerable<MediaItem> Items => Frames.Values.SelectMany(x => x);
    }

    private readonly YtDlpClient _yt;
    private readonly HlsClient _hls = new();
    private IAppServices? _svc;
    private VideoButtonWindow? _win;
    private System.Windows.Forms.Timer? _tick;
    private readonly Dictionary<int, TabState> _tabs = new();
    private readonly Dictionary<int, string?> _hiddenTabs = new();
    private VideoGeometryMessage? _raw, _eff;
    private int _tabId = -1;
    private IntPtr _browser;
    private ContextMenuStrip? _menu;
    private CancellationTokenSource? _lookupCts;
    private IntPtr _menuBrowser;
    private string _menuTitle = "";

    // Kalite bilgisi: kilitli, sınırlı (son 50) ve ömürlü (10 dk) önbellek; aynı kaynak için devam eden iş paylaşılır,
    // tüm bekleyenler vazgeçince iş iptal edilir (yt-dlp süreci öldürülür). Anahtar imzasız URL.
    private const int InfoCapacity = 50, MaxTabs = 64;
    private static readonly TimeSpan InfoTtl = TimeSpan.FromMinutes(10);
    private readonly SharedTaskCache<VideoInfo> _info = new(InfoCapacity, InfoTtl);
    // Aynı anda en fazla 2 yt-dlp -J (CPU/bellek/ağ patlamasını önler).
    private readonly SemaphoreSlim _ytGate = new(2, 2);
    private readonly Dictionary<string, (int TabId, CancellationTokenSource Cts)> _running = new();   // requestId -> iptal

    public VideoOverlayMediaSink(YtDlpClient yt) { _yt = yt; }

    private AppSettings Settings => _svc!.Settings;

    // ------------------------------------------------------------ IMediaSink

    public void Attach(IAppServices services)
    {
        _svc = services;
        _tick = new System.Windows.Forms.Timer { Interval = 500 };
        _tick.Tick += (_, _) =>
        {
            // Kalite menüsü açıkken kullanıcı başka sekmeye/pencereye geçtiyse menüyü kapat.
            if (_menu != null && _menuBrowser != IntPtr.Zero &&
                (!BrowserWindows.IsUsable(_menuBrowser) || BrowserWindows.Title(_menuBrowser) != _menuTitle))
                CloseMenu();
            if (_raw?.Visible == true && _win is { Dragging: false }) Reposition();
        };
        _tick.Start();
    }

    public void OnMedia(MediaMessage m) => Post(() =>
    {
        var t = Tab(m.TabId);
        // Append=true: yalnızca yeni öğeler gelir, mevcut listeye eklenir; değilse çerçeve listesi değiştirilir (boş = temizle).
        t.Frames.TryGetValue(m.FrameId, out var existing);
        t.Frames[m.FrameId] = MediaMerge.Merge(existing, m);
        foreach (var i in m.Items ?? new()) if (!t.Seen.ContainsKey(i.Url)) t.Seen[i.Url] = ++t.Counter;
        if (t.Seen.Count > 300)   // sınırsız büyümesin: artık hiçbir çerçevede olmayan adresleri at
        {
            var live = new HashSet<string>(t.Items.Select(i => i.Url), StringComparer.Ordinal);
            foreach (var k in t.Seen.Keys.Where(k => !live.Contains(k)).ToList()) t.Seen.Remove(k);
        }
        if (!string.IsNullOrEmpty(m.PageUrl)) t.PageUrl = m.PageUrl;
        if (!string.IsNullOrEmpty(m.PageTitle)) t.PageTitle = m.PageTitle;
    });

    public void OnTabClosed(int tabId) => Post(() =>
    {
        _tabs.Remove(tabId);
        _hiddenTabs.Remove(tabId);
        foreach (var kv in _running.Where(r => r.Value.TabId == tabId).ToList()) CancelRunning(kv.Key);
        if (_tabId == tabId) { _tabId = -1; _raw = null; HideWin(); }
    });

    // İptal, istek kaydı oluşmadan önce gelebilir (App'te Task.Run -> Post gecikmesi): kimlik "iptal edildi" kümesine yazılır, istek başında bakılır (A15).
    private readonly HashSet<string> _cancelledEarly = new();
    private readonly Queue<string> _cancelledOrder = new();

    public void OnQualitiesCancel(string requestId) => Post(() =>
    {
        if (_running.ContainsKey(requestId)) { CancelRunning(requestId); return; }
        if (_cancelledEarly.Add(requestId))
        {
            _cancelledOrder.Enqueue(requestId);
            while (_cancelledOrder.Count > 100) _cancelledEarly.Remove(_cancelledOrder.Dequeue());
        }
    });

    // Kayıt SİLİNMEZ: GetQualitiesAsync'in finally'si kaydı siler ve Cts'yi dispose eder (A15: iptal sonrası Dispose sızıntısı).
    private void CancelRunning(string requestId)
    {
        if (_running.TryGetValue(requestId, out var r)) { try { r.Cts.Cancel(); } catch (ObjectDisposedException) { } }
    }

    // Eklenti saniyede onlarca konum mesajı gönderebilir; UI thread'ine yalnızca sekme başına en sonuncusu gider.
    private readonly TDownloader.Core.Util.LatestCoalescer<int, VideoGeometryMessage> _geo = new();

    public void OnVideoGeometry(VideoGeometryMessage g)
    {
        if (_svc == null || !Settings.UseNativeVideoButton) return;   // sayfa içi düğme kullanılıyor
        if (_geo.Offer(g.TabId, g)) Post(() => { foreach (var m in _geo.Drain()) Apply(m); });
    }

    public void Detach()
    {
        void Dispose()
        {
            _tick?.Stop(); _tick?.Dispose(); _tick = null;
            CloseMenu();
            _win?.Dispose(); _win = null;
        }
        try
        {
            if (_svc?.UiThread is { InvokeRequired: true } ui) ui.Invoke((Action)Dispose, null); else Dispose();
        }
        catch (Exception) { }
    }

    private void Post(Action a)
    {
        var ui = _svc?.UiThread;
        if (ui == null) return;
        try { ui.BeginInvoke((Action)(() => { try { a(); } catch (Exception) { /* UI thread'i düşürme */ } }), null); } catch (Exception) { }
    }

    private TabState Tab(int id)
    {
        if (!_tabs.TryGetValue(id, out var t))
        {
            _tabs[id] = t = new TabState();
            if (_tabs.Count > MaxTabs)   // üst sınır: en uzun süredir kullanılmayan (etkin olmayan) sekme atılır
            {
                var old = _tabs.Where(p => p.Key != id && p.Key != _tabId).OrderBy(p => p.Value.LastUsed).Select(p => p.Key).FirstOrDefault(-1);
                if (old != -1) { _tabs.Remove(old); _hiddenTabs.Remove(old); }
            }
        }
        t.LastUsed = DateTime.UtcNow;
        return t;
    }

    // ------------------------------------------------------------ geometri

    private void Apply(VideoGeometryMessage g)
    {
        if (_svc == null) return;
        var tab = Tab(g.TabId);
        if (!string.IsNullOrEmpty(g.PageUrl)) tab.PageUrl = g.PageUrl;
        if (!string.IsNullOrEmpty(g.PageTitle)) tab.PageTitle = g.PageTitle;

        if (_hiddenTabs.TryGetValue(g.TabId, out var hiddenUrl))
        {
            if (string.Equals(hiddenUrl, g.PageUrl, StringComparison.Ordinal)) return;
            _hiddenTabs.Remove(g.TabId);   // sekme başka sayfaya gitti: düğme yeniden görünebilir
        }
        if (!g.Visible)
        {
            if (g.TabId == _tabId) { _raw = g; HideWin(); }
            return;
        }
        if (g.TabId == _tabId && g == _raw && _win is { Visible: true }) return;   // aynı konum: yeniden çizme
        if (g.TabId != _tabId) CloseMenu();   // başka sekmenin videosu: eski menü kapanır
        _tabId = g.TabId;
        _raw = g;
        Reposition();
    }

    private void HideWin()
    {
        _win?.HidePanel();
    }

    private VideoButtonWindow EnsureWindow()
    {
        if (_win != null) return _win;
        _win = new VideoButtonWindow(Ui.Dlu.Scale);
        _win.MainClicked += ShowMenu;
        _win.HelpClicked += ShowAbout;
        _win.CloseClicked += HideInTab;
        _win.DragCompleted += OnDragCompleted;
        _win.ResetRequested += OnReset;
        return _win;
    }

    private static string HostOf(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";

    private void Reposition()
    {
        if (_svc == null || _raw == null) return;
        if (!Settings.ShowVideoButton || !_raw.Visible || _hiddenTabs.ContainsKey(_tabId)) { HideWin(); return; }

        var match = BrowserWindows.Locate(_raw.BrowserWindow);
        _browser = match?.Hwnd ?? IntPtr.Zero;
        var geo = match is { } mm ? _raw with { BrowserWindow = mm.Rect } : _raw;
        _eff = geo;

        if (_browser != IntPtr.Zero && !BrowserWindows.IsUsable(_browser)) { HideWin(); return; }

        var win = EnsureWindow();
        double dpr = geo.DevicePixelRatio > 0 ? geo.DevicePixelRatio : 1;
        var client = ButtonPlacement.ClientArea(geo.BrowserWindow, geo.Viewport, dpr);
        var video = ButtonPlacement.VideoOnScreen(client, geo.Rect, dpr);
        win.SetCompact(video.Width < win.FullWidth * 0.75);

        Settings.VideoButtonOffsets.TryGetValue(HostOf(Tab(_tabId).PageUrl ?? geo.PageUrl), out var off);
        var pl = ButtonPlacement.Compute(geo, win.PanelSize.Width, win.PanelSize.Height, off?.X ?? 0, off?.Y ?? 0);
        if (!pl.Visible) { HideWin(); return; }

        if (_browser != IntPtr.Zero)
        {
            // Başka bir uygulama tarayıcıyı örtüyorsa düğmeyi gösterme.
            int cx = (int)((Math.Max(video.Left, client.Left) + Math.Min(video.Right, client.Right)) / 2);
            int cy = (int)((Math.Max(video.Top, client.Top) + Math.Min(video.Bottom, client.Bottom)) / 2);
            if (!BrowserWindows.IsBrowserOnTopAt(_browser, cx, cy)) { HideWin(); return; }
        }
        win.PlaceAt(pl.X, pl.Y);
        win.ShowPanel();
    }

    private void OnDragCompleted(Point loc)
    {
        if (_eff == null || _win == null) return;
        var (ox, oy) = ButtonPlacement.OffsetFromDrag(_eff, loc.X, loc.Y, _win.PanelSize.Width, _win.PanelSize.Height);
        var host = HostOf(Tab(_tabId).PageUrl ?? _eff.PageUrl);
        if (host.Length > 0)
        {
            Settings.VideoButtonOffsets[host] = new ButtonOffset { X = ox, Y = oy };
            (_svc as AppServices)?.SaveSettings();
        }
        Reposition();
    }

    private void OnReset()
    {
        var host = HostOf(Tab(_tabId).PageUrl ?? _eff?.PageUrl);
        if (Settings.VideoButtonOffsets.Remove(host)) (_svc as AppServices)?.SaveSettings();
        CloseMenu();
        Reposition();
    }

    private void HideInTab()
    {
        CloseMenu();
        _hiddenTabs[_tabId] = Tab(_tabId).PageUrl ?? _raw?.PageUrl;
        _ = _svc!.SendToExtensionAsync(new HideButtonMessage(_tabId));
        HideWin();
    }

    // ------------------------------------------------------------ menü

    private static bool IsHttp(string? u) => Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == "http" || x.Scheme == "https");

    private void CloseMenu()
    {
        try { _lookupCts?.Cancel(); } catch (ObjectDisposedException) { }
        _lookupCts = null;
        var m = _menu; _menu = null;
        if (m != null) { try { m.Close(); m.Dispose(); } catch (Exception) { } }
        if (_win != null) _win.MenuOpen = false;
    }

    private readonly record struct Sources(List<MediaItem> Direct, string? Page, string? Source, MediaContext Ctx, bool Hls, string? Referer);

    /// <summary>Sekmedeki medyaları çözer: doğrudan dosyalar ve en son görülen akış (HLS/DASH). Akışın başlıkları iframe'den gelir.</summary>
    private static Sources Resolve(TabState tab, int preferFrame = -1)
    {
        var direct = tab.Items.Where(i => i.Kind == "file" && IsHttp(i.Url)).DistinctBy(i => i.Url).Take(25).ToList();
        // Düğmenin çizildiği çerçevenin akışı önce; yoksa sekmedeki en son görülen.
        var streams = tab.Frames.SelectMany(f => f.Value.Select(i => (Frame: f.Key, Item: i)))
            .Where(x => (x.Item.Kind == "hls" || x.Item.Kind == "dash") && IsHttp(x.Item.Url));
        var stream = streams.OrderByDescending(x => x.Frame == preferFrame ? 1 : 0)
            .ThenByDescending(x => tab.Seen.GetValueOrDefault(x.Item.Url)).Select(x => x.Item).FirstOrDefault();
        string? page = IsHttp(tab.PageUrl) ? tab.PageUrl : null;
        if (stream != null)
        {
            var ctx = ContextOf(stream, page);
            return new Sources(direct, page, stream.Url, ctx, stream.Kind == "hls" || HlsClient.LooksLikeHlsUrl(stream.Url), ctx.Referer);
        }
        bool isVideoSite = IsKnownVideoSite(page);
        return new Sources(direct, page, isVideoSite || direct.Count == 0 ? page : null, MediaContext.Empty, false, null);
    }

    /// <summary>Öğenin kendi başlıkları; yoksa sayfa adresi Referer olur.</summary>
    public static bool IsKnownVideoSite(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return url.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("instagram.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("tiktok.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("facebook.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("fb.watch/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("twitter.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("x.com/", StringComparison.OrdinalIgnoreCase);
    }
    public static MediaContext ContextOf(MediaItem i, string? page) => MediaContexts.ContextOf(i, page);
    public static MediaContext ForSource(MediaContext ctx, string? url) => MediaContexts.ForSource(ctx, url);
    public static string Scrub(string? text) => MediaContexts.Scrub(text);

    /// <summary>
    /// Önbellekteki (veya yeni başlatılan) kalite bilgisi görevi. HLS akışları önce master playlist'ten doğrudan okunur
    /// (yt-dlp'siz, çok hızlı); olmazsa yt-dlp'ye düşer. Başarısız/eskimiş görev yeniden başlatılır.
    /// </summary>
    private Task<VideoInfo> InfoFor(string source, MediaContext ctx, bool hls, string? title, CancellationToken waiter = default) =>
        _info.GetOrStart(QualityCacheKey.For(source), async ct =>
        {
            ctx = ForSource(ctx, source);
            if (hls)
            {
                try { return await _hls.GetInfoAsync(source, ctx, title, ct).ConfigureAwait(false); }
                catch (HlsException ex) when (ex.IsDrm) { throw new YtDlpException(ex.Message); }
                catch (Exception ex) when (ex is HlsException or HttpRequestException or OperationCanceledException or UriFormatException)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_yt.FindYtDlp() == null) throw new YtDlpException(Strings.CouldNotReadTheStreamOpenTheVideo + ex.Message);
                }
            }
            await _ytGate.WaitAsync(ct).ConfigureAwait(false);
            try { return await _yt.GetInfoAsync(source, ctx.Referer, ct, ctx).ConfigureAwait(false); }
            finally { _ytGate.Release(); }
        }, waiter);

    private void ShowMenu()
    {
        if (_svc == null || _win == null) return;
        CloseMenu();
        var tab = Tab(_tabId);
        var (direct, page, source, ctx, hls, referer) = Resolve(tab);

        // Tek doğrudan medya: menüsüz indir.
        if (source == null && direct.Count == 1) { DownloadDirect(direct[0], page, tab.PageTitle); return; }

        var menu = new ContextMenuStrip { Renderer = new ThemeRenderer(), Font = Theme.UiFont(9f), ShowImageMargin = false, ShowCheckMargin = false };
        _menu = menu;
        int n = 1;
        foreach (var d in direct)
        {
            string name = DirectName(d, tab.PageTitle);
            var it = new ToolStripMenuItem($"{n++}.  {Shorten(name, 60)}   {MimeLabel(d.Mime)}");
            var cap = d;
            it.Click += (_, _) => DownloadDirect(cap, page, tab.PageTitle);
            menu.Items.Add(it);
        }
        if (direct.Count >= 2)
        {
            var all = new ToolStripMenuItem(Strings.DownloadAll);
            all.Click += (_, _) => { foreach (var d in direct) DownloadDirect(d, page, tab.PageTitle); };
            menu.Items.Insert(0, all);
            menu.Items.Insert(1, new ToolStripSeparator());
        }

        ToolStripMenuItem? loading = null;
        if (source != null)
        {
            if (!hls && _yt.FindYtDlp() == null)
            {
                var miss = new ToolStripMenuItem(Strings.YtDlpNotFoundInstallationInformation) { ForeColor = Theme.Danger };
                miss.Click += (_, _) => ShowInstallInfo();
                menu.Items.Add(miss);
            }
            else
            {
                loading = new ToolStripMenuItem(Strings.LoadingQualities) { Enabled = false };
                menu.Items.Add(loading);
            }
        }
        else if (direct.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem(Strings.NoDownloadableMediaFoundOnThisPagePlay) { Enabled = false });
        }
        menu.Items.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem(Strings.OtherQualities);
        other.Click += (_, _) => Msg(Strings.ForAQualityNotListedHereSelectIt +
                                      Strings.IfTheBrowserExtensionDetectsThatQualityURL,
                                      MessageBoxIcon.Information);
        menu.Items.Add(other);

        menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) { _win!.MenuOpen = false; } };
        _win.MenuOpen = true;
        _menuBrowser = _browser;
        _menuTitle = BrowserWindows.Title(_browser);

        Task<VideoInfo>? infoTask = loading != null && source != null ? InfoFor(source, ctx, hls, tab.PageTitle) : null;
        if (infoTask is { IsCompletedSuccessfully: true })
        {
            FillQualities(menu, loading!, infoTask.Result, source!, ctx, page ?? referer);   // önceden alınmış: anında
            infoTask = null;
        }

        var r = _win.Bounds;
        menu.Show(r.Left, r.Bottom - 2);

        if (infoTask != null)
        {
            var cts = _lookupCts = new CancellationTokenSource();
            string src = source!;
            var loadingItem = loading!;
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await infoTask.WaitAsync(cts.Token).ConfigureAwait(false);
                    Post(() => { if (ReferenceEquals(_menu, menu)) FillQualities(menu, loadingItem, info, src, ctx, page ?? referer); });
                }
                catch (OperationCanceledException) { }
                catch (YtDlpNotFoundException) { Post(() => ReplaceLoading(menu, loadingItem, Strings.YtDlpNotFoundInstallationInformation, ShowInstallInfo)); }
                catch (YtDlpException ex) { Post(() => ReplaceLoading(menu, loadingItem, Strings.ActionFailedTryAgainDetails + Shorten(ex.Message, 80), () => Msg(ex.Message, MessageBoxIcon.Warning))); }
                catch (Exception ex) { Post(() => ReplaceLoading(menu, loadingItem, Strings.ActionFailedTryAgainDetails + Shorten(ex.Message, 80), null)); }
            });
        }
    }

    private void ReplaceLoading(ContextMenuStrip menu, ToolStripMenuItem loading, string text, Action? click)
    {
        if (!ReferenceEquals(_menu, menu)) return;
        int i = menu.Items.IndexOf(loading);
        if (i < 0) return;
        menu.Items.RemoveAt(i);
        var it = new ToolStripMenuItem(text) { ForeColor = Theme.Danger, Enabled = click != null };
        if (click != null) it.Click += (_, _) => click();
        menu.Items.Insert(i, it);
    }

    private void FillQualities(ContextMenuStrip menu, ToolStripMenuItem loading, VideoInfo info, string source, MediaContext ctx, string? referer)
    {
        int i = menu.Items.IndexOf(loading);
        if (i < 0) return;
        menu.Items.RemoveAt(i);
        int def = YtDlpParsing.PickDefault(info.Options, Settings.DefaultVideoQuality);
        int n = 1;
        foreach (var opt in info.Options)
        {
            var it = new ToolStripMenuItem($"{n}.  {opt.Label}");
            if (n - 1 == def) it.Font = new Font(menu.Font, FontStyle.Bold);
            var cap = opt;
            it.Click += (_, _) => StartQuality(source, referer, ctx, info.Title, cap);
            menu.Items.Insert(i + n - 1, it);
            n++;
        }
    }

    // ------------------------------------------------------------ sayfa içi düğme (eklenti) için kalite listesi

    private sealed record Entry(MediaItem? File, QualityOption? Quality);
    private sealed record Selection(List<Entry> Entries, string Source, MediaContext Ctx, string Title, string? Page, string? Referer);
    private readonly Dictionary<string, Selection> _selections = new();

    private MediaLog? _mlog;
    private MediaLog Mlog => _mlog ??= new MediaLog(((_svc as AppServices)?.Paths ?? AppPaths.Default).MediaLogFile);

    private static string MimeFromUrl(string url) => Path.GetExtension(url.Split('?', '#')[0]).ToLowerInvariant() switch
    {
        ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime", ".m4v" => "video/x-m4v", _ => "video/mp4",
    };

    /// <summary>Ana videoya ait olmayabilecek küçük/kısa doğrudan dosya mı? Boyut biliniyorsa &lt; 2 MB ya da ana kalitenin %5'inden küçük.</summary>
    public static bool IsMinorFile(long size, long mainSize) =>
        size > 0 && (size < 2L * 1024 * 1024 || (mainSize > 0 && size < mainSize * 0.05));

    private sealed record Snap(List<(int Frame, MediaItem Item)> Items, List<MediaItem> Hls, string? FrameDoc);

    public async Task<QualitiesResultMessage> GetQualitiesAsync(QualitiesMessage q)
    {
        static QualitiesResultMessage Fail(string id, string msg, string code) => new(id, false, msg, code, null, null, null);
        if (_svc == null) return Fail(q.RequestId, Strings.TheAppIsNotReadyWaitAndTry, "error");

        var tcs = new TaskCompletionSource<(Sources S, string? Title, string? Page, Snap Snap, CancellationToken Ct)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                var cts = new CancellationTokenSource();
                _running[q.RequestId] = (q.TabId, cts);
                if (_cancelledEarly.Remove(q.RequestId)) cts.Cancel();      // iptal istekten önce gelmişti
                var tab = Tab(q.TabId);
                // Çerezler medya mesajlarıyla gelmez; düğme tıklanınca tek seferde (origin -> başlık) gelir ve öğelere uygulanır.
                if (q.Cookies is { Count: > 0 })
                    foreach (var fk in tab.Frames.Keys.ToList()) tab.Frames[fk] = MediaCookies.Apply(tab.Frames[fk], q.Cookies, q.CookieHosts);
                if (!string.IsNullOrEmpty(q.PageUrl)) tab.PageUrl = q.PageUrl;
                if (!string.IsNullOrEmpty(q.PageTitle)) tab.PageTitle = q.PageTitle;
                var s = Resolve(tab, q.FrameId);
                // Video öğesi doğrudan http(s) dosyası oynatıyorsa (blob değil) ve listede yoksa ekle.
                if (IsHttp(q.Src) && !HlsClient.LooksLikeHlsUrl(q.Src!) && !s.Direct.Any(d => d.Url == q.Src))
                    s.Direct.Insert(0, new MediaItem(q.Src!, "", "file", q.FrameUrl));
                // Yakalanan TÜM HLS adresleri: düğmenin çerçevesindekiler; yoksa sekmedeki tümü. En yeni önce.
                var all = tab.Frames.SelectMany(f => f.Value.Select(i => (Frame: f.Key, Item: i))).ToList();
                var hlsAll = all.Where(x => x.Item.Kind == "hls" && IsHttp(x.Item.Url)).ToList();
                var mine = hlsAll.Where(x => x.Frame == q.FrameId).ToList();
                var hls = (mine.Count > 0 ? mine : hlsAll).Select(x => x.Item).DistinctBy(i => i.Url)
                    .OrderByDescending(i => tab.Seen.GetValueOrDefault(i.Url)).ToList();
                tcs.TrySetResult((s, tab.PageTitle, tab.PageUrl, new Snap(all, hls, q.FrameUrl), cts.Token));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        var (src, pageTitle, pageUrl, snap, reqCt) = await tcs.Task.ConfigureAwait(false);
        try { return await BuildQualitiesAsync(q, src, pageTitle, pageUrl, snap, reqCt).ConfigureAwait(false); }
        catch (OperationCanceledException) when (reqCt.IsCancellationRequested) { return Fail(q.RequestId, Strings.Cancelled, "error"); }
        finally { Post(() => { if (_running.Remove(q.RequestId, out var r)) r.Cts.Dispose(); }); }
    }

    private async Task<QualitiesResultMessage> BuildQualitiesAsync(QualitiesMessage q, Sources src, string? pageTitle, string? pageUrl, Snap snap, CancellationToken reqCt)
    {
        static QualitiesResultMessage Fail(string id, string msg, string code) => new(id, false, msg, code, null, null, null);

        // Tanı günlüğü: bu sekme/çerçeve için bilinenler (host+yol; sorgu/imza/çerez YOK).
        var players = (q.Players ?? new()).Where(p => IsHttp(p.Url)).Take(40).ToList();
        Mlog.Write($"CLICK tab={q.TabId} frame={q.FrameId} page={MediaLog.Sanitize(pageUrl)} frameDoc={MediaLog.Sanitize(q.FrameUrl)} captured={snap.Items.Count} hls={snap.Hls.Count} players={players.Count}");
        foreach (var (frame, it) in snap.Items.Take(60))
            Mlog.Write($"  item frame={frame} kind={it.Kind} mime={(string.IsNullOrEmpty(it.Mime) ? "-" : it.Mime)} size={(it.Size > 0 ? it.Size.ToString() : "-")} url={MediaLog.Sanitize(it.Url)}");
        foreach (var p in players)
            Mlog.Write($"  player label={(string.IsNullOrEmpty(p.Label) ? "-" : p.Label)} height={p.Height} url={MediaLog.Sanitize(p.Url)}");

        var entries = new List<Entry>();
        var infos = new List<QualityOptionInfo>();
        string title = string.IsNullOrWhiteSpace(pageTitle) ? Strings.Video : pageTitle!;
        string? error = null, code = null;
        string? streamSource = src.Source;
        var ctx = src.Ctx;
        var streamOpts = new List<QualityOption>();
        var otherOpts = new List<QualityOption>();
        string? hlsSource = null;

        // Oynatıcıdan okunanlar: m3u8 -> HLS adayı; mp4 vb. -> etiketli doğrudan dosya
        var cands = snap.Hls.Select(i => new HlsCandidate(i.Url)).ToList();
        var fileHints = new Dictionary<string, PlayerSource>(StringComparer.Ordinal);
        foreach (var p in players)
        {
            if (HlsClient.LooksLikeHlsUrl(p.Url)) cands.Add(new HlsCandidate(p.Url, p.Label, p.Height, true));
            else
            {
                fileHints[p.Url] = p;
                if (!src.Direct.Any(d => d.Url == p.Url))
                    src.Direct.Add(new MediaItem(p.Url, MimeFromUrl(p.Url), "file", string.IsNullOrEmpty(q.FrameUrl) ? pageUrl : q.FrameUrl));
            }
        }

        if (cands.Count > 0)
        {
            hlsSource = cands[0].Url;
            ctx = snap.Hls.Count > 0 ? ContextOf(snap.Hls[0], pageUrl)
                : new MediaContext(string.IsNullOrEmpty(q.FrameUrl) ? pageUrl : q.FrameUrl, null, null, null);
            string key = QualityCacheKey.ForSet(cands.Select(c => c.Url));
            try
            {
                bool fresh = !_info.Contains(key);
                var info = await _info.GetOrStart(key, ct => _hls.GetInfoSetAsync(cands, ctx, pageTitle, probeMaster: true, l => Mlog.Write("  " + l), ct), reqCt).ConfigureAwait(false);
                if (!fresh) Mlog.Write(Strings.CachedQualityList);
                if (!string.IsNullOrWhiteSpace(info.Title)) title = info.Title;
                streamOpts.AddRange(info.Options.Where(o => !o.Other));
                otherOpts.AddRange(info.Options.Where(o => o.Other));
            }
            catch (HlsException ex) when (ex.IsDrm) { error = ex.Message; code = "error"; }
            catch (Exception ex) when (ex is HlsException or HttpRequestException or OperationCanceledException or UriFormatException)
            {
                reqCt.ThrowIfCancellationRequested();
                Mlog.Write(Strings.HLSSelectionFailedTryAgainDetails + Scrub(ex.Message));
                // yt-dlp'ye düş (varsa): en yeni akış adresiyle
                if (_yt.FindYtDlp() == null) { error = Strings.CouldNotReadTheStreamOpenTheVideo + ex.Message; code = "error"; }
                else
                {
                    try
                    {
                        var info = await InfoFor(cands[0].Url, ctx, true, pageTitle, reqCt).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(info.Title)) title = info.Title;
                        streamOpts.AddRange(info.Options);
                    }
                    catch (OperationCanceledException) when (reqCt.IsCancellationRequested) { throw; }
                    catch (YtDlpException yex) { error = yex.Message; code = "error"; }
                    catch (Exception yex) { error = yex.Message; code = "error"; }
                }
            }
            streamSource = hlsSource;
        }
        else if (streamSource != null)
        {
            if (!src.Hls && _yt.FindYtDlp() == null) { error = Strings.YtDlpNotFoundSetItsPathIn; code = "no_ytdlp"; }
            else
            {
                try
                {
                    var info = await InfoFor(streamSource, ctx, src.Hls, pageTitle, reqCt).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(info.Title)) title = info.Title;
                    streamOpts.AddRange(info.Options);
                }
                catch (OperationCanceledException) when (reqCt.IsCancellationRequested) { throw; }
                catch (YtDlpNotFoundException) { error = Strings.YtDlpNotFoundSetItsPathIn; code = "no_ytdlp"; }
                catch (YtDlpException ex) { error = ex.Message; code = "error"; }
                catch (Exception ex) { error = ex.Message; code = "error"; }
            }
        }

        int def = YtDlpParsing.PickDefault(streamOpts, Settings.DefaultVideoQuality);
        for (int i = 0; i < streamOpts.Count; i++)
        {
            var o = streamOpts[i];
            entries.Add(new Entry(null, o));
            infos.Add(new QualityOptionInfo(o.Label, o.AudioOnly ? "audio" : "video", o.Height, o.Size, o.AudioOnly, i == def));
        }
        long mainSize = streamOpts.Count == 0 ? 0 : streamOpts.Max(o => o.Size);

        bool isVideoSite = IsKnownVideoSite(pageUrl ?? q.PageUrl);

        // Doğrudan dosyalar: etiketli (oynatıcıdan) önce, sonra yakalananlar; küçük/kısa olanlar "Diğer dosyalar"a
        var files = src.Direct.Select(d => (D: d, Hint: fileHints.GetValueOrDefault(d.Url)))
            .OrderByDescending(x => x.Hint?.Height ?? 0).ToList();
        var minor = new List<(MediaItem D, string Label)>();
        foreach (var (d, hint) in files)
        {
            if (isVideoSite && streamOpts.Count > 0 && hint == null) continue;
            string label = hint != null && (!string.IsNullOrEmpty(hint.Label) || hint.Height > 0)
                ? (string.IsNullOrEmpty(hint.Label) ? hint.Height + "p" : hint.Label) + " " + Path.GetExtension(d.Url.Split('?', '#')[0]).TrimStart('.').ToLowerInvariant()
                : Shorten(DirectName(d, pageTitle), 48);
            if (hint == null && IsMinorFile(d.Size, mainSize)) { minor.Add((d, label)); continue; }
            entries.Add(new Entry(d, null));
            infos.Add(new QualityOptionInfo(label.Trim(), "file", hint?.Height ?? 0, Math.Max(0, d.Size), false, infos.Count == 0));
        }
        foreach (var o in otherOpts)
        {
            entries.Add(new Entry(null, o));
            infos.Add(new QualityOptionInfo(o.Label, "other", 0, o.Size, false, false));
        }
        if (!isVideoSite || streamOpts.Count == 0)
        {
            foreach (var (d, label) in minor)
            {
                entries.Add(new Entry(d, null));
                infos.Add(new QualityOptionInfo(label, "other", 0, Math.Max(0, d.Size), false, false));
            }
        }

        if (entries.Count == 0)
        {
            Mlog.Write($"RESULT fail code={code ?? "no_media"} err={Scrub(error)}");
            return Fail(q.RequestId, error ?? Strings.NoDownloadableMediaFoundOnThisPagePlay2, code ?? "no_media");
        }
        Mlog.Write($"RESULT ok n={infos.Count} [{string.Join(" | ", infos.Select(i => i.Kind + ":" + i.Label))}]");

        string id = Guid.NewGuid().ToString("N");
        var sel = new Selection(entries, streamSource ?? "", ctx, title, pageUrl, src.Referer);
        Post(() =>
        {
            if (_selections.Count >= 20) _selections.Remove(_selections.Keys.First());
            _selections[id] = sel;
        });
        return new QualitiesResultMessage(q.RequestId, true, null, null, title, id, infos);
    }

    public void OnQualitySelect(QualitySelectMessage m) => Post(() =>
    {
        if (!_selections.TryGetValue(m.SelectionId, out var sel) || m.Index < 0 || m.Index >= sel.Entries.Count) return;
        var e = sel.Entries[m.Index];
        if (e.File != null) DownloadDirect(e.File, sel.Page, sel.Title);
        else if (e.Quality != null) StartQuality(sel.Source, sel.Page ?? sel.Referer, sel.Ctx, sel.Title, e.Quality);
    });

    // ------------------------------------------------------------ indirme

    private static string DirectName(MediaItem m, string? title)
    {
        if (Uri.TryCreate(m.Url, UriKind.Absolute, out var u))
        {
            var name = Core.Queue.DownloadManager.NameFromUri(u);
            if (Path.HasExtension(name)) return name;
        }
        string ext = (m.Mime ?? "").ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4", "video/webm" => ".webm", "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a",
            "audio/ogg" => ".ogg", "video/x-matroska" => ".mkv", _ => ".mp4",
        };
        var stem = string.IsNullOrWhiteSpace(title) ? "video" : title!;
        return Core.Queue.DownloadManager.SanitizeFileName(Shorten(stem, 100)) + ext;
    }

    private static string MimeLabel(string? mime) => string.IsNullOrEmpty(mime) ? "" : "(" + mime + ")";

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void DownloadDirect(MediaItem item, string? page, string? title)
    {
        // force:true: video düğmesinden gelen indirme HER ZAMAN onay penceresi gösterir ("capture sonrası dosya bilgisi" kapalı olsa bile sessiz indirme yok; A2).
        _ = _svc!.PromptDownloadAsync(item.Url, string.IsNullOrWhiteSpace(item.Referer) ? page : item.Referer, ForSource(ContextOf(item, page), item.Url).Cookies, item.UserAgent, DirectName(item, title), force: true);
    }

    private void StartQuality(string source, string? referer, MediaContext ctx, string title, QualityOption opt)
    {
        // İndirme başlamadan "Dosya İndirme Bilgisi" penceresi açılır (IDM gibi).
        _ = _svc!.PromptExternalAsync(source, ctx.Referer ?? referer, title, opt, ForSource(ctx, source));
    }

    // ------------------------------------------------------------ bilgi kutuları

    private static void Msg(string text, MessageBoxIcon icon)
    {
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        owner.Show();
        MessageBox.Show(owner, text, Strings.TDownloaderVideoPanel, MessageBoxButtons.OK, icon);
    }

    private void ShowAbout() => Msg(
        Strings.TDownloaderVideoPanel2 +
        Strings.ClickTheButtonToViewVideoQualitiesA +
        Strings.DragThePanelToMoveItSavedPer +
        Strings.TheXButtonHidesThePanelInThis +
        Strings.DRMContentIsNotSupportedChooseUnprotectedContent, MessageBoxIcon.Information);

    public static string InstallInfoText(string toolsDir) =>
        Strings.YtDlpNotFound +
        Strings.HLSDASHStreamsAndSitesSuchAsYouTube +
        "• yt-dlp: https://github.com/yt-dlp/yt-dlp/releases\n" +
        Strings.FfmpegHttpsWwwGyanDevFfmpegBuildsOr +
        Strings.PlaceYtDlpExeAndFfmpegExeIn + toolsDir + "\n" +
        Strings.OrSetTheirPathsInSettingsVideo +
        Strings.OpenTheYtDlpDownloadPageInYour;

    private void ShowInstallInfo()
    {
        var tools = (_svc as AppServices)?.Paths.ToolsDir ?? AppPaths.Default.ToolsDir;
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        owner.Show();
        var r = MessageBox.Show(owner, InstallInfoText(tools), Strings.YtDlpNotFound2, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/yt-dlp/yt-dlp/releases") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
    }
}
