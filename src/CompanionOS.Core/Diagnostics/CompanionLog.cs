namespace CompanionOS.Core.Diagnostics;

/// <summary>How loud a log line is. Ordered, so "at least Info" is a simple comparison.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4
}

/// <summary>One line of history, kept in memory so the UI can show it without touching disk.</summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public LogLevel Level { get; init; }
    public string Scope { get; init; } = "App";
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }

    public string Format()
    {
        string stamp = Timestamp.ToString("HH:mm:ss.fff");
        string scope = Scope.PadRight(12);
        return Detail is null
            ? $"{stamp} {Level,-7} {scope} {Message}"
            : $"{stamp} {Level,-7} {scope} {Message}{Environment.NewLine}{Detail}";
    }
}

/// <summary>
/// The single diary every part of her writes to.
///
/// Why not a logging framework: this is a desktop app that ships without a console, so the
/// log has to be readable by a human in Notepad and by the diagnostics panel in the UI at the
/// same time. One file, one format, no configuration files to get wrong.
///
/// Nothing here throws. A logger that can crash the app is worse than no logger.
/// </summary>
public static class CompanionLog
{
    private const int RememberedEntries = 600;
    private const long MaxFileBytes = 8L * 1024 * 1024;

    private static readonly object WriteGate = new();
    private static readonly Queue<LogEntry> Entries = new();
    private static LogLevel _minimumLevel = LogLevel.Info;
    private static string _logFilePath = string.Empty;
    private static string _logDirectory = string.Empty;
    private static bool _fileAppendFailureAnnounced;

    /// <summary>Raised for every line that passes the level filter. UI subscribes to this.</summary>
    public static event Action<LogEntry>? EntryWritten;

    public static string LogFilePath
    {
        get { lock (WriteGate) { return _logFilePath; } }
    }

    public static void SetMinimumLevel(LogLevel level)
    {
        lock (WriteGate) { _minimumLevel = level; }
    }

    /// <summary>
    /// Parses the settings' log-level word. Anything unrecognised — including an
    /// EMPTY or null value — falls back to Info instead of taking the boot path
    /// down (2026-10-27 audit: a null word was an immediate NRE at startup, and the
    /// literal "debug" never mapped to anything until restart).
    /// </summary>
    public static LogLevel ParseLevelWord(string? word) =>
        word?.Trim().ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Info,
            "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            _ => LogLevel.Info
        };

    /// <summary>Points the log at a folder. The active file is bounded while the app is running.</summary>
    public static void Configure(string directory)
    {
        IrisEvent.Configure(directory);
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "companion.log");
            string previous = Path.Combine(directory, "companion.previous.log");
            if (File.Exists(previous) && new FileInfo(previous).Length > MaxFileBytes)
            {
                File.Delete(previous);
            }
            lock (WriteGate) { _logDirectory = Path.GetFullPath(directory); _logFilePath = path; }
        }
        catch (Exception failure)
        {
            // A read-only disk must not stop her from thinking.
            System.Diagnostics.Debug.WriteLine($"Log configuration failed: {failure.Message}");
        }
    }

    public static void Trace(string scope, string message) => Write(LogLevel.Trace, scope, message, null);
    public static void Debug(string scope, string message) => Write(LogLevel.Debug, scope, message, null);
    public static void Info(string scope, string message) => Write(LogLevel.Info, scope, message, null);

    public static void Warning(string scope, string message, Exception? failure = null) =>
        Write(LogLevel.Warning, scope, message, failure);

    public static void Error(string scope, string message, Exception? failure = null) =>
        Write(LogLevel.Error, scope, message, failure);

    /// <summary>Newest last, so a panel can simply scroll to the bottom.</summary>
    public static IReadOnlyList<LogEntry> Recent(int maximumEntries = 200)
    {
        lock (WriteGate)
        {
            int skip = Math.Max(0, Entries.Count - maximumEntries);
            return Entries.Skip(skip).ToArray();
        }
    }

    private static void Write(LogLevel level, string scope, string message, Exception? failure)
    {
        LogEntry entry;
        lock (WriteGate)
        {
            if (level < _minimumLevel)
            {
                return;
            }

            entry = new LogEntry
            {
                Level = level,
                Scope = string.IsNullOrWhiteSpace(scope) ? "App" : scope,
                Message = message,
                Detail = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}"
            };

            Entries.Enqueue(entry);
            while (Entries.Count > RememberedEntries)
            {
                Entries.Dequeue();
            }

            if (_logFilePath.Length > 0)
            {
                try
                {
                    AppendBounded(entry.Format());
                    _fileAppendFailureAnnounced = false;
                }
                catch (Exception fileFailure) when (fileFailure is IOException or UnauthorizedAccessException)
                {
                    // Disk busy/full or the file locked (a stray second instance). The
                    // in-memory history stays truthful and the failure is ANNOUNCED
                    // once per failure period: the old empty catches meant a log file
                    // that stopped taking writes stayed silent forever, and there was
                    // no way to notice the missing diagnostic trail (2026-10-27 audit).
                    if (!_fileAppendFailureAnnounced)
                    {
                        _fileAppendFailureAnnounced = true;
                        string notice =
                            "The log file is unwritable; entries continue in memory only " +
                            $"({fileFailure.Message}).";
                        System.Diagnostics.Debug.WriteLine($"CompanionLog: {notice}");
                        IrisEvent.Log("Log", "log.file_unwritable", LogLevel.Warning, message: notice);
                    }
                }
            }
        }

        try
        {
            EntryWritten?.Invoke(entry);
        }
        catch (Exception listenerFailure)
        {
            // A broken UI listener must never take the brain down with it.
            System.Diagnostics.Debug.WriteLine($"Log listener failed: {listenerFailure.Message}");
        }
    }

    private static void AppendBounded(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text + Environment.NewLine);
        if (bytes.LongLength > MaxFileBytes)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(text[..Math.Min(text.Length, 4096)] + " [truncated]" + Environment.NewLine);
        }

        FileInfo active = new(_logFilePath);
        if (active.Exists && active.Length > 0 && active.Length + bytes.LongLength > MaxFileBytes)
        {
            string previous = Path.Combine(_logDirectory, "companion.previous.log");
            if (active.Length <= MaxFileBytes)
            {
                File.Move(_logFilePath, previous, overwrite: true);
            }
            else
            {
                // Drop oversized legacy/corrupt history instead of preserving an unbounded file.
                File.Delete(_logFilePath);
                if (File.Exists(previous)) File.Delete(previous);
            }
        }

        using FileStream stream = new(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
    }
}
