namespace TDownloader.Core.Util;

/// <summary>
/// Art arda gelen Trigger() çağrılarını tek bir eyleme indirger: ilk çağrıdan <c>delay</c> sonra, thread-pool'da (UI dışında) bir kez çalışır.
/// Flush() bekleyen eylemi hemen (çağıranın thread'inde) çalıştırır (çıkışta kaydetmek için).
/// </summary>
public sealed class Debouncer
{
    private readonly TimeSpan _delay;
    private readonly Action _action;
    private readonly object _lock = new();
    private bool _pending;

    public Debouncer(TimeSpan delay, Action action) { _delay = delay; _action = action; }

    public bool IsPending { get { lock (_lock) return _pending; } }

    public void Trigger()
    {
        lock (_lock)
        {
            if (_pending) return;
            _pending = true;
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(_delay).ConfigureAwait(false);
            Run();
        });
    }

    public void Flush() => Run();

    private void Run()
    {
        lock (_lock)
        {
            if (!_pending) return;
            _pending = false;
        }
        try { _action(); } catch (Exception) { /* kaydetme hatası uygulamayı düşürmez */ }
    }
}
