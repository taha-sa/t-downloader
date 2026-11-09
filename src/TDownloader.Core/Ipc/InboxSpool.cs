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

    public const string FailedDirName = "failed";
    public static string FailedDirOf(string inboxDir) => Path.Combine(inboxDir, FailedDirName);

    /// <summary>Dosyayı silmeden inboxailed\ altına taşır ve nedenini yanına (.reason.txt) yazar. Yeni yolu döner (başarısızlıkta null).</summary>
    public static string? MoveToFailed(string path, string reason)
    {
        try
        {
            string failed = FailedDirOf(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(failed);
            string dest = Path.Combine(failed, Path.GetFileName(path));
            File.Move(path, dest, overwrite: true);
            try { File.WriteAllText(Path.ChangeExtension(dest, ".reason.txt"), reason, new UTF8Encoding(false)); } catch (IOException) { }
            return dest;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>failed\ altındaki istekleri tekrar inbox'a alır (yaş sayacı sıfırlanır; 24 sa kuralı hemen tekrar vurmasın). Taşınan sayıyı döner.</summary>
    public static int RetryFailed(string inboxDir)
    {
        string failed = FailedDirOf(inboxDir);
        if (!Directory.Exists(failed)) return 0;
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(failed, "*" + Extension).ToList())
        {
            try
            {
                string dest = Path.Combine(inboxDir, Path.GetFileName(f));
                File.Move(f, dest, overwrite: true);
                File.SetCreationTimeUtc(dest, DateTime.UtcNow);
                try { File.Delete(Path.ChangeExtension(f, ".reason.txt")); } catch (IOException) { }
                n++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>Bool sürümü: true = işlendi (sil), false = kalsın.</summary>
    public static int Drain(string inboxDir, Func<DownloadMessage, bool> handler, DateTime? nowUtc = null, Action<string, string>? onFailed = null) =>
        Drain(inboxDir, (m, _) => handler(m) ? DrainAction.Done : DrainAction.Keep, nowUtc, onFailed);

    /// <summary>
    /// Bekleyen istekleri eskiden yeniye işler. Handler: Done = dosyayı sil, Keep = kalsın (sonra yeniden denenir),
    /// Pending = işlem sürüyor (dosya kalır, dokunulmaz; çağıran bitince siler/taşır), Failed = failed\ altına taşı.
    /// 24 saatten eski veya bozuk dosyalar SESSİZCE SİLİNMEZ: failed\ altına taşınır, onFailed(yol, neden) çağrılır.
    /// Dönen sayı = Done sayısı. Yarım (.tmp) dosyalara dokunulmaz.
    /// </summary>
    public static int Drain(string inboxDir, Func<DownloadMessage, string, DrainAction> handler, DateTime? nowUtc = null, Action<string, string>? onFailed = null)
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
        void Fail(string path, string reason)
        {
            var dest = InboxSpool.MoveToFailed(path, reason);
            if (dest != null) onFailed?.Invoke(dest, reason);
        }
        foreach (var (path, time) in files)
        {
            try
            {
                if (now - time > MaxAge) { Fail(path, "24 saatten uzun süre işlenemedi"); continue; }
                var msg = IpcSerializer.TryDeserialize(File.ReadAllText(path, Encoding.UTF8)) as DownloadMessage;
                if (msg == null || string.IsNullOrEmpty(msg.Url)) { Fail(path, "Bozuk istek dosyası"); continue; }
                switch (handler(msg, path))
                {
                    case DrainAction.Done: File.Delete(path); handled++; break;
                    case DrainAction.Failed: Fail(path, "İstek reddedildi"); break;
                }
            }
            catch (IOException) { /* başka işlem kullanıyor / az önce silindi: sonraki turda */ }
            catch (UnauthorizedAccessException) { }
        }
        return handled;
    }
}

public enum DrainAction { Done, Keep, Pending, Failed }
