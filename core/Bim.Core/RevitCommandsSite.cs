using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// Site commands: query.toposolid_types, toposolid.create and toposolid.create_subdivision (Revit 2024+ toposolids,
// for a light site model: terrain, small boulders, a trail region). Same envelope (protocol_version 1), schema subset,
// unit rules, copies-only guard and receipts as the earlier catalogue parts, which are unchanged. Vendor-free like
// every RevitCommands*.cs file; the add-in executor (CommandsExecutorSite.cs) consumes these types.

// A point in millimetres. Z is an elevation in the same frame as a level's project elevation (query.levels
// project_elevation_mm).
public readonly record struct SitePoint(Length X, Length Y, Length Z);
// A toposolid of Type on Level whose top face passes through Points (plan outline: their convex hull).
public sealed record ToposolidCreateArgs(string Level, string Type, IReadOnlyList<SitePoint> Points, string? Comment) : RevitCommandArgs;
// A subdivision of the toposolid HostUid inside a closed plan Outline, with optional material, subdivide height
// (offset above the host surface) and Comments text.
public sealed record ToposolidSubdivisionCreateArgs(string HostUid, IReadOnlyList<Point2> Outline, string? Material, Length? SubdivideHeight, string? Comment) : RevitCommandArgs;

public static class RevitSiteRules
{
    public const int MinToposolidPoints = 3;
    public const int MaxToposolidPoints = 2000;

    // Every coordinate finite within 1e8 mm (length_invalid at field[i][k]); no two points closer than 1.2 mm in plan
    // (args_invalid at the later index: a vertical step cannot be a top face); not all on one plan line (every point
    // within 1.2 mm of the line through the first point and the point farthest from it: boundary_zero_area).
    public static void CheckToposolidPoints(IReadOnlyList<(double X, double Y, double Z)> points, string field)
    {
        for (var i = 0; i < points.Count; i++)
        {
            RevitUnits.Millimetres(points[i].X, field + "[" + i + "][0]", allowNegative: true, allowZero: true);
            RevitUnits.Millimetres(points[i].Y, field + "[" + i + "][1]", allowNegative: true, allowZero: true);
            RevitUnits.Millimetres(points[i].Z, field + "[" + i + "][2]", allowNegative: true, allowZero: true);
        }
        if (points.Count < MinToposolidPoints) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "at least " + MinToposolidPoints + " points");
        if (points.Count > MaxToposolidPoints) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "at most " + MaxToposolidPoints + " points");
        // Relative to the first point so precision holds far from the origin.
        var origin = points[0];
        var p = points.Select(point => (X: point.X - origin.X, Y: point.Y - origin.Y)).ToArray();
        // Sort and sweep on x: only pairs closer than 1.2 mm in x can be closer than 1.2 mm in plan.
        var order = Enumerable.Range(0, p.Length).OrderBy(i => p[i].X).ThenBy(i => i).ToArray();
        var duplicate = -1;
        for (var a = 0; a < order.Length; a++)
        {
            for (var b = a + 1; b < order.Length && p[order[b]].X - p[order[a]].X < RevitCommandGeometry.MinSegmentMm; b++)
            {
                if (!(RevitCommandGeometry.Distance(p[order[a]].X, p[order[a]].Y, p[order[b]].X, p[order[b]].Y) >= RevitCommandGeometry.MinSegmentMm))
                    duplicate = duplicate < 0 ? Math.Max(order[a], order[b]) : Math.Min(duplicate, Math.Max(order[a], order[b]));
            }
        }
        if (duplicate >= 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + duplicate + "]", "point closer than 1.2 mm in plan to an earlier point");
        var far = 0;
        var farDistance = 0.0;
        for (var i = 1; i < p.Length; i++)
        {
            var distance = Math.Sqrt(p[i].X * p[i].X + p[i].Y * p[i].Y);
            if (distance > farDistance) { far = i; farDistance = distance; }
        }
        var offLine = 0.0;
        for (var i = 1; i < p.Length; i++) offLine = Math.Max(offLine, Math.Abs(p[far].X * p[i].Y - p[far].Y * p[i].X) / farDistance);
        if (!(offLine >= RevitCommandGeometry.MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.BoundaryZeroArea, field, "points are collinear in plan (within 1.2 mm of one line): no area");
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> SiteSpecs = new(BuildSite);

    public static IReadOnlyList<RevitCommandSpec> Site => SiteSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildSite()
    {
        const string Point3 = """{"type":"array","description":"[x, y, z] in millimetres; z is an elevation in the frame of a level's project_elevation_mm (revit_query_levels)","items":{"type":"number"},"minItems":3,"maxItems":3}""";
        var comment = Text("Text for the element's Comments parameter so it can be identified (for example 'site: boulder 3'); optional", 256);

        var toposolid = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"points":{"type":"array","description":"Points of the top face, 3 to @2@, no two closer than 1.2 mm in plan and not all on one plan line; the plan outline is their convex hull. The whole request must stay within 65536 characters (about 2000 points with short numbers)","items":@3@,"minItems":@4@,"maxItems":@2@},"comment":@5@},"required":["level","type","points"],"additionalProperties":false}""",
            Reference("Level: name or UniqueId from revit_query_levels"), Reference("Toposolid type: name or UniqueId from revit_query_toposolid_types"),
            RevitSiteRules.MaxToposolidPoints.ToString(CultureInfo.InvariantCulture), Point3, RevitSiteRules.MinToposolidPoints.ToString(CultureInfo.InvariantCulture), comment);

        var subdivision = Fill("""{"type":"object","properties":{"host_uid":@0@,"outline":{"type":"array","description":"Closed plan outline of the region as [x, y] millimetre vertices; the closing edge is implicit; must not self-intersect","items":@1@,"minItems":3,"maxItems":@2@},"material":@3@,"subdivide_height_mm":{"type":"number","description":"Sub-divide height above the host surface in millimetres, 0 or more, at most 9144000; optional"},"comment":@4@},"required":["host_uid","outline"],"additionalProperties":false}""",
            UniqueIdSchema, Point, MaxBoundaryPoints.ToString(CultureInfo.InvariantCulture), Reference("Material: name or UniqueId of a material in the document; optional, else Revit's default"), comment);

        return new[]
        {
            new RevitCommandSpec("query.toposolid_types", false, "List the toposolid types available in the document with name, UniqueId and thickness in millimetres (null when the type has no layer structure). Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
            new RevitCommandSpec("toposolid.create", true, "Create a toposolid (Revit 2024+ terrain solid, also usable for small boulders) of a type on a level from 3 to 2000 [x, y, z] millimetre points of its top face, with an optional Comments text. Revit keeps the toposolid bottom flat at the level's elevation minus the type thickness, so points lower than the level minus the type thickness are not supported (Revit keeps a flat bottom there) and are refused as args_invalid; use a thicker type or a lower level. Allowed only on enabled test copies.", toposolid, BindToposolid),
            new RevitCommandSpec("toposolid.create_subdivision", true, "Mark a region of an existing toposolid (for example a trail) as a subdivision from a closed [x, y] millimetre outline, with an optional material, sub-divide height and Comments text. Allowed only on enabled test copies.", subdivision, BindToposolidSubdivision),
        };
    }

    static string? OptionalText(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.GetString() : null;

    static RevitCommandArgs BindToposolid(JsonElement args)
    {
        var raw = args.GetProperty("points").EnumerateArray().Select(point => (X: Number(point[0]), Y: Number(point[1]), Z: Number(point[2]))).ToArray();
        RevitSiteRules.CheckToposolidPoints(raw, "args.points");
        var points = raw.Select(point => new SitePoint(Length.FromMillimetres(point.X), Length.FromMillimetres(point.Y), Length.FromMillimetres(point.Z))).ToArray();
        return new ToposolidCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("type").GetString()!, points, OptionalText(args, "comment"));
    }

    static RevitCommandArgs BindToposolidSubdivision(JsonElement args)
    {
        var vertices = new List<(double X, double Y)>();
        var index = 0;
        foreach (var vertex in args.GetProperty("outline").EnumerateArray())
        {
            vertices.Add(Coordinates(vertex, "args.outline[" + index + "]"));
            index++;
        }
        var outline = RevitCommandGeometry.CheckBoundary(vertices, "args.outline");
        Length? height = null;
        if (args.TryGetProperty("subdivide_height_mm", out var heightElement))
        {
            var value = RevitUnits.Millimetres(Number(heightElement), "args.subdivide_height_mm", allowNegative: false, allowZero: true);
            if (value.Millimetres > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.subdivide_height_mm", "sub-divide height above 9144000 mm (Revit's 30000 ft limit)");
            height = value;
        }
        return new ToposolidSubdivisionCreateArgs(args.GetProperty("host_uid").GetString()!, outline.Select(ToPoint).ToArray(), OptionalText(args, "material"), height, OptionalText(args, "comment"));
    }
}
