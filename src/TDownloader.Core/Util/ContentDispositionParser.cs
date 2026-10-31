using System.Text;
using System.Text.RegularExpressions;

namespace TDownloader.Core.Util;

/// <summary>Content-Disposition başlığından dosya adı (filename*=UTF-8''... öncelikli, sonra filename=).</summary>
public static class ContentDispositionParser
{
    private static readonly Regex Ext = new(@"filename\*\s*=\s*([^']*)'[^']*'([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Quoted = new("filename\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Plain = new(@"filename\s*=\s*([^;""]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        string? name = null;
        var m = Ext.Match(header);
        if (m.Success)
        {
            try
            {
                var enc = string.IsNullOrWhiteSpace(m.Groups[1].Value) ? Encoding.UTF8 : Encoding.GetEncoding(m.Groups[1].Value.Trim());
                name = UnescapeBytes(m.Groups[2].Value.Trim(), enc);
            }
            catch (ArgumentException) { name = Uri.UnescapeDataString(m.Groups[2].Value.Trim()); }
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            m = Quoted.Match(header);
            if (m.Success) name = Regex.Replace(m.Groups[1].Value, @"\\(.)", "$1");
            else if ((m = Plain.Match(header)).Success) name = m.Groups[1].Value.Trim();
            if (name != null && Regex.IsMatch(name, "%[0-9A-Fa-f]{2}")) { try { name = Uri.UnescapeDataString(name); } catch (UriFormatException) { } }
        }
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = Path.GetFileName(name.Replace('/', '\\')).Trim().Trim('.');
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? null : name;
    }

    private static string UnescapeBytes(string s, Encoding enc)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length + 0 && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return enc.GetString(bytes.ToArray());
    }
}
