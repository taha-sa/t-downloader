using TDownloader.Core.Ipc;
using TDownloader.Host;

// stdout YALNIZCA native messaging çerçeveleri içindir; Console.Write* kullanılmaz.
string pipe = IpcSerializer.PipeName;
string? appExe = null, logPath = HostLog.DefaultPath();
int startTimeoutMs = 10_000;
string? origin = null;
string? inbox = InboxSpool.DirOf(TDownloader.Core.Settings.AppPaths.Default.Root);

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
        case "--app" when i + 1 < args.Length: appExe = args[++i]; break;
        case "--inbox" when i + 1 < args.Length: inbox = args[++i]; break;
        case "--log" when i + 1 < args.Length: logPath = args[++i]; break;
        case "--start-timeout-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t): startTimeoutMs = t; i++; break;
        default:
            if (args[i].StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) origin = args[i];
            break;
    }
}

var log = new HostLog(logPath);
log.Write($"Host başladı. origin={origin ?? "(yok)"} args=[{string.Join(' ', args)}]");

appExe ??= Path.Combine(AppContext.BaseDirectory, "TDownloader.exe");
var runner = new HostRunner(
    new HostOptions(pipe, appExe, TimeSpan.FromMilliseconds(startTimeoutMs), TimeSpan.FromSeconds(30), inbox),
    Console.OpenStandardInput(), Console.OpenStandardOutput(), log);

return await runner.RunAsync();
