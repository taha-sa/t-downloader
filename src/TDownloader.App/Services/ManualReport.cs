using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using TSuite.Diagnostics;

namespace TDownloader.App.Services;

internal static class ManualReport
{
    public static CrashReport Create()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "T-Suite", "logs", UpdateService.AppId);
        var lines = new List<string>();
        foreach (string name in new[] { "app.log.2", "app.log.1", "app.log" })
        {
            try { lines.AddRange(File.ReadLines(Path.Combine(directory, name))); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        using var process = Process.GetCurrentProcess();
        return new CrashReport("manual-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow,
            UpdateService.AppId, "T-Downloader", AppServices.Version, "taha-sa/t-downloader-releases",
            RuntimeInformation.OSDescription + " (" + Environment.OSVersion.Version + ")",
            RuntimeInformation.FrameworkDescription, CultureInfo.CurrentCulture.Name,
            DateTime.UtcNow - process.StartTime.ToUniversalTime(),
            new ExceptionDetails("Manual report", "Kullanıcı tarafından hazırlanan hata raporu", null, []),
            lines.TakeLast(200).ToArray());
    }
}
