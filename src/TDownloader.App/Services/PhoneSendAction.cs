using System.Text.Json;
using TDownloader.App.Ui;
using TSuite.Link;

namespace TDownloader.App.Services;

/// <summary>
/// "Telefona gönder" (ICompletedAction): yalnızca T-Bridge kayıtlıysa VE telefon bağlıysa (phone.status) görünür.
/// Durum arka planda yoklanır (en çok 3 sn'de bir; UI thread'i bloklanmaz), değişince <see cref="Changed"/> tetiklenir.
/// Gönderme: T-Bridge'e T-Link komutu file.send {path}.
/// </summary>
public sealed class PhoneSendAction : ICompletedAction
{
    private volatile bool _available;
    private long _lastCheck;
    private int _busy;

    public event Action? Changed;
    public string Text => "Telefona gönder";

    public bool IsAvailable(CompletedEntry entry)
    {
        long now = Environment.TickCount64;
        if (now - _lastCheck > 3000 && Interlocked.Exchange(ref _busy, 1) == 0)
        {
            _lastCheck = now;
            _ = Task.Run(async () =>
            {
                try
                {
                    bool v = await QueryAsync().ConfigureAwait(false);
                    if (v != _available) { _available = v; Changed?.Invoke(); }
                }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }
        return _available;
    }

    public void Execute(CompletedEntry entry) => _ = Task.Run(async () =>
    {
        try
        {
            await using var c = new LinkClient("t-bridge", TimeSpan.FromSeconds(5));
            c.Start();
            if (!await c.WaitConnectedAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false)) return;
            await c.SendCommandAsync("file.send", LinkJson.Obj(("path", entry.Path)), TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch { /* gönderilemedi: sessizce geç */ }
    });

    private static async Task<bool> QueryAsync()
    {
        try
        {
            var m = new AppRegistry().Read("t-bridge");
            if (m == null || !AppRegistry.IsAlive(m.Pid)) return false;
            await using var c = new LinkClient("t-bridge", TimeSpan.FromSeconds(5));
            c.Start();
            if (!await c.WaitConnectedAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)) return false;
            var r = await c.SendCommandAsync("phone.status", null, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return r.Data is { } d && d.TryGetProperty("result", out var res)
                && res.ValueKind == JsonValueKind.Object && res.TryGetProperty("connected", out var cn) && cn.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
}
