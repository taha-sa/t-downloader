using TDownloader.Core.Localization;
using System.Text.Json;
namespace TDownloader.App.Ui;

public static class PhoneTransferReply
{
    public static bool Accepted(JsonElement? data) => data is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
        && value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
        && result.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array
        && ids.EnumerateArray().Any(id => id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()));
}

/// <summary>"İndirme tamamlandı" penceresinde gösterilen bir kayıt.</summary>
public sealed record CompletedEntry(string Id, string FileName, string Url, string Path, long Size);

/// <summary>
/// Birden çok tamamlanma tek pencerede biriktirilir ("1/3" sayfalayıcı). WinForms'tan bağımsız, test edilebilir.
/// Yeni kayıt eklenince o gösterilir (en yeni); aynı kimlik ikinci kez eklenmez.
/// </summary>
public sealed class CompletedQueue
{
    private readonly List<CompletedEntry> _items = new();
    public int Count => _items.Count;
    public int Index { get; private set; }
    public CompletedEntry? Current => Index >= 0 && Index < _items.Count ? _items[Index] : null;
    public string Pager => _items.Count > 1 ? $"{Index + 1}/{_items.Count}" : "";

    public bool Add(CompletedEntry e)
    {
        foreach (var x in _items) if (x.Id == e.Id) return false;
        _items.Add(e);
        Index = _items.Count - 1;
        return true;
    }

    public bool Next() { if (Index + 1 >= _items.Count) return false; Index++; return true; }
    public bool Prev() { if (Index <= 0) return false; Index--; return true; }
    public void Clear() { _items.Clear(); Index = 0; }
}

public static class CompletedText
{
    /// <summary>"İndirildi: 9.90 MB (10.385.264 bayt)" - iki ondalık, bayt sayısı binlik ayraçlı (tr-TR).</summary>
    public static string SizeLine(long bytes)
    {
        if (bytes < 0) return Strings.Downloaded2;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        var tr = System.Globalization.CultureInfo.CurrentCulture;
        string human = i == 0 ? $"{bytes}\u00a0B" : v.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture) + "\u00a0" + u[i];
        return Strings.DownloadedBytes(human, bytes.ToString("N0", tr));
    }

    /// <summary>
    /// Ortadan kısaltma: sığdırma ölçütü fits(metin) ile verilir (gerçek pencerede TextRenderer.MeasureText, testte uzunluk).
    /// Başı ve sonu korur; sığan en uzun biçimi bulur.
    /// </summary>
    public static string MiddleEllipsis(string text, Func<string, bool> fits)
    {
        if (string.IsNullOrEmpty(text) || fits(text)) return text;
        const string dots = "…";
        int lo = 0, hi = text.Length - 1;     // kaç karakter korunur
        string best = dots;
        while (lo <= hi)
        {
            int keep = (lo + hi) / 2;
            int head = (keep + 1) / 2, tail = keep / 2;
            string cand = text[..head] + dots + text[^tail..];
            if (tail == 0) cand = text[..head] + dots;
            if (fits(cand)) { best = cand; lo = keep + 1; } else hi = keep - 1;
        }
        return best;
    }
}

/// <summary>Gelecekteki eylemler (ör. T-Bridge "Telefonda aç", Faz 5) için uzantı noktası: pencere şimdilik yalnızca boş bir yuva ayırır.</summary>
public interface ICompletedAction
{
    string Text { get; }
    bool IsAvailable(CompletedEntry entry);
    void Execute(CompletedEntry entry);
}

/// <summary>Sanal liste için süzülmüş + sıralanmış görünüm ve anahtar -> sıra eşlemesi (O(1) arama).</summary>
public sealed class ViewIndex<T> where T : class
{
    private readonly Func<T, string> _key;
    private readonly List<T> _list = new();
    private readonly Dictionary<string, int> _index = new();

    public ViewIndex(Func<T, string> key) { _key = key; }
    public int Count => _list.Count;
    public T this[int i] => _list[i];
    public int IndexOf(string key) => _index.TryGetValue(key, out var i) ? i : -1;
    public bool Contains(string key) => _index.ContainsKey(key);

    public void Rebuild(IEnumerable<T> source, Func<T, bool> filter, IComparer<T>? cmp)
    {
        _list.Clear();
        foreach (var x in source) if (filter(x)) _list.Add(x);
        if (cmp != null) _list.Sort(cmp);
        Reindex();
    }

    /// <summary>Sona ekler (sıralama yoksa); zaten varsa false.</summary>
    public bool Append(T x)
    {
        var k = _key(x);
        if (_index.ContainsKey(k)) return false;
        _index[k] = _list.Count;
        _list.Add(x);
        return true;
    }

    public void Resort(IComparer<T> cmp) { _list.Sort(cmp); Reindex(); }

    private void Reindex()
    {
        _index.Clear();
        for (int i = 0; i < _list.Count; i++) _index[_key(_list[i])] = i;
    }
}

/// <summary>"Aç" öncesi onay gerektiren çalıştırılabilir/betik uzantıları.</summary>
public static class ExecutableGuard
{
    private static readonly HashSet<string> Risky = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".lnk", ".scr" };

    public static bool IsRisky(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext;
        try { ext = System.IO.Path.GetExtension(path.Trim().TrimEnd('.', ' ')); } catch (ArgumentException) { return false; }
        return Risky.Contains(ext);
    }
}

/// <summary>Sekme sırasında odak döngüsü (son düğmeden ilkine, geri yönde sondan başa).</summary>
public static class FocusCycle
{
    public static string Next(IReadOnlyList<string> order, string current, bool back)
    {
        if (order.Count == 0) return "";
        int i = -1;
        for (int k = 0; k < order.Count; k++) if (order[k] == current) { i = k; break; }
        if (i < 0) return back ? order[^1] : order[0];
        return order[((i + (back ? -1 : 1)) % order.Count + order.Count) % order.Count];
    }
}
