using System.Text;

namespace TDownloader.Host;

/// <summary>
/// stdout native mesajlara ayrıldığından tüm günlük dosyaya yazılır. Hiçbir hata dışarı sızmaz.
/// Dosya boyutla döndürülür: sınır (varsayılan 1 MB) aşılınca mevcut dosya `.old` olur, yenisi başlar (çalışırken de).
/// </summary>
public sealed class HostLog
{
    public const long DefaultMaxBytes = 1024 * 1024;
    private readonly long _maxBytes;
    private readonly string? _path;
    private readonly object _lock = new();
    private long _size;

    public HostLog(string? path, long maxBytes = DefaultMaxBytes)
    {
        _path = path;
        _maxBytes = Math.Max(256, maxBytes);
        try
        {
            if (_path == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path)) _size = new FileInfo(_path).Length;
            if (_size > _maxBytes) Rotate();
        }
        catch { }
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "T-Downloader", "logs", "host.log");

    private void Rotate()
    {
        File.Move(_path!, _path + ".old", overwrite: true);
        _size = 0;
    }

    public void Write(string message)
    {
        if (_path == null) return;
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}";
            var bytes = new UTF8Encoding(false).GetBytes(line);
            lock (_lock)
            {
                if (_size > 0 && _size + bytes.Length > _maxBytes) Rotate();
                using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes);
                _size += bytes.Length;
            }
        }
        catch { }
    }
}
