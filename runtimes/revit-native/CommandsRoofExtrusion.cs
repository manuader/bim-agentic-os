using Bim.Core;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// roof.create_extrusion executor decisions, readback checks and readback JSON, in millimetres. Autodesk-free on
// purpose: tests/revit-commands source-links this file. CommandsExecutorRoofExtrusion.cs reads the Revit facts
// (lengths converted with UnitUtils) and calls the Revit API.
//
// Frame (RoofExtrusionFrame): origin = plane.start in plan, U = the unit plan direction start -> end, N = U rotated 90
// degrees counter-clockwise (the left normal, the extrusion direction). A model point (x, y, z) has u = (p - origin) . U,
// w = (p - origin) . N (its offset from the plane) and profile z = z - the level's project elevation.

public sealed record RoofExtrusionFrame(double OriginX, double OriginY, double Ux, double Uy)
{
    public double Nx => -Uy;
    public double Ny => Ux;
}
// One expected profile curve: its ends and its midpoint in (u, z); Arc false for a line.
public sealed record ExpectedProfileEdge(ProfilePointMm A, ProfilePointMm B, ProfilePointMm Mid, bool Arc);
// One curve of the roof's own profile as Revit reports it (ExtrusionRoof.GetProfile): ends and parameter midpoint in
// (u, z), whether it is an Arc, and the largest distance of those three points from the requested plane (|w|).
public sealed record ObservedProfileEdge(ProfilePointMm A, ProfilePointMm B, ProfilePointMm Mid, bool Arc, double PlaneOffsetMm);
// What Revit reports after creation. StartMm/EndMm are the extents in the request's sign (along N, see
// CommandRulesRoofExtrusion.ContractExtents); PlaneParallel: the profile's sketch plane normal is parallel to N;
// BoxMin/BoxMax: the roof's model bounding box corners [x, y, z] in millimetres (null without a box);
// LevelElevationMm: the reference level's project elevation. StartMm/EndMm come back through the same normal
// direction the executor used to send them, so they cannot show an extrusion to the wrong side: the bounding box
// check (CommandRulesRoofExtrusion.ExpectedBox) does.
public sealed record ObservedRoofExtrusion(string UniqueId, string LevelUid, string TypeUid, string? PlaneUid, double? StartMm, double? EndMm,
    IReadOnlyList<ObservedProfileEdge> Edges, bool PlaneParallel, double[]? BoxMin, double[]? BoxMax, double LevelElevationMm = 0);

public static class CommandRulesRoofExtrusion
{
    // Profile points and extents are compared within 1 mm (they are set exactly; 1 mm absorbs unit conversion and the
    // 0.5 mm connection snapping), the bounding box with 25 mm because Revit pads element boxes.
    public const double ToleranceMm = 1.0;
    public const double BoxToleranceMm = 25.0;

    public static RoofExtrusionFrame Frame(RoofExtrusionCreateArgs args)
    {
        double x0 = args.PlaneStart.X.Millimetres, y0 = args.PlaneStart.Y.Millimetres, dx = args.PlaneEnd.X.Millimetres - x0, dy = args.PlaneEnd.Y.Millimetres - y0;
        var length = Math.Sqrt(dx * dx + dy * dy);
        return new RoofExtrusionFrame(x0, y0, dx / length, dy / length);
    }

    // Plan point of a profile u.
    public static PlanPoint PlanOf(RoofExtrusionFrame frame, double u) => new(frame.OriginX + u * frame.Ux, frame.OriginY + u * frame.Uy);

    // (u, z) of a model point in millimetres, and its offset w from the plane along N.
    public static (ProfilePointMm Uz, double W) UzwOf(RoofExtrusionFrame frame, double x, double y, double z, double levelElevationMm)
    {
        double px = x - frame.OriginX, py = y - frame.OriginY;
        return (new ProfilePointMm(px * frame.Ux + py * frame.Uy, z - levelElevationMm), px * frame.Nx + py * frame.Ny);
    }

    // Revit measures the extents along the created reference plane's own normal. normalAgrees: that normal points
    // the same way as N; otherwise the extents are mirrored. The same mapping turns Revit's extents back into the
    // request's (it is its own inverse).
    public static (double Start, double End) RevitExtents(double startMm, double endMm, bool normalAgrees) => normalAgrees ? (startMm, endMm) : (-endMm, -startMm);
    public static (double Start, double End) ContractExtents(double revitStartMm, double revitEndMm, bool normalAgrees) => RevitExtents(revitStartMm, revitEndMm, normalAgrees);

    public static ExpectedProfileEdge[] Expected(RoofExtrusionCreateArgs args) => args.Profile.Select(segment =>
    {
        var a = new ProfilePointMm(segment.Start.U.Millimetres, segment.Start.Z.Millimetres);
        var b = new ProfilePointMm(segment.End.U.Millimetres, segment.End.Z.Millimetres);
        return segment is RoofProfileArc arc
            ? new ExpectedProfileEdge(a, b, new ProfilePointMm(arc.Mid.U.Millimetres, arc.Mid.Z.Millimetres), true)
            : new ExpectedProfileEdge(a, b, new ProfilePointMm((a.U + b.U) / 2, (a.Z + b.Z) / 2), false);
    }).ToArray();

    // The model box [x, y, z] in millimetres of the requested profile (ends and arc extremes) swept from the start to
    // the end extent along N, or, mirrored, along -N (the roof extruded to the wrong side). The roof's own box also
    // holds the type's thickness, so it contains this box but may be larger.
    public static (double[] Min, double[] Max) ExpectedBox(RoofExtrusionCreateArgs args, double levelElevationMm, bool mirrored)
    {
        var frame = Frame(args);
        var points = args.Profile.SelectMany(segment => RevitRoofExtrusionRules.Extremes(segment)).ToArray();
        double uMin = points.Min(p => p.U), uMax = points.Max(p => p.U), zMin = points.Min(p => p.Z), zMax = points.Max(p => p.Z);
        var (wStart, wEnd) = RevitExtents(args.ExtrusionStart.Millimetres, args.ExtrusionEnd.Millimetres, !mirrored);
        var corners = new[] { uMin, uMax }.SelectMany(u => new[] { wStart, wEnd }.Select(w => (X: frame.OriginX + u * frame.Ux + w * frame.Nx, Y: frame.OriginY + u * frame.Uy + w * frame.Ny))).ToArray();
        return (new[] { corners.Min(c => c.X), corners.Min(c => c.Y), levelElevationMm + zMin }, new[] { corners.Max(c => c.X), corners.Max(c => c.Y), levelElevationMm + zMax });
    }

    // The observed box holds the expected one within the box tolerance.
    public static bool Holds(double[] min, double[] max, (double[] Min, double[] Max) expected) =>
        Enumerable.Range(0, 3).All(k => min[k] <= expected.Min[k] + BoxToleranceMm && max[k] >= expected.Max[k] - BoxToleranceMm);

    // Sum of the corner coordinate differences between two boxes.
    public static double Gap(double[] min, double[] max, (double[] Min, double[] Max) other) =>
        Enumerable.Range(0, 3).Sum(k => Math.Abs(min[k] - other.Min[k]) + Math.Abs(max[k] - other.Max[k]));

    static double Distance(ProfilePointMm a, ProfilePointMm b) => Math.Sqrt((a.U - b.U) * (a.U - b.U) + (a.Z - b.Z) * (a.Z - b.Z));

    // The observed curves are the expected ones, one for one, in any order or direction.
    public static bool SameProfile(IReadOnlyList<ExpectedProfileEdge> expected, IReadOnlyList<ObservedProfileEdge> observed)
    {
        if (expected.Count != observed.Count) return false;
        var used = new bool[observed.Count];
        foreach (var edge in expected)
        {
            var match = -1;
            for (var k = 0; k < observed.Count && match < 0; k++)
            {
                var o = observed[k];
                if (used[k] || o.Arc != edge.Arc || Distance(o.Mid, edge.Mid) > ToleranceMm) continue;
                var forward = Distance(o.A, edge.A) <= ToleranceMm && Distance(o.B, edge.B) <= ToleranceMm;
                var backward = Distance(o.A, edge.B) <= ToleranceMm && Distance(o.B, edge.A) <= ToleranceMm;
                if (forward || backward) match = k;
            }
            if (match < 0) return false;
            used[match] = true;
        }
        return true;
    }
}

// Each check returns null when the readback matches, else a plain-English mismatch; the executor then rolls the
// whole transaction group back and answers transaction_failed with that text.
public static class CommandReadbackRoofExtrusion
{
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string? Roof(string levelUid, string typeUid, RoofExtrusionCreateArgs args, ObservedRoofExtrusion observed)
    {
        if (observed.LevelUid != levelUid) return "roof reference level is " + observed.LevelUid + ", expected " + levelUid;
        if (observed.TypeUid != typeUid) return "roof type is " + observed.TypeUid + ", expected " + typeUid;
        if (observed.PlaneUid is null) return "the roof's reference plane does not exist";
        double start = args.ExtrusionStart.Millimetres, end = args.ExtrusionEnd.Millimetres;
        if (observed.StartMm is not { } s || observed.EndMm is not { } e || Math.Abs(s - start) > CommandRulesRoofExtrusion.ToleranceMm || Math.Abs(e - end) > CommandRulesRoofExtrusion.ToleranceMm)
            return "roof extrusion reads back as " + (observed.StartMm is { } a ? F(a) : "null") + " to " + (observed.EndMm is { } b ? F(b) : "null") + " mm, expected " + F(start) + " to " + F(end) + " mm";
        if (!observed.PlaneParallel) return "the roof profile's sketch plane is not the requested vertical plane";
        if (observed.Edges.FirstOrDefault(edge => edge.PlaneOffsetMm > CommandRulesRoofExtrusion.ToleranceMm) is { } off)
            return "a roof profile curve lies " + F(off.PlaneOffsetMm) + " mm off the requested plane";
        if (!CommandRulesRoofExtrusion.SameProfile(CommandRulesRoofExtrusion.Expected(args), observed.Edges))
            return "the roof profile reads back as " + observed.Edges.Count + " curves that differ from the " + args.Profile.Count + " requested segments (ends and midpoints within 1 mm, same kind)";
        if (observed.BoxMin is not { Length: 3 } min || observed.BoxMax is not { Length: 3 } max) return "the roof has no bounding box";
        // Wrong-side guard, both ways: the box must hold the profile swept to the requested side and must not be
        // nearer the profile swept to the other side by more than 2 x 25 mm (skipped when both sides give the same box;
        // the margin keeps a box that holds both sweeps, a tie, from being refused on rounding).
        var expected = CommandRulesRoofExtrusion.ExpectedBox(args, observed.LevelElevationMm, mirrored: false);
        var other = CommandRulesRoofExtrusion.ExpectedBox(args, observed.LevelElevationMm, mirrored: true);
        string Box(double[] lo, double[] hi) => "[" + string.Join(", ", lo.Concat(hi).Select(v => F(Math.Round(v, 1)))) + "]";
        if (!CommandRulesRoofExtrusion.Holds(min, max, expected))
            return "the roof bounding box " + Box(min, max) + " does not hold the requested profile swept along the plane's left normal " + Box(expected.Min, expected.Max) + " (extruded the wrong way?)";
        if (CommandRulesRoofExtrusion.Gap(expected.Min, expected.Max, other) > 6 * CommandRulesRoofExtrusion.BoxToleranceMm
            && CommandRulesRoofExtrusion.Gap(min, max, other) + 2 * CommandRulesRoofExtrusion.BoxToleranceMm < CommandRulesRoofExtrusion.Gap(min, max, expected))
            return "the roof bounding box " + Box(min, max) + " is nearer the profile swept to the other side of the plane " + Box(other.Min, other.Max) + " (extruded the wrong way?)";
        return null;
    }

    // ---- Readback JSON (snake_case, millimetres) ----

    public static string RoofJson(ObservedRoofExtrusion observed, RoofExtrusionCreateArgs args)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("roof_extrusion");
            writer.WriteString("unique_id", observed.UniqueId);
            writer.WriteString("level_unique_id", observed.LevelUid);
            writer.WriteString("type_unique_id", observed.TypeUid);
            if (observed.PlaneUid is null) writer.WriteNull("reference_plane_unique_id"); else writer.WriteString("reference_plane_unique_id", observed.PlaneUid);
            Pair(writer, "plane_start_mm", args.PlaneStart.X.Millimetres, args.PlaneStart.Y.Millimetres);
            Pair(writer, "plane_end_mm", args.PlaneEnd.X.Millimetres, args.PlaneEnd.Y.Millimetres);
            Number(writer, "extrusion_start_mm", observed.StartMm);
            Number(writer, "extrusion_end_mm", observed.EndMm);
            writer.WriteStartArray("profile_mm");
            foreach (var edge in observed.Edges)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", edge.Arc ? "arc" : "line");
                Pair(writer, "start", edge.A.U, edge.A.Z);
                Pair(writer, "end", edge.B.U, edge.B.Z);
                if (edge.Arc) Pair(writer, "mid", edge.Mid.U, edge.Mid.Z);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (observed.BoxMin is { Length: 3 } min && observed.BoxMax is { Length: 3 } max)
            {
                writer.WriteStartObject("bounding_box_mm");
                Triple(writer, "min", min);
                Triple(writer, "max", max);
                writer.WriteEndObject();
            }
            else writer.WriteNull("bounding_box_mm");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number)) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    static void Pair(Utf8JsonWriter writer, string name, double a, double b)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(a);
        writer.WriteNumberValue(b);
        writer.WriteEndArray();
    }

    static void Triple(Utf8JsonWriter writer, string name, double[] values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }
}
