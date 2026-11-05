using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// element.move / element.copy executor decisions, readback checks and readback JSON, in millimetres (project
// coordinates). Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorTransform.cs
// reads the Revit facts (lengths converted with UnitUtils) and calls the Revit API.

public static class TransformLocationKinds
{
    public const string Point = "point";
    public const string Curve = "curve";
    public const string Box = "box";
}

// Where an element is: a location point A (with its rotation in radians when Revit reports one), a location curve from
// A to B, or, for elements with neither (floors, roofs and other sketch-based elements), the model bounding box from
// A (min) to B (max). Points are [x, y, z] in millimetres.
public sealed record TransformLocation(string Kind, double[] A, double[]? B, double? RotationRad = null);
// The Revit facts a target is refused on. HostedPlacement: a family instance of a hosted placement type; HostPresent:
// its host exists. Location: null when the element has no location point, curve or box (types, views, data storage).
public sealed record TransformTargetFacts(string Uid, bool Pinned, bool ViewSpecific, bool Grouped, bool HostedPlacement, bool HostPresent, TransformLocation? Location);
// An element considered when mapping copies to sources. Key: what a copy shares with its source (Revit class,
// category and type).
public sealed record TransformCandidate(string Uid, string Key, TransformLocation? Location);
public sealed record CopyPair(string SourceUid, string CopyUid);
// Pairs: one per source, in request order. AlsoCreated: new elements that are no source's copy (hosted doors and
// windows Revit copied with their walls). Mismatch: why a source has no copy, else null.
public sealed record CopyMapping(IReadOnlyList<CopyPair> Pairs, IReadOnlyList<string> AlsoCreated, string? Mismatch);

public static class CommandRulesTransform
{
    // Locations are compared point by point within 1 mm (Euclidean); a moved point keeps its rotation within 1e-6 rad.
    public const double ToleranceMm = 1.0;
    public const double RotationToleranceRad = 1e-6;

    // Refusal order: pinned, view-specific, group member, hosted without host, no location.
    public static void CheckTarget(TransformTargetFacts facts, string field)
    {
        if (facts.Pinned) throw new RevitCommandException(RevitTransformCodes.ElementPinned, field, facts.Uid + " is pinned; unpin it in Revit first");
        if (facts.ViewSpecific) throw new RevitCommandException(RevitTransformCodes.ElementViewSpecific, field, facts.Uid + " is view-specific (annotation or detail); only model elements are moved or copied");
        if (facts.Grouped) throw new RevitCommandException(RevitTransformCodes.ElementInGroup, field, facts.Uid + " is a group member; edit or ungroup the group in Revit first");
        if (facts.HostedPlacement && !facts.HostPresent) throw new RevitCommandException(RevitTransformCodes.HostMissing, field, facts.Uid + " is a hosted element whose host is missing");
        if (facts.Location is null) throw new RevitCommandException(RevitTransformCodes.LocationUnsupported, field, facts.Uid + " has no location point, location curve or bounding box to move and read back");
    }

    public static double[] Vector(TranslationVector vector) => new[] { vector.Dx.Millimetres, vector.Dy.Millimetres, vector.Dz.Millimetres };

    static double[] Add(double[] point, double[] vector) => new[] { point[0] + vector[0], point[1] + vector[1], point[2] + vector[2] };

    static double Distance(double[] a, double[] b)
    {
        double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // The location shifted by the vector (rotation kept).
    public static TransformLocation Shift(TransformLocation location, double[] vector) =>
        location with { A = Add(location.A, vector), B = location.B is null ? null : Add(location.B, vector) };

    static string Format(double[] point) =>
        "(" + string.Join(", ", point.Select(value => value.ToString("0.###", CultureInfo.InvariantCulture))) + ")";

    // Null when observed is the expected location within the tolerances, else what differs.
    public static string? Compare(TransformLocation expected, TransformLocation? observed, string what)
    {
        if (observed is null) return what + " no longer has a readable location";
        if (observed.Kind != expected.Kind) return what + " has a location " + observed.Kind + " where a location " + expected.Kind + " was read before";
        if (!(Distance(observed.A, expected.A) <= ToleranceMm))
            return what + " location " + expected.Kind + (expected.Kind == TransformLocationKinds.Point ? "" : expected.Kind == TransformLocationKinds.Curve ? " start" : " min") + " is at " + Format(observed.A) + " mm, expected " + Format(expected.A) + " within 1 mm";
        if (expected.B is not null && (observed.B is null || !(Distance(observed.B, expected.B) <= ToleranceMm)))
            return what + " location " + expected.Kind + (expected.Kind == TransformLocationKinds.Curve ? " end" : " max") + " is at " + (observed.B is null ? "nothing" : Format(observed.B)) + " mm, expected " + Format(expected.B) + " within 1 mm";
        if (expected.RotationRad is { } rotation && observed.RotationRad is { } turned && !(Math.Abs(turned - rotation) <= RotationToleranceRad))
            return what + " rotation changed from " + (rotation * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + " to " + (turned * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + " degrees";
        return null;
    }

    // Each source, in request order, takes the first unused copy with its key at its location plus the vector (within
    // 1 mm); a source left without one is a mismatch. Copies no source takes are AlsoCreated, in Revit's order.
    public static CopyMapping MapCopies(IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformCandidate> copies, double[] vector)
    {
        var used = new bool[copies.Count];
        var pairs = new List<CopyPair>(sources.Count);
        foreach (var source in sources)
        {
            if (source.Location is null) return new CopyMapping(pairs, Array.Empty<string>(), "source " + source.Uid + " has no location to map its copy by");
            var expected = Shift(source.Location, vector);
            var index = -1;
            for (var i = 0; i < copies.Count && index < 0; i++)
                if (!used[i] && string.Equals(copies[i].Key, source.Key, StringComparison.Ordinal) && Compare(expected, copies[i].Location, "copy") is null) index = i;
            if (index < 0) return new CopyMapping(pairs, Array.Empty<string>(), "no new element of the same kind sits at the location of " + source.Uid + " plus the vector " + Format(vector) + " mm (within 1 mm)");
            used[index] = true;
            pairs.Add(new CopyPair(source.Uid, copies[index].Uid));
        }
        return new CopyMapping(pairs, copies.Where((_, i) => !used[i]).Select(copy => copy.Uid).ToArray(), null);
    }

    // The reply's created list: the mapped copies in request order, then the extra copies.
    public static IReadOnlyList<string> Created(CopyMapping mapping) => mapping.Pairs.Select(pair => pair.CopyUid).Concat(mapping.AlsoCreated).ToArray();
}

public static class CommandReadbackTransform
{
    // Every target's location (read before and after, from the element) shifted by the vector.
    public static string? Moved(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, double[] vector)
    {
        for (var i = 0; i < uids.Count; i++)
            if (CommandRulesTransform.Compare(CommandRulesTransform.Shift(before[i], vector), after[i], "element " + uids[i]) is { } mismatch) return mismatch;
        return null;
    }

    // Sources unchanged, and every source mapped to a copy at its location plus the vector.
    public static string? Copied(IReadOnlyList<TransformCandidate> sources, IReadOnlyList<TransformLocation?> sourcesAfter, IReadOnlyList<TransformCandidate> copies, double[] vector)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i].Location is not { } before) return "source " + sources[i].Uid + " has no location";
            if (CommandRulesTransform.Compare(before, sourcesAfter[i], "source " + sources[i].Uid) is { } moved) return moved;
        }
        return CommandRulesTransform.MapCopies(sources, copies, vector).Mismatch;
    }

    public static string MoveJson(IReadOnlyList<string> uids, IReadOnlyList<TransformLocation> before, IReadOnlyList<TransformLocation?> after, double[] vector) => Write(writer =>
    {
        Triple(writer, "vector_mm", vector);
        writer.WriteStartArray("moved");
        for (var i = 0; i < uids.Count; i++)
        {
            writer.WriteStartObject();
            writer.WriteString("unique_id", uids[i]);
            Location(writer, "before", before[i]);
            Location(writer, "after", after[i]);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    public static string CopyJson(CopyMapping mapping, IReadOnlyList<TransformCandidate> copies, double[] vector) => Write(writer =>
    {
        Triple(writer, "vector_mm", vector);
        writer.WriteStartArray("copies");
        foreach (var pair in mapping.Pairs)
        {
            writer.WriteStartObject();
            writer.WriteString("source_unique_id", pair.SourceUid);
            writer.WriteString("copy_unique_id", pair.CopyUid);
            Location(writer, "location", copies.FirstOrDefault(copy => copy.Uid == pair.CopyUid)?.Location);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("also_created");
        foreach (var uid in mapping.AlsoCreated) writer.WriteStringValue(uid);
        writer.WriteEndArray();
    });

    static string Write(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Location(Utf8JsonWriter writer, string name, TransformLocation? location)
    {
        if (location is null) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name);
        writer.WriteString("kind", location.Kind);
        switch (location.Kind)
        {
            case TransformLocationKinds.Point:
                Triple(writer, "point", location.A);
                if (location.RotationRad is { } rotation) writer.WriteNumber("rotation_deg", rotation * 180 / Math.PI);
                break;
            case TransformLocationKinds.Curve:
                Triple(writer, "start", location.A);
                if (location.B is not null) Triple(writer, "end", location.B);
                break;
            default:
                Triple(writer, "min", location.A);
                if (location.B is not null) Triple(writer, "max", location.B);
                break;
        }
        writer.WriteEndObject();
    }

    static void Triple(Utf8JsonWriter writer, string name, double[] values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }
}
