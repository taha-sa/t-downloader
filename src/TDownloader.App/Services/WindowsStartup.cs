using Microsoft.Win32;

namespace TDownloader.App.Services;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "T-Downloader";

    public static bool TryApply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                // DLL/dotnet başlatmalarında da kayıt gerçek uygulama exe'sini göstersin.
                var executable = Path.Combine(AppContext.BaseDirectory, "TDownloader.exe");
                key.SetValue(ValueName, $"\"{executable}\" --background", RegistryValueKind.String);
            }
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Trace.TraceWarning("Windows başlangıç ayarı: {0}", ex.Message);
            return false;
        }
    }
}
