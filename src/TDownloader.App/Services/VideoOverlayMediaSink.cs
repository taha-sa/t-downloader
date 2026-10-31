using System.Diagnostics;
using TDownloader.App.Ui;
using TDownloader.Core.Ipc;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;
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

    // yt-dlp bilgisi sayfa açılınca arka planda alınır; menü açılınca hazır olur.
    private static readonly TimeSpan InfoTtl = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (DateTime At, Task<VideoInfo> Task)> _info = new(StringComparer.Ordinal);

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
        t.Frames[m.FrameId] = m.Items ?? new();
        foreach (var i in m.Items ?? new()) if (!t.Seen.ContainsKey(i.Url)) t.Seen[i.Url] = ++t.Counter;
        if (!string.IsNullOrEmpty(m.PageUrl)) t.PageUrl = m.PageUrl;
        if (!string.IsNullOrEmpty(m.PageTitle)) t.PageTitle = m.PageTitle;
    });

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
        if (!_tabs.TryGetValue(id, out var t)) _tabs[id] = t = new TabState();
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
        return new Sources(direct, page, direct.Count == 0 ? page : null, MediaContext.Empty, false, null);
    }

    /// <summary>Öğenin kendi başlıkları; yoksa sayfa adresi Referer olur.</summary>
    public static MediaContext ContextOf(MediaItem i, string? page) =>
        new(string.IsNullOrWhiteSpace(i.Referer) ? page : i.Referer, i.Origin, i.Cookies, i.UserAgent);

    /// <summary>
    /// Önbellekteki (veya yeni başlatılan) kalite bilgisi görevi. HLS akışları önce master playlist'ten doğrudan okunur
    /// (yt-dlp'siz, çok hızlı); olmazsa yt-dlp'ye düşer. Başarısız/eskimiş görev yeniden başlatılır.
    /// </summary>
    private Task<VideoInfo> InfoFor(string source, MediaContext ctx, bool hls, string? title)
    {
        var now = DateTime.UtcNow;
        if (_info.TryGetValue(source, out var e) && now - e.At < InfoTtl && !e.Task.IsFaulted && !e.Task.IsCanceled)
            return e.Task;
        foreach (var k in _info.Where(p => now - p.Value.At >= InfoTtl).Select(p => p.Key).ToList()) _info.Remove(k);
        var t = Task.Run(async () =>
        {
            if (hls)
            {
                try { return await _hls.GetInfoAsync(source, ctx, title).ConfigureAwait(false); }
                catch (HlsException ex) when (ex.IsDrm) { throw new YtDlpException(ex.Message); }
                catch (Exception ex) when (ex is HlsException or HttpRequestException or OperationCanceledException or UriFormatException)
                {
                    if (_yt.FindYtDlp() == null) throw new YtDlpException("Akış okunamadı: " + ex.Message);
                }
            }
            return await _yt.GetInfoAsync(source, ctx.Referer, default, ctx).ConfigureAwait(false);
        });
        _info[source] = (now, t);
        return t;
    }

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
            var all = new ToolStripMenuItem("Tümünü indir");
            all.Click += (_, _) => { foreach (var d in direct) DownloadDirect(d, page, tab.PageTitle); };
            menu.Items.Insert(0, all);
            menu.Items.Insert(1, new ToolStripSeparator());
        }

        ToolStripMenuItem? loading = null;
        if (source != null)
        {
            if (!hls && _yt.FindYtDlp() == null)
            {
                var miss = new ToolStripMenuItem("yt-dlp bulunamadı — kurulum bilgisi") { ForeColor = Theme.Danger };
                miss.Click += (_, _) => ShowInstallInfo();
                menu.Items.Add(miss);
            }
            else
            {
                loading = new ToolStripMenuItem("Kaliteler yükleniyor…") { Enabled = false };
                menu.Items.Add(loading);
            }
        }
        else if (direct.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("Bu sayfada indirilebilir medya bulunamadı") { Enabled = false });
        }
        menu.Items.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem("Diğer kaliteler...");
        other.Click += (_, _) => Msg("Burada listelenmeyen bir kalite için, videoyu oynatıcıda istediğiniz kaliteye ayarlayıp birkaç saniye oynatın.\n" +
                                      "Tarayıcı eklentisi o kalitenin adresini algılarsa bu listeye eklenir.\n\nYine de bulamazsanız yt-dlp ile sayfa adresinden indirmeyi deneyin.",
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
                catch (YtDlpNotFoundException) { Post(() => ReplaceLoading(menu, loadingItem, "yt-dlp bulunamadı — kurulum bilgisi", ShowInstallInfo)); }
                catch (YtDlpException ex) { Post(() => ReplaceLoading(menu, loadingItem, "Hata: " + Shorten(ex.Message, 80), () => Msg(ex.Message, MessageBoxIcon.Warning))); }
                catch (Exception ex) { Post(() => ReplaceLoading(menu, loadingItem, "Hata: " + Shorten(ex.Message, 80), null)); }
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

    public async Task<QualitiesResultMessage> GetQualitiesAsync(QualitiesMessage q)
    {
        static QualitiesResultMessage Fail(string id, string msg, string code) => new(id, false, msg, code, null, null, null);
        if (_svc == null) return Fail(q.RequestId, "Uygulama hazır değil.", "error");

        var tcs = new TaskCompletionSource<(Sources S, string? Title, string? Page)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                var tab = Tab(q.TabId);
                if (!string.IsNullOrEmpty(q.PageUrl)) tab.PageUrl = q.PageUrl;
                if (!string.IsNullOrEmpty(q.PageTitle)) tab.PageTitle = q.PageTitle;
                var s = Resolve(tab, q.FrameId);
                // Video öğesi doğrudan http(s) dosyası oynatıyorsa (blob değil) ve listede yoksa ekle.
                if (IsHttp(q.Src) && !HlsClient.LooksLikeHlsUrl(q.Src!) && !s.Direct.Any(d => d.Url == q.Src))
                    s.Direct.Insert(0, new MediaItem(q.Src!, "", "file", q.FrameUrl));
                tcs.TrySetResult((s, tab.PageTitle, tab.PageUrl));
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        var (src, pageTitle, pageUrl) = await tcs.Task.ConfigureAwait(false);

        var entries = new List<Entry>();
        var infos = new List<QualityOptionInfo>();
        string title = string.IsNullOrWhiteSpace(pageTitle) ? "Video" : pageTitle!;
        string? error = null, code = null;
        string? streamSource = src.Source;
        var ctx = src.Ctx;

        if (streamSource != null)
        {
            if (!src.Hls && _yt.FindYtDlp() == null) { error = "yt-dlp bulunamadı."; code = "no_ytdlp"; }
            else
            {
                try
                {
                    var info = await InfoFor(streamSource, ctx, src.Hls, pageTitle).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(info.Title)) title = info.Title;
                    int def = YtDlpParsing.PickDefault(info.Options, Settings.DefaultVideoQuality);
                    for (int i = 0; i < info.Options.Count; i++)
                    {
                        var o = info.Options[i];
                        entries.Add(new Entry(null, o));
                        infos.Add(new QualityOptionInfo(o.Label, o.AudioOnly ? "audio" : "video", o.Height, o.Size, o.AudioOnly, i == def));
                    }
                }
                catch (YtDlpNotFoundException) { error = "yt-dlp bulunamadı."; code = "no_ytdlp"; }
                catch (YtDlpException ex) { error = ex.Message; code = "error"; }
                catch (Exception ex) { error = ex.Message; code = "error"; }
            }
        }
        foreach (var d in src.Direct)
        {
            entries.Add(new Entry(d, null));
            infos.Add(new QualityOptionInfo(Shorten(DirectName(d, pageTitle), 48), "file", 0, 0, false, infos.Count == 0));
        }

        if (entries.Count == 0)
            return Fail(q.RequestId, error ?? "Bu sayfada indirilebilir medya bulunamadı.", code ?? "no_media");

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
        _ = _svc!.PromptDownloadAsync(item.Url, string.IsNullOrWhiteSpace(item.Referer) ? page : item.Referer, item.Cookies, item.UserAgent, DirectName(item, title));
    }

    private void StartQuality(string source, string? referer, MediaContext ctx, string title, QualityOption opt)
    {
        // İndirme başlamadan "Dosya İndirme Bilgisi" penceresi açılır (IDM gibi).
        _ = _svc!.PromptExternalAsync(source, ctx.Referer ?? referer, title, opt, ctx);
    }

    // ------------------------------------------------------------ bilgi kutuları

    private static void Msg(string text, MessageBoxIcon icon)
    {
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        owner.Show();
        MessageBox.Show(owner, text, "T-Downloader video paneli", MessageBoxButtons.OK, icon);
    }

    private void ShowAbout() => Msg(
        "T-Downloader video paneli\n\n" +
        "Düğmeye tıklayınca videonun kalite listesi açılır; tek bir medya varsa doğrudan indirilir.\n" +
        "Paneli fare ile sürükleyerek taşıyabilirsiniz (site başına hatırlanır); yüzeyine çift tıklarsanız ilk konumuna döner.\n" +
        "X düğmesi paneli bu sekmede gizler. Panel Seçenekler > Video sekmesinden tamamen kapatılabilir.\n\n" +
        "DRM korumalı içerikler desteklenmez.", MessageBoxIcon.Information);

    public static string InstallInfoText(string toolsDir) =>
        "yt-dlp bulunamadı.\n\n" +
        "HLS/DASH akışları ve YouTube gibi siteler için yt-dlp (ve video+ses birleştirmek için ffmpeg) gerekir. T-Downloader bunları kendisi indirmez/kurmaz.\n\n" +
        "• yt-dlp: https://github.com/yt-dlp/yt-dlp/releases\n" +
        "• ffmpeg: https://www.gyan.dev/ffmpeg/builds/ (veya https://ffmpeg.org)\n\n" +
        "yt-dlp.exe ve ffmpeg.exe dosyalarını şu klasöre koyun:\n" + toolsDir + "\n" +
        "ya da Seçenekler > Video sekmesinden yollarını gösterin.\n\n" +
        "yt-dlp indirme sayfası tarayıcıda açılsın mı?";

    private void ShowInstallInfo()
    {
        var tools = (_svc as AppServices)?.Paths.ToolsDir ?? AppPaths.Default.ToolsDir;
        using var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1, 1) };
        owner.Show();
        var r = MessageBox.Show(owner, InstallInfoText(tools), "yt-dlp bulunamadı", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/yt-dlp/yt-dlp/releases") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }
    }
}
