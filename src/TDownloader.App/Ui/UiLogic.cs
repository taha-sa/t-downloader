namespace TDownloader.App.Ui;

/// <summary>Metin güncellemelerini en fazla belirli aralıkta bir kez geçiren sayaç (WinForms'tan bağımsız, test edilebilir).</summary>
public sealed class UpdateGate
{
    private long _last = long.MinValue;
    public const int DefaultIntervalMs = 1000;

    /// <summary>Süre dolduysa true döner ve zamanı kaydeder; aksi halde false.</summary>
    public bool Due(long nowMs, int intervalMs = DefaultIntervalMs)
    {
        if (_last != long.MinValue && nowMs - _last < intervalMs && nowMs >= _last) return false;
        _last = nowMs;
        return true;
    }

    /// <summary>Sonraki Due çağrısı hemen true dönsün (ör. indirme durunca).</summary>
    public void Reset() => _last = long.MinValue;
}

/// <summary>Bir dosyanın Geri Dönüşüm Kutusu'na gönderilme sonucu.</summary>
public enum RecycleOutcome { Recycled, Missing, Skipped, Failed }

/// <summary>"Diskten sil" için silinecek dosya özeti ve onay metni.</summary>
public static class DiskDeletePlan
{
    /// <summary>Var olan dosyaların toplam boyutu (olmayan/erişilemeyen dosya 0 sayılır).</summary>
    public static long TotalExistingSize(IEnumerable<string?> paths, Func<string, long?>? sizeOf = null)
    {
        sizeOf ??= p => { try { var f = new FileInfo(p); return f.Exists ? f.Length : null; } catch { return null; } };
        long total = 0;
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            var s = sizeOf(p);
            if (s is > 0) total += s.Value;
        }
        return total;
    }

    public static string ConfirmText(int count, long totalBytes) =>
        $"{count} dosya diskten silinecek (toplam {Fmt.Size(totalBytes)}). Dosyalar Geri Dönüşüm Kutusu'na gönderilecek; kutuya gönderilemeyen dosya için ayrıca sorulur (sessizce kalıcı silinmez). Emin misiniz?";

    /// <summary>
    /// SHFileOperation sonucu: kod 0 ve iptal yok -> dosya gitti (Recycled; hâlâ duruyorsa Failed). Kullanıcı kalıcı silme uyarısını
    /// reddettiyse (aborted) dosya yerinde kalır -> Skipped. Diğer kodlar Failed.
    /// </summary>
    public static RecycleOutcome Interpret(int code, bool aborted, bool existsAfter)
    {
        if (aborted || code == 0x75 || code == 0x4C7) return existsAfter ? RecycleOutcome.Skipped : RecycleOutcome.Recycled;
        if (code != 0) return RecycleOutcome.Failed;
        return existsAfter ? RecycleOutcome.Failed : RecycleOutcome.Recycled;
    }

    public static string SkippedSummary(int skipped) =>
        $"{skipped} dosya Geri Dönüşüm Kutusu'na gönderilemediği ve kalıcı silme onaylanmadığı için diskte bırakıldı (listeden kaldırıldı).";

    public static string ErrorSummary(int failed, string? firstError) =>
        $"{failed} dosya Geri Dönüşüm Kutusu'na gönderilemedi." + (string.IsNullOrEmpty(firstError) ? "" : "\n" + firstError);
}
