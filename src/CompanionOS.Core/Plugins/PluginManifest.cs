using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CompanionOS.Core.Plugins;

/// <summary>
/// The plugin contract: one manifest, one folder, total honesty. A plugin exists when
/// its folder contains a valid plugin.json — if the file is missing or malformed the
/// plugin does not exist, and nothing in her UI may pretend otherwise.
///
/// Transports, all real:
///   • "builtin"  — the tool lives in a compiled module who is declared here so the
///                  folder remains the single source of truth about what she carries.
///   • "command"  — each call spawns a process; JSON (one line, UTF-8) goes in on
///                  stdin, one JSON line comes back on stdout. Exit code 0 = success.
///   • "mcp"      — the plugin speaks the Model Context Protocol over stdio: initialize,
///                  tools/list, tools/call. Her loader maps MCP tool names/schemas into
///                  her own registry, so anything an MCP server offers is offered by her.
///
/// Schema (v1):
/// {
///   "schema": 1,
///   "id": "youtube-control",
///   "name": "YouTube control",
///   "version": "0.1.0",
///   "description": "What this plugin does, one or two sentences.",
///   "transport": "mcp",
///   "command": ["node", "server.js"],
///   "workingDirectory": ".",
///   "asksPermission": ["playlist.write"],
///   "disabled": true,
///   "tools": [
///     { "name": "search", "description": "Search videos.",
///       "permission": null, "schema": { ...JSON schema... } }
///   ]
/// }
/// </summary>
public sealed class PluginManifest
{
    public int Schema { get; init; }
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "0.0.0";
    public string Description { get; init; } = string.Empty;
    public string Transport { get; init; } = "command";
    public List<string> Command { get; init; } = new();
    public string WorkingDirectory { get; init; } = ".";
    public List<string> AsksPermission { get; init; } = new();
    /// <summary>Whether this external process is allowed to join Iris at next launch.
    /// Built-in modules use the runtime switch; folder plugins deliberately require a
    /// restart so no unreviewed executable begins running mid-session.</summary>
    public bool Disabled { get; set; } = true;
    public List<PluginTool> Tools { get; init; } = new();

    public string Folder { get; set; } = string.Empty;

    /// <summary>Parses a manifest; returns the reason it is not usable instead of throwing.</summary>
    public static bool TryLoad(string folder, out PluginManifest? manifest, out string rejectionReason)
    {
        manifest = null;
        string file = Path.Combine(folder, "plugin.json");
        if (!Directory.Exists(folder) || !File.Exists(file))
        {
            rejectionReason = "no plugin.json in the folder";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement;
            List<PluginTool> tools = [];
            if (root.TryGetProperty("tools", out JsonElement toolsElement) &&
                toolsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tool in toolsElement.EnumerateArray())
                {
                    tools.Add(new PluginTool(
                        Name: tool.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "",
                        Description: tool.TryGetProperty("description", out JsonElement descriptionElement) ? descriptionElement.GetString() ?? "" : "",
                        Permission: tool.TryGetProperty("permission", out JsonElement permissionElement) && permissionElement.ValueKind == JsonValueKind.String
                            ? permissionElement.GetString()
                            : null,
                        Schema: tool.TryGetProperty("schema", out JsonElement schemaElement) ? schemaElement.GetRawText() : "{}"));
                }
            }

            List<string> asks = [];
            if (root.TryGetProperty("asksPermission", out JsonElement asksElement) &&
                asksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement ask in asksElement.EnumerateArray())
                {
                    if (ask.ValueKind == JsonValueKind.String)
                    {
                        asks.Add(ask.GetString() ?? "");
                    }
                }
            }

            List<string> command = [];
            if (root.TryGetProperty("command", out JsonElement commandElement) &&
                commandElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement part in commandElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        command.Add(part.GetString() ?? "");
                    }
                }
            }

            bool disabledByDefault = true;
            if (root.TryGetProperty("disabled", out JsonElement disabledElement))
            {
                if (disabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    rejectionReason = "'disabled' must be a boolean; new external modules default to disabled";
                    return false;
                }

                disabledByDefault = disabledElement.ValueKind == JsonValueKind.True;
            }

            PluginManifest parsed = new()
            {
                Schema = root.TryGetProperty("schema", out JsonElement schema) ? schema.GetInt32() : 0,
                Id = root.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" : "",
                Name = root.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                Version = root.TryGetProperty("version", out JsonElement version) ? version.GetString() ?? "0.0.0" : "",
                Description = root.TryGetProperty("description", out JsonElement description) ? description.GetString() ?? "" : "",
                Transport = root.TryGetProperty("transport", out JsonElement transport) ? transport.GetString() ?? "" : "",
                Command = command,
                WorkingDirectory = root.TryGetProperty("workingDirectory", out JsonElement workingDirectory) ? workingDirectory.GetString() ?? "." : ".",
                AsksPermission = asks,
                Disabled = disabledByDefault,
                Tools = tools,
                Folder = folder
            };

            if (parsed.Schema != 1)
            {
                rejectionReason = $"unknown schema {parsed.Schema}, expected 1";
                return false;
            }
            if (string.IsNullOrWhiteSpace(parsed.Id) || string.IsNullOrWhiteSpace(parsed.Name))
            {
                rejectionReason = "id and name are required";
                return false;
            }
            if (parsed.Transport != "builtin" && parsed.Transport != "command" && parsed.Transport != "mcp")
            {
                rejectionReason = $"unknown transport '{parsed.Transport}' (builtin, command, mcp)";
                return false;
            }
            if (parsed.Transport is "command" or "mcp" && parsed.Command.Count == 0)
            {
                rejectionReason = "transport needs a 'command' array";
                return false;
            }

            manifest = parsed;
            rejectionReason = "";
            return true;
        }
        catch (JsonException exception)
        {
            rejectionReason = $"plugin.json does not parse: {exception.Message}";
            return false;
        }
    }
}

/// <summary>One callable tool a plugin offers. Schema keeps the real JSON schema for callers.</summary>
public sealed record PluginTool(
    string Name,
    string Description,
    string? Permission,
    string Schema);

/// <summary>What a plugin run returns; failure carries the plugin's own words.</summary>
public record PluginResult(bool Ok, string Text, string? Error = null);
