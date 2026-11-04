using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// roof.create_extrusion (P9-07): an extruded roof (barrel / curved sheet-metal roofs, gables drawn as a profile) from
// an open profile of lines and arcs drawn in a vertical reference plane, extruded perpendicular to that plane between
// two extents. Same envelope (protocol_version 1), schema subset, unit rules, copies-only guard and operation_id
// receipts as the earlier catalogue parts, which are unchanged. Vendor-free like every RevitCommands*.cs file; the
// add-in executor (CommandsExecutorRoofExtrusion.cs) consumes these types.
//
// Frame: the reference plane is the vertical plane through the plan line plane.start -> plane.end. A profile point
// [u, z] is u millimetres along that line from plane.start and z millimetres above the reference level's project
// elevation (the frame of wall.set_profile). The extrusion runs along the plane's left normal n (the plan direction
// rotated 90 degrees counter-clockwise, seen from above) from extrusion_start_mm to extrusion_end_mm.

// One profile segment in the (u, z) plane. Start is snapped to the previous segment's End (within 0.5 mm).
public abstract record RoofProfileSegment(ProfileVertex Start, ProfileVertex End);
public sealed record RoofProfileLine(ProfileVertex Start, ProfileVertex End) : RoofProfileSegment(Start, End);
// Mid is the arc's own midpoint (half its sweep), whichever form the request used; Sweep is strictly between 0 and
// 360 degrees; CounterClockwise is the turn from Start to End with u to the right and z up.
public sealed record RoofProfileArc(ProfileVertex Start, ProfileVertex End, ProfileVertex Mid, ProfileVertex Center, Length Radius, Angle Sweep, bool CounterClockwise) : RoofProfileSegment(Start, End);

public sealed record RoofExtrusionCreateArgs(string Level, string Type, Point2 PlaneStart, Point2 PlaneEnd, IReadOnlyList<RoofProfileSegment> Profile, Length ExtrusionStart, Length ExtrusionEnd) : RevitCommandArgs;

public static class RevitRoofExtrusionRules
{
    public const int MinSegments = 1;
    public const int MaxSegments = 100;
    // A segment must start within this distance of the previous segment's end; it is then snapped onto it.
    public const double ConnectToleranceMm = 0.5;
    const double AngleEpsilon = 1e-12;

    // Internal segment form in millimetres: a line (Arc false) or an arc around (Cx, Cz) with radius R starting at
    // angle A0 (radians, from +u toward +z) and sweeping SignedSweep (positive counter-clockwise).
    readonly record struct Seg(double Sx, double Sz, double Ex, double Ez, bool Arc, double Cx, double Cz, double R, double A0, double SignedSweep);

    static Seg Of(RoofProfileSegment segment)
    {
        double sx = segment.Start.U.Millimetres, sz = segment.Start.Z.Millimetres, ex = segment.End.U.Millimetres, ez = segment.End.Z.Millimetres;
        if (segment is not RoofProfileArc arc) return new Seg(sx, sz, ex, ez, false, 0, 0, 0, 0, 0);
        double cx = arc.Center.U.Millimetres, cz = arc.Center.Z.Millimetres;
        return new Seg(sx, sz, ex, ez, true, cx, cz, arc.Radius.Millimetres, Math.Atan2(sz - cz, sx - cx), arc.CounterClockwise ? arc.Sweep.Radians : -arc.Sweep.Radians);
    }

    // Arc midpoint from centre, start point, sweep and turn direction.
    public static ProfileVertex ArcMid(double cx, double cz, double radius, double sx, double sz, double sweepRadians, bool counterClockwise)
    {
        var mid = Math.Atan2(sz - cz, sx - cx) + (counterClockwise ? sweepRadians : -sweepRadians) / 2;
        return new ProfileVertex(Length.FromMillimetres(cx + radius * Math.Cos(mid)), Length.FromMillimetres(cz + radius * Math.Sin(mid)));
    }

    static double Distance(double ax, double az, double bx, double bz) => RevitCommandGeometry.Distance(ax, az, bx, bz);

    // Connected chain: each segment starts within ConnectToleranceMm of the previous end. Returns the profile with
    // every start snapped onto the previous end. A gap is args_invalid at args.profile[i].
    public static IReadOnlyList<RoofProfileSegment> Connect(IReadOnlyList<RoofProfileSegment> profile, string field)
    {
        var connected = new List<RoofProfileSegment>(profile.Count);
        for (var i = 0; i < profile.Count; i++)
        {
            var segment = profile[i];
            if (i > 0)
            {
                var previous = connected[i - 1].End;
                var gap = Distance(previous.U.Millimetres, previous.Z.Millimetres, segment.Start.U.Millimetres, segment.Start.Z.Millimetres);
                if (!(gap <= ConnectToleranceMm))
                    throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + i + "]",
                        "segment " + i + " starts " + gap.ToString("0.###", CultureInfo.InvariantCulture) + " mm away from the end of segment " + (i - 1) + "; each segment must start where the previous one ends (within 0.5 mm), in order");
                segment = segment switch
                {
                    RoofProfileArc arc => arc with { Start = previous },
                    _ => new RoofProfileLine(previous, segment.End),
                };
            }
            connected.Add(segment);
        }
        return connected;
    }

    // The chain must be open: its first start and last end at least 1.2 mm apart.
    public static void CheckOpen(IReadOnlyList<RoofProfileSegment> profile, string field)
    {
        var first = profile[0].Start;
        var last = profile[^1].End;
        if (!(Distance(first.U.Millimetres, first.Z.Millimetres, last.U.Millimetres, last.Z.Millimetres) >= RevitCommandGeometry.MinSegmentMm))
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field, "the profile is closed (its last segment ends where the first starts); an extrusion roof takes an open profile");
    }

    // No self-intersection: segments that are not neighbours must neither touch nor come within 1.2 mm; neighbours
    // may only meet at their shared end (no overlap, no second crossing, neither far end within 1.2 mm of the other).
    // A refusal is args_invalid at args.profile[j], j the later of the two segments.
    public static void CheckSimple(IReadOnlyList<RoofProfileSegment> profile, string field)
    {
        var segments = profile.Select(Of).ToArray();
        for (var j = 1; j < segments.Length; j++)
        {
            for (var i = 0; i < j; i++)
            {
                var a = segments[i];
                var b = segments[j];
                bool bad;
                if (j == i + 1)
                {
                    double jx = a.Ex, jz = a.Ez;
                    bad = !(PointDistance(b, a.Sx, a.Sz) >= RevitCommandGeometry.MinSegmentMm) || !(PointDistance(a, b.Ex, b.Ez) >= RevitCommandGeometry.MinSegmentMm)
                        || Intersections(a, b).Any(p => Distance(p.X, p.Z, jx, jz) > RevitCommandGeometry.MinSegmentMm);
                }
                else bad = Intersections(a, b).Count > 0 || !(SegmentDistance(a, b) >= RevitCommandGeometry.MinSegmentMm);
                if (bad)
                    throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + j + "]",
                        "segments " + i + " and " + j + " intersect, overlap or come within 1.2 mm of each other; the profile must not cross itself");
            }
        }
    }

    static double Normalize(double angle)
    {
        var turn = 2 * Math.PI;
        angle %= turn;
        return angle < 0 ? angle + turn : angle;
    }

    // Whether the point's direction from the arc centre lies within the arc's sweep.
    static bool WithinSweep(Seg arc, double x, double z)
    {
        var theta = Math.Atan2(z - arc.Cz, x - arc.Cx);
        var t = arc.SignedSweep >= 0 ? Normalize(theta - arc.A0) : Normalize(arc.A0 - theta);
        return t <= Math.Abs(arc.SignedSweep) + AngleEpsilon || t >= 2 * Math.PI - AngleEpsilon;
    }

    static double PointSegmentDistance(double px, double pz, double ax, double az, double bx, double bz)
    {
        double dx = bx - ax, dz = bz - az;
        var lengthSquared = dx * dx + dz * dz;
        var t = lengthSquared > 0 ? Math.Clamp(((px - ax) * dx + (pz - az) * dz) / lengthSquared, 0, 1) : 0;
        return Distance(px, pz, ax + t * dx, az + t * dz);
    }

    static double PointDistance(Seg s, double x, double z)
    {
        if (!s.Arc) return PointSegmentDistance(x, z, s.Sx, s.Sz, s.Ex, s.Ez);
        if (WithinSweep(s, x, z)) return Math.Abs(Distance(x, z, s.Cx, s.Cz) - s.R);
        return Math.Min(Distance(x, z, s.Sx, s.Sz), Distance(x, z, s.Ex, s.Ez));
    }

    // Least distance between two segments that do not intersect: the least over the end points of each to the other
    // and, for arcs, the points where the connecting line is normal to both curves.
    static double SegmentDistance(Seg a, Seg b)
    {
        var best = Math.Min(Math.Min(PointDistance(b, a.Sx, a.Sz), PointDistance(b, a.Ex, a.Ez)), Math.Min(PointDistance(a, b.Sx, b.Sz), PointDistance(a, b.Ex, b.Ez)));
        foreach (var (arc, other) in new[] { (a, b), (b, a) })
        {
            if (!arc.Arc) continue;
            if (!other.Arc)
            {
                double dx = other.Ex - other.Sx, dz = other.Ez - other.Sz, length = Math.Sqrt(dx * dx + dz * dz);
                if (!(length > 0)) continue;
                double nx = -dz / length, nz = dx / length;
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    double px = arc.Cx + sign * arc.R * nx, pz = arc.Cz + sign * arc.R * nz;
                    if (WithinSweep(arc, px, pz)) best = Math.Min(best, PointDistance(other, px, pz));
                }
            }
            else
            {
                double dx = other.Cx - arc.Cx, dz = other.Cz - arc.Cz, d = Math.Sqrt(dx * dx + dz * dz);
                if (!(d > 0)) continue;
                foreach (var sign in new[] { 1.0, -1.0 })
                {
                    double px = arc.Cx + sign * arc.R * dx / d, pz = arc.Cz + sign * arc.R * dz / d;
                    if (WithinSweep(arc, px, pz)) best = Math.Min(best, PointDistance(other, px, pz));
                }
            }
        }
        return best;
    }

    // Points the two segments share (for overlapping collinear lines or arcs on one circle: the overlap's ends).
    static List<(double X, double Z)> Intersections(Seg a, Seg b)
    {
        var points = new List<(double X, double Z)>();
        if (!a.Arc && !b.Arc) LineLine(a, b, points);
        else if (a.Arc && b.Arc) ArcArc(a, b, points);
        else if (a.Arc) LineArc(b, a, points);
        else LineArc(a, b, points);
        return points;
    }

    static void LineLine(Seg a, Seg b, List<(double X, double Z)> points)
    {
        double rx = a.Ex - a.Sx, rz = a.Ez - a.Sz, sx = b.Ex - b.Sx, sz = b.Ez - b.Sz;
        var denominator = rx * sz - rz * sx;
        double qx = b.Sx - a.Sx, qz = b.Sz - a.Sz;
        var scale = Math.Sqrt(rx * rx + rz * rz) * Math.Sqrt(sx * sx + sz * sz);
        if (Math.Abs(denominator) <= 1e-12 * scale)
        {
            // Parallel: only collinear lines can share points, the ends of one lying on the other.
            if (Math.Abs(qx * rz - qz * rx) > 1e-9 * scale) return;
            foreach (var (px, pz, line) in new[] { (b.Sx, b.Sz, a), (b.Ex, b.Ez, a), (a.Sx, a.Sz, b), (a.Ex, a.Ez, b) })
                if (PointSegmentDistance(px, pz, line.Sx, line.Sz, line.Ex, line.Ez) <= 1e-9) points.Add((px, pz));
            return;
        }
        var t = (qx * sz - qz * sx) / denominator;
        var u = (qx * rz - qz * rx) / denominator;
        if (t >= 0 && t <= 1 && u >= 0 && u <= 1) points.Add((a.Sx + t * rx, a.Sz + t * rz));
    }

    static void LineArc(Seg line, Seg arc, List<(double X, double Z)> points)
    {
        double dx = line.Ex - line.Sx, dz = line.Ez - line.Sz, fx = line.Sx - arc.Cx, fz = line.Sz - arc.Cz;
        double qa = dx * dx + dz * dz, qb = 2 * (fx * dx + fz * dz), qc = fx * fx + fz * fz - arc.R * arc.R;
        if (!(qa > 0)) return;
        var discriminant = qb * qb - 4 * qa * qc;
        if (discriminant < 0) return;
        var root = Math.Sqrt(discriminant);
        foreach (var t in root == 0 ? new[] { -qb / (2 * qa) } : new[] { (-qb - root) / (2 * qa), (-qb + root) / (2 * qa) })
        {
            if (t < 0 || t > 1) continue;
            double px = line.Sx + t * dx, pz = line.Sz + t * dz;
            if (WithinSweep(arc, px, pz)) points.Add((px, pz));
        }
    }

    static void ArcArc(Seg a, Seg b, List<(double X, double Z)> points)
    {
        double dx = b.Cx - a.Cx, dz = b.Cz - a.Cz, d = Math.Sqrt(dx * dx + dz * dz);
        if (d <= 1e-9 * Math.Max(a.R, b.R))
        {
            // Concentric: only arcs on the same circle share points, the ends of one lying on the other.
            if (Math.Abs(a.R - b.R) > 1e-9 * Math.Max(a.R, b.R)) return;
            foreach (var (px, pz, arc) in new[] { (b.Sx, b.Sz, a), (b.Ex, b.Ez, a), (a.Sx, a.Sz, b), (a.Ex, a.Ez, b) })
                if (WithinSweep(arc, px, pz)) points.Add((px, pz));
            return;
        }
        if (d > a.R + b.R || d < Math.Abs(a.R - b.R)) return;
        var along = (a.R * a.R - b.R * b.R + d * d) / (2 * d);
        var h = Math.Sqrt(Math.Max(0, a.R * a.R - along * along));
        double mx = a.Cx + along * dx / d, mz = a.Cz + along * dz / d;
        foreach (var sign in h == 0 ? new[] { 0.0 } : new[] { 1.0, -1.0 })
        {
            double px = mx - sign * h * dz / d, pz = mz + sign * h * dx / d;
            if (WithinSweep(a, px, pz) && WithinSweep(b, px, pz)) points.Add((px, pz));
        }
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> RoofExtrusionSpecs = new(BuildRoofExtrusion);

    public static IReadOnlyList<RevitCommandSpec> RoofExtrusion => RoofExtrusionSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildRoofExtrusion()
    {
        const string Uz = """{"type":"array","description":"[u, z] in millimetres: u along the plane line from plane.start, z above the reference level","items":{"type":"number"},"minItems":2,"maxItems":2}""";
        var line = Fill("""{"type":"object","description":"Straight profile segment from start to end","properties":{"kind":{"type":"string","enum":["line"]},"start":@0@,"end":@0@},"required":["kind","start","end"],"additionalProperties":false}""", Uz);
        var arcThrough = Fill("""{"type":"object","description":"Arc profile segment from start to end passing through a third point (not collinear)","properties":{"kind":{"type":"string","enum":["arc"]},"start":@0@,"end":@0@,"through":@0@},"required":["kind","start","end","through"],"additionalProperties":false}""", Uz);
        var arcCenter = Fill("""{"type":"object","description":"Arc profile segment around a centre, counter-clockwise (u to the right, z up) from start_angle_deg to end_angle_deg (0 degrees = +u, 90 = +z); sweep strictly between 0 and 360; it starts at the start angle","properties":{"kind":{"type":"string","enum":["arc"]},"center":@0@,"radius_mm":{"type":"number","description":"Radius in millimetres, greater than 0 and at most 9144000 (Revit limit)"},"start_angle_deg":{"type":"number","description":"Start angle in degrees"},"end_angle_deg":{"type":"number","description":"End angle in degrees, greater than start_angle_deg"}},"required":["kind","center","radius_mm","start_angle_deg","end_angle_deg"],"additionalProperties":false}""", Uz);
        var plane = Fill("""{"type":"object","description":"The vertical reference plane, as a plan line in millimetres (at least 1.2 mm long): the profile is drawn in the vertical plane through it, u along it from start; the roof is extruded perpendicular to it, toward its left side seen from start looking at end","properties":{"start":@0@,"end":@0@},"required":["start","end"],"additionalProperties":false}""", Point);
        var schema = Fill("""{"type":"object","properties":{"level":@0@,"type":@1@,"plane":@2@,"profile":{"type":"array","description":"Open profile of @3@ to @4@ line or arc segments in order, each starting where the previous one ends (within 0.5 mm); the last must not end where the first starts and segments must not cross or touch","items":{"oneOf":[@5@,@6@,@7@]},"minItems":@3@,"maxItems":@4@},"extrusion_start_mm":{"type":"number","description":"Start of the extrusion along the plane's left normal, in millimetres from the plane; may be negative; at most 9144000 either way"},"extrusion_end_mm":{"type":"number","description":"End of the extrusion along the plane's left normal, in millimetres from the plane, at least 1.2 greater than extrusion_start_mm; at most 9144000 either way"}},"required":["level","type","plane","profile","extrusion_start_mm","extrusion_end_mm"],"additionalProperties":false}""",
            Reference("Reference level: name or UniqueId from revit_query_levels; profile z is measured above its elevation"), Reference("Roof type: name or UniqueId from revit_query_roof_types"), plane,
            RevitRoofExtrusionRules.MinSegments.ToString(CultureInfo.InvariantCulture), RevitRoofExtrusionRules.MaxSegments.ToString(CultureInfo.InvariantCulture), line, arcThrough, arcCenter);
        return new[]
        {
            new RevitCommandSpec("roof.create_extrusion", true,
                "Create an extrusion roof (a barrel or curved sheet-metal roof, or any roof whose section is a profile) of a roof type: an open profile of 1 to 100 line and arc segments drawn in a vertical reference plane and extruded perpendicular to it. "
                + "The plane is the plan line plane.start -> plane.end; a profile point [u, z] is u mm along that line from plane.start and z mm above the reference level's elevation; the roof runs from extrusion_start_mm to extrusion_end_mm along the plane's left side (seen from start looking at end). "
                + "Example: a barrel roof over a 6000 x 10000 cabin with 300 mm overhangs, eaves at 2700 and crown at 3800, is plane [0,0] -> [6000,0], profile [{kind: arc, start: [-300,2700], end: [6300,2700], through: [3000,3800]}], extrusion -300 to 10300. "
                + "The profile must be open, connected in order and must not cross itself. Allowed only on enabled test copies.",
                schema, BindRoofExtrusion),
        };
    }

    static RevitCommandArgs BindRoofExtrusion(JsonElement args)
    {
        var planeElement = args.GetProperty("plane");
        var planeStart = Coordinates(planeElement, "start", "args.plane.start");
        var planeEnd = Coordinates(planeElement, "end", "args.plane.end");
        RevitCommandGeometry.CheckLine(planeStart.X, planeStart.Y, planeEnd.X, planeEnd.Y, "args.plane");

        var segments = new List<RoofProfileSegment>();
        var index = 0;
        foreach (var segment in args.GetProperty("profile").EnumerateArray())
        {
            segments.Add(BindProfileSegment(segment, "args.profile[" + index + "]"));
            index++;
        }
        var profile = RevitRoofExtrusionRules.Connect(segments, "args.profile");
        RevitRoofExtrusionRules.CheckOpen(profile, "args.profile");
        RevitRoofExtrusionRules.CheckSimple(profile, "args.profile");

        var start = ExtrusionExtent(args, "extrusion_start_mm");
        var end = ExtrusionExtent(args, "extrusion_end_mm");
        if (!(end.Millimetres - start.Millimetres >= RevitCommandGeometry.MinSegmentMm))
            throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.extrusion_end_mm", "extrusion_end_mm must be at least 1.2 mm greater than extrusion_start_mm");
        return new RoofExtrusionCreateArgs(args.GetProperty("level").GetString()!, args.GetProperty("type").GetString()!, ToPoint(planeStart), ToPoint(planeEnd), profile, start, end);
    }

    static Length ExtrusionExtent(JsonElement args, string name)
    {
        var field = "args." + name;
        var extent = RevitUnits.Millimetres(Number(args.GetProperty(name)), field, allowNegative: true, allowZero: true);
        if (Math.Abs(extent.Millimetres) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "beyond 9144000 mm (Revit's 30000 ft limit)");
        return extent;
    }

    // A [u, z] point of the profile within Revit's 30000 ft limit.
    static (double U, double Z) ProfilePoint(JsonElement parent, string name, string field)
    {
        var (u, z) = Coordinates(parent, name, field);
        if (Math.Abs(u) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field + "[0]", "u beyond 9144000 mm (Revit's 30000 ft limit)");
        if (Math.Abs(z) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field + "[1]", "z beyond 9144000 mm (Revit's 30000 ft limit)");
        return (u, z);
    }

    static ProfileVertex Vertex(double u, double z) => new(Length.FromMillimetres(u), Length.FromMillimetres(z));

    static RoofProfileSegment BindProfileSegment(JsonElement segment, string field)
    {
        if (segment.GetProperty("kind").GetString() == "line")
        {
            var start = ProfilePoint(segment, "start", field + ".start");
            var end = ProfilePoint(segment, "end", field + ".end");
            RevitCommandGeometry.CheckLine(start.U, start.Z, end.U, end.Z, field);
            return new RoofProfileLine(Vertex(start.U, start.Z), Vertex(end.U, end.Z));
        }
        if (segment.TryGetProperty("through", out _))
        {
            var start = ProfilePoint(segment, "start", field + ".start");
            var end = ProfilePoint(segment, "end", field + ".end");
            var through = ProfilePoint(segment, "through", field + ".through");
            var arc = RevitCommandGeometry.ArcThrough(start.U, start.Z, end.U, end.Z, through.U, through.Z, field);
            var sweep = Angle.FromDegrees(arc.SweepDegrees);
            return new RoofProfileArc(Vertex(start.U, start.Z), Vertex(end.U, end.Z), RevitRoofExtrusionRules.ArcMid(arc.CenterX, arc.CenterY, arc.RadiusMm, start.U, start.Z, sweep.Radians, arc.CounterClockwise),
                Vertex(arc.CenterX, arc.CenterY), Length.FromMillimetres(arc.RadiusMm), sweep, arc.CounterClockwise);
        }
        var center = ProfilePoint(segment, "center", field + ".center");
        var radius = Number(segment.GetProperty("radius_mm"));
        var startAngle = Number(segment.GetProperty("start_angle_deg"));
        var endAngle = Number(segment.GetProperty("end_angle_deg"));
        RevitCommandGeometry.CheckArcCenter(center.U, center.Z, radius, startAngle, endAngle, field);
        double a0 = startAngle * Math.PI / 180, a1 = endAngle * Math.PI / 180;
        double su = center.U + radius * Math.Cos(a0), sz = center.Z + radius * Math.Sin(a0);
        var arcSweep = Angle.FromRadians(a1 - a0);
        return new RoofProfileArc(Vertex(su, sz), Vertex(center.U + radius * Math.Cos(a1), center.Z + radius * Math.Sin(a1)),
            RevitRoofExtrusionRules.ArcMid(center.U, center.Z, radius, su, sz, arcSweep.Radians, true), Vertex(center.U, center.Z), Length.FromMillimetres(radius), arcSweep, true);
    }
}
