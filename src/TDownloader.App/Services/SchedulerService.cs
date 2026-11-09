using TDownloader.Core.Queue;

namespace TDownloader.App.Services;

/// <summary>Zamanlayıcı: mantık Core.SchedulerCore'da; burası yalnız zamanlayıcıyı sürer.</summary>
public sealed class SchedulerService : IDisposable
{
    private readonly SchedulerCore _core;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public event Action<string>? Notify { add => _core.Notify += value; remove => _core.Notify -= value; }
    public event Action? ExitRequested { add => _core.ExitRequested += value; remove => _core.ExitRequested -= value; }
    public event Action<int>? ExitCountdownChanged { add => _core.ExitCountdownChanged += value; remove => _core.ExitCountdownChanged -= value; }

    public SchedulerService(AppServices svc)
    {
        _core = new SchedulerCore(() => svc.Settings, () => svc.Manager.Items, () => svc.Manager.StartAll());
        _timer.Tick += (_, _) => _core.Tick(DateTime.Now);
        _timer.Start();
    }

    public void WatchQueue() => _core.WatchQueue();
    public void StopWatching() => _core.StopWatching();
    public void CancelExit() => _core.CancelExit();

    public void Dispose() => _timer.Dispose();
}
