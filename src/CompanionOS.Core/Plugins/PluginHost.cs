using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CompanionOS.Core.Diagnostics;
using CompanionOS.Core.Events;
using CompanionOS.Core.Modules;

namespace CompanionOS.Core.Plugins;

/// <summary>
/// The plugin spine. One door for everything a tool can be: a folder-declared builtin,
/// a plain command, or a full MCP server over stdio. The client of this class is the
/// user — plugins live in their folder so they develop and update tools without
/// touching her code.
///
/// Hard rules:
///   • the folder is the only source of truth; what it does not contain does not exist,
///   • a rejected manifest is remembered with its reason and reported honestly,
///   • a plugin process is spawned per tool call (command) or one healthy connection
///     (mcp) with real timeouts; a workspace never becomes the user's problem.
/// </summary>
public sealed class PluginHost : IDisposable
{
    private const int MaximumCommandOutputCharacters = 256 * 1024;
    private const int MaximumCommandErrorCharacters = 16 * 1024;
    private const int MaximumProgressMessageCharacters = 240;
    private const int MaximumToolInputCharacters = 256 * 1024;
    private const int MaximumDiscoveredTools = 128;
    private const int MaximumDiscoveredSchemaCharacters = 16_384;
    private const int MaximumDiscoveredDescriptionCharacters = 1_000;
    private const int MaximumSurfaceUpdatesPerCommand = 128;
    private static readonly TimeSpan CommandProgressMinimumInterval = TimeSpan.FromMilliseconds(100);
    private readonly EventBus _events;

    private readonly List<PluginManifest> _loaded = [];
    private readonly List<(string Folder, string Reason)> _rejected = [];
    private readonly Dictionary<string, McpConnection> _mcpServers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mcpConnectionGate = new(1, 1);
    private readonly string _pluginsRoot;

    public PluginHost(EventBus events, string? pluginsRoot = null)
    {
        _events = events;
        _pluginsRoot = pluginsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CompanionOS", "plugins");
    }

    /// <summary>Every plugin that loaded cleanly, in folder order.</summary>
    public IReadOnlyList<PluginManifest> Loaded => _loaded;

    /// <summary>Folders that had a plugin.json but failed the contract, with reasons.</summary>
    public IReadOnlyList<(string Folder, string Reason)> Rejected => _rejected;

    public string Root => _pluginsRoot;

    /// <summary>
    /// Starts opted-in MCP servers and reads their current tool schemas before the
    /// module router is built. Discovery is bounded and failures leave that plugin
    /// with no callable discovered tools rather than a stale schema.
    /// </summary>
    public async Task PrepareEnabledAsync(CancellationToken cancellationToken)
    {
        foreach (PluginManifest plugin in _loaded.Where(candidate =>
                     !candidate.Disabled && candidate.DiscoverTools && candidate.Transport is "mcp" or "command"))
        {
            using CancellationTokenSource discoveryBudget =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            discoveryBudget.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                IReadOnlyList<PluginTool> discovered;
                if (plugin.Transport == "command")
                {
                    PluginResult response = await CallCommandAsync(plugin,
                        new PluginTool("__discover_tools__", "Discover module tools.", null, "{}"),
                        "{}", string.Empty, discoveryBudget.Token, action: "tools/list").ConfigureAwait(false);
                    if (!response.Ok)
                    {
                        throw new InvalidOperationException(response.Error ?? response.Text);
                    }
                    discovered = ParseCommandDiscoveredTools(response.Text);
                }
                else
                {
                    McpConnection connection = await GetMcpAsync(plugin, discoveryBudget.Token).ConfigureAwait(false);
                    discovered = await connection.ListToolsAsync(discoveryBudget.Token).ConfigureAwait(false);
                }

                if (!ApplyDiscoveredTools(plugin, discovered) && plugin.Transport == "mcp")
                {
                    StopMcp(plugin.Id);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                plugin.Tools = [];
                if (plugin.Transport == "mcp")
                {
                    StopMcp(plugin.Id);
                }
                throw;
            }
            catch (Exception exception)
            {
                plugin.Tools = [];
                if (plugin.Transport == "mcp")
                {
                    StopMcp(plugin.Id);
                }
                CompanionLog.Warning("Plugins", $"Tool discovery failed for '{plugin.Name}': {exception.Message}");
            }
        }
    }

    private bool ApplyDiscoveredTools(PluginManifest plugin, IReadOnlyList<PluginTool> discovered)
    {
        List<PluginTool> merged = discovered.Select(tool =>
        {
            PluginTool? policy = plugin.DeclaredTools.FirstOrDefault(declared =>
                string.Equals(declared.Name, tool.Name, StringComparison.OrdinalIgnoreCase));
            return tool with { Permission = policy?.Permission, Background = policy?.Background ?? false };
        }).ToList();

        plugin.Tools = merged;
        if (!PluginManifest.ValidateDiscoveredSurfaces(plugin, out string reason))
        {
            plugin.Tools = [];
            CompanionLog.Warning("Plugins", $"Rejected discovered tools for '{plugin.Name}': {reason}");
            return false;
        }

        CompanionLog.Info("Plugins", $"Discovered {merged.Count} {plugin.Transport} tools for '{plugin.Name}'.");
        return true;
    }

    private static IReadOnlyList<PluginTool> ParseCommandDiscoveredTools(string jsonl)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonl);
            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Command module tool discovery must return one JSON object: {exception.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out JsonElement kind) || kind.GetString() != "tools" ||
            !root.TryGetProperty("tools", out JsonElement toolsElement) || toolsElement.ValueKind != JsonValueKind.Array ||
            toolsElement.GetArrayLength() > MaximumDiscoveredTools)
        {
            throw new InvalidOperationException($"Command module discovery must return a 'tools' envelope with at most {MaximumDiscoveredTools} tools.");
        }

        List<PluginTool> tools = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in toolsElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Command module discovery returned a tool without a string name.");
            }

            string name = nameElement.GetString() ?? string.Empty;
            if (name.Length is 0 or > 128 || !names.Add(name))
            {
                throw new InvalidOperationException("Command module discovery returned an empty, oversized, or duplicate tool name.");
            }

            string description = string.Empty;
            if (entry.TryGetProperty("description", out JsonElement descriptionElement))
            {
                if (descriptionElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException($"Discovered tool '{name}' description must be a string.");
                }
                description = descriptionElement.GetString() ?? string.Empty;
                if (description.Length > MaximumDiscoveredDescriptionCharacters)
                {
                    throw new InvalidOperationException($"Discovered tool '{name}' description exceeds {MaximumDiscoveredDescriptionCharacters} characters.");
                }
            }

            JsonElement schema = entry.TryGetProperty("schema", out JsonElement schemaElement)
                ? schemaElement
                : entry.TryGetProperty("inputSchema", out JsonElement inputSchemaElement) ? inputSchemaElement : default;
            if (schema.ValueKind != JsonValueKind.Object ||
                !schema.TryGetProperty("type", out JsonElement typeElement) || typeElement.ValueKind != JsonValueKind.String ||
                typeElement.GetString() != "object")
            {
                throw new InvalidOperationException($"Discovered tool '{name}' must provide an object schema.");
            }

            string schemaJson = schema.GetRawText();
            if (schemaJson.Length > MaximumDiscoveredSchemaCharacters)
            {
                throw new InvalidOperationException($"Discovered tool '{name}' schema exceeds {MaximumDiscoveredSchemaCharacters} characters.");
            }
            tools.Add(new PluginTool(name, description, null, schemaJson));
        }

        return tools;
    }

    private void StopMcp(string pluginId)
    {
        if (_mcpServers.Remove(pluginId, out McpConnection? connection))
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Changes the launch preference in the plugin's own manifest. External plugins
    /// are never hot-loaded or hot-unloaded: their process boundary and permissions are
    /// reviewed at startup, so the change is explicitly for the next Iris launch.
    /// </summary>
    public bool SetLaunchEnabled(string pluginId, bool enabled, out string reason)
    {
        PluginManifest? plugin = _loaded.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
        {
            reason = "Plugin is no longer present.";
            return false;
        }

        string manifestPath = Path.Combine(plugin.Folder, "plugin.json");
        try
        {
            JsonObject? manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            if (manifest is null)
            {
                reason = "Plugin manifest is not a JSON object.";
                return false;
            }

            manifest["disabled"] = !enabled;
            string temporaryPath = manifestPath + ".next";
            File.WriteAllText(temporaryPath, manifest.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporaryPath, manifestPath, true);
            plugin.Disabled = !enabled;
            reason = enabled ? "It will be enabled the next time Iris starts."
                : "It will be disabled the next time Iris starts.";
            CompanionLog.Info("Plugins", $"{plugin.Name}: {reason}");
            return true;
        }
        catch (Exception exception)
        {
            reason = $"Could not update plugin.json: {exception.Message}";
            return false;
        }
    }

    /// <summary>Scans the plugins folder once at boot. Re-scan is possible but each call
    /// restarts MCP connections, so it is not wired to a hot path yet.</summary>
    public void Load()
    {
        _loaded.Clear();
        _rejected.Clear();

        if (!Directory.Exists(_pluginsRoot))
        {
            Directory.CreateDirectory(_pluginsRoot);
            CompanionLog.Info("Plugins", "Her plugin folder is new and empty — " +
                "plugins/json plus manifest/plugin.json and she will carry them.");
            return;
        }

        foreach (string folder in Directory.EnumerateDirectories(_pluginsRoot).OrderBy(f => f, StringComparer.Ordinal))
        {
            if (PluginManifest.TryLoad(folder, out PluginManifest? manifest, out string reason) && manifest is not null)
            {
                _loaded.Add(manifest);
                CompanionLog.Info("Plugins",
                    $"Loaded '{manifest.Name}' v{manifest.Version} ({manifest.Transport}) " +
                    $"with {manifest.Tools.Count} tools from {Path.GetFileName(folder)}.");
            }
            else
            {
                _rejected.Add((folder, reason));
                CompanionLog.Warning("Plugins", $"Rejected {folder}: {reason}");
            }
        }
    }

    /// <summary>
    /// Calls a plugin tool. Builtins do not flow through here (their modules own them);
    /// "command" spawns a process per call; "mcp" keeps one warm connection per plugin
    /// and speaks JSON-RPC. Never throws: errors come back as PluginResult with words.
    /// </summary>
    public async Task<PluginResult> CallAsync(PluginManifest plugin, PluginTool tool,
        string argumentsJson, string turnId, CancellationToken cancellationToken)
    {
        if (argumentsJson is null || argumentsJson.Length > MaximumToolInputCharacters)
        {
            return new PluginResult(false, string.Empty,
                $"Tool input is missing or exceeds the {MaximumToolInputCharacters:N0}-character limit.");
        }

        if (plugin.Disabled)
        {
            return new PluginResult(false, "", $"Plugin '{plugin.Name}' is disabled in its manifest.");
        }

        try
        {
            if (plugin.Transport == "command")
            {
                return await CallCommandAsync(plugin, tool, argumentsJson, turnId, cancellationToken).ConfigureAwait(false);
            }

            if (plugin.Transport == "mcp")
            {
                return await CallMcpAsync(plugin, tool, argumentsJson, turnId, cancellationToken).ConfigureAwait(false);
            }

            return new PluginResult(false, "", $"'{plugin.Name}' is a builtin-shaped plugin; call its module instead.");
        }
        catch (Exception exception)
        {
            CompanionLog.Warning("Plugins", $"Call failed in '{plugin.Name}' /'{tool.Name}': {exception.Message}");
            return new PluginResult(false, "", exception.Message);
        }
    }

    private async Task<PluginResult> CallCommandAsync(PluginManifest plugin, PluginTool tool,
        string argumentsJson, string turnId, CancellationToken cancellationToken, string action = "call")
    {
        string fileName = PluginCommandPath.Resolve(plugin);
        if (PluginCommandPath.IsPathLike(plugin.Command[0]) && !File.Exists(fileName))
        {
            return new PluginResult(false, "", $"Command file '{fileName}' does not exist.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = ResolveWorkingDirectory(plugin),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (string argument in plugin.Command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        long started = Stopwatch.GetTimestamp();
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return new PluginResult(false, "", $"Could not start '{plugin.Name}'.");
        }

        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });

        using JsonDocument inputDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        string payload = JsonSerializer.Serialize(new
        {
            protocol = 1,
            action,
            plugin = plugin.Id,
            tool = tool.Name,
            arguments = inputDocument.RootElement.Clone()
        });
        await process.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);
        process.StandardInput.Close();

        Task<CommandOutput> standardOutTask = ReadCommandOutputAsync(process.StandardOutput, process, plugin, tool, turnId,
            cancellationToken);
        Task<string> standardErrorTask = ReadBoundedOutputAsync(process.StandardError, MaximumCommandErrorCharacters,
            cancellationToken);
        await Task.WhenAll(standardOutTask, standardErrorTask).ConfigureAwait(false);
        CommandOutput commandOutput = await standardOutTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        double cost = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _events.Publish(new PluginActivityEvent(plugin.Id, "call", $"{plugin.Name} answered in {cost:0} ms"));

        if (commandOutput.ExceededLimit)
        {
            return new PluginResult(false, string.Empty,
                $"'{plugin.Name}' exceeded the {MaximumCommandOutputCharacters:N0}-character output limit.");
        }

        if (process.ExitCode != 0)
        {
            string error = string.IsNullOrWhiteSpace(standardError) ? $"exit code {process.ExitCode}" : standardError.Trim();
            return new PluginResult(false, commandOutput.Text.Trim(), error);
        }

        if (commandOutput.Result is { } result)
        {
            return result.Ok
                ? new PluginResult(true, result.Text)
                : new PluginResult(false, result.Text, string.IsNullOrWhiteSpace(result.Text) ? "The module reported failure." : result.Text);
        }

        return new PluginResult(true, commandOutput.Text.Trim());
    }

    private async Task<CommandOutput> ReadCommandOutputAsync(StreamReader reader, Process process,
        PluginManifest plugin, PluginTool tool, string turnId, CancellationToken cancellationToken)
    {
        StringBuilder plainText = new();
        StringBuilder line = new();
        char[] buffer = new char[2048];
        int totalCharacters = 0;
        bool exceededLimit = false;
        PluginResult? result = null;
        long lastProgressTicks = 0;
        int surfaceUpdateCount = 0;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                totalCharacters++;
                if (totalCharacters > MaximumCommandOutputCharacters)
                {
                    if (!exceededLimit)
                    {
                        exceededLimit = true;
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                            }
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                    continue;
                }

                if (character == '\n')
                {
                    ProcessCommandLine(line.ToString().TrimEnd('\r'), plainText, plugin, tool, turnId, ref result,
                        ref lastProgressTicks, ref surfaceUpdateCount);
                    line.Clear();
                }
                else
                {
                    line.Append(character);
                }
            }
        }

        if (!exceededLimit && line.Length > 0)
        {
            ProcessCommandLine(line.ToString().TrimEnd('\r'), plainText, plugin, tool, turnId, ref result,
                ref lastProgressTicks, ref surfaceUpdateCount);
        }

        return new CommandOutput(plainText.ToString(), result, exceededLimit);
    }

    private void ProcessCommandLine(string line, StringBuilder plainText, PluginManifest plugin, PluginTool tool,
        string turnId, ref PluginResult? result, ref long lastProgressTicks, ref int surfaceUpdateCount)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out JsonElement typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                string? type = typeElement.GetString();
                if (string.Equals(type, "progress", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(turnId))
                    {
                        return;
                    }
                    long now = Stopwatch.GetTimestamp();
                    if (lastProgressTicks != 0 && Stopwatch.GetElapsedTime(lastProgressTicks, now) < CommandProgressMinimumInterval)
                    {
                        return;
                    }
                    lastProgressTicks = now;
                    string message = root.TryGetProperty("message", out JsonElement messageElement) &&
                                     messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (message.Length > MaximumProgressMessageCharacters)
                    {
                        message = message[..MaximumProgressMessageCharacters];
                    }
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        message = $"{tool.Name} is working";
                    }

                    _events.Publish(new ModuleProgressEvent
                    {
                        ModuleId = $"plugin:{plugin.Id}",
                        ModuleName = plugin.Name,
                        ToolName = tool.Name,
                        TurnId = turnId,
                        Message = message,
                        IconKey = plugin.ResolveIcon(plugin.Icon),
                        Progress = TryReadFiniteNumber(root, "progress"),
                        Total = TryReadFiniteNumber(root, "total")
                    });
                    return;
                }

                if (string.Equals(type, "result", StringComparison.OrdinalIgnoreCase))
                {
                    bool ok = !root.TryGetProperty("ok", out JsonElement okElement) ||
                              okElement.ValueKind != JsonValueKind.False;
                    string text = root.TryGetProperty("text", out JsonElement textElement) &&
                                  textElement.ValueKind == JsonValueKind.String
                        ? textElement.GetString() ?? string.Empty
                        : string.Empty;
                    result = new PluginResult(ok, text, ok ? null : text);
                    return;
                }

                if (string.Equals(type, "ui", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(turnId) && surfaceUpdateCount < MaximumSurfaceUpdatesPerCommand)
                    {
                        TryPublishSurfaceState(root, plugin, ref surfaceUpdateCount);
                    }
                    return;
                }
            }
        }
        catch (JsonException)
        {
            // Plain text remains the protocol-v1 fallback for existing command modules.
        }

        if (plainText.Length > 0)
        {
            plainText.AppendLine();
        }
        plainText.Append(line);
    }

    private void TryPublishSurfaceState(JsonElement root, PluginManifest plugin, ref int surfaceUpdateCount)
    {
        if (!TryReadBoundedString(root, "surface", 64, out string surfaceId) ||
            !TryReadBoundedString(root, "element", 64, out string elementId))
        {
            return;
        }

        PluginSurfaceManifest? surface = plugin.Surfaces.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, surfaceId, StringComparison.OrdinalIgnoreCase));
        PluginSurfaceElementManifest? element = surface?.Elements.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, elementId, StringComparison.OrdinalIgnoreCase));
        if (element is null)
        {
            return;
        }

        string? label = null;
        if (root.TryGetProperty("label", out JsonElement labelElement))
        {
            if (labelElement.ValueKind != JsonValueKind.String || (label = labelElement.GetString()) is null || label.Length > 96)
            {
                return;
            }
        }

        string? value = null;
        if (root.TryGetProperty("value", out JsonElement valueElement))
        {
            if (valueElement.ValueKind != JsonValueKind.String || (value = valueElement.GetString()) is null || value.Length > 4096 ||
                string.Equals(element.Type, "button", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        bool? visible = TryReadOptionalBoolean(root, "visible", out bool visibleValue, out bool visibleInvalid)
            ? visibleValue : null;
        bool? enabled = TryReadOptionalBoolean(root, "enabled", out bool enabledValue, out bool enabledInvalid)
            ? enabledValue : null;
        if (visibleInvalid || enabledInvalid)
        {
            return;
        }

        IReadOnlyList<ModuleSurfaceOption>? options = null;
        if (root.TryGetProperty("options", out JsonElement optionsElement))
        {
            if (!string.Equals(element.Type, "select", StringComparison.OrdinalIgnoreCase) || !element.DynamicOptions ||
                optionsElement.ValueKind != JsonValueKind.Array || optionsElement.GetArrayLength() > 64)
            {
                return;
            }

            List<ModuleSurfaceOption> parsedOptions = [];
            HashSet<string> values = new(StringComparer.Ordinal);
            foreach (JsonElement option in optionsElement.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object || !TryReadBoundedString(option, "value", 96, out string optionValue) ||
                    string.IsNullOrWhiteSpace(optionValue) || !TryReadBoundedString(option, "label", 96, out string optionLabel) ||
                    string.IsNullOrWhiteSpace(optionLabel) || !values.Add(optionValue))
                {
                    return;
                }
                parsedOptions.Add(new ModuleSurfaceOption(optionValue, optionLabel));
            }
            options = parsedOptions;
        }

        if (value is not null && string.Equals(element.Type, "select", StringComparison.OrdinalIgnoreCase))
        {
            if (element.DynamicOptions && options is null)
            {
                return;
            }
            IReadOnlyList<PluginSurfaceOptionManifest> availableOptions = options is null
                ? element.Options
                : options.Select(option => new PluginSurfaceOptionManifest(option.Value, option.Label)).ToList();
            if ((availableOptions.Count > 0 || !element.DynamicOptions) &&
                !availableOptions.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
            {
                return;
            }
        }

        if (label is null && value is null && visible is null && enabled is null && options is null)
        {
            return;
        }
        surfaceUpdateCount++;
        _events.Publish(new ModuleSurfaceStateChangedEvent
        {
            ModuleId = $"plugin:{plugin.Id}",
            SurfaceId = $"plugin:{plugin.Id}:{surfaceId}",
            ElementId = elementId,
            Label = label,
            Value = value,
            IsVisible = visible,
            IsEnabled = enabled,
            Options = options
        });
    }

    private static bool TryReadBoundedString(JsonElement root, string name, int maximumLength, out string result)
    {
        result = string.Empty;
        if (!root.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String ||
            element.GetString() is not { } value || value.Length is 0 || value.Length > maximumLength)
        {
            return false;
        }
        result = value;
        return true;
    }

    private static bool TryReadOptionalBoolean(JsonElement root, string name, out bool value, out bool invalid)
    {
        value = false;
        invalid = false;
        if (!root.TryGetProperty(name, out JsonElement element))
        {
            return false;
        }
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            invalid = true;
            return false;
        }
        value = element.ValueKind == JsonValueKind.True;
        return true;
    }

    private static double? TryReadFiniteNumber(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out double number) && double.IsFinite(number)
            ? number
            : null;
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader, int maximumCharacters,
        CancellationToken cancellationToken)
    {
        StringBuilder output = new();
        char[] buffer = new char[1024];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToString();
            }
            int remaining = maximumCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private sealed record CommandOutput(string Text, PluginResult? Result, bool ExceededLimit);

    /// <summary>Talks JSON-RPC 2.0 over the plugin's stdio, MCP-style.</summary>
    private async Task<PluginResult> CallMcpAsync(PluginManifest plugin, PluginTool tool,
        string argumentsJson, string turnId, CancellationToken cancellationToken)
    {
        McpConnection connection = await GetMcpAsync(plugin, cancellationToken).ConfigureAwait(false);
        if (connection.Failed is not null)
        {
            return new PluginResult(false, "", connection.Failed);
        }

        JsonElement arguments;
        try
        {
            using JsonDocument argumentDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            arguments = argumentDocument.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return new PluginResult(false, "", $"Arguments are not JSON: {exception.Message}");
        }

        JsonElement lat = await connection.CallToolAsync(tool.Name, arguments,
            progress => _events.Publish(new ModuleProgressEvent
            {
                ModuleId = $"plugin:{plugin.Id}",
                ModuleName = plugin.Name,
                ToolName = tool.Name,
                TurnId = turnId,
                Message = progress.Message ?? $"{tool.Name} is working",
                IconKey = plugin.ResolveIcon(plugin.Icon),
                Progress = progress.Progress,
                Total = progress.Total
            }), cancellationToken).ConfigureAwait(false);

        bool isError = lat.TryGetProperty("isError", out JsonElement errorElement) && errorElement.ValueKind == JsonValueKind.True;
        string text = ExtractText(lat);
        return new PluginResult(!isError, text, isError ? "The plugin reported this call as an error." : null);
    }

    private async Task<McpConnection> GetMcpAsync(PluginManifest plugin, CancellationToken cancellationToken)
    {
        if (_mcpServers.TryGetValue(plugin.Id, out McpConnection? existing))
        {
            return existing;
        }

        await _mcpConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mcpServers.TryGetValue(plugin.Id, out existing))
            {
                return existing;
            }

            McpConnection connection = new(plugin, ResolveWorkingDirectory(plugin));
            _mcpServers[plugin.Id] = connection;
            await connection.StartAndInitializeAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        finally
        {
            _mcpConnectionGate.Release();
        }
    }

    private static string ResolveWorkingDirectory(PluginManifest plugin)
    {
        string directory = plugin.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(directory) || directory == ".")
        {
            return plugin.Folder;
        }
        return Path.IsPathRooted(directory)
            ? directory
            : Path.GetFullPath(Path.Combine(plugin.Folder, directory));
    }

    /// <summary>Reads the plain text an MCP result carries; empty when it used raw JSON.</summary>
    private static string ExtractText(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return result.ToString();
        }

        StringBuilder builder = new();
        foreach (JsonElement piece in content.EnumerateArray())
        {
            if (piece.TryGetProperty("text", out JsonElement textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(textElement.GetString());
            }
        }
        return builder.ToString();
    }

    public void Dispose()
    {
        foreach (McpConnection connection in _mcpServers.Values)
        {
            connection.Dispose();
        }
        _mcpServers.Clear();
        _mcpConnectionGate.Dispose();
    }
}

/// <summary>She says around a plugin while it runs. Tool calls park here.</summary>
public sealed class PluginActivityEvent : CompanionEvent
{
    public PluginActivityEvent(string pluginId, string activity, string description)
    {
        PluginId = pluginId;
        Activity = activity;
        Description = description;
    }

    public string PluginId { get; }
    public string Activity { get; }
    public string Description { get; }
}

/// <summary>
/// One live MCP server connection: process + JSON-RPC over stdin/stdout lines.
/// Ids are monotonous integers per connection, answers are matched by id.
/// </summary>
internal sealed class McpConnection : IDisposable
{
    private const int MaximumDiscoveredTools = 128;
    private const int MaximumToolPages = 16;
    private const int MaximumInputSchemaCharacters = 16_384;
    private const int MaximumToolDescriptionCharacters = 1_000;
    private readonly PluginManifest _plugin;
    private Process? _process;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly object _progressLock = new();
    private readonly Dictionary<string, Action<McpProgressUpdate>> _progressHandlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastProgressTicks = new(StringComparer.Ordinal);
    private int _nextId;

    public McpConnection(PluginManifest plugin, string workingDirectory)
    {
        _plugin = plugin;
        WorkingDirectory = workingDirectory;
    }

    public string WorkingDirectory { get; }
    public string? Failed { get; private set; }

    public async Task StartAndInitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            string fileName = PluginCommandPath.Resolve(_plugin);
            if (PluginCommandPath.IsPathLike(_plugin.Command[0]) && !File.Exists(fileName))
            {
                Failed = $"Command file '{fileName}' does not exist.";
                return;
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = fileName,
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };
            foreach (string argument in _plugin.Command.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputLine;
            _process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    CompanionLog.Trace("Plugins", $"'{_plugin.Name}' stderr: {args.Data}");
                }
            };
            if (!_process.Start())
            {
                Failed = "the MCP server refused to start";
                return;
            }
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            JsonElement capabilities = await RequestAsync("initialize", new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = JsonSerializer.SerializeToElement(new { }),
                ["clientInfo"] = JsonSerializer.SerializeToElement(new { name = "CompanionOS", version = "1.0" })
            }, cancellationToken).ConfigureAwait(false);

            CompanionLog.Info("Plugins",
                $"'{_plugin.Name}' MCP is alive: {capabilities.GetProperty("protocolVersion").GetString()}");

            // The MCP lifecycle: one notification, then tools are callable.
            await NotifyAsync("notifications/initialized", new Dictionary<string, object?>()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Failed = $"MCP handshake failed: {exception.Message}";
        }
    }

    private void OnOutputLine(object? sender, DataReceivedEventArgs eventArgs)
    {
        string? line = eventArgs.Data;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            if (TryHandleProgress(document.RootElement))
            {
                return;
            }

            TaskCompletionSource<JsonElement>? waiter = null;
            int responseId = 0;
            if (document.RootElement.TryGetProperty("id", out JsonElement idElement) &&
                idElement.ValueKind == JsonValueKind.Number)
            {
                responseId = idElement.GetInt32();
                lock (_pendingLock)
                {
                    _pending.Remove(responseId, out waiter);
                }
            }

            if (waiter is not null)
            {
                if (document.RootElement.TryGetProperty("error", out JsonElement errorElement) &&
                    errorElement.ValueKind == JsonValueKind.Object)
                {
                    waiter.TrySetException(new InvalidOperationException(
                        errorElement.TryGetProperty("message", out JsonElement messageElement)
                            ? messageElement.GetString() ?? "MCP error"
                            : "MCP error"));
                }
                else if (document.RootElement.TryGetProperty("result", out JsonElement resultElement))
                {
                    waiter.TrySetResult(resultElement.Clone());
                }
            }
        }
        catch (JsonException)
        {
            companionTrace("not json");
        }
    }

    private bool TryHandleProgress(JsonElement message)
    {
        if (!message.TryGetProperty("method", out JsonElement methodElement) ||
            methodElement.ValueKind != JsonValueKind.String ||
            methodElement.GetString() != "notifications/progress" ||
            !message.TryGetProperty("params", out JsonElement parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("progressToken", out JsonElement tokenElement))
        {
            return false;
        }

        string token = tokenElement.ValueKind switch
        {
            JsonValueKind.String => tokenElement.GetString() ?? string.Empty,
            JsonValueKind.Number => tokenElement.GetRawText(),
            _ => string.Empty
        };
        if (token.Length == 0 || !parameters.TryGetProperty("progress", out JsonElement progressElement) ||
            !progressElement.TryGetDouble(out double progress) || !double.IsFinite(progress) || progress < 0)
        {
            companionTrace("invalid MCP progress notification");
            return true;
        }

        double? total = parameters.TryGetProperty("total", out JsonElement totalElement) &&
                        totalElement.TryGetDouble(out double parsedTotal) &&
                        double.IsFinite(parsedTotal) && parsedTotal > 0
            ? parsedTotal
            : null;
        string? text = parameters.TryGetProperty("message", out JsonElement messageElement) &&
                       messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : null;
        if (text?.Length > 240)
        {
            text = text[..240];
        }

        Action<McpProgressUpdate>? handler;
        lock (_progressLock)
        {
            if (!_progressHandlers.TryGetValue(token, out handler))
            {
                return true;
            }

            long now = Stopwatch.GetTimestamp();
            if (_lastProgressTicks.TryGetValue(token, out long previous) &&
                Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(100) &&
                (total is null || progress < total.Value))
            {
                return true;
            }
            _lastProgressTicks[token] = now;
        }

        handler(new McpProgressUpdate(progress, total, text));
        return true;
    }

    public async Task<JsonElement> CallToolAsync(string toolName, JsonElement arguments,
        Action<McpProgressUpdate> progressHandler, CancellationToken cancellationToken)
    {
        string progressToken = Guid.NewGuid().ToString("N");
        lock (_progressLock)
        {
            _progressHandlers[progressToken] = progressHandler;
        }

        try
        {
            return await RequestAsync("tools/call", new Dictionary<string, object?>
            {
                ["name"] = toolName,
                ["arguments"] = JsonSerializer.SerializeToElement(arguments),
                ["_meta"] = new Dictionary<string, object?> { ["progressToken"] = progressToken }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_progressLock)
            {
                _progressHandlers.Remove(progressToken);
                _lastProgressTicks.Remove(progressToken);
            }
        }
    }

    public async Task<IReadOnlyList<PluginTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        List<PluginTool> tools = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string? cursor = null;
        using CancellationTokenSource discoveryBudget =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryBudget.CancelAfter(TimeSpan.FromSeconds(10));
        int pageCount = 0;
        do
        {
            if (++pageCount > MaximumToolPages)
            {
                throw new InvalidOperationException($"MCP tools/list exceeded the {MaximumToolPages}-page limit.");
            }
            Dictionary<string, object?> parameters = cursor is null
                ? new()
                : new() { ["cursor"] = cursor };
            JsonElement result = await RequestAsync("tools/list", parameters,
                discoveryBudget.Token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("tools", out JsonElement toolsElement) ||
                toolsElement.ValueKind != JsonValueKind.Array ||
                tools.Count + toolsElement.GetArrayLength() > MaximumDiscoveredTools)
            {
                throw new InvalidOperationException($"MCP tools/list must return pages containing at most {MaximumDiscoveredTools} total tools.");
            }

            foreach (JsonElement entry in toolsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("name", out JsonElement nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("MCP tools/list returned a tool without a string name.");
                }

                string name = nameElement.GetString() ?? string.Empty;
                if (name.Length is 0 or > 128 || !names.Add(name))
                {
                    throw new InvalidOperationException("MCP tools/list returned an empty, oversized, or duplicate tool name.");
                }

                string description = entry.TryGetProperty("description", out JsonElement descriptionElement) &&
                                     descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString() ?? string.Empty
                    : string.Empty;
                if (description.Length > MaximumToolDescriptionCharacters)
                {
                    throw new InvalidOperationException($"MCP tool '{name}' description exceeds {MaximumToolDescriptionCharacters} characters.");
                }

                if (!entry.TryGetProperty("inputSchema", out JsonElement schema) ||
                    schema.ValueKind != JsonValueKind.Object ||
                    !schema.TryGetProperty("type", out JsonElement typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String || typeElement.GetString() != "object")
                {
                    throw new InvalidOperationException($"MCP tool '{name}' must provide an object inputSchema.");
                }

                string schemaJson = schema.GetRawText();
                if (schemaJson.Length > MaximumInputSchemaCharacters)
                {
                    throw new InvalidOperationException($"MCP tool '{name}' inputSchema exceeds {MaximumInputSchemaCharacters} characters.");
                }

                tools.Add(new PluginTool(name, description, null, schemaJson));
            }

            cursor = result.TryGetProperty("nextCursor", out JsonElement cursorElement) &&
                     cursorElement.ValueKind == JsonValueKind.String
                ? cursorElement.GetString()
                : null;
            if (!string.IsNullOrEmpty(cursor) && !cursors.Add(cursor))
            {
                throw new InvalidOperationException("MCP tools/list repeated a page cursor.");
            }
        } while (!string.IsNullOrEmpty(cursor));

        return tools;
    }

    private void companionTrace(string text)
    {
        CompanionLog.Trace("Plugins", $"'{_plugin.Name}' said something outside JSON-RPC: {text}");
    }

    public async Task<JsonElement> RequestAsync(string method, Dictionary<string, object?> parameters,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (Failed is not null || _process is null)
        {
            throw new InvalidOperationException(Failed ?? "not started");
        }

        int id = Interlocked.Increment(ref _nextId);
        TaskCompletionSource<JsonElement> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock)
        {
            _pending[id] = waiter;
        }

        string request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToElement(parameters)
        });

        await WriteLineAsync(request, cancellationToken).ConfigureAwait(false);

        // A stuck plugin can jam its tool; 30 s is already generous before it loses the slot.
        Task finished = await Task.WhenAny(waiter.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(30), cancellationToken))
            .ConfigureAwait(false);
        if (finished != waiter.Task)
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await NotifyAsync("notifications/cancelled", new Dictionary<string, object?>
                    {
                        ["requestId"] = id,
                        ["reason"] = "Cancelled by user"
                    }).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    CompanionLog.Trace("Plugins", $"Could not send MCP cancellation for '{_plugin.Name}': {exception.Message}");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            throw new TimeoutException($"'{_plugin.Name}'.{method} timed out after {effectiveTimeout.TotalSeconds:0} s");
        }
        return await waiter.Task.ConfigureAwait(false);
    }

    private async Task NotifyAsync(string method, Dictionary<string, object?> parameters)
    {
        if (_process is null)
        {
            return;
        }
        string notification = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToElement(parameters)
        });
        await WriteLineAsync(notification, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited)
            {
                throw new IOException("The MCP process is not running.");
            }
            await _process.StandardInput.WriteLineAsync(message).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_pendingLock)
        {
            foreach (TaskCompletionSource<JsonElement> waiter in _pending.Values)
            {
                waiter.TrySetException(new ObjectDisposedException(nameof(McpConnection)));
            }

            _pending.Clear();
        }

        _process?.Kill(entireProcessTree: true);
        _process?.Dispose();
        _writeGate.Dispose();
    }
}

internal readonly record struct McpProgressUpdate(double Progress, double? Total, string? Message);

/// <summary>Resolves packaged executables relative to a module and bare runtime names via PATH.</summary>
internal static class PluginCommandPath
{
    public static string Resolve(PluginManifest plugin)
    {
        string command = plugin.Command[0];
        if (Path.IsPathRooted(command))
        {
            return command;
        }

        string local = Path.GetFullPath(Path.Combine(plugin.Folder, command));
        if (File.Exists(local) || IsPathLike(command))
        {
            return local;
        }

        // A bare runtime such as "node" or "python" is intentionally resolved by
        // Process.Start through PATH. Commands containing a path separator remain local
        // to the plugin directory so a typo cannot unexpectedly select another file.
        return command;
    }

    public static bool IsPathLike(string command) =>
        Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) ||
        command.Contains(Path.AltDirectorySeparatorChar);
}
