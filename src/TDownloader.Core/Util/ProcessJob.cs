using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TDownloader.Core.Util;

/// <summary>
/// Windows Job Object (KILL_ON_JOB_CLOSE): yt-dlp / ffmpeg gibi alt süreçler bu işe bağlanır; uygulama çökse veya
/// sonlansa bile işletim sistemi tutamacı kapatınca hepsini öldürür (yetim süreç kalmaz). Windows dışında hiçbir şey yapmaz.
/// </summary>
public static class ProcessJob
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private static readonly object Lock = new();
    private static IntPtr _job;
    private static bool _tried;

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    private static IntPtr GetJob()
    {
        lock (Lock)
        {
            if (_tried) return _job;
            _tried = true;
            if (!OperatingSystem.IsWindows()) return _job;
            try
            {
                var h = CreateJobObjectW(IntPtr.Zero, null);
                if (h == IntPtr.Zero) return _job;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, p, false);
                    if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, p, (uint)size)) return _job;   // tutamak süreç ömrü boyu açık kalır
                }
                finally { Marshal.FreeHGlobal(p); }
                _job = h;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
            return _job;
        }
    }

    /// <summary>Çalışan süreci işe bağlar. Başarısızlık (ör. Windows dışı) sessizce false döner; indirmeyi etkilemez.</summary>
    public static bool Assign(Process p)
    {
        try
        {
            var job = GetJob();
            return job != IntPtr.Zero && AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException) { return false; }
    }

    /// <summary>Süreç bu işin içinde mi (test/tanı).</summary>
    public static bool Contains(Process p)
    {
        try
        {
            var job = GetJob();
            return job != IntPtr.Zero && IsProcessInJob(p.Handle, job, out bool r) && r;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException) { return false; }
    }
}
