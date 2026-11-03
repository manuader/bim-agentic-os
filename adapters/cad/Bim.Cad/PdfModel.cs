using System.Globalization;
namespace Bim.Cad;

// Provenance-tagged 2D vector model read from PDF page content streams. Like the DXF model it is a CAD input model,
// separate from BIM-IR: only what the page draws, plus where each fact came from. Coordinates are in PDF default user
// space (1/72 inch at 100 % print scale) after the current transformation matrix, so they are page coordinates before
// /Rotate and before any calibration (see PdfCalibration). Angles are degrees, counter-clockwise, in [0, 360).

// A 2D affine matrix in PDF order [a b c d e f]: x' = a x + c y + e, y' = b x + d y + f (row vector times matrix).
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity => new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static PdfMatrix Scaling(double scale) => new(scale, 0, 0, scale, 0, 0);

    // Counter-clockwise rotation; multiples of 90 degrees are exact.
    public static PdfMatrix Rotation(double degrees)
    {
        var turns = degrees / 90;
        if (Math.Abs(turns - Math.Round(turns)) < 1e-12)
        {
            var quarter = (int)(((long)Math.Round(turns) % 4 + 4) % 4);
            return quarter switch
            {
                0 => Identity,
                1 => new PdfMatrix(0, 1, -1, 0, 0, 0),
                2 => new PdfMatrix(-1, 0, 0, -1, 0, 0),
                _ => new PdfMatrix(0, -1, 1, 0, 0, 0),
            };
        }
        var radians = degrees * Math.PI / 180;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new PdfMatrix(cos, sin, -sin, cos, 0, 0);
    }

    // This transform followed by next (the PDF product this x next); "a b c d e f cm" sets CTM = M.Then(CTM).
    public PdfMatrix Then(PdfMatrix next) => new(
        A * next.A + B * next.C, A * next.B + B * next.D,
        C * next.A + D * next.C, C * next.B + D * next.D,
        E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);

    public Point2 Apply(Point2 point) => new(A * point.X + C * point.Y + E, B * point.X + D * point.Y + F);

    public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);
}

// Where an element came from: source file name, 1-based page number, the object number of the content stream (or Form
// XObject) that holds the operator, the 0-based index of the operator within that stream, and the operator itself
// (the painting operator of a path, the showing operator of a text).
public sealed record PdfProvenance(string SourceFile, int Page, int ObjectNumber, int OperatorIndex, string Operator);

public enum PdfSegmentKind { Line, Cubic }

// One path segment in page coordinates. For a line Control1 = Start and Control2 = End. OperatorIndex/Operator name the
// constructing operator (l, c, v, y, re, h, or s/b/b* for the implicit close of a painting operator).
public sealed record PdfSegment(PdfSegmentKind Kind, Point2 Start, Point2 Control1, Point2 Control2, Point2 End, int OperatorIndex, string Operator);

// A subpath from m (or re). Closed when h, re, s, b or b* closed it; the closing segment, when it has length, is the
// last segment.
public sealed record PdfSubpath(Point2 Start, IReadOnlyList<PdfSegment> Segments, bool Closed)
{
    // The start point followed by every segment end; each cubic contributes curveSegments points at equal t steps.
    public IReadOnlyList<Point2> Flatten(int curveSegments = 16) => PdfGeometry.Flatten(this, curveSegments);
}

public abstract record PdfElement(PdfProvenance Provenance);

// A painted path. Paths ended with n (including clipping paths) are not reported. LineWidth is the w operand in user
// space at painting time (not scaled by the CTM).
public sealed record PdfPath(PdfProvenance Provenance, IReadOnlyList<PdfSubpath> Subpaths, bool Stroked, bool Filled, bool EvenOdd, double LineWidth) : PdfElement(Provenance);

// A shown string. Origin is the text-space origin through the text matrix (with rise) and the CTM. Glyph widths are not
// computed (no font metrics), so consecutive strings without repositioning share the origin of the first. Value is
// decoded as UTF-16BE (with BOM), UTF-8 (with BOM) or else byte-per-character Latin-1; CID fonts are not mapped.
// Height is FontSize scaled by the text matrix and CTM; RotationDegrees is the direction of the text baseline.
public sealed record PdfText(PdfProvenance Provenance, Point2 Origin, string Value, string FontName, double FontSize, double Height, double RotationDegrees) : PdfElement(Provenance);

// Normalised rectangle (Left <= Right, Bottom <= Top) in default user space.
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top);

// One page in page-tree order. MediaBox and Rotate are inherited through the page tree; Rotate is normalised to
// [0, 360). Elements are in content order (Form XObject elements where the Do operator occurs).
public sealed record PdfPage(int Number, int ObjectNumber, PdfRect MediaBox, int Rotate, IReadOnlyList<PdfElement> Elements)
{
    // The page in the DXF drawing model (lines, polylines, text) so DXF consumers such as WallPairDetector can use it.
    public IReadOnlyList<DxfEntity> ToDxfEntities(PdfCalibration? calibration = null, int curveSegments = 16) => PdfToDxf.Convert(this, calibration, curveSegments);
}

// A recoverable problem. Page is 0 and OperatorIndex -1 for file-level warnings.
public sealed record PdfWarning(string Code, int Page, int ObjectNumber, int OperatorIndex, string Message);

public sealed record PdfDocument(string SourceFile, string Version, IReadOnlyList<PdfPage> Pages, IReadOnlyList<PdfWarning> Warnings);

// Bounds for one read. MaxDepth bounds object nesting, q/Q nesting, page-tree depth and Form XObject nesting;
// MaxOperators counts content operators over the whole document; MaxDecodedBytes counts decompressed bytes in total.
public sealed record PdfReadOptions(
    long MaxBytes = 64L * 1024 * 1024,
    int MaxObjects = 200_000,
    int MaxOperators = 2_000_000,
    int MaxDepth = 32,
    long MaxDecodedBytes = 256L * 1024 * 1024,
    int MaxPages = 10_000,
    int MaxWarnings = 1_000);

// Thrown for input this reader refuses: Code is a stable identifier (empty, too-large, not-pdf, encrypted, no-pages,
// object-limit, operator-limit, depth-limit, decode-limit, page-limit), Offset the byte offset in the file or in the
// decoded stream where it was found (-1 when not tied to an offset).
public sealed class PdfFormatException : Exception
{
    public PdfFormatException(string code, long offset, string message) : base(message + (offset >= 0 ? " (offset " + offset.ToString(CultureInfo.InvariantCulture) + ")" : ""))
    {
        Code = code;
        Offset = offset;
    }

    public string Code { get; }
    public long Offset { get; }
}

internal static class PdfGeometry
{
    public static Point2 Cubic(Point2 p0, Point2 p1, Point2 p2, Point2 p3, double t)
    {
        var u = 1 - t;
        return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
    }

    public static IReadOnlyList<Point2> Flatten(PdfSubpath subpath, int curveSegments)
    {
        ArgumentNullException.ThrowIfNull(subpath);
        if (curveSegments < 1 || curveSegments > 1024) throw new ArgumentOutOfRangeException(nameof(curveSegments), "curveSegments must be in [1, 1024]");
        var points = new List<Point2>(subpath.Segments.Count + 1) { subpath.Start };
        foreach (var segment in subpath.Segments)
        {
            if (segment.Kind == PdfSegmentKind.Line) { points.Add(segment.End); continue; }
            for (var i = 1; i < curveSegments; i++) points.Add(Cubic(segment.Start, segment.Control1, segment.Control2, segment.End, (double)i / curveSegments));
            points.Add(segment.End);
        }
        return points;
    }

    public static double NormalizeDegrees(double degrees)
    {
        if (!double.IsFinite(degrees)) return 0;
        var value = degrees % 360;
        if (value < 0) value += 360;
        if (value >= 360 || Math.Abs(value - 360) < 1e-12) value = 0;
        return Math.Abs(value) < 1e-12 ? 0 : value;
    }

    public static double AngleDegrees(double x, double y) => NormalizeDegrees(Math.Atan2(y, x) * 180 / Math.PI);
}
