namespace TDownloader.App.Ui;

public static class Fmt
{
    public static string Size(long b)
    {
        if (b < 0) return "";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{b} B" : $"{v:0.##} {u[i]}";
    }

    public static string SizeFull(long b) => b < 0 ? "Bilinmiyor" : $"{Size(b)} ({b:N0} bayt)";

    public static string Speed(double bps) => bps <= 0 ? "" : $"{Size((long)bps)}/sn";

    public static string Eta(TimeSpan? t)
    {
        if (t is null) return "";
        var v = t.Value;
        if (v.TotalDays >= 1) return $"{(int)v.TotalDays} gün {v.Hours} sa";
        if (v.TotalHours >= 1) return $"{(int)v.TotalHours} sa {v.Minutes} dk {v.Seconds} sn";
        if (v.TotalMinutes >= 1) return $"{v.Minutes} dk {v.Seconds} sn";
        return $"{Math.Max(0, v.Seconds)} sn";
    }

    public static string Date(DateTime? d) => d is null ? "" : d.Value.ToString("dd.MM.yyyy HH:mm");
}
