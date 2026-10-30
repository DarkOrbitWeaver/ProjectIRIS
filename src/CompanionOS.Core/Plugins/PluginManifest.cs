using System;
using System.Buffers.Binary;
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
///                  tools/call, tools/list discovery, and progress notifications.
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
///   "discoverTools": true,
///   "disabled": true,
///   "tools": [
///     { "name": "search", "description": "Search videos.",
///       "permission": null, "routingKeywords": ["search videos"],
///       "schema": { ...JSON schema... } }
///   ]
/// }
/// </summary>
public sealed class PluginManifest
{
    private static readonly HashSet<string> BuiltInIconKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "body", "chat", "memory", "music", "search", "send", "settings", "telegram", "timer", "web"
    };

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
    /// <summary>Credential names this module may receive in its process environment.</summary>
    public List<string> Secrets { get; init; } = new();
    /// <summary>Obtain callable schemas from MCP tools/list or the command tools/list handshake at startup.</summary>
    public bool DiscoverTools { get; init; }
    /// <summary>Manifest-authored permission and execution policy, retained when schemas are discovered.</summary>
    public List<PluginTool> DeclaredTools { get; init; } = new();
    public List<PluginSurfaceManifest> Surfaces { get; init; } = new();
    /// <summary>Whether this external process is enabled at startup and explicit folder refresh.
    /// Built-in modules use the runtime switch; external code runs only after a user enables it.</summary>
    public bool Disabled { get; set; } = true;
    public List<PluginTool> Tools { get; set; } = new();

    public string Folder { get; set; } = string.Empty;

    /// <summary>Returns the UI transport key for a validated builtin glyph or local PNG asset.</summary>
    public string ResolveIcon(string reference) => BuiltInIconKeys.Contains(reference)
        ? reference
        : "png:" + Path.GetFullPath(Path.Combine(Folder, reference.Replace('/', Path.DirectorySeparatorChar)));

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

                    bool backgroundCapable = false;
                    if (tool.TryGetProperty("background", out JsonElement backgroundElement))
                    {
                        if (backgroundElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            rejectionReason = "tool 'background' must be a boolean";
                            return false;
                        }
                        backgroundCapable = backgroundElement.ValueKind == JsonValueKind.True;
                    }

                    bool alwaysConfirm = false;
                    if (tool.TryGetProperty("alwaysConfirm", out JsonElement alwaysConfirmElement))
                    {
                        if (alwaysConfirmElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            rejectionReason = "tool 'alwaysConfirm' must be a boolean";
                            return false;
                        }
                        alwaysConfirm = alwaysConfirmElement.ValueKind == JsonValueKind.True;
                    }

                    static string? OptionalText(JsonElement element, string property, int maximum)
                    {
                        if (element.TryGetProperty(property, out JsonElement value) &&
                            value.ValueKind == JsonValueKind.String)
                        {
                            string text = (value.GetString() ?? string.Empty).Trim();
                            return text.Length == 0 ? null : text.Length > maximum ? text[..maximum] : text;
                        }

                        return null;
                    }

                    // Optional, friendly extras. A plugin that omits them still works and still reads
                    // in plain words, because Iris humanizes the tool name.
                    bool sessionTrust = tool.TryGetProperty("sessionTrust", out JsonElement sessionTrustElement) &&
                                        sessionTrustElement.ValueKind == JsonValueKind.True;
                    string? access = OptionalText(tool, "access", 8);
                    if (access is not null && access is not ("read" or "act"))
                    {
                        rejectionReason = "tool 'access' must be \"read\" or \"act\"";
                        return false;
                    }

                    int? timeoutSeconds = null;
                    if (tool.TryGetProperty("timeoutSeconds", out JsonElement timeoutElement))
                    {
                        if (timeoutElement.ValueKind != JsonValueKind.Number ||
                            !timeoutElement.TryGetInt32(out int parsedTimeout) ||
                            parsedTimeout < 5 || parsedTimeout > 600)
                        {
                            rejectionReason = "tool 'timeoutSeconds' must be a whole number from 5 to 600";
                            return false;
                        }
                        timeoutSeconds = parsedTimeout;
                    }

                    List<string> routingKeywords = [];
                    if (tool.TryGetProperty("routingKeywords", out JsonElement routingElement))
                    {
                        if (routingElement.ValueKind != JsonValueKind.Array ||
                            routingElement.GetArrayLength() > 16)
                        {
                            rejectionReason = "tool 'routingKeywords' must be an array with at most 16 entries";
                            return false;
                        }
                        foreach (JsonElement keywordElement in routingElement.EnumerateArray())
                        {
                            if (keywordElement.ValueKind != JsonValueKind.String)
                            {
                                rejectionReason = "each 'routingKeywords' entry must be a string";
                                return false;
                            }
                            string keyword = (keywordElement.GetString() ?? string.Empty).Trim();
                            if (keyword.Length is < 3 or > 64)
                            {
                                rejectionReason = "routing keywords must contain 3 to 64 characters";
                                return false;
                            }
                            if (!routingKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                            {
                                routingKeywords.Add(keyword);
                            }
                        }
                    }

                    tools.Add(new PluginTool(
                        Name: tool.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? "" : "",
                        Description: tool.TryGetProperty("description", out JsonElement descriptionElement) ? descriptionElement.GetString() ?? "" : "",
                        Permission: permission,
                        Schema: tool.TryGetProperty("schema", out JsonElement schemaElement) ? schemaElement.GetRawText() : "{}",
                        Background: backgroundCapable,
                        AlwaysConfirm: alwaysConfirm,
                        SessionTrust: sessionTrust,
                        Label: OptionalText(tool, "label", 80),
                        DoneLabel: OptionalText(tool, "doneLabel", 80),
                        Icon: OptionalText(tool, "icon", 32),
                        Access: access,
                        TimeoutSeconds: timeoutSeconds,
                        RoutingKeywords: routingKeywords));
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

            List<string> secrets = [];
            if (root.TryGetProperty("secrets", out JsonElement secretsElement))
            {
                if (secretsElement.ValueKind != JsonValueKind.Array)
                {
                    rejectionReason = "'secrets' must be an array of credential names";
                    return false;
                }
                HashSet<string> environmentNames = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonElement secret in secretsElement.EnumerateArray())
                {
                    if (secret.ValueKind != JsonValueKind.String)
                    {
                        rejectionReason = "each 'secrets' entry must be a string";
                        return false;
                    }
                    string secretName = secret.GetString() ?? string.Empty;
                    string environmentName = ToSecretEnvironmentName(secretName);
                    if (!IsSecretName(secretName) || !environmentNames.Add(environmentName))
                    {
                        rejectionReason = $"credential name '{secretName}' is invalid or collides with another environment name";
                        return false;
                    }
                    secrets.Add(secretName);
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

                            List<PluginSurfaceOptionManifest> options = [];
                            if (element.TryGetProperty("options", out JsonElement optionsElement))
                            {
                                if (optionsElement.ValueKind != JsonValueKind.Array)
                                {
                                    rejectionReason = "UI element 'options' must be an array";
                                    return false;
                                }
                                foreach (JsonElement option in optionsElement.EnumerateArray())
                                {
                                    if (option.ValueKind != JsonValueKind.Object)
                                    {
                                        rejectionReason = "each select option must be an object";
                                        return false;
                                    }
                                    options.Add(new PluginSurfaceOptionManifest(
                                        ReadString(option, "value"), ReadString(option, "label")));
                                }
                            }

                            bool dynamicOptions = false;
                            if (element.TryGetProperty("dynamic", out JsonElement dynamicElement))
                            {
                                if (dynamicElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                                {
                                    rejectionReason = "UI element 'dynamic' must be a boolean";
                                    return false;
                                }
                                dynamicOptions = dynamicElement.ValueKind == JsonValueKind.True;
                            }

                            elements.Add(new PluginSurfaceElementManifest(
                                ReadString(element, "id"),
                                ReadString(element, "type"),
                                ReadString(element, "label"),
                                ReadString(element, "name"),
                                ReadString(element, "placeholder"),
                                ReadString(element, "tool"), options, dynamicOptions));
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

            bool discoverTools = false;
            if (root.TryGetProperty("discoverTools", out JsonElement discoverElement))
            {
                if (discoverElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    rejectionReason = "'discoverTools' must be a boolean";
                    return false;
                }
                discoverTools = discoverElement.ValueKind == JsonValueKind.True;
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
                Secrets = secrets,
                DiscoverTools = discoverTools,
                Surfaces = surfaces,
                Disabled = disabledByDefault,
                Tools = tools,
                DeclaredTools = tools,
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
            if (parsed.DiscoverTools && parsed.Transport is not ("mcp" or "command"))
            {
                rejectionReason = "'discoverTools' is supported only for MCP or command modules";
                return false;
            }

            if (!ValidateSurfaces(parsed, out rejectionReason, validateActionsAndSchemas: !parsed.DiscoverTools))
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

    internal static bool ValidateDiscoveredSurfaces(PluginManifest plugin, out string rejectionReason) =>
        ValidateSurfaces(plugin, out rejectionReason, validateActionsAndSchemas: true);

    private static bool ValidateSurfaces(PluginManifest plugin, out string rejectionReason,
        bool validateActionsAndSchemas)
    {
        const int maximumSurfaces = 8;
        const int maximumSidebarSurfaces = 5;
        const int maximumOverlaySurfaces = 3;
        const int maximumElementsPerSurface = 20;
        HashSet<string> surfaceIds = new(StringComparer.OrdinalIgnoreCase);

        if (!IsValidIconReference(plugin.Folder, plugin.Icon))
        {
            rejectionReason = $"module icon '{plugin.Icon}' must be a builtin icon key or a safe PNG asset inside the module folder";
            return false;
        }

        if (plugin.Surfaces.Count > maximumSurfaces)
        {
            rejectionReason = $"at most {maximumSurfaces} UI surfaces are allowed (up to five sidebar and three overlay surfaces)";
            return false;
        }

        if (plugin.Surfaces.Count(surface =>
                string.Equals(surface.Slot, "sidebar", StringComparison.OrdinalIgnoreCase)) > maximumSidebarSurfaces)
        {
            rejectionReason = $"at most {maximumSidebarSurfaces} sidebar surfaces are allowed per module";
            return false;
        }

        if (plugin.Surfaces.Count(surface =>
                string.Equals(surface.Slot, "overlay", StringComparison.OrdinalIgnoreCase)) > maximumOverlaySurfaces)
        {
            rejectionReason = $"at most {maximumOverlaySurfaces} independent overlay surfaces are allowed per module";
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
            if (!string.Equals(surface.Slot, "sidebar", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(surface.Slot, "overlay", StringComparison.OrdinalIgnoreCase))
            {
                rejectionReason = $"UI surface '{surface.Id}' uses unsupported slot '{surface.Slot}'; v1 supports sidebar and overlay";
                return false;
            }
            if (!IsValidIconReference(plugin.Folder, surface.Icon))
            {
                rejectionReason = $"UI surface '{surface.Id}' icon must be a builtin key or a safe PNG asset inside the module folder";
                return false;
            }
            if (surface.Elements.Count is 0 or > maximumElementsPerSurface)
            {
                rejectionReason = $"UI surface '{surface.Id}' must contain 1 to {maximumElementsPerSurface} elements";
                return false;
            }

            HashSet<string> elementIds = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> inputNames = surface.Elements
                .Where(element => string.Equals(element.Type, "input", StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(element.Type, "select", StringComparison.OrdinalIgnoreCase))
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
                if (!string.Equals(element.Type, "select", StringComparison.OrdinalIgnoreCase) &&
                    (element.Options.Count > 0 || element.DynamicOptions))
                {
                    rejectionReason = $"only select element '{element.Id}' may declare options";
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

                if (string.Equals(element.Type, "select", StringComparison.OrdinalIgnoreCase))
                {
                    HashSet<string> optionValues = new(StringComparer.Ordinal);
                    if (!IsIdentifier(element.Name) || string.IsNullOrWhiteSpace(element.Label) ||
                        !seenInputNames.Add(element.Name) || element.Options.Count > 64 ||
                        (element.Options.Count == 0 && !element.DynamicOptions) ||
                        element.Options.Any(option => string.IsNullOrWhiteSpace(option.Value) || option.Value.Length > 96 ||
                                                      string.IsNullOrWhiteSpace(option.Label) || option.Label.Length > 96 ||
                                                      !optionValues.Add(option.Value)))
                    {
                        rejectionReason = $"select element '{element.Id}' needs a unique name, label, and 1 to 64 unique options";
                        return false;
                    }
                    continue;
                }

                PluginTool? targetTool = plugin.Tools.FirstOrDefault(tool =>
                    string.Equals(tool.Name, element.ToolName, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(element.Type, "button", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(element.Label) || (validateActionsAndSchemas && targetTool is null))
                {
                    rejectionReason = $"UI element '{element.Id}' must be text, input, select, or a button for a declared tool";
                    return false;
                }

                if (targetTool is not null && validateActionsAndSchemas &&
                    !ValidateStringInputs(surface, targetTool, inputNames, out rejectionReason))
                {
                    return false;
                }
            }
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool IsValidIconReference(string pluginFolder, string reference)
    {
        if (BuiltInIconKeys.Contains(reference))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) ||
            !string.Equals(Path.GetExtension(reference), ".png", StringComparison.OrdinalIgnoreCase) ||
            reference.Contains(':') || reference.Contains('\0'))
        {
            return false;
        }

        string[] segments = reference.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginFolder));
            string path = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
            string rootPrefix = root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            string current = root;
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            FileInfo file = new(path);
            if (!file.Exists || file.Length is <= 24 or > 131_072)
            {
                return false;
            }

            using FileStream stream = file.OpenRead();
            byte[] header = new byte[24];
            if (stream.Read(header, 0, header.Length) != header.Length ||
                !header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)) != 13 ||
                !header.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            {
                return false;
            }

            uint width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
            return width is > 0 and <= 128 && height is > 0 and <= 128;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or
                                           NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool ValidatePermissions(PluginManifest plugin, out string rejectionReason)
    {
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> toolNames = new(StringComparer.OrdinalIgnoreCase);
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
            if (!toolNames.Add(tool.Name))
            {
                rejectionReason = $"tool name '{tool.Name}' is duplicated";
                return false;
            }
            if (string.IsNullOrWhiteSpace(tool.Name) ||
                (!plugin.DiscoverTools && string.IsNullOrWhiteSpace(tool.Description)))
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

    private static bool IsSecretName(string value) =>
        value.Length is > 0 and <= 64 && char.IsAsciiLetter(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string ToSecretEnvironmentName(string secretName) =>
        "IRIS_SECRET_" + secretName.Replace('.', '_').Replace('-', '_').ToUpperInvariant();

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

            PluginSurfaceElementManifest? select = surface.Elements.FirstOrDefault(element =>
                string.Equals(element.Name, inputName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(element.Type, "select", StringComparison.OrdinalIgnoreCase));
            if (select is not null && inputSchema.TryGetProperty("enum", out JsonElement enumValues) &&
                enumValues.ValueKind == JsonValueKind.Array)
            {
                HashSet<string> allowedValues = enumValues.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString() ?? string.Empty)
                    .ToHashSet(StringComparer.Ordinal);
                if (select.Options.Any(option => !allowedValues.Contains(option.Value)))
                {
                    rejectionReason = $"select '{select.Name}' has an option outside tool '{tool.Name}''s enum";
                    return false;
                }
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
    string Schema,
    bool Background = false,
    bool AlwaysConfirm = false,
    bool SessionTrust = false,
    string? Label = null,
    string? DoneLabel = null,
    string? Icon = null,
    string? Access = null,
    int? TimeoutSeconds = null,
    List<string>? RoutingKeywords = null);

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
    string ToolName,
    List<PluginSurfaceOptionManifest> Options,
    bool DynamicOptions = false);

public sealed record PluginSurfaceOptionManifest(string Value, string Label);

/// <summary>What a plugin run returns; failure carries the plugin's own words.</summary>
public record PluginResult(bool Ok, string Text, string? Error = null, bool IsOutcomeUncertain = false,
    string? SpokenText = null);
