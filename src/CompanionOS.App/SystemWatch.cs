using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Perception;

namespace CompanionOS.App;

/// <summary>
/// The OS black box. CompanionOS is asked to behave like a desktop environment, so it
/// carries a desktop-environment diagnostic: a dedicated journal of everything that can
/// produce a "it just froze" report from the user chair.
///
/// It records, into its own file (logs\system-YYYYMMDD.jsonl, separate from companion.log
/// and the iris event diary):
///   • boot milestones with timings (the ctor, the async runtime start, the window show),
///   • a UI-thread heartbeat and the cost of each sync frame, so a sluggish pump shows
///     up as numbers instead of a mystery,
///   • every stall: when the UI thread stopped pumping, for how long, and — once the
///     stall passes 5 seconds — full thread stacks captured out of process, which name
///     the exact blocking call,
///   • every view switch, overlay toggle and window move the user makes,
///   • the process exit code, so a run without that line is a known hard-kill.
///
/// Design rules: it flushes every line the moment it is written (a hard kill must never
/// lose evidence), it never touches the UI thread beyond the heartbeat it already owns,
/// and any failure inside the watch itself is swallowed — the black box must never be
/// the thing that breaks the machine it is watching.
/// </summary>
public static class SystemWatch
{
    private static readonly object Gate = new();
    private static readonly Queue<string> RecentLines = new();

    private static long _lastHeartbeat;
    private static long _stallBegan;
    private static bool _hungReported;
    private static bool _stackRequested;
    private static long _lastTickTimestamp;
    private static int _ticksSincePumpLine;
    private static double _syncEwmaMs;
    private static bool _stackToolWarned;
    private static RollingJsonLinesWriter? _logWriter;
    private static readonly WindowsNative.CpuLoadSampler HostCpuSampler = new();
    private static long _lastHostSnapshotTick;
    private static double _lastProcessCpuMilliseconds;

    public static event Action<string>? LineWritten;

    public static IReadOnlyList<string> Recent(int maximumLines = 200)
    {
        lock (Gate)
        {
            return RecentLines.Skip(Math.Max(0, RecentLines.Count - maximumLines)).ToArray();
        }
    }

    /// <summary>Called once at the very top of Main, before anything else runs.</summary>
    public static void InstallStatic(string[] args)
    {
        try
        {
            CompanionPaths.EnsureCreated();
            _logWriter = new RollingJsonLinesWriter(CompanionPaths.LogsDirectory, "system", 8L * 1024 * 1024);
        }
        catch (Exception exception)
        {
            // A blocked or full data drive should leave Iris usable, while making the loss
            // of on-disk diagnostics visible in the in-memory OS watch.
            Trace("diagnostics", "system log unavailable", exception.GetType().Name + ": " + exception.Message);
        }
        Trace("life", "process start", $"pid={Environment.ProcessId} argument_count={args.Length}");
        Trace("life", $"boot.uptime_before_ui={Environment.TickCount64}ms");
        TraceHostEnvironment();
        RecordHostSnapshot("startup");
    }

    /// <summary>
    /// Starts the heartbeat and the hang watchdog. Must be called on the UI thread before
    /// the main window is constructed, so a hang inside the constructor is covered too.
    /// </summary>
    public static void AttachUiHeartbeat()
    {
        // A fault on the UI thread used to kill her silently. Now it is recorded and the
        // shell stays alive — a desktop environment heals, it does not evaporate.
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            Fault("ui", eventArgs.Exception, "unhandled on the UI thread");
            eventArgs.Handled = true;
        };

        _lastHeartbeat = Environment.TickCount64;
        _lastTickTimestamp = _lastHeartbeat;

        DispatcherTimer heartbeat = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, HeartbeatTick);
        heartbeat.Start();

        _ = Task.Run(WatchdogLoop);
        Trace("ui", "watchdog armed", "heartbeat=250ms stall_threshold=1500ms stack_capture_after=5000ms");
    }

    /// <summary>Window lifecycle taps, wired once the window exists.</summary>
    public static void AttachWindow(Window window)
    {
        window.Opened += (_, _) =>
        {
            Trace("life", "window shown",
                $"pos={window.Position.X},{window.Position.Y} size={window.Width:0}x{window.Height:0} " +
                $"scale={(window.RenderScaling).ToString("0.0#", CultureInfo.InvariantCulture)}");
        };
        window.PositionChanged += (_, _) =>
            Trace("life", "window moved", $"pos={window.Position.X},{window.Position.Y}");
        window.Closed += (_, _) => Trace("life", "window closed");
    }

    /// <summary>The UI thread writes one of these every sync frame; the value joins the pump line.</summary>
    public static void RecordSync(double milliseconds)
    {
        _syncEwmaMs = _syncEwmaMs <= 0 ? milliseconds : _syncEwmaMs * 0.8 + milliseconds * 0.2;
    }

    /// <summary>
    /// Diagnostic self-test: deliberately blocks the UI thread for 8.5 seconds. A human or
    /// an automation layer that suspects the watchdog can press Ctrl+Alt+Shift+F5 and then
    /// read this file — it must show ui.hung, a stack capture, and a stall that ends.
    /// </summary>
    public static void SelfTestStall()
    {
        Trace("selftest", "ui.selftest begin — the UI thread will be blocked for 8.5s");
        Thread.Sleep(8500);
        Trace("selftest", "ui.selftest end");
    }

    public static void Trace(string area, string message, string? data = null)
    {
        try
        {
            string line = Encode(area, message, data);
            lock (Gate)
            {
                RecentLines.Enqueue(line);
                while (RecentLines.Count > 600) RecentLines.Dequeue();
                if (_logWriter is not null)
                {
                    _logWriter.Append(DateTimeOffset.Now, line);
                }
            }
            try { LineWritten?.Invoke(line); }
            catch (Exception) { /* observers must not interfere with the process watch */ }
        }
        catch (OutOfMemoryException)
        {
            throw;
        }
        catch (Exception)
        {
            // The black box must never break the machine it is watching.
        }
    }

    public static void Fault(string area, Exception? exception, string context = "")
    {
        if (exception is null)
        {
            return;
        }

        Trace(area, "exception", context + (string.IsNullOrEmpty(context) ? "" : " | ") +
            exception.GetType().Name + ": " + exception.Message + Environment.NewLine +
            Clean(exception.StackTrace));
    }

    private static void HeartbeatTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        long period = now - _lastTickTimestamp;
        Volatile.Write(ref _lastHeartbeat, now);
        _lastTickTimestamp = now;

        if (_hungReported)
        {
            long stall = now - Interlocked.Read(ref _stallBegan);
            _hungReported = false;
            Trace("ui", "ui.stall ended", $"blocked_for={stall}ms — the UI thread is pumping again");
        }

        if (_stackRequested)
        {
            _stackRequested = false;
            Trace("ui", "stack capture finished while hung state lingered", null);
        }

        _ticksSincePumpLine++;
        if (_ticksSincePumpLine >= 16)
        {
            _ticksSincePumpLine = 0;
            Trace("ui", "ui.pump alive",
                $"tick_period={period}ms sync_ewma={_syncEwmaMs.ToString("0.0", CultureInfo.InvariantCulture)}ms");
        }

        if (period > 700)
        {
            Trace("ui", "ui.tick slow but pumping", $"tick_period={period}ms (heartbeat=250ms) — frame cost, not a hang");
        }
    }

    private static async Task WatchdogLoop()
    {
        long lastSnapshot = Environment.TickCount64;
        while (true)
        {
            try
            {
                await Task.Delay(400).ConfigureAwait(false);

                long now = Environment.TickCount64;
                if (now - lastSnapshot >= 30_000)
                {
                    lastSnapshot = now;
                    RecordHostSnapshot("periodic");
                }

                long last = Volatile.Read(ref _lastHeartbeat);
                long stall = now - last;

                if (stall >= 1500 && !_hungReported)
                {
                    _hungReported = true;
                    Interlocked.Exchange(ref _stallBegan, last);
                    Trace("ui", "ui.hung", $"the UI thread stopped pumping {stall}ms ago — watching");
                }

                if (stall >= 5000 && !_stackRequested)
                {
                    _stackRequested = true;
                    Trace("ui", "ui.hung continues", $"stall={stall}ms — requesting thread stacks for the real call site");
                    _ = Task.Run(CaptureStacks);
                }
            }
            catch (OutOfMemoryException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace("ui", "watchdog iteration failed", exception.Message);
            }
        }
    }

    private static void TraceHostEnvironment()
    {
        try
        {
            string osVersion = Environment.OSVersion.Version.ToString();
            Trace("host", "host.environment",
                $"os={osVersion} os_arch={RuntimeInformation.OSArchitecture} " +
                $"process_arch={RuntimeInformation.ProcessArchitecture} runtime={RuntimeInformation.FrameworkDescription} " +
                $"logical_cpus={Environment.ProcessorCount} page_size={Environment.SystemPageSize}");
        }
        catch (Exception exception)
        {
            Trace("host", "host.environment unavailable", exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Captures inexpensive host pressure every 30 seconds. This uses Windows' in-process
    /// counters and process properties; it does not launch nvidia-smi or touch the GPU.
    /// </summary>
    private static void RecordHostSnapshot(string reason)
    {
        try
        {
            long now = Environment.TickCount64;
            double cpuPercent = HostCpuSampler.ReadPercent();
            (double ramUsed, double ramTotal) = WindowsNative.ReadMemoryUsage();
            using Process process = Process.GetCurrentProcess();
            double processCpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds;
            long previousTick = Interlocked.Exchange(ref _lastHostSnapshotTick, now);
            double previousCpu = Interlocked.Exchange(ref _lastProcessCpuMilliseconds, processCpuMilliseconds);
            double elapsedMilliseconds = Math.Max(1, now - previousTick);
            double processCpuPercent = previousTick == 0 ? 0 : Math.Clamp(
                (processCpuMilliseconds - previousCpu) / elapsedMilliseconds / Environment.ProcessorCount * 100,
                0, 100);

            Trace("host", "host.snapshot",
                $"reason={reason} total_cpu={cpuPercent:0.0}% ram_used_mb={ramUsed:0} ram_total_mb={ramTotal:0} " +
                $"iris_cpu_all_cores={processCpuPercent:0.0}% iris_working_set_mb={process.WorkingSet64 / 1024d / 1024d:0} " +
                $"iris_private_mb={process.PrivateMemorySize64 / 1024d / 1024d:0} uptime_ms={now}");
        }
        catch (Exception exception)
        {
            Trace("host", "host.snapshot unavailable", exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void CaptureStacks()
    {
        try
        {
            string stackPath = Path.Combine(Path.GetDirectoryName(LogPath())!,
                $"stack-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            ProcessStartInfo startInfo = new()
            {
                FileName = "dotnet-stack",
                Arguments = $"report --process-id {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process process = new() { StartInfo = startInfo };
            process.OutputDataReceived += (_, e) => AppendCapture(stackPath, e.Data);
            process.ErrorDataReceived += (_, e) => AppendCapture(stackPath, e.Data);

            if (!process.Start())
            {
                Trace("ui", "stack capture failed", "dotnet-stack could not start");
                _stackToolWarned = true;
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process.WaitForExit(75_000))
            {
                Trace("ui", "stack capture saved", Path.GetFileName(stackPath) + " — find the 'UI thread' entry");
            }
            else
            {
                process.Kill(entireProcessTree: true);
                Trace("ui", "stack capture timed out", Path.GetFileName(stackPath) + " (partial content kept)");
            }
        }
        catch (Exception exception)
        {
            if (!_stackToolWarned)
            {
                _stackToolWarned = true;
                Trace("ui", "stack capture unavailable", exception.Message);
            }
        }
    }

    private static void AppendCapture(string path, string? line)
    {
        try
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // Never make evidence collection itself a crash site.
        }
    }

    private static string LogPath() => _logWriter?.ActivePath ??
        Path.Combine(CompanionPaths.LogsDirectory, $"system-{DateTime.Now:yyyyMMdd}.jsonl");

    private static string Encode(string area, string message, string? data)
    {
        StringBuilder builder = new(96);
        // The timestamp carries its UTC offset. IrisEvent lines in the same folder are
        // offset-stamped; a bare local time here made the two diaries disagree about
        // WHEN things happened whenever the clocks of two machines were compared
        // across a DST change (2026-10-27 audit).
        builder.Append(CultureInfo.InvariantCulture, $"{{\"ts\":\"{DateTimeOffset.Now:yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffzzz}\",\"pid\":{Environment.ProcessId}");
        builder.Append(CultureInfo.InvariantCulture, $",\"area\":\"{Escape(area)}\"");
        builder.Append(CultureInfo.InvariantCulture, $",\"msg\":\"{Escape(message)}\"");
        if (!string.IsNullOrEmpty(data))
        {
            builder.Append(CultureInfo.InvariantCulture, $",\"data\":\"{Escape(data)}\"");
        }
        builder.Append('}');
        return builder.ToString();
    }

    private static string Escape(string value)
    {
        StringBuilder builder = new(value.Length + 8);
        foreach (char character in value)
        {
            switch (character)
            {
                case '\"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (character < 32)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        return builder.ToString();
    }

    private static string Clean(string? value)
    {
        return value ?? "<no stack>";
    }
}
