using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.App.Services;

/// <summary>
/// What is left of her when she is asleep.
///
/// Sleep means everything stops: the runtime is disposed, both windows are gone, the
/// microphone is closed, the speech model is freed and the database is checkpointed.
/// This tray icon is the only thing that stays, and it exists purely so "load it again"
/// is one click instead of finding the executable.
///
/// Waking starts a fresh process instead of reviving the old one on purpose. A process
/// that has torn down its audio devices, its HTTP clients and its model host has no
/// business pretending to be the same session afterwards; a clean start is both simpler
/// and what the user asked for.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const string IconUri = "avares://CompanionOS/Assets/iris.ico";

    private TrayIcon? _trayIcon;
    private bool _isDisposed;

    /// <summary>The user clicked the tray icon or chose "Wake Iris".</summary>
    public event Action? WakeRequested;

    /// <summary>The user chose "Quit". No process is left behind.</summary>
    public event Action? QuitRequested;

    /// <summary>True while the icon is on screen.</summary>
    public bool IsShown => _trayIcon is { IsVisible: true };

    /// <summary>
    /// Puts her in the notification area. Returns false when the platform or the icon
    /// could not be created, which the caller uses to fall back to the overlay pill —
    /// an unreachable companion is worse than a slightly larger footprint.
    /// </summary>
    public bool Show()
    {
        if (_isDisposed)
        {
            return false;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = true;
            return true;
        }

        Application? application = Application.Current;
        if (application is null)
        {
            return false;
        }

        try
        {
            TrayIcon trayIcon = new()
            {
                ToolTipText = "Iris is asleep. Right-click to wake her.",
                Icon = LoadIcon(),
                IsVisible = true,
                Menu = BuildMenu()
            };

            // Clicking the icon itself (not the menu) is the fastest way back.
            trayIcon.Clicked += (_, _) => WakeRequested?.Invoke();

            // The icons collection is an attached property on the Application, which is
            // where Avalonia looks for tray icons to host.
            TrayIcon.SetIcons(application, new TrayIcons { trayIcon });

            _trayIcon = trayIcon;
            CompanionLog.Info("App", "Sleeping. Her tray icon is the only thing still running.");
            return true;
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("App", $"The tray icon could not be created: {exception.Message}");
            return false;
        }
    }

    /// <summary>Removes the icon. Used when she wakes up or quits.</summary>
    public void Hide()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.IsVisible = false;
    }

    /// <summary>
    /// Starts a fresh companion process. Returns false when the executable cannot be
    /// determined, so the caller can at least leave the user a clear message instead of
    /// silently doing nothing.
    /// </summary>
    public static bool TryRelaunch()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            ProcessStartInfo startInfo;

            if (!string.IsNullOrWhiteSpace(processPath) &&
                !Path.GetFileNameWithoutExtension(processPath)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                startInfo = new ProcessStartInfo(processPath) { UseShellExecute = true };
            }
            else
            {
                // Framework-dependent launch (`dotnet CompanionOS.dll`); reuse the host
                // but point it at our assembly.
                string location = Assembly.GetEntryAssembly()?.Location ?? string.Empty;
                if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(processPath))
                {
                    return false;
                }

                startInfo = new ProcessStartInfo(processPath)
                {
                    UseShellExecute = true,
                    ArgumentList = { location }
                };
            }

            return Process.Start(startInfo) is not null;
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("App", $"Could not start a new process: {exception.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.IsVisible = false;

                if (Application.Current is { } application)
                {
                    TrayIcon.SetIcons(application, null);
                }

                // Disposal releases the native handle. Done through the interface so this
                // file does not depend on the exact signature Avalonia ships.
                (_trayIcon as IDisposable)?.Dispose();
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("App", $"Tray icon cleanup failed: {exception.Message}");
        }

        _trayIcon = null;
    }

    private NativeMenu BuildMenu()
    {
        NativeMenuItem wakeItem = new() { Header = "Wake Iris" };
        wakeItem.Click += (_, _) => WakeRequested?.Invoke();

        NativeMenuItem quitItem = new() { Header = "Quit" };
        quitItem.Click += (_, _) => QuitRequested?.Invoke();

        NativeMenu menu = new();
        menu.Items.Add(wakeItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);
        return menu;
    }

    /// <summary>Her icon, embedded in the assembly. Null only if the asset is missing.</summary>
    private static WindowIcon? LoadIcon()
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch (Exception exception)
        {
            // The tray still works without an icon; it is not worth failing over.
            CompanionLog.Trace("App", $"Tray icon asset missing: {exception.Message}");
            return null;
        }
    }
}
