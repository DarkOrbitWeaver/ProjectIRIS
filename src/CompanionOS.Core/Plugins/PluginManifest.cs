using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CompanionOS.Core.Modules;

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
///                  tools/call, and progress notifications. Tool definitions currently live
///                  in this manifest; dynamic tools/list discovery is a planned SDK feature.
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
    public string Icon { get; init; } = "body";
    public string Transport { get; init; } = "command";
    public List<string> Command { get; init; } = new();
    public string WorkingDirectory { get; init; } = ".";
    public List<string> AsksPermission { get; init; } = new();
    public List<PluginSurfaceManifest> Surfaces { get; init; } = new();
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
            if (root.TryGetProperty("tools", out JsonElement toolsElement))
            {
                if (toolsElement.ValueKind != JsonValueKind.Array)
                {
                    rejectionReason = "'tools' must be an array";
                    return false;
                }

                foreach (JsonElement tool in toolsElement.EnumerateArray())
                {
                    if (tool.ValueKind != JsonValueKind.Object)
                    {
                        rejectionReason = "each tool declaration must be an object";
                        return false;
                    }

                    string? permission = null;
                    if (tool.TryGetProperty("permission", out JsonElement permissionElement))
                    {
                        if (permissionElement.ValueKind == JsonValueKind.String)
                        {
                            permission = permissionElement.GetString();
                        }
                        else if (permissionElement.ValueKind != JsonValueKind.Null)
                        {
                            rejectionReason = "tool 'permission' must be a string or null";
                            return false;
                        }
                    }

                    tools.Add(new PluginTool(
                        Name: tool.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "",
                        Description: tool.TryGetProperty("description", out JsonElement descriptionElement) ? descriptionElement.GetString() ?? "" : "",
                        Permission: permission,
                        Schema: tool.TryGetProperty("schema", out JsonElement schemaElement) ? schemaElement.GetRawText() : "{}"));
                }
            }

            List<string> asks = [];
            if (root.TryGetProperty("asksPermission", out JsonElement asksElement))
            {
                if (asksElement.ValueKind != JsonValueKind.Array)
                {
                    rejectionReason = "'asksPermission' must be an array of capability names";
                    return false;
                }

                foreach (JsonElement ask in asksElement.EnumerateArray())
                {
                    if (ask.ValueKind != JsonValueKind.String)
                    {
                        rejectionReason = "each 'asksPermission' entry must be a string";
                        return false;
                    }
                    asks.Add(ask.GetString() ?? "");
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

            List<PluginSurfaceManifest> surfaces = [];
            if (root.TryGetProperty("surfaces", out JsonElement surfacesElement))
            {
                if (surfacesElement.ValueKind != JsonValueKind.Array)
                {
                    rejectionReason = "'surfaces' must be an array";
                    return false;
                }

                foreach (JsonElement surface in surfacesElement.EnumerateArray())
                {
                    if (surface.ValueKind != JsonValueKind.Object)
                    {
                        rejectionReason = "each UI surface must be an object";
                        return false;
                    }

                    List<PluginSurfaceElementManifest> elements = [];
                    if (surface.TryGetProperty("elements", out JsonElement elementsElement))
                    {
                        if (elementsElement.ValueKind != JsonValueKind.Array)
                        {
                            rejectionReason = "surface 'elements' must be an array";
                            return false;
                        }

                        foreach (JsonElement element in elementsElement.EnumerateArray())
                        {
                            if (element.ValueKind != JsonValueKind.Object)
                            {
                                rejectionReason = "each UI element must be an object";
                                return false;
                            }

                            elements.Add(new PluginSurfaceElementManifest(
                                ReadString(element, "id"),
                                ReadString(element, "type"),
                                ReadString(element, "label"),
                                ReadString(element, "name"),
                                ReadString(element, "placeholder"),
                                ReadString(element, "tool")));
                        }
                    }

                    surfaces.Add(new PluginSurfaceManifest(
                        ReadString(surface, "id"),
                        ReadString(surface, "title"),
                        ReadString(surface, "slot"),
                        ReadString(surface, "icon"),
                        elements));
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
                Icon = string.IsNullOrWhiteSpace(ReadString(root, "icon")) ? "body" : ReadString(root, "icon"),
                Transport = root.TryGetProperty("transport", out JsonElement transport) ? transport.GetString() ?? "" : "",
                Command = command,
                WorkingDirectory = root.TryGetProperty("workingDirectory", out JsonElement workingDirectory) ? workingDirectory.GetString() ?? "." : ".",
                AsksPermission = asks,
                Surfaces = surfaces,
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

            if (!ValidateSurfaces(parsed, out rejectionReason))
            {
                return false;
            }
            if (!ValidatePermissions(parsed, out rejectionReason))
            {
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
        catch (InvalidOperationException exception)
        {
            rejectionReason = $"plugin.json contains a value of the wrong type: {exception.Message}";
            return false;
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ValidateSurfaces(PluginManifest plugin, out string rejectionReason)
    {
        const int maximumSurfaces = 6;
        const int maximumElementsPerSurface = 20;
        string[] iconKeys = ["body", "chat", "memory", "music", "search", "send", "settings", "telegram", "timer", "web"];
        HashSet<string> surfaceIds = new(StringComparer.OrdinalIgnoreCase);

        if (!iconKeys.Contains(plugin.Icon, StringComparer.OrdinalIgnoreCase))
        {
            rejectionReason = $"module uses unknown icon key '{plugin.Icon}'";
            return false;
        }

        if (plugin.Surfaces.Count > maximumSurfaces)
        {
            rejectionReason = $"at most {maximumSurfaces} UI surfaces are allowed";
            return false;
        }

        foreach (PluginSurfaceManifest surface in plugin.Surfaces)
        {
            if (!IsIdentifier(surface.Id) || !surfaceIds.Add(surface.Id) ||
                string.IsNullOrWhiteSpace(surface.Title) || surface.Title.Length > 64)
            {
                rejectionReason = "each UI surface needs a unique id and a title of at most 64 characters";
                return false;
            }
            if (!string.Equals(surface.Slot, "sidebar", StringComparison.OrdinalIgnoreCase))
            {
                rejectionReason = $"UI surface '{surface.Id}' uses unsupported slot '{surface.Slot}'; v1 supports sidebar";
                return false;
            }
            if (!iconKeys.Contains(surface.Icon, StringComparer.OrdinalIgnoreCase))
            {
                rejectionReason = $"UI surface '{surface.Id}' uses unknown icon key '{surface.Icon}'";
                return false;
            }
            if (surface.Elements.Count is 0 or > maximumElementsPerSurface)
            {
                rejectionReason = $"UI surface '{surface.Id}' must contain 1 to {maximumElementsPerSurface} elements";
                return false;
            }

            HashSet<string> elementIds = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> inputNames = surface.Elements
                .Where(element => string.Equals(element.Type, "input", StringComparison.OrdinalIgnoreCase))
                .Select(element => element.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenInputNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (PluginSurfaceElementManifest element in surface.Elements)
            {
                if (!IsIdentifier(element.Id) || !elementIds.Add(element.Id) ||
                    element.Label.Length > 96 || element.Placeholder.Length > 96)
                {
                    rejectionReason = $"UI surface '{surface.Id}' has an invalid or duplicate element id, or oversized text";
                    return false;
                }

                if (string.Equals(element.Type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(element.Label))
                    {
                        rejectionReason = $"text element '{element.Id}' needs label text";
                        return false;
                    }
                    continue;
                }

                if (string.Equals(element.Type, "input", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsIdentifier(element.Name) || string.IsNullOrWhiteSpace(element.Label) ||
                        !seenInputNames.Add(element.Name))
                    {
                        rejectionReason = $"input element '{element.Id}' needs a unique name and label";
                        return false;
                    }
                    continue;
                }

                PluginTool? targetTool = plugin.Tools.FirstOrDefault(tool =>
                    string.Equals(tool.Name, element.ToolName, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(element.Type, "button", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(element.Label) || targetTool is null)
                {
                    rejectionReason = $"UI element '{element.Id}' must be text, input, or a button for a declared tool";
                    return false;
                }

                if (!ValidateStringInputs(surface, targetTool, inputNames, out rejectionReason))
                {
                    return false;
                }
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool ValidatePermissions(PluginManifest plugin, out string rejectionReason)
    {
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        foreach (string scope in plugin.AsksPermission)
        {
            if (!IsPermissionScope(scope) || !declared.Add(scope))
            {
                rejectionReason = $"permission scope '{scope}' is invalid or duplicated";
                return false;
            }
        }

        foreach (PluginTool tool in plugin.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || string.IsNullOrWhiteSpace(tool.Description))
            {
                rejectionReason = "every tool needs a name and description";
                return false;
            }
            if (tool.Permission is not null && !IsPermissionScope(tool.Permission))
            {
                rejectionReason = $"tool '{tool.Name}' has invalid permission scope '{tool.Permission}'";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(tool.Permission) && !declared.Contains(tool.Permission))
            {
                rejectionReason = $"tool '{tool.Name}' requests undeclared permission scope '{tool.Permission}'";
                return false;
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool IsPermissionScope(string value) =>
        value.Length is > 0 and <= 96 && char.IsAsciiLetter(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool ValidateStringInputs(PluginSurfaceManifest surface, PluginTool tool,
        HashSet<string> inputNames, out string rejectionReason)
    {
        using JsonDocument schemaDocument = JsonDocument.Parse(tool.Schema);
        JsonElement schema = schemaDocument.RootElement;
        JsonElement properties = schema.TryGetProperty("properties", out JsonElement propertiesElement)
            ? propertiesElement
            : default;

        foreach (string inputName in inputNames)
        {
            if (properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty(inputName, out JsonElement inputSchema))
            {
                rejectionReason = $"input '{inputName}' on surface '{surface.Id}' is not a property of tool '{tool.Name}'";
                return false;
            }

            if (!inputSchema.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String || typeElement.GetString() != "string")
            {
                rejectionReason = $"sidebar input '{inputName}' must map to a string property on tool '{tool.Name}'";
                return false;
            }
        }

        if (schema.TryGetProperty("required", out JsonElement required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement requiredName in required.EnumerateArray())
            {
                if (requiredName.ValueKind == JsonValueKind.String &&
                    !inputNames.Contains(requiredName.GetString() ?? string.Empty))
                {
                    rejectionReason = $"surface '{surface.Id}' has no input for required argument '{requiredName.GetString()}' of tool '{tool.Name}'";
                    return false;
                }
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= 64 &&
        char.IsAsciiLetter(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}

/// <summary>One callable tool a plugin offers. Schema keeps the real JSON schema for callers.</summary>
public sealed record PluginTool(
    string Name,
    string Description,
    string? Permission,
    string Schema);

public sealed record PluginSurfaceManifest(
    string Id,
    string Title,
    string Slot,
    string Icon,
    List<PluginSurfaceElementManifest> Elements);

public sealed record PluginSurfaceElementManifest(
    string Id,
    string Type,
    string Label,
    string Name,
    string Placeholder,
    string ToolName);

/// <summary>What a plugin run returns; failure carries the plugin's own words.</summary>
public record PluginResult(bool Ok, string Text, string? Error = null);
