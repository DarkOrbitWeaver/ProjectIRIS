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
        if (plugin.Disabled)
        {
            return new PluginResult(false, "", $"Plugin '{plugin.Name}' is disabled in its manifest.");
        }

        try
        {
            if (plugin.Transport == "command")
            {
                return await CallCommandAsync(plugin, argumentsJson, cancellationToken).ConfigureAwait(false);
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

    private async Task<PluginResult> CallCommandAsync(PluginManifest plugin,
        string argumentsJson, CancellationToken cancellationToken)
    {
        string fileName = plugin.Command[0];
        if (!Path.IsPathRooted(fileName))
        {
            fileName = Path.GetFullPath(Path.Combine(plugin.Folder, fileName));
        }
        if (!File.Exists(fileName))
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

        string payload = JsonSerializer.Serialize(new
        {
            plugin = plugin.Id,
            arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement.Clone()
        });
        await process.StandardInput.WriteLineAsync(payload).ConfigureAwait(false);

        Task<string> standardOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(standardOutTask, standardErrorTask).ConfigureAwait(false);
        string standardOut = await standardOutTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        double cost = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        _events.Publish(new PluginActivityEvent(plugin.Id, "call", $"{plugin.Name} answered in {cost:0} ms"));

        if (process.ExitCode != 0)
        {
            string error = string.IsNullOrWhiteSpace(standardError) ? $"exit code {process.ExitCode}" : standardError.Trim();
            return new PluginResult(false, standardOut.Trim(), error);
        }

        return new PluginResult(true, standardOut.Trim());
    }

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
    private readonly PluginManifest _plugin;
    private Process? _process;
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = [];
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
            string fileName = _plugin.Command[0];
            if (!File.Exists(fileName) && !Path.IsPathRooted(fileName))
            {
                fileName = Path.GetFullPath(Path.Combine(_plugin.Folder, fileName));
            }
            if (!File.Exists(fileName))
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

    private void companionTrace(string text)
    {
        CompanionLog.Trace("Plugins", $"'{_plugin.Name}' said something outside JSON-RPC: {text}");
    }

    public async Task<JsonElement> RequestAsync(string method, Dictionary<string, object?> parameters,
        CancellationToken cancellationToken)
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

        await _process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);

        // A stuck plugin can jam its tool; 30 s is already generous before it loses the slot.
        Task finished = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken))
            .ConfigureAwait(false);
        if (finished != waiter.Task)
        {
            lock (_pendingLock)
            {
                _pending.Remove(id);
            }
            throw new TimeoutException($"'{_plugin.Name}'.{method} timed out after 30 s");
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
        await _process.StandardInput.WriteLineAsync(notification).ConfigureAwait(false);
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
    }
}

internal readonly record struct McpProgressUpdate(double Progress, double? Total, string? Message);
