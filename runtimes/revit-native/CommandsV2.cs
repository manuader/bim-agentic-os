using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// Catalogue v2 (P9-01) executor decisions, expected geometry, readback checks and readback JSON, in millimetres and
// degrees. Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorV2.cs reads the
// Revit facts (converted with UnitUtils) and calls the Revit API.

public sealed record RoofTypeRow(string Name, string UniqueId, double? WidthMm);
// One footprint edge as Revit reports it: whether it defines slope and, if so, its slope in degrees.
public sealed record ObservedRoofEdge(bool DefinesSlope, double? SlopeDeg);
public sealed record ObservedRoof(string UniqueId, string LevelUid, string TypeUid, double? BaseOffsetMm, IReadOnlyList<ObservedRoofEdge> Edges);
// Nullable values are parameters that could not be read.
public sealed record ObservedStairs(string UniqueId, string? BaseLevelUid, string? TopLevelUid, IReadOnlyList<double?> RunWidthsMm, int? Risers);
// Where the added curtain grid lines must be: vertical lines through these plan points (placed at VerticalZMm),
// horizontal lines at these project elevations (placed at the plan point HorizontalAt).
public sealed record ExpectedCurtainGrid(IReadOnlyList<PlanPoint> Vertical, double VerticalZMm, IReadOnlyList<double> HorizontalZMm, PlanPoint HorizontalAt);
// An added curtain grid line's full curve: plan end points and their elevations in millimetres.
public sealed record ObservedGridLine(string UniqueId, PlanPoint Start, double StartZMm, PlanPoint End, double EndZMm);
public sealed record ObservedFloorOpening(string UniqueId, string? HostUid, bool IsRectBoundary, IReadOnlyList<PlanPoint> Vertices);

public static class CommandRulesV2
{
    // Wall kinds are WallKind names ("Basic", "Curtain", "Stacked").
    public static void CheckCurtainWallType(string wallKind)
    {
        if (wallKind != "Curtain") throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.type", "not a curtain wall type (kind " + wallKind + "); pick a type of kind Curtain from revit_query_wall_types");
    }

    // After resolution: two references may still name one level (a name and its UniqueId), and the top must be above.
    public static void CheckStairLevels(string baseUid, double baseElevationMm, string topUid, double topElevationMm)
    {
        if (string.Equals(baseUid, topUid, StringComparison.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "base_level and top_level resolve to the same level");
        if (!(topElevationMm - baseElevationMm >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "the top level must be above the base level");
    }

    // Revit stores a roof edge slope as rise over run (FootPrintRoof.SlopeAngle), not as an angle.
    public static double SlopeRatio(Angle slope) => Math.Tan(slope.Radians);
    public static double SlopeDegrees(double ratio) => Math.Atan(ratio) * 180 / Math.PI;

    // CurtainGrid.AddGridLine(isUGridLine, ...): on a curtain wall U grid lines are taken to be horizontal and V grid
    // lines vertical. Unverified in Revit; CommandReadbackV2.CurtainGrid checks each added line's orientation, so a
    // swapped mapping rolls back instead of being reported as success.
    public static bool IsUGridLine(bool vertical) => !vertical;

    // The plan point at distanceMm along the requested curve from its requested start (independent of how Revit
    // parameterizes the created curve).
    public static PlanPoint PointAlong(WallCurve curve, double distanceMm)
    {
        switch (curve)
        {
            case LineWallCurve line:
            {
                double sx = line.Start.X.Millimetres, sy = line.Start.Y.Millimetres, dx = line.End.X.Millimetres - sx, dy = line.End.Y.Millimetres - sy;
                var length = Math.Sqrt(dx * dx + dy * dy);
                return new PlanPoint(sx + dx * distanceMm / length, sy + dy * distanceMm / length);
            }
            case ArcThroughWallCurve arc:
            {
                double cx = arc.Center.X.Millimetres, cy = arc.Center.Y.Millimetres, r = arc.Radius.Millimetres;
                var start = Math.Atan2(arc.Start.Y.Millimetres - cy, arc.Start.X.Millimetres - cx);
                var angle = start + (arc.CounterClockwise ? 1 : -1) * distanceMm / r;
                return new PlanPoint(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
            }
            case ArcCenterWallCurve arc:
            {
                double cx = arc.Center.X.Millimetres, cy = arc.Center.Y.Millimetres, r = arc.Radius.Millimetres;
                var angle = arc.StartAngle.Radians + distanceMm / r;
                return new PlanPoint(cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
            }
            default: throw new ArgumentOutOfRangeException(nameof(curve));
        }
    }

    // levelElevationMm is the level's project elevation; the wall base is that plus the base offset.
    public static ExpectedCurtainGrid CurtainGrid(CurtainWallCreateArgs args, double levelElevationMm)
    {
        var length = RevitCommandGeometryV2.CurveLengthMm(args.Curve);
        var baseZ = levelElevationMm + args.BaseOffset.Millimetres;
        var height = args.Height.Millimetres;
        var vertical = args.VerticalSpacing is { } along
            ? RevitCommandGeometryV2.GridPositions(length, along.Millimetres).Select(distance => PointAlong(args.Curve, distance)).ToArray()
            : Array.Empty<PlanPoint>();
        var horizontal = args.HorizontalSpacing is { } up
            ? RevitCommandGeometryV2.GridPositions(height, up.Millimetres).Select(rise => baseZ + rise).ToArray()
            : Array.Empty<double>();
        return new ExpectedCurtainGrid(vertical, baseZ + height / 2, horizontal, PointAlong(args.Curve, length / 2));
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackV2
{
    // Roof slopes are compared in degrees after the rise/run round trip.
    public const double SlopeToleranceDeg = 1e-6;

    static bool Near(double a, double b) => CommandGeometry.Near(a, b);
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string F(PlanPoint point) => "(" + F(point.X) + ", " + F(point.Y) + ")";
    static string Uid(string? value) => value ?? "unreadable";

    public static string? Roof(string levelUid, string typeUid, int edgeCount, double? slopeDeg, double baseOffsetMm, ObservedRoof observed)
    {
        if (observed.LevelUid != levelUid) return "roof level is " + observed.LevelUid + ", expected " + levelUid;
        if (observed.TypeUid != typeUid) return "roof type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.BaseOffsetMm is not { } offset || !Near(offset, baseOffsetMm)) return "roof base offset is " + (observed.BaseOffsetMm is { } o ? F(o) : "unreadable") + " mm, expected " + F(baseOffsetMm);
        if (observed.Edges.Count != edgeCount) return "roof footprint has " + observed.Edges.Count + " edges, expected " + edgeCount;
        for (var i = 0; i < observed.Edges.Count; i++)
        {
            var edge = observed.Edges[i];
            if (slopeDeg is not { } slope)
            {
                if (edge.DefinesSlope) return "roof footprint edge " + i + " defines slope; a flat roof was requested";
            }
            else if (!edge.DefinesSlope) return "roof footprint edge " + i + " does not define slope";
            else if (edge.SlopeDeg is not { } actual || !(Math.Abs(actual - slope) <= SlopeToleranceDeg)) return "roof footprint edge " + i + " slope is " + (edge.SlopeDeg is { } s ? F(s) : "unreadable") + " degrees, expected " + F(slope);
        }
        return null;
    }

    public static string? Stairs(string baseLevelUid, string topLevelUid, double widthMm, ObservedStairs observed)
    {
        if (observed.BaseLevelUid != baseLevelUid) return "stair base level is " + Uid(observed.BaseLevelUid) + ", expected " + baseLevelUid;
        if (observed.TopLevelUid != topLevelUid) return "stair top level is " + Uid(observed.TopLevelUid) + ", expected " + topLevelUid;
        if (observed.RunWidthsMm.Count != 1) return "stair has " + observed.RunWidthsMm.Count + " runs, expected 1";
        if (observed.RunWidthsMm[0] is not { } width || !Near(width, widthMm)) return "stair run width is " + (observed.RunWidthsMm[0] is { } w ? F(w) : "unreadable") + " mm, expected " + F(widthMm);
        if (observed.Risers is { } risers && risers < 1) return "stair has no risers";
        return null;
    }

    public static string? CurtainGrid(ExpectedCurtainGrid expected, IReadOnlyList<ObservedGridLine> vertical, IReadOnlyList<ObservedGridLine> horizontal)
    {
        if (vertical.Count != expected.Vertical.Count) return "curtain grid has " + vertical.Count + " added vertical lines, expected " + expected.Vertical.Count;
        for (var i = 0; i < vertical.Count; i++)
        {
            var line = vertical[i];
            if (!CommandGeometry.Near(line.Start, line.End) || !(Math.Abs(line.EndZMm - line.StartZMm) >= RevitCommandGeometry.MinSegmentMm)) return "added vertical grid line " + i + " is not vertical";
            if (!CommandGeometry.Near(line.Start, expected.Vertical[i])) return "added vertical grid line " + i + " is at " + F(line.Start) + ", expected " + F(expected.Vertical[i]);
        }
        if (horizontal.Count != expected.HorizontalZMm.Count) return "curtain grid has " + horizontal.Count + " added horizontal lines, expected " + expected.HorizontalZMm.Count;
        for (var i = 0; i < horizontal.Count; i++)
        {
            var line = horizontal[i];
            if (!Near(line.StartZMm, line.EndZMm) || !(RevitCommandGeometry.Distance(line.Start.X, line.Start.Y, line.End.X, line.End.Y) >= RevitCommandGeometry.MinSegmentMm)) return "added horizontal grid line " + i + " is not horizontal";
            if (!Near(line.StartZMm, expected.HorizontalZMm[i])) return "added horizontal grid line " + i + " is at elevation " + F(line.StartZMm) + " mm, expected " + F(expected.HorizontalZMm[i]);
        }
        return null;
    }

    public static string? FloorOpening(string hostUid, IReadOnlyList<Point2> profile, ObservedFloorOpening observed)
    {
        if (observed.HostUid != hostUid) return "opening host is " + (observed.HostUid ?? "none") + ", expected floor " + hostUid;
        if (observed.IsRectBoundary) return "opening has a rectangular boundary, expected the requested profile";
        if (observed.Vertices.Count != profile.Count) return "opening profile has " + observed.Vertices.Count + " vertices, expected " + profile.Count;
        var expectedArea = CommandGeometry.PolygonAreaMm2(profile);
        var area = AreaMm2(observed.Vertices);
        if (!(Math.Abs(area - expectedArea) <= 1 + 1e-9 * expectedArea)) return "opening area is " + F(area) + " mm2, expected " + F(expectedArea);
        foreach (var vertex in profile)
        {
            var point = new PlanPoint(vertex.X.Millimetres, vertex.Y.Millimetres);
            if (!observed.Vertices.Any(observedVertex => CommandGeometry.Near(observedVertex, point))) return "opening profile has no vertex at " + F(point);
        }
        return null;
    }

    // Positive enclosed area, relative to the first vertex for precision.
    public static double AreaMm2(IReadOnlyList<PlanPoint> vertices)
    {
        if (vertices.Count < 3) return 0;
        double ox = vertices[0].X, oy = vertices[0].Y, twice = 0;
        for (var i = 0; i < vertices.Count; i++)
        {
            var next = vertices[(i + 1) % vertices.Count];
            twice += (vertices[i].X - ox) * (next.Y - oy) - (next.X - ox) * (vertices[i].Y - oy);
        }
        return Math.Abs(twice) / 2;
    }

    // ---- Readback JSON (snake_case, millimetres and degrees, rows in a stable order) ----

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

    public static string RoofTypesJson(IEnumerable<RoofTypeRow> rows) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("roof_types");
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

    public static string RoofJson(ObservedRoof roof) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("roof");
        writer.WriteString("unique_id", roof.UniqueId);
        writer.WriteString("level_unique_id", roof.LevelUid);
        writer.WriteString("type_unique_id", roof.TypeUid);
        Number(writer, "base_offset_mm", roof.BaseOffsetMm);
        writer.WriteStartArray("edges");
        foreach (var edge in roof.Edges)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("defines_slope", edge.DefinesSlope);
            Number(writer, "slope_deg", edge.SlopeDeg);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string StairsJson(ObservedStairs stairs) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("stairs");
        writer.WriteString("unique_id", stairs.UniqueId);
        Text(writer, "base_level_unique_id", stairs.BaseLevelUid);
        Text(writer, "top_level_unique_id", stairs.TopLevelUid);
        writer.WriteStartArray("run_widths_mm");
        foreach (var width in stairs.RunWidthsMm)
        {
            if (width is { } value && double.IsFinite(value)) writer.WriteNumberValue(value);
            else writer.WriteNullValue();
        }
        writer.WriteEndArray();
        if (stairs.Risers is { } risers) writer.WriteNumber("risers", risers);
        else writer.WriteNull("risers");
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    // {"wall": <the wall.create readback wall object>, "curtain_grid": {totals on the wall, added line UniqueIds}}.
    public static string CurtainWallJson(ObservedWall wall, int verticalLines, int horizontalLines, IReadOnlyList<string> addedGridLineUids) => Json(writer =>
    {
        using var wallJson = JsonDocument.Parse(CommandReadback.WallJson(wall));
        writer.WriteStartObject();
        writer.WritePropertyName("wall");
        wallJson.RootElement.GetProperty("wall").WriteTo(writer);
        writer.WriteStartObject("curtain_grid");
        writer.WriteNumber("vertical_lines", verticalLines);
        writer.WriteNumber("horizontal_lines", horizontalLines);
        writer.WriteStartArray("added_grid_line_unique_ids");
        foreach (var uid in addedGridLineUids) writer.WriteStringValue(uid);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    public static string FloorOpeningJson(ObservedFloorOpening opening) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartObject("floor_opening");
        writer.WriteString("unique_id", opening.UniqueId);
        Text(writer, "host_floor_unique_id", opening.HostUid);
        writer.WriteBoolean("rect_boundary", opening.IsRectBoundary);
        writer.WriteStartArray("profile");
        foreach (var vertex in opening.Vertices)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(vertex.X);
            writer.WriteNumberValue(vertex.Y);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        Number(writer, "area_mm2", AreaMm2(opening.Vertices));
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
