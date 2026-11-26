using TDownloader.Core.Localization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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

    /// <summary>Şifreli dosya öneki; yoksa eski (düz JSON) dosya sayılır ve okunmaya devam eder.</summary>
    public const string ProtectedPrefix = "TDP1:";

    /// <summary>Çerezler düz metin kalmasın: içerik DPAPI (CurrentUser) ile korunur. Windows dışında düz yazılır.</summary>
    internal static string Protect(string json)
    {
        if (!OperatingSystem.IsWindows()) return json;
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        return ProtectedPrefix + Convert.ToBase64String(enc);
    }

    /// <summary>Dosya metnini çözer (eski düz JSON da kabul); çözülemezse null.</summary>
    internal static string? Unprotect(string text)
    {
        if (!text.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return text;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(text[ProtectedPrefix.Length..].Trim()), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    /// <summary>Klasör yalnızca geçerli kullanıcıya (miras kesilerek) açılır. Başarısızlık yazmayı engellemez.</summary>
    internal static void RestrictToCurrentUser(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier))) sec.RemoveAccessRule(r);
            var me = WindowsIdentity.GetCurrent().User!;
            sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    public static string DirOf(string rootDir) => Path.Combine(rootDir, "inbox");

    /// <summary>İsteği atomik yazar; dosya yolunu döner (başarısızlıkta istisna fırlatır).</summary>
    public static string Write(string inboxDir, DownloadMessage msg)
    {
        bool fresh = !Directory.Exists(inboxDir);
        Directory.CreateDirectory(inboxDir);
        if (fresh) RestrictToCurrentUser(inboxDir);
        string name = Guid.NewGuid().ToString("N");
        string tmp = Path.Combine(inboxDir, name + ".tmp");
        string final = Path.Combine(inboxDir, name + Extension);
        File.WriteAllText(tmp, Protect(IpcSerializer.Serialize(msg)), new UTF8Encoding(false));
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
                if (now - time > MaxAge) { Fail(path, Strings.CouldNotProcessTheRequestWithin24Hours); continue; }
                var plain = Unprotect(File.ReadAllText(path, Encoding.UTF8));
                var msg = plain == null ? null : IpcSerializer.TryDeserialize(plain) as DownloadMessage;
                if (msg == null || string.IsNullOrEmpty(msg.Url)) { Fail(path, Strings.TheRequestFileIsDamagedAddTheURL); continue; }
                switch (handler(msg, path))
                {
                    case DrainAction.Done: File.Delete(path); handled++; break;
                    case DrainAction.Failed: Fail(path, Strings.TheRequestWasRejectedCheckTheURLAnd); break;
                }
            }
            catch (IOException) { /* başka işlem kullanıyor / az önce silindi: sonraki turda */ }
            catch (UnauthorizedAccessException) { }
        }
        return handled;
    }
}

public enum DrainAction { Done, Keep, Pending, Failed }
