using TDownloader.Core.Downloads;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Queue;

/// <summary>
/// Zamanlayıcı mantığı (UI'dan bağımsız, zaman dışarıdan verilir). Başlangıç saati kaçırılırsa (uyku/uygulama kapalı) telafi eder,
/// çalıştırılacak öğe yoksa izlemeyi açmaz, yalnız bu izleme sırasında biten indirmeleri sayar, "exit" eylemi geri sayımla iptal edilebilir.
/// </summary>
public sealed class SchedulerCore
{
    /// <summary>Uygulama kapalıyken kaçan başlangıcın ilk Tick'te telafi edileceği azami süre.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ExitCountdown = TimeSpan.FromSeconds(30);

    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<DownloadItem>> _items;
    private readonly Action _startAll;
    private DateTime? _lastCheck;
    private string _lastFiredKey = "";
    private bool _watching;
    private HashSet<string> _completedAtWatch = new();
    private DateTime? _exitAt;

    public event Action<string>? Notify;
    public event Action? ExitRequested;
    /// <summary>Çıkış geri sayımı: kalan saniye (iptal edilince -1).</summary>
    public event Action<int>? ExitCountdownChanged;

    public SchedulerCore(Func<AppSettings> settings, Func<IReadOnlyList<DownloadItem>> items, Action startAll)
    { _settings = settings; _items = items; _startAll = startAll; }

    public bool Watching => _watching;
    public bool ExitPending => _exitAt != null;

    /// <summary>Elle kuyruk başlatıldığında "bitince" eylemi için izlemeyi aç (yalnız başlatılacak/çalışan öğe varsa).</summary>
    public void WatchQueue()
    {
        var items = _items();
        if (!items.Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Failed)) return;
        BeginWatch(items);
    }

    public void StopWatching() { _watching = false; CancelExit(); }

    public void CancelExit()
    {
        if (_exitAt == null) return;
        _exitAt = null;
        ExitCountdownChanged?.Invoke(-1);
    }

    private void BeginWatch(IReadOnlyList<DownloadItem> items)
    {
        _watching = true;
        _completedAtWatch = items.Where(i => i.Status == DownloadStatus.Completed).Select(i => i.Id).ToHashSet();
        CancelExit();
    }

    public void Tick(DateTime now)
    {
        var s = _settings().Scheduler;
        var from = _lastCheck ?? now - StartupGrace;
        if (from > now) from = now;   // saat geri alındı
        _lastCheck = now;

        if (s.Enabled && TimeSpan.TryParse(s.StartTime, System.Globalization.CultureInfo.InvariantCulture, out var t))
        {
            // (from, now] aralığına düşen en son zamanlanmış başlangıç (kaçırılanlar dahil).
            for (var day = now.Date; day >= from.Date; day = day.AddDays(-1))
            {
                var at = day + new TimeSpan(t.Hours, t.Minutes, 0);
                if (!s.Days.Contains((int)day.DayOfWeek) || at > now || at <= from) continue;
                var key = at.ToString("yyyyMMddHHmm");
                if (key != _lastFiredKey)
                {
                    _lastFiredKey = key;
                    var items = _items();
                    if (items.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Queued))
                    {
                        BeginWatch(items);
                        _startAll();
                    }
                }
                break;
            }
        }

        var cur = _items();
        bool busy = cur.Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading);
        if (_exitAt != null)
        {
            if (busy) { CancelExit(); _watching = true; return; }
            int left = (int)Math.Ceiling((_exitAt.Value - now).TotalSeconds);
            if (left <= 0) { _exitAt = null; ExitRequested?.Invoke(); }
            else ExitCountdownChanged?.Invoke(left);
            return;
        }
        if (_watching && !busy)
        {
            _watching = false;
            if (cur.Any(i => i.Status == DownloadStatus.Completed && !_completedAtWatch.Contains(i.Id)))
            {
                switch (s.OnFinished)
                {
                    case "exit":
                        _exitAt = now + ExitCountdown;
                        Notify?.Invoke($"Kuyruk bitti; {(int)ExitCountdown.TotalSeconds} sn içinde çıkılacak (iptal için kuyruğu durdurun).");
                        ExitCountdownChanged?.Invoke((int)ExitCountdown.TotalSeconds);
                        break;
                    case "notify": Notify?.Invoke("Kuyruktaki tüm indirmeler tamamlandı."); break;
                }
            }
        }
    }
}
