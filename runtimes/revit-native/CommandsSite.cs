using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// Site commands executor decisions, readback checks and readback JSON, in millimetres. Autodesk-free on purpose:
// tests/revit-commands source-links this file. CommandsExecutorSite.cs reads the Revit facts (lengths converted with
// UnitUtils) and calls the Revit API.

public sealed record ToposolidTypeRow(string Name, string UniqueId, double? ThicknessMm);
// An axis-aligned box in millimetres, in the model frame (z in the frame of a level's project elevation).
public sealed record BoxMm(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);
// Nullable values could not be read. HostUid: Toposolid.HostTopoId, null when the toposolid is not a subdivision;
// Box: Element.get_BoundingBox(null); Comment: ALL_MODEL_INSTANCE_COMMENTS.
public sealed record ObservedToposolid(string UniqueId, string? LevelUid, string TypeUid, string? HostUid, BoxMm? Box, string? Comment);
// ListedByHost: the host's GetSubDivisionIds() contains the subdivision; MaterialUid: the subdivision's material
// parameter; SubdivideHeightMm: ParameterTypeId.ToposolidSubdivideHeight.
public sealed record ObservedSubdivision(string UniqueId, string? HostUid, bool ListedByHost, string? MaterialUid, double? SubdivideHeightMm, BoxMm? Box, string? Comment);

public static class CommandRulesSite
{
    // Bounding boxes are compared with a coarser tolerance than curves: Revit computes them from tessellated geometry.
    public const double BoxToleranceMm = 1.0;

    public static BoxMm Extents(IEnumerable<(double X, double Y, double Z)> points)
    {
        var all = points.ToArray();
        if (all.Length == 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.points", "no points");
        return new BoxMm(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z), all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z));
    }

    // A subdivision is hosted by a toposolid that is not itself a subdivision.
    public static void CheckSubdivisionHost(bool isSubdivision)
    {
        if (isSubdivision) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.host_uid", "the toposolid is itself a subdivision; pass the UniqueId of its host toposolid");
    }

    // The subdivision material is set through the one writable instance parameter whose data type is a material
    // reference (no locale-dependent parameter name); none or several fail closed.
    public static int MaterialParameterIndex(int writableMaterialParameters, string field)
    {
        if (writableMaterialParameters == 1) return 0;
        throw new RevitCommandException(RevitCommandCodes.TransactionFailed, field,
            writableMaterialParameters == 0 ? "the subdivision has no writable material parameter" : "the subdivision has " + writableMaterialParameters + " writable material parameters; which one is the surface material is ambiguous");
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackSite
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(double? value) => value is { } number ? F(number) : "unreadable";
    static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static bool Close(double a, double b) => Math.Abs(a - b) <= CommandRulesSite.BoxToleranceMm;

    // requested: the extents of the requested points. The plan extents of the toposolid are their convex hull's, the
    // top is the highest point and the bottom (the top face less the type thickness) is not above the lowest point.
    public static string? Toposolid(string levelUid, string typeUid, BoxMm requested, string? comment, ObservedToposolid observed)
    {
        if (!Same(observed.TypeUid, typeUid)) return "toposolid type is " + observed.TypeUid + ", expected " + typeUid;
        if (!Same(observed.LevelUid, levelUid)) return "toposolid level is " + (observed.LevelUid ?? "unreadable") + ", expected " + levelUid;
        if (observed.HostUid is not null) return "the new toposolid reports host " + observed.HostUid + " (a subdivision), expected a standalone toposolid";
        if (observed.Box is not { } box) return "the toposolid has no bounding box";
        if (!Close(box.MinX, requested.MinX) || !Close(box.MinY, requested.MinY) || !Close(box.MaxX, requested.MaxX) || !Close(box.MaxY, requested.MaxY))
            return "toposolid plan extents are (" + F(box.MinX) + ", " + F(box.MinY) + ") to (" + F(box.MaxX) + ", " + F(box.MaxY) + "), expected the points' (" + F(requested.MinX) + ", " + F(requested.MinY) + ") to (" + F(requested.MaxX) + ", " + F(requested.MaxY) + ")";
        if (!Close(box.MaxZ, requested.MaxZ)) return "toposolid top is at " + F(box.MaxZ) + " mm, expected the highest point's " + F(requested.MaxZ) + " mm (z is a project elevation)";
        if (!(box.MinZ <= requested.MinZ + CommandRulesSite.BoxToleranceMm) || !(box.MinZ < box.MaxZ)) return "toposolid bottom is at " + F(box.MinZ) + " mm, expected below the lowest point's " + F(requested.MinZ) + " mm";
        if (comment is not null && !string.Equals(observed.Comment, comment, StringComparison.Ordinal)) return "toposolid Comments is " + (observed.Comment is null ? "unreadable" : "'" + observed.Comment + "'") + ", expected '" + comment + "'";
        return null;
    }

    public static string? Subdivision(string hostUid, string? materialUid, double? subdivideHeightMm, string? comment, ObservedSubdivision observed)
    {
        if (!Same(observed.HostUid, hostUid)) return "subdivision host is " + (observed.HostUid ?? "none") + ", expected " + hostUid;
        if (!observed.ListedByHost) return "the host toposolid does not list the subdivision in GetSubDivisionIds";
        if (observed.Box is null) return "the subdivision has no bounding box";
        if (materialUid is not null && !Same(observed.MaterialUid, materialUid)) return "subdivision material is " + (observed.MaterialUid ?? "unreadable") + ", expected " + materialUid;
        if (subdivideHeightMm is { } height && (observed.SubdivideHeightMm is not { } actual || !CommandGeometry.Near(actual, height))) return "subdivision sub-divide height is " + F(observed.SubdivideHeightMm) + " mm, expected " + F(height);
        if (comment is not null && !string.Equals(observed.Comment, comment, StringComparison.Ordinal)) return "subdivision Comments is " + (observed.Comment is null ? "unreadable" : "'" + observed.Comment + "'") + ", expected '" + comment + "'";
        return null;
    }

    // ---- Readback JSON (snake_case, millimetres, rows in a stable order) ----

    static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number)) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    static void Box(Utf8JsonWriter writer, BoxMm? box)
    {
        if (box is null) { writer.WriteNull("bounding_box_mm"); return; }
        writer.WriteStartObject("bounding_box_mm");
        writer.WriteStartArray("min");
        writer.WriteNumberValue(box.MinX);
        writer.WriteNumberValue(box.MinY);
        writer.WriteNumberValue(box.MinZ);
        writer.WriteEndArray();
        writer.WriteStartArray("max");
        writer.WriteNumberValue(box.MaxX);
        writer.WriteNumberValue(box.MaxY);
        writer.WriteNumberValue(box.MaxZ);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static string ToposolidTypesJson(IEnumerable<ToposolidTypeRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("toposolid_types");
        foreach (var row in rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            Number(writer, "thickness_mm", row.ThicknessMm);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string ToposolidJson(ObservedToposolid toposolid, int pointCount) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("toposolid");
        writer.WriteString("unique_id", toposolid.UniqueId);
        Text(writer, "level_unique_id", toposolid.LevelUid);
        writer.WriteString("type_unique_id", toposolid.TypeUid);
        Text(writer, "comment", toposolid.Comment);
        writer.WriteNumber("point_count", pointCount);
        Box(writer, toposolid.Box);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string SubdivisionJson(ObservedSubdivision subdivision) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("toposolid_subdivision");
        writer.WriteString("unique_id", subdivision.UniqueId);
        Text(writer, "host_unique_id", subdivision.HostUid);
        writer.WriteBoolean("listed_by_host", subdivision.ListedByHost);
        Text(writer, "material_unique_id", subdivision.MaterialUid);
        Number(writer, "subdivide_height_mm", subdivision.SubdivideHeightMm);
        Text(writer, "comment", subdivision.Comment);
        Box(writer, subdivision.Box);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
