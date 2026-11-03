namespace Bim.Cad;

// Provenance-tagged 2D drawing model read from an ASCII DXF file. It is a CAD input model, separate from BIM-IR:
// nothing here is a building element, only what the file says, plus where in the file each fact came from.
// Coordinates are in drawing units (see DxfDrawing.Units / MetresPerUnit); angles are degrees, counter-clockwise.

public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

// Where an entity came from: source file name, DXF handle (group 5; null when the file has none), effective layer,
// DXF entity type, and the 1-based line of the entity's "0" group code. For entities produced by exploding INSERTs,
// InsertPath lists the handles of the INSERT chain from the outermost insert inwards, and Layer is the effective
// layer (an entity on layer "0" inside a block takes the layer of the insert that places it).
public sealed record DxfProvenance(string SourceFile, string? Handle, string Layer, string EntityType, int Line, IReadOnlyList<string> InsertPath)
{
    public DxfProvenance(string sourceFile, string? handle, string layer, string entityType, int line)
        : this(sourceFile, handle, layer, entityType, line, Array.Empty<string>()) { }
}

public abstract record DxfEntity(DxfProvenance Provenance);

public sealed record DxfLine(DxfProvenance Provenance, Point2 Start, Point2 End) : DxfEntity(Provenance);

// Arc counter-clockwise from StartAngleDegrees to EndAngleDegrees, both normalised to [0, 360).
public sealed record DxfArc(DxfProvenance Provenance, Point2 Center, double Radius, double StartAngleDegrees, double EndAngleDegrees) : DxfEntity(Provenance);

public sealed record DxfCircle(DxfProvenance Provenance, Point2 Center, double Radius) : DxfEntity(Provenance);

// LWPOLYLINE vertex; Bulge is the tangent of a quarter of the included angle of the segment that starts at this
// vertex (0 straight, positive counter-clockwise, 1 a semicircle).
public readonly record struct DxfVertex(Point2 Point, double Bulge);

public sealed record DxfPolyline(DxfProvenance Provenance, IReadOnlyList<DxfVertex> Vertices, bool Closed) : DxfEntity(Provenance)
{
    public IReadOnlyList<DxfPolylineSegment> Segments() => DxfGeometry.Segments(this);
}

// One polyline segment. For an arc segment (non-zero bulge) Center, Radius and the counter-clockwise angle span are set.
public sealed record DxfPolylineSegment(int Index, Point2 Start, Point2 End, double Bulge, Point2? Center, double Radius, double StartAngleDegrees, double EndAngleDegrees)
{
    public bool IsArc => Center is not null;
    public Point2 Midpoint => DxfGeometry.SegmentMidpoint(this);
}

// TEXT or MTEXT. Value is the raw text (MTEXT chunks from groups 3 then 1 concatenated); PlainText removes MTEXT
// inline formatting (\P becomes a newline). Insertion is group 10/20.
public sealed record DxfText(DxfProvenance Provenance, Point2 Insertion, double Height, double RotationDegrees, string Value, string PlainText, bool IsMText) : DxfEntity(Provenance);

public sealed record DxfInsert(DxfProvenance Provenance, string BlockName, Point2 Insertion, double ScaleX, double ScaleY, double RotationDegrees) : DxfEntity(Provenance);

public sealed record DxfLayer(string Name, int Color, string LineType, bool Frozen, bool Off, bool Declared);

public sealed record DxfBlock(string Name, Point2 BasePoint, string? Handle, int Line, IReadOnlyList<DxfEntity> Entities);

public sealed record DxfWarning(string Code, int Line, string Message);

public enum DxfUnits
{
    Unspecified = -1,
    Unitless = 0, Inches = 1, Feet = 2, Miles = 3, Millimetres = 4, Centimetres = 5, Metres = 6, Kilometres = 7,
    Microinches = 8, Mils = 9, Yards = 10, Angstroms = 11, Nanometres = 12, Microns = 13, Decimetres = 14,
    Decametres = 15, Hectometres = 16, Gigametres = 17, AstronomicalUnits = 18, LightYears = 19, Parsecs = 20,
    UsSurveyFeet = 21,
    Unknown = 999,
}

// The whole file. Entities are the ENTITIES section in file order; Blocks are sorted by name (ordinal); Layers are
// the LAYER table entries plus any layer referenced by an entity but not declared (Declared = false), sorted by name
// (ordinal). InsUnits is the raw $INSUNITS value (null when absent); MetresPerUnit is null when the units are
// unspecified, unitless or unknown. Unsupported lists entities of types this reader does not model.
public sealed record DxfDrawing(
    string SourceFile,
    int? InsUnits,
    DxfUnits Units,
    double? MetresPerUnit,
    IReadOnlyList<DxfLayer> Layers,
    IReadOnlyList<DxfBlock> Blocks,
    IReadOnlyList<DxfEntity> Entities,
    IReadOnlyList<DxfProvenance> Unsupported,
    IReadOnlyList<DxfWarning> Warnings)
{
    public DxfExplodedDrawing Explode(DxfExplodeOptions? options = null) => DxfExploder.Explode(this, options ?? new DxfExplodeOptions());
}

public sealed record DxfExplodeOptions(int MaxDepth = 32, int MaxEntities = 1_000_000, int ArcTessellationSegments = 32);

// Model-space entities with every INSERT replaced, recursively, by its block's entities in world coordinates.
public sealed record DxfExplodedDrawing(IReadOnlyList<DxfEntity> Entities, IReadOnlyList<DxfWarning> Warnings);

public sealed record DxfReadOptions(long MaxBytes = 256L * 1024 * 1024);

// Thrown for input that is not a readable ASCII DXF: Code is a stable identifier (binary-dxf, empty, too-large,
// malformed-group-code, missing-value, bad-number, malformed-structure, unterminated-section, missing-eof,
// explode-limit), Line the
// 1-based line where the problem was found (0 when not tied to a line).
public sealed class DxfFormatException : Exception
{
    public DxfFormatException(string code, int line, string message) : base(message + (line > 0 ? " (line " + line.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" : ""))
    {
        Code = code;
        Line = line;
    }

    public string Code { get; }
    public int Line { get; }
}
