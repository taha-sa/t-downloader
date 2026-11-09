using System.Runtime.InteropServices;

namespace TDownloader.App.Ui;

/// <summary>
/// Dosya uzantısına göre Windows shell simgesi (SHGetFileInfo). Küçük simgeler ASENKRON yüklenir ve uzantı başına bir kez
/// önbelleğe alınır: IndexFor UI thread'inde asla shell çağrısı yapmaz (yer tutucu döner; simge hazır olunca IconsChanged tetiklenir).
/// </summary>
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
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private static SynchronizationContext? _uiCtx;
    private static SynchronizationContext? Ui => _uiCtx ??= SynchronizationContext.Current;

    /// <summary>Yeni bir simge ImageList'e eklenince (UI thread'inde) tetiklenir: liste yeniden boyanmalı.</summary>
    public event Action? IconsChanged;

    public ShellIcons()
    {
        Images.Images.Add(new Bitmap(Images.ImageSize.Width, Images.ImageSize.Height));   // 0: yer tutucu (boş)
    }

    private static string ExtOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length == 0 ? "." : ext;
    }

    /// <summary>UI thread'inden çağrılır. Önbellekte yoksa 0 (yer tutucu) döner ve arka planda yükler.</summary>
    public int IndexFor(string fileName)
    {
        var ext = ExtOf(fileName);
        if (_map.TryGetValue(ext, out var i)) return i;
        if (_pending.Add(ext))
        {
            _ = Task.Run(() =>
            {
                var bmp = LoadSmall(ext);
                void Apply()
                {
                    int idx = 0;
                    if (bmp != null) { Images.Images.Add(bmp); idx = Images.Images.Count - 1; }
                    _map[ext] = idx; _pending.Remove(ext);
                    IconsChanged?.Invoke();
                }
                var ui = Ui;
                if (ui != null) ui.Post(_ => Apply(), null);   // UI context yoksa ImageList'e dokunma (çapraz-thread yok)
            });
        }
        return 0;
    }

    private static Bitmap? LoadSmall(string ext)
    {
        try
        {
            var fi = new SHFILEINFO();
            SHGetFileInfo("x" + ext, 0x80, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (fi.hIcon == IntPtr.Zero) return null;
            try { using var ic = Icon.FromHandle(fi.hIcon); return ic.ToBitmap(); }
            finally { DestroyIcon(fi.hIcon); }
        }
        catch { return null; }
    }

    /// <summary>Uzantıya göre büyük (32x32) shell simgesi; bulunamazsa null. (Tek seferlik, pencere açılırken; satır başına değil.)</summary>
    public static Bitmap? Large(string fileName)
    {
        try
        {
            var ext = ExtOf(fileName);
            var fi = new SHFILEINFO();
            SHGetFileInfo("x" + ext, 0x80, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
            if (fi.hIcon == IntPtr.Zero) return null;
            try { using var ic = Icon.FromHandle(fi.hIcon); return ic.ToBitmap(); }
            finally { DestroyIcon(fi.hIcon); }
        }
        catch { return null; }
    }
}
