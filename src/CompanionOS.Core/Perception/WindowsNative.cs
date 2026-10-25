using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core.Perception;

/// <summary>
/// The thin layer between CompanionOS and Windows.
///
/// Everything here is a read-only query or a harmless measurement: which window is
/// in front, how long the keyboard has been quiet, how much memory is free. No input
/// is synthesised and nothing is changed, so this file can never surprise the user.
///
/// Each call is wrapped so a Windows update or a protected window cannot throw into
/// her thinking loop; a failed probe simply returns a neutral value.
/// </summary>
public static class WindowsNative
{
    /// <summary>What is in front of the user right now.</summary>
    public sealed class ForegroundWindowInfo
    {
        public string WindowTitle { get; init; } = string.Empty;
        public string ProcessName { get; init; } = string.Empty;
        public string ApplicationName { get; init; } = string.Empty;
        public int ProcessId { get; init; }
    }

    /// <summary>Reads the active window's title and owning process.</summary>
    public static ForegroundWindowInfo ReadForegroundWindow()
    {
        try
        {
            IntPtr handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return new ForegroundWindowInfo();
            }

            int titleLength = GetWindowTextLength(handle);
            StringBuilder titleBuilder = new(Math.Max(titleLength + 1, 256));
            GetWindowText(handle, titleBuilder, titleBuilder.Capacity);

            GetWindowThreadProcessId(handle, out uint processId);
            string processName = string.Empty;
            string applicationName = string.Empty;

            if (processId > 0)
            {
                using Process process = Process.GetProcessById((int)processId);
                processName = process.ProcessName + ".exe";
                applicationName = process.ProcessName;
            }

            return new ForegroundWindowInfo
            {
                WindowTitle = titleBuilder.ToString(),
                ProcessName = processName,
                ApplicationName = applicationName,
                ProcessId = (int)processId
            };
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Senses", $"Foreground window unreadable: {exception.Message}");
            return new ForegroundWindowInfo();
        }
    }

    /// <summary>Seconds since the last keyboard or mouse input.</summary>
    public static double ReadIdleSeconds()
    {
        try
        {
            LastInputInfo lastInput = new() { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref lastInput))
            {
                return 0;
            }

            return (Environment.TickCount64 - lastInput.Time) / 1000.0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Physical memory in megabytes.</summary>
    public static (double UsedMegabytes, double TotalMegabytes) ReadMemoryUsage()
    {
        try
        {
            MemoryStatus memoryStatus = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memoryStatus))
            {
                return (0, 0);
            }

            double total = memoryStatus.TotalPhysical / 1024.0 / 1024.0;
            double available = memoryStatus.AvailablePhysical / 1024.0 / 1024.0;
            return (total - available, total);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>Battery percentage and whether mains power is connected.</summary>
    public static (int Percent, bool OnAcPower) ReadPower()
    {
        try
        {
            if (!GetSystemPowerStatus(out SystemPowerStatus status))
            {
                return (100, true);
            }

            bool onAc = status.AlternateLineStatus == 1;
            int percent = status.BatteryLifePercent == 255 ? 100 : status.BatteryLifePercent;
            return (percent, onAc);
        }
        catch
        {
            return (100, true);
        }
    }

    /// <summary>Screen bounds of the primary display, in physical pixels.</summary>
    public static (int Width, int Height) ReadPrimaryScreenSize() => (GetSystemMetrics(0), GetSystemMetrics(1));

    /// <summary>
    /// Reads CPU load from the two cumulative system-time counters.
    /// Kept as a small class because it needs the previous sample to compute a delta.
    /// </summary>
    public sealed class CpuLoadSampler
    {
        private ulong _previousIdle;
        private ulong _previousKernel;
        private ulong _previousUser;
        private bool _hasPreviousSample;

        public double ReadPercent()
        {
            try
            {
                if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
                {
                    return 0;
                }

                ulong idleTicks = ToUInt64(idle);
                ulong kernelTicks = ToUInt64(kernel);
                ulong userTicks = ToUInt64(user);

                if (!_hasPreviousSample)
                {
                    _previousIdle = idleTicks;
                    _previousKernel = kernelTicks;
                    _previousUser = userTicks;
                    _hasPreviousSample = true;
                    return 0;
                }

                ulong idleDelta = idleTicks - _previousIdle;
                ulong kernelDelta = kernelTicks - _previousKernel;
                ulong userDelta = userTicks - _previousUser;

                _previousIdle = idleTicks;
                _previousKernel = kernelTicks;
                _previousUser = userTicks;

                // Kernel time includes idle time, hence the subtraction.
                ulong totalDelta = kernelDelta + userDelta;
                if (totalDelta == 0)
                {
                    return 0;
                }

                double busy = (double)(totalDelta - idleDelta) / totalDelta;
                return Math.Clamp(busy * 100.0, 0, 100);
            }
            catch
            {
                return 0;
            }
        }

        private static ulong ToUInt64(FileTime fileTime) =>
            ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AlternateLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder textBuffer, int maximumCharacters);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInput);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memoryStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus powerStatus);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
