using System.Text;

namespace TDownloader.Host;

/// <summary>stdout native mesajlara ayrıldığından tüm günlük dosyaya yazılır. Hiçbir hata dışarı sızmaz.</summary>
public sealed class HostLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly string? _path;
    private readonly object _lock = new();

    public HostLog(string? path)
    {
        _path = path;
        try
        {
            if (_path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                File.Move(_path, _path + ".old", overwrite: true);
        }
        catch { }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "T-Downloader", "logs", "host.log");

    public void Write(string message)
    {
        if (_path == null) return;
        try
        {
            lock (_lock)
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { }
    }
}
