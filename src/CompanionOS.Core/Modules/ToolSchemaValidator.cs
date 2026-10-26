using System.Text.Json;
using System.Text.RegularExpressions;

namespace CompanionOS.Core.Modules;

/// <summary>
/// Validates the JSON Schema subset used by Iris tools at the shared execution boundary.
/// Unsupported schema keywords fail closed so a module cannot accidentally run with arguments
/// that Iris did not validate.
/// </summary>
internal static class ToolSchemaValidator
{
    private const int MaximumDepth = 32;
    private static readonly HashSet<string> SupportedKeywords = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$comment", "title", "description", "default", "examples",
        "deprecated", "readOnly", "writeOnly", "format", "type", "properties", "required",
        "additionalProperties", "items", "enum", "const", "minimum", "maximum",
        "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength",
        "pattern", "minItems", "maxItems", "uniqueItems", "minProperties", "maxProperties",
        "allOf", "anyOf", "oneOf"
    };

    public static bool TryValidate(JsonElement arguments, string schemaJson, out string reason)
    {
        reason = string.Empty;
        try
        {
            using JsonDocument schemaDocument = JsonDocument.Parse(schemaJson);
            JsonElement schema = schemaDocument.RootElement;
            return ValidateSchemaDefinition(schema, "$", 0, out reason) &&
                   Validate(arguments, schema, "$", 0, out reason);
        }
        catch (JsonException)
        {
            reason = "the tool has an invalid JSON Schema";
            return false;
        }
    }

    private static bool ValidateSchemaDefinition(JsonElement schema, string path, int depth, out string reason)
    {
        reason = string.Empty;
        if (depth > MaximumDepth || schema.ValueKind != JsonValueKind.Object)
        {
            reason = "the tool schema is invalid or too deeply nested";
            return false;
        }

        JsonProperty[] keywords = schema.EnumerateObject().ToArray();
        if (keywords.Select(keyword => keyword.Name).Distinct(StringComparer.Ordinal).Count() != keywords.Length)
        {
            reason = $"the tool schema at {path} contains duplicate keywords";
            return false;
        }

        foreach (JsonProperty keyword in keywords)
        {
            if (!SupportedKeywords.Contains(keyword.Name))
            {
                reason = $"the tool schema uses unsupported keyword '{keyword.Name}'";
                return false;
            }
            switch (keyword.Name)
            {
                case "type":
                    if (!IsValidType(keyword.Value))
                    {
                        reason = $"the tool schema at {path} has an invalid 'type'";
                        return false;
                    }
                    break;
                case "properties":
                    if (keyword.Value.ValueKind != JsonValueKind.Object)
                    {
                        reason = $"the tool schema at {path} has invalid 'properties'";
                        return false;
                    }
                    foreach (JsonProperty propertySchema in keyword.Value.EnumerateObject())
                    {
                        if (!ValidateSchemaDefinition(propertySchema.Value, $"{path}.{propertySchema.Name}", depth + 1, out reason))
                            return false;
                    }
                    break;
                case "items":
                    if (!ValidateSchemaDefinition(keyword.Value, $"{path}[]", depth + 1, out reason)) return false;
                    break;
                case "additionalProperties":
                    if (keyword.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) &&
                        !ValidateSchemaDefinition(keyword.Value, $"{path}.*", depth + 1, out reason))
                    {
                        reason = string.IsNullOrEmpty(reason) ? $"the tool schema at {path} has invalid 'additionalProperties'" : reason;
                        return false;
                    }
                    break;
                case "required":
                    if (keyword.Value.ValueKind != JsonValueKind.Array ||
                        keyword.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String) ||
                        keyword.Value.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count() != keyword.Value.GetArrayLength())
                    {
                        reason = $"the tool schema at {path} has an invalid 'required' list";
                        return false;
                    }
                    break;
                case "enum":
                    if (keyword.Value.ValueKind != JsonValueKind.Array || keyword.Value.GetArrayLength() == 0)
                    {
                        reason = $"the tool schema at {path} has an invalid 'enum'";
                        return false;
                    }
                    break;
                case "allOf": case "anyOf": case "oneOf":
                    if (keyword.Value.ValueKind != JsonValueKind.Array || keyword.Value.GetArrayLength() == 0)
                    {
                        reason = $"the tool schema at {path} has invalid '{keyword.Name}'";
                        return false;
                    }
                    int index = 0;
                    foreach (JsonElement alternative in keyword.Value.EnumerateArray())
                    {
                        if (!ValidateSchemaDefinition(alternative, $"{path}.{keyword.Name}[{index++}]", depth + 1, out reason))
                            return false;
                    }
                    break;
                case "minProperties": case "maxProperties": case "minItems": case "maxItems":
                case "minLength": case "maxLength":
                    if (!keyword.Value.TryGetInt32(out int count) || count < 0)
                    {
                        reason = $"the tool schema at {path} has invalid '{keyword.Name}'";
                        return false;
                    }
                    break;
                case "minimum": case "maximum": case "exclusiveMinimum": case "exclusiveMaximum": case "multipleOf":
                    if (!keyword.Value.TryGetDecimal(out decimal number) ||
                        keyword.Name == "multipleOf" && number <= 0)
                    {
                        reason = $"the tool schema at {path} has invalid '{keyword.Name}'";
                        return false;
                    }
                    break;
                case "pattern": case "format": case "$schema": case "$id": case "$comment":
                case "title": case "description":
                    if (keyword.Value.ValueKind != JsonValueKind.String)
                    {
                        reason = $"the tool schema at {path} has invalid '{keyword.Name}'";
                        return false;
                    }
                    if (keyword.Name == "pattern")
                    {
                        try { _ = new Regex(keyword.Value.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
                        catch (ArgumentException)
                        {
                            reason = $"the tool schema at {path} has an invalid 'pattern'";
                            return false;
                        }
                    }
                    break;
                case "uniqueItems": case "deprecated": case "readOnly": case "writeOnly":
                    if (keyword.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        reason = $"the tool schema at {path} has invalid '{keyword.Name}'";
                        return false;
                    }
                    break;
            }
        }
        return true;
    }

    private static bool IsValidType(JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
        {
            JsonElement[] types = type.EnumerateArray().ToArray();
            return types.Length > 0 && types.All(IsValidType) &&
                   types.Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count() == types.Length;
        }
        return type.ValueKind == JsonValueKind.String && type.GetString() is
            "object" or "array" or "string" or "boolean" or "null" or "number" or "integer";
    }

    private static bool Validate(JsonElement value, JsonElement schema, string path, int depth,
        out string reason)
    {
        reason = string.Empty;
        if (depth > MaximumDepth || schema.ValueKind != JsonValueKind.Object)
        {
            reason = "the tool schema is invalid or too deeply nested";
            return false;
        }

        foreach (JsonProperty keyword in schema.EnumerateObject())
        {
            if (!SupportedKeywords.Contains(keyword.Name))
            {
                reason = $"the tool schema uses unsupported keyword '{keyword.Name}'";
                return false;
            }
        }

        if (schema.TryGetProperty("type", out JsonElement type) && !MatchesType(value, type))
        {
            reason = $"{path} has the wrong type";
            return false;
        }

        if (schema.TryGetProperty("enum", out JsonElement enumValues) &&
            (enumValues.ValueKind != JsonValueKind.Array || !enumValues.EnumerateArray().Any(item =>
                JsonElement.DeepEquals(item, value))))
        {
            reason = $"{path} is not an allowed value";
            return false;
        }

        if (schema.TryGetProperty("const", out JsonElement constant) && !JsonElement.DeepEquals(constant, value))
        {
            reason = $"{path} does not match the required value";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object && !ValidateObject(value, schema, path, depth, out reason))
        {
            return false;
        }
        if (value.ValueKind == JsonValueKind.Array && !ValidateArray(value, schema, path, depth, out reason))
        {
            return false;
        }
        if (value.ValueKind == JsonValueKind.String && !ValidateString(value, schema, path, out reason))
        {
            return false;
        }
        if (value.ValueKind == JsonValueKind.Number && !ValidateNumber(value, schema, path, out reason))
        {
            return false;
        }

        foreach (string composition in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (!schema.TryGetProperty(composition, out JsonElement alternatives))
            {
                continue;
            }
            if (alternatives.ValueKind != JsonValueKind.Array)
            {
                reason = $"the tool schema has an invalid '{composition}'";
                return false;
            }

            int matches = 0;
            foreach (JsonElement alternative in alternatives.EnumerateArray())
            {
                bool valid = Validate(value, alternative, path, depth + 1, out _);
                if (valid) matches++;
                if (composition == "allOf" && !valid)
                {
                    reason = $"{path} does not satisfy all required schemas";
                    return false;
                }
            }
            if ((composition == "anyOf" && matches == 0) || (composition == "oneOf" && matches != 1))
            {
                reason = $"{path} does not satisfy '{composition}'";
                return false;
            }
        }

        return true;
    }

    private static bool ValidateObject(JsonElement value, JsonElement schema, string path, int depth,
        out string reason)
    {
        reason = string.Empty;
        JsonProperty[] properties = value.EnumerateObject().ToArray();
        if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
        {
            reason = $"{path} contains duplicate property names";
            return false;
        }

        if (schema.TryGetProperty("required", out JsonElement required))
        {
            if (required.ValueKind != JsonValueKind.Array)
            {
                reason = "the tool schema has an invalid 'required' list";
                return false;
            }
            foreach (JsonElement requiredName in required.EnumerateArray())
            {
                if (requiredName.ValueKind != JsonValueKind.String ||
                    !value.TryGetProperty(requiredName.GetString()!, out _))
                {
                    reason = $"{path} is missing a required argument";
                    return false;
                }
            }
        }

        if (!CheckCount(schema, "minProperties", properties.Length, path, out reason) ||
            !CheckCount(schema, "maxProperties", properties.Length, path, out reason))
        {
            return false;
        }

        JsonElement declared = schema.TryGetProperty("properties", out JsonElement declaredProperties)
            ? declaredProperties
            : default;
        if (declared.ValueKind != JsonValueKind.Undefined && declared.ValueKind != JsonValueKind.Object)
        {
            reason = "the tool schema has invalid 'properties'";
            return false;
        }

        foreach (JsonProperty property in properties)
        {
            if (declared.ValueKind == JsonValueKind.Object && declared.TryGetProperty(property.Name, out JsonElement propertySchema))
            {
                if (!Validate(property.Value, propertySchema, $"{path}.{property.Name}", depth + 1, out reason))
                {
                    return false;
                }
                continue;
            }

            if (schema.TryGetProperty("additionalProperties", out JsonElement additional))
            {
                if (additional.ValueKind == JsonValueKind.False)
                {
                    reason = $"{path} contains an unexpected argument '{property.Name}'";
                    return false;
                }
                if (additional.ValueKind == JsonValueKind.Object &&
                    !Validate(property.Value, additional, $"{path}.{property.Name}", depth + 1, out reason))
                {
                    return false;
                }
                if (additional.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Object))
                {
                    reason = "the tool schema has invalid 'additionalProperties'";
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidateArray(JsonElement value, JsonElement schema, string path, int depth,
        out string reason)
    {
        reason = string.Empty;
        JsonElement.ArrayEnumerator items = value.EnumerateArray();
        JsonElement[] values = items.ToArray();
        if (!CheckCount(schema, "minItems", values.Length, path, out reason) ||
            !CheckCount(schema, "maxItems", values.Length, path, out reason))
        {
            return false;
        }
        if (schema.TryGetProperty("uniqueItems", out JsonElement unique) && unique.ValueKind == JsonValueKind.True)
        {
            for (int index = 0; index < values.Length; index++)
            {
                if (values.Skip(index + 1).Any(item => JsonElement.DeepEquals(item, values[index])))
                {
                    reason = $"{path} contains duplicate items";
                    return false;
                }
            }
        }
        if (schema.TryGetProperty("items", out JsonElement itemSchema))
        {
            for (int index = 0; index < values.Length; index++)
            {
                if (!Validate(values[index], itemSchema, $"{path}[{index}]", depth + 1, out reason))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool ValidateString(JsonElement value, JsonElement schema, string path, out string reason)
    {
        reason = string.Empty;
        string text = value.GetString() ?? string.Empty;
        if (!CheckCount(schema, "minLength", text.Length, path, out reason) ||
            !CheckCount(schema, "maxLength", text.Length, path, out reason))
        {
            return false;
        }
        if (schema.TryGetProperty("pattern", out JsonElement pattern))
        {
            if (pattern.ValueKind != JsonValueKind.String)
            {
                reason = "the tool schema has an invalid 'pattern'";
                return false;
            }
            try
            {
                if (!Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    reason = $"{path} does not match the required format";
                    return false;
                }
            }
            catch (ArgumentException)
            {
                reason = "the tool schema has an invalid 'pattern'";
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                reason = "the tool schema's 'pattern' took too long to evaluate";
                return false;
            }
        }
        return true;
    }

    private static bool ValidateNumber(JsonElement value, JsonElement schema, string path, out string reason)
    {
        reason = string.Empty;
        if (!value.TryGetDecimal(out decimal number))
        {
            reason = $"{path} is outside the supported numeric range";
            return false;
        }
        if (!CheckNumber(schema, "minimum", number, expected => number >= expected, path, out reason) ||
            !CheckNumber(schema, "maximum", number, expected => number <= expected, path, out reason) ||
            !CheckNumber(schema, "exclusiveMinimum", number, expected => number > expected, path, out reason) ||
            !CheckNumber(schema, "exclusiveMaximum", number, expected => number < expected, path, out reason))
        {
            return false;
        }
        if (schema.TryGetProperty("multipleOf", out JsonElement multipleOf))
        {
            if (!multipleOf.TryGetDecimal(out decimal divisor) || divisor <= 0)
            {
                reason = "the tool schema has an invalid 'multipleOf'";
                return false;
            }
            if (number % divisor != 0)
            {
                reason = $"{path} is not a permitted multiple";
                return false;
            }
        }
        return true;
    }

    private static bool MatchesType(JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray().Any(candidate => MatchesType(value, candidate));
        }
        if (type.ValueKind != JsonValueKind.String) return false;
        return type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number) && number % 1 == 0,
            _ => false
        };
    }

    private static bool CheckCount(JsonElement schema, string keyword, int actual, string path, out string reason)
    {
        reason = string.Empty;
        if (!schema.TryGetProperty(keyword, out JsonElement limit)) return true;
        if (!limit.TryGetInt32(out int expected) || expected < 0)
        {
            reason = $"the tool schema has an invalid '{keyword}'";
            return false;
        }
        bool valid = keyword.StartsWith("min", StringComparison.Ordinal) ? actual >= expected : actual <= expected;
        if (!valid) reason = $"{path} violates '{keyword}'";
        return valid;
    }

    private static bool CheckNumber(JsonElement schema, string keyword, decimal actual,
        Func<decimal, bool> comparison, string path, out string reason)
    {
        reason = string.Empty;
        if (!schema.TryGetProperty(keyword, out JsonElement limit)) return true;
        if (!limit.TryGetDecimal(out decimal expected))
        {
            reason = $"the tool schema has an invalid '{keyword}'";
            return false;
        }
        bool valid = comparison(expected);
        if (!valid) reason = $"{path} violates '{keyword}'";
        return valid;
    }
}
