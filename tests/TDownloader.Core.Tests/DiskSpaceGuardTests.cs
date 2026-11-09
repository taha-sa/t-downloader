using TDownloader.Core.Util;

namespace TDownloader.Core.Tests;

public class DiskSpaceGuardTests
{
    private const long GB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(10 * GB, 5 * GB, false)]
    [InlineData(5 * GB, 5 * GB, true)]
    [InlineData(6 * GB, 5 * GB, false)]
    [InlineData(6 * GB - 1, 5 * GB, true)]
    [InlineData(100, -1, false)]
    [InlineData(0, 0, false)]
    public void IsInsufficient(long free, long size, bool expected) =>
        Assert.Equal(expected, DiskSpaceGuard.IsInsufficient(free, size));

    [Fact]
    public void FreeBytes_resolves_existing_and_missing_folders()
    {
        Assert.True(DiskSpaceGuard.FreeBytes(Path.GetTempPath()) > 0);
        Assert.True(DiskSpaceGuard.FreeBytes(Path.Combine(Path.GetTempPath(), "yok-" + Guid.NewGuid().ToString("N"), "alt")) > 0);
        Assert.Null(DiskSpaceGuard.FreeBytes(""));
    }
}
