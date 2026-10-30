using TDownloader.App.Forms;
using TDownloader.App.Services;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.App;

internal static class Program
{
    private const string MutexName = @"Local\TDownloader.SingleInstance";
    private const string ShowEventName = @"Local\TDownloader.ShowWindow";

    [STAThread]
    private static void Main(string[] args)
    {
        bool background = args.Any(a => string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase));
        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // İkinci örnek: mevcut pencereyi öne getirt (arka plan başlatmada sessizce çık).
            if (!background)
            {
                try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch (Exception) { }
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new Font("Tahoma", 8f));

        var paths = AppPaths.Default;
        var settings = AppSettings.Load(paths.SettingsFile);
        var manager = new DownloadManager(paths, settings);
        manager.Load();

        var svc = new AppServices { Manager = manager, Paths = paths };
        var form = new MainForm(svc, background);
        svc.Main = form;
        var ipc = new IpcCoordinator(svc);
        ipc.Start();

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var cts = new CancellationTokenSource();
        var waiter = new Thread(() =>
        {
            var handles = new[] { showEvent, cts.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                try { form.BeginInvoke(form.ShowMain); } catch (Exception) { }
            }
        }) { IsBackground = true, Name = "ShowEventWaiter" };
        waiter.Start();

        Application.Run(form);

        cts.Cancel();
        svc.Media.Detach();
        try { ipc.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { }
        try { manager.ShutdownAsync().Wait(10000); } catch (Exception) { }
    }
}
