using System.Runtime.InteropServices;

namespace TDownloader.App.Ui;

/// <summary>Dosya uzantısına göre Windows shell simgesi (SHGetFileInfo).</summary>
public sealed class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO fi, uint cb, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10;

    public ImageList Images { get; } = new() { ColorDepth = ColorDepth.Depth32Bit, ImageSize = SystemInformation.SmallIconSize };
    private readonly Dictionary<string, int> _map = new(StringComparer.OrdinalIgnoreCase);

    public int IndexFor(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length == 0) ext = ".";
        if (_map.TryGetValue(ext, out var i)) return i;
        int idx;
        try
        {
            var fi = new SHFILEINFO();
            SHGetFileInfo("x" + ext, 0x80, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (fi.hIcon != IntPtr.Zero)
            {
                using var ic = Icon.FromHandle(fi.hIcon);
                Images.Images.Add(ic.ToBitmap());
                DestroyIcon(fi.hIcon);
            }
            else Images.Images.Add(new Bitmap(16, 16));
        }
        catch { Images.Images.Add(new Bitmap(16, 16)); }
        idx = Images.Images.Count - 1;
        _map[ext] = idx;
        return idx;
    }
}
