using System.Globalization;
namespace Bim.Cad;

// Maps PDF page coordinates to real millimetres: a uniform scale (MillimetresPerUnit, from a known dimension) composed
// with an optional alignment (translate and rotate so a chosen reference segment starts at a target origin and points
// along a target angle). Transform is the whole page-to-millimetre map; RotationDegrees is its rotation in [0, 360).
public sealed record PdfCalibration(double MillimetresPerUnit, PdfMatrix Transform)
{
    public double RotationDegrees => PdfGeometry.AngleDegrees(Transform.A, Transform.B);

    public Point2 Apply(Point2 point) => Transform.Apply(point);

    // Scale from two page points whose real distance is known: mm per PDF unit = realLength / |second - first|.
    // The transform is that scale about the page origin (no translation or rotation).
    public static PdfCalibration FromKnownDimension(Point2 first, Point2 second, double realLengthMillimetres)
    {
        if (!IsFinite(first) || !IsFinite(second)) throw new ArgumentException("calibration points must be finite", nameof(second));
        var units = (second - first).Length;
        if (!(units > 0) || !double.IsFinite(units)) throw new ArgumentException("calibration points must be distinct", nameof(second));
        if (!(realLengthMillimetres > 0) || !double.IsFinite(realLengthMillimetres))
            throw new ArgumentOutOfRangeException(nameof(realLengthMillimetres), "the real length must be a positive finite number of millimetres");
        var scale = realLengthMillimetres / units;
        if (!(scale > 0) || !double.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(realLengthMillimetres), "the calibration scale is not a positive finite number");
        return new PdfCalibration(scale, PdfMatrix.Scaling(scale));
    }

    // Keeps the scale and replaces the alignment: referenceStart maps to targetOrigin (mm) and the direction from
    // referenceStart to referenceEnd maps to targetAngleDegrees (0 = +X, counter-clockwise).
    public PdfCalibration AlignTo(Point2 referenceStart, Point2 referenceEnd, double targetAngleDegrees = 0, Point2 targetOrigin = default)
    {
        if (!IsFinite(referenceStart) || !IsFinite(referenceEnd)) throw new ArgumentException("reference points must be finite", nameof(referenceEnd));
        var direction = referenceEnd - referenceStart;
        if (!(direction.Length > 0) || !double.IsFinite(direction.Length)) throw new ArgumentException("the reference segment must have a positive finite length", nameof(referenceEnd));
        if (!double.IsFinite(targetAngleDegrees)) throw new ArgumentOutOfRangeException(nameof(targetAngleDegrees), "the target angle must be finite");
        if (!IsFinite(targetOrigin)) throw new ArgumentException("the target origin must be finite", nameof(targetOrigin));
        var referenceAngle = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
        var transform = PdfMatrix.Translation(-referenceStart.X, -referenceStart.Y)
            .Then(PdfMatrix.Rotation(targetAngleDegrees - referenceAngle))
            .Then(PdfMatrix.Scaling(MillimetresPerUnit))
            .Then(PdfMatrix.Translation(targetOrigin.X, targetOrigin.Y));
        return this with { Transform = transform };
    }

    private static bool IsFinite(Point2 point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}

// PDF page elements as DXF drawing-model entities. Each subpath becomes a DxfLine (one open straight segment) or a
// DxfPolyline (curves flattened, closed when the subpath is closed); each text becomes a DxfText. Provenance: the PDF
// source file, Handle "p{page}:o{object}:i{operator index}" (followed by "@" and the Form XObject invocation chain joined
// with ">" when the element was drawn by a form), InsertPath = that chain, Layer "PDF-P{page}", EntityType PDF-PATH or
// PDF-TEXT, and Line = operator index + 1.
internal static class PdfToDxf
{
    public static IReadOnlyList<DxfEntity> Convert(PdfPage page, PdfCalibration? calibration, int curveSegments)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (curveSegments < 1 || curveSegments > 1024) throw new ArgumentOutOfRangeException(nameof(curveSegments), "curveSegments must be in [1, 1024]");
        var transform = calibration?.Transform ?? PdfMatrix.Identity;
        var scale = calibration?.MillimetresPerUnit ?? 1;
        var rotation = calibration?.RotationDegrees ?? 0;
        var result = new List<DxfEntity>();
        foreach (var element in page.Elements)
        {
            var source = element.Provenance;
            var handle = "p" + I(source.Page) + ":o" + I(source.ObjectNumber) + ":i" + I(source.OperatorIndex)
                + (source.XObjectPath.Count > 0 ? "@" + string.Join(">", source.XObjectPath) : "");
            var layer = "PDF-P" + I(source.Page);
            if (element is PdfPath path)
            {
                var provenance = new DxfProvenance(source.SourceFile, handle, layer, "PDF-PATH", source.OperatorIndex + 1, source.XObjectPath);
                foreach (var subpath in path.Subpaths)
                {
                    var points = subpath.Flatten(curveSegments).Select(transform.Apply).ToList();
                    if (!subpath.Closed && points.Count == 2)
                    {
                        result.Add(new DxfLine(provenance, points[0], points[1]));
                        continue;
                    }
                    if (subpath.Closed && points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
                    if (points.Count < 2) continue;
                    result.Add(new DxfPolyline(provenance, points.Select(p => new DxfVertex(p, 0)).ToList(), subpath.Closed));
                }
            }
            else if (element is PdfText text)
            {
                var provenance = new DxfProvenance(source.SourceFile, handle, layer, "PDF-TEXT", source.OperatorIndex + 1, source.XObjectPath);
                result.Add(new DxfText(provenance, transform.Apply(text.Origin), text.Height * scale, PdfGeometry.NormalizeDegrees(text.RotationDegrees + rotation), text.Value, text.Value, false));
            }
        }
        return result;
    }

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
