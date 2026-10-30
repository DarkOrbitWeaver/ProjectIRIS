using System.Text.Json;
using CompanionOS.Core.Prompts;

namespace CompanionOS.Core.Plugins;

/// <summary>
/// Turns an MCP <c>elicitation/create</c> request from a plugin into one of Iris's own
/// prompts, and the user's answer back into the MCP reply shape. The plugin never draws
/// a dialog; it only says what it needs to know.
///
/// Accepted request shapes (MCP 2025-06-18):
/// <code>
/// { "message": "Which one?",
///   "requestedSchema": { "type":"object", "properties": { "choice": {
///        "type":"string", "enum":["1","2"], "enumNames":["Song A","Song B"] } }, "required":["choice"] },
///   "_meta": { "iris": { "kind":"numbered|choice|confirm", "risk":"normal|sensitive", "title":"…",
///              "options":[{"id","label","detail","aliases":[…],"voiceAllowed":true}] } } }
/// </code>
/// A schema with a single boolean property (or no enum at all) becomes a Yes / No confirm.
/// The <c>_meta.iris</c> block is optional sugar: details and voice aliases per option.
/// Reply: <c>{ "action": "accept", "content": { "&lt;prop&gt;": "&lt;id&gt;" } }</c>,
/// or <c>{ "action": "decline" }</c> / <c>{ "action": "cancel" }</c>.
/// </summary>
public static class McpElicitation
{
    private const int MaximumOptions = 12;
    private const int MaximumQuestionCharacters = 240;
    private const int MaximumLabelCharacters = 80;

    public sealed record Parsed(PromptRequest Request, string PropertyName, bool IsBoolean);

    /// <summary>Returns null when the request is not something Iris can ask.</summary>
    public static Parsed? TryParse(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string question = ReadString(parameters, "message", MaximumQuestionCharacters) ?? string.Empty;
        if (question.Length == 0)
        {
            return null;
        }

        JsonElement iris = default;
        bool hasIris = parameters.TryGetProperty("_meta", out JsonElement meta) &&
                       meta.ValueKind == JsonValueKind.Object &&
                       meta.TryGetProperty("iris", out iris) &&
                       iris.ValueKind == JsonValueKind.Object;

        // Find the one property we are asked about.
        string propertyName = "choice";
        JsonElement property = default;
        bool haveProperty = false;
        if (parameters.TryGetProperty("requestedSchema", out JsonElement schema) &&
            schema.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("properties", out JsonElement properties) &&
            properties.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty candidate in properties.EnumerateObject())
            {
                propertyName = candidate.Name;
                property = candidate.Value;
                haveProperty = true;
                break;
            }
        }

        bool isBoolean = haveProperty && property.ValueKind == JsonValueKind.Object &&
                         property.TryGetProperty("type", out JsonElement typeElement) &&
                         typeElement.ValueKind == JsonValueKind.String &&
                         typeElement.GetString() == "boolean";

        List<PromptOption> options = [];
        if (!isBoolean && haveProperty && property.ValueKind == JsonValueKind.Object &&
            property.TryGetProperty("enum", out JsonElement enumElement) &&
            enumElement.ValueKind == JsonValueKind.Array)
        {
            List<string> ids = enumElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(id => id.Length > 0)
                .Take(MaximumOptions)
                .ToList();
            List<string> names = property.TryGetProperty("enumNames", out JsonElement namesElement) &&
                                 namesElement.ValueKind == JsonValueKind.Array
                ? namesElement.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString()).ToList()
                : [];

            Dictionary<string, JsonElement> extras = ReadIrisOptions(hasIris ? iris : default);
            for (int index = 0; index < ids.Count; index++)
            {
                string id = ids[index];
                string label = Clip(index < names.Count && names[index].Length > 0 ? names[index] : id, MaximumLabelCharacters);
                string? detail = null;
                List<string>? aliases = null;
                bool voiceAllowed = true;
                if (extras.TryGetValue(id, out JsonElement extra))
                {
                    detail = ReadString(extra, "detail", MaximumLabelCharacters);
                    string? betterLabel = ReadString(extra, "label", MaximumLabelCharacters);
                    if (betterLabel is not null)
                    {
                        label = betterLabel;
                    }
                    if (extra.TryGetProperty("aliases", out JsonElement aliasElement) && aliasElement.ValueKind == JsonValueKind.Array)
                    {
                        aliases = aliasElement.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => (item.GetString() ?? string.Empty).Trim())
                            .Where(alias => alias.Length is > 0 and <= 40)
                            .Take(6)
                            .ToList();
                    }
                    if (extra.TryGetProperty("voiceAllowed", out JsonElement voiceElement) && voiceElement.ValueKind == JsonValueKind.False)
                    {
                        voiceAllowed = false;
                    }
                }

                options.Add(new PromptOption(id, label, detail, PromptOptionRole.Normal, aliases, voiceAllowed));
            }
        }

        string? kindText = hasIris ? ReadString(iris, "kind", 16)?.ToLowerInvariant() : null;
        PromptKind? kind = kindText switch
        {
            "confirm" => PromptKind.Confirm,
            "choice" => PromptKind.Choice,
            "numbered" => PromptKind.Numbered,
            _ => null
        };
        PromptRisk risk = hasIris && string.Equals(ReadString(iris, "risk", 16), "sensitive", StringComparison.OrdinalIgnoreCase)
            ? PromptRisk.Sensitive
            : PromptRisk.Normal;
        string? title = hasIris ? ReadString(iris, "title", MaximumLabelCharacters) : null;

        if (options.Count < 2)
        {
            // Boolean, or an enum we could not read: ask yes / no.
            options = [];
            kind = PromptKind.Confirm;
            isBoolean = true;
        }

        PromptRequest request = new()
        {
            Question = question,
            Title = title,
            Options = options,
            Kind = kind,
            Risk = risk
        };
        return new Parsed(request, propertyName, isBoolean);
    }

    /// <summary>Builds the JSON-RPC result the plugin receives.</summary>
    public static Dictionary<string, object?> BuildResult(Parsed parsed, PromptAnswer answer)
    {
        if (!answer.IsDeliberate)
        {
            return new Dictionary<string, object?> { ["action"] = answer.TimedOut ? "cancel" : "decline" };
        }

        if (parsed.IsBoolean)
        {
            bool yes = answer.OptionId == PromptIds.Yes;
            return yes
                ? new Dictionary<string, object?>
                {
                    ["action"] = "accept",
                    ["content"] = new Dictionary<string, object?> { [parsed.PropertyName] = true }
                }
                : new Dictionary<string, object?> { ["action"] = "decline" };
        }

        return new Dictionary<string, object?>
        {
            ["action"] = "accept",
            ["content"] = new Dictionary<string, object?> { [parsed.PropertyName] = answer.OptionId }
        };
    }

    private static Dictionary<string, JsonElement> ReadIrisOptions(JsonElement iris)
    {
        Dictionary<string, JsonElement> map = new(StringComparer.Ordinal);
        if (iris.ValueKind != JsonValueKind.Object ||
            !iris.TryGetProperty("options", out JsonElement options) ||
            options.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (JsonElement option in options.EnumerateArray())
        {
            string? id = option.ValueKind == JsonValueKind.Object ? ReadString(option, "id", 64) : null;
            if (id is not null)
            {
                map[id] = option;
            }
        }
        return map;
    }

    private static string? ReadString(JsonElement element, string name, int maximum)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        string text = (value.GetString() ?? string.Empty).Trim();
        return text.Length == 0 ? null : Clip(text, maximum);
    }

    private static string Clip(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
}
