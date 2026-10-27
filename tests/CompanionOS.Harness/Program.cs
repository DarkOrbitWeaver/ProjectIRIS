using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionOS.Core.Brain;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Modules;
using CompanionOS.Core.Modules.BuiltIn;
using CompanionOS.Core.Perception;
using CompanionOS.Core.Recovery;
using CompanionOS.Core.Voice;

// ---------------------------------------------------------------------------
// Iris harness runner: drives the REAL brain loop — modules, router, sandbox,
// model client, journal, turn spans — against the already-running llama-server,
// inside an isolated CompanionPaths root, so her live diaries stay untouched.
//
// Verdicts are receipts: they are read back from the journal and the day file
// that the turn itself wrote. Her answer text is printed next to those
// receipts so the report can quote her honestly.
// ---------------------------------------------------------------------------

string liveRoot = CompanionPaths.Root;

string tempRoot = args.Length > 0
    ? args[0]
    : Path.Combine(Path.GetTempPath(), $"iris-harness-{DateTime.Now:yyyyMMdd-HHmmss}");

if (Directory.Exists(tempRoot))
{
    Console.WriteLine($"Harness root already exists; refusing to reuse '{tempRoot}'. Pass a fresh path.");
    return 2;
}

CompanionPaths.OverrideRoot(tempRoot);
CompanionPaths.EnsureCreated();
CompanionLog.Configure(CompanionPaths.LogsDirectory);
Console.WriteLine($"Live root:  {liveRoot}");
Console.WriteLine($"Harness root:{CompanionPaths.Root}");
Console.WriteLine($"Day diary:  {IrisEvent.FilePath}");
Console.WriteLine();

// --- settings: hers, cloned, with only the harness deltas -------------------
string liveSettingsPath = Path.Combine(liveRoot, "config", "settings.json");
string settingsJson = File.Exists(liveSettingsPath)
    ? File.ReadAllText(liveSettingsPath)
    : "{\"SchemaVersion\":3}";
JsonObject patch = JsonNode.Parse(settingsJson)!.AsObject();

// The harness points the brain at the ALREADY RUNNING llama-server; a Managed
// profile would spawn a second server and fight over port 8747.
if (patch["ActiveBrainProfile"] is JsonObject activeProfile)
{
    activeProfile["Kind"] = "External";
    activeProfile["Endpoint"] = "http://127.0.0.1:8747/v1";
    activeProfile["HasFullUrl"] = true;
}
else
{
    Console.WriteLine("Live settings had no ActiveBrainProfile; aborting before a wrong brain starts.");
    return 2;
}

JsonNode voice = patch["Voice"]!;
voice["SpeakReplies"] = false;

string settingsPath = CompanionPaths.SettingsFile;
Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
File.WriteAllText(settingsPath, patch.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

// --- persona: hers, so the brain under test is the real her -----------------
string livePersonaPath = Path.Combine(liveRoot, "config", "persona.json");
if (File.Exists(livePersonaPath))
{
    Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
    File.Copy(livePersonaPath, CompanionPaths.PersonaFile, overwrite: true);
}

SettingsStore settings = new();
settings.Load();
Console.WriteLine($"Profile:    {settings.Current.ActiveBrainProfile.DisplayName} ({settings.Current.ActiveBrainProfile.Kind})");
Console.WriteLine();

EventBus events = new();
MindStateMachine mind = new();
PersonaStore persona = new(CompanionPaths.PersonaFile);
persona.Load();

MemoryGraph graph = new(CompanionPaths.BrainDatabaseFile);
SynapticMemoryEngine memory = new(graph, settings, events);

FileAuditTrail auditTrail = FileAuditTrail.CreateDefault();
PermissionGrantStore grants = PermissionGrantStore.CreateDefault();
SandboxGuard sandbox = new(events, auditTrail, grants);
ModuleHost modules = new(settings, events, sandbox, memory);

// Nobody houses the shell in the harness, so permission prompts answer
// themselves with a remembered grant — every ask is still recorded in the
// audit trail and receipts before the answer lands.
events.Subscribe<PermissionRequestedEvent>(notification =>
    notification.Request.Answer(PermissionDecision.AllowAlways));

PerceptionEngine perception = new(settings, events);
VoicePipeline voicePipeline = new(settings, events, persona); // never started: no microphone, no speech engines
LlamaServerHost serverHost = new();
ModelClient modelClient = new();
TurnJournal journal = TurnJournal.CreateDefault();

AssistantBrain brain = new(settings, events, persona, memory, modules, serverHost, modelClient,
    perception, voicePipeline, journal, mind);
modules.Host = brain;

foreach (IAssistantModule module in new IAssistantModule[]
    {
        new CoreToolsModule(),
        new BackgroundTasksModule(),
        new MusicPlayerModule(),
        new SystemControlModule()
    })
{
    await modules.RegisterAsync(module).ConfigureAwait(false);
}

perception.Start();
await brain.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
Console.WriteLine("=== initialized: senses running, external brain at 8747, SpeakReplies off ===");
Console.WriteLine();

// --- the matrix -------------------------------------------------------------
Case[] matrix =
[
    new("chat-hello", "Morning, Iris, good to see you", CaseKind.PureChat),
    new("q-song", "what song is playing right now", CaseKind.QuestionNoNudge),
    new("act-pause", "pause the music", CaseKind.ActionExpectTools, ["control_windows_media", "music_player"]),
    new("act-typho", "please ressume my yt song", CaseKind.ActionExpectTools, ["control_windows_media", "music_player"]),
    new("act-unpause", "unpause the song", CaseKind.ActionExpectTools, ["control_windows_media", "music_player"]),
    new("mem-remember", "remember that my spare game key lives in the desk drawer", CaseKind.ActionExpectTools, ["remember"]),
    new("mem-recall", "where did I say my spare game key lives?", CaseKind.QuestionNoNudge, null, "drawer"),
    new("mem-forget", "forget what I told you about the game key", CaseKind.ActionExpectTools, ["forget_memory"]),
    new("note-write", "write a note: the harness test run is on", CaseKind.ActionExpectTools, ["write_note"]),
    new("note-read", "read my notes", CaseKind.ActionExpectTools, ["read_notes"]),
    new("timer-set", "start a timer for five minutes", CaseKind.ActionExpectTools, ["set_timer"]),
    new("timer-cancel", "cancel the timer", CaseKind.ActionExpectTools, ["cancel_timers", "cancel_timer"]),
    new("q-capabilities", "what can you actually do for me around this pc?", CaseKind.QuestionNoNudge),
    new("q-machine", "how is my machine running at the moment?", CaseKind.QuestionNoNudge),
    new("act-find", "find the tax report files somewhere on this pc", CaseKind.ActionExpectTools, ["find_files"]),
    new("act-open", "open the calculator", CaseKind.ActionExpectTools, ["open_application"]),
    new("mode-on", "switch into gaming mode", CaseKind.ActionExpectTools, ["set_operating_mode"]),
    new("mode-off", "come out of gaming mode", CaseKind.ActionExpectTools, ["set_operating_mode"]),
    new("garble-isit", "Is it", CaseKind.GarbledNoNudge),
    new("garble-dont", "How we don't.", CaseKind.GarbledNoNudge),
    new("garble-noise", "asdf zx qw 123 bagel downhill whatever huh", CaseKind.GarbledNoNudge),
    new("cx-multitask",
        "play hollowlight, then start a five minute timer, and remember I'm at seventy percent health",
        CaseKind.ComplexExpectTools, ["music_player", "music_library", "music_playlists", "set_timer", "remember"]),
    new("q-watch", "what am I watching, and how loaded is my cpu right now?", CaseKind.QuestionNoNudge),
    new("chat-thanks", "thanks, that was a good round of tests", CaseKind.PureChat)
];

List<CaseResult> results = [];

for (int i = 0; i < matrix.Length; i++)
{
    Case item = matrix[i];
    Console.WriteLine($"[{i + 1}/{matrix.Length}] {item.Id}: \"{item.Input}\"");
    CaseResult outcome;
    try
    {
        outcome = await RunCaseAsync(brain, item).ConfigureAwait(false);
    }
    catch (Exception failure)
    {
        outcome = new CaseResult
        {
            Answer = failure.GetType().Name + ": " + failure.Message,
            Id = item.Id,
            Pass = false,
            Note = "HARNESS ERROR."
        };
    }

    results.Add(outcome);
    Console.WriteLine($"    rounds={outcome.Rounds} calls={outcome.CallsMade} dropped={outcome.Dropped} " +
                      $"nudge={(outcome.ReceiptCheckFired ? "yes" : "no")} " +
                      $"tools=[{(outcome.Tools.Count == 0 ? "-" : string.Join(", ", outcome.Tools))}] " +
                      $"succeeded=[{(outcome.SucceededTools.Count == 0 ? "-" : string.Join(", ", outcome.SucceededTools))}] " +
                      $"verdict={(outcome.Pass ? "PASS" : "FAIL")}");
    Console.WriteLine($"    she said: {Clip(outcome.Answer, 300)}");
    if (!string.IsNullOrEmpty(outcome.Note))
    {
        Console.WriteLine($"    note: {outcome.Note}");
    }

    Console.WriteLine();
}

int landed = results.Count(result => result.Pass);
Console.WriteLine($"=== {landed}/{results.Count} cases landed on their receipts ===");

string reportPath = Path.Combine(CompanionPaths.Root, "harness-report.md");
await File.WriteAllTextAsync(reportPath, BuildReport(results, matrix)).ConfigureAwait(false);
Console.WriteLine($"Report written: {reportPath}");

await brain.DisposeAsync().ConfigureAwait(false);
return landed == results.Count ? 0 : 1;

// ---------------------------------------------------------------------------
// Turn execution and receipt harvesting
// ---------------------------------------------------------------------------
static async Task<CaseResult> RunCaseAsync(AssistantBrain brain, Case item)
{
    long journalBefore = CountBytes(CompanionPaths.JournalFile);
    long diaryBefore = CountBytes(IrisEvent.FilePath);

    CaseResult result = new();

    string answer = await brain.SendMessageAsync(item.Input, "text").ConfigureAwait(false);
    result.Answer = answer;

    // One turn at a time, so anything appended between the two offsets belongs
    // to this case alone.
    List<DiaryRecord> journalTurns = Collect((CompanionPaths.JournalFile, journalBefore));
    List<DiaryRecord> diaryTurns = Collect((IrisEvent.FilePath, diaryBefore));

    string turnId = journalTurns
        .Where(record => record.Kind == "user" && Clamp(record.Text) == item.Input)
        .Select(record => record.TurnId)
        .LastOrDefault() ?? string.Empty;

    if (string.IsNullOrEmpty(turnId))
    {
        result.Pass = false;
        result.Note = "No journal 'user' entry found for this turn.";
    }

    foreach (DiaryRecord record in journalTurns.Where(record => record.TurnId == turnId))
    {
        if (record.Kind == "tool" && record.Detail.Contains('/'))
        {
            string toolName = record.Detail.Split('/')[^1];
            result.Tools.Add(toolName);
            result.Arguments.Add(record.Arguments);
        }

        if (record.Kind == "tool-result")
        {
            if (string.Equals(record.State, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                result.SucceededTools.Add(result.Tools.Count > 0 ? result.Tools[^1] : "?");
            }

            if (string.Equals(record.State, "Denied", StringComparison.OrdinalIgnoreCase))
            {
                result.DeniedTools.Add(result.Tools.Count > 0 ? result.Tools[^1] : "?");
            }
        }
    }

    foreach (DiaryRecord record in diaryTurns.Where(record => record.Correlation == turnId))
    {
        switch (record.Kind)
        {
            case "turn.round":
                result.Rounds++;
                result.CallsMade += ToInt(record.Data, "calls_made");
                result.Dropped += ToInt(record.Data, "calls_dropped");
                break;
            case "turn.receipt_check":
                result.ReceiptCheckFired = true;
                break;
            case "turn.cancelled":
                result.Cancelled = true;
                break;
        }
    }

    switch (item.Kind)
    {
        case CaseKind.ActionExpectTools:
        {
            bool matched = result.SucceededTools
                .Any(tool => item.ExpectedTools.Any(expected =>
                    tool.StartsWith(expected, StringComparison.OrdinalIgnoreCase)));
            if (matched)
            {
                result.Note = "Hand ran and landed.";
            }
            else if (result.SucceededTools.Count > 0)
            {
                result.Note = "A hand ran, but not one the case named: " +
                              string.Join(", ", result.SucceededTools);
            }
            else
            {
                result.Pass = false;
                result.Note = result.DeniedTools.Count > 0
                    ? "A denial with no carry: " + string.Join(", ", result.DeniedTools)
                    : ClaimsSuccess(result.Answer)
                        ? "Claimed completion with zero succeeded hands (fabrication)."
                        : "No named hand succeeded.";
            }

            break;
        }

        case CaseKind.QuestionNoNudge:
            if (result.ReceiptCheckFired)
            {
                result.Pass = false;
                result.Note = "Receipt-check nudge on a question.";
            }
            else if (item.ExpectedAnswerContains is string needle &&
                     !result.Answer.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                result.Pass = false;
                result.Note = $"Answer did not contain '{needle}'.";
            }

            break;

        case CaseKind.GarbledNoNudge:
            if (result.ReceiptCheckFired)
            {
                result.Pass = false;
                result.Note = "Nudged a burp.";
            }
            else if (IsCanned(result.Answer))
            {
                result.Pass = false;
                result.Note = "Canned reflex filler was all she had.";
            }

            break;

        case CaseKind.PureChat:
            if (result.ReceiptCheckFired)
            {
                result.Pass = false;
                result.Note = "Nudged ordinary chat.";
            }
            else if (result.Answer.Trim().Length < 12)
            {
                result.Pass = false;
                result.Note = "Answer too brief for ordinary chat.";
            }

            break;

        case CaseKind.ComplexExpectTools:
        {
            int distinct = result.SucceededTools
                .Where(tool => item.ExpectedTools.Any(expected =>
                    tool.StartsWith(expected, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (distinct < 2)
            {
                result.Pass = false;
                result.Note = $"Only {distinct} distinct succeeded hand(s) for a multi-part order.";
            }

            break;
        }
    }

    if (result.Dropped > 0)
    {
        result.Pass = false;
        result.Note += " Tool calls were dropped by the stream interface.";
    }

    if (result.Rounds > 8)
    {
        result.Pass = false;
        result.Note += " Turn exceeded the eight-round loop.";
    }

    if (result.Cancelled)
    {
        result.Pass = false;
        result.Note += " Turn was cancelled.";
    }

    return result;
}

static List<DiaryRecord> Collect((string Path, long Offset) source)
{
    List<DiaryRecord> records = [];
    try
    {
        string text = File.ReadAllText(source.Path);
        int cursor = (int)Math.Min(text.Length, Math.Max(0, source.Offset));
        string tail = text[cursor..];
        foreach (string raw in tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (JsonNode.Parse(raw) is not JsonObject entry)
                {
                    continue;
                }

                string kind = entry["Kind"]?.ToString() ?? entry["event"]?.ToString() ?? string.Empty;
                if (kind.Length == 0)
                {
                    continue;
                }

                records.Add(new DiaryRecord
                {
                    Kind = kind,
                    TurnId = entry["TurnId"]?.ToString() ?? string.Empty,
                    Correlation = entry["correlation_id"]?.ToString() ?? string.Empty,
                    Text = entry["Text"]?.ToString() ?? string.Empty,
                    State = entry["State"]?.ToString() ?? string.Empty,
                    Detail = entry["Detail"]?.ToString() ?? string.Empty,
                    Arguments = entry["Arguments"]?.ToString() ?? string.Empty,
                    Data = entry["data"] as JsonObject
                });
            }
            catch (JsonException)
            {
                // A torn line is a drop, not a crash; the verdict handles the loss.
            }
        }
    }
    catch (IOException)
    {
        // The diary writer owns the file mid-append; the next read gets the rest.
    }

    return records;
}

static int ToInt(JsonObject? data, string key)
{
    string value = data?[key]?.ToString() ?? string.Empty;
    return int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
}

static long CountBytes(string path)
{
    try
    {
        return new FileInfo(path).Length;
    }
    catch (IOException)
    {
        return 0;
    }
}

static string Clip(string text, int max) =>
    string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";

static string Clamp(string text) => (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

static bool ClaimsSuccess(string answer)
{
    string[] claims =
 {
        "resumed", "paused", "playing", "timer set", "canceled", "cancelled",
        "remembered that", "saved that", "note written", "opened the", "found the"
    };
    string lowered = answer.ToLowerInvariant();
    return claims.Any(lowered.Contains);
}

static bool IsCanned(string answer)
{
    string trimmed = answer.Trim().TrimEnd('.', '!');
    string[] canned = { "Iris is here", "Iris is awake and ready to assist", "Iris is okay", "I'm okay" };
    return canned.Any(word => trimmed.Equals(word, StringComparison.OrdinalIgnoreCase));
}

static string BuildReport(List<CaseResult> results, Case[] matrix)
{
    List<string> lines =
    [
        "# Iris harness report",
        string.Empty,
        $"Run finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
        $"Cases: {results.Count}; landed: {results.Count(result => result.Pass)}",
        string.Empty,
        "| # | id | verdict | rounds | calls | nudge | tools | answer |",
        "|---|----|---------|--------|-------|-------|-------|--------|"
    ];

    for (int i = 0; i < results.Count; i++)
    {
        CaseResult outcome = results[i];
        Case plan = matrix[i];
        lines.Add("| " + (i + 1) +
                  " | " + plan.Id +
                  " | " + (outcome.Pass ? "PASS" : "FAIL") +
                  " | " + outcome.Rounds +
                  " | " + outcome.CallsMade +
                  " | " + (outcome.ReceiptCheckFired ? "yes" : "no") +
                  " | " + (outcome.SucceededTools.Count == 0 ? "-" : string.Join("; ", outcome.SucceededTools)) +
                  " | " + Clip(outcome.Answer, 160).Replace("|", "-").Replace("\n", " ") + " |");
        if (!string.IsNullOrEmpty(outcome.Note))
        {
            lines.Add("| | | | | | | " + outcome.Note.Replace("|", "-") + " |");
        }
    }

    lines.Add(string.Empty);
    lines.Add("## Her words, pasted as she said them");
    lines.Add(string.Empty);
    for (int i = 0; i < results.Count; i++)
    {
        lines.Add($"### {i + 1}. {matrix[i].Id} — \"{matrix[i].Input}\"");
        lines.Add(string.Empty);
        lines.Add(results[i].Answer.Length == 0 ? "(empty answer)" : results[i].Answer);
        lines.Add(string.Empty);
        if (!string.IsNullOrEmpty(results[i].Note))
        {
            lines.Add($"*{results[i].Note}*");
            lines.Add(string.Empty);
        }
    }

    return string.Join('\n', lines);
}

// ---------------------------------------------------------------------------
// Records
// ---------------------------------------------------------------------------
internal enum CaseKind
{
    ActionExpectTools,
    QuestionNoNudge,
    PureChat,
    GarbledNoNudge,
    ComplexExpectTools
}

internal sealed record Case(
    string Id,
    string Input,
    CaseKind Kind,
    string[]? ExpectedTools = null,
    string? ExpectedAnswerContains = null)
{
    public string[] ExpectedTools { get; init; } = ExpectedTools ?? [];
}

internal sealed class CaseResult
{
    public string Id { get; init; } = "-";

    public string Answer { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public bool Pass { get; set; } = true;

    public int Rounds { get; set; }

    public int CallsMade { get; set; }

    public int Dropped { get; set; }

    public bool ReceiptCheckFired { get; set; }

    public bool Cancelled { get; set; }

    public List<string> Tools { get; } = [];

    public List<string> SucceededTools { get; } = [];

    public List<string> DeniedTools { get; } = [];

    public List<string> Arguments { get; } = [];
}

internal sealed class DiaryRecord
{
    public string Kind { get; set; } = string.Empty;

    public string TurnId { get; set; } = string.Empty;

    public string Correlation { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public JsonObject? Data { get; set; }
}
