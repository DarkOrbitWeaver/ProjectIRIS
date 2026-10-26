using System.Diagnostics;
using System.Runtime.InteropServices;
using CompanionOS.Core.Diagnostics;

namespace CompanionOS.Core;

/// <summary>
/// The family contract between Iris and everything she spawns.
///
/// Every long-lived child she starts — the brain server, the voice worker, a plugin's
/// MCP server — is bound into one Windows Job Object flagged KILL_ON_JOB_CLOSE. When
/// her last job handle closes, meaning her process exits for any reason at all
/// (normal quit, crash, force-kill from Task Manager, power cut), the operating
/// system immediately reaps every process in the family. No child of hers can ever
/// again drain RAM quietly on a "closed" app, which is exactly what happened to the
/// owner: llama-servers and workers outliving her session.
///
/// The graceful path (MainWindow -> CompanionRuntime.DisposeAsync) still stops each
/// child politely first; this guard is the floor under it, not a replacement.
/// </summary>
public static class ChildProcessGuard
{
    private const int JobObjectInfoClassExtendedLimit = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass,
        ref JobObjectExtendedLimitInformation limits, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    private static readonly object JobLock = new();
    private static nint _job;

    /// <summary>
    /// Opens the family job once per process and keeps the handle for Iris's lifetime:
    /// the kill flag fires exactly when that final handle goes away, so the handle must
    /// never be closed deliberately.
    /// </summary>
    private static nint GetJob()
    {
        lock (JobLock)
        {
            if (_job != nint.Zero)
            {
                return _job;
            }

            nint job = CreateJobObjectW(nint.Zero, "companionos-child-family");
            if (job == nint.Zero)
            {
                CompanionLog.Warning("System",
                    "The child-process cleanup job could not be created; children still stop politely " +
                    "while she exits normally, but a crash could leave stragglers behind.");
                return nint.Zero;
            }

            JobObjectExtendedLimitInformation limits = new();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            if (!SetInformationJobObject(job, JobObjectInfoClassExtendedLimit, ref limits,
                    Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                CompanionLog.Warning("System",
                    "The child-process cleanup job refused its kill-on-exit flag; children still stop " +
                    "politely while she exits normally, but a crash could leave stragglers behind.");
                return nint.Zero;
            }

            _job = job;
            return job;
        }
    }

    /// <summary>
    /// Binds a child she just started so the operating system owns its exit as much as
    /// she does. Always safe to call: a failure logs and leaves the polite path intact.
    /// </summary>
    public static void Adopt(Process? child)
    {
        nint job = GetJob();
        if (job == nint.Zero || child is null)
        {
            return;
        }

        try
        {
            if (!AssignProcessToJobObject(job, child.Handle))
            {
                CompanionLog.Trace("System",
                    $"A child process (pid {child.Id}) could not join the cleanup job " +
                    $"(Win32 error {Marshal.GetLastWin32Error()}).");
                return;
            }

            CompanionLog.Info("System",
                $"Bound child pid {child.Id} ({child.ProcessName}) to the exit-with-her job.");
        }
        catch (Exception exception)
        {
            // A dead-at-bind child is not an error worth alarming anyone about.
            CompanionLog.Trace("System", $"Child binding skipped: {exception.Message}");
        }
    }
}
