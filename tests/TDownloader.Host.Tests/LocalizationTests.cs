using System.Globalization;
using System.Xml.Linq;
using TDownloader.App.Ui;
using TDownloader.Core.Localization;
using TDownloader.Core.Settings;

namespace TDownloader.Host.Tests;

public class LocalizationTests
{
    [Fact]
    public void Resources_have_identical_keys_and_format_arguments()
    {
        Dictionary<string, string> Read(string suffix) => XDocument.Load(Path.Combine(
            AppContext.BaseDirectory, "Localization", $"Strings{suffix}.resx"))
            .Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);
        var neutral = Read("");
        Assert.Equal(neutral.Count, neutral.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var accessors = typeof(Strings).GetProperties().Select(p => p.Name).Concat(
            typeof(Strings).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName).Select(m => m.Name));
        Assert.Equal(neutral.Keys.Order(), accessors.Order());
        foreach (var suffix in new[] { ".tr", ".de" })
        {
            var translated = Read(suffix);
            Assert.Equal(neutral.Keys.Order(), translated.Keys.Order());
            foreach (var key in neutral.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(translated[key]), key);
                Assert.Equal(System.Text.CompositeFormat.Parse(neutral[key]).MinimumArgumentCount,
                    System.Text.CompositeFormat.Parse(translated[key]).MinimumArgumentCount);
                Assert.DoesNotContain("!", translated[key]);
            }
        }
    }

    [Theory]
    [InlineData("tr-TR", "Ayarlar", "1,5", "14.05.2026", "/sn")]
    [InlineData("de-DE", "Einstellungen", "1,5", "14.05.2026", "/s")]
    [InlineData("en-US", "Settings", "1.5", "2026-05-14", "/s")]
    [InlineData("fr-FR", "Settings", "1,5", "2026-05-14", "/s")]
    public void Ui_and_formatting_follow_the_selected_culture(string culture, string settings, string number, string date, string speedUnit)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(settings, Strings.Settings2);
            Assert.Equal(number + "\u00a0KB", Fmt.Size(1536));
            Assert.Contains(date, Fmt.Date(new DateTime(2026, 5, 14, 16, 45, 0)));
            Assert.EndsWith(speedUnit, Fmt.Speed(1536));
            Assert.Contains(number.Replace("1,5", "1,50").Replace("1.5", "1.50"), CompletedText.SizeLine(1572864));
            Assert.Equal("T-Downloader", Strings.AppName);
        }
        finally { CultureInfo.CurrentCulture = previous; CultureInfo.CurrentUICulture = previousUi; }
    }

    [Theory]
    [InlineData("")]
    [InlineData("tr")]
    [InlineData("en")]
    [InlineData("de")]
    public void Language_preference_round_trips(string language)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tdownloader-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "settings.json");
        try
        {
            new AppSettings { Language = language, SettingsVersion = AppSettings.CurrentSettingsVersion }.Save(path);
            Assert.Equal(language, AppSettings.Load(path).Language);
        }
        finally { File.Delete(path); Directory.Delete(folder); }
    }
}
