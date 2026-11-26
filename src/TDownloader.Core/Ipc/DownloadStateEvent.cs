using System.Text.Json;
using TDownloader.Core.Downloads;

namespace TDownloader.Core.Ipc;

/// <summary>Terminal states use their own events; paused and queued states must reach remote controls immediately.</summary>
public static class DownloadStateEvent
{
    public static JsonElement? Create(DownloadItem item)
    {
        if (item.Status is not (DownloadStatus.Paused or DownloadStatus.Queued)) return null;
        return JsonSerializer.SerializeToElement(new {
            id = item.Id, name = item.FileName, bytes = item.Downloaded, total = item.TotalSize,
            speed = 0, pct = item.TotalSize > 0 ? item.Downloaded * 100.0 / item.TotalSize : 0.0,
            state = item.Status == DownloadStatus.Paused ? "paused" : "queued"
        });
    }
}
