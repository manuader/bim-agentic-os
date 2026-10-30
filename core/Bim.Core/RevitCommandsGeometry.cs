namespace Bim.Core;

// Plan geometry checks for catalogue v1, in millimetres. Fail closed: touching, or coming closer than Revit's
// short-curve tolerance, counts as intersecting.
public static class RevitCommandGeometry
{
    // Revit's short-curve tolerance is 1/256 ft (about 1.19 mm); shorter curves or edges cannot be created.
    public const double MinSegmentMm = 1.2;
    // Three points whose turn has a sine below this are treated as collinear.
    public const double CollinearSine = 1e-9;
    const double AdjacentCollinearSine = 1e-12;
    const double SweepEpsilonDegrees = 1e-9;

    public readonly record struct Arc(double CenterX, double CenterY, double RadiusMm, double SweepDegrees, bool CounterClockwise);

    public static double Distance(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static void CheckLine(double sx, double sy, double ex, double ey, string field)
    {
        if (!(Distance(sx, sy, ex, ey) >= MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.CurveTooShort, field, "line shorter than 1.2 mm");
    }

    // Arc from start to end passing through a third point.
    public static Arc ArcThrough(double sx, double sy, double ex, double ey, double tx, double ty, string field)
    {
        var chord = Distance(sx, sy, ex, ey);
        if (!(chord >= MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.ArcSweepInvalid, field, "start and end coincide: sweep would be 0 or 360 degrees");
        double ux = tx - sx, uy = ty - sy, vx = ex - sx, vy = ey - sy;
        var cross = ux * vy - uy * vx;
        var lu = Math.Sqrt(ux * ux + uy * uy);
        if (!(lu >= MinSegmentMm) || !(Distance(tx, ty, ex, ey) >= MinSegmentMm) || !(Math.Abs(cross) > CollinearSine * lu * chord))
            throw new RevitCommandException(RevitCommandCodes.ArcCollinear, field, "start, through and end must not be collinear");
        // Circumcentre relative to start.
        double d = 2 * cross, uu = ux * ux + uy * uy, vv = vx * vx + vy * vy;
        double cx = (vy * uu - uy * vv) / d, cy = (ux * vv - vx * uu) / d;
        var radius = Math.Sqrt(cx * cx + cy * cy);
        double centerX = sx + cx, centerY = sy + cy;
        var counterClockwise = cross > 0;
        var ccw = Math.Atan2(ey - centerY, ex - centerX) - Math.Atan2(sy - centerY, sx - centerX);
        if (ccw <= 0) ccw += 2 * Math.PI;
        var sweep = counterClockwise ? ccw : 2 * Math.PI - ccw;
        var sweepDegrees = sweep * 180 / Math.PI;
        // Largest distance between the arc and its chord, in a cancellation-free form.
        var halfChord = chord / 2;
        var apothem = Math.Sqrt(Math.Max(0, radius * radius - halfChord * halfChord));
        var sagitta = sweep <= Math.PI ? halfChord * halfChord / (radius + apothem) : radius + apothem;
        if (!(sagitta >= MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.ArcCollinear, field, "arc deviates less than 1.2 mm from its chord");
        CheckExtent(centerX, centerY, radius, field);
        if (!(sweepDegrees > SweepEpsilonDegrees && sweepDegrees < 360 - SweepEpsilonDegrees)) throw new RevitCommandException(RevitCommandCodes.ArcSweepInvalid, field, "sweep must be strictly between 0 and 360 degrees");
        if (!(radius * sweep >= MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.CurveTooShort, field, "arc shorter than 1.2 mm");
        return new Arc(centerX, centerY, radius, sweepDegrees, counterClockwise);
    }

    // Centre arc, counter-clockwise from start to end angle.
    public static void CheckArcCenter(double centerX, double centerY, double radiusMm, double startDegrees, double endDegrees, string field)
    {
        if (!double.IsFinite(radiusMm) || radiusMm <= 0 || radiusMm > RevitUnits.MaxAbsMillimetres)
            throw new RevitCommandException(RevitCommandCodes.ArcRadiusInvalid, field + ".radius_mm", "radius must be > 0 and within 1e8 mm");
        RevitUnits.Degrees(startDegrees, field + ".start_angle_deg");
        RevitUnits.Degrees(endDegrees, field + ".end_angle_deg");
        var sweep = endDegrees - startDegrees;
        if (!(sweep > SweepEpsilonDegrees && sweep < 360 - SweepEpsilonDegrees))
            throw new RevitCommandException(RevitCommandCodes.ArcSweepInvalid, field, "end_angle_deg - start_angle_deg must be strictly between 0 and 360");
        // A short arc is curve_too_short; a long arc whose ends nearly meet is a near-full circle.
        if (!(radiusMm * sweep * Math.PI / 180 >= MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.CurveTooShort, field, "arc shorter than 1.2 mm");
        if (!(2 * radiusMm * Math.Sin(sweep * Math.PI / 360) >= MinSegmentMm))
            throw new RevitCommandException(RevitCommandCodes.ArcSweepInvalid, field, "start and end points closer than 1.2 mm");
        CheckExtent(centerX, centerY, radiusMm, field);
    }

    // The whole circle (centre plus or minus radius) must stay within the coordinate range.
    static void CheckExtent(double centerX, double centerY, double radius, string field)
    {
        if (!double.IsFinite(radius) || radius <= 0 || Math.Abs(centerX) + radius > RevitUnits.MaxAbsMillimetres || Math.Abs(centerY) + radius > RevitUnits.MaxAbsMillimetres)
            throw new RevitCommandException(RevitCommandCodes.ArcRadiusInvalid, field, "arc centre and radius must stay within 1e8 mm of the origin");
    }

    // Closed simple polygon. A repeated closing vertex is dropped; the closing edge is implicit. Coordinates are
    // taken relative to the first vertex so orientation and area keep their precision far from the origin.
    // Edges that cross, touch or come within 1.2 mm of each other, or a vertex within 1.2 mm of a neighbouring
    // edge, make the outline boundary_self_intersecting.
    public static (double X, double Y)[] CheckBoundary(IReadOnlyList<(double X, double Y)> points, string field)
    {
        var original = points.ToList();
        if (original.Count >= 2 && Distance(original[0].X, original[0].Y, original[^1].X, original[^1].Y) < MinSegmentMm) original.RemoveAt(original.Count - 1);
        var n = original.Count;
        if (n < 3) throw new RevitCommandException(RevitCommandCodes.BoundaryTooFewPoints, field, "at least 3 distinct vertices");
        var origin = original[0];
        var p = original.Select(point => (X: point.X - origin.X, Y: point.Y - origin.Y)).ToList();
        for (var i = 0; i < n; i++)
        {
            var next = p[(i + 1) % n];
            if (!(Distance(p[i].X, p[i].Y, next.X, next.Y) >= MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.BoundaryEdgeTooShort, field + "[" + i + "]", "edge shorter than 1.2 mm");
        }
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                bool tooClose;
                if (j == i + 1) tooClose = Backtracks(p[i], p[j], p[(j + 1) % n]) || AdjacentTooClose(p[i], p[j], p[(j + 1) % n]);
                else if (i == 0 && j == n - 1) tooClose = Backtracks(p[1], p[0], p[n - 1]) || AdjacentTooClose(p[1], p[0], p[n - 1]);
                else tooClose = SegmentsIntersect(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n]) || !(SegmentDistance(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n]) >= MinSegmentMm);
                if (tooClose) throw new RevitCommandException(RevitCommandCodes.BoundarySelfIntersecting, field + "[" + i + "]", "edges " + i + " and " + j + " intersect, overlap or come within 1.2 mm");
            }
        }
        var twiceArea = 0.0;
        for (var i = 0; i < n; i++)
        {
            var next = p[(i + 1) % n];
            twiceArea += p[i].X * next.Y - next.X * p[i].Y;
        }
        if (!(Math.Abs(twiceArea) / 2 >= MinSegmentMm * MinSegmentMm)) throw new RevitCommandException(RevitCommandCodes.BoundaryZeroArea, field, "enclosed area is (nearly) zero");
        return original.ToArray();
    }

    // Edges (a,q) and (q,r): the far end of either lies within 1.2 mm of the other edge.
    static bool AdjacentTooClose((double X, double Y) a, (double X, double Y) q, (double X, double Y) r) =>
        !(PointSegmentDistance(a, q, r) >= MinSegmentMm) || !(PointSegmentDistance(r, a, q) >= MinSegmentMm);

    static double PointSegmentDistance((double X, double Y) point, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        var t = lengthSquared > 0 ? Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1) : 0;
        return Distance(point.X, point.Y, a.X + t * dx, a.Y + t * dy);
    }

    // Distance between two segments that do not intersect: the closest endpoint-to-segment distance.
    static double SegmentDistance((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d) =>
        Math.Min(Math.Min(PointSegmentDistance(a, c, d), PointSegmentDistance(b, c, d)), Math.Min(PointSegmentDistance(c, a, b), PointSegmentDistance(d, a, b)));

    // Edges (a,q) and (q,r) overlap when they are collinear and r turns back along (a,q).
    static bool Backtracks((double X, double Y) a, (double X, double Y) q, (double X, double Y) r)
    {
        double ax = a.X - q.X, ay = a.Y - q.Y, rx = r.X - q.X, ry = r.Y - q.Y;
        var cross = ax * ry - ay * rx;
        var lengths = Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(rx * rx + ry * ry);
        return Math.Abs(cross) <= AdjacentCollinearSine * lengths && ax * rx + ay * ry > 0;
    }

    static int Orient((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        var value = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return value > 0 ? 1 : value < 0 ? -1 : 0;
    }

    static bool Within((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        Math.Min(a.X, b.X) <= c.X && c.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= c.Y && c.Y <= Math.Max(a.Y, b.Y);

    // Inclusive: shared or touching points count as an intersection.
    static bool SegmentsIntersect((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        int o1 = Orient(a, b, c), o2 = Orient(a, b, d), o3 = Orient(c, d, a), o4 = Orient(c, d, b);
        if (o1 != o2 && o3 != o4) return true;
        return (o1 == 0 && Within(a, b, c)) || (o2 == 0 && Within(a, b, d)) || (o3 == 0 && Within(c, d, a)) || (o4 == 0 && Within(c, d, b));
    }
}
