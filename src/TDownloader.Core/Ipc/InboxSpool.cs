using System.Text;

namespace TDownloader.Core.Ipc;

/// <summary>
/// Gelen kutusu (spool): App kapalıyken Host'un aldığı indirme istekleri <c>%APPDATA%\T-Downloader\inbox\&lt;guid&gt;.json</c>
/// olarak atomik yazılır (önce .tmp, sonra taşıma). App açılınca boşaltır ve çalışırken izler. Kayıp indirme olmaz.
/// </summary>
public static class InboxSpool
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public const string Extension = ".json";

    public static string DirOf(string rootDir) => Path.Combine(rootDir, "inbox");

    /// <summary>İsteği atomik yazar; dosya yolunu döner (başarısızlıkta istisna fırlatır).</summary>
    public static string Write(string inboxDir, DownloadMessage msg)
    {
        Directory.CreateDirectory(inboxDir);
        string name = Guid.NewGuid().ToString("N");
        string tmp = Path.Combine(inboxDir, name + ".tmp");
        string final = Path.Combine(inboxDir, name + Extension);
        File.WriteAllText(tmp, IpcSerializer.Serialize(msg), new UTF8Encoding(false));
        File.Move(tmp, final);
        return final;
    }

    /// <summary>
    /// Bekleyen istekleri eskiden yeniye işler. handler true dönerse dosya silinir; false/istisnada kalır (sonra yeniden denenir).
    /// 24 saatten eski veya bozuk dosyalar silinir. İşlenen sayıyı döner. Yarım (.tmp) dosyalara dokunulmaz.
    /// </summary>
    public static int Drain(string inboxDir, Func<DownloadMessage, bool> handler, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(inboxDir)) return 0;
        var now = nowUtc ?? DateTime.UtcNow;
        var files = new List<(string path, DateTime time)>();
        foreach (var f in Directory.EnumerateFiles(inboxDir, "*" + Extension))
        {
            try { files.Add((f, File.GetCreationTimeUtc(f))); } catch (IOException) { }
        }
        files.Sort((a, b) => a.time.CompareTo(b.time));
        int handled = 0;
        foreach (var (path, time) in files)
        {
            try
            {
                if (now - time > MaxAge) { File.Delete(path); continue; }
                var msg = IpcSerializer.TryDeserialize(File.ReadAllText(path, Encoding.UTF8)) as DownloadMessage;
                if (msg == null || string.IsNullOrEmpty(msg.Url)) { File.Delete(path); continue; }
                if (handler(msg)) { File.Delete(path); handled++; }
            }
            catch (IOException) { /* başka işlem kullanıyor: sonraki turda */ }
            catch (UnauthorizedAccessException) { }
        }
        return handled;
    }
}
