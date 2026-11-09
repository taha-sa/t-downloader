namespace TDownloader.App.Services;

/// <summary>Disk koruması bağlamı: sessiz modda (zamanlayıcı, T-Link) pencere açılmaz; yer yoksa başlatma atlanır.</summary>
public static class DiskGate
{
    [ThreadStatic] private static int _silent;
    public static bool IsSilent => _silent > 0;
    public static IDisposable Silent() { _silent++; return new Scope(); }
    private sealed class Scope : IDisposable { public void Dispose() { if (_silent > 0) _silent--; } }
}
