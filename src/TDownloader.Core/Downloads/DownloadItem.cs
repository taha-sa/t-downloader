using System.Text.Json.Serialization;

namespace TDownloader.Core.Downloads;

public enum DownloadStatus { Queued, Downloading, Paused, Completed, Failed }

/// <summary>Http = kendi motorumuz; External = harici süreç (yt-dlp) ile indirilen öğe.</summary>
public enum DownloadKind { Http, External }

public sealed class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Tamamlandığında dosyanın bulunacağı tam yol.</summary>
    public string SavePath { get; set; } = "";
    /// <summary>Bayt cinsinden boyut; bilinmiyorsa -1.</summary>
    public long TotalSize { get; set; } = -1;
    public long Downloaded { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string Category { get; set; } = Categories.General;
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? LastTryAt { get; set; }
    public string? Description { get; set; }
    public string? Referrer { get; set; }
    public string? Cookie { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>Akış istekleri için Origin başlığı (iframe oynatıcı sayfasının origin'i); yoksa Referrer'dan türetilir.</summary>
    public string? Origin { get; set; }
    public string? Error { get; set; }
    /// <summary>İndirme türü; eski downloads.json dosyalarında yoksa Http.</summary>
    public DownloadKind Kind { get; set; } = DownloadKind.Http;
    /// <summary>External için yt-dlp -f biçim seçicisi.</summary>
    public string? ExternalFormat { get; set; }

    /// <summary>Anlık hız (bayt/sn); kalıcı değil.</summary>
    [JsonIgnore] public double SpeedBytesPerSec { get; set; }
}
