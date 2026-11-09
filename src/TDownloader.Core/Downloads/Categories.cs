namespace TDownloader.Core.Downloads;

public static class Categories
{
    public const string Compressed = "Sıkıştırılmış";
    public const string Documents = "Belgeler";
    public const string Music = "Müzik";
    public const string Programs = "Programlar";
    public const string Video = "Video";
    public const string Pictures = "Resimler";
    public const string General = "Genel";

    public static readonly string[] All = { Compressed, Documents, Music, Programs, Video, Pictures, General };

    private static readonly Dictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) d[e] = cat; }
        Add(Compressed, "zip rar 7z gz tgz tar bz2 xz iso z cab");
        Add(Documents, "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp epub csv");
        Add(Music, "mp3 wav flac aac ogg m4a wma opus mid");
        Add(Programs, "exe msi msix appx apk dmg deb rpm bat jar");
        Add(Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts 3gp");
        Add(Pictures, "jpg jpeg png gif webp svg ico bmp avif tiff psd raw heic heif");
        return d;
    }

    public static string FromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return ext.Length > 0 && Map.TryGetValue(ext, out var c) ? c : General;
    }
}
