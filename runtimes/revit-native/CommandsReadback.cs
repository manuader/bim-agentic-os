using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// C03 (ADR0009) expected geometry, readback checks and readback JSON, all in millimetres. Autodesk-free on purpose:
// tests/integration/RevitCommandGuard.Tests source-links this file. CommandsExecutor.cs converts Revit internal
// units with UnitUtils (UnitTypeId.Millimeters) before anything here sees a value.

public sealed record PlanPoint(double X, double Y);
// What the wall location curve must be after Wall.Create, from the typed arguments.
public sealed record ExpectedWall(bool IsArc, PlanPoint Start, PlanPoint End, double LengthMm, double? RadiusMm, PlanPoint? Center);
// What Revit reports back. Nullable values are parameters that could not be read.
public sealed record ObservedWall(string UniqueId, string LevelUid, string TypeUid, bool IsArc, PlanPoint Start, PlanPoint End, double LengthMm, double? RadiusMm, PlanPoint? Center, double? HeightMm, double? BaseOffsetMm, bool? Structural);
public sealed record ObservedOpening(string UniqueId, string? HostUid, string SymbolUid, PlanPoint? Point, double? SillMm);
public sealed record ObservedFloor(string UniqueId, string LevelUid, string TypeUid, double? AreaMm2);
public sealed record ObservedRoom(string UniqueId, string LevelUid, string? Name, string? Number, double AreaMm2);
public sealed record LevelRow(string Name, string UniqueId, double ElevationMm, double ProjectElevationMm);
public sealed record WallTypeRow(string Name, string UniqueId, string Kind, double? WidthMm);
public sealed record FloorTypeRow(string Name, string UniqueId, double? WidthMm);
public sealed record SymbolRow(string Family, string Type, string UniqueId, bool Active, bool WallHosted);

public static class CommandGeometry
{
    // Readback tolerance: 1 micrometre plus 1e-9 relative, well above double/feet conversion noise and far below
    // Revit's 1.2 mm short-curve tolerance.
    public const double ToleranceMm = 1e-3;

    public static bool Near(double a, double b) => Math.Abs(a - b) <= ToleranceMm + 1e-9 * Math.Max(Math.Abs(a), Math.Abs(b));
    public static bool Near(PlanPoint a, PlanPoint b) => Near(a.X, b.X) && Near(a.Y, b.Y);

    static PlanPoint P(Point2 point) => new(point.X.Millimetres, point.Y.Millimetres);

    public static ExpectedWall Wall(WallCurve curve) => curve switch
    {
        LineWallCurve line => new ExpectedWall(false, P(line.Start), P(line.End),
            RevitCommandGeometry.Distance(line.Start.X.Millimetres, line.Start.Y.Millimetres, line.End.X.Millimetres, line.End.Y.Millimetres), null, null),
        ArcThroughWallCurve arc => new ExpectedWall(true, P(arc.Start), P(arc.End), arc.Radius.Millimetres * arc.Sweep.Radians, arc.Radius.Millimetres, P(arc.Center)),
        ArcCenterWallCurve arc => CenterArc(arc),
        _ => throw new ArgumentOutOfRangeException(nameof(curve)),
    };

    // Counter-clockwise from StartAngle to EndAngle; Angle already holds radians (degrees * pi / 180).
    static ExpectedWall CenterArc(ArcCenterWallCurve arc)
    {
        double cx = arc.Center.X.Millimetres, cy = arc.Center.Y.Millimetres, r = arc.Radius.Millimetres;
        double a0 = arc.StartAngle.Radians, a1 = arc.EndAngle.Radians;
        return new ExpectedWall(true, new PlanPoint(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)), new PlanPoint(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), r * (a1 - a0), r, new PlanPoint(cx, cy));
    }

    // position_along_mm measured from the host curve's start becomes the normalized parameter for
    // Curve.Evaluate(parameter, normalized: true); for lines and arcs it is proportional to length.
    public static double OpeningParameter(double hostLengthMm, double positionMm, string field)
    {
        if (!double.IsFinite(hostLengthMm) || !(hostLengthMm > 0)) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "host wall location curve has no length");
        if (!double.IsFinite(positionMm) || positionMm < 0 || positionMm > hostLengthMm + ToleranceMm)
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "position must lie on the host wall: 0 to " + hostLengthMm.ToString("R", CultureInfo.InvariantCulture) + " mm");
        return Math.Clamp(positionMm / hostLengthMm, 0, 1);
    }

    // Positive enclosed area of the outline (implicit closing edge), relative to its first vertex for precision.
    public static double PolygonAreaMm2(IReadOnlyList<Point2> boundary)
    {
        if (boundary.Count < 3) return 0;
        double ox = boundary[0].X.Millimetres, oy = boundary[0].Y.Millimetres, twice = 0;
        for (var i = 0; i < boundary.Count; i++)
        {
            var next = boundary[(i + 1) % boundary.Count];
            double x0 = boundary[i].X.Millimetres - ox, y0 = boundary[i].Y.Millimetres - oy, x1 = next.X.Millimetres - ox, y1 = next.Y.Millimetres - oy;
            twice += x0 * y1 - x1 * y0;
        }
        return Math.Abs(twice) / 2;
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadback
{
    static bool Near(double a, double b) => CommandGeometry.Near(a, b);
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(PlanPoint point) => "(" + F(point.X) + ", " + F(point.Y) + ")";

    public static string? Wall(ExpectedWall expected, string levelUid, string typeUid, double heightMm, double baseOffsetMm, bool structural, ObservedWall observed)
    {
        if (observed.LevelUid != levelUid) return "wall base level is " + observed.LevelUid + ", expected " + levelUid;
        if (observed.TypeUid != typeUid) return "wall type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.IsArc != expected.IsArc) return "wall location curve is " + (observed.IsArc ? "an arc" : "a line") + ", expected " + (expected.IsArc ? "an arc" : "a line");
        var same = CommandGeometry.Near(observed.Start, expected.Start) && CommandGeometry.Near(observed.End, expected.End);
        var reversed = CommandGeometry.Near(observed.Start, expected.End) && CommandGeometry.Near(observed.End, expected.Start);
        if (!same && !reversed) return "wall endpoints are " + F(observed.Start) + "-" + F(observed.End) + ", expected " + F(expected.Start) + "-" + F(expected.End);
        if (!Near(observed.LengthMm, expected.LengthMm)) return "wall length is " + F(observed.LengthMm) + " mm, expected " + F(expected.LengthMm);
        if (expected.IsArc)
        {
            if (observed.RadiusMm is not { } radius || !Near(radius, expected.RadiusMm!.Value)) return "arc radius is " + (observed.RadiusMm is { } r ? F(r) : "unreadable") + ", expected " + F(expected.RadiusMm!.Value);
            if (observed.Center is not { } center || !CommandGeometry.Near(center, expected.Center!)) return "arc centre is " + (observed.Center is { } c ? F(c) : "unreadable") + ", expected " + F(expected.Center!);
        }
        if (observed.HeightMm is not { } height || !Near(height, heightMm)) return "wall unconnected height is " + (observed.HeightMm is { } h ? F(h) : "unreadable") + ", expected " + F(heightMm);
        if (observed.BaseOffsetMm is not { } offset || !Near(offset, baseOffsetMm)) return "wall base offset is " + (observed.BaseOffsetMm is { } o ? F(o) : "unreadable") + ", expected " + F(baseOffsetMm);
        if (observed.Structural is { } isStructural && isStructural != structural) return "wall structural flag is " + isStructural + ", expected " + structural;
        return null;
    }

    public static string? Opening(string hostUid, string symbolUid, PlanPoint expectedPoint, double? sillMm, ObservedOpening observed)
    {
        if (observed.HostUid != hostUid) return "opening host is " + (observed.HostUid ?? "none") + ", expected wall " + hostUid;
        if (observed.SymbolUid != symbolUid) return "opening family type is " + observed.SymbolUid + ", expected " + symbolUid;
        if (observed.Point is not { } point || !CommandGeometry.Near(point, expectedPoint)) return "opening location is " + (observed.Point is { } p ? F(p) : "unreadable") + ", expected " + F(expectedPoint);
        if (sillMm is { } sill && (observed.SillMm is not { } actual || !Near(actual, sill))) return "window sill height is " + (observed.SillMm is { } s ? F(s) : "unreadable") + " mm, expected " + F(sill);
        return null;
    }

    public static string? Floor(string levelUid, string typeUid, double expectedAreaMm2, ObservedFloor observed)
    {
        if (observed.LevelUid != levelUid) return "floor level is " + observed.LevelUid + ", expected " + levelUid;
        if (observed.TypeUid != typeUid) return "floor type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.AreaMm2 is not { } area || !(Math.Abs(area - expectedAreaMm2) <= 1 + 1e-9 * expectedAreaMm2))
            return "floor area is " + (observed.AreaMm2 is { } a ? F(a) : "unreadable") + " mm2, expected " + F(expectedAreaMm2);
        return null;
    }

    public static string? Room(string levelUid, string? name, string? number, ObservedRoom observed)
    {
        if (observed.LevelUid != levelUid) return "room level is " + observed.LevelUid + ", expected " + levelUid;
        if (!(observed.AreaMm2 > 0)) return "room is not in a properly enclosed region (area 0); place it inside bounding walls";
        if (name is not null && observed.Name != name) return "room name is '" + observed.Name + "', expected '" + name + "'";
        if (number is not null && observed.Number != number) return "room number is '" + observed.Number + "', expected '" + number + "'";
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

    static void Point(Utf8JsonWriter writer, string name, PlanPoint? point)
    {
        if (point is null) { writer.WriteNull(name); return; }
        writer.WriteStartArray(name);
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteEndArray();
    }

    static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    public static string LevelsJson(IEnumerable<LevelRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("levels");
        foreach (var row in rows.OrderBy(row => row.ElevationMm).ThenBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            Number(writer, "elevation_mm", row.ElevationMm);
            Number(writer, "project_elevation_mm", row.ProjectElevationMm);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string WallTypesJson(IEnumerable<WallTypeRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("wall_types");
        foreach (var row in rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteString("kind", row.Kind);
            Number(writer, "width_mm", row.WidthMm);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string FloorTypesJson(IEnumerable<FloorTypeRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("floor_types");
        foreach (var row in rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("unique_id", row.UniqueId);
            Number(writer, "width_mm", row.WidthMm);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    // "reference" is the "Family: Type" text opening.place accepts as symbol.
    public static string SymbolsJson(string category, IEnumerable<SymbolRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("category", category);
        writer.WriteStartArray("family_symbols");
        foreach (var row in rows.OrderBy(row => row.Family, StringComparer.Ordinal).ThenBy(row => row.Type, StringComparer.Ordinal).ThenBy(row => row.UniqueId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("family", row.Family);
            writer.WriteString("type", row.Type);
            writer.WriteString("reference", CommandReferences.Qualified(row.Family, row.Type));
            writer.WriteString("unique_id", row.UniqueId);
            writer.WriteBoolean("active", row.Active);
            writer.WriteBoolean("wall_hosted", row.WallHosted);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static string WallJson(ObservedWall wall) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("wall");
        writer.WriteString("unique_id", wall.UniqueId);
        writer.WriteString("level_unique_id", wall.LevelUid);
        writer.WriteString("type_unique_id", wall.TypeUid);
        writer.WriteStartObject("curve");
        writer.WriteString("kind", wall.IsArc ? "arc" : "line");
        Point(writer, "start", wall.Start);
        Point(writer, "end", wall.End);
        Number(writer, "length_mm", wall.LengthMm);
        if (wall.IsArc)
        {
            Number(writer, "radius_mm", wall.RadiusMm);
            Point(writer, "center", wall.Center);
        }
        writer.WriteEndObject();
        Number(writer, "height_mm", wall.HeightMm);
        Number(writer, "base_offset_mm", wall.BaseOffsetMm);
        if (wall.Structural is { } structural) writer.WriteBoolean("structural", structural);
        else writer.WriteNull("structural");
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string OpeningJson(string category, ObservedOpening opening) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("opening");
        writer.WriteString("unique_id", opening.UniqueId);
        writer.WriteString("category", category);
        Text(writer, "host_wall_unique_id", opening.HostUid);
        writer.WriteString("symbol_unique_id", opening.SymbolUid);
        Point(writer, "point", opening.Point);
        Number(writer, "sill_mm", opening.SillMm);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string FloorJson(ObservedFloor floor) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("floor");
        writer.WriteString("unique_id", floor.UniqueId);
        writer.WriteString("level_unique_id", floor.LevelUid);
        writer.WriteString("type_unique_id", floor.TypeUid);
        Number(writer, "area_mm2", floor.AreaMm2);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string RoomJson(ObservedRoom room) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("room");
        writer.WriteString("unique_id", room.UniqueId);
        writer.WriteString("level_unique_id", room.LevelUid);
        Text(writer, "name", room.Name);
        Text(writer, "number", room.Number);
        Number(writer, "area_mm2", room.AreaMm2);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
