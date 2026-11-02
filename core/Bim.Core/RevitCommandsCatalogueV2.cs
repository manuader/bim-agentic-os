using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// Catalogue v2 (P9-01): roofs, stairs, curtain walls and floor openings. Same envelope (protocol_version 1), schema
// subset, unit rules, copies-only guard and receipts as v1; v1 commands are unchanged. Vendor-free like every
// RevitCommands*.cs file; the add-in executor consumes these types.

// Footprint roof: a closed outline on a level. Slope null is a flat roof; otherwise every edge defines that slope.
public sealed record RoofFootprintCreateArgs(string Level, string Type, IReadOnlyList<Point2> Boundary, Angle? Slope, Length BaseOffset) : RevitCommandArgs;
// One straight run from RunStart (bottom) to RunEnd (top) between two distinct levels, with the default stairs type.
public sealed record StairCreateArgs(string BaseLevel, string TopLevel, Point2 RunStart, Point2 RunEnd, Length Width) : RevitCommandArgs;
// A wall of a Curtain kind type; optional extra grid lines on this wall only (VerticalSpacing along the wall from its
// start, HorizontalSpacing up from its base). The wall type is never edited.
public sealed record CurtainWallCreateArgs(string Level, string Type, Length BaseOffset, Length Height, WallCurve Curve, Length? VerticalSpacing, Length? HorizontalSpacing) : RevitCommandArgs;
// An opening cut through an existing floor along a closed, non-self-intersecting profile.
public sealed record FloorOpeningCreateArgs(string HostFloorUid, IReadOnlyList<Point2> Profile) : RevitCommandArgs;

// Geometry rules that are new in v2, in millimetres.
public static class RevitCommandGeometryV2
{
    // Curtain grid lines added per direction by one command.
    public const int MaxCurtainGridLines = 500;
    // Steeper roof slopes are refused: near 90 degrees the ridge height (rise over run) explodes.
    public const double MaxRoofSlopeDeg = 85;

    // Smallest width of the outline: over its convex hull, the least of each hull edge's farthest vertex distance.
    // Half of it is the longest run any sloped edge can have before slopes meet, so tan(slope) times that half
    // bounds the roof rise.
    public static double MinimumWidthMm(IReadOnlyList<Point2> outline)
    {
        if (outline.Count < 3) return 0;
        double ox = outline[0].X.Millimetres, oy = outline[0].Y.Millimetres;
        var points = outline.Select(point => (X: point.X.Millimetres - ox, Y: point.Y.Millimetres - oy)).Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToList();
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double X, double Y)>();
        foreach (var pass in new[] { points, Enumerable.Reverse(points).ToList() })
        {
            var start = hull.Count;
            foreach (var point in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        if (hull.Count < 3) return 0;
        var width = double.PositiveInfinity;
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            var length = RevitCommandGeometry.Distance(a.X, a.Y, b.X, b.Y);
            if (!(length > 0)) continue;
            width = Math.Min(width, hull.Max(point => Math.Abs(Cross(a, b, point)) / length));
        }
        return double.IsFinite(width) ? width : 0;
    }

    public static double CurveLengthMm(WallCurve curve) => curve switch
    {
        LineWallCurve line => RevitCommandGeometry.Distance(line.Start.X.Millimetres, line.Start.Y.Millimetres, line.End.X.Millimetres, line.End.Y.Millimetres),
        ArcThroughWallCurve arc => arc.Radius.Millimetres * arc.Sweep.Radians,
        ArcCenterWallCurve arc => arc.Radius.Millimetres * arc.Sweep.Radians,
        _ => throw new ArgumentOutOfRangeException(nameof(curve)),
    };

    // Grid lines at k * spacing (k = 1, 2, ...) that stay at least 1.2 mm (Revit's short-curve tolerance) short of
    // the far end; the start and end of the wall are its own edges and get no added line.
    public static int GridLineCount(double extentMm, double spacingMm)
    {
        if (!double.IsFinite(extentMm) || !double.IsFinite(spacingMm) || !(spacingMm > 0) || !(extentMm > RevitCommandGeometry.MinSegmentMm)) return 0;
        var count = Math.Floor((extentMm - RevitCommandGeometry.MinSegmentMm) / spacingMm);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    public static IReadOnlyList<double> GridPositions(double extentMm, double spacingMm) =>
        Enumerable.Range(1, GridLineCount(extentMm, spacingMm)).Select(k => k * spacingMm).ToArray();

    // A spacing that yields between 1 and MaxCurtainGridLines lines over the extent.
    public static Length CheckGridSpacing(double spacingMm, double extentMm, string field, string extentName)
    {
        var spacing = RevitUnits.Millimetres(spacingMm, field, allowNegative: false, allowZero: false);
        if (spacingMm < RevitCommandGeometry.MinSegmentMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "spacing under 1.2 mm");
        var count = GridLineCount(extentMm, spacingMm);
        if (count < 1) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "spacing leaves no grid line inside the wall " + extentName + " (" + extentMm.ToString("R", CultureInfo.InvariantCulture) + " mm)");
        if (count > MaxCurtainGridLines) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "spacing makes " + count + " grid lines; at most " + MaxCurtainGridLines);
        return spacing;
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of both parts exists (the v1 curve schemas are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> V2Specs = new(BuildV2);

    public static IReadOnlyList<RevitCommandSpec> V2 => V2Specs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildV2()
    {
        var outline = Fill("""{"type":"array","description":"@0@","items":@1@,"minItems":3,"maxItems":@2@}""", "@D@", Point, MaxBoundaryPoints.ToString(CultureInfo.InvariantCulture));
        string OutlineSchema(string description) => outline.Replace("@D@", description, StringComparison.Ordinal);
        const string BaseOffset = """{"type":"number","description":"Base offset from the level in millimetres; may be negative, at most 9144000 either way; default 0"}""";

        var roof = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"boundary":@2@,"slope_deg":{"type":"number","description":"Slope of every footprint edge in degrees, greater than 0 and at most 85 (a hip roof); omit for a flat roof"},"base_offset_mm":@3@},"required":["level","type","boundary"],"additionalProperties":false}""",
            Reference("Base level: name or UniqueId from revit_query_levels"), Reference("Roof type: name or UniqueId from revit_query_roof_types"),
            OutlineSchema("Closed footprint outline as [x, y] millimetre vertices; the closing edge is implicit; must not self-intersect"), BaseOffset);

        var run = Fill("""{"type":"object","description":"Straight run centre line in plan, from the bottom step to the top step, in millimetres","properties":{"start":@0@,"end":@1@},"required":["start","end"],"additionalProperties":false}""", Point, Point);
        var stair = Fill("""{"type":"object","properties":{"base_level":@0@,"top_level":@1@,"run":@2@,"width_mm":{"type":"number","description":"Run width in millimetres, at least 1.2 and at most 9144000"}},"required":["base_level","top_level","run","width_mm"],"additionalProperties":false}""",
            Reference("Bottom level: name or UniqueId from revit_query_levels"), Reference("Top level, above the bottom level: name or UniqueId from revit_query_levels"), run);

        var grid = """{"type":"object","description":"Extra grid lines on this wall only; the curtain wall type is not changed. Give at least one spacing","properties":{"vertical_spacing_mm":{"type":"number","description":"Distance between vertical grid lines along the wall from its start, in millimetres; 1 to 500 lines"},"horizontal_spacing_mm":{"type":"number","description":"Distance between horizontal grid lines up from the wall base, in millimetres; 1 to 500 lines"}},"additionalProperties":false}""";
        var curtain = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"base_offset_mm":@2@,"height_mm":{"type":"number","description":"Unconnected height in millimetres, greater than 0 and at most 9144000 (Revit limit)"},"curve":{"description":"Wall location line in plan, in millimetres","oneOf":[@3@,@4@,@5@]},"grid":@6@},"required":["level","type","height_mm","curve"],"additionalProperties":false}""",
            Reference("Base level: name or UniqueId from revit_query_levels"), Reference("Curtain wall type (kind Curtain): name or UniqueId from revit_query_wall_types"), BaseOffset,
            LineCurve, ArcThroughCurve, ArcCenterCurve, grid);

        var opening = Fill("""{"type":"object","properties":{"host_floor_uid":@0@,"profile":@1@},"required":["host_floor_uid","profile"],"additionalProperties":false}""",
            UniqueIdSchema, OutlineSchema("Closed opening outline as [x, y] millimetre vertices inside the host floor; the closing edge is implicit; must not self-intersect"));

        return new[]
        {
            new RevitCommandSpec("query.roof_types", false, "List the roof types available in the document with name, UniqueId and thickness in millimetres. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
            new RevitCommandSpec("roof.create_footprint", true, "Create a footprint roof of a given type on a level from a closed outline in millimetres, flat or with one slope on every edge (hip roof). Allowed only on enabled test copies.", roof, BindRoof),
            new RevitCommandSpec("stair.create", true, "Create a stair with one straight run between a base and a top level, along a plan line with a run width in millimetres. Allowed only on enabled test copies.", stair, BindStair),
            new RevitCommandSpec("curtain_wall.create", true, "Create one straight or curved curtain wall of a curtain wall type on a level, with optional extra vertical and horizontal grid lines at a spacing in millimetres. Allowed only on enabled test copies.", curtain, BindCurtainWall),
            new RevitCommandSpec("floor.create_opening", true, "Cut an opening through an existing floor along a closed, non-self-intersecting profile in millimetres. Allowed only on enabled test copies.", opening, BindFloorOpening),
        };
    }

    static IReadOnlyList<Point2> BindOutline(JsonElement args, string name)
    {
        var points = new List<(double X, double Y)>();
        var index = 0;
        foreach (var vertex in args.GetProperty(name).EnumerateArray())
        {
            points.Add(Coordinates(vertex, "args." + name + "[" + index + "]"));
            index++;
        }
        return RevitCommandGeometry.CheckBoundary(points, "args." + name).Select(ToPoint).ToArray();
    }

    static RevitCommandArgs BindRoof(JsonElement args)
    {
        var boundary = BindOutline(args, "boundary");
        Angle? slope = null;
        if (args.TryGetProperty("slope_deg", out var slopeElement))
        {
            var degrees = Number(slopeElement);
            var angle = RevitUnits.Degrees(degrees, "args.slope_deg");
            if (!(degrees > 0 && degrees <= RevitCommandGeometryV2.MaxRoofSlopeDeg)) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, "args.slope_deg", "slope must be greater than 0 and at most 85 degrees; omit it for a flat roof");
            var rise = Math.Tan(angle.Radians) * RevitCommandGeometryV2.MinimumWidthMm(boundary) / 2;
            if (!(rise <= RevitCommandGeometry.MaxRevitLengthMm)) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, "args.slope_deg", "the roof would rise up to " + rise.ToString("R", CultureInfo.InvariantCulture) + " mm, above 9144000 mm (Revit's 30000 ft limit)");
            slope = angle;
        }
        var baseOffset = args.TryGetProperty("base_offset_mm", out var offset)
            ? RevitUnits.Millimetres(Number(offset), "args.base_offset_mm", allowNegative: true, allowZero: true)
            : Length.FromMetres(0);
        if (Math.Abs(baseOffset.Millimetres) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.base_offset_mm", "base offset beyond 9144000 mm (Revit's 30000 ft limit)");
        return new RoofFootprintCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("type").GetString()!, boundary, slope, baseOffset);
    }

    static RevitCommandArgs BindStair(JsonElement args)
    {
        var baseLevel = args.GetProperty("base_level").GetString()!;
        var topLevel = args.GetProperty("top_level").GetString()!;
        if (string.Equals(baseLevel, topLevel, StringComparison.Ordinal)) throw new RevitCommandException(RevitCommandCodes.LevelsNotDistinct, "args.top_level", "base_level and top_level must name different levels");
        var run = args.GetProperty("run");
        var start = Coordinates(run, "start", "args.run.start");
        var end = Coordinates(run, "end", "args.run.end");
        RevitCommandGeometry.CheckLine(start.X, start.Y, end.X, end.Y, "args.run");
        var width = RevitUnits.Millimetres(Number(args.GetProperty("width_mm")), "args.width_mm", allowNegative: false, allowZero: false);
        if (width.Millimetres < RevitCommandGeometry.MinSegmentMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.width_mm", "width under 1.2 mm");
        if (width.Millimetres > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.width_mm", "width above 9144000 mm (Revit's 30000 ft limit)");
        return new StairCreateArgs(baseLevel, topLevel, ToPoint(start), ToPoint(end), width);
    }

    static RevitCommandArgs BindCurtainWall(JsonElement args)
    {
        var (baseOffset, height) = BindHeights(args);
        var curve = BindCurve(args.GetProperty("curve"));
        Length? vertical = null, horizontal = null;
        if (args.TryGetProperty("grid", out var grid))
        {
            var hasVertical = grid.TryGetProperty("vertical_spacing_mm", out var verticalElement);
            var hasHorizontal = grid.TryGetProperty("horizontal_spacing_mm", out var horizontalElement);
            if (!hasVertical && !hasHorizontal) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.grid", "give vertical_spacing_mm, horizontal_spacing_mm or both; omit grid for none");
            if (hasVertical) vertical = RevitCommandGeometryV2.CheckGridSpacing(Number(verticalElement), RevitCommandGeometryV2.CurveLengthMm(curve), "args.grid.vertical_spacing_mm", "length");
            if (hasHorizontal) horizontal = RevitCommandGeometryV2.CheckGridSpacing(Number(horizontalElement), height.Millimetres, "args.grid.horizontal_spacing_mm", "height");
        }
        return new CurtainWallCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("type").GetString()!, baseOffset, height, curve, vertical, horizontal);
    }

    static RevitCommandArgs BindFloorOpening(JsonElement args) =>
        new FloorOpeningCreateArgs(args.GetProperty("host_floor_uid").GetString()!, BindOutline(args, "profile"));
}
