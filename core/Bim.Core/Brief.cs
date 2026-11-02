namespace Bim.Core;

// Project brief v1: the architectural programme (rooms, areas, adjacencies, global constraints) extracted from a
// user prompt or a programme document. A new input format, separate from BIM-IR. Areas are square metres, lengths
// metres, levels 0-based above-ground indices. Every extracted value may carry a provenance record saying where it
// came from and how sure the extraction is. Contract: docs/contracts/brief.schema.json.
// Parse -> Validate -> plan: BriefParser checks structure, BriefValidator checks meaning, BriefSerializer writes the
// canonical form.
public enum BriefSource { User, Document, Inferred }

public enum BriefAdjacencyKind { Required, Preferred, Forbidden }

public enum BriefSeverity { Error, Warning }

// Confidence is 0..1. Reference is free text pointing at the source (file name, quoted sentence); Page is 1-based.
public sealed record BriefProvenance(BriefSource Source, double Confidence, string? Reference = null, int? Page = null);

// An extracted value with optional provenance. In JSON it is either the bare value or {"value": v, "provenance": {...}}.
public sealed record BriefValue<T>(T Value, BriefProvenance? Provenance = null) where T : notnull;

public sealed record BriefSite(BriefValue<double> WidthMetres, BriefValue<double> DepthMetres);

public sealed record BriefConstraints(BriefValue<string>? Jurisdiction = null, BriefSite? Site = null, BriefValue<int>? Levels = null,
    BriefValue<double>? TotalAreaBudgetSquareMetres = null);

// Count defaults to 1 when absent. PreferredLevel is a 0-based level index.
public sealed record BriefRoom(string Id, string Name, BriefValue<int>? Count = null, BriefValue<double>? TargetAreaSquareMetres = null,
    BriefValue<double>? MinAreaSquareMetres = null, BriefValue<double>? MaxAreaSquareMetres = null, BriefValue<int>? PreferredLevel = null,
    BriefProvenance? Provenance = null)
{
    public int EffectiveCount => Count?.Value ?? 1;
}

// Adjacency is symmetric: (A, B) and (B, A) state the same relation.
public sealed record BriefAdjacency(string A, string B, BriefAdjacencyKind Kind, BriefProvenance? Provenance = null);

public sealed record ProjectBrief(int Version, string Name, BriefConstraints Constraints, IReadOnlyList<BriefRoom> Rooms, IReadOnlyList<BriefAdjacency> Adjacencies)
{
    public const int CurrentVersion = 1;
}

// Field is the JSON path of the offending value, for example "rooms[2].min_area_m2".
public sealed record BriefIssue(string Code, string Field, BriefSeverity Severity, string Detail);

public static class BriefCodes
{
    // Parser refusals (BriefException).
    public const string InputTooLarge = "input_too_large";
    public const string JsonInvalid = "json_invalid";
    public const string VersionUnsupported = "version_unsupported";
    public const string MemberUnknown = "member_unknown";
    public const string MemberDuplicate = "member_duplicate";
    public const string MemberMissing = "member_missing";
    public const string TypeInvalid = "type_invalid";
    public const string ValueInvalid = "value_invalid";
    // Validator issues (BriefIssue).
    public const string NameBlank = "name_blank";
    public const string TextInvalid = "text_invalid";
    public const string JurisdictionBlank = "jurisdiction_blank";
    public const string RoomsEmpty = "rooms_empty";
    public const string RoomIdInvalid = "room_id_invalid";
    public const string RoomIdDuplicate = "room_id_duplicate";
    public const string CountInvalid = "count_invalid";
    public const string AreaNonPositive = "area_non_positive";
    public const string AreaRangeInverted = "area_range_inverted";
    public const string AreaTargetOutsideRange = "area_target_outside_range";
    public const string LevelsInvalid = "levels_invalid";
    public const string LevelOutOfRange = "level_out_of_range";
    public const string SiteDimensionNonPositive = "site_dimension_non_positive";
    public const string BudgetNonPositive = "budget_non_positive";
    public const string BudgetExceeded = "budget_exceeded";
    public const string ConfidenceOutOfRange = "confidence_out_of_range";
    public const string PageInvalid = "page_invalid";
    public const string AdjacencyRoomUnknown = "adjacency_room_unknown";
    public const string AdjacencySelf = "adjacency_self";
    public const string AdjacencyDuplicate = "adjacency_duplicate";
    public const string AdjacencyContradictory = "adjacency_contradictory";
}

// A typed parser refusal. Message equals Code; Field is the JSON path of the offending value or null for the document.
public sealed class BriefException : FormatException
{
    public BriefException(string code, string? field = null, string? detail = null) : base(code)
    {
        Code = code;
        Field = field;
        Detail = detail;
    }

    public string Code { get; }
    public string? Field { get; }
    public string? Detail { get; }
}
