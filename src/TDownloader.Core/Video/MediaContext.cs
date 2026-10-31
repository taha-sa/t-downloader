namespace TDownloader.Core.Video;

/// <summary>
/// Bir akışı tarayıcıdaki gibi istemek için gereken başlıklar. Film siteleri videoyu başka alan adındaki iframe
/// oynatıcıdan verir; CDN çoğu zaman Referer/Origin/Cookie'yi (oynatıcı sayfasına ait) denetler.
/// </summary>
public sealed record MediaContext(string? Referer = null, string? Origin = null, string? Cookies = null, string? UserAgent = null)
{
    public static readonly MediaContext Empty = new();

    public bool IsEmpty => string.IsNullOrWhiteSpace(Referer) && string.IsNullOrWhiteSpace(Origin) &&
                           string.IsNullOrWhiteSpace(Cookies) && string.IsNullOrWhiteSpace(UserAgent);

    /// <summary>Origin verilmediyse Referer'ın origin'i (https://alan.adi).</summary>
    public string? EffectiveOrigin
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Origin)) return Origin;
            return Uri.TryCreate(Referer, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
                ? u.GetLeftPart(UriPartial.Authority) : null;
        }
    }

    /// <summary>İsteğe başlıkları ekler. Cookie yalnızca <paramref name="cookieHost"/> ile aynı sunucuya gider.</summary>
    public void Apply(HttpRequestMessage req, string? cookieHost = null)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", !string.IsNullOrWhiteSpace(UserAgent)
            ? UserAgent : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) T-Downloader/1.0");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (!string.IsNullOrWhiteSpace(Referer) && Uri.TryCreate(Referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
        var o = EffectiveOrigin;
        if (!string.IsNullOrEmpty(o)) req.Headers.TryAddWithoutValidation("Origin", o);
        if (!string.IsNullOrWhiteSpace(Cookies) && (cookieHost == null || string.Equals(cookieHost, req.RequestUri?.Host, StringComparison.OrdinalIgnoreCase)))
            req.Headers.TryAddWithoutValidation("Cookie", Cookies);
    }
}
