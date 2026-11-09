using System.Net;
using System.Net.Sockets;

namespace TDownloader.Core.Tests;

/// <summary>
/// Test sunucuları için boş loopback portu. Aynı işlemde bir port İKİ KEZ verilmez: kapanmış bir sunucunun portunu
/// sonradan açılan başka bir test alırsa, öncekinden kalan bağlantılar/yeniden denemeler yeni testin istek sayacını
/// bozar ve statik HostGate (yetki -> sınır) durumu testler arasında sızar.
/// </summary>
public static class TestPorts
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> Used = new();

    public static int Next()
    {
        lock (Gate)
        {
            for (int i = 0; i < 200; i++)
            {
                var t = new TcpListener(IPAddress.Loopback, 0); t.Start();
                int port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
                if (Used.Add(port)) return port;
            }
            throw new InvalidOperationException("Kullanılmamış boş port bulunamadı");
        }
    }
}
