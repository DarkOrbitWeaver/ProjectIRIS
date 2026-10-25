using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CompanionOS.App.Interface;
using CompanionOS.App.Services;

namespace CompanionOS.App;

/// <summary>
/// Application shell. It creates the window, hands out the tray icon, and gets out of the way.
/// </summary>
public partial class App : Application
{
    private TrayIconHost? _trayHost;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The black box goes in before anything can hang: the heartbeat must already
            // be running when the main window is constructed, so even a freeze inside
            // the constructor is caught with stacks.
            SystemWatch.AttachUiHeartbeat();

            // The companion must not quit when the main window is hidden to the overlay,
            // so shutdown is always explicit.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _trayHost = new TrayIconHost();
            _trayHost.WakeRequested += () => WakeFromTray(desktop);
            _trayHost.QuitRequested += () => QuitFromTray(desktop);

            MainWindow mainWindow = new()
            {
                // Sleep asks for the tray icon before it tears anything down. If the icon
                // cannot be created she stays awake and resting instead of vanishing.
                EnterSleepHost = _trayHost.Show
            };

            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            SystemWatch.AttachWindow(mainWindow);
            SystemWatch.Trace("life", "main window shown — the visible boot is finished, runtime start continues");

            // Whatever ends the process, the icon must leave the notification area with it.
            desktop.Exit += (_, _) =>
            {
                SystemWatch.Trace("life", "application exit event — the shell is going down gracefully");
                _trayHost?.Dispose();
            };

            // Runtime startup is asynchronous and must never block the first paint.
            _ = mainWindow.InitializeCompanionAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// "Wake Iris" starts a brand-new process and lets this one exit.
    ///
    /// There is deliberately no attempt to resurrect the sleeping runtime: its audio
    /// devices, model host and perception loop were all disposed. A clean start is what
    /// makes waking reliable, and it costs about a second and a half.
    /// </summary>
    private void WakeFromTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _trayHost?.Hide();

        if (!TrayIconHost.TryRelaunch())
        {
            // Never leave her unreachable: if the relaunch failed, keep the tray icon so
            // the user can try again or quit deliberately.
            _trayHost?.Show();
            return;
        }

        desktop.Shutdown();
    }

    private void QuitFromTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _trayHost?.Dispose();
        desktop.Shutdown();
    }
}
