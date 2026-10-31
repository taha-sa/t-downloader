using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;

namespace TDownloader.Core.Video;

/// <summary>
/// Video indirme yönlendiricisi: HLS kalite biçimi ("hls\t...") yt-dlp'siz, doğrudan HLS indiriciyle çalışır;
/// başarısız olursa (DRM hariç) ve yt-dlp varsa yt-dlp'ye düşer. Diğer her şey yt-dlp'ye gider.
/// </summary>
public sealed class VideoRunner : IExternalRunner
{
    private readonly YtDlpClient _yt;
    private readonly HlsClient _hls;

    public VideoRunner(YtDlpClient yt, HlsClient? hls = null) { _yt = yt; _hls = hls ?? new HlsClient(); }

    public static MediaContext ContextOf(DownloadItem item) => new(item.Referrer, item.Origin, item.Cookie, item.UserAgent);

    public async Task<ExternalResult> RunAsync(DownloadItem item, Action<ExternalProgress> report, CancellationToken ct)
    {
        var f = HlsClient.ParseFormat(item.ExternalFormat);
        if (f == null) return await _yt.RunAsync(item, report, ct).ConfigureAwait(false);

        var (variant, audio, height) = f.Value;
        var dir = Path.GetDirectoryName(item.SavePath)!;
        Directory.CreateDirectory(dir);
        string outBase = Path.Combine(dir, Path.GetFileNameWithoutExtension(item.SavePath));
        string? error;
        // Parçalar DwnlData\<id>\hls\ altında saklanır: duraklat/yarıda kalma sonrası yalnızca eksik parçalar iner.
        string idDir = Path.Combine(_yt.Paths.DwnlDataDir, item.Id);
        string hlsDir = Path.Combine(idDir, "hls");
        try
        {
            var opt = new HlsClient.Options(_yt.FindFfmpeg(), WorkDir: hlsDir, Log: m => System.Diagnostics.Debug.WriteLine(m));
            var path = await _hls.DownloadAsync(variant, audio, ContextOf(item), outBase, opt, report, ct).ConfigureAwait(false);
            TryDeleteEmpty(idDir);
            return new ExternalResult(true, null, path);
        }
        catch (OperationCanceledException) { throw; }
        catch (HlsException ex) when (ex.IsDrm) { return new ExternalResult(false, ex.Message, null); }
        catch (Exception ex) when (ex is HlsException or HttpRequestException or IOException or UriFormatException)
        {
            error = ex.Message;
        }

        // Doğrudan indirme olmadı: yt-dlp varsa aynı başlıklarla onu dene.
        if (_yt.FindYtDlp() == null) return new ExternalResult(false, error, null);
        var fallback = new DownloadItem
        {
            Id = item.Id, Url = item.Url, SavePath = item.SavePath, Referrer = item.Referrer, Origin = item.Origin,
            Cookie = item.Cookie, UserAgent = item.UserAgent, Kind = DownloadKind.External,
            ExternalFormat = height > 0 ? $"bv*[height<={height}]+ba/b[height<={height}]/b" : "bv*+ba/b",
        };
        var res = await _yt.RunAsync(fallback, report, ct).ConfigureAwait(false);
        if (res.Success)
        {
            try { if (Directory.Exists(hlsDir)) Directory.Delete(hlsDir, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            TryDeleteEmpty(idDir);
        }
        return res;
    }

    private static void TryDeleteEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
