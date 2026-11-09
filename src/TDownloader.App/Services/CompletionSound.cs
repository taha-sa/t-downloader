using System.Media;

namespace TDownloader.App.Services;

/// <summary>Kaynaktan yükleme ve oynatma UI iş parçacığını bekletmez.</summary>
internal sealed class CompletionSound : IDisposable
{
    private readonly object _gate = new();
    private SoundPlayer? _player;
    private Stream? _stream;
    private bool _disposed;

    public void Play() => _ = Task.Run(() =>
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                if (_player == null)
                {
                    _stream = typeof(CompletionSound).Assembly.GetManifestResourceStream("tdownloader_tamamlandi.wav")
                        ?? throw new InvalidOperationException("Tamamlanma sesi bulunamadı.");
                    _player = new SoundPlayer(_stream);
                    _player.Load();
                }
                _player.Play();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                _player?.Dispose();
                _player = null;
                _stream?.Dispose();
                _stream = null;
                // Ses aygıtı/kaynağı hatası tamamlanma bildirimini engellemesin.
                System.Diagnostics.Trace.TraceWarning("Tamamlanma sesi: {0}", ex.Message);
            }
        }
    });

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _player?.Dispose();
            _stream?.Dispose();
        }
    }
}
