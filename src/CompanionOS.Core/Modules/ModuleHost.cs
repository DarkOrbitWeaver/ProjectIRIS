using System.Text.Json;
using System.Text.Json.Nodes;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Memory;

namespace CompanionOS.Core.Modules;

/// <summary>
/// The body: it holds every installed hand, decides which hands a request needs, and
/// makes sure nothing dangerous happens without permission.
///
/// The important idea is dynamic routing. A small model given eighty tool definitions
/// becomes slow and confused, so before each turn the router picks at most two
/// modules whose description matches the user's sentence, plus the always-available
/// core tools. That keeps the model looking at roughly eight tools no matter whether
/// three modules or three hundred are installed.
/// </summary>
public sealed class ModuleHost : IAsyncDisposable
{
    /// <summary>Hard cap on tools per turn. Small models do not do well above this.</summary>
    public const int MaximumToolsPerTurn = 8;

    private const string DisabledModulesFile = "modules.disabled.json";
    private const int ToolTimeoutSeconds = 45;

    private readonly SettingsStore _settings;
    private readonly EventBus _events;
    private readonly SandboxGuard _sandbox;
    private readonly IMemoryAccess _memory;
    private readonly object _registryLock = new();
    private readonly Dictionary<string, IAssistantModule> _modulesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IAssistantModule> _toolOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledModuleIds = new(StringComparer.OrdinalIgnoreCase);

    public ModuleHost(SettingsStore settings, EventBus events, SandboxGuard sandbox, IMemoryAccess memory)
    {
        _settings = settings;
        _events = events;
        _sandbox = sandbox;
        _memory = memory;
        LoadDisabledModules();
    }

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

        try
        {
            await module.InitializeAsync(BuildContext(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            CompanionLog.Error("Modules", $"Module '{module.Id}' failed to initialise.", exception);
        }

        _events.Publish(new ModuleRegistryChangedEvent { ModuleId = module.Id, Change = "installed" });
        RegistryChanged?.Invoke();
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

    /// <summary>
    /// Chooses the tools for one turn. Returns an empty list when nothing fits, which
    /// is normal: she can still answer from memory and conversation.
    /// </summary>
    public IReadOnlyList<ModuleTool> RouteTools(string userMessage, out IReadOnlyList<IAssistantModule> selectedModules)
    {
        List<IAssistantModule> enabledModules;
        lock (_registryLock)
        {
            enabledModules = _modulesById.Values
                .Where(module => !_disabledModuleIds.Contains(module.Id))
                .ToList();
        }

        List<ModuleTool> routedTools = new();
        HashSet<string> addedToolNames = new(StringComparer.OrdinalIgnoreCase);
        List<IAssistantModule> chosenModules = new();

        // 1. Core is always eligible, but not every core tool is attached to every turn.
        // The old implementation exposed all eight core tools first, filling the entire
        // eight-tool budget and silently starving Music and System Control forever.
        // Select the most relevant core hands, then leave room for routed modules.
        foreach (IAssistantModule module in enabledModules.Where(module => module.AlwaysAvailable))
        {
            chosenModules.Add(module);
            foreach (ModuleTool tool in SelectCoreTools(module.Tools, userMessage))
            {
                if (addedToolNames.Add(tool.Name))
                {
                    routedTools.Add(tool);
                }
            }
        }

        // 2. Score the rest against the sentence and take the best two.
        float[] requestVector = TextVectorizer.Embed(userMessage);
        List<(IAssistantModule Module, double Score)> scored = new();

        foreach (IAssistantModule module in enabledModules.Where(module => !module.AlwaysAvailable))
        {
            float[] capabilityVector = TextVectorizer.Embed(
                module.DisplayName + " " + module.Purpose + " " + module.CapabilityDescription);

            double score = TextVectorizer.Cosine(requestVector, capabilityVector);
            scored.Add((module, score));
        }

        foreach ((IAssistantModule module, double score) in scored
                     .Where(pair => pair.Score >= 0.08)
                     .OrderByDescending(pair => pair.Score)
                     .Take(2))
        {
            chosenModules.Add(module);
            foreach (ModuleTool tool in module.Tools)
            {
                if (routedTools.Count >= MaximumToolsPerTurn)
                {
                    break;
                }

                if (addedToolNames.Add(tool.Name))
                {
                    routedTools.Add(tool);
                }
            }
        }

        // 3. Never exceed the cap, and never drop a core tool in favour of a bonus one.
        if (routedTools.Count > MaximumToolsPerTurn)
        {
            routedTools = routedTools.Take(MaximumToolsPerTurn).ToList();
        }

        selectedModules = chosenModules;
        return routedTools;
    }

    private static IReadOnlyList<ModuleTool> SelectCoreTools(
        IReadOnlyList<ModuleTool> tools, string userMessage)
    {
        const int coreToolBudget = 4;
        if (tools.Count <= coreToolBudget)
        {
            return tools;
        }

        string[] words = userMessage
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tools
            .Select((tool, index) =>
            {
                int keywordHits = tool.RoutingKeywords.Count(keyword =>
                    words.Any(word => word.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                                      keyword.Contains(word, StringComparison.OrdinalIgnoreCase)));
                double lexical = TextVectorizer.Cosine(
                    TextVectorizer.Embed(userMessage),
                    TextVectorizer.Embed(tool.Name + " " + tool.Description + " " +
                                         string.Join(' ', tool.RoutingKeywords)));
                // Stable index tie-break keeps the tool list deterministic for equal scores.
                return (Tool: tool, Score: keywordHits * 2.0 + lexical, Index: index);
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Index)
            .Take(coreToolBudget)
            .Select(item => item.Tool)
            .ToList();
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

        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.ToolStarted,
            TurnId = turnId,
            Text = tool.ShortLabel,
            Detail = owner.DisplayName,
            ModuleId = owner.Id,
            IconKey = owner.IconKey
        });

        bool isAllowed = await _sandbox
            .AuthorizeAsync(owner.Id, owner.DisplayName, tool, DescribeAction(owner, tool, arguments), cancellationToken)
            .ConfigureAwait(false);

        if (!isAllowed)
        {
            ToolResult refusal = ToolResult.Fail("The user did not allow that action.");
            PublishToolFinished(turnId, owner, tool, refusal);
            return refusal;
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
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(ToolTimeoutSeconds));

        try
        {
            Task<ToolResult> execution = owner.ExecuteAsync(invocation, timeoutSource.Token);
            Task finished = await Task.WhenAny(execution, Task.Delay(Timeout.Infinite, timeoutSource.Token))
                .ConfigureAwait(false);

            result = finished == execution
                ? await execution.ConfigureAwait(false)
                : ToolResult.Fail($"{tool.Name} took too long, so I stopped it.");
        }
        catch (OperationCanceledException)
        {
            result = ToolResult.Fail($"{tool.Name} was cancelled.");
        }
        catch (Exception exception)
        {
            CompanionLog.Error("Modules", $"Tool '{tool.Name}' threw.", exception);
            result = ToolResult.Fail($"That failed: {exception.Message}");
        }

        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        PublishToolFinished(turnId, owner, tool, result);

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

    /// <summary>Short capability map inserted into the prompt so she knows her own body.</summary>
    public string DescribeCapabilities()
    {
        List<string> lines = new();
        foreach (IAssistantModule module in Modules.Where(module => IsEnabled(module.Id)))
        {
            string toolNames = string.Join(", ", module.Tools.Select(tool => tool.Name));
            lines.Add($"- {module.DisplayName}: {module.Purpose} (tools: {toolNames})");
        }

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
    }

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
        foreach (IAssistantModule module in Modules)
        {
            try
            {
                await module.ShutdownAsync().ConfigureAwait(false);
            }
            catch
            {
                // Shutting down anyway.
            }
        }
    }

    private ModuleContext BuildContext() => new()
    {
        Host = Host,
        Settings = _settings.Current,
        PersistSettings = () =>
        {
            _settings.Save();
            _events.Publish(new SettingsChangedEvent());
        },
        Events = _events,
        Memory = _memory,
        Sandbox = _sandbox,
        WorkspaceDirectory = CompanionPaths.WorkspaceDirectory,
        ModelsDirectory = CompanionPaths.ModelsDirectory,
        CacheDirectory = CompanionPaths.CacheDirectory
    };

    private static string DescribeAction(IAssistantModule module, ModuleTool tool, JsonElement arguments)
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

        return $"{module.DisplayName} wants to {action.Trim().TrimEnd('.')}.";
    }

    private void PublishToolFinished(string turnId, IAssistantModule owner, ModuleTool tool, ToolResult result)
    {
        _events.Publish(new ConversationEvent
        {
            Phase = ConversationPhase.ToolFinished,
            TurnId = turnId,
            Text = tool.ShortLabel,
            Detail = result.Succeeded ? result.DisplayText ?? result.ModelText : result.ModelText
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
