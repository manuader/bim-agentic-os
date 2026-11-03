using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// P9-02 structure commands executor decisions, readback checks and readback JSON, in millimetres. Autodesk-free on
// purpose: tests/revit-commands source-links this file. CommandsExecutorStructure.cs reads the Revit facts (lengths
// converted with UnitUtils) and calls the Revit API. Revit enum values travel as their ToString() text
// (StructuralType "Column", "Beam", "Footing"; WallKind "Basic").

// Family null: a wall foundation type, which is not a family symbol.
public sealed record StructuralTypeRow(string? Family, string Type, string UniqueId, bool Active);
public sealed record GridRow(string Name, string UniqueId, bool Curved, PlanPoint Start, PlanPoint End);
public sealed record ObservedGrid(string UniqueId, string Name, bool Curved, PlanPoint Start, PlanPoint End);
// Nullable values could not be read. Point: the LocationPoint in plan; levels and offsets: the column's
// FAMILY_BASE_LEVEL_PARAM, FAMILY_TOP_LEVEL_PARAM, FAMILY_BASE_LEVEL_OFFSET_PARAM and FAMILY_TOP_LEVEL_OFFSET_PARAM.
public sealed record ObservedColumn(string UniqueId, string TypeUid, string StructuralType, bool Slanted, PlanPoint? Point, string? BaseLevelUid, string? TopLevelUid, double? BaseOffsetMm, double? TopOffsetMm);
// LevelUid: INSTANCE_REFERENCE_LEVEL_PARAM; Start/End: the LocationCurve end points in plan.
public sealed record ObservedBeam(string UniqueId, string TypeUid, string StructuralType, string? LevelUid, PlanPoint? Start, PlanPoint? End);
public sealed record ObservedFooting(string UniqueId, string TypeUid, string StructuralType, string? LevelUid, PlanPoint? Point);
// WallUid: WallFoundation.WallId, null when it names no wall.
public sealed record ObservedWallFoundation(string UniqueId, string TypeUid, string? WallUid);

public static class CommandRulesStructure
{
    public const string ColumnStructuralType = "Column";
    public const string BeamStructuralType = "Beam";
    public const string FootingStructuralType = "Footing";

    // query.structural_types category -> Revit BuiltInCategory name of its family symbols; null for wall_foundation,
    // whose types are WallFoundationType elements.
    public static string? BuiltInCategory(string category) => category switch
    {
        "column" => "OST_StructuralColumns",
        "framing" => "OST_StructuralFraming",
        "foundation" => "OST_StructuralFoundation",
        "wall_foundation" => null,
        _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.category", "unknown structural category " + category),
    };

    // Revit refuses a duplicate grid name; compared ignoring case (stricter than Revit) so a collision fails closed here.
    public static void CheckGridNameFree(string? name, IEnumerable<string> existing)
    {
        if (name is not null && existing.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a grid named '" + name + "' (ignoring case) already exists");
    }

    // After level resolution (two references may name one level). Elevations are project elevations in millimetres.
    public static double ColumnHeightMm(string baseUid, double baseElevationMm, double baseOffsetMm, string topUid, double topElevationMm, double topOffsetMm)
    {
        if (string.Equals(baseUid, topUid, StringComparison.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "base_level and top_level resolve to the same level");
        var height = topElevationMm + topOffsetMm - (baseElevationMm + baseOffsetMm);
        if (!(height >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "the column top (top level plus top offset) must be at least 1.2 mm above its base (base level plus base offset)");
        if (height > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.top_offset_mm", "column taller than 9144000 mm (Revit's 30000 ft limit)");
        return height;
    }

    // wallKind is the WallKind name of the host wall type; existingFoundations counts wall foundations already on it.
    public static void CheckWallFoundationHost(string wallKind, int existingFoundations)
    {
        if (wallKind != "Basic") throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.host_wall_uid", "a wall foundation needs a basic wall, not a " + wallKind + " wall");
        if (existingFoundations > 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.host_wall_uid", "the host wall already has a wall foundation");
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackStructure
{
    static bool Near(double a, double b) => CommandGeometry.Near(a, b);
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(PlanPoint? point) => point is null ? "unreadable" : "(" + F(point.X) + ", " + F(point.Y) + ")";
    static string F(double? value) => value is { } number ? F(number) : "unreadable";
    static string Uid(string? value) => value ?? "unreadable";
    static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static bool At(PlanPoint? observed, PlanPoint expected) => observed is not null && CommandGeometry.Near(observed, expected);

    public static string? Grid(PlanPoint start, PlanPoint end, string? name, ObservedGrid observed)
    {
        if (observed.Curved) return "the grid is curved, expected a straight line";
        if (!At(observed.Start, start) || !At(observed.End, end)) return "grid goes from " + F(observed.Start) + " to " + F(observed.End) + ", expected " + F(start) + " to " + F(end);
        if (name is not null && !string.Equals(observed.Name, name, StringComparison.Ordinal)) return "grid name is '" + observed.Name + "', expected '" + name + "'";
        return null;
    }

    public static string? Column(string typeUid, PlanPoint point, string baseLevelUid, string topLevelUid, double baseOffsetMm, double topOffsetMm, ObservedColumn observed)
    {
        if (!Same(observed.TypeUid, typeUid)) return "column type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.StructuralType != CommandRulesStructure.ColumnStructuralType) return "column structural type is " + observed.StructuralType + ", expected " + CommandRulesStructure.ColumnStructuralType;
        if (observed.Slanted) return "the column is slanted, expected vertical";
        if (!At(observed.Point, point)) return "column is at " + F(observed.Point) + ", expected " + F(point);
        if (!Same(observed.BaseLevelUid, baseLevelUid)) return "column base level is " + Uid(observed.BaseLevelUid) + ", expected " + baseLevelUid;
        if (!Same(observed.TopLevelUid, topLevelUid)) return "column top level is " + Uid(observed.TopLevelUid) + ", expected " + topLevelUid;
        if (observed.BaseOffsetMm is not { } baseOffset || !Near(baseOffset, baseOffsetMm)) return "column base offset is " + F(observed.BaseOffsetMm) + " mm, expected " + F(baseOffsetMm);
        if (observed.TopOffsetMm is not { } topOffset || !Near(topOffset, topOffsetMm)) return "column top offset is " + F(observed.TopOffsetMm) + " mm, expected " + F(topOffsetMm);
        return null;
    }

    public static string? Beam(string typeUid, string levelUid, PlanPoint start, PlanPoint end, ObservedBeam observed)
    {
        if (!Same(observed.TypeUid, typeUid)) return "beam type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.StructuralType != CommandRulesStructure.BeamStructuralType) return "beam structural type is " + observed.StructuralType + ", expected " + CommandRulesStructure.BeamStructuralType;
        if (!Same(observed.LevelUid, levelUid)) return "beam reference level is " + Uid(observed.LevelUid) + ", expected " + levelUid;
        if (!At(observed.Start, start) || !At(observed.End, end)) return "beam goes from " + F(observed.Start) + " to " + F(observed.End) + ", expected " + F(start) + " to " + F(end);
        return null;
    }

    public static string? Footing(string typeUid, string levelUid, PlanPoint point, ObservedFooting observed)
    {
        if (!Same(observed.TypeUid, typeUid)) return "foundation type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.StructuralType != CommandRulesStructure.FootingStructuralType) return "foundation structural type is " + observed.StructuralType + ", expected " + CommandRulesStructure.FootingStructuralType;
        if (!Same(observed.LevelUid, levelUid)) return "foundation level is " + Uid(observed.LevelUid) + ", expected " + levelUid;
        if (!At(observed.Point, point)) return "foundation is at " + F(observed.Point) + ", expected " + F(point);
        return null;
    }

    public static string? WallFoundation(string typeUid, string wallUid, ObservedWallFoundation observed)
    {
        if (!Same(observed.TypeUid, typeUid)) return "wall foundation type is " + observed.TypeUid + ", expected " + typeUid;
        if (!Same(observed.WallUid, wallUid)) return "wall foundation host wall is " + (observed.WallUid ?? "none") + ", expected " + wallUid;
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

    static void Point(Utf8JsonWriter writer, string name, PlanPoint? point)
    {
        if (point is null) { writer.WriteNull(name); return; }
        writer.WriteStartArray(name);
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteEndArray();
    }

    public static string GridsJson(IEnumerable<GridRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("grids");
        foreach (var row in rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteBoolean("curved", row.Curved);
            Point(writer, "start", row.Start);
            Point(writer, "end", row.End);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    // "reference" is the text the creators accept as type: "Family: Type" for family symbols, the name otherwise.
    public static string StructuralTypesJson(string category, IEnumerable<StructuralTypeRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("category", category);
        writer.WriteStartArray("structural_types");
        foreach (var row in rows.OrderBy(row => row.Family ?? "", StringComparer.Ordinal).ThenBy(row => row.Type, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            Text(writer, "family", row.Family);
            writer.WriteString("type", row.Type);
            writer.WriteString("reference", row.Family is { } family ? CommandReferences.Qualified(family, row.Type) : row.Type);
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteBoolean("active", row.Active);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    static string Wrapped(string name, Action<Utf8JsonWriter> members) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject(name);
        members(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string GridJson(ObservedGrid grid) => Wrapped("grid", writer =>
    {
        writer.WriteString("unique_id", grid.UniqueId);
        writer.WriteString("name", grid.Name);
        writer.WriteBoolean("curved", grid.Curved);
        Point(writer, "start", grid.Start);
        Point(writer, "end", grid.End);
    });

    public static string ColumnJson(ObservedColumn column) => Wrapped("structural_column", writer =>
    {
        writer.WriteString("unique_id", column.UniqueId);
        writer.WriteString("type_unique_id", column.TypeUid);
        writer.WriteString("structural_type", column.StructuralType);
        writer.WriteBoolean("slanted", column.Slanted);
        Point(writer, "point", column.Point);
        Text(writer, "base_level_unique_id", column.BaseLevelUid);
        Text(writer, "top_level_unique_id", column.TopLevelUid);
        Number(writer, "base_offset_mm", column.BaseOffsetMm);
        Number(writer, "top_offset_mm", column.TopOffsetMm);
    });

    public static string BeamJson(ObservedBeam beam) => Wrapped("structural_beam", writer =>
    {
        writer.WriteString("unique_id", beam.UniqueId);
        writer.WriteString("type_unique_id", beam.TypeUid);
        writer.WriteString("structural_type", beam.StructuralType);
        Text(writer, "level_unique_id", beam.LevelUid);
        Point(writer, "start", beam.Start);
        Point(writer, "end", beam.End);
    });

    public static string FootingJson(ObservedFooting footing) => Wrapped("foundation", writer =>
    {
        writer.WriteString("unique_id", footing.UniqueId);
        writer.WriteString("type_unique_id", footing.TypeUid);
        writer.WriteString("structural_type", footing.StructuralType);
        Text(writer, "level_unique_id", footing.LevelUid);
        Point(writer, "point", footing.Point);
    });

    public static string WallFoundationJson(ObservedWallFoundation foundation) => Wrapped("wall_foundation", writer =>
    {
        writer.WriteString("unique_id", foundation.UniqueId);
        writer.WriteString("type_unique_id", foundation.TypeUid);
        Text(writer, "host_wall_unique_id", foundation.WallUid);
    });
}
