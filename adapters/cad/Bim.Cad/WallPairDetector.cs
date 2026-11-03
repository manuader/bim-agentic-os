using System.Globalization;
namespace Bim.Cad;

// Finds candidate wall pairs among straight segments (see WallPairOptions and WallPair). Directions are put in
// equal angle bins at least as wide as the tolerance, so parallel candidates share a bin or sit in neighbouring
// bins (180 wraps to 0). Within each bin pair, segments are swept in order of their offset across a reference
// direction; |offset difference| never exceeds the true separation, so only segments whose offset ranges are within
// MaxThickness are compared. A plan of many parallel lines is therefore not quadratic. The result is ordered by
// confidence (descending), then by A and B input order, so the same input always gives the same list.
public static class WallPairDetector
{
    public static IReadOnlyList<WallPair> Detect(DxfDrawing drawing, WallPairOptions options)
    {
        ArgumentNullException.ThrowIfNull(drawing);
        Validate(options);
        HashSet<string>? hidden = null;
        if (options.SkipHiddenLayers)
            hidden = new HashSet<string>(drawing.Layers.Where(l => l.Frozen || l.Off).Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        return Run(drawing.Explode().Entities, options, hidden);
    }

    public static IReadOnlyList<WallPair> Detect(IEnumerable<DxfEntity> entities, WallPairOptions options)
    {
        ArgumentNullException.ThrowIfNull(entities);
        Validate(options);
        return Run(entities, options, null);
    }

    private static IReadOnlyList<WallPair> Run(IEnumerable<DxfEntity> entities, WallPairOptions options, HashSet<string>? hidden)
    {
        var layers = options.Layers is null ? null : new HashSet<string>(options.Layers, StringComparer.OrdinalIgnoreCase);
        var segments = new List<Segment>();
        var order = 0;
        foreach (var entity in entities)
        {
            order++;
            var source = entity.Provenance;
            if (layers is not null && !layers.Contains(source.Layer)) continue;
            if (hidden is not null && hidden.Contains(source.Layer)) continue;
            if ((source.PaperSpace && !options.IncludePaperSpace) || (source.Invisible && !options.IncludeInvisible)) continue;
            switch (entity)
            {
                case DxfLine line:
                    AddSegment(segments, new WallSide(line.Provenance, null, line.Start, line.End), order, 0, options);
                    break;
                case DxfPolyline polyline:
                    foreach (var segment in polyline.Segments())
                    {
                        if (!segment.IsArc) AddSegment(segments, new WallSide(polyline.Provenance, segment.Index, segment.Start, segment.End), order, segment.Index, options);
                    }
                    break;
                default:
                    break;
            }
        }
        var tolerance = options.AngleToleranceDegrees;
        var binCount = Math.Max(2, (int)Math.Floor(180 / tolerance));
        var width = 180.0 / binCount;
        var bins = new List<Segment>[binCount];
        for (var b = 0; b < binCount; b++) bins[b] = new List<Segment>();
        foreach (var segment in segments) bins[Math.Min(binCount - 1, (int)(segment.Angle / width))].Add(segment);
        var pairs = new List<(WallPair Pair, Segment A, Segment B)>();
        long comparisons = 0;
        var reach = options.MaxThickness;
        for (var b = 0; b < binCount; b++)
        {
            // With two bins, one group holds every segment; otherwise each group is bin b plus its next neighbour and
            // pairs lying wholly in the neighbour are left to that neighbour's own group.
            if (binCount == 2 && b == 1) break;
            var next = (b + 1) % binCount;
            var group = new List<(Segment S, bool InNext, double Low, double High)>(bins[b].Count + bins[next].Count);
            var radians = (b + 1) * width * Math.PI / 180;
            var reference = new Point2(Math.Cos(radians), Math.Sin(radians));
            foreach (var segment in bins[b]) group.Add(Offsets(segment, reference, false));
            foreach (var segment in bins[next]) group.Add(Offsets(segment, reference, binCount > 2));
            if (group.Count < 2) continue;
            group.Sort((x, y) => x.Low != y.Low ? x.Low.CompareTo(y.Low) : x.S.CompareOrder(y.S));
            for (var i = 0; i < group.Count; i++)
            {
                var limit = group[i].High + reach;
                for (var j = i + 1; j < group.Count && group[j].Low <= limit; j++)
                {
                    if (++comparisons > options.MaxComparisons) throw Limit("more than " + options.MaxComparisons.ToString(CultureInfo.InvariantCulture) + " segment comparisons");
                    if (group[i].InNext && group[j].InNext) continue;
                    Consider(group[i].S, group[j].S, options, pairs);
                    if (pairs.Count > options.MaxPairs) throw Limit("more than " + options.MaxPairs.ToString(CultureInfo.InvariantCulture) + " wall pairs");
                }
            }
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

    private static (Segment, bool, double, double) Offsets(Segment segment, Point2 reference, bool inNext)
    {
        var a = Cross(reference, segment.Side.Start);
        var b = Cross(reference, segment.Side.End);
        return (segment, inNext, Math.Min(a, b), Math.Max(a, b));
    }

    private static DxfFormatException Limit(string what) => new("detect-limit", 0, "wall-pair detection stopped: " + what);

    private static void Validate(WallPairOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.MinThickness) || options.MinThickness < 0) throw new ArgumentException("MinThickness must be a finite non-negative length", nameof(options));
        if (!double.IsFinite(options.MaxThickness) || options.MaxThickness < options.MinThickness || !(options.MaxThickness > 0)) throw new ArgumentException("MaxThickness must be finite, positive and at least MinThickness", nameof(options));
        if (!double.IsFinite(options.MinOverlap) || options.MinOverlap < 0) throw new ArgumentException("MinOverlap must be a finite non-negative length", nameof(options));
        if (!(options.AngleToleranceDegrees > 0) || !(options.AngleToleranceDegrees < 90)) throw new ArgumentException("AngleToleranceDegrees must be in (0, 90)", nameof(options));
        if (options.MaxSegments < 1 || options.MaxPairs < 1 || options.MaxComparisons < 1) throw new ArgumentException("MaxSegments, MaxPairs and MaxComparisons must be positive", nameof(options));
    }

    private static void AddSegment(List<Segment> segments, WallSide side, int order, int index, WallPairOptions options)
    {
        var v = side.End - side.Start;
        var length = v.Length;
        if (!(length > 0) || !double.IsFinite(length) || !double.IsFinite(side.Start.X) || !double.IsFinite(side.Start.Y)) return;
        var angle = Math.Atan2(v.Y, v.X) * 180 / Math.PI % 180;
        if (angle < 0) angle += 180;
        if (angle >= 180) angle = 0;
        if (segments.Count >= options.MaxSegments) throw Limit("more than " + options.MaxSegments.ToString(CultureInfo.InvariantCulture) + " candidate segments");
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
