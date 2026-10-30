using System.Diagnostics;

namespace TDownloader.Core.Downloads;

/// <summary>Global hız sınırı için token-bucket (borçlanmalı). 0 = sınırsız. Thread-safe.</summary>
public sealed class RateLimiter
{
    private readonly object _l = new();
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();
    private long _rate;

    public RateLimiter(long bytesPerSecond = 0) { _rate = bytesPerSecond; }

    public long BytesPerSecond
    {
        get { lock (_l) return _rate; }
        set { lock (_l) { _rate = Math.Max(0, value); _tokens = 0; _last = Stopwatch.GetTimestamp(); } }
    }

    public async ValueTask AcquireAsync(int bytes, CancellationToken ct)
    {
        double waitSec;
        lock (_l)
        {
            if (_rate <= 0) return;
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _last) / (double)Stopwatch.Frequency;
            _last = now;
            _tokens = Math.Min(_tokens + elapsed * _rate, _rate * 0.25); // en çok 250 ms patlama
            _tokens -= bytes;
            waitSec = _tokens < 0 ? -_tokens / _rate : 0;
        }
        if (waitSec > 0.001) await Task.Delay(TimeSpan.FromSeconds(waitSec), ct).ConfigureAwait(false);
    }
}
