using TDownloader.Core.Localization;
using System.Text.Json;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Ipc;
using TDownloader.Core.Util;
using TSuite.Link;
using TSuite.Update;

namespace TDownloader.App.Services;

/// <summary>
/// T-Link (T-Suite) sunucusu: \\.\pipe\TSuite.t-downloader. Olaylar: download.progress (kimlik başına birleştirilir, YALNIZCA abone
/// varken üretilir), download.completed, download.failed. Komutlar: download.add/pause/resume/list, app.show, app.version.
/// Kayıt (manifest) başlangıçta yazılır, çıkışta silinir. T-Link isteğe bağlıdır: hata uygulamayı etkilemez.
/// </summary>
public sealed class LinkService : IAsyncDisposable
{
    public const string AppId = "t-downloader";
    private static readonly string[] Events = ["download.progress", "download.state", "download.completed", "download.failed", "app.update.available"];
    private static readonly string[] Commands = ["download.add", "download.pause", "download.resume", "download.list", "app.show", "app.version", .. UpdateLink.Commands];

    private readonly AppServices _svc;
    private readonly DownloadManager _mgr;
    private readonly AppRegistry _registry = new();
    private readonly Dictionary<string, long> _lastProgress = new();
    private LinkServer? _server;

    public LinkService(AppServices svc) { _svc = svc; _mgr = svc.Manager; }

    public void Publish(string name, JsonElement data) => _server?.Publish(name, data);

    public void PublishUpdateAvailable(TSuite.Update.UpdateInfo info) =>
        _server?.Publish("app.update.available", LinkJson.Obj(("version", info.NewVersion), ("notes", info.Notes)));

    /// Standart güncelleme komutlarını (app.update.check/install) bağlar.
    public void RegisterUpdate(UpdateController ctl)
    {
        if (_server != null) UpdateLink.Register(_server, ctl);
    }

    public void Start()
    {
        try
        {
            var srv = new LinkServer(AppId, AppServices.Version, Events, Commands);
            srv.OnCommand("download.add", OnAdd);
            srv.OnCommand("download.pause", (d, _) => Task.FromResult(ControlDownload(d, pause: true)));
            srv.OnCommand("download.resume", (d, _) => Task.FromResult(ControlDownload(d, pause: false)));
            srv.OnCommand("download.list", (_, _) => Task.FromResult<JsonElement?>(ListItems()));
            srv.OnCommand("app.show", (_, _) =>
            {
                try { _svc.Main?.BeginInvoke(_svc.Main.ShowMain); } catch (InvalidOperationException) { }
                return Task.FromResult<JsonElement?>(LinkJson.Obj());
            });
            srv.OnCommand("app.version", (_, _) =>
                Task.FromResult<JsonElement?>(LinkJson.Obj(("version", AppServices.Version), ("protocol", LinkProtocol.Version))));
            srv.Start();
            _server = srv;

            _mgr.ProgressChanged += OnProgress;
            _mgr.DownloadCompleted += OnCompleted;
            _mgr.DownloadFailed += OnFailed;
            _mgr.ItemChanged += OnItemChanged;
            _mgr.ItemRemoved += OnItemRemoved;

            _registry.CleanupStale();
            _registry.Write(new AppManifest
            {
                Id = AppId, Name = "T-Downloader", Version = AppServices.Version,
                ExePath = Environment.ProcessPath ?? "", Pid = Environment.ProcessId,
                Events = Events, Commands = Commands, ReleasesRepo = "taha-sa/t-downloader-releases",
                Icon = Environment.ProcessPath,
            });
        }
        catch { /* T-Link opsiyonel */ }
    }

    // ---- olaylar ----

    private void OnProgress(object? sender, DownloadProgress p)
    {
        var srv = _server;
        if (srv == null || !srv.HasSubscriber("download.progress")) return;      // abone yoksa hiçbir şey üretilmez
        long now = Environment.TickCount64;
        lock (_lastProgress)
        {
            if (_lastProgress.TryGetValue(p.Id, out var last) && now - last < 500) return;   // kimlik başına en çok 2 Hz
            _lastProgress[p.Id] = now;
        }
        var it = _mgr.Get(p.Id);
        srv.Publish("download.progress", LinkJson.Obj(
            ("id", p.Id), ("name", it?.FileName ?? ""), ("bytes", p.Downloaded), ("total", p.TotalSize),
            ("speed", p.SpeedBytesPerSec), ("pct", p.TotalSize > 0 ? p.Downloaded * 100.0 / p.TotalSize : 0.0), ("state", "downloading")),
            key: p.Id);
    }

    private void OnCompleted(object? sender, DownloadItem i)
    {
        lock (_lastProgress) _lastProgress.Remove(i.Id);
        _server?.Publish("download.completed", LinkJson.Obj(
            ("id", i.Id), ("fileName", i.FileName), ("path", i.SavePath), ("size", i.TotalSize > 0 ? i.TotalSize : i.Downloaded),
            ("url", i.Url), ("mime", MimeTypes.FromFileName(i.FileName))));
    }

    /// <summary>Hız sınırlayıcı kayıtlarının sayısı (sızıntı denetimi için).</summary>
    public int TrackedProgressCount { get { lock (_lastProgress) return _lastProgress.Count; } }

    // Duraklatılan / sıraya dönen / iptal edilen kimlikler artık ilerleme üretmez: kaydı temizle.
    private void OnItemChanged(object? sender, DownloadItem i)
    {
        if (i.Status != DownloadStatus.Downloading) lock (_lastProgress) _lastProgress.Remove(i.Id);
        if (_server is { } srv && srv.HasSubscriber("download.state") && DownloadStateEvent.Create(i) is { } state)
            srv.Publish("download.state", state, key: i.Id);
    }

    private void OnItemRemoved(object? sender, DownloadItem i)
    {
        lock (_lastProgress) _lastProgress.Remove(i.Id);
    }

    private void OnFailed(object? sender, DownloadItem i)
    {
        lock (_lastProgress) _lastProgress.Remove(i.Id);
        _server?.Publish("download.failed", LinkJson.Obj(("id", i.Id), ("name", i.FileName), ("error", i.Error ?? "")));
    }

    // ---- komutlar ----

    private static string? Str(JsonElement? d, string prop) =>
        d is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private Task<JsonElement?> OnAdd(JsonElement? d, CancellationToken ct)
    {
        var url = Str(d, "url");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(Strings.InvalidURLEnterAnHTTPOrHTTPSURL);
        // Disk koruması (T-Link üzerinden gelen eklemede de): yer yoksa "disk_full;free=<bayt>;needed=<bayt>" ile reddet.
        long needed = d is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("size", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt64(out var sz) && sz > 0 ? sz : 0;
        // Hedef sürücü dosya adının kategori klasörüne göre; boyut yoksa yalnız 1 GB payı denetlenir.
        var probeName = Str(d, "fileName") ?? System.IO.Path.GetFileName(parsedUri.AbsolutePath);
        var v = TDownloader.Core.Util.DiskSpaceGuard.Evaluate(_mgr.TargetFolder(null, probeName), needed);
        if (v.Warn && v.Free is { } fb)
            throw new InvalidOperationException($"disk_full;free={fb};needed={v.Needed}");
        var item = _mgr.Add(new AddDownloadRequest(url, FileName: Str(d, "fileName"), Referrer: Str(d, "referrer")));
        return Task.FromResult<JsonElement?>(LinkJson.Obj(("id", item.Id)));
    }

    private JsonElement? ControlDownload(JsonElement? d, bool pause)
    {
        var id = Str(d, "id");
        if (string.IsNullOrEmpty(id)) throw new ArgumentException(Strings.ADownloadIDIsRequiredSelectADownload);
        var item = _mgr.Get(id) ?? throw new InvalidOperationException("download_unavailable");
        if (pause)
        {
            if (item.Status is not (DownloadStatus.Downloading or DownloadStatus.Queued)) throw new InvalidOperationException("download_not_active");
            _mgr.Pause(id); // Running downloads report the final paused state after the worker stops.
        }
        else
        {
            if (item.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) throw new InvalidOperationException("download_not_paused");
            using var _s = DiskGate.Silent();
            _mgr.Start(id);
            if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed) throw new InvalidOperationException("download_not_started");
        }
        return LinkJson.Obj(("accepted", true));
    }

    private JsonElement ListItems()
    {
        var rows = _mgr.Items.Select(i => new
        {
            id = i.Id, fileName = i.FileName, url = i.Url, status = i.Status.ToString().ToLowerInvariant(),
            downloaded = i.Downloaded, total = i.TotalSize, path = i.SavePath,
        }).ToList();
        return JsonSerializer.SerializeToElement(new { items = rows });
    }

    public async ValueTask DisposeAsync()
    {
        _mgr.ProgressChanged -= OnProgress;
        _mgr.DownloadCompleted -= OnCompleted;
        _mgr.DownloadFailed -= OnFailed;
        _mgr.ItemChanged -= OnItemChanged;
        _mgr.ItemRemoved -= OnItemRemoved;
        try { _registry.MarkStopped(AppId); } catch { }
        if (_server != null) { try { await _server.DisposeAsync().ConfigureAwait(false); } catch { } }
    }
}
