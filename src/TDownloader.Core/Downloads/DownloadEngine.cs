using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TDownloader.Core.Downloads;

/// <summary>
/// Tek bir indirmeyi yürütür: çok bağlantılı Range indirme, dinamik bölme, duraklat/devam.
/// Yaklaşım: dosya DwnlData\&lt;id&gt;\data.part olarak baştan ön-tahsis edilir, her bağlantı kendi
/// ofsetine yazar (birleştirme yok); bitince SavePath'e taşınır. Parça durumu state.json'da tutulur.
/// </summary>
public sealed class DownloadEngine : IDisposable
{
    private sealed class Segment
    {
        public long Start, End, Position, Reserved;
        public bool Assigned;
    }

    private sealed class Worker
    {
        public int Index;
        public volatile ConnectionState State = ConnectionState.Idle;
        public Segment? Seg;
        public long Bytes;
        public long LastBytes;
        public double Speed;
        public int Retries;
    }

    private readonly DownloadItem _item;
    private readonly string _dir;
    private readonly string _partPath;
    private readonly string _statePath;
    private readonly EngineOptions _opt;
    private readonly RateLimiter _limiter;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _gate = new();

    private List<Segment> _segments = new();
    private List<Worker> _workers = new();
    private SafeFileHandle? _handle;
    private CancellationTokenSource? _cts;
    private Exception? _fatal;
    private int _running;
    private long _total = -1;
    private bool _ranges;
    private string? _etag;
    private int _splitCount;
    private int _retiredWorkers;
    private volatile bool _pauseRequested;
    private HostGate _hostGate = null!;

    public DownloadItem Item => _item;
    /// <summary>Dinamik bölmenin kaç kez gerçekleştiği.</summary>
    public int SplitCount => Volatile.Read(ref _splitCount);
    public bool SupportsRanges => _ranges;

    public event EventHandler<DownloadProgress>? ProgressChanged;
    public event EventHandler<DownloadStatus>? StatusChanged;
    public event EventHandler<SegmentSplitEventArgs>? SegmentSplit;

    /// <param name="dataRoot">DwnlData klasörü; parça verisi dataRoot\&lt;id&gt;\ altında tutulur.</param>
    public DownloadEngine(DownloadItem item, string dataRoot, EngineOptions? options = null,
        RateLimiter? limiter = null, HttpClient? http = null)
    {
        _item = item;
        _opt = options ?? new EngineOptions();
        _limiter = limiter ?? new RateLimiter();
        _dir = Path.Combine(dataRoot, item.Id);
        _partPath = Path.Combine(_dir, "data.part");
        _statePath = Path.Combine(_dir, "state.json");
        _http = http ?? EngineHttp.Shared;
        _ownsHttp = false;
    }

    /// <summary>İndirmeyi duraklatır; RunAsync kısa süre içinde Paused ile döner.</summary>
    public void Pause()
    {
        // RunAsync henüz _cts oluşturmadıysa istek kaybolmasın: bayrak RunAsync başında okunur.
        _pauseRequested = true;
        Interlocked.MemoryBarrier();
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>İndirmeyi çalıştırır. Completed / Paused / Failed döner (istisna fırlatmaz).</summary>
    public async Task<DownloadStatus> RunAsync(CancellationToken external = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("Motor zaten çalışıyor.");
        _fatal = null;
        var cts0 = CancellationTokenSource.CreateLinkedTokenSource(external);
        Interlocked.Exchange(ref _cts, cts0);   // tam bariyer: Pause() ile Dekker tarzı yarış kapanır
        if (_pauseRequested) { try { cts0.Cancel(); } catch (ObjectDisposedException) { } }
        var ct = cts0.Token;
        using var progressStop = new CancellationTokenSource();
        Task? progressTask = null;
        try
        {
            _item.LastTryAt = DateTime.Now;
            _item.Error = null;
            SetStatus(DownloadStatus.Downloading);
            Directory.CreateDirectory(_dir);

            await PrepareAsync(ct).ConfigureAwait(false);

            if (_total == 0)
            {
                await File.WriteAllBytesAsync(_partPath, Array.Empty<byte>(), ct).ConfigureAwait(false);
            }
            else
            {
                _handle = File.OpenHandle(_partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.Asynchronous);
                int n = _ranges && _total > 0 ? _opt.Connections : 1;
                n = Math.Clamp(n, 1, 32);
                _workers = Enumerable.Range(0, n).Select(i => new Worker { Index = i }).ToList();
                progressTask = ProgressLoopAsync(progressStop.Token);
                await Task.WhenAll(_workers.Select(w => WorkerAsync(w, ct))).ConfigureAwait(false);
            }

            progressStop.Cancel();
            if (progressTask != null) await progressTask.ConfigureAwait(false);

            if (_fatal != null) return Fail(_fatal);
            if (ct.IsCancellationRequested && !(_total > 0 && SumDownloaded() >= _total))
            {
                SaveState();
                PublishProgress(0);
                SetStatus(DownloadStatus.Paused);
                return DownloadStatus.Paused;
            }

            return Complete();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            progressStop.Cancel();
            if (progressTask != null) { try { await progressTask.ConfigureAwait(false); } catch { } }
            SaveState();
            SetStatus(DownloadStatus.Paused);
            return DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            progressStop.Cancel();
            if (progressTask != null) { try { await progressTask.ConfigureAwait(false); } catch { } }
            SaveState();
            return Fail(ex);
        }
        finally
        {
            _handle?.Dispose();
            _handle = null;
            _item.SpeedBytesPerSec = 0;
            var c = _cts; _cts = null; c?.Dispose();
            _pauseRequested = false;
            Volatile.Write(ref _running, 0);
        }
    }

    // ------------------------------------------------------------------ hazırlık

    private async Task PrepareAsync(CancellationToken ct)
    {
        _opt.Connections = Math.Clamp(_opt.Connections, 1, 32);
        _hostGate = HostGate.For(HostKey(_item.Url), Math.Clamp(_opt.MaxConnectionsPerHost, 1, EngineHttp.PoolLimit));
        var (total, ranges, etag) = await ProbeAsync(ct).ConfigureAwait(false);
        _total = total; _ranges = ranges; _etag = etag;
        if (_item.TotalSize != total) _item.TotalSize = total;

        var state = TryLoadState();
        bool resume = state != null && ranges && total > 0
            && state.Url == _item.Url && state.TotalSize == total && state.AcceptRanges
            && (state.ETag == null || etag == null || state.ETag == etag)
            && state.Segments.Count > 0
            && File.Exists(_partPath) && new FileInfo(_partPath).Length == total;

        if (resume)
        {
            _segments = state!.Segments.Select(s => new Segment
            {
                Start = s.Start, End = s.End, Position = s.Position, Reserved = s.Position
            }).ToList();
        }
        else
        {
            // Temiz başlangıç.
            try { if (File.Exists(_partPath)) File.Delete(_partPath); } catch { }
            try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { }
            _segments = new List<Segment>();
            if (ranges && total > 0)
            {
                long min = Math.Max(1, _opt.MinSplitSize);
                int n = (int)Math.Clamp(total / min, 1, _opt.Connections);
                long size = total / n;
                for (int i = 0; i < n; i++)
                {
                    long s = i * size, e = i == n - 1 ? total : s + size;
                    _segments.Add(new Segment { Start = s, End = e, Position = s, Reserved = s });
                }
            }
            else
            {
                _segments.Add(new Segment { Start = 0, End = total > 0 ? total : long.MaxValue });
            }
            if (total > 0)
            {
                using var fs = new FileStream(_partPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                fs.SetLength(total);
            }
            else
            {
                using var fs = new FileStream(_partPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            }
            SaveState();
        }
        SyncItem();
    }

    private static string HostKey(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : url;

    private static bool IsOverloadStatus(Exception ex) =>
        ex is HttpStatusException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable };

    private HttpRequestMessage BuildRequest()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _item.Url);
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(_item.UserAgent)
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) T-Downloader/1.0" : _item.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(_item.Referrer) && Uri.TryCreate(_item.Referrer, UriKind.Absolute, out var r))
            req.Headers.Referrer = r;
        if (!string.IsNullOrWhiteSpace(_item.Cookie))
            req.Headers.TryAddWithoutValidation("Cookie", _item.Cookie);
        return req;
    }

    private async Task<(long total, bool ranges, string? etag)> ProbeAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var req = BuildRequest();
                req.Headers.Range = new RangeHeaderValue(0, 0);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_opt.ReadTimeout);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                string? etag = resp.Headers.ETag?.Tag;
                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    var cr = resp.Content.Headers.ContentRange;
                    if (cr?.Length is long len) return (len, true, etag);
                    // toplam boyut bilinmiyor: tek bağlantıya düş
                    return (-1, false, etag);
                }
                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    return (0, true, etag); // boş dosya
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    long? cl = resp.Content.Headers.ContentLength;
                    return (cl ?? -1, false, etag);
                }
                throw HttpStatusException.From(resp);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsRetryable(ex))
            {
                if (IsOverloadStatus(ex)) _hostGate.Throttle();
                if (++attempt > _opt.MaxRetries) throw;
                await Task.Delay(Backoff(attempt, ex), ct).ConfigureAwait(false);
            }
        }
    }

    // ------------------------------------------------------------------ işçiler

    private async Task WorkerAsync(Worker w, CancellationToken ct)
    {
        // İşçi başına tek ArrayPool tamponu: okuma ve (asenkron) disk yazımı bunu kullanır.
        var buf = ArrayPool<byte>.Shared.Rent(_opt.BufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Segment? seg = TakeSegment(w);
                if (seg == null)
                {
                    // Tüm parçalar bitmediyse bekle: başka bir işçi emekli olursa parçasını devralabilmeliyiz.
                    if (!HasUnfinishedSegment()) break;
                    w.State = ConnectionState.Idle;
                    await Task.Delay(25, ct).ConfigureAwait(false);
                    continue;
                }
                w.Seg = seg;
                try
                {
                    await DownloadSegmentAsync(w, seg, buf, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsRetryable(ex))
                {
                    // Bağlantı hatası bütçesi doldu: yalnızca BU işçi emekli olur, parçası kuyruğa döner.
                    RetireWorker(w, seg, ex);
                    return;
                }
            }
            w.State = ct.IsCancellationRequested ? ConnectionState.Idle : ConnectionState.Done;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            w.State = ConnectionState.Idle;
        }
        catch (Exception ex)
        {
            // Kalıcı hata (401/403/404/410, Range desteklenmiyor, disk hatası...): tüm indirme başarısız.
            w.State = ConnectionState.Failed;
            Interlocked.CompareExchange(ref _fatal, ex, null);
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private void RetireWorker(Worker w, Segment seg, Exception ex)
    {
        lock (_gate) { seg.Assigned = false; seg.Reserved = seg.Position; }
        w.State = ConnectionState.Failed;
        if (Interlocked.Increment(ref _retiredWorkers) >= _workers.Count)
        {
            // Hiç işçi kalmadı: hepsi art arda başarısız oldu.
            Interlocked.CompareExchange(ref _fatal, ex, null);
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private bool HasUnfinishedSegment()
    {
        lock (_gate)
        {
            foreach (var s in _segments) if (s.Position < s.End) return true;
            return false;
        }
    }

    private Segment? TakeSegment(Worker w)
    {
        SegmentSplitEventArgs? ev = null;
        Segment? result = null;
        lock (_gate)
        {
            foreach (var s in _segments)
            {
                if (!s.Assigned && s.Position < s.End) { s.Assigned = true; result = s; break; }
            }
            if (result == null && _ranges && _total > 0)
            {
                Segment? best = null; long bestRem = 0;
                foreach (var s in _segments)
                {
                    long rem = s.End - s.Reserved;
                    if (s.Assigned && rem > bestRem) { best = s; bestRem = rem; }
                }
                long min = Math.Max(1, _opt.MinSplitSize);
                if (best != null && bestRem >= 2 * min)
                {
                    long mid = best.Reserved + bestRem / 2;
                    var ns = new Segment { Start = mid, End = best.End, Position = mid, Reserved = mid, Assigned = true };
                    ev = new SegmentSplitEventArgs { SplitAt = mid, SegmentEnd = best.End, ConnectionIndex = w.Index };
                    best.End = mid;
                    _segments.Add(ns);
                    _segments.Sort((a, b) => a.Start.CompareTo(b.Start));
                    result = ns;
                    Interlocked.Increment(ref _splitCount);
                }
            }
        }
        if (ev != null) SegmentSplit?.Invoke(this, ev);
        return result;
    }

    private async Task DownloadSegmentAsync(Worker w, Segment seg, byte[] buf, CancellationToken ct)
    {
        int attempt = 0;
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long startPos, endEx;
                lock (_gate)
                {
                    if (seg.Position >= seg.End) return;
                    startPos = seg.Position; endEx = seg.End;
                }
                try
                {
                    w.State = ConnectionState.Connecting;
                    // Aynı host için bağlantı tavanı: sıra beklemek zaman aşımına sayılmaz (cts aşağıda kurulur).
                    using var lease = await _hostGate.AcquireAsync(ct).ConfigureAwait(false);
                    using var req = BuildRequest();
                    if (_ranges)
                        req.Headers.Range = new RangeHeaderValue(startPos, endEx == long.MaxValue ? null : endEx - 1);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(_opt.ReadTimeout);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw HttpStatusException.From(resp);
                    if (_ranges && resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new NonRetryableException("Sunucu Range isteğine parçalı yanıt vermedi.");

                    w.State = ConnectionState.Receiving;
                    await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                    while (true)
                    {
                        long remaining;
                        lock (_gate) remaining = seg.End - seg.Reserved;
                        if (remaining <= 0) return; // parça (bölme sonrası daraldıysa da) tamam
                        cts.CancelAfter(_opt.ReadTimeout);
                        int want = (int)Math.Min(buf.Length, remaining);
                        int n = await stream.ReadAsync(buf.AsMemory(0, want), cts.Token).ConfigureAwait(false);
                        if (n == 0)
                        {
                            if (_total < 0)
                            {
                                // boyutu bilinmeyen akış burada bitti
                                lock (_gate) { seg.End = seg.Position; _total = seg.Position; }
                                return;
                            }
                            throw new IOException("Bağlantı beklenenden erken kapandı.");
                        }
                        // Hız sınırı beklemesi okuma zaman aşımına sayılmasın: zamanlayıcı yalnızca soket okumasını ölçer.
                        cts.CancelAfter(Timeout.InfiniteTimeSpan);
                        await _limiter.AcquireAsync(n, ct).ConfigureAwait(false);
                        long off; int allow;
                        lock (_gate)
                        {
                            allow = (int)Math.Min(n, seg.End - seg.Reserved);
                            if (allow <= 0) return;
                            off = seg.Reserved;
                            seg.Reserved += allow;
                        }
                        await RandomAccess.WriteAsync(_handle!, buf.AsMemory(0, allow), off, ct).ConfigureAwait(false);
                        lock (_gate) seg.Position += allow;
                        Interlocked.Add(ref w.Bytes, allow);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsRetryable(ex))
                {
                    if (IsOverloadStatus(ex)) _hostGate.Throttle();
                    long before = startPos;
                    lock (_gate)
                    {
                        if (!_ranges) { seg.Position = seg.Start; }
                        seg.Reserved = seg.Position;
                        if (seg.Position > before) attempt = 0;
                    }
                    if (++attempt > _opt.MaxRetries) throw;
                    w.State = ConnectionState.Retrying;
                    w.Retries++;
                    await Task.Delay(Backoff(attempt, ex), ct).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool IsRetryable(Exception ex)
    {
        if (ex is NonRetryableException) return false;
        if (ex is HttpRequestException { StatusCode: { } sc })
        {
            int c = (int)sc;
            if (c >= 400 && c < 500 && c != 408 && c != 429) return false;
        }
        return ex is HttpRequestException or IOException or OperationCanceledException or TimeoutException
            or System.Net.Sockets.SocketException;
    }

    /// <summary>Üstel bekleme + jitter; 429/503'te sunucunun Retry-After süresi alt sınır olur.</summary>
    private TimeSpan Backoff(int attempt, Exception? ex = null)
    {
        var ms = _opt.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 5));
        ms = Math.Min(ms, 30_000);
        ms *= 1.0 + Random.Shared.NextDouble() * 0.5;
        if (ex is HttpStatusException { RetryAfter: { } ra })
            ms = Math.Max(ms, Math.Min(ra.TotalMilliseconds, HttpStatusException.MaxRetryAfter.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(ms);
    }

    // ------------------------------------------------------------------ ilerleme / durum

    private async Task ProgressLoopAsync(CancellationToken stop)
    {
        var sw = Stopwatch.StartNew();
        long lastSave = 0;
        double lastT = 0;
        long lastTotal = SumDownloaded();
        double speed = 0;
        try
        {
            using var timer = new PeriodicTimer(_opt.ProgressInterval);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                double t = sw.Elapsed.TotalSeconds, dt = Math.Max(0.001, t - lastT);
                lastT = t;
                long cur = SumDownloaded();
                double inst = (cur - lastTotal) / dt;
                lastTotal = cur;
                speed = speed <= 0 ? inst : speed * 0.6 + inst * 0.4;
                foreach (var w in _workers)
                {
                    long b = Interlocked.Read(ref w.Bytes);
                    double wi = (b - w.LastBytes) / dt;
                    w.LastBytes = b;
                    w.Speed = w.Speed <= 0 ? wi : w.Speed * 0.6 + wi * 0.4;
                }
                if (sw.ElapsedMilliseconds - lastSave >= _opt.StateSaveInterval.TotalMilliseconds)
                {
                    lastSave = sw.ElapsedMilliseconds;
                    SaveState();
                }
                PublishProgress(speed);
            }
        }
        catch (OperationCanceledException) { }
    }

    private long SumDownloaded()
    {
        lock (_gate)
        {
            long sum = 0;
            foreach (var s in _segments) sum += s.Position - s.Start;
            return sum;
        }
    }

    private void SyncItem()
    {
        _item.Downloaded = SumDownloaded();
        if (_total >= 0) _item.TotalSize = _total;
    }

    private void PublishProgress(double speed)
    {
        SyncItem();
        _item.SpeedBytesPerSec = speed;
        var conns = _workers.Select(w =>
        {
            var s = w.Seg;
            long st = 0, en = 0, pos = 0;
            if (s != null) lock (_gate) { st = s.Start; en = s.End; pos = s.Position; }
            return new ConnectionProgress
            {
                Index = w.Index, State = w.State, SegmentStart = st, SegmentEnd = en, Position = pos,
                SpeedBytesPerSec = w.State == ConnectionState.Receiving ? w.Speed : 0, Retries = w.Retries
            };
        }).ToList();
        TimeSpan? eta = null;
        if (speed > 1 && _total > 0) eta = TimeSpan.FromSeconds(Math.Max(0, (_total - _item.Downloaded) / speed));
        ProgressChanged?.Invoke(this, new DownloadProgress
        {
            Id = _item.Id, Downloaded = _item.Downloaded, TotalSize = _total, SpeedBytesPerSec = speed,
            Eta = eta, Connections = conns
        });
    }

    private void SetStatus(DownloadStatus s)
    {
        _item.Status = s;
        StatusChanged?.Invoke(this, s);
    }

    private DownloadStatus Fail(Exception ex)
    {
        _item.Error = ex.Message;
        SetStatus(DownloadStatus.Failed);
        return DownloadStatus.Failed;
    }

    private DownloadStatus Complete()
    {
        long expected = _total;
        long got = SumDownloaded();
        if (expected > 0 && got != expected)
            return Fail(new IOException($"Eksik veri: {got}/{expected}"));

        _handle?.Dispose(); _handle = null;
        if (expected < 0)
        {
            _total = got; _item.TotalSize = got;
            using var fs = new FileStream(_partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.SetLength(got);
        }
        try
        {
            var dir = Path.GetDirectoryName(_item.SavePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Move(_partPath, _item.SavePath, overwrite: true);
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
        _item.Downloaded = _item.TotalSize = _total;
        _item.SpeedBytesPerSec = 0;
        _item.Error = null;
        PublishProgress(0);
        SetStatus(DownloadStatus.Completed);
        return DownloadStatus.Completed;
    }

    // ------------------------------------------------------------------ kalıcılık

    private void SaveState()
    {
        try
        {
            EngineState st;
            lock (_gate)
            {
                st = new EngineState
                {
                    Url = _item.Url, TotalSize = _total, AcceptRanges = _ranges, ETag = _etag,
                    Segments = _segments.Select(s => new SegmentState { Start = s.Start, End = s.End, Position = s.Position }).ToList()
                };
            }
            JsonDefaults.WriteAtomic(_statePath, JsonSerializer.Serialize(st, JsonDefaults.Options));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private EngineState? TryLoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            return JsonSerializer.Deserialize<EngineState>(File.ReadAllText(_statePath), JsonDefaults.Options);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        Pause();
        if (_ownsHttp) _http.Dispose();
    }
}
