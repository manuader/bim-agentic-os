namespace Bim.Core;

// Layout intent v1 (P3-01): a typed AST for a multi-room layout on one or more levels. It is a new input format,
// separate from BIM-IR; IntentCompiler turns it into the existing Project/Element/Provenance records without any
// schema change. All lengths are metres.
//
// Rooms are axis-aligned rectangles given by their wall AXIS lines: X/Y is the south-west axis corner, Width/Depth
// the axis-to-axis size. Two rooms whose sides lie on the same axis line share that stretch as one wall element.
// Keys are stable identities (1-64 of [a-z0-9_-], starting with a letter or digit); names are display text only and
// never reach BIM-IR, so renaming changes nothing compiled.
public enum IntentSide { South, East, North, West }

public enum IntentOpeningKind { Door, Window }

public sealed record IntentLevel(string Key, string Name, double ElevationMetres);

// Height is the default (and, in v1, only) unconnected height of walls of this type.
public sealed record IntentWallType(string Key, string Name, double ThicknessMetres, double HeightMetres);

// Level and WallType are keys. Every side of the room uses WallType.
public sealed record IntentRoom(string Key, string Name, string Level, string WallType,
    double XMetres, double YMetres, double WidthMetres, double DepthMetres);

// Where an opening sits. Offset is always measured along the host from its end with the smaller coordinate
// (west end of a south/north side, south end of an east/west side).
public abstract record IntentHost;

// A side of one room, measured from that side's axis corner. The opening must fit inside one wall segment of the side
// (a side can be split where it is partly shared); a stretch shared with a neighbour resolves to the shared wall.
public sealed record IntentRoomSide(string Room, IntentSide Side) : IntentHost;

// The single wall shared by two adjacent rooms, measured from the shared stretch's lower end. Order is irrelevant.
public sealed record IntentSharedWall(string RoomA, string RoomB) : IntentHost;

// Sill is the height of the opening bottom above the level (a door's sill must be 0); Height is the opening height.
// The opening must fit the host's clear span (axis span minus half the thickness of the perpendicular walls joined at
// each end) and the host height.
// Compiled BIM-IR element: Kind "door" or "window", GeometryMetres [ax,ay,bx,by,sill,height] in metres, where (ax,ay)
// and (bx,by) are the opening's ends on the host wall axis, ordered in the host wall's own direction, then sill and
// height. Dependencies [level, host wall]. GeometryCodec has no arity or typed view for these kinds yet (Geometry.cs).
public sealed record IntentOpening(string Key, IntentOpeningKind Kind, IntentHost Host,
    double OffsetMetres, double WidthMetres, double HeightMetres, double SillMetres);

public sealed record LayoutIntent(int Version, Provenance Provenance, IReadOnlyList<IntentLevel> Levels,
    IReadOnlyList<IntentWallType> WallTypes, IReadOnlyList<IntentRoom> Rooms, IReadOnlyList<IntentOpening> Openings)
{
    public const int CurrentVersion = 1;
    public const int MaxLevels = 64;
    public const int MaxWallTypes = 64;
    public const int MaxRooms = 1024;
    public const int MaxOpenings = 4096;
}

// Key is the intent key the issue is about (for pairs, the ordinally smaller key; Detail names the other).
public sealed record IntentIssue(string Code, string Key, string Detail);

public static class IntentCodes
{
    // Parser refusals (IntentException from IntentJson.Parse).
    public const string InputTooLarge = "input_too_large";
    public const string JsonInvalid = "json_invalid";
    public const string VersionUnsupported = "version_unsupported";
    public const string MemberUnknown = "member_unknown";
    public const string MemberDuplicate = "member_duplicate";
    public const string MemberMissing = "member_missing";
    public const string TypeInvalid = "type_invalid";
    public const string ValueInvalid = "value_invalid";
    // Validation issues (IntentIssue from IntentCompiler.Validate; Compile throws IntentException carrying them).
    public const string LimitExceeded = "limit_exceeded";
    public const string ProvenanceInvalid = "provenance_invalid";
    public const string KeyInvalid = "key_invalid";
    public const string TextInvalid = "text_invalid";
    public const string DuplicateKey = "duplicate_key";
    public const string DimensionInvalid = "dimension_invalid";
    public const string UnknownLevel = "unknown_level";
    public const string UnknownWallType = "unknown_wall_type";
    public const string UnknownRoom = "unknown_room";
    public const string RoomTooSmall = "room_too_small";
    public const string RoomOverlap = "room_overlap";
    public const string WallClash = "wall_clash";
    public const string SharedWallConflict = "shared_wall_conflict";
    public const string NotAdjacent = "not_adjacent";
    public const string OpeningOutsideWall = "opening_outside_wall";
    public const string OpeningOverlap = "opening_overlap";
    public const string DoorSillInvalid = "door_sill_invalid";
}

// Typed refusal. Message equals Code. Parser refusals carry Field (JSON path); compile refusals carry Issues.
public sealed class IntentException : FormatException
{
    public IntentException(string code, string? field = null, string? detail = null, IReadOnlyList<IntentIssue>? issues = null) : base(code)
    {
        Code = code;
        Field = field;
        Detail = detail;
        Issues = issues ?? Array.Empty<IntentIssue>();
    }

    public string Code { get; }
    public string? Field { get; }
    public string? Detail { get; }
    public IReadOnlyList<IntentIssue> Issues { get; }
}
