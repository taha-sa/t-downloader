namespace TDownloader.Core.Util;

/// <summary>İndirmeden önce hedef sürücünün boş alanını denetler (yalnızca DriveInfo; başka bağımlılık yok).</summary>
public static class DiskSpaceGuard
{
    public const long DefaultMargin = 1L * 1024 * 1024 * 1024; // 1 GB pay

    /// <summary>Boş alan, indirme boyutu + pay'dan azsa true. Boyut bilinmiyorsa (≤0) asla uyarmaz.</summary>
    public static bool IsInsufficient(long freeBytes, long size, long margin = DefaultMargin) =>
        size > 0 && freeBytes >= 0 && freeBytes < size + margin;

    /// <summary>Tek karar noktası: uyarı gerekir mi? Boyut bilinmiyorsa (≤0) yalnız boş alan paydan az ise uyarır. Okunamazsa (Free=null) uyarmaz.</summary>
    public readonly record struct Verdict(bool Warn, long? Free, long Needed);

    public static Verdict Evaluate(string? folder, long size, long margin = DefaultMargin)
    {
        var free = FreeBytes(folder);
        long needed = (size > 0 ? size : 0) + margin;
        return new Verdict(free is { } f && f >= 0 && f < needed, free, needed);
    }

    /// <summary>Klasörün sürücüsündeki boş bayt (klasör yoksa var olan üst klasör); okunamazsa null.</summary>
    public static long? FreeBytes(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var dir = Path.GetFullPath(folder);
            while (!Directory.Exists(dir))
            {
                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent)) break;
                dir = parent;
            }
            var root = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(root)) return null;
            var d = new DriveInfo(root);
            return d.IsReady ? d.AvailableFreeSpace : null;
        }
        catch { return null; }
    }
}
