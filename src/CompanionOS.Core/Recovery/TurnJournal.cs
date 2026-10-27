using System.Globalization;
using System.Text.Json;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Mind;

namespace CompanionOS.Core.Recovery;

/// <summary>One line of the journal. Everything is optional except the kind and time.</summary>
public sealed class JournalEntry
{
    public string Kind { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string RelatedId { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public string State { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

/// <summary>What the journal could work out after a restart.</summary>
public sealed class JournalRecovery
{
    public MindState LastState { get; init; } = MindState.Idle;
    public string? InterruptedTurnId { get; init; }
    public string? InterruptedUserMessage { get; init; }
    public string? PartialAnswer { get; init; }
    public IReadOnlyList<InterruptedToolAction> InterruptedTools { get; init; } = Array.Empty<InterruptedToolAction>();
    public IReadOnlyList<InterruptedBackgroundTask> InterruptedBackgroundTasks { get; init; } =
        Array.Empty<InterruptedBackgroundTask>();
    public bool HadIncompleteTurn => InterruptedTurnId is not null;
}

public enum JournalToolStatus
{
    Unknown,
    Succeeded,
    Failed,
    BackgroundStarted
}

/// <summary>A tool call whose turn had not finished when Iris last stopped.</summary>
public sealed record InterruptedToolAction(string OperationId, string Tool, JournalToolStatus Status,
    string? BackgroundTaskId);

/// <summary>A background module task that was queued or running when Iris last stopped.</summary>
public sealed record InterruptedBackgroundTask(string TaskId, string Detail, string State);

/// <summary>
/// An append-only record of what she was doing, written before it happens.
///
/// This is what makes "she never forgets, even after a crash" true in practice:
/// the durable memory is the graph, but the graph alone cannot tell you that a
/// reply was half-spoken when the power went out. The journal can.
///
/// Every line is flushed to disk immediately. If a write is cut in half, the
/// reader skips the broken final line instead of failing.
/// </summary>
public sealed class TurnJournal
{
    private const long MaximumFileBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public TurnJournal(string filePath) => _filePath = filePath;

    /// <summary>Default location: next to the brain database.</summary>
    public static TurnJournal CreateDefault() => new(Configuration.CompanionPaths.JournalFile);

    /// <summary>Records the user's request before any work starts.</summary>
    public void RecordUserMessage(string turnId, string text) =>
        Append(new JournalEntry { Kind = "user", TurnId = turnId, Text = text });

    /// <summary>Records that the model started answering.</summary>
    public void RecordAnswerStarted(string turnId) =>
        Append(new JournalEntry { Kind = "answer-started", TurnId = turnId });

    /// <summary>Records a partial answer, so a crash mid-sentence is explainable.</summary>
    public void RecordAnswerProgress(string turnId, string textSoFar) =>
        Append(new JournalEntry { Kind = "answer-progress", TurnId = turnId, Text = textSoFar });

    /// <summary>Records a tool call before it runs; a crash leaves evidence of it.</summary>
    public string RecordToolCall(string turnId, string moduleId, string toolName)
    {
        string operationId = Guid.NewGuid().ToString("N");
        Append(new JournalEntry
        {
            Kind = "tool", TurnId = turnId, OperationId = operationId, Detail = moduleId + "/" + toolName
        });
        return operationId;
    }

    /// <summary>Records whether the action finished or only started background work; no arguments or result text are saved.</summary>
    public void RecordToolResult(string turnId, string operationId, JournalToolStatus status,
        string? backgroundTaskId = null) =>
        Append(new JournalEntry
        {
            Kind = "tool-result",
            TurnId = turnId,
            OperationId = operationId,
            State = status.ToString(),
            RelatedId = backgroundTaskId ?? string.Empty
        });

    /// <summary>Records only lifecycle transitions, never progress text, arguments, or task results.</summary>
    public void RecordBackgroundTaskState(string taskId, string moduleId, string toolName, string state) =>
        Append(new JournalEntry
        {
            Kind = "background-task",
            TaskId = taskId,
            Detail = moduleId + "/" + toolName,
            State = state
        });

    /// <summary>Records that the turn finished cleanly.</summary>
    public void RecordAnswerCompleted(string turnId) =>
        Append(new JournalEntry { Kind = "answer-completed", TurnId = turnId });

    /// <summary>Records an intentional cancellation so recovery does not call it a crash.</summary>
    public void RecordAnswerCancelled(string turnId) =>
        Append(new JournalEntry { Kind = "answer-cancelled", TurnId = turnId });

    /// <summary>Records a mind state change so the next start can restore it.</summary>
    public void RecordState(MindState state, string reason) =>
        Append(new JournalEntry { Kind = "state", State = state.ToString(), Detail = reason });

    /// <summary>
    /// Reads the journal and works out whether the previous session died mid-turn.
    /// Only the tail of the file is read: the journal is diagnostic, not the memory.
    /// </summary>
    public JournalRecovery Analyse()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new JournalRecovery();
            }

            List<JournalEntry> entries = new();
            foreach (string line in File.ReadLines(_filePath).TakeLast(400))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    JournalEntry? entry = JsonSerializer.Deserialize<JournalEntry>(line, SerializerOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch
                {
                    // Torn final line after a power cut: expected, ignore it.
                }
            }

            MindState lastState = MindState.Idle;
            foreach (JournalEntry entry in entries.Where(entry => entry.Kind == "state"))
            {
                if (Enum.TryParse(entry.State, out MindState parsed))
                {
                    lastState = parsed;
                }
            }

            JournalEntry? unanswered = null;
            foreach (JournalEntry entry in entries)
            {
                if (entry.Kind == "user")
                {
                    unanswered = entry;
                }
                else if (unanswered is not null && entry.TurnId == unanswered.TurnId &&
                         entry.Kind is "answer-completed" or "answer-cancelled")
                {
                    unanswered = null;
                }
            }

            List<InterruptedBackgroundTask> interruptedBackgroundTasks = entries
                .Where(entry => entry.Kind == "background-task" && !string.IsNullOrWhiteSpace(entry.TaskId))
                .GroupBy(entry => entry.TaskId, StringComparer.Ordinal)
                .Select(group => group.Last())
                .Where(entry => entry.State is "Queued" or "Running" or "CancelRequested")
                .Select(entry => new InterruptedBackgroundTask(entry.TaskId, entry.Detail, entry.State))
                .ToList();

            if (unanswered is null)
            {
                return new JournalRecovery
                {
                    LastState = lastState,
                    InterruptedBackgroundTasks = interruptedBackgroundTasks
                };
            }

            JournalEntry? progress = entries
                .Where(entry => entry.Kind == "answer-progress" && entry.TurnId == unanswered.TurnId)
                .LastOrDefault();
            List<InterruptedToolAction> interruptedTools = entries
                .Where(entry => entry.Kind == "tool" && entry.TurnId == unanswered.TurnId)
                .Select(started =>
                {
                    JournalEntry? result = entries.LastOrDefault(entry => entry.Kind == "tool-result" &&
                        entry.TurnId == unanswered.TurnId && entry.OperationId == started.OperationId);
                    JournalToolStatus status = result is not null &&
                        Enum.TryParse(result.State, out JournalToolStatus parsedStatus)
                            ? parsedStatus
                            : JournalToolStatus.Unknown;
                    return new InterruptedToolAction(started.OperationId, started.Detail, status,
                        string.IsNullOrWhiteSpace(result?.RelatedId) ? null : result.RelatedId);
                }).ToList();

            return new JournalRecovery
            {
                LastState = lastState,
                InterruptedTurnId = unanswered.TurnId,
                InterruptedUserMessage = unanswered.Text,
                PartialAnswer = progress?.Text,
                InterruptedTools = interruptedTools,
                InterruptedBackgroundTasks = interruptedBackgroundTasks
            };
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Journal", $"Could not read the journal: {exception.Message}");
            return new JournalRecovery();
        }
    }

    private void Append(JournalEntry entry)
    {
        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                RollIfTooLarge();
                string line = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;
                using FileStream stream = new(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Journal", $"Could not append to the journal: {exception.Message}");
        }
    }

    private void RollIfTooLarge()
    {
        FileInfo file = new(_filePath);
        if (!file.Exists || file.Length < MaximumFileBytes)
        {
            return;
        }

        string archivePath = _filePath + $".{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.old";
        File.Move(_filePath, archivePath, overwrite: true);
        DeleteSurplusArchives();
    }

    /// <summary>
    /// Keeps at most two archived journals. Rotation minted a fresh timestamped .old
    /// file for every oversized journal and nothing ever deleted them - a few busy
    /// weeks left an archive family nobody could explain (2026-10-27 audit).
    /// </summary>
    private void DeleteSurplusArchives()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (directory is null)
            {
                return;
            }

            string pattern = Path.GetFileName(_filePath) + ".*.old";
            List<string> archives = Directory
                .EnumerateFiles(directory, pattern)
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();

            foreach (string stale in archives.Skip(2))
            {
                File.Delete(stale);
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Trace("Journal", $"Could not prune journal archives: {exception.Message}");
        }
    }
}
