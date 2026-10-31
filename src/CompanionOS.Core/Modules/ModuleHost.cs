using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;

namespace CompanionOS.Core.Modules;

/// <summary>
/// The body: it holds every installed hand, decides which hands a request needs, and
/// makes sure nothing dangerous happens without permission.
///
/// Tool schemas are offered progressively. The prompt carries a compact map of every
/// enabled capability; this class offers relevant schemas plus the always-available
/// discovery tool, which can load more schemas into the live turn as its task evolves.
/// </summary>
public sealed class ModuleHost : IAsyncDisposable
{
    /// <summary>
    /// Turn cap for the whole tool offer. The built-in catalog is around 43 tools —
    /// comfortably under this — so it binds only when third-party modules balloon the
    /// body. It exists to catch catastrophic accidental picks, not everyday turns.
    /// </summary>
    public const int MaximumToolsPerTurn = 64;
    public const int MaximumInitialToolsPerTurn = 12;
    public const int MaximumDiscoveredToolsPerRequest = 8;

    private const string DisabledModulesFile = "modules.disabled.json";
    private const int ToolTimeoutSeconds = 45;
    private const string CapabilityDiscoveryTool = "search_capabilities";
    private static readonly HashSet<string> DiscoveryStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "can", "could", "do", "for", "from", "get", "give", "help",
        "how", "i", "in", "is", "it", "me", "my", "of", "on", "or", "please", "show", "some",
        "that", "the", "this", "to", "use", "what", "when", "where", "which", "who", "would",
        "you", "your"
    };

    private readonly SettingsStore _settings;
    private readonly EventBus _events;
    private readonly SandboxGuard _sandbox;
    private readonly IMemoryAccess _memory;
    private readonly BackgroundTaskManager _backgroundTasks;
    private readonly ModuleUiService _moduleUi;
    private readonly object _registryLock = new();
    private readonly Dictionary<string, IAssistantModule> _modulesById = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Insertion order of installed modules; keeps the tool offer deterministic.</summary>
    private readonly List<IAssistantModule> _registrationOrder = new();
    private readonly Dictionary<string, IAssistantModule> _toolOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledModuleIds = new(StringComparer.OrdinalIgnoreCase);

    public ModuleHost(SettingsStore settings, EventBus events, SandboxGuard sandbox, IMemoryAccess memory)
    {
        _settings = settings;
        _events = events;
        _sandbox = sandbox;
        _memory = memory;
        _backgroundTasks = new BackgroundTaskManager(events);
        _moduleUi = new ModuleUiService(events);
        LoadDisabledModules();
    }

    /// <summary>Bounded background work contributed by explicitly opted-in module tools.</summary>
    public IBackgroundTaskAccess BackgroundTasks => _backgroundTasks;

    /// <summary>The shared question service: approvals, choices and numbered lists.</summary>
    public CompanionOS.Core.Prompts.PromptService Prompts => _sandbox.Prompts;

    /// <summary>
    /// Her body as seen from a module. Set once by the runtime after the brain exists,
    /// because the brain owns the modules and the modules need the brain.
    /// </summary>
    public IAssistantHostServices Host { get; set; } = null!;

    /// <summary>Every installed module, enabled or not.</summary>
    public IReadOnlyList<IAssistantModule> Modules
    {
        get
        {
            lock (_registryLock)
            {
                return _modulesById.Values.ToList();
            }
        }
    }

    /// <summary>True when at least one module provides this tool.</summary>
    public bool HasTool(string toolName)
    {
        lock (_registryLock)
        {
            return _toolOwners.ContainsKey(toolName);
        }
    }

    /// <summary>Raised whenever a module is added, removed, enabled or disabled.</summary>
    public event Action? RegistryChanged;

    /// <summary>Adds a module (built-in or external) and initialises it.</summary>
    public async Task RegisterAsync(IAssistantModule module, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);

        lock (_registryLock)
        {
            if (_modulesById.TryGetValue(module.Id, out IAssistantModule? replaced))
            {
                foreach (ModuleTool oldTool in replaced.Tools)
                {
                    if (_toolOwners.TryGetValue(oldTool.Name, out IAssistantModule? owner) && owner.Id == module.Id)
                    {
                        _toolOwners.Remove(oldTool.Name);
                    }
                }
            }
            else
            {
                // Replace keeps the original insertion slot; a fresh id takes the tail.
                _registrationOrder.Add(module);
            }
            _modulesById[module.Id] = module;
            foreach (ModuleTool tool in module.Tools)
            {
                if (_toolOwners.ContainsKey(tool.Name))
                {
                    CompanionLog.Warning("Modules",
                        $"Tool name '{tool.Name}' is already taken; the newest module keeps it.");
                }

                _toolOwners[tool.Name] = module;
            }
        }

        bool initialized;
        try
        {
            await module.InitializeAsync(BuildContext(module), cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        catch (Exception exception)
        {
            // A failed module must not stay routable: its tools were already mapped onto it
            // above, so the first call would hand the model an uninitialized module and the
            // answer would be "Object reference not set..." — evidence the model cannot use
            // (2026-10-27 audit). Retire the registration and say so instead of publishing
            // a disagreement between the registry and reality.
            CompanionLog.Error("Modules", $"Module '{module.Id}' failed to initialise; its tools are retired.", exception);
            initialized = false;
            RetireModuleFromRegistry(module.Id);

            // A retired module that already grabbed IDisposable state in its constructor
            // (a CTS, timers) loses it here: a failed start must not leave that behind.
            try
            {
                (module as IDisposable)?.Dispose();
            }
            catch (Exception disposeFailure)
            {
                CompanionLog.Trace("Modules",
                    $"Module '{module.Id}' did not dispose after a failed start: {disposeFailure.Message}");
            }
            _events.Publish(new ModuleRegistryChangedEvent { ModuleId = module.Id, Change = "failed" });
            _events.PublishNotice(
                $"The {module.Id} module failed to start and is out of service: {exception.Message}.",
                NoticeSeverity.Warning);
        }

        if (initialized)
        {
            _events.Publish(new ModuleRegistryChangedEvent { ModuleId = module.Id, Change = "installed" });
        }

        RegistryChanged?.Invoke();
    }

    /// <summary>Detaches one module and its tools from the registry under the lock.</summary>
    private void RetireModuleFromRegistry(string moduleId)
    {
        lock (_registryLock)
        {
            if (!_modulesById.Remove(moduleId, out IAssistantModule? module))
            {
                return;
            }

            _registrationOrder.Remove(module);

            foreach (ModuleTool tool in module.Tools)
            {
                if (_toolOwners.TryGetValue(tool.Name, out IAssistantModule? owner) && owner.Id == moduleId)
                {
                    _toolOwners.Remove(tool.Name);
                }
            }
        }
    }

    /// <summary>Removes a module and everything it contributed.</summary>
    public async Task UnregisterAsync(string moduleId)
    {
        IAssistantModule? module;
        lock (_registryLock)
        {
            if (!_modulesById.Remove(moduleId, out module))
            {
                return;
            }

            _registrationOrder.Remove(module);

            foreach (ModuleTool tool in module.Tools)
            {
                if (_toolOwners.TryGetValue(tool.Name, out IAssistantModule? owner) && owner.Id == moduleId)
                {
                    _toolOwners.Remove(tool.Name);
                }
            }
        }

        try
        {
            await module.ShutdownAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Modules", $"Module '{moduleId}' did not shut down cleanly: {exception.Message}");
        }
        finally
        {
            // Same last sweep as DisposeAsync: ShutdownAsync covers the interface's
            // idea of clean; modules carrying extra IDisposable state get it released
            // here, guarded so a dirty Dispose cannot escalate into a registry break.
            try
            {
                (module as IDisposable)?.Dispose();
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Modules", $"Module '{moduleId}' did not dispose cleanly: {exception.Message}");
            }
        }

        _events.Publish(new ModuleRegistryChangedEvent { ModuleId = moduleId, Change = "removed" });
        RegistryChanged?.Invoke();
    }

    /// <summary>Turns a module on or off without uninstalling it.</summary>
    public void SetEnabled(string moduleId, bool isEnabled)
    {
        lock (_registryLock)
        {
            if (isEnabled)
            {
                _disabledModuleIds.Remove(moduleId);
            }
            else
            {
                _disabledModuleIds.Add(moduleId);
            }
        }

        SaveDisabledModules();
        _events.Publish(new ModuleRegistryChangedEvent
        {
            ModuleId = moduleId,
            Change = isEnabled ? "enabled" : "disabled"
        });
        RegistryChanged?.Invoke();
    }

    /// <summary>True when the module is installed and switched on.</summary>
    public bool IsEnabled(string moduleId)
    {
        lock (_registryLock)
        {
            return _modulesById.ContainsKey(moduleId) && !_disabledModuleIds.Contains(moduleId);
        }
    }

    /// <summary>Reads the persisted user preference even before an external module is registered.</summary>
    public bool IsEnabledSetting(string moduleId)
    {
        lock (_registryLock)
        {
            return !_disabledModuleIds.Contains(moduleId);
        }
    }

    /// <summary>Offers relevant schemas and the always-available discovery tool.</summary>
    public IReadOnlyList<ModuleTool> RouteTools(string userMessage, out IReadOnlyList<IAssistantModule> selectedModules)
    {
        List<IAssistantModule> enabled;
        lock (_registryLock)
        {
            enabled = _registrationOrder
                .Where(module => !_disabledModuleIds.Contains(module.Id))
                .ToList();
        }

        List<(IAssistantModule Module, ModuleTool Tool, int Score, int ModuleOrder)> candidates = new();
        for (int moduleIndex = 0; moduleIndex < enabled.Count; moduleIndex++)
        {
            IAssistantModule module = enabled[moduleIndex];
            foreach (ModuleTool tool in module.Tools)
            {
                int score = ScoreToolMatch(userMessage, module, tool);
                if (score > 0)
                {
                    candidates.Add((module, tool, score, moduleIndex));
                }
            }
        }

        List<ModuleTool> routedTools = new();
        HashSet<string> addedToolNames = new(StringComparer.OrdinalIgnoreCase);
        ModuleTool? discoveryTool = enabled
            .SelectMany(module => module.Tools)
            .FirstOrDefault(tool => tool.Name.Equals(CapabilityDiscoveryTool, StringComparison.OrdinalIgnoreCase));
        if (discoveryTool is not null)
        {
            routedTools.Add(discoveryTool);
            addedToolNames.Add(discoveryTool.Name);
        }

        foreach (var candidate in candidates
                     .OrderByDescending(candidate => candidate.Score)
                     .ThenByDescending(candidate => candidate.Module.AlwaysAvailable)
                     .ThenBy(candidate => candidate.ModuleOrder)
                     .ThenBy(candidate => candidate.Tool.Name, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(0, MaximumInitialToolsPerTurn - routedTools.Count)))
        {
            if (addedToolNames.Add(candidate.Tool.Name))
            {
                routedTools.Add(candidate.Tool);
            }
        }

        selectedModules = enabled
            .Where(module => module.Tools.Any(tool => addedToolNames.Contains(tool.Name)))
            .ToArray();

        IrisEvent.Log("Modules", "router.progressive", LogLevel.Debug,
            message: "Offered relevant schemas and kept the full enabled catalog discoverable.",
            data: new Dictionary<string, string?>
            {
                ["offered"] = routedTools.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["catalog"] = enabled.Sum(module => module.Tools.Count)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["discovery_tool"] = discoveryTool is null ? "missing" : discoveryTool.Name
            });

        return routedTools;
    }

    /// <summary>
    /// Searches every enabled module for detailed schemas to add after the model
    /// requests a capability. Existing offers are omitted and prior offers are kept.
    /// </summary>
    public IReadOnlyList<ModuleTool> DiscoverTools(string query, IEnumerable<string>? alreadyOfferedNames = null,
        int maximumResults = MaximumDiscoveredToolsPerRequest)
    {
        string cleanedQuery = (query ?? string.Empty).Trim();
        if (cleanedQuery.Length == 0)
        {
            return Array.Empty<ModuleTool>();
        }

        HashSet<string> alreadyOffered = new(alreadyOfferedNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        if (maximumResults <= 0)
        {
            return Array.Empty<ModuleTool>();
        }

        List<IAssistantModule> enabled;
        lock (_registryLock)
        {
            enabled = _registrationOrder
                .Where(module => !_disabledModuleIds.Contains(module.Id))
                .ToList();
        }

        return enabled
            .SelectMany((module, moduleIndex) => module.Tools.Select(tool =>
                (Tool: tool, Score: ScoreToolMatch(cleanedQuery, module, tool), ModuleOrder: moduleIndex)))
            .Where(candidate => candidate.Score > 0 && !alreadyOffered.Contains(candidate.Tool.Name) &&
                                !candidate.Tool.Name.Equals(CapabilityDiscoveryTool, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.ModuleOrder)
            .ThenBy(candidate => candidate.Tool.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maximumResults, 1, MaximumToolsPerTurn))
            .Select(candidate => candidate.Tool)
            .ToArray();
    }

    private static int ScoreToolMatch(string query, IAssistantModule module, ModuleTool tool)
    {
        HashSet<string> queryTerms = TokenizeForDiscovery(query)
            .Where(term => !DiscoveryStopWords.Contains(term))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (queryTerms.Count == 0)
        {
            return 0;
        }

        int toolScore = 0;
        foreach (string keyword in tool.RoutingKeywords)
        {
            if (keyword.Length > 1 && query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                toolScore += 12;
            }

            toolScore += TokenizeForDiscovery(keyword).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(queryTerms.Contains) * 5;
        }

        toolScore += TokenizeForDiscovery(tool.Name).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(queryTerms.Contains) * 7;
        toolScore += TokenizeForDiscovery(tool.Description).Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(queryTerms.Contains);
        int moduleNameScore = TokenizeForDiscovery(module.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count(queryTerms.Contains) * 3;
        if (toolScore == 0 && moduleNameScore == 0)
        {
            return 0;
        }

        int moduleContextScore = TokenizeForDiscovery(module.DisplayName + " " + module.Purpose + " " +
                                                     module.CapabilityDescription)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count(queryTerms.Contains) * 2;
        return (toolScore == 0 ? moduleNameScore : toolScore + moduleContextScore) +
               (module.AlwaysAvailable ? 20 : 0);
    }

    private static IEnumerable<string> TokenizeForDiscovery(string text)
    {
        StringBuilder token = new();
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (token.Length > 0)
            {
                yield return NormalizeDiscoveryToken(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return NormalizeDiscoveryToken(token.ToString());
        }
    }

    private static string NormalizeDiscoveryToken(string token)
    {
        if (token.Length > 5 && token.EndsWith("ing", StringComparison.Ordinal))
        {
            return token[..^3];
        }
        if (token.Length > 4 && token.EndsWith("ed", StringComparison.Ordinal))
        {
            return token[..^2];
        }
        if (token.Length > 4 && token.EndsWith("er", StringComparison.Ordinal))
        {
            return token[..^2];
        }
        if (token.Length > 4 &&
            (token.EndsWith("ches", StringComparison.Ordinal) ||
             token.EndsWith("shes", StringComparison.Ordinal) ||
             token.EndsWith("sses", StringComparison.Ordinal) ||
             token.EndsWith("xes", StringComparison.Ordinal) ||
             token.EndsWith("zes", StringComparison.Ordinal)))
        {
            return token[..^2];
        }
        if (token.Length > 3 && token.EndsWith('s'))
        {
            return token[..^1];
        }

        return token;
    }

    /// <summary>Shows validated tool calls as waiting rows before any action or permission prompt begins.</summary>
    public void PlanTool(string turnId, string toolName, JsonElement arguments)
    {
        IAssistantModule? owner;
        ModuleTool? tool;
        lock (_registryLock)
        {
            owner = _toolOwners.GetValueOrDefault(toolName);
            tool = owner?.Tools.FirstOrDefault(candidate =>
                candidate.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        }

        if (owner is null || tool is null || !IsEnabled(owner.Id) ||
            !ToolSchemaValidator.TryValidate(arguments, tool.ParametersJson, out _))
        {
            return;
        }

        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.ToolPlanned,
            TurnId = turnId,
            Text = ToolPresentationCatalog.Resolve(tool, arguments).Doing,
            Detail = owner.DisplayName,
            ModuleId = owner.Id,
            ToolName = tool.Name,
            IconKey = ToolIconKey(owner, tool, arguments)
        });
    }

    /// <summary>
    /// What she can actually do, one line per enabled module with its tool names. This
    /// feeds the system prompt's capability block AND search_capabilities; a generic
    /// sentence once stood here, which is how her ability-discovery tool went blind.
    /// </summary>
    public string DescribeCapabilities()
    {
        List<IAssistantModule> modules;
        lock (_registryLock)
        {
            modules = _registrationOrder
                .Where(module => !_disabledModuleIds.Contains(module.Id))
                .ToList();
        }

        if (modules.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (IAssistantModule module in modules)
        {
            builder.Append(module.DisplayName);
            builder.Append(": ");
            builder.AppendLine(string.Join(", ", module.Tools.Select(tool => tool.Name)));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// The prompt-side capability map: module purpose and tool names, but no argument
    /// schemas. This keeps capability awareness while schemas load progressively.
    /// </summary>
    public string DescribeCapabilitiesOverview()
    {
        List<IAssistantModule> modules;
        lock (_registryLock)
        {
            modules = _registrationOrder
                .Where(module => !_disabledModuleIds.Contains(module.Id))
                .ToList();
        }

        if (modules.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (IAssistantModule module in modules)
        {
            string purpose = string.IsNullOrWhiteSpace(module.Purpose)
                ? "provides hands and senses for this body"
                : module.Purpose.Trim();
            builder.Append($"{module.DisplayName}: {purpose} Tools: ");
            builder.AppendLine(string.Join(", ", module.Tools.Select(tool => tool.Name)));
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Runs one tool call through the sandbox and reports what happened.</summary>
    public async Task<ToolResult> ExecuteAsync(string toolName, JsonElement arguments, string turnId,
        CancellationToken cancellationToken, string? requiredModuleId = null)
    {
        IAssistantModule? owner;
        ModuleTool? tool;

        lock (_registryLock)
        {
            owner = _toolOwners.TryGetValue(toolName, out IAssistantModule? foundModule) ? foundModule : null;
            tool = owner?.Tools.FirstOrDefault(candidate =>
                candidate.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
        }

        if (owner is null || tool is null ||
            (requiredModuleId is not null && !owner.Id.Equals(requiredModuleId, StringComparison.OrdinalIgnoreCase)))
        {
            IrisEvent.Log("Modules", "tool.unknown", LogLevel.Warning, message: $"No tool named '{toolName}'.",
                data: new Dictionary<string, string?>
                {
                    ["tool"] = toolName,
                    ["turn"] = turnId
                });
            return ToolResult.Fail(requiredModuleId is null
                ? $"I do not have a tool called '{toolName}'."
                : $"Module '{requiredModuleId}' does not provide the '{toolName}' action.");
        }

        if (!IsEnabled(owner.Id))
        {
            IrisEvent.Log("Modules", "tool.disabled", LogLevel.Warning, message: $"{owner.DisplayName} is off.",
                data: new Dictionary<string, string?>
                {
                    ["tool"] = toolName,
                    ["module"] = owner.Id,
                    ["turn"] = turnId
                });
            return ToolResult.Fail($"{owner.DisplayName} is switched off right now.");
        }

        if (!ToolSchemaValidator.TryValidate(arguments, tool.ParametersJson, out string schemaFailure))
        {
            ToolResult invalidArguments = ToolResult.Fail(
                $"I did not run {tool.Name} because its arguments did not match the action's requirements. {schemaFailure}.",
                ToolFailureKind.InvalidArguments);
            PublishToolStarted(turnId, owner, tool, arguments);
            PublishToolFinished(turnId, owner, tool, invalidArguments, arguments);
            IrisEvent.Log("Modules", "tool.arguments.invalid", LogLevel.Warning,
                message: $"Rejected arguments for '{tool.Name}': {schemaFailure}.",
                data: new Dictionary<string, string?>
                {
                    ["tool"] = tool.Name,
                    ["module"] = owner.Id,
                    ["turn"] = turnId
                });
            return invalidArguments;
        }

        AuthorizationOutcome authorization = await _sandbox
            .AuthorizeAsync(owner.Id, owner.DisplayName, tool, DescribeAction(owner, tool, arguments), turnId,
                cancellationToken)
            .ConfigureAwait(false);

        if (!authorization.IsAllowed)
        {
            ToolFailureKind kind = authorization.Stop switch
            {
                PermissionStop.TimedOut => ToolFailureKind.PermissionTimedOut,
                PermissionStop.Interrupted => ToolFailureKind.Cancelled,
                _ => ToolFailureKind.PermissionDenied
            };
            ToolResult refusal = ToolResult.Fail(authorization.RefusalReason, kind);
            PublishToolFinished(turnId, owner, tool, refusal, arguments);
            return refusal;
        }

        if (tool.BackgroundCapable)
        {
            _events.Publish(new ConversationEvent
            {
                Phase = ConversationPhase.ToolStarted,
                TurnId = turnId,
                Text = ToolPresentationCatalog.Resolve(tool, arguments).Doing,
                Detail = owner.DisplayName,
                ModuleId = owner.Id,
                ToolName = tool.Name,
                IconKey = ToolIconKey(owner, tool, arguments)
            });
            try
            {
                string taskId = _backgroundTasks.Start(owner.Id, owner.DisplayName, owner.IconKey, tool.Name,
                    (backgroundTurnId, token) => ExecuteAuthorizedToolAsync(owner, tool, arguments, backgroundTurnId,
                        token, publishConversationEvents: false, isBackground: true));
                ToolResult started = new()
                {
                    Succeeded = true,
                    ModelText = $"Started {owner.DisplayName}'s background task {taskId}. It will continue while you chat; use background-task status or cancel tools to follow it.",
                    DisplayText = $"Started in background · {taskId}",
                    BackgroundTaskId = taskId
                };
                PublishToolFinished(turnId, owner, tool, started, arguments);
                return started;
            }
            catch (InvalidOperationException exception)
            {
                ToolResult unavailable = ToolResult.Fail(exception.Message);
                PublishToolFinished(turnId, owner, tool, unavailable, arguments);
                return unavailable;
            }
        }

        return await ExecuteAuthorizedToolAsync(owner, tool, arguments, turnId, cancellationToken,
            publishConversationEvents: true, isBackground: false).ConfigureAwait(false);
    }

    /// <summary>Discards short-lived permission grants when the originating turn ends.</summary>
    public void CompleteTurn(string turnId) => _sandbox.ClearTurnGrants(turnId);

    private async Task<ToolResult> ExecuteAuthorizedToolAsync(IAssistantModule owner, ModuleTool tool,
        JsonElement arguments, string turnId, CancellationToken cancellationToken, bool publishConversationEvents,
        bool isBackground)
    {
        if (publishConversationEvents)
        {
            // The chip reads like an agent's hand, not a catalogue entry: what the hand
            // is doing and on what, built from the module's own narrator.
            _events.Publish(new ConversationEvent
            {
                Phase = ConversationPhase.ToolStarted,
                TurnId = turnId,
                // Plain words: "Resuming your music", not the first sentence of the model's description.
                Text = ToolPresentationCatalog.Resolve(tool, arguments).Doing,
                Detail = owner.DisplayName,
                ModuleId = owner.Id,
                ToolName = tool.Name,
                IconKey = ToolIconKey(owner, tool, arguments)
            });
        }

        ToolInvocation invocation = new()
        {
            ModuleId = owner.Id,
            Tool = tool,
            Arguments = arguments,
            TurnId = turnId
        };

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ToolResult result;
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(isBackground ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(tool.TimeoutSeconds ?? ToolTimeoutSeconds));

        try
        {
            Task<ToolResult> execution = owner.ExecuteAsync(invocation, timeoutSource.Token);
            Task finished = await Task.WhenAny(execution, Task.Delay(Timeout.Infinite, timeoutSource.Token))
                .ConfigureAwait(false);

            if (finished == execution)
            {
                result = await execution.ConfigureAwait(false);
            }
            else
            {
                // Caller cancellation is an interrupted turn; it is not a module timeout.
                cancellationToken.ThrowIfCancellationRequested();

                // A module can ignore cancellation and finish a side effect after we stop
                // waiting. Its late fault used to be swallowed with `_ = task.Exception`
                // — now it is logged, because an outcome the agent never sees still has
                // to exist somewhere in the day's records.
                _ = execution.ContinueWith(task =>
                {
                    Exception fault = task.Exception!.GetBaseException();
                    CompanionLog.Warning("Modules",
                        $"Late result from '{tool.Name}' after the {ToolTimeoutSeconds}s wait: {fault.Message}. " +
                        "Waiting stopped; the module kept running.");
                }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                result = ToolResult.Uncertain($"I stopped waiting for {tool.Name} after {ToolTimeoutSeconds} seconds. " +
                    "It may have finished without telling anyone — check the current state before repeating it.");
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            result = ToolResult.Uncertain($"{tool.Name} was stopped part-way through.");
        }
        catch (Exception exception)
        {
            CompanionLog.Error("Modules", $"Tool '{tool.Name}' threw.", exception);
            result = ToolResult.Fail($"That failed: {exception.Message}");
        }

        if (!isBackground && !result.Succeeded && !result.IsOutcomeUncertain &&
            ToolPresentationCatalog.AccessOf(tool) == ToolAccess.Reads &&
            !cancellationToken.IsCancellationRequested)
        {
            IrisEvent.Log("Modules", "tool.read_retry", LogLevel.Info,
                message: $"Retrying the failed read-only tool '{tool.Name}' once.",
                data: new Dictionary<string, string?> { ["tool"] = tool.Name, ["turn"] = turnId });
            result = await RetryReadOnlyToolAsync(owner, invocation, tool, cancellationToken)
                .ConfigureAwait(false);
        }

        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (publishConversationEvents)
        {
            PublishToolFinished(turnId, owner, tool, result, arguments);
        }

        IrisEvent.Log("Modules",
            result.Succeeded ? "tool.done" : "tool.refused",
            result.Succeeded ? LogLevel.Debug : LogLevel.Warning,
            result.Succeeded ? null : result.ModelText,
            elapsed,
            new Dictionary<string, string?>
            {
                ["tool"] = tool.Name,
                ["module"] = owner.Id,
                ["tier"] = tool.Tier.ToString(),
                ["turn"] = turnId
            });

        return result;
    }

    private static async Task<ToolResult> RetryReadOnlyToolAsync(IAssistantModule owner, ToolInvocation invocation,
        ModuleTool tool, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(tool.TimeoutSeconds ?? ToolTimeoutSeconds));

        try
        {
            Task<ToolResult> execution = owner.ExecuteAsync(invocation, timeoutSource.Token);
            Task finished = await Task.WhenAny(execution, Task.Delay(Timeout.Infinite, timeoutSource.Token))
                .ConfigureAwait(false);
            if (finished == execution)
            {
                return await execution.ConfigureAwait(false);
            }

            _ = execution.ContinueWith(task =>
            {
                Exception fault = task.Exception!.GetBaseException();
                CompanionLog.Warning("Modules",
                    $"Late result from retried read-only tool '{tool.Name}': {fault.Message}.");
            }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            cancellationToken.ThrowIfCancellationRequested();
            return ToolResult.Uncertain($"The read-only retry for {tool.Name} also timed out.");
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return ToolResult.Uncertain($"The read-only retry for {tool.Name} was stopped part-way through.");
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Modules", $"Read-only retry for '{tool.Name}' failed: {exception.Message}");
            return ToolResult.Fail($"The read-only retry also failed: {exception.Message}");
        }
    }

    /// <summary>
    /// When a module is replaced: the same id gets a fresh tools list; the old
    /// capability text lives on only through the replacement registration.
    /// Note: tool definitions themselves are supplied to the model every request in
    /// RouteTools, so this prose stays a module-level map that search_capabilities
    /// can grep — never a re-listing of every tool description.
    /// </summary>
    /// <summary>Rolls with the sandbox in front of every call.</summary>

    /// <summary>Panels contributed by modules, for the shell to host.</summary>
    public IReadOnlyList<ModuleSurface> GetSurfaces()
    {
        List<ModuleSurface> surfaces = new();
        foreach (IAssistantModule module in Modules.Where(module => IsEnabled(module.Id)))
        {
            if (module is not IModuleSurfaceProvider provider)
            {
                continue;
            }

            try
            {
                surfaces.AddRange(provider.GetSurfaces());
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Modules", $"'{module.Id}' could not describe its panels: {exception.Message}");
            }
        }

        return surfaces;
    }

    public async ValueTask DisposeAsync()
    {
        await _backgroundTasks.DisposeAsync().ConfigureAwait(false);
        foreach (IAssistantModule module in Modules)
        {
            try
            {
                await module.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Shutting down anyway — but honestly: the fault is on record, the same
                // way UnregisterAsync reports a dirty release.
                CompanionLog.Warning("Modules",
                    $"Module '{module.Id}' did not shut down cleanly: {exception.Message}");
            }
            finally
            {
                // ShutdownAsync covers the interface's idea of clean, but some modules
                // carry extra state behind IDisposable (a timer, a player session).
                // Disposing it here is the last sweep, guarded so one misbehaving module
                // cannot stall her exit.
                try
                {
                    (module as IDisposable)?.Dispose();
                }
                catch (Exception exception)
                {
                    CompanionLog.Warning("Modules",
                        $"Module '{module.Id}' did not dispose cleanly: {exception.Message}");
                }
            }
        }
    }

    private ModuleContext BuildContext(IAssistantModule module) => new()
    {
        Host = Host,
        Settings = _settings.Current,
        PersistSettings = () =>
        {
            _settings.Save();
            _events.Publish(new SettingsChangedEvent());
        },
        MutateSettings = MutateSettingsAndAnnounce,
        Events = _events,
        Memory = _memory,
        Sandbox = _sandbox,
        BackgroundTasks = _backgroundTasks,
        Ui = _moduleUi.ForModule(module.Id, module.DisplayName, module.IconKey),
        Prompts = _sandbox.Prompts.ForModule(module.Id, module.DisplayName, module.IconKey),
        WorkspaceDirectory = CompanionPaths.WorkspaceDirectory,
        ModelsDirectory = CompanionPaths.ModelsDirectory,
        CacheDirectory = CompanionPaths.CacheDirectory
    };

    /// <summary>
    /// The module-context door onto the settings gate: mutation happens inside the
    /// store's lock, and a real change announces itself on the bus exactly the way the
    /// old PersistSettings save did (2026-10-27 audit finding 96).
    /// </summary>
    private bool MutateSettingsAndAnnounce(Func<CompanionSettings, bool> mutate)
    {
        bool changed = _settings.Mutate(mutate);
        if (changed)
        {
            _events.Publish(new SettingsChangedEvent());
        }

        return changed;
    }

    /// <summary>
    /// The verb phrase a module's own narrator builds for a call — "play 'hollowlight'" —
    /// the raw action text that the tool chip and the permission prompt both build on.
    /// </summary>
    private static string DescribeActionPhrase(IAssistantModule module, ModuleTool tool, JsonElement arguments)
    {
        string action = tool.Description.TrimEnd('.').ToLowerInvariant();
        if (module is IModuleActionDescriber describer)
        {
            try
            {
                action = describer.DescribeAction(tool, arguments);
            }
            catch (Exception exception)
            {
                CompanionLog.Warning("Modules", $"'{module.Id}' could not describe a permission target: {exception.Message}");
            }
        }

        return action.Trim().TrimEnd('.');
    }

    private static string DescribeAction(IAssistantModule module, ModuleTool tool, JsonElement arguments)
    {
        if (tool.Presentation is { } presentation)
        {
            return $"{presentation.Doing.Trim().TrimEnd('.')}.";
        }

        return $"{module.DisplayName} wants to {DescribeActionPhrase(module, tool, arguments)}.";
    }

    /// <summary>Turns a verb phrase into a card headline: "play 'hollowlight'" → "Play 'hollowlight'".</summary>
    private static string CapitalizeFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private void PublishToolFinished(string turnId, IAssistantModule owner, ModuleTool tool, ToolResult result,
        JsonElement arguments = default)
    {
        ToolPresentation words = ToolPresentationCatalog.Resolve(tool, arguments);
        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.ToolFinished,
            TurnId = turnId,
            Text = result.Succeeded ? words.Done : words.Failed,
            Detail = result.Succeeded ? result.DisplayText ?? result.ModelText : result.ModelText,
            ModuleId = owner.Id,
            ToolName = tool.Name,
            IconKey = ToolIconKey(owner, tool, arguments),
            Succeeded = result.Succeeded
        });
    }

    /// <summary>The icon for one call: the tool's own glyph when it has one, else the module's.</summary>
    private static string ToolIconKey(IAssistantModule owner, ModuleTool tool, JsonElement arguments)
    {
        string icon = ToolPresentationCatalog.Resolve(tool, arguments).IconKey;
        return string.Equals(icon, "body", StringComparison.OrdinalIgnoreCase) ? owner.IconKey : icon;
    }

    private void PublishToolStarted(string turnId, IAssistantModule owner, ModuleTool tool,
        JsonElement arguments = default)
    {
        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.ToolStarted,
            TurnId = turnId,
            Text = ToolPresentationCatalog.Resolve(tool, arguments).Doing,
            Detail = owner.DisplayName,
            ModuleId = owner.Id,
            ToolName = tool.Name,
            IconKey = ToolIconKey(owner, tool, arguments)
        });
    }

    private string DisabledModulesFilePath =>
        Path.Combine(CompanionPaths.ConfigDirectory, DisabledModulesFile);

    private void LoadDisabledModules()
    {
        try
        {
            if (!File.Exists(DisabledModulesFilePath))
            {
                return;
            }

            string json = File.ReadAllText(DisabledModulesFilePath);
            List<string>? ids = JsonSerializer.Deserialize<List<string>>(json);
            if (ids is null)
            {
                return;
            }

            foreach (string id in ids)
            {
                _disabledModuleIds.Add(id);
            }
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Modules", $"Could not read disabled modules: {exception.Message}");
        }
    }

    private void SaveDisabledModules()
    {
        try
        {
            List<string> ids;
            lock (_registryLock)
            {
                ids = _disabledModuleIds.ToList();
            }

            Directory.CreateDirectory(CompanionPaths.ConfigDirectory);
            File.WriteAllText(DisabledModulesFilePath, JsonSerializer.Serialize(ids));
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Modules", $"Could not save disabled modules: {exception.Message}");
        }
    }

    /// <summary>
    /// Built-ins and process-isolated external adapters share this registry, router and
    /// sandbox. External commands and MCP servers are registered only through the plugin
    /// host; arbitrary DLLs are never loaded into Iris. The full install-review and module
    /// UI SDK is still being built.
    /// </summary>
}
