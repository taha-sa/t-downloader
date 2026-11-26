using TDownloader.Core.Localization;
namespace TDownloader.App.Ui;

public static class Fmt
{
    public static string Size(long b)
    {
        if (b < 0) return "";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b}\u00a0B" : $"{v:0.##}\u00a0{u[i]}";
    }

    public static string SizeFull(long b) => b < 0 ? Strings.Unknown : Strings.Bytes(Size(b), b);

    public static string Speed(double bps) => bps <= 0 ? "" : Size((long)bps) + Strings.PerSecond;

    /// <summary>Kalan süre: &lt;1 dk "X sn", &lt;1 sa "X dk Y sn", üstü "X sa Y dk" (saniye yukarı yuvarlanır).</summary>
    public static string Eta(TimeSpan? t)
    {
        if (t is null) return "";
        long sec = (long)Math.Ceiling(Math.Max(0, t.Value.TotalSeconds));
        if (sec < 60) return Strings.S(sec);
        if (sec < 3600) return Strings.MinS(sec / 60, sec % 60);
        return Strings.HMin(sec / 3600, sec % 3600 / 60);
    }

    public static string Date(DateTime? d) => d is null ? "" : d.Value.ToString(Strings.DatePattern, System.Globalization.CultureInfo.CurrentCulture);
}
