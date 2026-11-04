using System.Globalization;
namespace Bim.Core;

// P5-05: applies the element-targeted rules of resolved skills (for example skills/bim.qa.element-basics) to the walls,
// doors and windows of a BIM-IR project, without any BIM-IR schema change. A rule targets an element kind ("wall", "door",
// "window") and names a length property with an explicit unit suffix: "<quantity>_mm" (millimetres) or "<quantity>_m"
// (metres). BIM-IR persists metres (ADR0008); a millimetre value is the metre value times 1000 rounded to six decimals
// (ElementFacts.MillimetreDigits), which removes binary conversion noise: 0.1 m reads exactly 100 mm, 0.0749 m 74.9 mm.
//
// Quantities (ElementFacts):
//   wall [ax,ay,bx,by,height,thickness]: length (axis), thickness, height.
//   door/window [ax,ay,bx,by,sill,height] with Dependencies [level, host wall]: width (distance between the two ends),
//   height, sill, and, against the host wall, start_clearance (host axis start to the nearer opening end, along the axis),
//   end_clearance (farther opening end to the host axis end), head_clearance (host height minus sill plus height) and
//   axis_offset (largest perpendicular distance of an opening end from the host axis line). An opening lies inside its
//   host when start_clearance >= 0, end_clearance >= 0, sill >= 0, head_clearance >= 0 and axis_offset <= tolerance, which
//   the pack expresses as ordinary min/max rules; an opening exactly at a wall end has a clearance of exactly 0.
//
// Never a silent pass: a rule whose property is not a quantity of the targeted kind, whose threshold is not a number,
// whose element geometry cannot be read or whose host cannot be found is NotEvaluable with a typed Reason
// (ElementReasons). The host is the single dependency that is a wall in the project: none and a dangling dependency is
// host_orphan, none at all host_missing, more than one host_ambiguous. BIM-IR walls are straight axis segments of exactly
// six values; a curved wall cannot be expressed, so a wall (or host) of any other arity, for example an arc carrying a
// bulge value, is shape_unsupported and no arc length is computed. Non-finite values are geometry_invalid; a host of zero
// axis length is host_degenerate. Thickness, height or sill values that are zero or negative are measured as given and
// fail the bound rules instead of being refused.
//
// Findings are ordered by element id (Guid "D" text, ordinal) then qualified rule id (ordinal). Comparisons allow
// SkillRoomValidator.RelativeTolerance times max(1, |bound|) of slack, so a value equal to its bound passes.
public static class ElementReasons
{
    public const string PropertyUnknown = "property_unknown";
    public const string ThresholdNotNumber = "threshold_not_number";
    public const string GeometryInvalid = "geometry_invalid";
    public const string ShapeUnsupported = "shape_unsupported";
    public const string HostMissing = "host_missing";
    public const string HostOrphan = "host_orphan";
    public const string HostAmbiguous = "host_ambiguous";
    public const string HostGeometryInvalid = "host_geometry_invalid";
    public const string HostShapeUnsupported = "host_shape_unsupported";
    public const string HostDegenerate = "host_degenerate";
    public const string RatioUndefined = "ratio_undefined";
    public const string UnitMismatch = "unit_mismatch";
    public const string HostedLayout = "hosted_layout";
    public const string LevelMissing = "level_missing";
}

// Measured quantities of one wall, door or window in metres. Problem (with ProblemDetail) is set when the element's own
// geometry cannot be read; HostProblem (with HostProblemDetail) when the host-derived quantities cannot be computed.
public sealed record ElementFacts(string Id, string Kind, string? HostId, string? Problem, string? ProblemDetail, string? HostProblem, string? HostProblemDetail,
    IReadOnlyDictionary<string, double> Metres)
{
    public const string Wall = "wall";
    public const string Door = "door";
    public const string Window = "window";
    public const string MillimetreSuffix = "_mm";
    public const string MetreSuffix = "_m";
    // Millimetre values are rounded to this many decimals (one nanometre), removing binary conversion noise so that
    // 0.0749 m reads exactly 74.9 mm and 0.1 m exactly 100 mm.
    public const int MillimetreDigits = 6;

    public static readonly IReadOnlyList<string> WallQuantities = new[] { "length", "thickness", "height" };
    public static readonly IReadOnlyList<string> OpeningQuantities = new[] { "width", "height", "sill", "start_clearance", "end_clearance", "head_clearance", "axis_offset" };
    public static readonly IReadOnlyList<string> HostQuantities = new[] { "start_clearance", "end_clearance", "head_clearance", "axis_offset" };

    public static IReadOnlyList<string> Quantities(string kind) => kind switch
    {
        Wall => WallQuantities,
        Door or Window => OpeningQuantities,
        _ => Array.Empty<string>(),
    };

    // Splits "thickness_mm" into ("thickness", "mm"); null when the suffix is not an explicit length unit.
    public static (string Quantity, string Unit)? Split(string property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.EndsWith(MillimetreSuffix, StringComparison.Ordinal) && property.Length > MillimetreSuffix.Length)
            return (property[..^MillimetreSuffix.Length], LengthUnits.Millimetres);
        if (property.EndsWith(MetreSuffix, StringComparison.Ordinal) && property.Length > MetreSuffix.Length)
            return (property[..^MetreSuffix.Length], LengthUnits.Metres);
        return null;
    }

    public static bool IsProperty(string kind, string property) => Split(property) is { } split && Quantities(kind).Contains(split.Quantity, StringComparer.Ordinal);

    // The value of a property in its own unit, or null with the reason it is unavailable.
    public double? Value(string property, out string? reason, out string detail)
    {
        reason = null;
        detail = "";
        if (Split(property) is not { } split || !Quantities(Kind).Contains(split.Quantity, StringComparer.Ordinal))
        {
            reason = ElementReasons.PropertyUnknown;
            detail = "property " + property + " is not a " + (Kind.Length == 0 ? "?" : Kind) + " quantity (" + string.Join(", ", Quantities(Kind).Select(quantity => quantity + "_mm|_m")) + ")";
            return null;
        }
        if (Problem is not null) { reason = Problem; detail = ProblemDetail ?? Problem; return null; }
        if (HostQuantities.Contains(split.Quantity, StringComparer.Ordinal) && Kind is Door or Window && HostProblem is not null)
        {
            reason = HostProblem;
            detail = HostProblemDetail ?? HostProblem;
            return null;
        }
        if (!Metres.TryGetValue(split.Quantity, out var metres)) { reason = ElementReasons.GeometryInvalid; detail = split.Quantity + " is not available"; return null; }
        return split.Unit == LengthUnits.Millimetres ? Zero(Math.Round(Length.FromMetres(metres).Millimetres, MillimetreDigits, MidpointRounding.ToEven)) : Zero(metres);
    }

    // Facts of every wall, door and window in the project, in ordinal id order. Duplicate ids are refused.
    public static IReadOnlyList<ElementFacts> FromProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(project.Elements);
        var byId = new Dictionary<Guid, Element>();
        foreach (var element in project.Elements)
        {
            ArgumentNullException.ThrowIfNull(element);
            if (!byId.TryAdd(element.Id, element)) throw new ArgumentException("duplicate element id " + element.Id.ToString("D"), nameof(project));
        }
        return project.Elements.Where(element => element.Kind is Wall or Door or Window)
            .OrderBy(element => element.Id.ToString("D"), StringComparer.Ordinal)
            .Select(element => Describe(element, byId)).ToArray();
    }

    public static ElementFacts Describe(Element element, IReadOnlyDictionary<Guid, Element> project)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(project);
        var id = element.Id.ToString("D");
        var kind = element.Kind ?? "";
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var g = element.GeometryMetres ?? Array.Empty<double>();
        if (kind == Wall)
        {
            if (WallShape(g, "wall") is { } wallProblem) return new ElementFacts(id, kind, null, wallProblem.Code, wallProblem.Detail, null, null, values);
            values["length"] = Distance(g[0], g[1], g[2], g[3]);
            values["thickness"] = g[5];
            values["height"] = g[4];
            return Finish(new ElementFacts(id, kind, null, null, null, null, null, values));
        }
        if (kind is not (Door or Window)) return new ElementFacts(id, kind, null, null, null, null, null, values);
        if (g.Length != 6) return new ElementFacts(id, kind, null, ElementReasons.GeometryInvalid, kind + " geometry has " + Count(g.Length) + " values, [ax,ay,bx,by,sill,height] expected", null, null, values);
        if (g.Any(value => !double.IsFinite(value))) return new ElementFacts(id, kind, null, ElementReasons.GeometryInvalid, kind + " geometry has a non-finite value", null, null, values);
        values["width"] = Distance(g[0], g[1], g[2], g[3]);
        values["height"] = g[5];
        values["sill"] = g[4];

        var dependencies = element.Dependencies ?? Array.Empty<Guid>();
        var walls = dependencies.Distinct().Where(dependency => project.TryGetValue(dependency, out var candidate) && candidate.Kind == Wall).ToArray();
        var dangling = dependencies.Distinct().Where(dependency => !project.ContainsKey(dependency)).Select(dependency => dependency.ToString("D")).OrderBy(text => text, StringComparer.Ordinal).ToArray();
        if (walls.Length > 1)
            return Finish(new ElementFacts(id, kind, null, null, null, ElementReasons.HostAmbiguous,
                "depends on " + Count(walls.Length) + " walls: " + string.Join(", ", walls.Select(wall => wall.ToString("D")).OrderBy(text => text, StringComparer.Ordinal)), values));
        if (walls.Length == 0)
            return dangling.Length > 0
                ? Finish(new ElementFacts(id, kind, null, null, null, ElementReasons.HostOrphan, "no host wall; dangling dependency " + string.Join(", ", dangling), values))
                : Finish(new ElementFacts(id, kind, null, null, null, ElementReasons.HostMissing, "no dependency is a wall of this project", values));
        var host = project[walls[0]];
        var hostId = host.Id.ToString("D");
        var h = host.GeometryMetres ?? Array.Empty<double>();
        if (WallShape(h, "host wall " + hostId) is { } hostProblem)
            return Finish(new ElementFacts(id, kind, hostId, null, null,
                hostProblem.Code == ElementReasons.ShapeUnsupported ? ElementReasons.HostShapeUnsupported : ElementReasons.HostGeometryInvalid, hostProblem.Detail, values));
        double dx = h[2] - h[0], dy = h[3] - h[1];
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || !double.IsFinite(length))
            return Finish(new ElementFacts(id, kind, hostId, null, null, ElementReasons.HostDegenerate, "host wall " + hostId + " has a zero-length axis", values));
        double ux = dx / length, uy = dy / length;
        double Along(double x, double y) => (x - h[0]) * ux + (y - h[1]) * uy;
        double Across(double x, double y) => Math.Abs((x - h[0]) * uy - (y - h[1]) * ux);
        double ta = Along(g[0], g[1]), tb = Along(g[2], g[3]);
        // An end that coincides with the host end projects to the axis length exactly, whatever the rounding of ux, uy.
        if (g[0] == h[2] && g[1] == h[3]) ta = length;
        if (g[2] == h[2] && g[3] == h[3]) tb = length;
        if (g[0] == h[0] && g[1] == h[1]) ta = 0;
        if (g[2] == h[0] && g[3] == h[1]) tb = 0;
        values["start_clearance"] = Math.Min(ta, tb);
        values["end_clearance"] = length - Math.Max(ta, tb);
        values["head_clearance"] = h[4] - (g[4] + g[5]);
        values["axis_offset"] = Math.Max(Across(g[0], g[1]), Across(g[2], g[3]));
        return Finish(new ElementFacts(id, kind, hostId, null, null, null, null, values));
    }

    static ElementFacts Finish(ElementFacts facts)
    {
        // A quantity that overflowed to infinity is not a measurement.
        foreach (var (key, value) in facts.Metres.ToArray())
            if (!double.IsFinite(value)) ((Dictionary<string, double>)facts.Metres).Remove(key);
        return facts;
    }

    static (string Code, string Detail)? WallShape(double[] g, string subject)
    {
        if (g.Length != 6)
            return (ElementReasons.ShapeUnsupported, subject + " geometry has " + Count(g.Length) + " values; BIM-IR walls are straight axis segments [ax,ay,bx,by,height,thickness], so curved or other shapes (no arc length) are not supported");
        if (g.Any(value => !double.IsFinite(value))) return (ElementReasons.GeometryInvalid, subject + " geometry has a non-finite value");
        return null;
    }

    static double Distance(double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static double Zero(double value) => value == 0 ? 0.0 : value;

    static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}

public enum ElementCheckOutcome { Pass, Fail, NotEvaluable }

// One rule applied to one element. RuleId is the qualified "<skill id>/<rule id>"; SkillId, SkillVersion and LocalRuleId
// trace it to the manifest, BoundSource to the literal ("<skill>:rule:<rule id>"), parameter ("<skill>:parameter:<name>")
// or run-time override ("override:<skill>/<name>") the bound came from. Op is the manifest op name, Unit "mm", "m" or
// "ratio". Measured is the element value in Unit (null when not evaluable); Bound the numeric threshold (null for in).
// HostId names the host wall of an opening when known. Reason is null unless NotEvaluable (ElementReasons).
public sealed record ElementFinding(string ElementId, string ElementKind, string RuleId, string SkillId, string SkillVersion, string LocalRuleId,
    SkillSeverity Severity, ElementCheckOutcome Outcome, string? Reason, string Op, string Property, string Unit, double? Measured, double? Bound,
    string? BoundSource, string? HostId, string Message, string Detail, string? Citation = null);

// Findings ordered by element id then rule id; UnusedRuleIds lists, in ordinal order, element rules that matched no element.
public sealed record ElementValidationReport(IReadOnlyList<ElementFinding> Findings, IReadOnlyList<string> UnusedRuleIds)
{
    public IReadOnlyList<ElementFinding> Failures => Findings.Where(finding => finding.Outcome == ElementCheckOutcome.Fail).ToArray();
    public IReadOnlyList<ElementFinding> NotEvaluable => Findings.Where(finding => finding.Outcome == ElementCheckOutcome.NotEvaluable).ToArray();
    // Every finding was evaluated (a not_evaluable rule is never compliance).
    public bool Complete => Findings.All(finding => finding.Outcome != ElementCheckOutcome.NotEvaluable);
    public bool Compliant => Complete && Findings.All(finding => finding.Outcome == ElementCheckOutcome.Pass);
}

public static class SkillElementValidator
{
    public const string RatioUnit = "ratio";

    // Resolves one manifest on its own (it must have no requirements) and validates the project against it.
    public static ElementValidationReport Validate(SkillManifest manifest, Project project, IReadOnlyDictionary<string, double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var resolution = SkillResolver.Resolve(new SkillCatalog(new[] { manifest }), new[] { new SkillRequest(manifest.Id) });
        if (!resolution.Succeeded)
            throw new InvalidOperationException("skill " + manifest.Id + " does not resolve on its own: " + string.Join("; ", resolution.Issues.Select(issue => issue.Code + "@" + issue.Subject)));
        return Validate(resolution, project, parameters);
    }

    // parameters overrides skill parameters at run time without changing the manifest: keys are "<skill id>/<parameter>",
    // values must be finite, within the parameter's [min, max] and integral for integer parameters, else ArgumentException.
    public static ElementValidationReport Validate(SkillResolution resolution, Project project, IReadOnlyDictionary<string, double>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(project);
        if (!resolution.Succeeded) throw new ArgumentException("resolution has issues", nameof(resolution));
        var overrides = Overrides(resolution, parameters);
        var facts = ElementFacts.FromProject(project);
        // FromProject covers walls, doors and windows; elements of any other kind a rule targets are described too, so the
        // rule is reported property_unknown on them instead of being skipped.
        var byId = project.Elements.ToDictionary(element => element.Id);
        var elementRules = resolution.Rules.Where(rule => rule.Rule.Target.ElementKind is not null).OrderBy(rule => rule.QualifiedId, StringComparer.Ordinal).ToArray();
        var targeted = elementRules.Select(rule => rule.Rule.Target.ElementKind!).ToHashSet(StringComparer.Ordinal);
        var subjects = facts.Concat(project.Elements.Where(element => element.Kind is not (ElementFacts.Wall or ElementFacts.Door or ElementFacts.Window) && targeted.Contains(element.Kind))
                .Select(element => ElementFacts.Describe(element, byId)))
            .OrderBy(subject => subject.Id, StringComparer.Ordinal).ToArray();
        var versions = resolution.Skills.ToDictionary(skill => skill.Id, skill => skill.Version.ToString(), StringComparer.Ordinal);
        var findings = new List<ElementFinding>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            foreach (var rule in elementRules.Where(rule => string.Equals(rule.Rule.Target.ElementKind, subject.Kind, StringComparison.Ordinal)))
            {
                used.Add(rule.QualifiedId);
                findings.Add(Evaluate(subject, rule, versions.TryGetValue(rule.SkillId, out var version) ? version : "", overrides));
            }
        }
        var unused = elementRules.Select(rule => rule.QualifiedId).Where(id => !used.Contains(id)).ToArray();
        return new ElementValidationReport(findings.ToArray(), unused);
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

    static ElementFinding Evaluate(ElementFacts element, SkillEffectiveRule effective, string version, Dictionary<string, double> overrides)
    {
        var rule = effective.Rule;
        var check = rule.Check;
        double? bound = effective.Threshold is { Kind: SkillValueKind.Number } threshold ? threshold.Number : null;
        string? boundSource = check.Op == SkillCheckOp.In ? null : effective.ThresholdSource?.ToString() ?? effective.Source.ToString();
        if (effective.ThresholdSource is { Origin: SkillTrace.ParameterOrigin } parameterSource)
        {
            var key = parameterSource.SkillId + "/" + parameterSource.Key;
            if (overrides.TryGetValue(key, out var overridden)) { bound = overridden; boundSource = "override:" + key; }
        }
        var unit = check.Op == SkillCheckOp.RatioMin ? RatioUnit : ElementFacts.Split(check.Property)?.Unit ?? "";
        ElementFinding Finding(ElementCheckOutcome outcome, string? reason, double? measured, string detail) =>
            new(element.Id, element.Kind, effective.QualifiedId, effective.SkillId, version, rule.Id, rule.Severity, outcome, reason, SkillParser.OpName(check.Op),
                check.Property, unit, measured, bound, boundSource, element.HostId, rule.Message, detail, rule.Citation);

        if (!ElementFacts.IsProperty(element.Kind, check.Property))
        {
            _ = element.Value(check.Property, out _, out var unknown);
            return Finding(ElementCheckOutcome.NotEvaluable, ElementReasons.PropertyUnknown, null, unknown);
        }
        if (check.Over is not null && !ElementFacts.IsProperty(element.Kind, check.Over))
        {
            _ = element.Value(check.Over, out _, out var unknown);
            return Finding(ElementCheckOutcome.NotEvaluable, ElementReasons.PropertyUnknown, null, "denominator: " + unknown);
        }
        IReadOnlyList<double> allowed = Array.Empty<double>();
        double limit = 0;
        if (check.Op == SkillCheckOp.In)
        {
            if (check.Values is null || check.Values.Any(value => value.Kind != SkillValueKind.Number))
                return Finding(ElementCheckOutcome.NotEvaluable, ElementReasons.ThresholdNotNumber, null, "in values must all be numbers for element quantities");
            allowed = check.Values.Select(value => value.Number).ToArray();
        }
        else if (bound is { } numeric) limit = numeric;
        else return Finding(ElementCheckOutcome.NotEvaluable, ElementReasons.ThresholdNotNumber, null, "rule threshold is not a number");

        if (element.Value(check.Property, out var reason, out var why) is not { } value) return Finding(ElementCheckOutcome.NotEvaluable, reason, null, why);
        var measured = value;
        var subject = check.Property;
        if (check.Op == SkillCheckOp.RatioMin)
        {
            if (element.Value(check.Over!, out var overReason, out var overWhy) is not { } over) return Finding(ElementCheckOutcome.NotEvaluable, overReason, null, overWhy);
            if (over <= 0) return Finding(ElementCheckOutcome.NotEvaluable, ElementReasons.RatioUndefined, null, check.Over + " is " + Text(over) + ", so the ratio is undefined");
            measured = value / over;
            subject = check.Property + " / " + check.Over;
        }
        if (check.Op == SkillCheckOp.In)
        {
            var member = allowed.Any(candidate => Math.Abs(measured - candidate) <= Slack(candidate));
            return Finding(member ? ElementCheckOutcome.Pass : ElementCheckOutcome.Fail, null, measured,
                subject + " " + Text(measured) + " " + unit + (member ? " in " : " not in ") + "[" + string.Join(", ", allowed.Select(Text)) + "]");
        }
        var slack = Slack(limit);
        bool pass;
        string relation;
        switch (check.Op)
        {
            case SkillCheckOp.Max: pass = measured <= limit + slack; relation = pass ? "<=" : ">"; break;
            case SkillCheckOp.EqualTo: pass = Math.Abs(measured - limit) <= slack; relation = pass ? "==" : "!="; break;
            default: pass = measured >= limit - slack; relation = pass ? ">=" : "<"; break;
        }
        return Finding(pass ? ElementCheckOutcome.Pass : ElementCheckOutcome.Fail, null, measured,
            subject + " " + Text(measured) + " " + relation + " " + Text(limit) + " " + unit + " (" + SkillParser.OpName(check.Op) + " bound from " + boundSource + ")");
    }

    static double Slack(double limit) => SkillRoomValidator.RelativeTolerance * Math.Max(1, Math.Abs(limit));

    static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
