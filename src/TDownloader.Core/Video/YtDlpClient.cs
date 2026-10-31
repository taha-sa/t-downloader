using System.Diagnostics;
using System.Text;
using TDownloader.Core.Downloads;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.Core.Video;

public sealed class YtDlpNotFoundException : Exception
{
    public YtDlpNotFoundException() : base("yt-dlp bulunamadı.") { }
}

public sealed class YtDlpException : Exception
{
    public YtDlpException(string message) : base(message) { }
}

/// <summary>yt-dlp alt sürecini çalıştırır: kalite listesi (-J) ve indirme. Hiçbir şey indirilmez/kurulmaz.</summary>
public sealed class YtDlpClient : IExternalRunner
{
    private readonly Func<AppSettings> _settings;
    private readonly AppPaths _paths;
    private readonly string _appBase;

    public YtDlpClient(Func<AppSettings> settings, AppPaths paths, string? appBaseDir = null)
    {
        _settings = settings; _paths = paths; _appBase = appBaseDir ?? AppContext.BaseDirectory;
    }

    public AppPaths Paths => _paths;

    public string? FindYtDlp() => ToolLocator.Find("yt-dlp", _settings().YtDlpPath, _paths.ToolsDir, _appBase);
    public string? FindFfmpeg() => ToolLocator.Find("ffmpeg", _settings().FfmpegPath, _paths.ToolsDir, _appBase);

    // ---------------------------------------------------------------- argümanlar (test edilebilir)

    /// <summary>Referer/Origin/Cookie/User-Agent'ı yt-dlp bağımsız değişkenlerine çevirir (OpenDM'deki http_headers karşılığı).</summary>
    public static void AddContextArgs(List<string> a, string? referer, MediaContext? ctx)
    {
        string? refr = !string.IsNullOrWhiteSpace(ctx?.Referer) ? ctx!.Referer : referer;
        if (!string.IsNullOrWhiteSpace(refr)) { a.Add("--referer"); a.Add(refr); }
        var origin = ctx != null ? ctx.EffectiveOrigin : (Uri.TryCreate(referer, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null);
        if (!string.IsNullOrWhiteSpace(origin)) { a.Add("--add-header"); a.Add("Origin:" + origin); }
        if (!string.IsNullOrWhiteSpace(ctx?.Cookies)) { a.Add("--add-header"); a.Add("Cookie:" + ctx!.Cookies); }
        if (!string.IsNullOrWhiteSpace(ctx?.UserAgent)) { a.Add("--user-agent"); a.Add(ctx!.UserAgent); }
    }

    public static List<string> BuildInfoArgs(string url, string? referer, MediaContext? ctx = null)
    {
        var a = new List<string> { "-J", "--no-playlist", "--no-warnings", "--no-colors", "--socket-timeout", "20", "--retries", "3" };
        AddContextArgs(a, referer, ctx);
        a.Add("--"); a.Add(url);
        return a;
    }

    public static List<string> BuildDownloadArgs(string url, string format, string outputTemplate, string? referer, string? ffmpeg, MediaContext? ctx = null)
    {
        var a = new List<string>
        {
            "-f", format, "--merge-output-format", "mp4", "-o", outputTemplate,
            "--no-playlist", "--newline", "--no-colors", "--no-warnings", "--no-quiet", "--no-simulate", "--progress",
            "--progress-template", YtDlpParsing.ProgressTemplate,
            "--print", "after_move:" + YtDlpParsing.FilePrefix + "%(filepath)s",
        };
        a.AddRange(new[] { "--retries", "10", "--fragment-retries", "10", "--concurrent-fragments", "4", "--socket-timeout", "30" });
        if (!string.IsNullOrWhiteSpace(ffmpeg)) { a.Add("--ffmpeg-location"); a.Add(ffmpeg); }
        AddContextArgs(a, referer, ctx);
        a.Add("--"); a.Add(url);
        return a;
    }

    private static ProcessStartInfo Psi(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        return psi;
    }

    // ---------------------------------------------------------------- kalite listesi

    public async Task<VideoInfo> GetInfoAsync(string url, string? referer, CancellationToken ct = default, MediaContext? ctx = null)
    {
        var exe = FindYtDlp() ?? throw new YtDlpNotFoundException();
        using var p = new Process { StartInfo = Psi(exe, BuildInfoArgs(url, referer, ctx)) };
        try { p.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new YtDlpException("yt-dlp başlatılamadı: " + ex.Message); }
        Util.ProcessJob.Assign(p);   // uygulama çökerse süreç yetim kalmasın
        try { p.StandardInput.Close(); } catch (IOException) { }
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Kill(p);
            if (ct.IsCancellationRequested) throw;
            throw new YtDlpException("yt-dlp zaman aşımına uğradı.");
        }
        string stdout = await outTask.ConfigureAwait(false), stderr = await errTask.ConfigureAwait(false);
        if (p.ExitCode != 0 || stdout.Trim().Length == 0) throw new YtDlpException(YtDlpParsing.MapError(stderr));
        try { return YtDlpParsing.ParseInfo(stdout); }
        catch (System.Text.Json.JsonException) { throw new YtDlpException("yt-dlp çıktısı anlaşılamadı."); }
    }

    // ---------------------------------------------------------------- indirme (IExternalRunner)

    public async Task<ExternalResult> RunAsync(DownloadItem item, Action<ExternalProgress> report, CancellationToken ct)
    {
        var exe = FindYtDlp();
        if (exe == null)
            return new ExternalResult(false, "yt-dlp bulunamadı. Seçenekler > Video sekmesinden yolunu belirtin veya kurulum bilgisine bakın.", null);
        var dir = Path.GetDirectoryName(item.SavePath)!;
        Directory.CreateDirectory(dir);
        string template = Path.Combine(dir, Path.GetFileNameWithoutExtension(item.SavePath)) + ".%(ext)s";
        var args = BuildDownloadArgs(item.Url, item.ExternalFormat ?? "bv*+ba/b", template, item.Referrer, FindFfmpeg(), VideoRunner.ContextOf(item));
        using var p = new Process { StartInfo = Psi(exe, args) };
        var err = new StringBuilder();
        string? finalPath = null;
        try { p.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new ExternalResult(false, "yt-dlp başlatılamadı: " + ex.Message, null); }
        Util.ProcessJob.Assign(p);   // uygulama çökerse süreç yetim kalmasın
        try { p.StandardInput.Close(); } catch (IOException) { }
        using var reg = ct.Register(() => Kill(p));

        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                var pr = YtDlpParsing.ParseProgressLine(line);
                if (pr != null) { report(new ExternalProgress(pr.Downloaded, pr.Total, pr.Speed, pr.EtaSeconds)); continue; }
                finalPath = YtDlpParsing.ParseFilePathLine(line) ?? finalPath;
            }
        });
        var errTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync().ConfigureAwait(false)) != null) lock (err) err.AppendLine(line);
        });
        await p.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
            return new ExternalResult(false, YtDlpParsing.MapError(err.ToString()), null);
        return new ExternalResult(true, null, finalPath);
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
    }
}
