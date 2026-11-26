using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TDownloader.Core.Video;

/// <summary>
/// yt-dlp için geçici Netscape çerez dosyası (komut satırında çerez görünmesin diye). %TEMP% altında, yalnızca geçerli
/// kullanıcıya açık izinlerle oluşturulur ve Dispose'ta silinir. Çerez yoksa veya çerezin alındığı host
/// (<see cref="MediaContext.CookieHost"/>) bilinmiyorsa dosya HİÇ oluşturulmaz.
/// </summary>
public sealed class CookieJarFile : IDisposable
{
    public string Path { get; }
    private CookieJarFile(string path) { Path = path; }

    public static CookieJarFile? Create(MediaContext? ctx)
    {
        if (ctx == null || string.IsNullOrWhiteSpace(ctx.Cookies) || string.IsNullOrWhiteSpace(ctx.CookieHost)) return null;
        string content = Build(ctx.CookieHost!, ctx.Cookies!);
        if (content.Length == 0) return null;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tdl-ck-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }   // önce boş oluştur, izni daralt
            Restrict(path);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch { try { File.Delete(path); } catch (Exception) { } throw; }
        return new CookieJarFile(path);
    }

    /// <summary>"a=1; b=2" -> Netscape satırları (alan adı .host, alt alanlar dahil, gizli çerez, uzak bitiş).</summary>
    internal static string Build(string host, string cookieHeader)
    {
        string cleanHost = host.Trim().TrimStart('.');
        if (cleanHost.Length == 0 || cleanHost.Any(c => c < 0x21 || c == 0x7F || c == '/' || c == '\\')) return "";
        string domain = "." + cleanHost;
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        int n = 0;
        foreach (var part in cookieHeader.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string name = part[..eq].Trim(), value = part[(eq + 1)..].Trim();
            if (name.Length == 0 || name.IndexOfAny(['\t', '\r', '\n']) >= 0 || value.IndexOfAny(['\t', '\r', '\n']) >= 0) continue;
            sb.Append(domain).Append("\tTRUE\t/\tFALSE\t2147483647\t").Append(name).Append('\t').Append(value).Append('\n');
            n++;
        }
        return n == 0 ? "" : sb.ToString();
    }

    private static void Restrict(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var sec = new FileSecurity();
                sec.SetAccessRuleProtection(true, false);   // miras alınan izinleri at
                sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(sec);
            }
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception) { /* %TEMP% zaten kullanıcıya özeldir; izin daraltılamazsa devam */ }
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch (Exception) { }
    }
}
