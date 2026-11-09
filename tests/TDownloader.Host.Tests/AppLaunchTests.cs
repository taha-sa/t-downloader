using TDownloader.Host;
using Xunit;

namespace TDownloader.Host.Tests;

public class AppLaunchTests
{
    [Fact]
    public void AppStartInfo_DoesNotInheritOrRedirectHostStdio()
    {
        var psi = HostRunner.BuildAppStartInfo(@"C:\x\TDownloader.exe");
        Assert.True(psi.UseShellExecute);
        Assert.False(psi.RedirectStandardInput);
        Assert.False(psi.RedirectStandardOutput);
        Assert.False(psi.RedirectStandardError);
        Assert.Equal("--background", psi.Arguments);
    }
}
