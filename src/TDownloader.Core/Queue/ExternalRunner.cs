using TDownloader.Core.Downloads;

namespace TDownloader.Core.Queue;

public sealed record ExternalProgress(long Downloaded, long Total, double SpeedBytesPerSec, double? EtaSeconds);

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
    bool StartNow = true);
