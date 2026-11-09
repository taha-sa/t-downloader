using TDownloader.Core.Util;

namespace TDownloader.Core.Tests;

public class MimeTypesTests
{
    [Theory]
    [InlineData("a.ZIP", "application/zip")]
    [InlineData("movie.mp4", "video/mp4")]
    [InlineData("noext", "application/octet-stream")]
    [InlineData("x.unknownext", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void FromFileName(string? name, string expected) => Assert.Equal(expected, MimeTypes.FromFileName(name));
}
