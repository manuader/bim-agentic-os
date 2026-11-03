namespace Bim.Cad;

// Finds candidate wall pairs among straight segments (see WallPairOptions and WallPair). Segments are sorted by
// direction so only directions within the angle tolerance are compared; the result is ordered by confidence
// (descending), then by A and B input order, so the same input always gives the same list.
public static class WallPairDetector
{
    public static IReadOnlyList<WallPair> Detect(DxfDrawing drawing, WallPairOptions options)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        return Detect(drawing.Explode().Entities, options);
    }

    public static IReadOnlyList<WallPair> Detect(IEnumerable<DxfEntity> entities, WallPairOptions options)
    {
        ArgumentNullException.ThrowIfNull(entities);
        Validate(options);
        var layers = options.Layers is null ? null : new HashSet<string>(options.Layers, StringComparer.OrdinalIgnoreCase);
        var segments = new List<Segment>();
        var order = 0;
        foreach (var entity in entities)
        {
            order++;
            if (layers is not null && !layers.Contains(entity.Provenance.Layer)) continue;
            switch (entity)
            {
                case DxfLine line:
                    AddSegment(segments, new WallSide(line.Provenance, null, line.Start, line.End), order, 0);
                    break;
                case DxfPolyline polyline:
                    foreach (var segment in polyline.Segments())
                    {
                        if (!segment.IsArc) AddSegment(segments, new WallSide(polyline.Provenance, segment.Index, segment.Start, segment.End), order, segment.Index);
                    }
                    break;
                default:
                    break;
            }
        }
        segments.Sort((a, b) => a.Angle != b.Angle ? a.Angle.CompareTo(b.Angle) : a.CompareOrder(b));
        var pairs = new List<(WallPair Pair, Segment A, Segment B)>();
        var tolerance = options.AngleToleranceDegrees;
        for (var i = 0; i < segments.Count; i++)
        {
            for (var j = i + 1; j < segments.Count && segments[j].Angle - segments[i].Angle <= tolerance; j++) Consider(segments[i], segments[j], options, pairs);
            // Directions near 180 degrees are parallel to directions near 0.
            for (var j = 0; j < i && segments[j].Angle + 180 - segments[i].Angle <= tolerance; j++) Consider(segments[i], segments[j], options, pairs);
        }
        pairs.Sort((x, y) =>
        {
            var byConfidence = y.Pair.Confidence.CompareTo(x.Pair.Confidence);
            if (byConfidence != 0) return byConfidence;
            var byA = x.A.CompareOrder(y.A);
            return byA != 0 ? byA : x.B.CompareOrder(y.B);
        });
        return pairs.Select(p => p.Pair).ToArray();
    }

    private static void Validate(WallPairOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.MinThickness) || options.MinThickness < 0) throw new ArgumentException("MinThickness must be a finite non-negative length", nameof(options));
        if (!double.IsFinite(options.MaxThickness) || options.MaxThickness < options.MinThickness || !(options.MaxThickness > 0)) throw new ArgumentException("MaxThickness must be finite, positive and at least MinThickness", nameof(options));
        if (!double.IsFinite(options.MinOverlap) || options.MinOverlap < 0) throw new ArgumentException("MinOverlap must be a finite non-negative length", nameof(options));
        if (!(options.AngleToleranceDegrees > 0) || !(options.AngleToleranceDegrees < 90)) throw new ArgumentException("AngleToleranceDegrees must be in (0, 90)", nameof(options));
    }

    private static void AddSegment(List<Segment> segments, WallSide side, int order, int index)
    {
        var v = side.End - side.Start;
        var length = v.Length;
        if (!(length > 0) || !double.IsFinite(length) || !double.IsFinite(side.Start.X) || !double.IsFinite(side.Start.Y)) return;
        var angle = Math.Atan2(v.Y, v.X) * 180 / Math.PI % 180;
        if (angle < 0) angle += 180;
        if (angle >= 180) angle = 0;
        segments.Add(new Segment(side, order, index, new Point2(v.X / length, v.Y / length), length, angle));
    }

    private static void Consider(Segment s, Segment t, WallPairOptions options, List<(WallPair, Segment, Segment)> pairs)
    {
        var (a, b) = s.CompareOrder(t) <= 0 ? (s, t) : (t, s);
        var deviation = Math.Abs(a.Angle - b.Angle);
        if (deviation > 90) deviation = 180 - deviation;
        if (deviation > options.AngleToleranceDegrees) return;
        var origin = a.Side.Start;
        var tb0 = Dot(b.Side.Start - origin, a.Direction);
        var tb1 = Dot(b.Side.End - origin, a.Direction);
        var lo = Math.Max(0, Math.Min(tb0, tb1));
        var hi = Math.Min(a.Length, Math.Max(tb0, tb1));
        var overlap = hi - lo;
        if (!(overlap > 0) || overlap < options.MinOverlap || tb1 == tb0) return;
        var middle = (lo + hi) / 2;
        var along = (middle - tb0) / (tb1 - tb0);
        var onB = b.Side.Start + (b.Side.End - b.Side.Start) * along;
        var signed = Cross(a.Direction, onB - origin);
        var thickness = Math.Abs(signed);
        if (!(thickness >= options.MinThickness) || !(thickness <= options.MaxThickness)) return;
        var confidence = overlap / Math.Max(a.Length, b.Length) * (1 - 0.5 * deviation / options.AngleToleranceDegrees);
        if (!(confidence > 0)) return;
        if (confidence > 1) confidence = 1;
        var normal = new Point2(-a.Direction.Y, a.Direction.X) * (signed / 2);
        var start = origin + a.Direction * lo + normal;
        var end = origin + a.Direction * hi + normal;
        pairs.Add((new WallPair(a.Side, b.Side, thickness, overlap, deviation, confidence, start, end), a, b));
    }

    private static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;

    private static double Cross(Point2 a, Point2 b) => a.X * b.Y - a.Y * b.X;

    private sealed record Segment(WallSide Side, int Order, int Index, Point2 Direction, double Length, double Angle)
    {
        public int CompareOrder(Segment other) => Order != other.Order ? Order.CompareTo(other.Order) : Index.CompareTo(other.Index);
    }
}
