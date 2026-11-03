namespace Bim.Cad;

// Wall-pair detection options, all lengths in drawing units. A pair is two straight segments (LINEs or straight
// LWPOLYLINE segments, after exploding INSERTs) that are parallel within AngleToleranceDegrees, whose perpendicular
// separation lies in [MinThickness, MaxThickness] and whose overlap along the first segment's direction is at least
// MinOverlap. Layers, when set, restricts candidates to those layer names (case-insensitive).
public sealed record WallPairOptions(double MinThickness, double MaxThickness, double MinOverlap, double AngleToleranceDegrees = 1.0, IReadOnlyCollection<string>? Layers = null)
{
    // Builds options from metric values for a drawing whose unit is metresPerUnit metres.
    public static WallPairOptions FromMetres(double minThicknessMetres, double maxThicknessMetres, double minOverlapMetres, double metresPerUnit, double angleToleranceDegrees = 1.0, IReadOnlyCollection<string>? layers = null)
    {
        if (!(metresPerUnit > 0) || double.IsInfinity(metresPerUnit)) throw new ArgumentOutOfRangeException(nameof(metresPerUnit), "metresPerUnit must be a positive finite number");
        return new WallPairOptions(minThicknessMetres / metresPerUnit, maxThicknessMetres / metresPerUnit, minOverlapMetres / metresPerUnit, angleToleranceDegrees, layers);
    }
}

// One side of a candidate wall: the source entity's provenance, the polyline segment index (null for a LINE) and the
// segment in world coordinates.
public sealed record WallSide(DxfProvenance Source, int? SegmentIndex, Point2 Start, Point2 End);

// A candidate wall. A is the side that appears first in the drawing (exploded entity order, then segment index).
// Thickness is the perpendicular distance from B, at the middle of the overlap, to A's line; Overlap is measured
// along A's direction; the centreline runs over the overlap, midway between the sides. Confidence in (0, 1] is
// overlap / longer side length multiplied by (1 - 0.5 * angle deviation / tolerance).
public sealed record WallPair(WallSide A, WallSide B, double Thickness, double Overlap, double AngleDeviationDegrees, double Confidence, Point2 CenterlineStart, Point2 CenterlineEnd);
