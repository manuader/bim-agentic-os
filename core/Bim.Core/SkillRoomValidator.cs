using System.Globalization;
using System.Text;
namespace Bim.Core;

// P5-02: applies the room-targeted rules of resolved skills (for example the CABA code pack at
// skills/ar.caba.codigo-edificacion) to measured rooms. Rules are matched by room name glob after case folding and accent
// stripping. A rule whose data the room lacks, whose op cannot be applied to a room fact or whose property is unknown is
// NotEvaluable with a reason: it never passes. Findings are ordered by room id then qualified rule id (ordinal).

// Measured facts of one room in metres and square metres; null means the model does not carry the value. Values are finite
// and non-negative. AreaSquareMetres is the clear floor area, MinSideMetres the shortest clear side, ClearHeightMetres the
// clear height, WindowAreaSquareMetres the illumination opening area and VentilationAreaSquareMetres the openable area.
public sealed record RoomFacts
{
    public const string AreaKey = "area_m2";
    public const string MinSideKey = "min_side_m";
    public const string ClearHeightKey = "clear_height_m";
    public const string WindowAreaKey = "window_area_m2";
    public const string VentilationAreaKey = "ventilation_area_m2";

    // The rule properties a room exposes, in this order.
    public static readonly IReadOnlyList<string> Properties = new[] { AreaKey, MinSideKey, ClearHeightKey, WindowAreaKey, VentilationAreaKey };

    public RoomFacts(string id, string name, double? areaSquareMetres = null, double? minSideMetres = null, double? clearHeightMetres = null,
        double? windowAreaSquareMetres = null, double? ventilationAreaSquareMetres = null, bool clearHeightApproximate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(name);
        Id = id;
        Name = name;
        AreaSquareMetres = Measure(areaSquareMetres, nameof(areaSquareMetres));
        MinSideMetres = Measure(minSideMetres, nameof(minSideMetres));
        ClearHeightMetres = Measure(clearHeightMetres, nameof(clearHeightMetres));
        WindowAreaSquareMetres = Measure(windowAreaSquareMetres, nameof(windowAreaSquareMetres));
        VentilationAreaSquareMetres = Measure(ventilationAreaSquareMetres, nameof(ventilationAreaSquareMetres));
        ClearHeightApproximate = clearHeightApproximate && ClearHeightMetres is not null;
    }

    public string Id { get; }
    public string Name { get; }
    public double? AreaSquareMetres { get; }
    public double? MinSideMetres { get; }
    public double? ClearHeightMetres { get; }
    public double? WindowAreaSquareMetres { get; }
    public double? VentilationAreaSquareMetres { get; }
    // True when ClearHeightMetres is an approximation (for example a wall height standing in for the clear height).
    public bool ClearHeightApproximate { get; }

    // A rectangular room from typed lengths in any unit: area width x depth, shortest side min(width, depth).
    public static RoomFacts Rectangle(string id, string name, Length width, Length depth, Length? clearHeight = null, Area? window = null, Area? ventilation = null) =>
        new(id, name, width.Metres * depth.Metres, Math.Min(width.Metres, depth.Metres), clearHeight?.Metres, window?.SquareMetres, ventilation?.SquareMetres);

    // Value of a rule property, or null when the room does not carry it or the property is unknown.
    public double? Property(string property) => property switch
    {
        AreaKey => AreaSquareMetres,
        MinSideKey => MinSideMetres,
        ClearHeightKey => ClearHeightMetres,
        WindowAreaKey => WindowAreaSquareMetres,
        VentilationAreaKey => VentilationAreaSquareMetres,
        _ => null,
    };

    // One RoomFacts per BIM-IR space element, in ordinal id order (Guid "D" text). Area and shortest side come from the
    // space's clear width and depth. BIM-IR has no clear height, so it is approximated by the lowest unconnected height of
    // the walls the space depends on (ignoring slab and ceiling build-up) and flagged ClearHeightApproximate; it is null when
    // the space depends on no wall or on any wall that cannot be decoded. BIM-IR has no openings, so window and ventilation are
    // null. Names come from the caller; a space without one gets an empty name and matches no named rule. A space whose
    // geometry cannot be decoded or is not positive carries no area or side.
    public static IReadOnlyList<RoomFacts> FromProject(Project project, IReadOnlyDictionary<Guid, string> names)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(names);
        var byId = new Dictionary<Guid, Element>();
        foreach (var element in project.Elements) byId.TryAdd(element.Id, element);
        var rooms = new List<RoomFacts>();
        var seen = new HashSet<Guid>();
        foreach (var space in project.Elements.Where(element => element.Kind == GeometryCodec.Space).OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal))
        {
            if (!seen.Add(space.Id)) continue;
            double? area = null, side = null;
            if (GeometryCodec.Problem(space) is null && GeometryCodec.Decode(space) is SpaceGeometry geometry && geometry.Width.Metres > 0 && geometry.Depth.Metres > 0)
            {
                area = geometry.Width.Metres * geometry.Depth.Metres;
                side = Math.Min(geometry.Width.Metres, geometry.Depth.Metres);
                if (!double.IsFinite(area.Value)) area = null;
            }
            double? height = null;
            var undecodable = false;
            foreach (var dependency in space.Dependencies)
            {
                if (!byId.TryGetValue(dependency, out var wall) || wall.Kind != GeometryCodec.Wall) continue;
                if (GeometryCodec.Problem(wall) is not null) { undecodable = true; break; }
                var wallHeight = ((WallGeometry)GeometryCodec.Decode(wall)).Height.Metres;
                height = height is null ? wallHeight : Math.Min(height.Value, wallHeight);
            }
            if (undecodable || height is <= 0) height = null;
            var name = names.TryGetValue(space.Id, out var given) && given is not null ? given : "";
            rooms.Add(new RoomFacts(space.Id.ToString("D"), name, area, side, height, clearHeightApproximate: true));
        }
        return rooms;
    }

    static double? Measure(double? value, string parameter)
    {
        if (value is not { } number) return null;
        if (!double.IsFinite(number) || number < 0) throw new ArgumentOutOfRangeException(parameter, "finite non-negative value required");
        return number == 0 ? 0.0 : number;
    }
}

public enum RoomCheckOutcome { Pass, Fail, NotEvaluable }

// One rule applied to one room. RuleId is the qualified skill/rule id. Article, SourceConfidence and Source come from the
// rule citation (see SkillRoomValidator.ParseCitation), as do ConfirmedBy and ConfirmedOn (who confirmed an owner-confirmed
// value and when, or null). Measured is the room value (property / over for ratio_min), null when not evaluable; Required is
// the resolved threshold when numeric. Detail explains the outcome in invariant text.
public sealed record RoomFinding(string RoomId, string RoomName, string RuleId, string Article, string SourceConfidence, string? Source,
    SkillSeverity Severity, RoomCheckOutcome Outcome, string Property, double? Measured, double? Required, string Message, string Detail,
    string? ConfirmedBy = null, string? ConfirmedOn = null);

// Findings ordered by room id then rule id; UnmatchedRoomIds lists, in ordinal order, rooms that no room rule targets.
public sealed record RoomValidationReport(IReadOnlyList<RoomFinding> Findings, IReadOnlyList<string> UnmatchedRoomIds)
{
    public IReadOnlyList<RoomFinding> Failures => Findings.Where(finding => finding.Outcome == RoomCheckOutcome.Fail).ToArray();
    public IReadOnlyList<RoomFinding> NotEvaluable => Findings.Where(finding => finding.Outcome == RoomCheckOutcome.NotEvaluable).ToArray();
    // Every room matched at least one rule and every applicable rule was evaluated.
    public bool Complete => UnmatchedRoomIds.Count == 0 && Findings.All(finding => finding.Outcome != RoomCheckOutcome.NotEvaluable);
    // Complete and every finding passed. An unmatched room or a not_evaluable rule is never compliance.
    public bool Compliant => Complete && Findings.All(finding => finding.Outcome == RoomCheckOutcome.Pass);
}

// Article, confidence, source and applicability parsed from a rule citation of the form
// "<article>; source_confidence=<cited|owner-confirmed|to-verify>; confirmed_by=<who>; confirmed_on=<date>;
// applies_if=<property><=<number>; source=<reference>", members after the article in any order and all optional.
// cited: the number was checked in the named source; owner-confirmed: the project owner (confirmed_by, on confirmed_on)
// confirmed the value although no primary source was checked; to-verify: pending review. AppliesIf, ConfirmedBy and
// ConfirmedOn are the raw member texts, or null.
public sealed record SkillCitation(string Article, string Confidence, string? Source, string? AppliesIf = null, string? ConfirmedBy = null, string? ConfirmedOn = null);

public static class SkillRoomValidator
{
    public const string ConfidenceCited = "cited";
    public const string ConfidenceOwnerConfirmed = "owner-confirmed";
    public const string ConfidenceToVerify = "to-verify";
    // Comparisons allow this much relative slack (times max(1, |threshold|)) so unit conversion and decimal thresholds
    // such as 1/30 written in JSON do not flip an exact boundary; anything further below fails.
    public const double RelativeTolerance = 1e-9;

    const string ConfidenceMarker = "; source_confidence=";
    const string SourceMarker = "; source=";
    const string AppliesIfMarker = "; applies_if=";
    const string ConfirmedByMarker = "; confirmed_by=";
    const string ConfirmedOnMarker = "; confirmed_on=";
    static readonly string[] Markers = { ConfidenceMarker, SourceMarker, AppliesIfMarker, ConfirmedByMarker, ConfirmedOnMarker };

    // The skill manifest format has no dedicated confidence field, so code packs carry it inside the citation text.
    // A missing or unknown confidence (matched ordinally, so case matters) is to-verify; a missing citation has an empty article.
    public static SkillCitation ParseCitation(string? citation)
    {
        if (string.IsNullOrWhiteSpace(citation)) return new SkillCitation("", ConfidenceToVerify, null);
        // Each member runs from its marker to the next marker (or the end), so values may contain ';' without a space.
        var found = new List<(int At, string Marker)>();
        foreach (var marker in Markers)
        {
            var at = citation.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0) found.Add((at, marker));
        }
        found.Sort((left, right) => left.At.CompareTo(right.At));
        var article = (found.Count == 0 ? citation : citation[..found[0].At]).Trim();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < found.Count; index++)
        {
            var start = found[index].At + found[index].Marker.Length;
            var end = index + 1 < found.Count ? found[index + 1].At : citation.Length;
            values[found[index].Marker] = citation[start..end].Trim();
        }
        var confidence = values.TryGetValue(ConfidenceMarker, out var level) && level is ConfidenceCited or ConfidenceOwnerConfirmed or ConfidenceToVerify ? level : ConfidenceToVerify;
        string? Member(string marker) => values.TryGetValue(marker, out var value) && value.Length > 0 ? value : null;
        return new SkillCitation(article, confidence, Member(SourceMarker), Member(AppliesIfMarker), Member(ConfirmedByMarker), Member(ConfirmedOnMarker));
    }

    // Parses "<property><=<number>" (invariant number), the only applies_if form.
    public static bool TryParseCondition(string? condition, out string property, out double maximum)
    {
        property = "";
        maximum = 0;
        if (condition is null) return false;
        var at = condition.IndexOf("<=", StringComparison.Ordinal);
        if (at <= 0) return false;
        property = condition[..at].Trim();
        return SkillParser.IsName(property) && double.TryParse(condition[(at + 2)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out maximum) && double.IsFinite(maximum);
    }

    // Removes diacritics (Baño -> Bano) so room names match the ASCII room globs the manifest format allows, turns every
    // whitespace run (non-breaking spaces and tabs included) into one space and trims the ends.
    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(character)) { pendingSpace = builder.Length > 0; continue; }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    // Resolves one manifest on its own (it must have no requirements) and validates the rooms against it.
    public static RoomValidationReport Validate(SkillManifest manifest, IEnumerable<RoomFacts> rooms, IReadOnlyDictionary<string, double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var resolution = SkillResolver.Resolve(new SkillCatalog(new[] { manifest }), new[] { new SkillRequest(manifest.Id) });
        if (!resolution.Succeeded)
            throw new InvalidOperationException("skill " + manifest.Id + " does not resolve on its own: " + string.Join("; ", resolution.Issues.Select(issue => issue.Code + "@" + issue.Subject)));
        return Validate(resolution, rooms, parameters);
    }

    // parameters overrides skill parameters at run time without changing the manifest: keys are "<skill id>/<parameter>",
    // values must be finite, within the parameter's [min, max] and integral for integer parameters, else ArgumentException.
    public static RoomValidationReport Validate(SkillResolution resolution, IEnumerable<RoomFacts> rooms, IReadOnlyDictionary<string, double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(rooms);
        if (!resolution.Succeeded) throw new ArgumentException("resolution has issues", nameof(resolution));
        var overrides = Overrides(resolution, parameters);
        var list = new List<RoomFacts>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            ArgumentNullException.ThrowIfNull(room);
            if (!ids.Add(room.Id)) throw new ArgumentException("duplicate room id " + room.Id, nameof(rooms));
            list.Add(room);
        }
        var roomRules = resolution.Rules.Where(rule => rule.Rule.Target.RoomPattern is not null).ToArray();
        var findings = new List<RoomFinding>();
        var unmatched = new List<string>();
        foreach (var room in list.OrderBy(room => room.Id, StringComparer.Ordinal))
        {
            var name = NormalizeName(room.Name);
            var matched = false;
            foreach (var rule in roomRules.Where(rule => rule.Rule.Target.MatchesRoom(name)).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal))
            {
                matched = true;
                findings.Add(Evaluate(room, rule, overrides));
            }
            if (!matched) unmatched.Add(room.Id);
        }
        return new RoomValidationReport(findings.ToArray(), unmatched.ToArray());
    }

    static Dictionary<string, double> Overrides(SkillResolution resolution, IReadOnlyDictionary<string, double>? parameters)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (parameters is null) return result;
        foreach (var (key, value) in parameters.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var slash = key.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0) throw new ArgumentException("parameter override key " + key + " is not <skill id>/<parameter>", nameof(parameters));
            var skill = resolution.Skills.FirstOrDefault(candidate => string.Equals(candidate.Id, key[..slash], StringComparison.Ordinal));
            var declared = skill?.Parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, key[(slash + 1)..], StringComparison.Ordinal));
            if (declared is null) throw new ArgumentException("parameter override " + key + " names no resolved skill parameter", nameof(parameters));
            if (declared.Type is not (SkillParameterType.Number or SkillParameterType.Integer)) throw new ArgumentException("parameter " + key + " is not numeric", nameof(parameters));
            if (!double.IsFinite(value) || (declared.Min is { } min && value < min) || (declared.Max is { } max && value > max)
                || (declared.Type == SkillParameterType.Integer && Math.Floor(value) != value))
                throw new ArgumentOutOfRangeException(nameof(parameters), "parameter " + key + " = " + Text(value) + " is outside its declared range");
            result[key] = value == 0 ? 0.0 : value;
        }
        return result;
    }

    static RoomFinding Evaluate(RoomFacts room, SkillEffectiveRule effective, Dictionary<string, double> overrides)
    {
        var rule = effective.Rule;
        var check = rule.Check;
        var citation = ParseCitation(rule.Citation);
        double? required = effective.Threshold is { Kind: SkillValueKind.Number } threshold ? threshold.Number : null;
        if (effective.ThresholdSource is { Origin: SkillTrace.ParameterOrigin } parameterSource && overrides.TryGetValue(parameterSource.SkillId + "/" + parameterSource.Key, out var overridden))
            required = overridden;
        RoomFinding Finding(RoomCheckOutcome outcome, double? measured, string detail) =>
            new(room.Id, room.Name, effective.QualifiedId, citation.Article, citation.Confidence, citation.Source, rule.Severity, outcome, check.Property, measured, required, rule.Message, detail,
                citation.ConfirmedBy, citation.ConfirmedOn);

        if (check.Op is not (SkillCheckOp.Min or SkillCheckOp.Max or SkillCheckOp.RatioMin))
            return Finding(RoomCheckOutcome.NotEvaluable, null, "check op " + SkillParser.OpName(check.Op) + " is not supported for room facts");
        if (!RoomFacts.Properties.Contains(check.Property, StringComparer.Ordinal))
            return Finding(RoomCheckOutcome.NotEvaluable, null, "property " + check.Property + " is not a room fact");
        if (check.Over is not null && !RoomFacts.Properties.Contains(check.Over, StringComparer.Ordinal))
            return Finding(RoomCheckOutcome.NotEvaluable, null, "denominator " + check.Over + " is not a room fact");
        if (required is not { } limit) return Finding(RoomCheckOutcome.NotEvaluable, null, "rule threshold is not a number");
        if (citation.AppliesIf is not null)
        {
            if (!TryParseCondition(citation.AppliesIf, out var conditionProperty, out var conditionMaximum) || !RoomFacts.Properties.Contains(conditionProperty, StringComparer.Ordinal))
                return Finding(RoomCheckOutcome.NotEvaluable, null, "applies_if " + citation.AppliesIf + " is not understood");
            if (room.Property(conditionProperty) is not { } conditionValue)
                return Finding(RoomCheckOutcome.NotEvaluable, null, conditionProperty + " is not available, so whether the rule applies (" + citation.AppliesIf + ") is unknown");
            if (conditionValue > conditionMaximum + RelativeTolerance * Math.Max(1, Math.Abs(conditionMaximum)))
                return Finding(RoomCheckOutcome.NotEvaluable, null, "rule covers " + citation.AppliesIf + " only; " + conditionProperty + " " + Text(conditionValue)
                    + " needs a requirement that depends on " + conditionProperty + ", which the manifest cannot express");
        }
        if (room.Property(check.Property) is not { } value)
            return Finding(RoomCheckOutcome.NotEvaluable, null, check.Property + " is not available for this room");
        var measured = value;
        if (check.Op == SkillCheckOp.RatioMin)
        {
            if (room.Property(check.Over!) is not { } over) return Finding(RoomCheckOutcome.NotEvaluable, null, check.Over + " is not available for this room");
            if (over <= 0) return Finding(RoomCheckOutcome.NotEvaluable, null, check.Over + " is zero, so the ratio is undefined");
            measured = value / over;
        }
        var slack = RelativeTolerance * Math.Max(1, Math.Abs(limit));
        var pass = check.Op == SkillCheckOp.Max ? measured <= limit + slack : measured >= limit - slack;
        var subject = check.Op == SkillCheckOp.RatioMin ? check.Property + " / " + check.Over : check.Property;
        var relation = check.Op == SkillCheckOp.Max ? (pass ? "<=" : ">") : (pass ? ">=" : "<");
        var note = room.ClearHeightApproximate && (check.Property == RoomFacts.ClearHeightKey || check.Over == RoomFacts.ClearHeightKey)
            ? " (clear height approximated by wall unconnected height)" : "";
        return Finding(pass ? RoomCheckOutcome.Pass : RoomCheckOutcome.Fail, measured, subject + " " + Text(measured) + " " + relation + " " + Text(limit) + note);
    }

    static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
