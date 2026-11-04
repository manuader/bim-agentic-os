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
        // Red stub (P5-05): no quantities are measured yet.
        return new ElementFacts(element.Id.ToString("D"), element.Kind ?? "", null, null, null, null, null, new Dictionary<string, double>(StringComparer.Ordinal));
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
        // Red stub (P5-05): element rules are not evaluated yet.
        _ = parameters;
        return new ElementValidationReport(Array.Empty<ElementFinding>(), Array.Empty<string>());
    }

    static double Slack(double limit) => SkillRoomValidator.RelativeTolerance * Math.Max(1, Math.Abs(limit));

    static string Text(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
