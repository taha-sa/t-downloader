namespace TDownloader.Core.Ipc;

/// <summary>
/// Tarayıcıdan gelen `download` isteğinin IDM tarzı akışı: yakalama kuralları uygunsa HEMEN `accepted:true` döner,
/// onay penceresi arka planda (sırayla) gösterilir. Böylece pencere açıkken eklentinin zaman aşımı (çift indirme)
/// oluşmaz. Pencerede "İptal" = hiçbir şey inmez (tarayıcıya geri bırakma yok). UI bağımlılığı yoktur.
/// </summary>
public sealed class DownloadCaptureGate
{
    private readonly Func<DownloadMessage, bool> _enabled;
    private readonly Func<DownloadMessage, Task> _showPrompt;
    private readonly Action<string>? _log;
    private int _pending;

    /// <summary>Aynı anda bekleyen onay penceresi üst sınırı; aşılırsa yeni istekler reddedilir (tarayıcı kendi indirir).</summary>
    public int MaxPending { get; init; } = 500;

    /// <param name="enabled">Yakalama bu istek için açık mı (kapalıysa accepted:false).</param>
    /// <param name="showPrompt">Onay penceresi; pencere kapanınca tamamlanan görev. Sıralamayı (aynı anda tek pencere) çağıran sağlar.</param>
    public DownloadCaptureGate(Func<DownloadMessage, bool> enabled, Func<DownloadMessage, Task> showPrompt, Action<string>? log = null)
    {
        _enabled = enabled; _showPrompt = showPrompt; _log = log;
    }

    public int Pending => Volatile.Read(ref _pending);

    public static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Anında karar verir; kabulde pencereyi arka planda başlatır. Asla bloklamaz.</summary>
    /// <param name="completed">İsteğe bağlı: pencere akışı bitince çağrılır. true = akış hatasız bitti (eklendi ya da kullanıcı iptal etti),
    /// false = pencere/ekleme hatası (istek KAYBOLMAMALI; çağıran dosyayı saklar ve bildirir). Reddedilen isteklerde çağrılmaz.</param>
    public DownloadResultMessage Handle(DownloadMessage d, Action<bool>? completed = null)
    {
        bool ok;
        try { ok = IsHttp(d.Url) && _enabled(d); }
        catch (Exception) { ok = false; }
        if (!ok) return new DownloadResultMessage(d.RequestId, false);

        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            _log?.Invoke("Bekleyen onay penceresi sınırı aşıldı; istek reddedildi.");
            return new DownloadResultMessage(d.RequestId, false);
        }
        _ = Task.Run(async () =>
        {
            bool ok = true;
            try { await _showPrompt(d).ConfigureAwait(false); }
            catch (Exception ex) { ok = false; _log?.Invoke("Onay penceresi hatası: " + ex.Message); }
            finally { Interlocked.Decrement(ref _pending); }
            try { completed?.Invoke(ok); } catch (Exception ex) { _log?.Invoke("Tamamlanma geri çağrısı hatası: " + ex.Message); }
        });
        return new DownloadResultMessage(d.RequestId, true);
    }
}
