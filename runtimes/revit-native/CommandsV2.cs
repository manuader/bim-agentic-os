using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// Catalogue v2 (P9-01) executor decisions, expected geometry, readback checks and readback JSON, in millimetres and
// degrees. Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorV2.cs reads the
// Revit facts (converted with UnitUtils) and calls the Revit API.

public sealed record RoofTypeRow(string Name, string UniqueId, double? WidthMm);
// One footprint edge as Revit reports it: whether it defines slope and, if so, its slope in degrees; Start and End are
// the sketch curve's plan end points (null when not read).
public sealed record ObservedRoofEdge(bool DefinesSlope, double? SlopeDeg, PlanPoint? Start = null, PlanPoint? End = null);
// Edges and Footprint come from the roof's own sketch (FootPrintRoof.GetProfiles), not from the creation call's
// mapping; SlopeParameterDeg is the roof's Slope parameter (ROOF_SLOPE), read separately from the edges.
public sealed record ObservedRoof(string UniqueId, string LevelUid, string TypeUid, double? BaseOffsetMm, IReadOnlyList<ObservedRoofEdge> Edges, double? SlopeParameterDeg, int Profiles, IReadOnlyList<PlanPoint> Footprint);
// Nullable values are parameters that could not be read. RunStart/RunEnd: the run's stairs path, bottom to top.
public sealed record ObservedStairs(string UniqueId, string? BaseLevelUid, string? TopLevelUid, IReadOnlyList<double?> RunWidthsMm, int? Risers, int? DesiredRisers, PlanPoint? RunStart, PlanPoint? RunEnd);
// Where the added curtain grid lines must be: vertical lines through these plan points (placed at VerticalZMm),
// horizontal lines at these project elevations (placed at the plan point HorizontalAt).
public sealed record ExpectedCurtainGrid(IReadOnlyList<PlanPoint> Vertical, double VerticalZMm, IReadOnlyList<double> HorizontalZMm, PlanPoint HorizontalAt);
// An added curtain grid line's full curve: plan end points and their elevations in millimetres.
public sealed record ObservedGridLine(string UniqueId, PlanPoint Start, double StartZMm, PlanPoint End, double EndZMm);
public sealed record ObservedFloorOpening(string UniqueId, string? HostUid, bool IsRectBoundary, IReadOnlyList<PlanPoint> Vertices);

public static class CommandRulesV2
{
    // The boundary edge (vertex i to vertex i + 1, the last edge closing back to vertex 0) whose end points are these,
    // in either direction, within the readback tolerance; null when none is.
    public static int? EdgeIndex(IReadOnlyList<Point2> boundary, PlanPoint start, PlanPoint end)
    {
        for (var i = 0; i < boundary.Count; i++)
        {
            PlanPoint a = new(boundary[i].X.Millimetres, boundary[i].Y.Millimetres), b = new(boundary[(i + 1) % boundary.Count].X.Millimetres, boundary[(i + 1) % boundary.Count].Y.Millimetres);
            if ((CommandGeometry.Near(start, a) && CommandGeometry.Near(end, b)) || (CommandGeometry.Near(start, b) && CommandGeometry.Near(end, a))) return i;
        }
        return null;
    }

    // NewFootPrintRoof's model curve mapping need not follow the input order: each curve is matched to a requested
    // edge by its end points. Every edge must be matched by exactly one curve, else transaction_failed.
    public static int[] MatchRoofEdges(IReadOnlyList<Point2> boundary, IReadOnlyList<(PlanPoint Start, PlanPoint End)> curves)
    {
        if (curves.Count != boundary.Count) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.boundary", "Revit mapped " + curves.Count + " footprint curves for " + boundary.Count + " requested edges");
        var indices = new int[curves.Count];
        var taken = new HashSet<int>();
        for (var k = 0; k < curves.Count; k++)
        {
            var (start, end) = curves[k];
            var index = EdgeIndex(boundary, start, end) ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.boundary", "footprint curve from (" + F(start) + ") to (" + F(end) + ") matches no requested edge");
            if (!taken.Add(index)) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.boundary", "two footprint curves match requested edge " + index);
            indices[k] = index;
        }
        return indices;
    }

    static string F(PlanPoint point) => point.X.ToString("R", CultureInfo.InvariantCulture) + ", " + point.Y.ToString("R", CultureInfo.InvariantCulture);

    // Wall kinds are WallKind names ("Basic", "Curtain", "Stacked").
    public static void CheckCurtainWallType(string wallKind)
    {
        if (wallKind != "Curtain") throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.type", "not a curtain wall type (kind " + wallKind + "); pick a type of kind Curtain from revit_query_wall_types");
    }

    // After resolution: two references may still name one level (a name and its UniqueId), and the top must be above.
    public static void CheckStairLevels(string baseUid, double baseElevationMm, string topUid, double topElevationMm)
    {
        if (string.Equals(baseUid, topUid, StringComparison.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "base_level and top_level resolve to the same level");
        if (!(topElevationMm - baseElevationMm >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "the top level must be above the base level");
    }

    // The created list: the primary element first, then every child UniqueId once (ordinal), in the order given.
    public static IReadOnlyList<string> Created(string primary, params IEnumerable<string>[] children)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { primary };
        var created = new List<string> { primary };
        foreach (var uid in children.SelectMany(group => group)) if (seen.Add(uid)) created.Add(uid);
        return created;
    }

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

    // slopedEdges: the requested boundary edges that define slope (null: every edge when slopeDeg is given). With a
    // list, each sketch edge is matched to its boundary edge by geometry, never by position in the sketch.
    public static string? Roof(string levelUid, string typeUid, IReadOnlyList<Point2> boundary, double? slopeDeg, double baseOffsetMm, ObservedRoof observed, IReadOnlyList<int>? slopedEdges = null)
    {
        if (observed.LevelUid != levelUid) return "roof level is " + observed.LevelUid + ", expected " + levelUid;
        if (observed.TypeUid != typeUid) return "roof type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.BaseOffsetMm is not { } offset || !Near(offset, baseOffsetMm)) return "roof base offset is " + (observed.BaseOffsetMm is { } o ? F(o) : "unreadable") + " mm, expected " + F(baseOffsetMm);
        if (observed.Profiles != 1) return "roof sketch has " + observed.Profiles + " loops, expected 1";
        if (Outline("roof sketch", boundary, observed.Footprint) is { } outline) return outline;
        if (observed.Edges.Count != boundary.Count) return "roof sketch reports slope for " + observed.Edges.Count + " edges, expected " + boundary.Count;
        var matched = new HashSet<int>();
        for (var i = 0; i < observed.Edges.Count; i++)
        {
            var edge = observed.Edges[i];
            var name = "roof footprint edge " + i;
            var slopes = slopeDeg is not null;
            if (slopedEdges is not null)
            {
                if (edge.Start is not { } start || edge.End is not { } end) return name + " has no readable end points to match a boundary edge";
                if (CommandRulesV2.EdgeIndex(boundary, start, end) is not { } index) return name + " lies on no requested boundary edge";
                if (!matched.Add(index)) return name + " lies on boundary edge " + index + ", which another sketch edge already covers";
                name += " (boundary edge " + index + ")";
                slopes = slopeDeg is not null && slopedEdges.Contains(index);
            }
            if (slopeDeg is not { } slope)
            {
                if (edge.DefinesSlope) return name + " defines slope; a flat roof was requested";
            }
            else if (!slopes)
            {
                if (edge.DefinesSlope) return name + " defines slope; it is not in sloped_edges";
            }
            else if (!edge.DefinesSlope) return name + " does not define slope";
            else if (edge.SlopeDeg is not { } actual || !(Math.Abs(actual - slope) <= SlopeToleranceDeg)) return name + " slope is " + (edge.SlopeDeg is { } s ? F(s) : "unreadable") + " degrees, expected " + F(slope);
        }
        // The roof-wide Slope parameter is compared only when Revit fills it (it may be empty when only some edges slope);
        // the per-edge slopes above are the authoritative check.
        if (slopeDeg is { } requested && observed.SlopeParameterDeg is { } parameter && !(Math.Abs(parameter - requested) <= SlopeToleranceDeg))
            return "roof Slope parameter is " + F(parameter) + " degrees, expected " + F(requested);
        return null;
    }

    // Same vertex count, same area (1 mm2 plus 1e-9 relative) and every requested vertex present (any start, either
    // orientation). Null when the observed outline is the requested one.
    static string? Outline(string what, IReadOnlyList<Point2> requested, IReadOnlyList<PlanPoint> observed)
    {
        if (observed.Count != requested.Count) return what + " has " + observed.Count + " edges, expected " + requested.Count;
        var expectedArea = CommandGeometry.PolygonAreaMm2(requested);
        var area = AreaMm2(observed);
        if (!(Math.Abs(area - expectedArea) <= 1 + 1e-9 * expectedArea)) return what + " area is " + F(area) + " mm2, expected " + F(expectedArea);
        foreach (var vertex in requested)
        {
            var point = new PlanPoint(vertex.X.Millimetres, vertex.Y.Millimetres);
            if (!observed.Any(observedVertex => CommandGeometry.Near(observedVertex, point))) return what + " has no vertex at " + F(point);
        }
        return null;
    }

    public static string? Stairs(string baseLevelUid, string topLevelUid, double widthMm, PlanPoint runStart, PlanPoint runEnd, ObservedStairs observed)
    {
        if (observed.BaseLevelUid != baseLevelUid) return "stair base level is " + Uid(observed.BaseLevelUid) + ", expected " + baseLevelUid;
        if (observed.TopLevelUid != topLevelUid) return "stair top level is " + Uid(observed.TopLevelUid) + ", expected " + topLevelUid;
        if (observed.RunWidthsMm.Count != 1) return "stair has " + observed.RunWidthsMm.Count + " runs, expected 1";
        if (observed.RunWidthsMm[0] is not { } width || !Near(width, widthMm)) return "stair run width is " + (observed.RunWidthsMm[0] is { } w ? F(w) : "unreadable") + " mm, expected " + F(widthMm);
        if (observed.Risers is not { } risers || observed.DesiredRisers is not { } desired) return "stair riser count is unreadable (actual " + (observed.Risers?.ToString(CultureInfo.InvariantCulture) ?? "unreadable") + ", desired " + (observed.DesiredRisers?.ToString(CultureInfo.InvariantCulture) ?? "unreadable") + ")";
        if (risers < 1) return "stair has no risers";
        // Revit keeps a stair whose run is too short: it then has fewer actual than desired risers.
        if (risers != desired) return "stair has " + risers + " of " + desired + " risers: it does not reach the top level";
        if (observed.RunStart is not { } start || observed.RunEnd is not { } end) return "stair run path is unreadable";
        if (!CommandGeometry.Near(start, runStart) || !CommandGeometry.Near(end, runEnd)) return "stair run goes from " + F(start) + " to " + F(end) + ", expected " + F(runStart) + " to " + F(runEnd);
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
        return Outline("opening profile", profile, observed.Vertices);
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

    // With the requested boundary, each sketch edge names the boundary edge it lies on (boundary_edge, null when none)
    // and sloped_edges lists, sorted, the boundary edges whose sketch edge defines slope: read from the roof, not the
    // request.
    public static string RoofJson(ObservedRoof roof, IReadOnlyList<Point2>? boundary = null) => Json(writer =>
    {
        int? IndexOf(ObservedRoofEdge edge) => boundary is not null && edge.Start is { } start && edge.End is { } end ? CommandRulesV2.EdgeIndex(boundary, start, end) : null;
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
            if (edge.Start is not null || edge.End is not null)
            {
                writer.WritePropertyName("start");
                Point(writer, edge.Start);
                writer.WritePropertyName("end");
                Point(writer, edge.End);
            }
            if (boundary is not null)
            {
                if (IndexOf(edge) is { } index) writer.WriteNumber("boundary_edge", index);
                else writer.WriteNull("boundary_edge");
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (boundary is not null)
        {
            writer.WriteStartArray("sloped_edges");
            foreach (var index in roof.Edges.Where(edge => edge.DefinesSlope).Select(IndexOf).OfType<int>().Distinct().OrderBy(index => index)) writer.WriteNumberValue(index);
            writer.WriteEndArray();
        }
        Number(writer, "slope_parameter_deg", roof.SlopeParameterDeg);
        writer.WriteNumber("loops", roof.Profiles);
        Points(writer, "footprint", roof.Footprint);
        Number(writer, "area_mm2", AreaMm2(roof.Footprint));
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    static void Points(Utf8JsonWriter writer, string name, IReadOnlyList<PlanPoint> points)
    {
        writer.WriteStartArray(name);
        foreach (var point in points) Point(writer, point);
        writer.WriteEndArray();
    }

    static void Point(Utf8JsonWriter writer, PlanPoint? point)
    {
        if (point is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        writer.WriteNumberValue(point.X);
        writer.WriteNumberValue(point.Y);
        writer.WriteEndArray();
    }

    // railingUids: the railings Revit created with the stair (Stairs.GetAssociatedRailings); they are also in created.
    public static string StairsJson(ObservedStairs stairs, IReadOnlyList<string> railingUids) => Json(writer =>
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
        if (stairs.DesiredRisers is { } desired) writer.WriteNumber("desired_risers", desired);
        else writer.WriteNull("desired_risers");
        writer.WriteStartObject("run");
        writer.WritePropertyName("start");
        Point(writer, stairs.RunStart);
        writer.WritePropertyName("end");
        Point(writer, stairs.RunEnd);
        writer.WriteEndObject();
        writer.WriteStartArray("railing_unique_ids");
        foreach (var uid in railingUids) writer.WriteStringValue(uid);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    // {"wall": <the wall.create readback wall object>, "curtain_grid": {totals on the wall, added line UniqueIds}}.
    // panels and mullions: what Revit made on the wall's grid (CurtainGrid.GetPanelIds / GetMullionIds); their
    // UniqueIds are reported in created, after the wall and the added grid lines.
    public static string CurtainWallJson(ObservedWall wall, int verticalLines, int horizontalLines, IReadOnlyList<string> addedGridLineUids, int panels, int mullions) => Json(writer =>
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
        writer.WriteNumber("panels", panels);
        writer.WriteNumber("mullions", mullions);
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
        Points(writer, "profile", opening.Vertices);
        Number(writer, "area_mm2", AreaMm2(opening.Vertices));
        writer.WriteEndObject();
        writer.WriteEndObject();
    });
}
