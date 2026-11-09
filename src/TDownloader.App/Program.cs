using TDownloader.App.Forms;
using TDownloader.App.Services;
using TDownloader.Core.Queue;
using TDownloader.Core.Settings;

namespace TDownloader.App;

internal static class Program
{
    // TDOWNLOADER_PIPE: test/yalıtım için ayrı örnek (mutex, olay ve pipe adı soneki).
    private static readonly string Sfx = Environment.GetEnvironmentVariable("TDOWNLOADER_PIPE") ?? "";
    private static readonly string MutexName = @"Local\TDownloader.SingleInstance" + Sfx;
    private static readonly string ShowEventName = @"Local\TDownloader.ShowWindow" + Sfx;

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

                // Kullanıcıya "zaten açık" uyarısı (görünmez TopMost sahip form ile en üstte).
                ApplicationConfiguration.Initialize();   // yüksek DPI + görsel stiller (ana yolla aynı) (A28)
                using var owner = new Form
                {
                    TopMost = true,
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-32000, -32000),
                    Size = new Size(1, 1),
                    Opacity = 0
                };
                owner.Show();
                MessageBox.Show(owner, "T-Downloader zaten açık.", "T-Downloader",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetDefaultFont(new Font("Tahoma", 8f));

        var paths = AppPaths.Default;
        var settings = AppSettings.Load(paths.SettingsFile);
        var manager = new DownloadManager(paths, settings);
        manager.Load();

        var yt = new Core.Video.YtDlpClient(() => manager.Settings, paths);
        manager.ExternalRunner = new TDownloader.Core.Video.VideoRunner(yt);
        var svc = new AppServices { Manager = manager, Paths = paths };
        var form = new MainForm(svc, background);
        svc.Main = form;
        svc.Media = new VideoOverlayMediaSink(yt);
        svc.Media.Attach(svc);
        var ipc = new IpcCoordinator(svc);
        ipc.Start();
        var link = new LinkService(svc);
        link.Start();
        new UpdateService(svc, link.PublishUpdateAvailable).ScheduleCheck();

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
        svc.FlushSettings();
        svc.Media.Detach();
        try { link.DisposeAsync().AsTask().Wait(1500); } catch (Exception) { }
        try { ipc.DisposeAsync().AsTask().Wait(2000); } catch (Exception) { }
        try { manager.ShutdownAsync().Wait(10000); } catch (Exception) { }
    }
}
