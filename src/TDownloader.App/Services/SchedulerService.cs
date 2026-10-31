using TDownloader.Core.Downloads;
using TDownloader.Core.Settings;

namespace TDownloader.App.Services;

/// <summary>Basit zamanlayıcı: belirlenen gün/saatte kuyruğu başlatır, bitince bildirir/uygulamadan çıkar.</summary>
public sealed class SchedulerService : IDisposable
{
    private readonly AppServices _svc;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };
    private string _lastFiredKey = "";
    private bool _watching;

    public event Action<string>? Notify;
    public event Action? ExitRequested;

    public SchedulerService(AppServices svc)
    {
        _svc = svc;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Elle kuyruk başlatıldığında "bitince" eylemi için izlemeyi aç.</summary>
    public void WatchQueue() => _watching = true;
    public void StopWatching() => _watching = false;

    public void Tick(DateTime? nowOverride = null)
    {
        var s = _svc.Settings.Scheduler;
        var now = nowOverride ?? DateTime.Now;
        if (s.Enabled && TimeSpan.TryParse(s.StartTime, System.Globalization.CultureInfo.InvariantCulture, out var t) && s.Days.Contains((int)now.DayOfWeek)
            && now.Hour == t.Hours && now.Minute == t.Minutes)
        {
            var key = now.ToString("yyyyMMddHHmm");
            if (key != _lastFiredKey)
            {
                _lastFiredKey = key;
                _watching = true;
                _svc.Manager.StartAll();
            }
        }
        if (_watching)
        {
            var items = _svc.Manager.Items;
            bool busy = items.Any(i => i.Status is DownloadStatus.Queued or DownloadStatus.Downloading);
            if (!busy)
            {
                _watching = false;
                if (items.Any(i => i.Status == DownloadStatus.Completed))
                {
                    switch (s.OnFinished)
                    {
                        case "exit": ExitRequested?.Invoke(); break;
                        case "notify": Notify?.Invoke("Kuyruktaki tüm indirmeler tamamlandı."); break;
                    }
                }
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
