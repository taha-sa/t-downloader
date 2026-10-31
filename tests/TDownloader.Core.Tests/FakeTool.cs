using System.Diagnostics;

namespace TDownloader.Core.Tests;

/// <summary>
/// Test derlemesi aynı zamanda sahte yt-dlp/ffmpeg olarak çalışır (apphost kopyası + fake.cfg). .cmd betikleri '|' içeren
/// argümanları taşıyamadığı için gerçek bir exe gerekir. Davranış, exe'nin yanındaki fake.cfg (satır başına anahtar=değer) ile seçilir:
///   spam=N     N adet yt-dlp ilerleme satırı basar (pipe tamponunu doldurmak için)
///   file=YOL   "TDL_FILE:YOL" satırı basar
///   hang=1     (ffmpeg) çıktıyı yazdıktan sonra 60 sn bekler
///   fail=1     (ffmpeg) çıktıyı yazıp 1 ile çıkar
/// Her çağrıda argümanlar args.txt'ye (çağrı başına bir blok, argüman başına bir satır) eklenir.
/// </summary>
public static class FakeTool
{
    public static int Main(string[] a)
    {
        string dir = AppContext.BaseDirectory;
        var cfg = new Dictionary<string, string>();
        string cfgPath = Path.Combine(dir, "fake.cfg");
        if (File.Exists(cfgPath))
            foreach (var l in File.ReadAllLines(cfgPath)) { int i = l.IndexOf('='); if (i > 0) cfg[l[..i]] = l[(i + 1)..]; }
        File.AppendAllText(Path.Combine(dir, "args.txt"), string.Join("\n", a) + "\n=====\n");

        if (a.Length > 0 && a[0] == "--help") { Console.Out.WriteLine("--progress-delta"); return 0; }

        int ci = Array.IndexOf(a, "--cookies");
        if (ci >= 0 && ci + 1 < a.Length) File.Copy(a[ci + 1], Path.Combine(dir, "seen-cookies.txt"), true);

        if (cfg.TryGetValue("spam", out var sp))
            for (int i = 1; i <= int.Parse(sp); i++)
                Console.Out.WriteLine($"TDL_PROG:downloading|{i}|100000|NA|1000|5|137");
        if (cfg.TryGetValue("file", out var f)) Console.Out.WriteLine("TDL_FILE:" + f);

        if (a.Contains("-progress"))   // ffmpeg kipi: son argüman çıktı yolu
        {
            File.WriteAllText(a[^1], "FAKE-FFMPEG-OUTPUT");
            if (cfg.ContainsKey("hang")) Thread.Sleep(60_000);
            return cfg.ContainsKey("fail") ? 1 : 0;
        }
        return 0;
    }

    /// <summary>Sahte aracı klasöre kopyalar ve yolunu döner.</summary>
    public static string Install(string dir, string cfg = "")
    {
        Directory.CreateDirectory(dir);
        string src = AppContext.BaseDirectory, name = "TDownloader.Core.Tests";
        foreach (var ext in new[] { ".exe", ".dll", ".runtimeconfig.json" })
            File.Copy(Path.Combine(src, name + ext), Path.Combine(dir, name + ext), true);
        File.WriteAllText(Path.Combine(dir, "fake.cfg"), cfg);
        return Path.Combine(dir, name + ".exe");
    }
}
