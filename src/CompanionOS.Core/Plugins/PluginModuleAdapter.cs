using CompanionOS.Core.Modules;

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
public sealed class PluginModuleAdapter : IAssistantModule
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
                tool.Name,
                string.IsNullOrWhiteSpace(tool.Description) ? $"Run {tool.Name}." : tool.Description,
                ToolTier.SystemCritical,
                string.IsNullOrWhiteSpace(tool.Schema) ? "{\"type\":\"object\"}" : tool.Schema,
                tool.Name,
                tool.Description))
            .ToList();
    }

    public string Id => $"plugin:{_plugin.Id}";

    public string DisplayName => _plugin.Name;

    public string Purpose => string.IsNullOrWhiteSpace(_plugin.Description)
        ? "External capability."
        : _plugin.Description;

    public string CapabilityDescription => string.Join(' ',
        new[] { _plugin.Name, _plugin.Description }
            .Concat(_plugin.Tools.Select(tool => $"{tool.Name} {tool.Description}")));

    public bool AlwaysAvailable => false;

    public IReadOnlyList<ModuleTool> Tools { get; }

    public Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        PluginTool? pluginTool = _plugin.Tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, invocation.Tool.Name, StringComparison.OrdinalIgnoreCase));
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
            cancellationToken).ConfigureAwait(false);

        return result.Ok
            ? ToolResult.Ok(string.IsNullOrWhiteSpace(result.Text) ? "The plugin completed." : result.Text,
                displayText: result.Text)
            : ToolResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? result.Text : result.Error);
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
