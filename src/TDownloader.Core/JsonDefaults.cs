using System.Text.Json;
using System.Text.Json.Serialization;

namespace TDownloader.Core;

/// <summary>Çekirdek genelinde ortak System.Text.Json ayarları (camelCase, enum = string).</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    /// <summary>
    /// Atomik yazım + bir önceki sürümün yedeği: önce tmp'ye yazılır ve diske zorlanır, sonra File.Replace ile
    /// hedefin yerine geçer (eski hedef <c>path + ".bak"</c> olur). Hedef yoksa yalnızca taşınır.
    /// </summary>
    internal static void WriteAtomicWithBackup(string path, Action<Stream> write)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            write(fs);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }

    /// <summary>Dosyaya atomik yazar (tmp + move).</summary>
    internal static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
