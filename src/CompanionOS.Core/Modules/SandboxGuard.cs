using System.Text.Json;
using System.Text.Json.Serialization;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Prompts;

namespace CompanionOS.Core.Modules;

/// <summary>
/// What the sandbox decided, including WHY a refusal happened. A 15-second unanswered
/// prompt is not "the user said no" — the model reading the result deserves the truth
/// so it can ask once more, plainly, or choose another path instead of treating a
/// missed prompt as a forbidden action.
/// </summary>
public readonly record struct AuthorizationOutcome(bool IsAllowed, string RefusalReason)
{
    public static AuthorizationOutcome Allowed() => new(true, string.Empty);

    public static AuthorizationOutcome Refused(string reason) => new(false, reason);
}

/// <summary>
/// Shape of a permission stop, so the audit trail can record a timeout as a timeout
/// instead of folding it into a plain denial.
/// </summary>
public enum PermissionStop
{
    Allowed,
    Denied,
    TimedOut,
    Interrupted
}

/// <summary>What the user decided about a dangerous action.</summary>
public enum PermissionDecision
{
    Deny,
    AllowOnce,
    AllowAlways,

    /// <summary>Allowed until Iris closes. Never written to disk.</summary>
    AllowSession,

    /// <summary>Allowed for repeated calls in the current turn only.</summary>
    AllowTurn
}

/// <summary>
/// A question waiting for the user: "Browser wants to click on the page."
/// It is a confirm <see cref="IrisPrompt"/> underneath, so it gets the same card, voice
/// answers, safety rules and timeout as every other question. The old Answer / Cancel /
/// CanAlwaysAllow surface stays so existing callers keep working.
/// </summary>
public sealed class PermissionRequest
{
    public PermissionRequest(string moduleId, string moduleName, string toolName, string question, ToolTier tier,
        string? permissionScope = null, bool alwaysConfirm = false, bool sessionTrust = false,
        PromptRisk risk = PromptRisk.Sensitive, TimeSpan? timeout = null)
    {
        ModuleId = moduleId;
        ModuleName = moduleName;
        ToolName = toolName;
        Question = question;
        Tier = tier;
        PermissionScope = permissionScope;
        CanAlwaysAllow = !alwaysConfirm;
        CanTrustSession = sessionTrust && !alwaysConfirm;

        List<PromptOption> options = new()
        {
            new PromptOption(PromptIds.Deny, "Deny", Role: PromptOptionRole.Deny,
                Aliases: new[] { "deny", "no" }),
            new PromptOption(PromptIds.AllowOnce, "Allow once", Role: PromptOptionRole.Approve)
        };
        if (!alwaysConfirm)
        {
            options.Add(new PromptOption(PromptIds.AllowTurn, "This turn", VoiceAllowed: false));
        }
        if (CanTrustSession)
        {
            // Pressed, never spoken: a misheard word must not trust a whole session.
            options.Add(new PromptOption(PromptIds.AllowSession, "This session", VoiceAllowed: false));
        }

        if (CanAlwaysAllow)
        {
            options.Add(new PromptOption(PromptIds.AllowAlways, "Always", Role: PromptOptionRole.Primary,
                VoiceAllowed: false));
        }

        string? detail = alwaysConfirm
            ? "Iris asks every time for this one."
            : string.IsNullOrWhiteSpace(permissionScope)
                ? null
                : $"Access: {permissionScope}";

        Prompt = new IrisPrompt(PromptKind.Confirm, "Needs your OK", question, options, risk,
            timeout ?? TimeSpan.FromSeconds(30), PromptIds.Deny, speakAloud: false, moduleId, moduleName, "shield")
        {
            Detail = detail
        };
    }

    public Guid Id => Prompt.Id;
    public IrisPrompt Prompt { get; }
    public string ModuleId { get; }
    public string ModuleName { get; }
    public string ToolName { get; }
    public string Question { get; }
    public ToolTier Tier { get; }
    public string? PermissionScope { get; }
    public bool CanAlwaysAllow { get; }
    public bool CanTrustSession { get; }
    public DateTimeOffset RequestedAt { get; } = DateTimeOffset.Now;

    /// <summary>Completes the request. Later answers are ignored.</summary>
    public void Answer(PermissionDecision decision) =>
        Prompt.TryAnswer(new PromptAnswer(OptionIdFor(decision), PromptSource.Button));

    /// <summary>Cancels the request, which counts as a refusal.</summary>
    public void Cancel() => Prompt.TryAnswer(new PromptAnswer(PromptIds.Deny, PromptSource.Cancelled));

    /// <summary>Waits for the user, or refuses when time runs out.</summary>
    public async Task<PermissionDecision> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        PromptAnswer answer = await Prompt.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        return DecisionFor(answer.OptionId);
    }

    public static string OptionIdFor(PermissionDecision decision) => decision switch
    {
        PermissionDecision.AllowOnce => PromptIds.AllowOnce,
        PermissionDecision.AllowSession => PromptIds.AllowSession,
        PermissionDecision.AllowTurn => PromptIds.AllowTurn,
        PermissionDecision.AllowAlways => PromptIds.AllowAlways,
        _ => PromptIds.Deny
    };

    public static PermissionDecision DecisionFor(string optionId) => optionId switch
    {
        PromptIds.AllowOnce => PermissionDecision.AllowOnce,
        PromptIds.AllowSession => PermissionDecision.AllowSession,
        PromptIds.AllowTurn => PermissionDecision.AllowTurn,
        PromptIds.AllowAlways => PermissionDecision.AllowAlways,
        _ => PermissionDecision.Deny
    };
}

/// <summary>
/// Decides what each tool call is allowed to do.
///
/// The posture is deliberately conservative: silence is a refusal, not consent.
///   Tier 1 (reflex) runs immediately and is logged.
///   Tier 2 (scoped) runs immediately once it has been allowed once; its first use asks,
///   and "always allow" remembers the grant. Every run lands in the audit trail.
///   Tier 3 (systemCritical) asks unless it was permanently granted, times out to Deny
///   after 15 seconds, and records the question and the answer forever. Tools marked
///   AlwaysConfirm always ask and never persist the resulting grant.
/// </summary>
public sealed class SandboxGuard
{
    private readonly EventBus _events;
    private readonly IAuditTrail _audit;
    private readonly PermissionGrantStore _grants;
    private readonly PromptService _prompts;
    private readonly HashSet<string> _sessionGrants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _turnGrants = new(StringComparer.Ordinal);
    private readonly object _sessionLock = new();

    public SandboxGuard(EventBus events, IAuditTrail audit, PermissionGrantStore grants,
        PromptService? prompts = null, SettingsStore? settingsStore = null)
    {
        _events = events;
        _audit = audit;
        _grants = grants;
        _prompts = prompts ?? new PromptService(events, settingsStore: settingsStore);
    }

    public PermissionGrantStore Grants => _grants;

    /// <summary>Every approval and every module question goes through this one service.</summary>
    public PromptService Prompts => _prompts;

    /// <summary>How many "allow for this session" grants are active. Shown in Settings.</summary>
    public int SessionGrantCount
    {
        get
        {
            lock (_sessionLock)
            {
                return _sessionGrants.Count;
            }
        }
    }

    /// <summary>Forgets every session grant right away.</summary>
    public void ClearSessionGrants()
    {
        lock (_sessionLock)
        {
            _sessionGrants.Clear();
        }
    }

    public void ClearTurnGrants(string turnId)
    {
        lock (_sessionLock)
        {
            _turnGrants.Remove(turnId);
        }
    }

    private static string SessionKey(string moduleId, ModuleTool tool) =>
        string.IsNullOrWhiteSpace(tool.PermissionScope)
            ? $"{moduleId}|tool:{tool.Name}"
            : $"{moduleId}|scope:{tool.PermissionScope}";

    private bool IsSessionGranted(string key)
    {
        lock (_sessionLock)
        {
            return _sessionGrants.Contains(key);
        }
    }

    private bool IsTurnGranted(string turnId, string key)
    {
        lock (_sessionLock)
        {
            return _turnGrants.TryGetValue(turnId, out HashSet<string>? grants) && grants.Contains(key);
        }
    }

    /// <summary>Look-only access can be answered with a plain "yes". Anything that acts needs "approve".</summary>
    private static PromptRisk RiskOf(ModuleTool tool) =>
        tool.Tier == ToolTier.Reflex ||
        (!tool.AlwaysConfirm && ToolPresentationCatalog.AccessOf(tool) == ToolAccess.Reads)
            ? PromptRisk.Normal
            : PromptRisk.Sensitive;

    /// <summary>Asks for permission according to the tool's tier and the user's saved grants.</summary>
    public Task<AuthorizationOutcome> AuthorizeAsync(string moduleId, string moduleName, ModuleTool tool,
        string plainEnglishAction, CancellationToken cancellationToken) =>
        AuthorizeAsync(moduleId, moduleName, tool, plainEnglishAction, Guid.NewGuid().ToString("N"),
            cancellationToken);

    public async Task<AuthorizationOutcome> AuthorizeAsync(string moduleId, string moduleName, ModuleTool tool,
        string plainEnglishAction, string turnId, CancellationToken cancellationToken)
    {
        string auditAction = string.IsNullOrWhiteSpace(tool.PermissionScope)
            ? plainEnglishAction
            : $"[{tool.PermissionScope}] {plainEnglishAction}";

        if (tool.Tier == ToolTier.Reflex && !tool.AlwaysConfirm)
        {
            IrisEvent.Log("Sandbox", "permission.auto", LogLevel.Trace,
                data: new Dictionary<string, string?> { ["tool"] = tool.Name, ["module"] = moduleId, ["tier"] = "reflex" });
            await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "auto", auditAction, cancellationToken)
                .ConfigureAwait(false);
            return AuthorizationOutcome.Allowed();
        }

        string grantKey = SessionKey(moduleId, tool);
        if (!tool.AlwaysConfirm && IsTurnGranted(turnId, grantKey))
        {
            await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "turn", auditAction, cancellationToken)
                .ConfigureAwait(false);
            return AuthorizationOutcome.Allowed();
        }

        if (!tool.AlwaysConfirm && tool.SessionTrust && IsSessionGranted(grantKey))
        {
            IrisEvent.Log("Sandbox", "permission.session_granted", LogLevel.Trace,
                data: new Dictionary<string, string?> { ["tool"] = tool.Name, ["module"] = moduleId });
            await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "session", auditAction, cancellationToken)
                .ConfigureAwait(false);
            return AuthorizationOutcome.Allowed();
        }

        if (!tool.AlwaysConfirm && !string.IsNullOrWhiteSpace(tool.PermissionScope) &&
            _grants.IsScopeGranted(moduleId, tool.PermissionScope))
        {
            IrisEvent.Log("Sandbox", "permission.scope_granted", LogLevel.Trace,
                data: new Dictionary<string, string?>
                {
                    ["tool"] = tool.Name,
                    ["module"] = moduleId,
                    ["scope"] = tool.PermissionScope
                });
            await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "granted", auditAction, cancellationToken)
                .ConfigureAwait(false);
            return AuthorizationOutcome.Allowed();
        }

        if (!tool.AlwaysConfirm && tool.Tier == ToolTier.Scoped)
        {
            if (string.IsNullOrWhiteSpace(tool.PermissionScope) && _grants.IsGranted(moduleId, tool.Name))
            {
                IrisEvent.Log("Sandbox", "permission.granted", LogLevel.Trace,
                    data: new Dictionary<string, string?> { ["tool"] = tool.Name, ["module"] = moduleId, ["tier"] = "scoped" });
                await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "granted", auditAction, cancellationToken)
                    .ConfigureAwait(false);
                return AuthorizationOutcome.Allowed();
            }
        }

        if (!tool.AlwaysConfirm && tool.Tier == ToolTier.SystemCritical && string.IsNullOrWhiteSpace(tool.PermissionScope) &&
            _grants.IsGranted(moduleId, tool.Name))
        {
            IrisEvent.Log("Sandbox", "permission.granted", LogLevel.Trace,
                data: new Dictionary<string, string?> { ["tool"] = tool.Name, ["module"] = moduleId, ["tier"] = "system-critical" });
            await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "granted", auditAction, cancellationToken)
                .ConfigureAwait(false);
            return AuthorizationOutcome.Allowed();
        }

        // The card says the action in plain words. Scope and "always" details sit in a small
        // caption the prompt carries itself, so nothing here reads like a config file.
        PermissionRequest request = new(moduleId, moduleName, tool.Name, plainEnglishAction,
            tool.Tier, tool.PermissionScope, tool.AlwaysConfirm, tool.SessionTrust, RiskOf(tool),
            _prompts.Policy.ConfirmTimeout);

        // Queue first, announce second: a subscriber that answers synchronously still lands
        // on a prompt that already exists.
        Task<PromptAnswer> pending = _prompts.AskAsync(request.Prompt, cancellationToken);
        _events.Publish(new PermissionRequestedEvent { Request = request });
        IrisEvent.Log("Sandbox", "permission.asked", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["tool"] = tool.Name,
                ["module"] = moduleId,
                ["tier"] = tool.Tier.ToString(),
                ["scope"] = tool.PermissionScope
            });

        PermissionDecision decision;
        PermissionStop stop = PermissionStop.Denied;
        try
        {
            PromptAnswer answer = await pending.ConfigureAwait(false);
            decision = PermissionRequest.DecisionFor(answer.OptionId);
            if (answer.TimedOut)
            {
                stop = PermissionStop.TimedOut;
                CompanionLog.Warning("Sandbox", $"No answer for '{tool.Name}' in time, so it was not allowed this time.");
            }
            else if (answer.WasCancelled)
            {
                stop = PermissionStop.Interrupted;
            }
        }
        catch (OperationCanceledException)
        {
            decision = PermissionDecision.Deny;
            stop = PermissionStop.Interrupted;
        }

        if (tool.AlwaysConfirm && decision == PermissionDecision.AllowAlways)
        {
            decision = PermissionDecision.AllowOnce;
        }

        if (decision == PermissionDecision.AllowSession)
        {
            if (tool.SessionTrust && !tool.AlwaysConfirm)
            {
                lock (_sessionLock)
                {
                    _sessionGrants.Add(SessionKey(moduleId, tool));
                }
            }
            else
            {
                decision = PermissionDecision.AllowOnce;
            }
        }

        if (decision == PermissionDecision.AllowTurn)
        {
            lock (_sessionLock)
            {
                if (!_turnGrants.TryGetValue(turnId, out HashSet<string>? grants))
                {
                    grants = new HashSet<string>(StringComparer.Ordinal);
                    _turnGrants.Add(turnId, grants);
                }
                grants.Add(grantKey);
            }
        }

        if (decision == PermissionDecision.AllowAlways)
        {
            if (string.IsNullOrWhiteSpace(tool.PermissionScope))
            {
                _grants.Grant(moduleId, tool.Name);
            }
            else
            {
                _grants.GrantScope(moduleId, tool.PermissionScope);
            }
        }

        string outcome = stop switch
        {
            PermissionStop.TimedOut => "timeout",
            PermissionStop.Interrupted => "interrupted",
            _ => decision switch
            {
                PermissionDecision.AllowOnce => "allowed-once",
                PermissionDecision.AllowTurn => "allowed-turn",
                PermissionDecision.AllowSession => "allowed-session",
                PermissionDecision.AllowAlways => "allowed-always",
                _ => "denied"
            }
        };

        IrisEvent.Log("Sandbox", "permission.decided", LogLevel.Info,
            data: new Dictionary<string, string?>
            {
                ["tool"] = tool.Name,
                ["module"] = moduleId,
                ["decision"] = outcome,
                ["scope"] = tool.PermissionScope
            });

        await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, outcome, auditAction, cancellationToken)
            .ConfigureAwait(false);

        if (decision != PermissionDecision.Deny)
        {
            return AuthorizationOutcome.Allowed();
        }

        // The refusal has to say what actually happened. The model that reads it must
        // be able to tell a no from a missed prompt — they call for different answers.
        string refusalReason = stop switch
        {
            PermissionStop.TimedOut =>
                $"Permission timed out: no one answered the prompt for {tool.Name} in time. " +
                "Nobody refused — answer the truth and offer the next step in one short sentence.",
            PermissionStop.Interrupted =>
                $"Permission prompt for {tool.Name} was interrupted (turn stopped).",
            _ =>
                $"The user was asked about {tool.Name} and did not allow it. That is a no: state it plainly " +
                "and do not ask again this turn."
        };
        return AuthorizationOutcome.Refused(refusalReason);
    }

    /// <summary>
    /// Ensures a requested path really lives inside her workspace.
    /// This single method is what makes the "scoped" tier meaningful.
    /// </summary>
    public static string ResolveWorkspacePath(string requestedPath)
    {
        string workspaceRoot = Path.GetFullPath(CompanionPaths.WorkspaceDirectory);
        string candidate = Path.IsPathRooted(requestedPath)
            ? Path.GetFullPath(requestedPath)
            : Path.GetFullPath(Path.Combine(workspaceRoot, requestedPath));

        string rootWithSeparator = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        bool insideWorkspace = candidate.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase) ||
                               candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);

        if (!insideWorkspace)
        {
            throw new UnauthorizedAccessException(
                "That path is outside my workspace, so I need your explicit permission first.");
        }

        return candidate;
    }
}

/// <summary>Everything she was allowed to do, kept as an append-only text file.</summary>
public interface IAuditTrail
{
    Task RecordAsync(string moduleId, string toolName, ToolTier tier, string outcome, string description,
        CancellationToken cancellationToken);

    IReadOnlyList<AuditEntry> ReadRecent(int maximumEntries);
}

/// <summary>One line of the audit trail.</summary>
public sealed class AuditEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string ModuleId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public int Tier { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// JSON-lines audit trail. Chosen over a database table because an interrupted
/// append can at worst corrupt the last line, which the reader simply skips.
/// </summary>
public sealed class FileAuditTrail : IAuditTrail
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _filePath;
    private const long MaximumAuditBytes = 8L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public FileAuditTrail(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>Default location: beside her memory, on the same volume.</summary>
    public static FileAuditTrail CreateDefault() =>
        new(Path.Combine(CompanionPaths.MemoryDirectory, "audit.jsonl"));

    public async Task RecordAsync(string moduleId, string toolName, ToolTier tier, string outcome,
        string description, CancellationToken cancellationToken)
    {
        AuditEntry entry = new()
        {
            ModuleId = moduleId,
            ToolName = toolName,
            Tier = (int)tier,
            Outcome = outcome,
            Description = description
        };

        string line = JsonSerializer.Serialize(entry, SerializerOptions);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            RotateIfFull();
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Audit", $"Could not write audit line: {exception.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Rotates the audit file into an archive when it grows past the bound, keeping the
    /// two newest archives. The trail used to be a forever-append: months of activity
    /// left one jsonl file that every ReadRecent walked from the start, growing without
    /// bound (2026-10-27 audit).
    /// </summary>
    private void RotateIfFull()
    {
        FileInfo file = new(_filePath);
        if (!file.Exists || file.Length < MaximumAuditBytes)
        {
            return;
        }

        string archive = _filePath + $".{DateTimeOffset.Now:yyyyMMdd-HHmmss}.old";
        File.Move(_filePath, archive, overwrite: true);

        string pattern = Path.GetFileName(_filePath) + ".*.old";
        string? directory = Path.GetDirectoryName(_filePath);
        if (directory is null)
        {
            return;
        }

        List<string> archives = Directory.EnumerateFiles(directory, pattern)
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();
        foreach (string stale in archives.Skip(2))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception exception)
            {
                CompanionLog.Trace("Audit", $"Could not delete a stale audit archive: {exception.Message}");
            }
        }
    }

    public IReadOnlyList<AuditEntry> ReadRecent(int maximumEntries)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return Array.Empty<AuditEntry>();
            }

            List<AuditEntry> entries = new();
            foreach (string line in File.ReadLines(_filePath).TakeLast(maximumEntries))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    AuditEntry? entry = JsonSerializer.Deserialize<AuditEntry>(line, SerializerOptions);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
                catch
                {
                    // A torn last line is expected after a crash; skip it.
                }
            }

            entries.Reverse();
            return entries;
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Audit", $"Could not read audit trail: {exception.Message}");
            return Array.Empty<AuditEntry>();
        }
    }
}

/// <summary>
/// Remembers "always allow this" answers. Stored separately from settings so a
/// reset of permissions can never disturb her personality or memory.
/// </summary>
public sealed class PermissionGrantStore
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly HashSet<string> _grantedKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _grantedScopes = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public PermissionGrantStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>Default file lives in the config folder next to settings.json.</summary>
    public static PermissionGrantStore CreateDefault() =>
        new(Path.Combine(CompanionPaths.ConfigDirectory, "permissions.json"));

    public bool IsGranted(string moduleId, string toolName)
    {
        lock (_gate)
        {
            return _grantedKeys.Contains(ComposeKey(moduleId, toolName));
        }
    }

    public bool IsScopeGranted(string moduleId, string permissionScope)
    {
        lock (_gate)
        {
            return _grantedScopes.Contains(ComposeKey(moduleId, permissionScope));
        }
    }

    public void Grant(string moduleId, string toolName)
    {
        lock (_gate)
        {
            _grantedKeys.Add(ComposeKey(moduleId, toolName));
        }

        Save();
    }

    public void GrantScope(string moduleId, string permissionScope)
    {
        lock (_gate)
        {
            _grantedScopes.Add(ComposeKey(moduleId, permissionScope));
        }

        Save();
    }

    public void Revoke(string moduleId, string toolName)
    {
        lock (_gate)
        {
            _grantedKeys.Remove(ComposeKey(moduleId, toolName));
        }

        Save();
    }

    public void RevokeScope(string moduleId, string permissionScope)
    {
        lock (_gate)
        {
            _grantedScopes.Remove(ComposeKey(moduleId, permissionScope));
        }

        Save();
    }

    public void RevokeAllForModule(string moduleId)
    {
        lock (_gate)
        {
            string prefix = moduleId.ToLowerInvariant() + "::";
            foreach (string key in _grantedKeys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _grantedKeys.Remove(key);
            }
            foreach (string key in _grantedScopes.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                _grantedScopes.Remove(key);
            }
        }

        Save();
    }

    /// <summary>Legacy per-tool grants, retained for existing installs.</summary>
    public IReadOnlyList<(string ModuleId, string ToolName)> ListAll()
    {
        lock (_gate)
        {
            return _grantedKeys
                .Select(key => key.Split("::", 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => (parts[0], parts[1]))
                .ToList();
        }
    }

    /// <summary>Every remembered capability grant, separate from legacy tool grants.</summary>
    public IReadOnlyList<(string ModuleId, string PermissionScope)> ListScopeGrants()
    {
        lock (_gate)
        {
            return _grantedScopes
                .Select(key => key.Split("::", 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => (parts[0], parts[1]))
                .ToList();
        }
    }

    private static string ComposeKey(string moduleId, string toolName) =>
        $"{moduleId.ToLowerInvariant()}::{toolName.ToLowerInvariant()}";

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            string json = File.ReadAllText(_filePath);
            List<GrantRecord>? records = JsonSerializer.Deserialize<List<GrantRecord>>(json, SerializerOptions);
            if (records is null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (GrantRecord record in records)
                {
                    if (!string.IsNullOrWhiteSpace(record.PermissionScope))
                    {
                        _grantedScopes.Add(ComposeKey(record.ModuleId, record.PermissionScope));
                    }
                    else if (!string.IsNullOrWhiteSpace(record.ToolName))
                    {
                        _grantedKeys.Add(ComposeKey(record.ModuleId, record.ToolName));
                    }
                }
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Sandbox", $"Could not read permission grants: {exception.Message}");
        }
    }

    private void Save()
    {
        try
        {
            List<GrantRecord> records;
            lock (_gate)
            {
                records = _grantedKeys
                    .Select(key => key.Split("::", 2))
                    .Where(parts => parts.Length == 2)
                    .Select(parts => new GrantRecord { ModuleId = parts[0], ToolName = parts[1] })
                    .Concat(_grantedScopes
                        .Select(key => key.Split("::", 2))
                        .Where(parts => parts.Length == 2)
                        .Select(parts => new GrantRecord { ModuleId = parts[0], PermissionScope = parts[1] }))
                    .ToList();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            string temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, SerializerOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Sandbox", $"Could not save permission grants: {exception.Message}");
        }
    }

    private sealed class GrantRecord
    {
        public string ModuleId { get; set; } = string.Empty;
        public string ToolName { get; set; } = string.Empty;
        public string? PermissionScope { get; set; }
    }
}
