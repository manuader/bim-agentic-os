namespace Bim.Cad;

public static class DxfGeometry
{
    // Largest accepted |bulge|: 1e6 is already a full circle to within 1e-12 of a turn; larger values are rejected.
    public const double MaxBulge = 1e6;

    // Polyline segments in order (closing segment last for a closed polyline). A bulged segment becomes an arc whose
    // StartAngleDegrees..EndAngleDegrees is the counter-clockwise span: from the start vertex for a positive bulge,
    // from the end vertex for a negative (clockwise) bulge. A zero-length chord is always straight.
    public static IReadOnlyList<DxfPolylineSegment> Segments(DxfPolyline polyline)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        var vertices = polyline.Vertices;
        var count = polyline.Closed ? vertices.Count : vertices.Count - 1;
        var segments = new List<DxfPolylineSegment>(Math.Max(count, 0));
        for (var i = 0; i < count; i++)
        {
            var start = vertices[i];
            var end = vertices[(i + 1) % vertices.Count].Point;
            segments.Add(Segment(i, start.Point, end, start.Bulge));
        }
        return segments;
    }

    internal static DxfPolylineSegment Segment(int index, Point2 start, Point2 end, double bulge)
    {
        var chord = end - start;
        var length = chord.Length;
        if (bulge == 0 || !(length > 0) || !double.IsFinite(bulge)) return new DxfPolylineSegment(index, start, end, bulge, null, 0, 0, 0);
        var mid = (start + end) * 0.5;
        var normal = new Point2(-chord.Y / length, chord.X / length);
        var offset = length / 2 * ((1 - bulge * bulge) / (2 * bulge));
        var center = mid + normal * offset;
        var radius = length * (1 + bulge * bulge) / (4 * Math.Abs(bulge));
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(radius)) return new DxfPolylineSegment(index, start, end, bulge, null, 0, 0, 0);
        var a0 = Degrees(start - center);
        var a1 = Degrees(end - center);
        return bulge > 0
            ? new DxfPolylineSegment(index, start, end, bulge, center, radius, a0, a1)
            : new DxfPolylineSegment(index, start, end, bulge, center, radius, a1, a0);
    }

    public static Point2 SegmentMidpoint(DxfPolylineSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Center is not { } center) return (segment.Start + segment.End) * 0.5;
        var mid = segment.StartAngleDegrees + Span(segment.StartAngleDegrees, segment.EndAngleDegrees) / 2;
        return PointAt(center, segment.Radius, mid);
    }

    // Counter-clockwise span from start to end in (0, 360]; equal angles mean a full turn.
    internal static double Span(double startDegrees, double endDegrees)
    {
        var span = (endDegrees - startDegrees) % 360;
        if (span <= 0) span += 360;
        return span;
    }

    internal static Point2 PointAt(Point2 center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point2(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    internal static double Degrees(Point2 v) => DxfValues.NormaliseDegrees(Math.Atan2(v.Y, v.X) * 180 / Math.PI);
}

// 2D affine map: x' = A x + C y + E, y' = B x + D y + F.
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static Affine Identity => new(1, 0, 0, 1, 0, 0);

    public static Affine Translate(Point2 t) => new(1, 0, 0, 1, t.X, t.Y);

    public static Affine Rotate(double degrees)
    {
        var r = degrees * Math.PI / 180;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        // Exact quarter turns keep axis-aligned geometry exact.
        var quarter = degrees / 90;
        if (quarter == Math.Floor(quarter))
        {
            var q = (((int)(quarter % 4)) + 4) % 4;
            (cos, sin) = q switch { 0 => (1.0, 0.0), 1 => (0.0, 1.0), 2 => (-1.0, 0.0), _ => (0.0, -1.0) };
        }
        return new Affine(cos, sin, -sin, cos, 0, 0);
    }

    public static Affine Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    // this ∘ inner: apply inner first, then this.
    public Affine Compose(Affine inner) => new(
        A * inner.A + C * inner.B,
        B * inner.A + D * inner.B,
        A * inner.C + C * inner.D,
        B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E,
        B * inner.E + D * inner.F + F);

    public Point2 Apply(Point2 p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    public Point2 ApplyVector(Point2 v) => new(A * v.X + C * v.Y, B * v.X + D * v.Y);

    public double Determinant => A * D - B * C;

    public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

    // Similarity (uniform scale, rotation, optional mirror): circles stay circles.
    public bool IsConformal
    {
        get
        {
            var u = A * A + B * B;
            var v = C * C + D * D;
            var dot = A * C + B * D;
            var scale = Math.Max(u, v);
            return scale > 0 && Math.Abs(u - v) <= 1e-12 * scale && Math.Abs(dot) <= 1e-12 * scale;
        }
    }

    public double UniformScale => Math.Sqrt(A * A + B * B);
}
