using System.Globalization;
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

    // Group 67 = 1: the entity is in paper space (for exploded entities, also when any enclosing INSERT is).
    public bool PaperSpace { get; init; }

    // Group 60 = 1: the entity is invisible (for exploded entities, also when any enclosing INSERT is).
    public bool Invisible { get; init; }

    // Why an entry of DxfDrawing.Unsupported is unsupported when its type is otherwise modelled: "ocs-non-planar" (its
    // extrusion direction puts it in a plane that is not horizontal) or "ocs-invalid" (zero extrusion vector). Null
    // for entities of unmodelled types and for every modelled entity.
    public string? Reason { get; init; }
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

// A MINSERT is an INSERT with more than one column (group 70) or row (group 71): cell (row, column) is the block
// placed with an extra offset (column * ColumnSpacing, row * RowSpacing) (groups 44, 45) applied after the scale and
// before the rotation, so the array turns with the insert but its spacing is not scaled. For an insert read from the
// mirrored OCS (0,0,-1) ColumnSpacing is negated like ScaleX. Exploding writes each cell's insert-path element as
// "key[row,column]" (0-based; key is the handle, or "@" + line; see DxfInsertPath); a 1x1 insert keeps the plain key.
// Exploded ATTRIBs of every cell keep the bare key as InsertHandle; the cell is only in their InsertPath.
public sealed record DxfInsert(DxfProvenance Provenance, string BlockName, Point2 Insertion, double ScaleX, double ScaleY, double RotationDegrees) : DxfEntity(Provenance)
{
    public int Columns { get; init; } = 1;
    public int Rows { get; init; } = 1;
    public double ColumnSpacing { get; init; }
    public double RowSpacing { get; init; }
    public bool IsArray => Columns > 1 || Rows > 1;

    // The ATTRIBs that follow the INSERT (group 66 = 1) up to its SEQEND, in file order.
    public IReadOnlyList<DxfAttribute> Attributes { get; init; } = Array.Empty<DxfAttribute>();
}

// ATTRIB (an attribute value of an INSERT) or ATTDEF (IsDefinition: an attribute definition in a block, with its
// Prompt and default Value). Tag is group 2; Value the raw group 1 and PlainText its plain form; Flags group 70
// (1 invisible, 2 constant, 4 verify, 8 preset). Insertion is the alignment point 11/21 when the text is justified,
// else 10/20. InsertHandle is the handle of the INSERT the attribute belongs to (null for an ATTDEF in a block or
// in model space).
public sealed record DxfAttribute(DxfProvenance Provenance, string Tag, Point2 Insertion, double Height, double RotationDegrees, string Value, string PlainText, bool IsDefinition) : DxfEntity(Provenance)
{
    public string? Prompt { get; init; }
    public int Flags { get; init; }
    public string? InsertHandle { get; init; }
    // Name of the block the owning INSERT places (null like InsertHandle).
    public string? BlockName { get; init; }
    public bool IsConstant => (Flags & 2) != 0;
}

// Elements of DxfProvenance.InsertPath: an INSERT's key (its handle, or "@" + its line when it has none) or, for one
// cell of a MINSERT array, "key[row,column]" (0-based). Key gives the INSERT key of either form.
public static class DxfInsertPath
{
    public static string Cell(string key, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key + "[" + row.ToString(CultureInfo.InvariantCulture) + "," + column.ToString(CultureInfo.InvariantCulture) + "]";
    }

    // The INSERT key of a path element: the element without a trailing "[row,column]".
    public static string Key(string element) => TryParseCell(element, out var key, out _, out _) ? key : element;

    // True when the element is a MINSERT cell "key[row,column]".
    public static bool TryParseCell(string element, out string key, out int row, out int column)
    {
        ArgumentNullException.ThrowIfNull(element);
        key = element;
        row = column = 0;
        if (!element.EndsWith(']')) return false;
        var open = element.LastIndexOf('[');
        if (open <= 0) return false;
        var inner = element.AsSpan(open + 1, element.Length - open - 2);
        var comma = inner.IndexOf(',');
        if (comma < 0
            || !int.TryParse(inner[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out var r)
            || !int.TryParse(inner[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var c)) return false;
        key = element[..open];
        row = r;
        column = c;
        return true;
    }
}

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
// the LAYER table entries plus any layer referenced by an entity but not declared (Declared = false); names match
// case-insensitively, as in DXF, and the list is sorted by name
// (ordinal). InsUnits is the raw $INSUNITS value (null when absent); MetresPerUnit is null when the units are
// unspecified, unitless or unknown. Unsupported lists entities of types this reader does not model, and modelled
// entities whose extrusion direction makes them non-planar in the plan (with a Reason).
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

// MaxWork bounds the total work of one explode: emitted entities, visited INSERTs and warnings all count.
public sealed record DxfExplodeOptions(int MaxDepth = 32, int MaxEntities = 1_000_000, int ArcTessellationSegments = 32, int MaxWork = 2_000_000);

// Model-space entities with every INSERT replaced, recursively, by its block's entities in world coordinates.
public sealed record DxfExplodedDrawing(IReadOnlyList<DxfEntity> Entities, IReadOnlyList<DxfWarning> Warnings);

public sealed record DxfReadOptions(long MaxBytes = 256L * 1024 * 1024);

// Thrown for input that is not a readable ASCII DXF: Code is a stable identifier (binary-dxf, empty, too-large,
// malformed-group-code, missing-value, bad-number, malformed-structure, unterminated-section, missing-eof,
// explode-limit; detect-limit from wall-pair detection), Line the
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
