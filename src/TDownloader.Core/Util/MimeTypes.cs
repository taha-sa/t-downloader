namespace TDownloader.Core.Util;

/// <summary>Uzantıdan kaba MIME türü (T-Link download.completed olayı için); bilinmeyen = application/octet-stream.</summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".zip"] = "application/zip", [".rar"] = "application/vnd.rar", [".7z"] = "application/x-7z-compressed",
        [".gz"] = "application/gzip", [".tar"] = "application/x-tar", [".iso"] = "application/x-iso9660-image",
        [".exe"] = "application/vnd.microsoft.portable-executable", [".msi"] = "application/x-msi",
        [".pdf"] = "application/pdf", [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".mp3"] = "audio/mpeg", [".flac"] = "audio/flac", [".wav"] = "audio/wav", [".m4a"] = "audio/mp4",
        [".mp4"] = "video/mp4", [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo", [".mov"] = "video/quicktime",
        [".wmv"] = "video/x-ms-wmv", [".webm"] = "video/webm", [".flv"] = "video/x-flv", [".m4v"] = "video/x-m4v",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".txt"] = "text/plain", [".html"] = "text/html", [".json"] = "application/json",
    };

    public static string FromFileName(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "");
        return ext.Length > 0 && Map.TryGetValue(ext, out var m) ? m : "application/octet-stream";
    }
}
