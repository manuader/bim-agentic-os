using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// The JSON Schema subset the catalogue uses. The same schema text is published as the MCP tool inputSchema and
// enforced here, so the advertised structure and the accepted structure cannot drift apart. Numeric ranges are
// deliberately not schema keywords: they are typed unit rules (length_invalid, arc_*) applied after this check.
public static class RevitCommandSchema
{
    static readonly string[] Keywords = { "type", "description", "properties", "required", "additionalProperties", "enum", "items", "minItems", "maxItems", "minLength", "maxLength", "pattern", "oneOf" };
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    // Throws InvalidOperationException when a schema uses a keyword this validator does not enforce.
    public static void CheckSupported(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("schema must be an object");
        foreach (var keyword in schema.EnumerateObject())
        {
            if (!Keywords.Contains(keyword.Name, StringComparer.Ordinal)) throw new InvalidOperationException("unsupported schema keyword " + keyword.Name);
            if (keyword.Name == "additionalProperties" && keyword.Value.ValueKind != JsonValueKind.False) throw new InvalidOperationException("only additionalProperties:false is supported");
        }
        if (schema.TryGetProperty("properties", out var properties)) foreach (var property in properties.EnumerateObject()) CheckSupported(property.Value);
        if (schema.TryGetProperty("items", out var items)) CheckSupported(items);
        if (schema.TryGetProperty("oneOf", out var branches)) foreach (var branch in branches.EnumerateArray()) CheckSupported(branch);
        if (schema.TryGetProperty("type", out var type) && type.GetString() == "object" && !schema.TryGetProperty("additionalProperties", out _))
            throw new InvalidOperationException("object schemas must set additionalProperties:false");
    }

    // Null when value conforms; otherwise the first failure as (JSON path, reason).
    public static (string Field, string Detail)? Validate(JsonElement value, JsonElement schema, string field)
    {
        if (schema.TryGetProperty("oneOf", out var branches)) return OneOf(value, branches, field);
        if (schema.TryGetProperty("type", out var typeElement) && !HasType(value, typeElement.GetString()!)) return (field, "expected " + typeElement.GetString());
        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(option => option.ValueKind == value.ValueKind && option.GetRawText() == value.GetRawText()))
            return (field, "expected one of " + string.Join(", ", allowed.EnumerateArray().Select(option => option.GetRawText())));
        switch (value.ValueKind)
        {
            case JsonValueKind.Object: return ValidateObject(value, schema, field);
            case JsonValueKind.Array: return ValidateArray(value, schema, field);
            case JsonValueKind.String: return ValidateString(value.GetString()!, schema, field);
            default: return null;
        }
    }

    static bool HasType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => throw new InvalidOperationException("unsupported schema type " + type),
    };

    static (string Field, string Detail)? ValidateObject(JsonElement value, JsonElement schema, string field)
    {
        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var closed = schema.TryGetProperty("additionalProperties", out _);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var path = field + "." + property.Name;
            if (!seen.Add(property.Name)) return (path, "duplicate member");
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                if (Validate(property.Value, propertySchema, path) is { } failure) return failure;
            }
            else if (closed) return (path, "unexpected member");
        }
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
                if (!seen.Contains(name)) return (field + "." + name, "required");
        }
        return null;
    }

    static (string Field, string Detail)? ValidateArray(JsonElement value, JsonElement schema, string field)
    {
        var count = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32()) return (field, "at least " + min.GetInt32() + " items");
        if (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32()) return (field, "at most " + max.GetInt32() + " items");
        if (schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (Validate(item, items, field + "[" + index + "]") is { } failure) return failure;
                index++;
            }
        }
        return null;
    }

    static (string Field, string Detail)? ValidateString(string value, JsonElement schema, string field)
    {
        if (schema.TryGetProperty("minLength", out var min) && value.Length < min.GetInt32()) return (field, "at least " + min.GetInt32() + " characters");
        if (schema.TryGetProperty("maxLength", out var max) && value.Length > max.GetInt32()) return (field, "at most " + max.GetInt32() + " characters");
        if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(value, Anchored(pattern.GetString()!), RegexOptions.CultureInvariant, RegexTimeout)) return (field, "does not match " + pattern.GetString());
        return null;
    }

    // JSON Schema patterns are ECMA-262, where a final $ is the end of input; .NET's $ also matches before a final
    // newline, so it is enforced as \z.
    static string Anchored(string pattern) =>
        pattern.EndsWith('$') && !pattern.EndsWith("\\$", StringComparison.Ordinal) ? pattern[..^1] + "\\z" : pattern;

    // Exactly one branch must match. Without a match, the failure from the branch declaring most
    // of the value's members is reported, then the one that got deepest past the kind discriminator.
    static (string Field, string Detail)? OneOf(JsonElement value, JsonElement branches, string field)
    {
        var matches = 0;
        var bestKnown = -1;
        (string Field, string Detail)? best = null;
        foreach (var branch in branches.EnumerateArray())
        {
            var failure = Validate(value, branch, field);
            if (failure is null) { matches++; continue; }
            var known = Known(value, branch);
            if (best is null || known > bestKnown || (known == bestKnown && Rank(failure.Value.Field, field) > Rank(best.Value.Field, field)))
            {
                best = failure;
                bestKnown = known;
            }
        }
        if (matches == 1) return null;
        if (matches > 1) return (field, "matches more than one shape");
        return best ?? (field, "matches no shape");
    }

    // How many of the value's members a branch declares: the closest shape explains the failure best.
    static int Known(JsonElement value, JsonElement branch) =>
        value.ValueKind == JsonValueKind.Object && branch.TryGetProperty("properties", out var properties)
            ? value.EnumerateObject().Count(member => properties.TryGetProperty(member.Name, out _))
            : 0;

    // A branch rejected on its "kind" discriminator is the least informative failure.
    static int Rank(string failureField, string field) => failureField == field + ".kind" ? -1 : failureField.Count(character => character is '.' or '[');
}
