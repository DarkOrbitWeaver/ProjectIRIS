using CompanionOS.Core.Diagnostics;
using Microsoft.Win32;

namespace CompanionOS.App;

/// <summary>
/// Windows start-up, wired to the truth: the HKCU Run key is the single source of
/// reality. The settings switch never claims a start-up entry that does not exist —
/// the switch is written only after the registry accepted the change, and every read
/// of the truth comes straight from the registry, so hand-deleting her entry there
/// leaves this screen saying off, honestly.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CompanionOS";

    /// <summary>True when a Run entry exists, whatever the settings file says.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string path && !string.IsNullOrWhiteSpace(path);
        }
        catch (Exception exception)
        {
            // A denied registry read used to look identical to "not installed" - the
            // screen showed off and the user trusted it (2026-10-27 audit).
            CompanionLog.Warning("App", $"Could not read the startup registry: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Writes or removes the Run entry, pointing at this very executable, then reads the
    /// value back before claiming success. Returns false on any refusal.
    /// </summary>
    public static bool TrySet(bool enable)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

            if (enable)
            {
                string? executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return IsEnabled() == enable;
        }
        catch (Exception exception)
        {
            // Claiming nothing while leaving a reason on record: "auto start failed"
            // with no cause was undiscoverable (2026-10-27 audit).
            CompanionLog.Warning("App", $"Could not change the startup entry: {exception.Message}");
            return false;
        }
    }
}
