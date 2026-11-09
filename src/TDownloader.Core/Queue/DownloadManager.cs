using System.Text.Json;
using TDownloader.Core.Downloads;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Queue;

public sealed record AddDownloadRequest(
    string Url,
    string? FileName = null,
    string? SaveFolder = null,
    string? Referrer = null,
    string? Cookie = null,
    string? UserAgent = null,
    string? Description = null,
    long FileSize = -1,
    bool StartNow = true);

/// <summary>
/// İndirme listesi, kuyruk (en fazla N aktif), global hız sınırı ve kalıcılık (downloads.json).
/// Olaylar arka plan thread'lerinden gelir; UI tarafında Invoke ile tüketilmelidir.
/// </summary>
public sealed class DownloadManager : IAsyncDisposable
{
    private sealed class Active
    {
        public required Action Stop { get; init; }
        public required Task Task { get; init; }
        public bool Pausing;   // _gate altında
    }

    // Duraklat tamamlanmadan gelen Devam istekleri (motor _active'ten çıkınca yeniden başlatılır).
    private readonly HashSet<string> _pendingResume = new();

    private readonly object _gate = new();
    private readonly List<DownloadItem> _items = new();
    // Hedef yol -> kullanım sayısı (OrdinalIgnoreCase); _gate altında _items ile birlikte güncellenir.
    private readonly Dictionary<string, int> _paths = new(StringComparer.OrdinalIgnoreCase);
    private void PathAdd(string? p) { if (string.IsNullOrEmpty(p)) return; _paths[p] = _paths.TryGetValue(p, out var n) ? n + 1 : 1; }
    private void PathRemove(string? p) { if (string.IsNullOrEmpty(p) || !_paths.TryGetValue(p, out var n)) return; if (n <= 1) _paths.Remove(p); else _paths[p] = n - 1; }
    private readonly Dictionary<string, Active> _active = new();
    private readonly HttpClient? _http;
    private readonly object _saveLock = new();
    private int _saveScheduled;
    private bool _disposed;

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; }
    public RateLimiter Limiter { get; } = new();
    /// <summary>Test/ince ayar için motor seçenekleri üzerinde son söz (bağlantı sayısı ayarlardan gelir).</summary>
    public Action<EngineOptions>? ConfigureEngine { get; set; }
    /// <summary>Kind == External olan öğeleri yürüten bileşen (yt-dlp).</summary>
    public IExternalRunner? ExternalRunner { get; set; }

    public event EventHandler<DownloadItem>? ItemAdded;
    public event EventHandler<DownloadItem>? ItemRemoved;
    /// <summary>Durum/ad/boyut gibi öğe alanları değişti (tamamlandı, duraklatıldı, hata...).</summary>
    public event EventHandler<DownloadItem>? ItemChanged;
    /// <summary>Aktif indirmelerin ilerlemesi (bağlantı başına durum dahil).</summary>
    public event EventHandler<DownloadProgress>? ProgressChanged;
    public event EventHandler<DownloadItem>? DownloadCompleted;
    public event EventHandler<DownloadItem>? DownloadFailed;

    public DownloadManager(AppPaths paths, AppSettings? settings = null, HttpClient? http = null)
    {
        Paths = paths;
        Settings = settings ?? AppSettings.Load(paths.SettingsFile);
        _http = http;
        ApplySettings();
    }

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    public DownloadItem? Get(string id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    public int ActiveCount { get { lock (_gate) return _active.Count; } }

    // ------------------------------------------------------------ ayarlar

    public void UpdateSettings(AppSettings settings)
    {
        Settings = settings;
        ApplySettings();
        ProcessQueue();
    }

    private void ApplySettings() => Limiter.BytesPerSecond = (long)Math.Max(0, Settings.SpeedLimitKBps) * 1024;

    // ------------------------------------------------------------ kalıcılık

    /// <summary>Son Load()'da ana dosya bozuk çıktıysa açıklama (yedekten yüklendi / liste boş); sorun yoksa null.</summary>
    public string? LoadWarning { get; private set; }

    private static List<DownloadItem>? TryReadList(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<List<DownloadItem>>(fs, JsonDefaults.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>downloads.json dosyasını yükler (bozuksa .bak yedeğinden). Yarım kalan indirmeler Paused olur.</summary>
    public void Load()
    {
        LoadWarning = null;
        var list = TryReadList(Paths.DownloadsFile);
        if (list == null && File.Exists(Paths.DownloadsFile))
        {
            // Ana dosya var ama okunamıyor: kanıt olarak ayır, yedekten yüklemeyi dene.
            try { File.Copy(Paths.DownloadsFile, Paths.DownloadsFile + ".corrupt", overwrite: true); } catch (IOException) { }
            list = TryReadList(Paths.DownloadsFile + ".bak");
            LoadWarning = list != null
                ? "downloads.json bozuktu; bir önceki sürüm (downloads.json.bak) yüklendi."
                : "downloads.json bozuktu ve yedek okunamadı; liste boş başlatıldı (bozuk dosya downloads.json.corrupt olarak saklandı).";
        }
        else if (list == null)
        {
            list = TryReadList(Paths.DownloadsFile + ".bak");   // ana dosya yok ama yedek var
            if (list != null) LoadWarning = "downloads.json yoktu; yedekten (downloads.json.bak) yüklendi.";
        }
        lock (_gate)
        {
            _items.Clear(); _paths.Clear();
            foreach (var i in list ?? new())
            {
                if (i == null || string.IsNullOrEmpty(i.Id) || !i.Id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is (char)45 or (char)95)) continue;   // null öğe / yol bileşeni olamayacak kimlik (C18, C22)
                if (i.Status is DownloadStatus.Downloading or DownloadStatus.Queued) i.Status = DownloadStatus.Paused;
                _items.Add(i); PathAdd(i.SavePath);
            }
        }
    }

    public void Save()
    {
        List<DownloadItem> snapshot;
        lock (_gate) snapshot = _items.ToList();
        lock (_saveLock)
        {
            // Girintisiz, doğrudan akışa; çerezler [JsonIgnore] olduğundan dosyaya yazılmaz.
            try { JsonDefaults.WriteAtomicWithBackup(Paths.DownloadsFile, st => JsonSerializer.Serialize(st, snapshot, JsonDefaults.Options)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>downloads.json kaydı gecikmesi (art arda değişiklikler tek yazma olur; UI thread'inde değil).</summary>
    public static int SaveDelayMs { get; set; } = 2000;

    private void RequestSave()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(SaveDelayMs).ConfigureAwait(false);
            Interlocked.Exchange(ref _saveScheduled, 0);
            Save();
        });
    }

    // ------------------------------------------------------------ ekleme / kaldırma

    public DownloadItem Add(AddDownloadRequest req)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Geçersiz URL (yalnızca http/https).", nameof(req));

        var name = SanitizeFileName(string.IsNullOrWhiteSpace(req.FileName) ? NameFromUri(uri) : req.FileName!);
        var category = Categories.FromFileName(name);
        var folder = !string.IsNullOrWhiteSpace(req.SaveFolder) ? req.SaveFolder! : FolderForCategory(category);
        var item = new DownloadItem
        {
            Url = req.Url, FileName = name, Category = category, TotalSize = req.FileSize > 0 ? req.FileSize : -1,
            Referrer = req.Referrer, Cookie = req.Cookie, UserAgent = req.UserAgent, Description = req.Description,
            Status = DownloadStatus.Paused,
        };
        ReserveUnique(item, folder, name);
        ItemAdded?.Invoke(this, item);
        RequestSave();
        if (req.StartNow) Start(item.Id);
        return item;
    }

    /// <summary>yt-dlp gibi harici süreçle indirilecek öğe ekler (http/https sayfa veya akış adresi).</summary>
    public DownloadItem AddExternal(AddExternalRequest req)
    {
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Geçersiz URL (yalnızca http/https).", nameof(req));
        var name = SanitizeFileName(string.IsNullOrWhiteSpace(req.FileName) ? "video.mp4" : req.FileName);
        var category = Categories.FromFileName(name);
        var folder = !string.IsNullOrWhiteSpace(req.SaveFolder) ? req.SaveFolder! : FolderForCategory(category);
        var item = new DownloadItem
        {
            Url = req.Url, FileName = name, Category = category, Referrer = req.Referrer, Description = req.Description,
            Kind = DownloadKind.External, ExternalFormat = req.Format, Status = DownloadStatus.Paused,
            Cookie = req.Cookie, CookieHost = req.CookieHost, UserAgent = req.UserAgent, Origin = req.Origin,
        };
        ReserveUnique(item, folder, name);
        ItemAdded?.Invoke(this, item);
        RequestSave();
        if (req.StartNow) Start(item.Id);
        return item;
    }

    /// <summary>Bir indirmenin gerçek hedef klasörü (açık klasör yoksa dosya adından kategori klasörü).</summary>
    public string TargetFolder(string? saveFolder, string? fileName) =>
        !string.IsNullOrWhiteSpace(saveFolder) ? saveFolder! : FolderForCategory(Categories.FromFileName(SanitizeFileName(string.IsNullOrWhiteSpace(fileName) ? "file" : fileName!)));

    public string FolderForCategory(string category)
    {
        if (Settings.CategoryFolders.TryGetValue(category, out var f) && !string.IsNullOrWhiteSpace(f)) return f;
        return Path.Combine(Settings.SaveFolder, category);
    }

    // Disk denetimi (File.Exists) kilit DIŞINDA; yol kümesi kontrolü + ekleme kilit altında (O(1)). Yarışta döngü yeniden dener.
    private void ReserveUnique(DownloadItem item, string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 0; ; i++)
        {
            var path = i == 0 ? Path.Combine(folder, name) : Path.Combine(folder, $"{stem} ({i}){ext}");
            bool taken;
            lock (_gate) taken = _paths.ContainsKey(path);
            if (taken || File.Exists(path)) continue;
            lock (_gate)
            {
                if (_paths.ContainsKey(path)) continue;
                item.SavePath = path;
                item.FileName = Path.GetFileName(path);
                _items.Add(item);
                PathAdd(path);
                return;
            }
        }
    }

    public static string NameFromUri(Uri uri)
    {
        var last = uri.Segments.Length > 0 ? uri.Segments[^1].Trim('/') : "";
        last = Uri.UnescapeDataString(last);
        return string.IsNullOrWhiteSpace(last) ? "indirme" : last;
    }

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "indirme" : name;
    }

    /// <summary>Öğeyi listeden kaldırır; deleteFile ile kayıtlı dosyayı da siler. Parça verisi her durumda temizlenir.</summary>
    public async Task RemoveAsync(string id, bool deleteFile = false)
    {
        Active? act; DownloadItem? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return;
            _active.TryGetValue(id, out act);
        }
        if (act != null) { act.Stop(); await act.Task.ConfigureAwait(false); }
        lock (_gate) { if (_items.Remove(item)) PathRemove(item.SavePath); }
        try { var d = Path.Combine(Paths.DwnlDataDir, id); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        // yt-dlp hedef klasörde .part/.ytdl/.part-FragN bırakır; tamamlanmamış harici indirme silinirken temizle.
        if (item.Kind == DownloadKind.External && item.Status != DownloadStatus.Completed)
            Util.ExternalCleanup.DeleteYtDlpLeftovers(item.SavePath);
        if (deleteFile)
        {
            try { if (File.Exists(item.SavePath)) File.Delete(item.SavePath); } catch { }
        }
        ItemRemoved?.Invoke(this, item);
        RequestSave();
        ProcessQueue();
    }

    // ------------------------------------------------------------ başlat / duraklat

    /// <summary>İndirmeyi kuyruğa alır; boş yer varsa hemen başlar.</summary>
    /// <summary>Başlatma kapısı (ör. disk koruması): false dönerse öğe başlatılmaz. UI/arayanın iş parçacığında, kilit dışında çağrılır.</summary>
    public Func<DownloadItem, bool>? StartGate { get; set; }

    public void Start(string id)
    {
        DownloadItem? item;
        var gate = StartGate;
        if (gate != null)
        {
            DownloadItem? probe;
            lock (_gate) probe = _items.FirstOrDefault(i => i.Id == id);
            if (probe != null && probe.Status is not (DownloadStatus.Completed or DownloadStatus.Downloading) && !IsActive(id)) { try { if (!gate(probe)) return; } catch (Exception) { } }
        }
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null || item.Status is DownloadStatus.Completed) return;
            if (_active.TryGetValue(id, out var running))
            {
                // Duraklatılıyor: motor çıkınca Devam uygulanır (ikinci tıklama gerekmesin).
                if (running.Pausing) _pendingResume.Add(id);
                return;
            }
            if (item.Status is DownloadStatus.Downloading) return;
            item.Status = DownloadStatus.Queued;
            item.Error = null;
        }
        ItemChanged?.Invoke(this, item);
        ProcessQueue();
    }

    private bool IsActive(string id) { lock (_gate) return _active.ContainsKey(id); }

    public void StartAll()
    {
        foreach (var i in Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed)) Start(i.Id);
    }

    public void Pause(string id)
    {
        DownloadItem? item; Active? act = null;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return;
            if (_active.TryGetValue(id, out act)) { act.Pausing = true; _pendingResume.Remove(id); /* motor Paused'a geçecek */ }
            else if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
            else return;
        }
        if (act != null) act.Stop();
        else { ItemChanged?.Invoke(this, item); RequestSave(); }
    }

    public void PauseAll()
    {
        foreach (var i in Items.Where(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Queued)) Pause(i.Id);
    }

    private void ProcessQueue()
    {
        List<DownloadItem> toStart = new();
        lock (_gate)
        {
            if (_disposed) return;
            int slots = Math.Max(1, Settings.MaxConcurrentDownloads) - _active.Count;
            foreach (var i in _items)
            {
                if (slots <= 0) break;
                if (i.Status == DownloadStatus.Queued && !_active.ContainsKey(i.Id)) { toStart.Add(i); slots--; }
            }
            foreach (var i in toStart) Launch(i);
        }
    }

    /// <summary>Olayı abone başına korumalı çağırır: bir abonenin istisnası diğerlerini ve kuyruğu etkilemez.</summary>
    private void Raise<T>(EventHandler<T>? handler, T arg)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((EventHandler<T>)d)(this, arg); }
            catch (Exception) { /* abone hatası motoru/kuyruğu durdurmaz */ }
        }
    }

    // _gate altında çağrılır
    private void Launch(DownloadItem item)
    {
        if (item.Kind == DownloadKind.External) { LaunchExternal(item); return; }
        var opt = new EngineOptions { Connections = Settings.EffectiveConnections, MaxConnectionsPerHost = Settings.MaxConnectionsPerHost };
        ConfigureEngine?.Invoke(opt);
        var engine = new DownloadEngine(item, Paths.DwnlDataDir, opt, Limiter, _http);
        engine.ProgressChanged += (_, p) => Raise(ProgressChanged, p);   // abone istisnası motoru etkilemesin
        engine.StatusChanged += (_, _) => Raise(ItemChanged, item);
        var task = Task.Run(async () =>
        {
            DownloadStatus result;
            try { result = await engine.RunAsync().ConfigureAwait(false); }
            catch (Exception ex) { item.Error = ex.Message; item.Status = DownloadStatus.Failed; result = DownloadStatus.Failed; }
            bool resume = false;
            try
            {
                resume = RemoveActive(item.Id);
                engine.Dispose();
                RequestSave();
                if (result == DownloadStatus.Completed) Raise(DownloadCompleted, item);
                else if (result == DownloadStatus.Failed) Raise(DownloadFailed, item);
            }
            finally
            {
                // Olay abonesi istisna fırlatsa bile kuyruk sürmeli.
                if (resume) Start(item.Id);
                ProcessQueue();
            }
        });
        _active[item.Id] = new Active { Stop = engine.Pause, Task = task };
    }

    private bool RemoveActive(string id)
    {
        lock (_gate) { _active.Remove(id); return _pendingResume.Remove(id); }
    }

    // _gate altında çağrılır
    private void LaunchExternal(DownloadItem item)
    {
        var cts = new CancellationTokenSource();
        var runner = ExternalRunner;
        item.Status = DownloadStatus.Downloading;
        item.Error = null;
        item.LastTryAt = DateTime.Now;
        var task = Task.Run(async () =>
        {
            Raise(ItemChanged, item);
            ExternalResult? res = null;
            bool canceled = false;
            try
            {
                if (runner == null) res = new ExternalResult(false, "Video indirici (yt-dlp) yapılandırılmamış.", null);
                else
                    res = await runner.RunAsync(item, p =>
                    {
                        item.Downloaded = p.Downloaded;
                        if (p.Total > 0) item.TotalSize = p.Total;
                        item.SpeedBytesPerSec = p.SpeedBytesPerSec;
                        Raise(ProgressChanged, new DownloadProgress
                        {
                            Id = item.Id, Downloaded = p.Downloaded, TotalSize = p.Total > 0 ? p.Total : item.TotalSize,
                            SpeedBytesPerSec = p.SpeedBytesPerSec,
                            Eta = p.EtaSeconds is double e && e >= 0 && e < 8_640_000 ? TimeSpan.FromSeconds(e) : null,
                            Phase = p.Phase,
                        });
                    }, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception ex) { res = new ExternalResult(false, ex.Message, null); }

            item.SpeedBytesPerSec = 0;
            if (canceled || (res is { Success: false } && cts.IsCancellationRequested)) item.Status = DownloadStatus.Paused;
            else if (res is { Success: true })
            {
                if (!string.IsNullOrWhiteSpace(res.FinalPath) && File.Exists(res.FinalPath))
                {
                    lock (_gate) { PathRemove(item.SavePath); item.SavePath = res.FinalPath; PathAdd(item.SavePath); }
                    item.FileName = Path.GetFileName(res.FinalPath);
                    item.Category = Categories.FromFileName(item.FileName);
                    try { item.TotalSize = new FileInfo(res.FinalPath).Length; } catch (IOException) { }
                }
                if (item.TotalSize > 0) item.Downloaded = item.TotalSize;
                item.Status = DownloadStatus.Completed;
            }
            else { item.Error = res?.Error ?? "Bilinmeyen hata."; item.Status = DownloadStatus.Failed; }

            bool resume = false;
            try
            {
                resume = RemoveActive(item.Id);
                cts.Dispose();
                Raise(ItemChanged, item);
                RequestSave();
                if (item.Status == DownloadStatus.Completed) Raise(DownloadCompleted, item);
                else if (item.Status == DownloadStatus.Failed) Raise(DownloadFailed, item);
            }
            finally
            {
                if (resume) Start(item.Id);
                ProcessQueue();
            }
        });
        _active[item.Id] = new Active { Stop = () => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }, Task = task };
    }

    /// <summary>Tüm aktifleri duraklatır, durumları diske yazar. Uygulama kapanırken çağrılmalı.</summary>
    public async Task ShutdownAsync()
    {
        List<Active> acts;
        lock (_gate) { _disposed = true; acts = _active.Values.ToList(); }
        foreach (var a in acts) a.Stop();
        await Task.WhenAll(acts.Select(a => a.Task)).ConfigureAwait(false);
        lock (_gate)
            foreach (var i in _items)
                if (i.Status is DownloadStatus.Queued or DownloadStatus.Downloading) i.Status = DownloadStatus.Paused;
        Save();
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
