using System.Runtime.InteropServices;
using System.Text;
using TDownloader.Core.Ipc;

namespace TDownloader.App.Services;

/// <summary>
/// Eklentinin bildirdiği browserWindow ile gerçek Chrome/Edge üst düzey penceresini eşleştirir
/// (chrome.windows değerleri DIP olabildiğinden ölçek denenir) ve görünürlük/örtülme kontrolü sağlar.
/// Eşleşme yoksa eklentinin verdiği koordinatlar olduğu gibi kullanılır.
/// </summary>
public static class BrowserWindows
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int X, Y; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    private const uint GA_ROOT = 2;

    public readonly record struct Match(IntPtr Hwnd, WindowRectInfo Rect);

    private static readonly double[] Scales = { 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 };

    public static Match? Locate(WindowRectInfo reported)
    {
        Match? best = null;
        double bestErr = double.MaxValue;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            var sb = new StringBuilder(64);
            GetClassName(h, sb, 64);
            if (sb.ToString() != "Chrome_WidgetWin_1") return true;
            if (!GetWindowRect(h, out var r)) return true;
            int w = r.R - r.L, hh = r.B - r.T;
            if (w < 200 || hh < 150) return true;
            foreach (var k in Scales)
            {
                double err = Math.Abs(r.L - reported.Left * k) + Math.Abs(r.T - reported.Top * k) +
                             Math.Abs(w - reported.Width * k) + Math.Abs(hh - reported.Height * k);
                if (err < bestErr) { bestErr = err; best = new Match(h, new WindowRectInfo(r.L, r.T, w, hh)); }
            }
            return true;
        }, IntPtr.Zero);
        return bestErr <= 80 ? best : null;
    }

    public static bool IsUsable(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd);

    /// <summary>
    /// (x,y) noktasında üstte gösterilen pencere tarayıcı penceresi mi (ya da bu uygulamanın kendisi mi)?
    /// Başka bir uygulamanın penceresi tarayıcıyı örtüyorsa false.
    /// </summary>
    public static bool IsBrowserOnTopAt(IntPtr browser, int x, int y)
    {
        var h = WindowFromPoint(new PT { X = x, Y = y });
        if (h == IntPtr.Zero) return true;
        var root = GetAncestor(h, GA_ROOT);
        if (root == browser) return true;
        GetWindowThreadProcessId(root, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
