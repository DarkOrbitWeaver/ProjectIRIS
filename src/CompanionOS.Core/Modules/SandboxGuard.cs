using System.Text.Json;
using System.Text.Json.Serialization;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;

namespace CompanionOS.Core.Modules;

/// <summary>What the user decided about a dangerous action.</summary>
public enum PermissionDecision
{
    Deny,
    AllowOnce,
    AllowAlways
}

/// <summary>
/// A question waiting for the user: "Iris wants to close Discord — allow?"
/// The interface answers it by calling <see cref="Answer"/>.
/// </summary>
public sealed class PermissionRequest
{
    private readonly TaskCompletionSource<PermissionDecision> _answerSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PermissionRequest(string moduleId, string moduleName, string toolName, string question, ToolTier tier,
        string? permissionScope = null, bool alwaysConfirm = false)
    {
        ModuleId = moduleId;
        ModuleName = moduleName;
        ToolName = toolName;
        Question = question;
        Tier = tier;
        PermissionScope = permissionScope;
        CanAlwaysAllow = !alwaysConfirm;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string ModuleId { get; }
    public string ModuleName { get; }
    public string ToolName { get; }
    public string Question { get; }
    public ToolTier Tier { get; }
    public string? PermissionScope { get; }
    public bool CanAlwaysAllow { get; }
    public DateTimeOffset RequestedAt { get; } = DateTimeOffset.Now;

    /// <summary>Completes the request. Later answers are ignored.</summary>
    public void Answer(PermissionDecision decision) => _answerSource.TrySetResult(decision);

    /// <summary>Cancels the request, which counts as a refusal.</summary>
    public void Cancel() => _answerSource.TrySetResult(PermissionDecision.Deny);

    /// <summary>Waits for the user, or refuses when time runs out.</summary>
    public Task<PermissionDecision> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _answerSource.Task.WaitAsync(timeout, cancellationToken);
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
    private static readonly TimeSpan PermissionTimeout = TimeSpan.FromSeconds(15);

    private readonly EventBus _events;
    private readonly IAuditTrail _audit;
    private readonly PermissionGrantStore _grants;

    public SandboxGuard(EventBus events, IAuditTrail audit, PermissionGrantStore grants)
    {
        _events = events;
        _audit = audit;
        _grants = grants;
    }

    public PermissionGrantStore Grants => _grants;

    /// <summary>Asks for permission according to the tool's tier and the user's saved grants.</summary>
    public async Task<bool> AuthorizeAsync(string moduleId, string moduleName, ModuleTool tool,
        string plainEnglishAction, CancellationToken cancellationToken)
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
            return true;
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
            return true;
        }

        if (!tool.AlwaysConfirm && tool.Tier == ToolTier.Scoped)
        {
            if (string.IsNullOrWhiteSpace(tool.PermissionScope) && _grants.IsGranted(moduleId, tool.Name))
            {
                IrisEvent.Log("Sandbox", "permission.granted", LogLevel.Trace,
                    data: new Dictionary<string, string?> { ["tool"] = tool.Name, ["module"] = moduleId, ["tier"] = "scoped" });
                await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "granted", auditAction, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
        }

        if (!tool.AlwaysConfirm && tool.Tier == ToolTier.SystemCritical && string.IsNullOrWhiteSpace(tool.PermissionScope) &&
            _grants.IsGranted(moduleId, tool.Name))
        {
            IrisEvent.Log("Sandbox", "permission.granted", LogLevel.Trace,
                data: new Dictionary<string, string?> { ["tool"] = tool.Name, ["module"] = moduleId, ["tier"] = "system-critical" });
            await _audit.RecordAsync(moduleId, tool.Name, tool.Tier, "granted", auditAction, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        string question = string.IsNullOrWhiteSpace(tool.PermissionScope)
            ? plainEnglishAction
            : $"{plainEnglishAction} Requested access: {tool.PermissionScope}." +
              (tool.AlwaysConfirm ? " Iris will ask again before this action is run." :
                  $" Always allow will remember this capability for {moduleName}.");
        PermissionRequest request = new(moduleId, moduleName, tool.Name, question,
            tool.Tier, tool.PermissionScope, tool.AlwaysConfirm);
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
        try
        {
            decision = await request.WaitAsync(PermissionTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            decision = PermissionDecision.Deny;
            CompanionLog.Warning("Sandbox", $"No answer for '{tool.Name}' in 15 s, so it was refused.");
        }
        catch (OperationCanceledException)
        {
            decision = PermissionDecision.Deny;
        }

        if (tool.AlwaysConfirm && decision == PermissionDecision.AllowAlways)
        {
            decision = PermissionDecision.AllowOnce;
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

        string outcome = decision switch
        {
            PermissionDecision.AllowOnce => "allowed-once",
            PermissionDecision.AllowAlways => "allowed-always",
            _ => "denied"
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

        return decision != PermissionDecision.Deny;
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
