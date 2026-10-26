namespace CompanionOS.App.Interface.Controls;

/// <summary>Maps untrusted module manifest keys to shell-owned glyph geometry.</summary>
internal static class ModuleIconGlyph
{
    public static string Resolve(string? iconKey) => iconKey?.ToLowerInvariant() switch
    {
        "chat" => IconGlyph.Chat,
        "memory" => IconGlyph.Memory,
        "music" => IconGlyph.Play,
        "search" or "web" => IconGlyph.Search,
        "send" or "telegram" => IconGlyph.Send,
        "settings" => IconGlyph.Settings,
        "timer" => IconGlyph.Clock14,
        _ => IconGlyph.Body
    };
}
