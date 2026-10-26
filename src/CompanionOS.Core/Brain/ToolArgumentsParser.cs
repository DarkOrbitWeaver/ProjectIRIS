using System.Text.Json;

namespace CompanionOS.Core.Brain;

/// <summary>Parses model supplied tool arguments without turning invalid input into an action.</summary>
internal static class ToolArgumentsParser
{
    public static bool TryParse(string? json, out JsonElement arguments)
    {
        arguments = default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            arguments = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
