using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P2-03 diff of a new query.snapshot (MirrorSnapshot) against the previous one of the same document. Pure and
// deterministic. Every observed element is keyed by its Revit UniqueId (ignoring case), mapped or unmapped, so an
// element that becomes unrepresentable (a straight wall made curved) is a change, not a removal plus an addition.
// Unmapped entries without a UniqueId (unique_id_missing) cannot be matched and are left out; for a UniqueId listed more
// than once (duplicate_unique_id) the first entry in snapshot order is compared. Diagnostic reply only (diff_version 1).

public sealed record MirrorDiffElement(string UniqueId, string Category);

// One changed field. Before / After are the invariant text of the value ("R" numbers in metres for geometry, "true" /
// "false" for mapped, comma-joined ids for dependencies), or null when the field does not apply on that side.
public sealed record MirrorFieldChange(string Field, string? Before, string? After);

public sealed record MirrorElementChange(string UniqueId, string Category, IReadOnlyList<MirrorFieldChange> Fields);

public sealed record MirrorSnapshotDiff(string DocumentUid, IReadOnlyList<MirrorDiffElement> Added, IReadOnlyList<MirrorDiffElement> Removed, IReadOnlyList<MirrorElementChange> Changed)
{
    public const int Version = 1;
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;

    // {"diff_version":1,"document_unique_id","added":[{"unique_id","category"}],"removed":[..],
    //  "changed":[{"unique_id","category","fields":[{"field","before","after"}]}]}
    public string ToJson() => MirrorJson.Write(Write);

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("diff_version", Version);
        writer.WriteString("document_unique_id", DocumentUid);
        foreach (var (name, list) in new[] { ("added", Added), ("removed", Removed) })
        {
            writer.WriteStartArray(name);
            foreach (var item in list)
            {
                writer.WriteStartObject();
                writer.WriteString("unique_id", item.UniqueId);
                writer.WriteString("category", item.Category);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteStartArray("changed");
        foreach (var change in Changed)
        {
            writer.WriteStartObject();
            writer.WriteString("unique_id", change.UniqueId);
            writer.WriteString("category", change.Category);
            writer.WriteStartArray("fields");
            foreach (var field in change.Fields)
            {
                writer.WriteStartObject();
                writer.WriteString("field", field.Field);
                if (field.Before is null) writer.WriteNull("before"); else writer.WriteString("before", field.Before);
                if (field.After is null) writer.WriteNull("after"); else writer.WriteString("after", field.After);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

public static class MirrorSnapshotDiffer
{
    // Geometry values closer than the mapper's tolerance (1e-3 mm, in metres) are equal.
    public const double ToleranceMetres = MirrorSnapshotMapper.ToleranceMm / 1000;

    // Named geometry fields per BIM-IR kind, in GeometryMetres order (MirrorMapper.cs layouts).
    static readonly IReadOnlyDictionary<string, string[]> FieldNames = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["level"] = new[] { "elevation" },
        ["wall"] = new[] { "ax", "ay", "bx", "by", "height", "thickness" },
        ["door"] = new[] { "ax", "ay", "bx", "by", "sill", "height" },
        ["window"] = new[] { "ax", "ay", "bx", "by", "sill", "height" },
        ["space"] = new[] { "x", "y", "width", "depth" },
    };

    sealed record State(string UniqueId, string Category, Element? Element, MirrorUnmapped? Unmapped);

    public static MirrorSnapshotDiff Compare(MirrorSnapshot? previous, MirrorSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is not null && !string.Equals(previous.DocumentUid, current.DocumentUid, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("diff_document_mismatch");
        var before = previous is null ? new Dictionary<string, State>() : States(previous);
        var after = States(current);
        static string Key(KeyValuePair<string, State> pair) => pair.Key;
        var added = after.Where(pair => !before.ContainsKey(pair.Key)).OrderBy(Key, StringComparer.Ordinal).Select(pair => new MirrorDiffElement(pair.Value.UniqueId, pair.Value.Category)).ToArray();
        var removed = before.Where(pair => !after.ContainsKey(pair.Key)).OrderBy(Key, StringComparer.Ordinal).Select(pair => new MirrorDiffElement(pair.Value.UniqueId, pair.Value.Category)).ToArray();
        var changed = new List<MirrorElementChange>();
        foreach (var pair in after.Where(pair => before.ContainsKey(pair.Key)).OrderBy(Key, StringComparer.Ordinal))
        {
            var fields = Fields(before[pair.Key], pair.Value);
            if (fields.Count > 0) changed.Add(new MirrorElementChange(pair.Value.UniqueId, pair.Value.Category, fields));
        }
        return new MirrorSnapshotDiff(current.DocumentUid, added, removed, changed);
    }

    static Dictionary<string, State> States(MirrorSnapshot snapshot)
    {
        var states = new Dictionary<string, State>(StringComparer.Ordinal);
        foreach (var element in snapshot.Elements)
        {
            var uid = snapshot.Sources[element.Id];
            states.TryAdd(uid.ToLowerInvariant(), new State(uid, snapshot.Categories[element.Id], element, null));
        }
        foreach (var entry in snapshot.Unmapped)
        {
            if (string.IsNullOrWhiteSpace(entry.UniqueId)) continue;
            states.TryAdd(entry.UniqueId.ToLowerInvariant(), new State(entry.UniqueId, entry.Category, null, entry));
        }
        return states;
    }

    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string? Geometry(Element? element) => element is null ? null : string.Join(",", element.GeometryMetres.Select(Number));
    static string? Dependencies(Element? element) => element is null ? null : string.Join(",", element.Dependencies.Select(id => id.ToString("D")));

    static IReadOnlyList<MirrorFieldChange> Fields(State before, State after)
    {
        var fields = new List<MirrorFieldChange>();
        void Compare(string field, string? a, string? b) { if (!string.Equals(a, b, StringComparison.Ordinal)) fields.Add(new(field, a, b)); }
        Compare("category", before.Category, after.Category);
        Compare("mapped", before.Element is null ? "false" : "true", after.Element is null ? "false" : "true");
        Compare("kind", before.Element?.Kind, after.Element?.Kind);
        if (before.Element is { } a && after.Element is { } b && a.Kind == b.Kind && a.GeometryMetres.Length == b.GeometryMetres.Length)
        {
            var names = FieldNames.TryGetValue(a.Kind, out var known) && known.Length == a.GeometryMetres.Length ? known : null;
            for (var i = 0; i < a.GeometryMetres.Length; i++)
            {
                double x = a.GeometryMetres[i], y = b.GeometryMetres[i];
                if (x == y || Math.Abs(x - y) <= ToleranceMetres) continue;
                fields.Add(new(names?[i] ?? "geometry[" + i + "]", Number(x), Number(y)));
            }
        }
        else
        {
            Compare("geometry", Geometry(before.Element), Geometry(after.Element));
        }
        Compare("dependencies", Dependencies(before.Element), Dependencies(after.Element));
        Compare("reason", before.Unmapped?.Reason, after.Unmapped?.Reason);
        Compare("detail", before.Unmapped?.Detail, after.Unmapped?.Detail);
        return fields;
    }
}
