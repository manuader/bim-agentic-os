using System.Globalization;
namespace Bim.Core;

// P5-07 typology programme. A typology skill pack states a dwelling room programme as programme.* defaults (the manifest
// format is unchanged; every value is a scalar):
//   programme.room.<kind>.required              boolean, mandatory: required rooms are always in the programme
//   programme.room.<kind>.min_area_m2           positive number, mandatory
//   programme.room.<kind>.target_area_m2        positive number, defaults to the minimum
//   programme.room.<kind>.max_area_m2           positive number, optional (open-ended when absent)
//   programme.room.<kind>.count                 integer >= 1, fixed number of rooms (default 1)
//   programme.room.<kind>.persons_per_room      positive number: count = ceil(family size / persons_per_room); not with count
//   programme.room.<kind>.include_from_persons  integer >= 1, optional rooms only: included from this family size on
//   programme.adjacency.<kind>.<kind>           "required", "preferred" or "forbidden" (symmetric, stated once)
//   programme.family_size.min / .max            integers >= 1 bounding the accepted family size
//   programme.source_confidence                 text, for example "illustrative"
// Optional rooms without include_from_persons join only when the caller requests them. SkillTypology.Resolve turns the
// programme, for a family size, into a TypologyProgramme whose Brief is a project brief v1 (Bim.Core.ProjectBrief) that
// BriefValidator accepts and the space planner and prototype code consume: room ids are the kinds, names are English
// and match the CABA pack name patterns (living*, kitchen*, bedroom*, bath*), counts are BriefRoom counts, areas are
// per room, and adjacency wishes become brief adjacencies between rooms that are in the programme. Settings outside
// programme.* are ignored except room.<kind>.min_area_m2 (for example from bim.typology.dwelling or a composed pack),
// which must not exceed the programme minimum of that kind. Every refusal is a typed SkillIssue (SkillTypologyCodes);
// on any issue the programme has no rooms, adjacencies or brief. The result depends only on the settings, the family
// size and the set of requested rooms: not on their order, the culture or the catalog order.
public sealed record TypologyRoom(string Kind, bool Required, int Count, double MinAreaSquareMetres, double TargetAreaSquareMetres,
    double? MaxAreaSquareMetres, SkillTrace Source);

// PackId is the typology pack id (from a resolution: the ids of its typology skills in ordinal order joined by '+').
// Rooms follow SkillTypology.RoomKinds order; adjacencies have A < B (ordinal) and are ordered by (A, B).
public sealed record TypologyProgramme(string PackId, int FamilySize, IReadOnlyList<TypologyRoom> Rooms, IReadOnlyList<BriefAdjacency> Adjacencies,
    ProjectBrief? Brief, IReadOnlyList<SkillIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0;
}

public static class SkillTypologyCodes
{
    // The manifest is not a typology pack.
    public const string KindMismatch = "typology_kind_mismatch";
    // The skill resolution has issues (detail lists their codes).
    public const string ResolutionFailed = "typology_resolution_failed";
    // No programme.room.* setting.
    public const string ProgrammeMissing = "typology_programme_missing";
    // Family size below one or outside programme.family_size bounds (subject "family_size").
    public const string FamilySizeInvalid = "typology_family_size_invalid";
    // A room kind outside SkillTypology.RoomKinds in a setting, an adjacency or the request (subject: the kind).
    public const string RoomKindUnknown = "typology_room_kind_unknown";
    // A requested room kind the programme does not state (subject: the kind).
    public const string RoomNotOffered = "typology_room_not_offered";
    // A malformed, mistyped, missing or contradictory programme setting (subject: the setting key).
    public const string SettingInvalid = "typology_setting_invalid";
    // Minimum above target or maximum, target above maximum, or a composed room.<kind>.min_area_m2 above the programme
    // minimum (subject: the kind).
    public const string MinimumConflict = "typology_minimum_conflict";
}

public static class SkillTypology
{
    public const string Prefix = "programme.";
    public const string FamilySizeSubject = "family_size";
    // Confidence of the inferred provenance on brief rooms and adjacencies: programme values are illustrative defaults.
    public const double ProvenanceConfidence = 0.6;
    // At most this many rooms of one kind (a fixed count or ceil(family size / persons_per_room)).
    public const int MaxRoomCount = 50;

    static readonly string[] Kinds = { "living", "dining", "kitchen", "hall", "bedroom", "bathroom", "toilet", "laundry", "study", "garage", "storage" };
    static readonly string[] Names = { "Living", "Dining", "Kitchen", "Hall", "Bedroom", "Bathroom", "Toilet", "Laundry", "Study", "Garage", "Storage" };
    static readonly string[] Attributes = { "required", "min_area_m2", "target_area_m2", "max_area_m2", "count", "persons_per_room", "include_from_persons" };
    static readonly string[] Relations = { "required", "preferred", "forbidden" };

    // The closed, ordered set of room kinds a programme may state.
    public static IReadOnlyList<string> RoomKinds { get; } = Array.AsReadOnly(Kinds);

    public static TypologyProgramme Resolve(SkillManifest pack, int familySize, IReadOnlyCollection<string>? include = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.Kind != SkillKind.Typology)
            return Refused(pack.Id, familySize, new[] { new SkillIssue(SkillTypologyCodes.KindMismatch, pack.Id, "skill kind " + SkillParser.KindName(pack.Kind) + ", typology required") });
        var settings = pack.Defaults.Select(entry => new Entry(entry.Key, entry.Value, new SkillTrace(pack.Id, SkillTrace.DefaultOrigin, entry.Key))).ToList();
        return Build(pack.Id, familySize, include, settings);
    }

    public static TypologyProgramme Resolve(SkillResolution resolution, int familySize, IReadOnlyCollection<string>? include = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded)
            return Refused("", familySize, new[] { new SkillIssue(SkillTypologyCodes.ResolutionFailed, "", string.Join(",", resolution.Issues.Select(issue => issue.Code + "@" + issue.Subject))) });
        var packId = string.Join("+", resolution.Skills.Where(skill => skill.Kind == SkillKind.Typology).Select(skill => skill.Id).OrderBy(id => id, StringComparer.Ordinal));
        var settings = resolution.Settings.Select(setting => new Entry(setting.Key, setting.Value, setting.Source)).ToList();
        return Build(packId, familySize, include, settings);
    }

    readonly record struct Entry(string Key, SkillValue Value, SkillTrace Source);

    sealed record Adjacency(string Key, string A, string B, BriefAdjacencyKind Kind, SkillTrace Source);

    static TypologyProgramme Refused(string packId, int familySize, IReadOnlyList<SkillIssue> issues) =>
        new(packId, familySize, Array.Empty<TypologyRoom>(), Array.Empty<BriefAdjacency>(), null, issues);

    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static bool IsPositive(SkillValue value) => value.Kind == SkillValueKind.Number && double.IsFinite(value.Number) && value.Number > 0;

    static bool IsCount(SkillValue value) => IsPositive(value) && value.Number >= 1 && value.Number <= int.MaxValue && Math.Floor(value.Number) == value.Number;

    static TypologyProgramme Build(string packId, int familySize, IReadOnlyCollection<string>? include, List<Entry> settings)
    {
        // Issues carry the setting key (or room prefix) they come from so they are reported in setting key order.
        var issues = new List<(string Order, int Sequence, SkillIssue Issue)>();
        void Issue(string order, string code, string subject, string detail) => issues.Add((order, issues.Count, new SkillIssue(code, subject, detail)));
        const string Last = "\uffff";

        settings.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        var rooms = new Dictionary<string, Dictionary<string, Entry>>(StringComparer.Ordinal);
        var adjacencyEntries = new List<Entry>();
        var composedMinimums = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var unknownKinds = new HashSet<string>(StringComparer.Ordinal);
        Entry? familyMin = null, familyMax = null;
        void Unknown(string order, string kind, string context)
        {
            if (unknownKinds.Add(kind)) Issue(order, SkillTypologyCodes.RoomKindUnknown, kind, context + " names room kind '" + kind + "', not one of " + string.Join(", ", Kinds));
        }

        foreach (var entry in settings)
        {
            var parts = entry.Key.Split('.');
            if (parts.Length == 3 && parts[0] == "room" && parts[2] == "min_area_m2" && Array.IndexOf(Kinds, parts[1]) >= 0 && entry.Value.Kind == SkillValueKind.Number)
            {
                composedMinimums[parts[1]] = entry;
                continue;
            }
            if (!entry.Key.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            switch (parts[1])
            {
                case "room" when parts.Length == 4:
                    if (Array.IndexOf(Kinds, parts[2]) < 0) Unknown(entry.Key, parts[2], entry.Key);
                    else if (Array.IndexOf(Attributes, parts[3]) < 0) Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "unknown room attribute '" + parts[3] + "', expected one of " + string.Join(", ", Attributes));
                    else
                    {
                        if (!rooms.TryGetValue(parts[2], out var attributes)) rooms[parts[2]] = attributes = new Dictionary<string, Entry>(StringComparer.Ordinal);
                        attributes[parts[3]] = entry;
                    }
                    break;
                case "adjacency":
                    adjacencyEntries.Add(entry);
                    break;
                case "family_size" when parts.Length == 3 && (parts[2] == "min" || parts[2] == "max"):
                    if (!IsCount(entry.Value)) Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "integer >= 1 required, got " + entry.Value);
                    else if (parts[2] == "min") familyMin = entry;
                    else familyMax = entry;
                    break;
                case "source_confidence" when parts.Length == 2:
                    if (entry.Value.Kind != SkillValueKind.Text) Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "text required, got " + entry.Value);
                    break;
                default:
                    Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "unknown programme setting");
                    break;
            }
        }

        // Family size bounds and the requested family size.
        var lowest = familyMin?.Value.Number ?? 1;
        var highest = familyMax?.Value.Number ?? double.PositiveInfinity;
        if (familyMin is not null && familyMax is not null && lowest > highest)
            Issue(familyMin.Value.Key, SkillTypologyCodes.SettingInvalid, familyMin.Value.Key, "minimum family size " + Number(lowest) + " above maximum " + Number(highest));
        else if (familySize < 1 || familySize < lowest || familySize > highest)
            Issue("", SkillTypologyCodes.FamilySizeInvalid, FamilySizeSubject, "family size " + familySize.ToString(CultureInfo.InvariantCulture) + " outside "
                + Number(Math.Max(1, lowest)) + ".." + (familyMax is null ? "unbounded" : Number(highest)));

        // Rooms.
        var specs = new List<(string Kind, bool Required, double Min, double Target, double? Max, Entry? Count, Entry? Persons, Entry? From, SkillTrace Source)>();
        foreach (var kind in Kinds)
        {
            if (!rooms.TryGetValue(kind, out var stated)) continue;
            Dictionary<string, Entry> attributes = stated;
            var prefix = Prefix + "room." + kind + ".";
            var valid = true;
            Entry? Get(string attribute) => attributes.TryGetValue(attribute, out var found) ? found : null;
            void Invalid(string attribute, string detail)
            {
                Issue(prefix + attribute, SkillTypologyCodes.SettingInvalid, prefix + attribute, detail);
                valid = false;
            }
            var required = Get("required");
            if (required is null) Invalid("required", "required flag missing");
            else if (required.Value.Value.Kind != SkillValueKind.Boolean) Invalid("required", "boolean required, got " + required.Value.Value);
            var minimum = Get("min_area_m2");
            if (minimum is null) Invalid("min_area_m2", "minimum area missing");
            foreach (var attribute in new[] { "min_area_m2", "target_area_m2", "max_area_m2", "persons_per_room" })
                if (Get(attribute) is { } area && !IsPositive(area.Value)) Invalid(attribute, "positive finite number required, got " + area.Value);
            foreach (var attribute in new[] { "count", "include_from_persons" })
                if (Get(attribute) is { } whole && !IsCount(whole.Value)) Invalid(attribute, "integer >= 1 required, got " + whole.Value);
            var count = Get("count");
            var persons = Get("persons_per_room");
            var threshold = Get("include_from_persons");
            if (count is not null && persons is not null) Invalid("count", "a fixed count and persons_per_room are exclusive");
            if (threshold is not null && required is { Value.Kind: SkillValueKind.Boolean } flag && flag.Value.Boolean) Invalid("include_from_persons", "only optional rooms take an inclusion threshold");
            if (!valid || required is null || minimum is null) continue;

            var min = minimum.Value.Value.Number;
            var target = Get("target_area_m2")?.Value.Number ?? min;
            double? max = Get("max_area_m2")?.Value.Number;
            var conflicts = new List<string>();
            if (min > target) conflicts.Add("minimum " + Number(min) + " above target " + Number(target));
            if (max is { } top && min > top) conflicts.Add("minimum " + Number(min) + " above maximum " + Number(top));
            if (max is { } ceiling && target > ceiling) conflicts.Add("target " + Number(target) + " above maximum " + Number(ceiling));
            if (composedMinimums.TryGetValue(kind, out var composed) && composed.Value.Number > min)
                conflicts.Add(composed.Source + " minimum " + Number(composed.Value.Number) + " above programme minimum " + Number(min));
            if (conflicts.Count > 0)
            {
                Issue(prefix, SkillTypologyCodes.MinimumConflict, kind, string.Join("; ", conflicts));
                continue;
            }
            specs.Add((kind, required.Value.Value.Boolean, min, target, max, count, persons, threshold, minimum.Value.Source));
        }
        if (rooms.Count == 0 && unknownKinds.Count == 0)
            Issue("", SkillTypologyCodes.ProgrammeMissing, packId, "no " + Prefix + "room.<kind>.* setting");

        // Adjacency wishes.
        var adjacencies = new List<Adjacency>();
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in adjacencyEntries)
        {
            var parts = entry.Key.Split('.');
            if (parts.Length != 4)
            {
                Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "adjacency key must be " + Prefix + "adjacency.<kind>.<kind>");
                continue;
            }
            var unknown = false;
            foreach (var kind in new[] { parts[2], parts[3] })
            {
                if (Array.IndexOf(Kinds, kind) >= 0) continue;
                Unknown(entry.Key, kind, entry.Key);
                unknown = true;
            }
            if (unknown) continue;
            var relation = entry.Value.Kind == SkillValueKind.Text ? Array.IndexOf(Relations, entry.Value.Text) : -1;
            if (parts[2] == parts[3]) Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "a room kind cannot be adjacent to itself");
            else if (relation < 0) Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "relation must be one of " + string.Join(", ", Relations) + ", got " + entry.Value);
            else if (!rooms.ContainsKey(parts[2]) || !rooms.ContainsKey(parts[3]))
                Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "adjacency names a room kind the programme does not state");
            else
            {
                var a = string.CompareOrdinal(parts[2], parts[3]) < 0 ? parts[2] : parts[3];
                var b = a == parts[2] ? parts[3] : parts[2];
                if (!pairs.Add(a + "." + b)) Issue(entry.Key, SkillTypologyCodes.SettingInvalid, entry.Key, "adjacency " + a + "-" + b + " is stated more than once");
                else adjacencies.Add(new Adjacency(entry.Key, a, b, (BriefAdjacencyKind)relation, entry.Source));
            }
        }

        // Requested optional rooms.
        var requested = new HashSet<string>(StringComparer.Ordinal);
        if (include is not null)
            foreach (var name in include.Distinct(StringComparer.Ordinal).OrderBy(requestedKind => requestedKind, StringComparer.Ordinal))
            {
                if (name is null || Array.IndexOf(Kinds, name) < 0)
                    Issue(Last, SkillTypologyCodes.RoomKindUnknown, name ?? "", "requested room kind '" + name + "' is not one of " + string.Join(", ", Kinds));
                else if (!rooms.ContainsKey(name))
                    Issue(Last, SkillTypologyCodes.RoomNotOffered, name, "the programme of " + packId + " does not state room kind '" + name + "'");
                else requested.Add(name);
            }

        if (issues.Count > 0)
            return Refused(packId, familySize, issues.OrderBy(item => item.Order, StringComparer.Ordinal).ThenBy(item => item.Sequence).Select(item => item.Issue).ToArray());

        var programmeRooms = new List<TypologyRoom>();
        foreach (var spec in specs)
        {
            var included = spec.Required || requested.Contains(spec.Kind) || (spec.From is { } inclusion && familySize >= inclusion.Value.Number);
            if (!included) continue;
            var count = spec.Count is { } fixedCount ? (int)fixedCount.Value.Number
                : spec.Persons is { } persons ? (int)Math.Ceiling(familySize / persons.Value.Number) : 1;
            programmeRooms.Add(new TypologyRoom(spec.Kind, spec.Required, Math.Max(1, count), spec.Min, spec.Target, spec.Max, spec.Source));
        }
        var present = new HashSet<string>(programmeRooms.Select(room => room.Kind), StringComparer.Ordinal);
        var family = familySize.ToString(CultureInfo.InvariantCulture);
        var briefAdjacencies = adjacencies.Where(adjacency => present.Contains(adjacency.A) && present.Contains(adjacency.B))
            .OrderBy(adjacency => adjacency.A, StringComparer.Ordinal).ThenBy(adjacency => adjacency.B, StringComparer.Ordinal)
            .Select(adjacency => new BriefAdjacency(adjacency.A, adjacency.B, adjacency.Kind,
                new BriefProvenance(BriefSource.Inferred, ProvenanceConfidence, "typology " + adjacency.Source.SkillId + " " + adjacency.Key)))
            .ToArray();
        var briefRooms = programmeRooms.Select(room => new BriefRoom(room.Kind, Names[Array.IndexOf(Kinds, room.Kind)], new BriefValue<int>(room.Count),
                new BriefValue<double>(room.TargetAreaSquareMetres), new BriefValue<double>(room.MinAreaSquareMetres),
                room.MaxAreaSquareMetres is { } top ? new BriefValue<double>(top) : null, null,
                new BriefProvenance(BriefSource.Inferred, ProvenanceConfidence, "typology " + room.Source.SkillId + " " + Prefix + "room." + room.Kind + " for a family of " + family)))
            .ToArray();
        var brief = new ProjectBrief(ProjectBrief.CurrentVersion, packId + " programme for a family of " + family, new BriefConstraints(), briefRooms, briefAdjacencies);
        return new TypologyProgramme(packId, familySize, programmeRooms.ToArray(), briefAdjacencies, brief, Array.Empty<SkillIssue>());
    }
}
