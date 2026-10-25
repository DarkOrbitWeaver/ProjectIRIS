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
    public bool HadIncompleteTurn => InterruptedTurnId is not null;
}

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
    public void RecordToolCall(string turnId, string moduleId, string toolName) =>
        Append(new JournalEntry
        {
            Kind = "tool", TurnId = turnId, Detail = moduleId + "/" + toolName
        });

    /// <summary>Records that the turn finished cleanly.</summary>
    public void RecordAnswerCompleted(string turnId) =>
        Append(new JournalEntry { Kind = "answer-completed", TurnId = turnId });

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
                else if (entry.Kind == "answer-completed")
                {
                    unanswered = null;
                }
            }

            if (unanswered is null)
            {
                return new JournalRecovery { LastState = lastState };
            }

            JournalEntry? progress = entries
                .Where(entry => entry.Kind == "answer-progress" && entry.TurnId == unanswered.TurnId)
                .LastOrDefault();

            return new JournalRecovery
            {
                LastState = lastState,
                InterruptedTurnId = unanswered.TurnId,
                InterruptedUserMessage = unanswered.Text,
                PartialAnswer = progress?.Text
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
    }
}
