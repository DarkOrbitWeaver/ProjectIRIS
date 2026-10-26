using System.Diagnostics;
using System.Runtime.InteropServices;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Perception;

/// <summary>
/// Reads the graphics card's memory usage through nvidia-smi, the one tool that is
/// already installed with every NVIDIA driver.
///
/// If the tool is missing, or the machine has no NVIDIA card, the probe simply reports
/// zero and the interface shows a dash instead of inventing a number. Honest telemetry
/// matters here: the whole point of the meter is knowing whether her brain actually
/// left the GPU.
///
/// One safety rail learned from a real occurrence: every few thousand launches,
/// Windows can hand nvidia-smi a failed initialisation (status 0xC0000142) instead of
/// a reading. The probe treats that as "no reading this cycle" — the next probe a few
/// seconds later tries again — and the hard-error dialog Windows otherwise draws for
/// a failed child process is suppressed while we fork, so a telemetry glitch never
/// becomes an owner-visible "nvidia-smi.exe — Application Error" window.
/// </summary>
public sealed class GpuMemoryProbe
{
    /// <summary>kernel32!SetErrorMode flags: keep DLL-init, fault and legacy-file dialogs silent.</summary>
    private const int SemFailCriticalErrors = 0x0001;
    private const int SemNoGpFaultErrorBox = 0x0002;
    private const int SemNoOpenFileErrorBox = 0x8000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetErrorMode(int newMode);

    private readonly object _probeLock = new();
    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;
    private (double UsedMegabytes, double TotalMegabytes) _cachedReading;

    /// <summary>True when the last probe found a working card.</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Returns used and total VRAM in megabytes. Cached for a few seconds because
    /// starting a process is expensive compared to reading her own state.
    /// </summary>
    public (double UsedMegabytes, double TotalMegabytes) Read(int cacheSeconds = 4)
    {
        lock (_probeLock)
        {
            if (DateTimeOffset.Now - _lastProbe < TimeSpan.FromSeconds(cacheSeconds))
            {
                return _cachedReading;
            }

            _lastProbe = DateTimeOffset.Now;
            _cachedReading = Probe();
            IsAvailable = _cachedReading.TotalMegabytes > 0;
            return _cachedReading;
        }
    }

    private static (double UsedMegabytes, double TotalMegabytes) Probe()
    {
        // The error mode is inherited by the child process, so setting it before the
        // fork — and restoring the machine's own choice after — keeps a dying
        // nvidia-smi from painting a dialog on the owner's screen.
        int previousErrorMode = SetErrorMode(
            SemFailCriticalErrors | SemNoGpFaultErrorBox | SemNoOpenFileErrorBox);
        try
        {
            return RunProbe();
        }
        finally
        {
            SetErrorMode(previousErrorMode);
        }
    }

    private static (double UsedMegabytes, double TotalMegabytes) RunProbe()
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = "nvidia-smi",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("--query-gpu=memory.used,memory.total");
            startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);

            string firstLine = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? string.Empty;

            string[] parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], out double used) ||
                !double.TryParse(parts[1], out double total))
            {
                return (0, 0);
            }

            return (used, total);
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"nvidia-smi unavailable: {exception.Message}");
            return (0, 0);
        }
    }
}
