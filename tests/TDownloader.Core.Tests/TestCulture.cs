using System.Globalization;
using System.Runtime.CompilerServices;
namespace TDownloader.Core.Tests;
internal static class TestCulture
{
    // Legacy text fixtures explicitly describe the Turkish UI.
    [ModuleInitializer]
    internal static void Initialize()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
    }
}

