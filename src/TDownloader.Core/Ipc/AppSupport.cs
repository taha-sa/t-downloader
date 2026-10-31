using System.Text;

namespace TDownloader.Core.Ipc;

// App (arayüz) tarafının UI'dan bağımsız, test edilebilir yardımcıları. Pencere/WinForms bağımlılığı yoktur.

/// <summary>Kalite önbelleği anahtarı: imza/zaman damgası gibi her istekte değişen sorgu parametreleri atılır.</summary>
public static class QualityCacheKey
{
    private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "tok", "sig", "signature", "sign", "expires", "expire", "exp", "e", "t", "h", "s", "st", "hash", "hmac", "md5",
        "hdnts", "hdntl", "policy", "key-pair-id", "acl", "auth", "verify", "ts", "time", "timestamp", "_", "rnd", "r", "nocache", "cb",
    };

    public static string For(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var kept = new List<string>();
        foreach (var part in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Split('=', 2)[0];
            if (Volatile.Contains(name) || name.StartsWith("X-Amz-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("X-Goog-", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(part);
        }
        kept.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder(u.GetLeftPart(UriPartial.Path));
        if (kept.Count > 0) sb.Append('?').Append(string.Join('&', kept));
        return sb.ToString();
    }

    public static string ForSet(IEnumerable<string> urls) => "set|" + string.Join("|", urls.Select(For));
}

/// <summary>
/// Kilitli, sınırlı (LRU) ve ömürlü görev önbelleği. Aynı anahtar için devam eden iş PAYLAŞILIR; iptal edilebilen bekleyenlerin
/// hepsi vazgeçerse iş iptal edilir (ör. yt-dlp süreci öldürülür). Başarısız/iptal edilmiş görev yeniden başlatılır.
/// </summary>
public sealed class SharedTaskCache<T>
{
    private sealed class Entry
    {
        public required Task<T> Task;
        public required CancellationTokenSource Cts;
        public DateTime At, Used;
        public int Waiters;
        public bool Pinned;      // iptal edilemeyen bekleyen var: asla iptal edilmez
    }

    private readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTime> _now;

    public SharedTaskCache(int capacity, TimeSpan ttl, Func<DateTime>? now = null)
    {
        _capacity = Math.Max(1, capacity); _ttl = ttl; _now = now ?? (() => DateTime.UtcNow);
    }

    public int Count { get { lock (_lock) return _map.Count; } }
    public bool Contains(string key) { lock (_lock) return _map.TryGetValue(key, out var e) && Usable(e, _now()); }

    private bool Usable(Entry e, DateTime now) => now - e.At < _ttl && !e.Task.IsFaulted && !e.Task.IsCanceled;

    public Task<T> GetOrStart(string key, Func<CancellationToken, Task<T>> factory, CancellationToken waiter = default)
    {
        Entry e;
        lock (_lock)
        {
            var now = _now();
            foreach (var k in _map.Where(p => !Usable(p.Value, now)).Select(p => p.Key).ToList()) Drop(k);
            if (!_map.TryGetValue(key, out e!))
            {
                var cts = new CancellationTokenSource();
                var ct = cts.Token;
                e = new Entry { Task = Task.Run(() => factory(ct), CancellationToken.None), Cts = cts, At = now, Used = now };
                _map[key] = e;
                while (_map.Count > _capacity)
                {
                    var oldest = _map.OrderBy(p => p.Value.Used).First().Key;
                    if (oldest == key) break;
                    Drop(oldest);
                }
            }
            e.Used = now;
            e.Waiters++;
            if (!waiter.CanBeCanceled) e.Pinned = true;
        }
        if (!waiter.CanBeCanceled) return e.Task;
        return WaitAsync(key, e, waiter);
    }

    private async Task<T> WaitAsync(string key, Entry e, CancellationToken waiter)
    {
        try { return await e.Task.WaitAsync(waiter).ConfigureAwait(false); }
        catch (OperationCanceledException) when (waiter.IsCancellationRequested && !e.Task.IsCompleted)
        {
            lock (_lock)
            {
                if (--e.Waiters <= 0 && !e.Pinned)
                {
                    if (_map.TryGetValue(key, out var cur) && ReferenceEquals(cur, e)) _map.Remove(key);
                    try { e.Cts.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
            throw;
        }
        catch { lock (_lock) e.Waiters--; throw; }
    }

    /// <summary>Tamamlanmış ve kullanılabilir sonuç var mı (UI'ı bloklamadan anında göstermek için).</summary>
    public bool TryGetCompleted(string key, out T value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var e) && Usable(e, _now()) && e.Task.IsCompletedSuccessfully) { value = e.Task.Result; return true; }
        }
        value = default!;
        return false;
    }

    private void Drop(string key)
    {
        if (_map.Remove(key, out var e) && e.Waiters <= 0 && !e.Task.IsCompleted && !e.Pinned)
        {
            try { e.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}

/// <summary>Eklentiden gelen çerezleri (origin -> başlık) çerezsiz medya öğelerine uygular.</summary>
public static class MediaCookies
{
    public static string? For(Dictionary<string, string>? byOrigin, string url)
    {
        if (byOrigin == null || byOrigin.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var origin = u.GetLeftPart(UriPartial.Authority);
        foreach (var kv in byOrigin)
            if (string.Equals(kv.Key, origin, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    public static List<MediaItem> Apply(List<MediaItem> items, Dictionary<string, string>? byOrigin)
    {
        if (byOrigin == null || byOrigin.Count == 0) return items;
        return items.Select(i => string.IsNullOrEmpty(i.Cookies) && For(byOrigin, i.Url) is { } c ? i with { Cookies = c } : i).ToList();
    }
}

/// <summary>Medya mesajı birleştirme: Append=true -> ekle (yinelenen URL atlanır), değilse değiştir; liste sınırlıdır.</summary>
public static class MediaMerge
{
    public const int MaxItemsPerFrame = 100;

    public static List<MediaItem> Merge(List<MediaItem>? existing, MediaMessage m)
    {
        var incoming = m.Items ?? new();
        if (!m.Append || existing == null || existing.Count == 0)
            return incoming.Count <= MaxItemsPerFrame ? new List<MediaItem>(incoming) : incoming.Take(MaxItemsPerFrame).ToList();
        var seen = new HashSet<string>(existing.Select(i => i.Url), StringComparer.Ordinal);
        var list = new List<MediaItem>(existing);
        foreach (var i in incoming)
            if (list.Count < MaxItemsPerFrame && seen.Add(i.Url)) list.Add(i);
        return list;
    }
}

/// <summary>Sayaçlar artımlı tutulur: durum çubuğu her güncellemede O(1) (liste kopyası/LINQ yok).</summary>
public sealed class DownloadStats
{
    private readonly Dictionary<string, (bool Active, double Speed)> _map = new();
    public int Total => _map.Count;
    public int Active { get; private set; }
    public double Speed { get; private set; }

    public void Set(string id, bool active, double speed)
    {
        if (!active) speed = 0;
        if (_map.TryGetValue(id, out var old)) { if (old.Active) { Active--; Speed -= old.Speed; } }
        _map[id] = (active, speed);
        if (active) { Active++; Speed += speed; }
        if (Active <= 0) { Active = 0; Speed = 0; }
    }

    public void Remove(string id)
    {
        if (!_map.Remove(id, out var old)) return;
        if (old.Active) { Active--; Speed -= old.Speed; }
        if (Active <= 0) { Active = 0; Speed = 0; }
    }

    public bool Contains(string id) => _map.ContainsKey(id);
}

/// <summary>Sınırlı kümelerde en eski kaydı atan yardımcı (tekrar sorulmayan istekler vb.).</summary>
public static class BoundedDictionary
{
    public static void TrimOldest<TKey, TVal>(Dictionary<TKey, TVal> map, int max, Func<TVal, DateTime> stamp) where TKey : notnull
    {
        while (map.Count > max)
        {
            TKey? oldest = default; DateTime best = DateTime.MaxValue; bool any = false;
            foreach (var kv in map) { var t = stamp(kv.Value); if (!any || t < best) { best = t; oldest = kv.Key; any = true; } }
            if (!any) return;
            map.Remove(oldest!);
        }
    }
}

/// <summary>
/// Bloklayabilen bir çağrıyı (ör. panoya erişim) ayrı bir STA iş parçacığında, süre sınırıyla çalıştırır.
/// UI thread'i hiçbir zaman süre sınırından fazla beklemez; süre dolarsa (ok=false) iş parçacığı arka planda sonlanır.
/// </summary>
public static class StaCall
{
    public static bool TryRun<T>(Func<T> func, int timeoutMs, out T? result)
    {
        T? value = default; bool done = false;
        var th = new Thread(() =>
        {
            try { value = func(); done = true; } catch (Exception) { }
        }) { IsBackground = true, Name = "StaCall" };
        if (OperatingSystem.IsWindows()) th.SetApartmentState(ApartmentState.STA);
        th.Start();
        bool joined = th.Join(timeoutMs);
        result = joined && done ? value : default;
        return joined && done;
    }
}
