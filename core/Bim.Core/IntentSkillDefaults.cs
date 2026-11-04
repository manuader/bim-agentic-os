using System.Globalization;
namespace Bim.Core;

// P5-04: resolved skill defaults (P5-03 SkillResolution settings) fill the unspecified fields of a layout intent draft
// BEFORE the P3-01 IntentCompiler runs. The draft is a new in-memory input that mirrors LayoutIntent with the defaultable
// fields made optional; the LayoutIntent format, its JSON and BIM-IR are unchanged, and compilation is the existing
// IntentCompiler.Compile on the filled intent.
//
// Defaultable fields, their fill rule and the dotted skill default keys each rule reads:
//   levels[k].elevation_m     level-elevation-ground (storey 0 -> 0, no default) or
//                             level-elevation-from-storey (storey x level.height_m)
//   wall_types[k].name        wall-type-name-from-role       wall.<role>.type
//   wall_types[k].thickness_m wall-thickness-from-role       wall.<role>.thickness_m
//   wall_types[k].height_m    wall-height-from-level         level.height_m
//   openings[k].sill_m        window-sill-from-style         window.sill_height_m   (windows)
//                             door-sill-on-level (always 0, no default)             (doors)
//   openings[k].height_m      window-height-from-head        window.head_height_m - effective sill (windows)
//                             door-height-from-style         door.height_m          (doors)
// where <role> is exterior or interior. An explicit draft value always wins and is reported with origin "explicit" and
// rule "explicit"; explicit values are passed to the compiler untouched (it validates them as usual).
//
// Every present recognised default is validated, whether or not the draft needs it: numbers must be numbers and text
// non-blank text (default_type_invalid); thicknesses, level height, clear height and head height must be finite and
// positive and the sill non-negative (default_value_invalid); and the style must be self-consistent:
// window.sill_height_m < window.head_height_m, room.min_clear_height_m <= level.height_m and
// window.head_height_m <= room.min_clear_height_m (default_inconsistent). A field the draft leaves open whose default is
// absent is refused as default_missing naming the field (never a silent zero); a derived value that is not positive
// (explicit sill at or above the default head) is derived_value_invalid. A resolution with issues is refused as
// defaults_resolution_failed. Issues are sorted by code, key and detail (ordinal); field sources by field (ordinal).
// Derived numbers are rounded to 1e-9 m so equal inputs give equal bytes.
public enum IntentWallRole { Exterior, Interior }

// Storey is only used when ElevationMetres is null.
public sealed record IntentDraftLevel(string Key, string Name, double? ElevationMetres = null, int Storey = 0);

// A null Name takes the role's wall type default (display text only, it never reaches BIM-IR).
public sealed record IntentDraftWallType(string Key, string? Name, IntentWallRole Role, double? ThicknessMetres = null, double? HeightMetres = null);

public sealed record IntentDraftOpening(string Key, IntentOpeningKind Kind, IntentHost Host,
    double OffsetMetres, double WidthMetres, double? HeightMetres = null, double? SillMetres = null);

public sealed record IntentDraft(int Version, Provenance Provenance, IReadOnlyList<IntentDraftLevel> Levels,
    IReadOnlyList<IntentDraftWallType> WallTypes, IReadOnlyList<IntentRoom> Rooms, IReadOnlyList<IntentDraftOpening> Openings);

// Field is the intent JSON path ("wall_types[ext].thickness_m"); Value its invariant text (numbers "R", text quoted);
// Origin explicit | default | derived | rule; Rule the fill rule id ("explicit" for explicit values); Defaults the skill
// defaults read, each traced to its skill (empty for explicit and rule-only fills).
public sealed record IntentFieldSource(string Field, string Value, string Origin, string Rule, IReadOnlyList<SkillTrace> Defaults)
{
    public const string ExplicitOrigin = "explicit";
    public const string DefaultOrigin = "default";
    public const string DerivedOrigin = "derived";
    public const string RuleOrigin = "rule";
    public const string ExplicitRule = "explicit";
}

// Either Issues is empty and Intent is the filled intent, or Intent is null and Issues lists every refusal.
public sealed record IntentDefaultsResult(LayoutIntent? Intent, IReadOnlyList<IntentFieldSource> Sources, IReadOnlyList<IntentIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0 && Intent is not null;

    public IntentFieldSource? Source(string field)
    {
        foreach (var source in Sources) if (string.Equals(source.Field, field, StringComparison.Ordinal)) return source;
        return null;
    }
}

public sealed record IntentDefaultsCompilation(Project Project, LayoutIntent Intent, IReadOnlyList<IntentFieldSource> Sources);

public static class IntentDefaultCodes
{
    public const string ResolutionFailed = "defaults_resolution_failed";
    public const string DefaultMissing = "default_missing";
    public const string DefaultTypeInvalid = "default_type_invalid";
    public const string DefaultValueInvalid = "default_value_invalid";
    public const string DefaultInconsistent = "default_inconsistent";
    public const string DerivedValueInvalid = "derived_value_invalid";
}

public static partial class IntentSkillDefaults
{
    public const string LevelHeight = "level.height_m";
    public const string ClearHeight = "room.min_clear_height_m";
    public const string ExteriorThickness = "wall.exterior.thickness_m";
    public const string InteriorThickness = "wall.interior.thickness_m";
    public const string ExteriorType = "wall.exterior.type";
    public const string InteriorType = "wall.interior.type";
    public const string WindowSill = "window.sill_height_m";
    public const string WindowHead = "window.head_height_m";
    public const string DoorHeight = "door.height_m";

    public static string ThicknessKey(IntentWallRole role) => role == IntentWallRole.Interior ? InteriorThickness : ExteriorThickness;
    public static string TypeKey(IntentWallRole role) => role == IntentWallRole.Interior ? InteriorType : ExteriorType;

    // Fills the draft from the resolution; never throws for bad defaults (typed issues instead).
    public static IntentDefaultsResult Fill(IntentDraft draft, SkillResolution resolution) => FillCore(draft, resolution);

    // Fill, then IntentCompiler.Compile. A fill refusal throws IntentException whose Code is the first sorted issue's code,
    // Field its key (the field path or default key) and Issues every fill issue; compile refusals are the compiler's own.
    public static IntentDefaultsCompilation Compile(Guid projectId, int revision, IntentDraft draft, SkillResolution resolution)
    {
        var filled = Fill(draft, resolution);
        if (!filled.Succeeded)
        {
            var first = filled.Issues[0];
            throw new IntentException(first.Code, first.Key, first.Detail, filled.Issues);
        }
        return new IntentDefaultsCompilation(IntentCompiler.Compile(projectId, revision, filled.Intent!), filled.Intent!, filled.Sources);
    }

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
