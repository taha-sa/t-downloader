using TDownloader.Core.Localization;
using System.Net;

namespace TDownloader.Core.Downloads;

public sealed class EngineOptions
{
    /// <summary>Bağlantı sayısı (1–32).</summary>
    public int Connections { get; set; } = 8;
    /// <summary>Dinamik bölmede en küçük parça (varsayılan 1 MB).</summary>
    public long MinSplitSize { get; set; } = 1024 * 1024;
    /// <summary>Bağlantı başına art arda hata sonrası vazgeçme sayısı (ilerleme olunca sayaç sıfırlanır).</summary>
    public int MaxRetries { get; set; } = 8;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1.5);
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan StateSaveInterval { get; set; } = TimeSpan.FromSeconds(1);
    public int BufferSize { get; set; } = 64 * 1024;
    /// <summary>Aynı host (host:port) için tüm indirmelerin toplam etkin bağlantı tavanı (varsayılan 32, en çok 64).</summary>
    public int MaxConnectionsPerHost { get; set; } = 32;
}

public enum ConnectionState { Idle, Connecting, Receiving, Retrying, Done, Failed }

public sealed class ConnectionProgress
{
    public int Index { get; init; }
    public ConnectionState State { get; init; }
    /// <summary>Bağlantının şu an üstlendiği parça [SegmentStart, SegmentEnd) ve içindeki konum.</summary>
    public long SegmentStart { get; init; }
    public long SegmentEnd { get; init; }
    public long Position { get; init; }
    public double SpeedBytesPerSec { get; init; }
    public int Retries { get; init; }
}

public sealed class DownloadProgress
{
    public string Id { get; init; } = "";
    public long Downloaded { get; init; }
    /// <summary>Bilinmiyorsa -1.</summary>
    public long TotalSize { get; init; }
    public double SpeedBytesPerSec { get; init; }
    public TimeSpan? Eta { get; init; }
    public IReadOnlyList<ConnectionProgress> Connections { get; init; } = Array.Empty<ConnectionProgress>();
    /// <summary>Harici (HLS) indirmede indirme sonrası aşama ("Birleştiriliyor %40"); yoksa null.</summary>
    public string? Phase { get; init; }
}

public sealed class SegmentSplitEventArgs : EventArgs
{
    public long SplitAt { get; init; }
    public long SegmentEnd { get; init; }
    public int ConnectionIndex { get; init; }
}

internal sealed class NonRetryableException : Exception
{
    public NonRetryableException(string m) : base(m) { }
}

/// <summary>HTTP durum kodlu hata; 429/503 için sunucunun Retry-After süresini taşır.</summary>
internal sealed class HttpStatusException : HttpRequestException
{
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(2);
    public TimeSpan? RetryAfter { get; }

    private HttpStatusException(string message, HttpStatusCode code, TimeSpan? retryAfter) : base(message, null, code)
    {
        RetryAfter = retryAfter;
    }

    public static HttpStatusException From(HttpResponseMessage resp)
    {
        TimeSpan? ra = null;
        var h = resp.Headers.RetryAfter;
        if (h?.Delta is { } d) ra = d;
        else if (h?.Date is { } dt) ra = dt - DateTimeOffset.UtcNow;
        if (ra is { } r && r < TimeSpan.Zero) ra = TimeSpan.Zero;
        return new HttpStatusException($"HTTP {(int)resp.StatusCode}", resp.StatusCode, ra);
    }
}

internal sealed class SegmentState { public long Start { get; set; } public long End { get; set; } public long Position { get; set; } }

internal sealed class EngineState
{
    public string Url { get; set; } = "";
    public long TotalSize { get; set; }
    public bool AcceptRanges { get; set; }
    public string? ETag { get; set; }
    public List<SegmentState> Segments { get; set; } = new();
}

public static class EngineHttp
{
    private static readonly Lazy<HttpClient> _shared = new(Create);
    public static HttpClient Shared => _shared.Value;

    /// <summary>HttpClient bağlantı havuzu üst sınırı (MaxConnectionsPerServer); host tavanı bunu aşamaz.</summary>
    public const int PoolLimit = 64;

    /// <summary>
    /// İsteği gönderir ve yönlendirmeleri elle izler (en çok 10). Her adım için <paramref name="build"/> hedef adresle yeni istek kurar,
    /// böylece çerez/Referer gibi başlıklar host değişince çağıranın kuralıyla eklenir ya da atılır.
    /// </summary>
    public static async Task<HttpResponseMessage> SendFollowingAsync(HttpClient http, Uri start, Func<Uri, HttpRequestMessage> build,
        HttpCompletionOption completion, CancellationToken ct, int maxRedirects = 10)
    {
        var uri = start;
        for (int hop = 0; ; hop++)
        {
            using var req = build(uri);
            var resp = await http.SendAsync(req, completion, ct).ConfigureAwait(false);
            if (resp.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) || resp.Headers.Location is not { } loc)
                return resp;
            resp.Dispose();
            if (hop >= maxRedirects) throw new HttpRequestException(Strings.TooManyRedirectsCheckTheURLAndTry);
            var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
            if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException(Strings.UnsupportedRedirectURLUseAnHTTPOrHTTPS);
            uri = next;
        }
    }

    public static HttpClient Create()
    {
        var h = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            // Yönlendirmeyi biz izleriz (SendFollowingAsync): .NET özel Cookie başlığını başka host'a da taşırdı (C9).
            AllowAutoRedirect = false,
            // Tarayıcılar gibi başlık değerleri UTF-8 gönderilir: ASCII dışı çerez/UA tüm indirmeyi düşürmesin (C11).
            RequestHeaderEncodingSelector = (_, _) => System.Text.Encoding.UTF8,
            MaxConnectionsPerServer = PoolLimit,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        return new HttpClient(h, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
