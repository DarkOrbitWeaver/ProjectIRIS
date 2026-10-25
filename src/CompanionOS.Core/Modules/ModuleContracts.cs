using System.Text.Json;
using CompanionOS.Core.Configuration;
using CompanionOS.Core.Events;
using CompanionOS.Core.Mind;
using CompanionOS.Core.Perception;

namespace CompanionOS.Core.Modules;

/// <summary>
/// How dangerous an action is. The guard decides whether it runs silently,
/// runs but is logged, or stops and asks the user first.
/// </summary>
public enum ToolTier
{
    /// <summary>Harmless and reversible: memory, music, timers, status.</summary>
    Reflex = 1,

    /// <summary>Touches her own workspace or whitelisted places. Allowed, always logged.</summary>
    Scoped = 2,

    /// <summary>System level: outside files, closing apps, shell commands. Always asks.</summary>
    SystemCritical = 3
}

/// <summary>
/// One callable action, described in the way a small language model needs:
/// a short snake_case name, a one-line purpose, and a strict JSON schema.
/// </summary>
public sealed class ModuleTool
{
    public ModuleTool(string name, string description, ToolTier tier, string parametersJson,
        params string[] routingKeywords)
    {
        Name = name;
        Description = description;
        Tier = tier;
        ParametersJson = string.IsNullOrWhiteSpace(parametersJson) ? EmptyParametersJson : parametersJson;
        RoutingKeywords = routingKeywords;
    }

    private const string EmptyParametersJson = """{"type":"object","properties":{},"required":[]}""";

    /// <summary>snake_case identifier the model calls.</summary>
    public string Name { get; }

    /// <summary>Plain-English purpose. This is what the model reads when choosing.</summary>
    public string Description { get; }

    public ToolTier Tier { get; }

    /// <summary>JSON Schema for the arguments object.</summary>
    public string ParametersJson { get; }

    /// <summary>Extra words that help the router pick this tool for a request.</summary>
    public IReadOnlyList<string> RoutingKeywords { get; }

    /// <summary>Short line for the tool capsule in the chat.</summary>
    public string ShortLabel => Description.Split('.')[0];
}

/// <summary>What a module may use. Passed once at initialisation, never stored.</summary>
public sealed class ModuleContext
{
    public required CompanionSettings Settings { get; init; }

    /// <summary>Persists a deliberate settings change made by a trusted module and
    /// publishes the matching live-refresh event.</summary>
    public required Action PersistSettings { get; init; }
    public required EventBus Events { get; init; }
    public required IMemoryAccess Memory { get; init; }
    public required SandboxGuard Sandbox { get; init; }

    /// <summary>Her writable folder. Modules should keep everything here.</summary>
    public required string WorkspaceDirectory { get; init; }

    /// <summary>Read-only models folder, for modules that need their own tiny models.</summary>
    public required string ModelsDirectory { get; init; }

    /// <summary>Cache folder for downloads and thumbnails.</summary>
    public required string CacheDirectory { get; init; }

    /// <summary>Her body as seen from inside a module: sleep, look, speak, learn.</summary>
    public required IAssistantHostServices Host { get; init; }
}

/// <summary>
/// The small, stable set of abilities modules may ask the host for.
///
/// Keeping this narrow is deliberate. A module can put her to sleep, look at the
/// screen, describe the machine, speak a line and teach her what a game is — but it
/// cannot reach into the brain, the conversation or another module.
/// </summary>
public interface IAssistantHostServices
{
    /// <summary>Awake, Sleeping or Gaming.</summary>
    OperatingMode CurrentMode { get; }

    /// <summary>True while the microphone is open.</summary>
    bool IsMicrophoneOpen { get; }

    /// <summary>The name she is using right now.</summary>
    string PersonaName { get; }

    /// <summary>Sleeps, wakes or enters gaming mode.</summary>
    Task SetOperatingModeAsync(OperatingMode mode, CancellationToken cancellationToken);
    Task SetManualGamingAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>Captures the screen for a vision-capable module.</summary>
    Task<ScreenFrame?> LookAtScreenAsync(bool forceFreshCapture, CancellationToken cancellationToken);

    /// <summary>Plain-English description of what the machine is doing.</summary>
    string DescribeSituation();

    /// <summary>Short list of installed abilities, one per line.</summary>
    string DescribeCapabilities();

    /// <summary>Teaches the game detector about an executable.</summary>
    void LearnGame(string executableName, bool isGame);

    /// <summary>Opens or closes the microphone.</summary>
    void SetMicrophoneOpen(bool isOpen);

    /// <summary>Speaks one line in her locked voice.</summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken);
}

/// <summary>One tool call, already routed to its module.</summary>
public sealed class ToolInvocation
{
    public required string ModuleId { get; init; }
    public required ModuleTool Tool { get; init; }
    public required JsonElement Arguments { get; init; }
    public required string TurnId { get; init; }

    /// <summary>Reads a string argument, or empty when missing.</summary>
    public string GetString(string argumentName)
    {
        if (Arguments.ValueKind == JsonValueKind.Object &&
            Arguments.TryGetProperty(argumentName, out JsonElement value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.ToString()
            };
        }

        return string.Empty;
    }

    /// <summary>Reads an integer argument, or the fallback when missing or invalid.</summary>
    public int GetInt32(string argumentName, int fallback)
    {
        if (Arguments.ValueKind == JsonValueKind.Object &&
            Arguments.TryGetProperty(argumentName, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    /// <summary>Reads a boolean argument, or the fallback when missing.</summary>
    public bool GetBoolean(string argumentName, bool fallback)
    {
        if (Arguments.ValueKind == JsonValueKind.Object &&
            Arguments.TryGetProperty(argumentName, out JsonElement value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) && parsed,
                _ => fallback
            };
        }

        return fallback;
    }
}

/// <summary>
/// The answer a tool hands back. Three separate texts on purpose:
/// ModelText is what the model reasons over, DisplayText is what the chat shows,
/// SpokenText is what she says out loud. They are often not the same sentence.
/// </summary>
public sealed class ToolResult
{
    public bool Succeeded { get; init; }

    /// <summary>Compact factual text for the model. Never markup.</summary>
    public string ModelText { get; init; } = string.Empty;

    /// <summary>Optional richer text for the chat bubble.</summary>
    public string? DisplayText { get; init; }

    /// <summary>Optional spoken line. Null means "do not speak, the model will".</summary>
    public string? SpokenText { get; init; }

    /// <summary>Optional image (data URI) handed straight to a vision model.</summary>
    public string? ImageDataUri { get; init; }

    /// <summary>Optional reply chips offered to the user, e.g. "Play it".</summary>
    public IReadOnlyList<string> SuggestedReplies { get; init; } = Array.Empty<string>();

    public static ToolResult Ok(string modelText, string? spokenText = null, string? displayText = null) => new()
    {
        Succeeded = true,
        ModelText = modelText,
        SpokenText = spokenText,
        DisplayText = displayText
    };

    public static ToolResult Fail(string reason) => new()
    {
        Succeeded = false,
        ModelText = reason,
        DisplayText = reason
    };
}

/// <summary>
/// Every capability of CompanionOS implements this. Built-in hands and external
/// plugins are treated identically by the router, the sandbox and the brain.
/// </summary>
public interface IAssistantModule
{
    /// <summary>Stable machine name, lower case, no spaces.</summary>
    string Id { get; }

    /// <summary>Name shown in the modules screen.</summary>
    string DisplayName { get; }

    /// <summary>One sentence about what this body part is for.</summary>
    string Purpose { get; }

    /// <summary>Example requests and synonyms. The router matches user speech against this.</summary>
    string CapabilityDescription { get; }

    /// <summary>Everything this module can do.</summary>
    IReadOnlyList<ModuleTool> Tools { get; }

    /// <summary>
    /// True for the few core body parts whose tools are always offered to the model
    /// (memory, screen, sleep). Everything else must earn its place through routing.
    /// </summary>
    bool AlwaysAvailable => false;

    /// <summary>Called once when the module is installed or enabled.</summary>
    Task InitializeAsync(ModuleContext context, CancellationToken cancellationToken);

    /// <summary>Runs one tool call. The sandbox has already approved it.</summary>
    Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);

    /// <summary>Called on shutdown. Must be quick and must not throw.</summary>
    Task ShutdownAsync();
}

/// <summary>Where a module may place a panel inside the interface.</summary>
public enum ShellSlot
{
    /// <summary>Right-hand dock: small always-visible pieces like "now playing".</summary>
    Sidebar,

    /// <summary>Full view reached from the left rail.</summary>
    MainTab,

    /// <summary>Bottom status strip fragment.</summary>
    StatusBar,

    /// <summary>Floating overlay content.</summary>
    Overlay
}

/// <summary>
/// A piece of interface contributed by a module. The shell only knows how to host
/// surfaces, never what they contain, which is what makes growth safe.
/// The Model is a plain observable object from Core; the App decides how to draw it.
/// </summary>
public sealed class ModuleSurface
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required ShellSlot Slot { get; init; }
    public required object Model { get; init; }
}

/// <summary>Optional: modules that want to show something on screen implement this.</summary>
public interface IModuleSurfaceProvider
{
    IReadOnlyList<ModuleSurface> GetSurfaces();
}

/// <summary>
/// Optional module hook for permission prompts whose target depends on tool arguments
/// or module state. The returned text is a verb phrase; ModuleHost adds the module name.
/// </summary>
public interface IModuleActionDescriber
{
    string DescribeAction(ModuleTool tool, JsonElement arguments);
}

/// <summary>
/// The memory abilities modules are allowed to use. Keeping this small on purpose:
/// modules may remember and recall, but only the memory engine may rewrite the graph.
/// </summary>
public interface IMemoryAccess
{
    Task<RecalledMemory> RememberAsync(RememberRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, int maximumResults, CancellationToken cancellationToken);

    /// <summary>Forgets only an exact named memory. Fuzzy deletion is intentionally not
    /// exposed: a companion must ask rather than erase the wrong fact.</summary>
    Task<MemoryForgetResult> ForgetAsync(string title, CancellationToken cancellationToken);
}

/// <summary>The explicit outcome of a memory-forget request.</summary>
public sealed class MemoryForgetResult
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
}

/// <summary>One fact she should keep, expressed the way a person would say it.</summary>
public sealed class RememberRequest
{
    public required string Subject { get; init; }
    public required string Relation { get; init; }
    public required string Target { get; init; }

    /// <summary>Optional longer explanation stored on the subject node.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>0 core self, 1 episodic, 2 semantic, 3 procedural.</summary>
    public int Layer { get; init; } = 2;

    /// <summary>Lower-case tag describing where the fact came from, used for explainability.</summary>
    public string Provenance { get; init; } = "conversation";
}
