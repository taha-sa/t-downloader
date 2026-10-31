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
    }

    private readonly object _gate = new();
    private readonly List<DownloadItem> _items = new();
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
            _items.Clear();
            foreach (var i in list ?? new())
            {
                if (i.Status is DownloadStatus.Downloading or DownloadStatus.Queued) i.Status = DownloadStatus.Paused;
                _items.Add(i);
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

    private void RequestSave()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
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
        lock (_gate)
        {
            item.SavePath = UniquePath(folder, name);
            item.FileName = Path.GetFileName(item.SavePath);
            _items.Add(item);
        }
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
            Cookie = req.Cookie, UserAgent = req.UserAgent, Origin = req.Origin,
        };
        lock (_gate)
        {
            item.SavePath = UniquePath(folder, name);
            item.FileName = Path.GetFileName(item.SavePath);
            _items.Add(item);
        }
        ItemAdded?.Invoke(this, item);
        RequestSave();
        if (req.StartNow) Start(item.Id);
        return item;
    }

    public string FolderForCategory(string category)
    {
        if (Settings.CategoryFolders.TryGetValue(category, out var f) && !string.IsNullOrWhiteSpace(f)) return f;
        return Path.Combine(Settings.SaveFolder, category);
    }

    private string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        if (!PathTaken(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!PathTaken(path)) return path;
        }
    }

    private bool PathTaken(string path) =>
        File.Exists(path) || _items.Any(i => string.Equals(i.SavePath, path, StringComparison.OrdinalIgnoreCase));

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
        lock (_gate) _items.Remove(item);
        try { var d = Path.Combine(Paths.DwnlDataDir, id); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
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
    public void Start(string id)
    {
        DownloadItem? item;
        lock (_gate)
        {
            item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null || item.Status is DownloadStatus.Completed or DownloadStatus.Downloading || _active.ContainsKey(id)) return;
            item.Status = DownloadStatus.Queued;
            item.Error = null;
        }
        ItemChanged?.Invoke(this, item);
        ProcessQueue();
    }

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
            if (_active.TryGetValue(id, out act)) { /* motor Paused'a geçecek */ }
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
        engine.ProgressChanged += (_, p) => { ProgressChanged?.Invoke(this, p); };
        engine.StatusChanged += (_, _) => ItemChanged?.Invoke(this, item);
        var task = Task.Run(async () =>
        {
            DownloadStatus result;
            try { result = await engine.RunAsync().ConfigureAwait(false); }
            catch (Exception ex) { item.Error = ex.Message; item.Status = DownloadStatus.Failed; result = DownloadStatus.Failed; }
            try
            {
                lock (_gate) _active.Remove(item.Id);
                engine.Dispose();
                RequestSave();
                if (result == DownloadStatus.Completed) Raise(DownloadCompleted, item);
                else if (result == DownloadStatus.Failed) Raise(DownloadFailed, item);
            }
            finally
            {
                // Olay abonesi istisna fırlatsa bile kuyruk sürmeli.
                ProcessQueue();
            }
        });
        _active[item.Id] = new Active { Stop = engine.Pause, Task = task };
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
            ItemChanged?.Invoke(this, item);
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
                        ProgressChanged?.Invoke(this, new DownloadProgress
                        {
                            Id = item.Id, Downloaded = p.Downloaded, TotalSize = p.Total > 0 ? p.Total : item.TotalSize,
                            SpeedBytesPerSec = p.SpeedBytesPerSec,
                            Eta = p.EtaSeconds is double e && e >= 0 && e < 8_640_000 ? TimeSpan.FromSeconds(e) : null,
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
                    item.SavePath = res.FinalPath;
                    item.FileName = Path.GetFileName(res.FinalPath);
                    item.Category = Categories.FromFileName(item.FileName);
                    try { item.TotalSize = new FileInfo(res.FinalPath).Length; } catch (IOException) { }
                }
                if (item.TotalSize > 0) item.Downloaded = item.TotalSize;
                item.Status = DownloadStatus.Completed;
            }
            else { item.Error = res?.Error ?? "Bilinmeyen hata."; item.Status = DownloadStatus.Failed; }

            try
            {
                lock (_gate) _active.Remove(item.Id);
                cts.Dispose();
                Raise(ItemChanged, item);
                RequestSave();
                if (item.Status == DownloadStatus.Completed) Raise(DownloadCompleted, item);
                else if (item.Status == DownloadStatus.Failed) Raise(DownloadFailed, item);
            }
            finally
            {
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
