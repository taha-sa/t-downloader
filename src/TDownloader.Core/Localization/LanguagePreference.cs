using System.Globalization;
namespace TDownloader.Core.Localization;
public static class LanguagePreference
{
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    public static void Apply(string? language)
    {
        var culture = language switch
        {
            "tr" => CultureInfo.GetCultureInfo("tr-TR"),
            "de" => CultureInfo.GetCultureInfo("de-DE"),
            "en" => CultureInfo.GetCultureInfo("en-US"),
            _ => SystemUiCulture
        };
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture =
            language is "tr" or "de" or "en" ? culture : SystemCulture;
    }
}
