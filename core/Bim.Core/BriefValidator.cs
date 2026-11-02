using System.Globalization;
namespace Bim.Core;

// Semantic checks on a parsed or programmatically built brief. Issues come back in document order; an empty result
// means the brief is consistent. Adjacency-duplicate is a warning; every other issue is an error.
public static class BriefValidator
{
    // Tolerance for comparing summed areas against the budget.
    const double AreaTolerance = 1e-9;

    public static BriefIssue[] Validate(ProjectBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var issues = new List<BriefIssue>();
        void Error(string code, string field, string detail) => issues.Add(new BriefIssue(code, field, BriefSeverity.Error, detail));

        void CheckProvenance(BriefProvenance? provenance, string path)
        {
            if (provenance is null) return;
            if (!(provenance.Confidence >= 0 && provenance.Confidence <= 1)) Error(BriefCodes.ConfidenceOutOfRange, path + ".confidence", "confidence must be within 0..1");
            if (provenance.Page is < 1) Error(BriefCodes.PageInvalid, path + ".page", "page is 1-based");
        }
        void CheckSourced<T>(BriefValue<T>? value, string path) where T : notnull => CheckProvenance(value?.Provenance, path + ".provenance");
        void CheckPositive(BriefValue<double>? value, string path, string code)
        {
            if (value is null) return;
            CheckSourced(value, path);
            if (!(double.IsFinite(value.Value) && value.Value > 0)) Error(code, path, "positive finite value required");
        }

        if (brief.Version != ProjectBrief.CurrentVersion) Error(BriefCodes.VersionUnsupported, "brief_version", "1 required");
        if (string.IsNullOrWhiteSpace(brief.Name)) Error(BriefCodes.NameBlank, "name", "non-blank name required");

        var constraints = brief.Constraints;
        CheckSourced(constraints.Jurisdiction, "constraints.jurisdiction");
        if (constraints.Jurisdiction is { } jurisdiction && string.IsNullOrWhiteSpace(jurisdiction.Value)) Error(BriefCodes.NameBlank, "constraints.jurisdiction", "non-blank jurisdiction required");
        if (constraints.Site is { } site)
        {
            CheckPositive(site.WidthMetres, "constraints.site.width_m", BriefCodes.SiteDimensionNonPositive);
            CheckPositive(site.DepthMetres, "constraints.site.depth_m", BriefCodes.SiteDimensionNonPositive);
        }
        int? levels = null;
        if (constraints.Levels is { } levelValue)
        {
            CheckSourced(levelValue, "constraints.levels");
            if (levelValue.Value < 1) Error(BriefCodes.LevelsInvalid, "constraints.levels", "at least one level required");
            else levels = levelValue.Value;
        }
        CheckPositive(constraints.TotalAreaBudgetSquareMetres, "constraints.total_area_budget_m2", BriefCodes.BudgetNonPositive);

        if (brief.Rooms.Count == 0) Error(BriefCodes.RoomsEmpty, "rooms", "at least one room required");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        double minimumSum = 0;
        for (var index = 0; index < brief.Rooms.Count; index++)
        {
            var room = brief.Rooms[index];
            var path = "rooms[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (!BriefParser.IsRoomId(room.Id)) Error(BriefCodes.RoomIdInvalid, path + ".id", "1-64 of [a-z0-9_-], starting with a letter or digit");
            else if (!ids.Add(room.Id)) Error(BriefCodes.RoomIdDuplicate, path + ".id", "room id '" + room.Id + "' already used");
            if (string.IsNullOrWhiteSpace(room.Name)) Error(BriefCodes.NameBlank, path + ".name", "non-blank room name required");
            CheckProvenance(room.Provenance, path + ".provenance");
            var countValid = true;
            if (room.Count is { } count)
            {
                CheckSourced(count, path + ".count");
                if (count.Value < 1) { Error(BriefCodes.CountInvalid, path + ".count", "count must be at least 1"); countValid = false; }
            }
            CheckPositive(room.TargetAreaSquareMetres, path + ".target_area_m2", BriefCodes.AreaNonPositive);
            CheckPositive(room.MinAreaSquareMetres, path + ".min_area_m2", BriefCodes.AreaNonPositive);
            CheckPositive(room.MaxAreaSquareMetres, path + ".max_area_m2", BriefCodes.AreaNonPositive);
            var min = Positive(room.MinAreaSquareMetres);
            var max = Positive(room.MaxAreaSquareMetres);
            var target = Positive(room.TargetAreaSquareMetres);
            if (min is { } low && max is { } high && low > high) Error(BriefCodes.AreaRangeInverted, path + ".min_area_m2", "minimum area exceeds maximum area");
            else if (target is { } wanted && ((min is { } floor && wanted < floor) || (max is { } ceiling && wanted > ceiling)))
                Error(BriefCodes.AreaTargetOutsideRange, path + ".target_area_m2", "target area lies outside min..max");
            if (min is { } minimum && countValid) minimumSum += minimum * room.EffectiveCount;
            if (room.PreferredLevel is { } level)
            {
                CheckSourced(level, path + ".preferred_level");
                if (level.Value < 0 || (levels is { } total && level.Value >= total))
                    Error(BriefCodes.LevelOutOfRange, path + ".preferred_level", levels is { } levelCount ? "level index must be within 0.." + (levelCount - 1).ToString(CultureInfo.InvariantCulture) : "level index must be non-negative");
            }
        }
        if (Positive(constraints.TotalAreaBudgetSquareMetres) is { } budget && minimumSum > budget + AreaTolerance)
            Error(BriefCodes.BudgetExceeded, "constraints.total_area_budget_m2",
                "sum of minimum room areas " + minimumSum.ToString("R", CultureInfo.InvariantCulture) + " m2 exceeds budget " + budget.ToString("R", CultureInfo.InvariantCulture) + " m2");

        // Unordered pair -> kinds already stated, so a later contradicting or repeated statement is reported once.
        var stated = new Dictionary<(string, string), HashSet<BriefAdjacencyKind>>();
        for (var index = 0; index < brief.Adjacencies.Count; index++)
        {
            var adjacency = brief.Adjacencies[index];
            var path = "adjacencies[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            CheckProvenance(adjacency.Provenance, path + ".provenance");
            var known = true;
            if (!ids.Contains(adjacency.A)) { Error(BriefCodes.AdjacencyRoomUnknown, path + ".a", "no room with id '" + adjacency.A + "'"); known = false; }
            if (!ids.Contains(adjacency.B)) { Error(BriefCodes.AdjacencyRoomUnknown, path + ".b", "no room with id '" + adjacency.B + "'"); known = false; }
            if (string.Equals(adjacency.A, adjacency.B, StringComparison.Ordinal)) { Error(BriefCodes.AdjacencySelf, path, "a room cannot be adjacent to itself"); continue; }
            if (!known) continue;
            var key = string.CompareOrdinal(adjacency.A, adjacency.B) < 0 ? (adjacency.A, adjacency.B) : (adjacency.B, adjacency.A);
            if (!stated.TryGetValue(key, out var kinds)) stated[key] = kinds = new HashSet<BriefAdjacencyKind>();
            var contradicts = adjacency.Kind == BriefAdjacencyKind.Forbidden
                ? kinds.Contains(BriefAdjacencyKind.Required) || kinds.Contains(BriefAdjacencyKind.Preferred)
                : kinds.Contains(BriefAdjacencyKind.Forbidden);
            if (contradicts) Error(BriefCodes.AdjacencyContradictory, path, "pair " + key.Item1 + "/" + key.Item2 + " is both wanted and forbidden");
            else if (kinds.Count > 0) issues.Add(new BriefIssue(BriefCodes.AdjacencyDuplicate, path, BriefSeverity.Warning, "pair " + key.Item1 + "/" + key.Item2 + " already stated"));
            kinds.Add(adjacency.Kind);
        }
        return issues.ToArray();
    }

    public static bool HasErrors(IEnumerable<BriefIssue> issues) => issues.Any(issue => issue.Severity == BriefSeverity.Error);

    static double? Positive(BriefValue<double>? value) => value is { } present && double.IsFinite(present.Value) && present.Value > 0 ? present.Value : null;
}
