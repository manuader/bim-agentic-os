#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using Bim.Core;

namespace Bim.Cli;

// The edits file shared by diff-edits (P3-07) and apply-edits (P3-08): a JSON array of ElementEdit
// {"Action":"create"|"update"|"delete","Id":guid,"Element":Element|null} (the record's default serialization). Unknown
// members and duplicate keys are refused in every object, including the nested Element and Provenance. Null entries and
// null element fields are left to the library (edit_invalid). Each verb maps a null result to its own *_edits_invalid code.
internal static class EditsInput
{
    // The IncrementalDiff refusals both verbs pass on unchanged (ArgumentException messages); invalid_ir is an
    // InvalidOperationException and handled by each verb.
    public static readonly string[] LibraryCodes =
    {
        IncrementalDiff.InvalidEdit, IncrementalDiff.UnknownId, IncrementalDiff.DuplicateId, IncrementalDiff.CreateExists, IncrementalDiff.BoundExceeded,
    };
    static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static ElementEdit[]? Parse(string text)
    {
        try
        {
            // System.Text.Json keeps the last of duplicate keys; the edits refuse them instead (bounded by the 8 MiB read
            // and JsonDocument's default depth limit of 64).
            using (var document = JsonDocument.Parse(text)) if (HasDuplicateKey(document.RootElement)) return null;
            return JsonSerializer.Deserialize<ElementEdit[]>(text, Options);
        }
        catch (JsonException) { return null; }
    }

    static bool HasDuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!names.Add(property.Name) || HasDuplicateKey(property.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) if (HasDuplicateKey(item)) return true;
                return false;
            default:
                return false;
        }
    }
}
