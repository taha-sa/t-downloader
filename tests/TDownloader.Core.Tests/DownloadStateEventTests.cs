using TDownloader.Core.Downloads;
using TDownloader.Core.Ipc;

namespace TDownloader.Core.Tests;

public class DownloadStateEventTests
{
    [Theory]
    [InlineData(DownloadStatus.Paused, "paused")]
    [InlineData(DownloadStatus.Queued, "queued")]
    public void StateEventDescribesActualItem(DownloadStatus state, string expected)
    {
        var item = new DownloadItem { Id = "id-1", FileName = "sample.pdf", Downloaded = 25, TotalSize = 100, Status = state };
        var result = DownloadStateEvent.Create(item)!.Value;
        Assert.Equal(expected, result.GetProperty("state").GetString());
        Assert.Equal("id-1", result.GetProperty("id").GetString());
        Assert.Equal(25, result.GetProperty("pct").GetDouble());
    }

    [Theory]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Failed)]
    public void ProgressAndTerminalStatesUseTheirOwnEvents(DownloadStatus state) =>
        Assert.Null(DownloadStateEvent.Create(new DownloadItem { Status = state }));
}
