namespace Bim.Core;

// P2-01 Revit -> BIM-IR snapshot ("mirror"): the read-only query.snapshot catalogue entry and the neutral observed
// records the add-in collector fills. Vendor-free like every core file: the add-in reads Revit facts, converts lengths
// to millimetres with its unit utilities, and hands these records to MirrorSnapshotMapper (MirrorMapper.cs).
// Nothing here changes the BIM-IR schema or any persisted format: the snapshot is a query reply, never stored.

// A plan point in millimetres, project (internal) coordinates.
public sealed record MirrorPoint(double XMm, double YMm);

// One closed room boundary loop as Revit reports it (finish faces): segment start points in order, whether any
// segment is curved, and the UniqueIds of the elements that bound it ("" when a segment has no element).
public sealed record MirrorLoop(IReadOnlyList<MirrorPoint> Vertices, bool HasCurves, IReadOnlyList<string> BoundingUids);

// A level: UniqueId, name and elevation (Level.Elevation, the convention the batch executor uses), in millimetres.
public sealed record MirrorObservedLevel(string UniqueId, string? Name, double? ElevationMm);

// One observed model element. Category is one of MirrorCategories (anything else is reported unsupported). Members
// that do not apply to the category stay null; a null member that does apply is missing data.
//   wall:   LevelUid, Start/End (straight location line; null when curved), Curved, HeightMm (unconnected height),
//           ThicknessMm (wall width), WallKind (WallType.Kind text: Basic, Curtain or Stacked),
//           LocationLineIsCenterline (WALL_KEY_REF_PARAM is Wall Centerline), BaseOffsetMm (WALL_BASE_OFFSET)
//   any:    Demolished (the element has a demolished phase)
//   door / window: LevelUid, HostUid (host wall), Location (insertion point), WidthMm, HeightMm, SillMm
//   room:   LevelUid, Loops (null or empty when the room is not placed or not enclosed)
//   floor:  LevelUid (no BIM-IR kind exists; always unmapped)
public sealed record MirrorObservedElement(string Category, string UniqueId)
{
    public string? LevelUid { get; init; }
    public string? HostUid { get; init; }
    public MirrorPoint? Start { get; init; }
    public MirrorPoint? End { get; init; }
    public bool Curved { get; init; }
    public double? HeightMm { get; init; }
    public double? ThicknessMm { get; init; }
    public MirrorPoint? Location { get; init; }
    public double? WidthMm { get; init; }
    public double? SillMm { get; init; }
    public IReadOnlyList<MirrorLoop>? Loops { get; init; }
    public string? WallKind { get; init; }
    public bool? LocationLineIsCenterline { get; init; }
    public double? BaseOffsetMm { get; init; }
    public bool Demolished { get; init; }
}

// Everything one query.snapshot call observed. DocumentUid is the ProjectInformation UniqueId of the document.
public sealed record MirrorObservation(string DocumentUid, IReadOnlyList<MirrorObservedLevel> Levels, IReadOnlyList<MirrorObservedElement> Elements);

// An observed element with no BIM-IR element in the snapshot, and why. Not part of BIM-IR and never persisted.
public sealed record MirrorUnmapped(string Category, string UniqueId, string Reason, string Detail);

public static class MirrorCategories
{
    public const string Level = "level";
    public const string Wall = "wall";
    public const string Door = "door";
    public const string Window = "window";
    public const string Floor = "floor";
    public const string Room = "room";
}

public static class MirrorReasons
{
    // BIM-IR has no element kind for this category (floors); adding one needs an owner-accepted ADR.
    public const string NoBimIrKind = "no_bim_ir_kind";
    public const string CategoryUnsupported = "category_unsupported";
    // BIM-IR walls are straight axes [ax,ay,bx,by,height,thickness].
    public const string CurvedWall = "curved_wall_unsupported";
    public const string CurtainWall = "curtain_wall_unsupported";
    public const string StackedWall = "stacked_wall_unsupported";
    // The location curve is the wall axis only when the location line is the wall centerline.
    public const string WallLocationLineNotCenterline = "wall_location_line_not_centerline";
    // BIM-IR walls start at their level.
    public const string WallBaseOffset = "wall_base_offset_unsupported";
    public const string Demolished = "demolished";
    public const string UniqueIdMissing = "unique_id_missing";
    public const string DuplicateUniqueId = "duplicate_unique_id";
    public const string MissingData = "missing_data";
    public const string InvalidGeometry = "invalid_geometry";
    public const string LevelUnknown = "level_unknown";
    public const string HostUnknown = "host_unknown";
    public const string OpeningOutsideHost = "opening_outside_host";
    public const string RoomUnplaced = "room_unplaced";
    // BIM-IR spaces are axis-aligned rectangles [corner x, corner y, width, depth].
    public const string RoomNotRectangular = "room_not_rectangular";
}

public static partial class RevitCommandCatalogue
{
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> MirrorSpecs = new(() => new[]
    {
        new RevitCommandSpec("query.snapshot", false, "Export the open document's walls, doors, windows and rooms (with their levels) as BIM-IR elements in metres with stable ids derived from Revit UniqueIds; straight walls, hosted doors and windows and rectangular rooms are mapped, and everything BIM-IR cannot represent (floors, curved walls, non-rectangular rooms) is listed under unmapped with a reason. Read-only.", EmptySchema, _ => new EmptyQueryArgs()),
    });

    // P2-01: the snapshot query, after v1, v2 and the view commands; P2-05 appends query.journal
    // (RevitCommandsJournal.cs), so the mirror part is query.snapshot then query.journal.
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> MirrorPart = new(() => MirrorSpecs.Value.Concat(Journal).ToArray());
    public static IReadOnlyList<RevitCommandSpec> Mirror => MirrorPart.Value;
}
