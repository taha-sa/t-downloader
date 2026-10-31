using TDownloader.Core.Downloads;

namespace TDownloader.Core.Queue;

/// <param name="Phase">İndirme bittikten sonraki aşama ("Birleştiriliyor %40", "Dönüştürülüyor %10"); indirme sürerken null.</param>
public sealed record ExternalProgress(long Downloaded, long Total, double SpeedBytesPerSec, double? EtaSeconds, string? Phase = null);

public sealed record ExternalResult(bool Success, string? Error, string? FinalPath);

/// <summary>Harici süreçle (yt-dlp) indirmeyi yürüten bileşen. İptal: ct iptal edilirse OperationCanceledException.</summary>
public interface IExternalRunner
{
    Task<ExternalResult> RunAsync(DownloadItem item, Action<ExternalProgress> report, CancellationToken ct);
}

public sealed record AddExternalRequest(
    string Url,
    string FileName,
    string Format,
    string? SaveFolder = null,
    string? Referrer = null,
    string? Description = null,
    bool StartNow = true,
    string? Cookie = null,
    string? UserAgent = null,
    string? Origin = null);
