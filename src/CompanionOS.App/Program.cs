using Avalonia;

namespace CompanionOS.App;

/// <summary>
/// Entry point. Avalonia needs this shape, and CompanionOS needs nothing else here:
/// the real wiring lives in CompanionRuntime and the interface lives in MainWindow.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The black box goes in first so even the earliest possible death leaves a record.
        SystemWatch.InstallStatic(args);

        // A process that dies silently teaches nobody anything. Every crash, anywhere,
        // lands in the event diary with its stack — the one source of truth for fixing her.
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            Core.Diagnostics.IrisEvent.Log("App", "crash", Core.Diagnostics.LogLevel.Error,
                message: eventArgs.IsTerminating ? "A crash is terminating her." : "A background fault was caught.",
                data: new Dictionary<string, string?>
                {
                    ["exception"] = eventArgs.ExceptionObject.ToString()
                });

            SystemWatch.Fault("domain", eventArgs.ExceptionObject as Exception,
                eventArgs.IsTerminating ? "terminating" : "background fault caught");
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Core.Diagnostics.IrisEvent.Log("App", "task_fault", Core.Diagnostics.LogLevel.Warning,
                message: eventArgs.Exception.Message,
                data: new Dictionary<string, string?>
                {
                    ["exception"] = eventArgs.Exception.ToString()
                });
            eventArgs.SetObserved();

            SystemWatch.Fault("task", eventArgs.Exception, "unobserved task fault");
        };

        int exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        SystemWatch.Trace("life", $"process exit code={exitCode} — a graceful end always writes this line");
        return exitCode;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .With(new Win32PlatformOptions
            {
                // Software rendering frees a few megabytes of VRAM when every megabyte
                // matters for the model. Avalonia picks the GPU path by default.
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software }
            });
}
