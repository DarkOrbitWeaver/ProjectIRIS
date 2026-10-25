using CompanionOS.Core.Modules;
using CompanionOS.Core.Modules.BuiltIn;

namespace CompanionOS.App;

/// <summary>
/// The body parts that ship inside the box.
///
/// They live in a separate assembly from the core so the core stays generic: it works
/// with any module the runtime hands it and knows nothing about music or files.
/// </summary>
public static class AppModules
{
    /// <summary>Creates one instance of each built-in module.</summary>
    public static IReadOnlyList<IAssistantModule> CreateBuiltInModules() => new IAssistantModule[]
    {
        new CoreToolsModule(),
        new MusicPlayerModule(),
        new SystemControlModule()
    };
}
