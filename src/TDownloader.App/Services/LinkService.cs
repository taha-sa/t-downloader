using System.Text.Json;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
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
    private static readonly string[] Events = ["download.progress", "download.completed", "download.failed", "app.update.available"];
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
            srv.OnCommand("download.pause", (d, _) => Task.FromResult(WithId(d, id => _mgr.Pause(id))));
            srv.OnCommand("download.resume", (d, _) => Task.FromResult(WithId(d, id => { using var _s = DiskGate.Silent(); _mgr.Start(id); })));
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
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("url gerekli");
        // Disk koruması (T-Link üzerinden gelen eklemede de): yer yoksa "disk_full;free=<bayt>;needed=<bayt>" ile reddet.
        long needed = d is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("size", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt64(out var sz) && sz > 0 ? sz : 0;
        // Hedef sürücü dosya adının kategori klasörüne göre; boyut yoksa yalnız 1 GB payı denetlenir.
        var v = TDownloader.Core.Util.DiskSpaceGuard.Evaluate(_mgr.TargetFolder(null, Str(d, "fileName") ?? System.IO.Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url)), needed);
        if (v.Warn && v.Free is { } fb)
            throw new InvalidOperationException($"disk_full;free={fb};needed={v.Needed}");
        var item = _mgr.Add(new AddDownloadRequest(url, FileName: Str(d, "fileName"), Referrer: Str(d, "referrer")));
        return Task.FromResult<JsonElement?>(LinkJson.Obj(("id", item.Id)));
    }

    private static JsonElement? WithId(JsonElement? d, Action<string> act)
    {
        var id = Str(d, "id");
        if (string.IsNullOrEmpty(id)) throw new ArgumentException("id gerekli");
        act(id);
        return LinkJson.Obj();
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
        try { _registry.MarkStopped(AppId); } catch { }
        if (_server != null) { try { await _server.DisposeAsync().ConfigureAwait(false); } catch { } }
    }
}
