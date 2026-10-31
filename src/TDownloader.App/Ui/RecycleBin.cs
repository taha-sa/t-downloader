using System.Runtime.InteropServices;

namespace TDownloader.App.Ui;

/// <summary>
/// Dosyayı Geri Dönüşüm Kutusu'na gönderir (SHFileOperation, FOF_ALLOWUNDO | FOF_WANTNUKEWARNING). Dosya kutuya gönderilemiyorsa
/// (kutu kapasitesi, ağ/çıkarılabilir sürücü) kabuk "kalıcı silinsin mi?" diye AYRICA sorar; kullanıcı hayır derse dosya silinmez
/// (<see cref="RecycleOutcome.Skipped"/>). Asla sessizce kalıcı silinmez (A3).
/// </summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x3;
    private const ushort FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Dosya yoksa <see cref="RecycleOutcome.Missing"/>; hata iletisi <paramref name="error"/>.</summary>
    public static RecycleOutcome Send(string path, IntPtr owner, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) return RecycleOutcome.Missing;
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = path + "\0",                  // çift sonlandırıcı: marshaller bir tane daha ekler
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
            };
            int code = SHFileOperation(ref op);
            var outcome = DiskDeletePlan.Interpret(code, op.fAnyOperationsAborted, File.Exists(path));
            if (outcome == RecycleOutcome.Failed) error = $"Silme başarısız (kod 0x{code:X}): {Path.GetFileName(path)}";
            return outcome;
        }
        catch (Exception ex) { error = ex.Message; return RecycleOutcome.Failed; }
    }
}
