namespace Bim.Core;

// P5-07 typology programme: a typology skill pack states a room programme as programme.* defaults and
// SkillTypology.Resolve turns it, for a family size, into a programme record and a project brief v1 (ProjectBrief).
public sealed record TypologyRoom(string Kind, bool Required, int Count, double MinAreaSquareMetres, double TargetAreaSquareMetres,
    double? MaxAreaSquareMetres, SkillTrace Source);

public sealed record TypologyProgramme(string PackId, int FamilySize, IReadOnlyList<TypologyRoom> Rooms, IReadOnlyList<BriefAdjacency> Adjacencies,
    ProjectBrief? Brief, IReadOnlyList<SkillIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0;
}

public static class SkillTypologyCodes
{
    public const string KindMismatch = "typology_kind_mismatch";
    public const string ResolutionFailed = "typology_resolution_failed";
    public const string ProgrammeMissing = "typology_programme_missing";
    public const string FamilySizeInvalid = "typology_family_size_invalid";
    public const string RoomKindUnknown = "typology_room_kind_unknown";
    public const string RoomNotOffered = "typology_room_not_offered";
    public const string SettingInvalid = "typology_setting_invalid";
    public const string MinimumConflict = "typology_minimum_conflict";
}

public static class SkillTypology
{
    public static IReadOnlyList<string> RoomKinds { get; } = Array.Empty<string>();

    public static TypologyProgramme Resolve(SkillManifest pack, int familySize, IReadOnlyCollection<string>? include = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return new TypologyProgramme(pack.Id, familySize, Array.Empty<TypologyRoom>(), Array.Empty<BriefAdjacency>(), null,
            new[] { new SkillIssue("not_implemented", pack.Id, "stub") });
    }

    public static TypologyProgramme Resolve(SkillResolution resolution, int familySize, IReadOnlyCollection<string>? include = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return new TypologyProgramme("", familySize, Array.Empty<TypologyRoom>(), Array.Empty<BriefAdjacency>(), null,
            new[] { new SkillIssue("not_implemented", "", "stub") });
    }
}
