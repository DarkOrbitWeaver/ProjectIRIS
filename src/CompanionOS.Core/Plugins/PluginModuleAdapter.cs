using CompanionOS.Core.Modules;
using System.Security.Cryptography;
using System.Text;

namespace CompanionOS.Core.Plugins;

/// <summary>
/// Exposes one validated folder plugin through the same routing and sandbox boundary as a
/// built-in module. PluginHost used to load and display manifests but never register them,
/// which made the Body page look extensible while the model could not actually call a plugin.
///
/// External processes are conservative by default: every plugin tool is system-critical and
/// therefore asks before it runs. A plugin can become less restrictive only after a future
/// permission policy gives that capability an explicit, reviewable tier.
/// </summary>
public sealed class PluginModuleAdapter : IAssistantModule, IModuleSurfaceProvider
{
    private readonly PluginHost _host;
    private readonly PluginManifest _plugin;

    public PluginModuleAdapter(PluginHost host, PluginManifest plugin)
    {
        _host = host;
        _plugin = plugin;

        Tools = plugin.Tools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
            .Select(tool => new ModuleTool(
                QualifiedToolName(plugin.Id, tool.Name),
                string.IsNullOrWhiteSpace(tool.Description) ? $"Run {tool.Name}." :
                    tool.Background ? tool.Description + " This runs in the background and returns a task id; use background-task status or cancel tools to follow it." : tool.Description,
                ToolTier.SystemCritical,
                string.IsNullOrWhiteSpace(tool.Schema) ? "{\"type\":\"object\"}" : tool.Schema,
                tool.Name,
                tool.Description)
            {
                PermissionScope = tool.Permission,
                BackgroundCapable = tool.Background
            })
            .ToList();
    }

    public string Id => $"plugin:{_plugin.Id}";

    public string DisplayName => _plugin.Name;

    public string IconKey => _plugin.ResolveIcon(_plugin.Icon);

    public string Purpose => string.IsNullOrWhiteSpace(_plugin.Description)
        ? "External capability."
        : _plugin.Description;

    public string CapabilityDescription => string.Join(' ',
        new[] { _plugin.Name, _plugin.Description }
            .Concat(_plugin.Tools.Select(tool => $"{tool.Name} {tool.Description}")));

    public bool AlwaysAvailable => false;

    public IReadOnlyList<ModuleTool> Tools { get; }

    public IReadOnlyList<ModuleSurface> GetSurfaces() => _plugin.Surfaces
        .Select(surface => new ModuleSurface
        {
            ModuleId = Id,
            ModuleName = DisplayName,
            Id = $"{Id}:{surface.Id}",
            Title = surface.Title,
            Slot = string.Equals(surface.Slot, "overlay", StringComparison.OrdinalIgnoreCase)
                ? ShellSlot.Overlay
                : ShellSlot.Sidebar,
            Model = surface,
            IconKey = _plugin.ResolveIcon(surface.Icon),
            Elements = surface.Elements.Select(element => new ModuleSurfaceElement
            {
                Id = element.Id,
                Type = element.Type.ToLowerInvariant() switch
                {
                    "text" => ModuleSurfaceElementType.Text,
                    "input" => ModuleSurfaceElementType.Input,
                    "select" => ModuleSurfaceElementType.Select,
                    _ => ModuleSurfaceElementType.Button
                },
                Label = element.Label,
                Name = element.Name,
                Placeholder = element.Placeholder,
                ToolName = QualifiedToolName(_plugin.Id, element.ToolName),
                Options = element.Options.Select(option => new ModuleSurfaceOption(option.Value, option.Label)).ToList(),
                DynamicOptions = element.DynamicOptions
            }).ToList()
        })
        .ToList();

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        PluginTool? pluginTool = _plugin.Tools.FirstOrDefault(tool =>
            string.Equals(QualifiedToolName(_plugin.Id, tool.Name), invocation.Tool.Name,
                StringComparison.OrdinalIgnoreCase));
        if (pluginTool is null)
        {
            return ToolResult.Fail($"The plugin does not provide '{invocation.Tool.Name}'.");
        }

        PluginResult result = await _host.CallAsync(
            _plugin,
            pluginTool,
            invocation.Arguments.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? "{}"
                : invocation.Arguments.GetRawText(),
            invocation.TurnId,
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ToolResult.Ok(string.IsNullOrWhiteSpace(result.Text) ? "The plugin completed." : result.Text,
                displayText: result.Text)
            : ToolResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? result.Text : result.Error);
    }

    public Task ShutdownAsync() => Task.CompletedTask;

    private static string QualifiedToolName(string pluginId, string toolName)
    {
        static string Segment(string value)
        {
            StringBuilder output = new(value.Length);
            foreach (char character in value.ToLowerInvariant())
            {
                if (char.IsAsciiLetterOrDigit(character))
                {
                    output.Append(character);
                }
                else if (output.Length == 0 || output[output.Length - 1] != '_')
                {
                    output.Append('_');
                }
            }

            string normalized = output.ToString().Trim('_');
            return normalized.Length == 0 ? "module" : normalized[..Math.Min(20, normalized.Length)];
        }

        string source = $"{pluginId}\0{toolName}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..10].ToLowerInvariant();
        return $"plugin_{Segment(pluginId)}_{Segment(toolName)}_{hash}";
    }
}
