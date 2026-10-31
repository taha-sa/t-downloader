using System.Collections.Concurrent;

namespace TDownloader.Core.Downloads;

/// <summary>
/// Aynı sunucu (host:port) için süreç genelinde etkin bağlantı tavanı. Tavanı aşan istekler zaman aşımı
/// işlemeden FIFO sırayla bekler. 429/503 gelince tavan geçici olarak yarıya iner, bir süre sonra eski haline döner.
/// </summary>
internal sealed class HostGate
{
    /// <summary>Azaltılmış tavanın eski haline dönme süresi.</summary>
    public static TimeSpan RestoreAfter { get; set; } = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ThrottleDedupe = TimeSpan.FromSeconds(3);
    private static readonly ConcurrentDictionary<string, HostGate> Gates = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _l = new();
    private readonly LinkedList<TaskCompletionSource> _waiters = new();
    /// <summary>Yalnızca test: bu örnek için geri yükleme süresi (statik <see cref="RestoreAfter"/> paylaşımını önler).</summary>
    internal TimeSpan? RestoreAfterOverride { get; set; }
    private int _max, _limit, _active;
    private DateTime _restoreAt = DateTime.MinValue, _lastThrottle = DateTime.MinValue;

    public HostGate(int max) { _max = _limit = Math.Max(1, max); }

    public static HostGate For(string authority, int max)
    {
        var g = Gates.GetOrAdd(authority, _ => new HostGate(max));
        g.Configure(max);
        return g;
    }

    public static HostGate? Peek(string authority) => Gates.TryGetValue(authority, out var g) ? g : null;

    public int Limit { get { lock (_l) { RestoreIfDue(); return _limit; } } }
    public int Active { get { lock (_l) return _active; } }

    public void Configure(int max)
    {
        max = Math.Max(1, max);
        lock (_l)
        {
            bool throttled = _limit < _max;
            _max = max;
            _limit = throttled ? Math.Min(_limit, max) : max;
            Pump();
        }
    }

    /// <summary>Sunucu 429/503 verdi: tavanı yarıya indir (en az 1). Art arda gelen çağrılar tek sayılır.</summary>
    public void Throttle()
    {
        lock (_l)
        {
            var now = DateTime.UtcNow;
            if (now - _lastThrottle < ThrottleDedupe) return;
            _lastThrottle = now;
            _limit = Math.Max(1, _limit / 2);
            _restoreAt = now + (RestoreAfterOverride ?? RestoreAfter);
        }
    }

    // _l altında çağrılır
    private void RestoreIfDue()
    {
        if (_limit < _max && DateTime.UtcNow >= _restoreAt) { _limit = _max; Pump(); }
    }

    public async ValueTask<Lease> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskCompletionSource tcs;
        LinkedListNode<TaskCompletionSource> node;
        lock (_l)
        {
            RestoreIfDue();
            if (_active < _limit) { _active++; return new Lease(this); }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(tcs);
        }
        using var reg = ct.Register(() =>
        {
            bool removed;
            lock (_l) { removed = node.List != null; if (removed) _waiters.Remove(node); }
            if (removed) tcs.TrySetCanceled(ct);
        });
        await tcs.Task.ConfigureAwait(false);
        return new Lease(this);
    }

    private void Release()
    {
        lock (_l) { _active--; RestoreIfDue(); Pump(); }
    }

    // _l altında çağrılır
    private void Pump()
    {
        while (_waiters.Count > 0 && _active < _limit)
        {
            var n = _waiters.First!; _waiters.RemoveFirst();
            _active++;
            if (!n.Value.TrySetResult()) _active--;   // bu arada iptal edilmiş
        }
    }

    public readonly struct Lease : IDisposable
    {
        private readonly HostGate? _g;
        internal Lease(HostGate g) { _g = g; }
        public void Dispose() => _g?.Release();
    }
}
