using Bim.Core;
using Bim.Generative;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
// P7-01 constraint space planner oracle: the checked-in apartment-2br brief, every constraint type (area range,
// minimum side, required/forbidden/preferred adjacency, footprint, levels, counts, budget, planner rules), canonical
// output determinism across runs, seeds and cultures, typed infeasible results, non-overlap and in-site invariants
// over many seeds and generated briefs, and a 30-room performance bound; P7-02 alternatives in AlternativeTests.cs, P7-03
// apartment prototypes in PrototypeTests.cs, P7-04 circulation in CirculationTests.cs, P7-05 facade openings in FacadeTests.cs, P7-06
// facade windows into the prototype BIM-IR in FacadeIrTests.cs.
// Unit-tested evidence only.
int passed = 0, failed = 0;

void Check(bool condition, string name, string detail = "")
{
    if (condition) { Console.WriteLine("PASS " + name); passed++; return; }
    Console.WriteLine("::error::generative FAIL " + name + (detail.Length > 0 ? " | " + detail : ""));
    failed++;
}

string RepoRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
    }
    throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
}

BriefValue<double> V(double value) => new(value);
BriefRoom Room(string id, double? target = null, double? min = null, double? max = null, int? count = null, int? level = null, string? name = null) =>
    new(id, name ?? id, count is { } c ? new BriefValue<int>(c) : null, target is { } t ? V(t) : null, min is { } low ? V(low) : null,
        max is { } high ? V(high) : null, level is { } l ? new BriefValue<int>(l) : null);
BriefAdjacency Adj(string a, string b, BriefAdjacencyKind kind) => new(a, b, kind);
ProjectBrief Brief(string name, double? width, double? depth, BriefRoom[] rooms, BriefAdjacency[]? adjacencies = null, int? levels = null, double? budget = null) =>
    new(1, name, new BriefConstraints(null, width is { } w && depth is { } d ? new BriefSite(V(w), V(d)) : null, levels is { } n ? new BriefValue<int>(n) : null,
        budget is { } b ? V(b) : null), rooms, adjacencies ?? Array.Empty<BriefAdjacency>());

string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
string Violations(SpacePlan plan) => string.Join("; ", plan.Violations.Select(v => v.Code + "@" + v.Subject + " " + v.Detail).Take(6));
SpacePlan Attempt(ProjectBrief brief, int seed = 1, PlannerOptions? options = null)
{
    try { return SpacePlanner.Plan(brief, seed, options); }
    catch (Exception error)
    {
        return new SpacePlan(SpacePlanStatus.Infeasible, "threw", seed, 0, null, 0, Array.Empty<PlannedRoom>(),
            new[] { new PlanConstraint("threw " + error.GetType().Name, "plan", true, false, error.Message) }, 0);
    }
}
PlannedRoom? Find(SpacePlan plan, string instance) => plan.Rooms.FirstOrDefault(room => room.InstanceId == instance);
// Shared wall length of two instances on the same level; -1 when either is missing or they are on different levels.
double Shared(SpacePlan plan, string a, string b) =>
    Find(plan, a) is { } x && Find(plan, b) is { } y && x.Level == y.Level ? SharedEdge(x.Rect, y.Rect) : -1;
bool Has(SpacePlan plan, string code, string subject, bool hard, bool satisfied) =>
    plan.Constraints.Any(c => c.Code == code && c.Subject == subject && c.Hard == hard && c.Satisfied == satisfied && c.Detail.Length > 0);
double ShortSide(PlannedRoom? room) => room is null ? -1 : Math.Min(room.Rect.Width, room.Rect.Depth);
string Layout(SpacePlan plan) => string.Join(";", plan.Rooms.Select(r => r.InstanceId + "@" + r.Level.ToString(CultureInfo.InvariantCulture) + ":" + F(r.Rect.X) + "," + F(r.Rect.Y) + "," + F(r.Rect.Width) + "," + F(r.Rect.Depth)));

// Independent oracle for a feasible plan: counts; areas against the brief and the planner rules (rule minimum area,
// rule minimum side, and that side squared raising the minimum area), checked exactly on the output rectangles;
// strict maxima; footprint containment; level range; pairwise non-overlap per level; bit-identical shared edges;
// adjacency semantics and the area budget. Null when everything holds.
string? Invariants(ProjectBrief brief, SpacePlan plan, PlannerOptions? options = null)
{
    if (!plan.IsFeasible) return "not feasible: " + Violations(plan);
    var door = options?.DoorWidthMetres ?? PlannerOptions.DefaultDoorWidthMetres;
    var rules = options?.Rules?.Rules ?? Array.Empty<PlannerRule>();
    var problems = new List<string>();
    // Preferred adjacencies are soft: a feasible plan may miss them, but never a hard constraint.
    if (plan.HardViolations.Count > 0 || plan.Violations.Any(v => v.Hard)) problems.Add("feasible plan lists hard violations: " + Violations(plan));
    if (plan.Constraints.Any(c => c.Hard == (c.Code == PlanCodes.AdjacencyPreferred))) problems.Add("only preferred adjacencies may be soft");
    var expected = brief.Rooms.Sum(room => room.EffectiveCount);
    if (plan.Rooms.Count != expected) problems.Add("rooms " + plan.Rooms.Count.ToString(CultureInfo.InvariantCulture) + " != " + expected.ToString(CultureInfo.InvariantCulture));
    if (plan.Footprint is not { } footprint) return "feasible plan without footprint";
    var levels = brief.Constraints.Levels?.Value ?? 1;
    foreach (var room in plan.Rooms)
    {
        var spec = brief.Rooms.FirstOrDefault(r => r.Id == room.RoomId);
        if (spec is null) { problems.Add("unknown room " + room.RoomId); continue; }
        var rect = room.Rect;
        double minArea = spec.MinAreaSquareMetres?.Value ?? 0, minSide = 0;
        foreach (var rule in rules.Where(candidate => (candidate.RoomId is null || candidate.RoomId == spec.Id) && (candidate.NamePattern is null || Wildcard(candidate.NamePattern, spec.Name))))
        {
            minArea = Math.Max(minArea, rule.MinAreaSquareMetres ?? 0);
            minSide = Math.Max(minSide, rule.MinSideMetres ?? 0);
        }
        minArea = Math.Max(minArea, minSide * minSide);
        if (!(rect.Width > 0 && rect.Depth > 0)) problems.Add(room.InstanceId + " degenerate");
        if (rect.Area != room.AreaSquareMetres || rect.Width * rect.Depth < minArea) problems.Add(room.InstanceId + " area " + F(rect.Area) + " below min " + F(minArea));
        if (Math.Min(rect.Width, rect.Depth) < minSide) problems.Add(room.InstanceId + " side " + F(Math.Min(rect.Width, rect.Depth)) + " below min " + F(minSide));
        if (spec.MaxAreaSquareMetres is { } max && rect.Area > max.Value + 1e-9) problems.Add(room.InstanceId + " area " + F(rect.Area) + " above max " + F(max.Value));
        if (rect.X < 0 || rect.Y < 0 || rect.Right > footprint.WidthMetres || rect.Top > footprint.DepthMetres) problems.Add(room.InstanceId + " outside footprint");
        if (room.Level < 0 || room.Level >= levels) problems.Add(room.InstanceId + " level out of range");
    }
    for (var i = 0; i < plan.Rooms.Count; i++)
        for (var j = i + 1; j < plan.Rooms.Count; j++)
        {
            var (x, y) = (plan.Rooms[i], plan.Rooms[j]);
            if (x.Level != y.Level) continue;
            if (Overlaps(x.Rect, y.Rect)) problems.Add(x.InstanceId + " overlaps " + y.InstanceId);
            // Edges that nearly coincide must coincide exactly: X + Width of one equals X of the other, bit for bit.
            foreach (var (edge, start) in new[] { (x.Rect.Right, y.Rect.X), (y.Rect.Right, x.Rect.X), (x.Rect.Top, y.Rect.Y), (y.Rect.Top, x.Rect.Y) })
                if (Math.Abs(edge - start) < 1e-6 && BitConverter.DoubleToInt64Bits(edge) != BitConverter.DoubleToInt64Bits(start))
                    problems.Add(x.InstanceId + "/" + y.InstanceId + " edges differ by " + (edge - start).ToString("R", CultureInfo.InvariantCulture));
        }
    foreach (var adjacency in brief.Adjacencies)
    {
        var a = plan.Rooms.Where(room => room.RoomId == adjacency.A).ToArray();
        var b = plan.Rooms.Where(room => room.RoomId == adjacency.B).ToArray();
        if (adjacency.Kind == BriefAdjacencyKind.Required)
        {
            foreach (var (side, other) in new[] { (a, b), (b, a) })
                foreach (var room in side)
                    if (!other.Any(partner => partner.Level == room.Level && SharedEdge(room.Rect, partner.Rect) >= door - 1e-6)) problems.Add(room.InstanceId + " lacks a door-width wall to " + string.Join(",", other.Select(o => o.InstanceId)));
        }
        else if (adjacency.Kind == BriefAdjacencyKind.Forbidden)
        {
            foreach (var x in a)
                foreach (var y in b)
                    if (x.Level == y.Level && SharedEdge(x.Rect, y.Rect) > 1e-6) problems.Add(x.InstanceId + " touches forbidden " + y.InstanceId);
        }
    }
    if (brief.Constraints.TotalAreaBudgetSquareMetres is { } budget && plan.Rooms.Sum(room => room.AreaSquareMetres) > budget.Value + 1e-9) problems.Add("budget exceeded");
    return problems.Count == 0 ? null : string.Join("; ", problems.Take(6));
}

void Infeasible(string name, SpacePlan plan, string code, string? subject = null)
{
    Check(plan.Status == SpacePlanStatus.Infeasible && !plan.IsFeasible && plan.Rooms.Count == 0 && plan.Constraints.Count > 0 && plan.Constraints.All(c => !c.Satisfied)
        && plan.Violations.Any(c => c.Code == code && (subject is null || c.Subject == subject) && c.Detail.Length > 0), name,
        "status " + plan.Status + " rooms " + plan.Rooms.Count.ToString(CultureInfo.InvariantCulture) + " | " + Violations(plan));
}

try
{
    var root = RepoRoot();
    var examplePath = Path.Combine(root, "examples", "brief", "apartment-2br.json");
    var example = BriefParser.Parse(File.ReadAllText(examplePath));
    Check(!BriefValidator.HasErrors(BriefValidator.Validate(example)), "example apartment-2br brief validates");

    // ---- apartment-2br ----
    var apartment = Attempt(example, 1);
    Check(apartment.IsFeasible, "apartment-2br plans feasibly with seed 1", Violations(apartment));
    Check(Invariants(example, apartment) is null, "apartment-2br plan honours every invariant", Invariants(example, apartment) ?? "");
    Check(apartment.Rooms.Select(r => r.InstanceId).SequenceEqual(example.Rooms.Select(r => r.Id)) && apartment.Rooms.All(r => r.Instance == 1 && r.Level == 0),
        "apartment-2br plans the 7 rooms in brief order on level 0", Layout(apartment));
    Check(apartment.Rooms.All(r => r.Name == example.Rooms.First(b => b.Id == r.RoomId).Name), "apartment-2br rooms carry brief names");
    Check(apartment.Footprint == new PlanFootprint(8.66, 20) && apartment.Levels == 1 && apartment.DoorWidthMetres == 0.9 && apartment.Seed == 1 && apartment.BriefName == example.Name,
        "apartment-2br uses the site as footprint, one level and the 0.9 m default door");
    Check(Shared(apartment, "kitchen", "living_dining") >= 0.9 - 1e-6, "apartment-2br kitchen shares a door-width wall with living-dining", F(Shared(apartment, "kitchen", "living_dining")));
    Check(Shared(apartment, "hall", "living_dining") >= 0.9 - 1e-6, "apartment-2br hall shares a door-width wall with living-dining", F(Shared(apartment, "hall", "living_dining")));
    var kitchenBedroom = Shared(apartment, "kitchen", "bedroom_main");
    Check(kitchenBedroom >= 0 && kitchenBedroom <= 1e-6, "apartment-2br kitchen shares no edge with the main bedroom", F(kitchenBedroom));
    Check(Has(apartment, PlanCodes.AdjacencyRequired, "kitchen/living_dining", true, true) && Has(apartment, PlanCodes.AdjacencyRequired, "hall/living_dining", true, true),
        "apartment-2br reports both required adjacencies satisfied");
    Check(Has(apartment, PlanCodes.AdjacencyForbidden, "kitchen/bedroom_main", true, true), "apartment-2br reports the forbidden adjacency satisfied");
    Check(apartment.Constraints.Count(c => c.Code == PlanCodes.AdjacencyPreferred && !c.Hard) == 2
        && apartment.Constraints.Any(c => c.Code == PlanCodes.AdjacencyPreferred && c.Subject == "bedroom_main/bathroom"), "apartment-2br scores both preferred adjacencies as soft constraints");
    Check(apartment.Constraints.Count(c => c.Code == PlanCodes.AreaMin && c.Satisfied && c.Hard) == 7 && apartment.Constraints.Count(c => c.Code == PlanCodes.AreaMax && c.Satisfied) == 2
        && apartment.Constraints.Count(c => c.Code == PlanCodes.InSite && c.Satisfied) == 7 && Has(apartment, PlanCodes.NoOverlap, "level 0", true, true)
        && Has(apartment, PlanCodes.AreaBudget, "plan", true, true), "apartment-2br reports area, site, overlap and budget constraints");
    Check(!apartment.Constraints.Any(c => c.Code == PlanCodes.SideMin), "apartment-2br without rules has no minimum-side constraints");
    Check(apartment.Score > 0 && apartment.Score <= 1, "apartment-2br score lies in (0, 1]", F(apartment.Score));
    var targetMiss = apartment.Rooms.Where(r => Math.Abs(r.AreaSquareMetres - example.Rooms.First(b => b.Id == r.RoomId).TargetAreaSquareMetres!.Value) > 0.01 * example.Rooms.First(b => b.Id == r.RoomId).TargetAreaSquareMetres!.Value).Select(r => r.InstanceId + " " + F(r.AreaSquareMetres));
    Check(apartment.Rooms.Count == 7 && !targetMiss.Any(), "apartment-2br rooms reach their target areas within 1%", string.Join(", ", targetMiss));

    // ---- planner rules (min area / min side by room id or name pattern) ----
    var rules = new PlannerRules(new[]
    {
        new PlannerRule(NamePattern: "*BEDROOM*", MinSideMetres: 2.4, Reference: "test rule: bedroom side"),
        new PlannerRule(RoomId: "bathroom", MinAreaSquareMetres: 4, MinSideMetres: 1.2),
        new PlannerRule(RoomId: "laundry", MinAreaSquareMetres: 3.5),
        new PlannerRule(NamePattern: "Hal?", MinSideMetres: 1.5),
        new PlannerRule(RoomId: "kitchen", NamePattern: "Bath*", MinSideMetres: 3),
    });
    var ruleOptions = new PlannerOptions(Rules: rules);
    var ruled = Attempt(example, 1, ruleOptions);
    Check(ruled.IsFeasible && Invariants(example, ruled, ruleOptions) is null, "apartment-2br with planner rules plans feasibly", Invariants(example, ruled, ruleOptions) ?? "");
    Check(ShortSide(Find(ruled, "bedroom_main")) >= 2.4 - 1e-9 && ShortSide(Find(ruled, "bedroom_2")) >= 2.4 - 1e-9, "name-pattern rule gives both bedrooms a 2.4 m minimum side",
        F(ShortSide(Find(ruled, "bedroom_main"))) + " " + F(ShortSide(Find(ruled, "bedroom_2"))));
    Check(Find(ruled, "bathroom") is { } bath && bath.AreaSquareMetres >= 4 && ShortSide(bath) >= 1.2 - 1e-9, "room-id rule raises bathroom minimum area and side");
    Check(Find(ruled, "laundry") is { } laundry && laundry.AreaSquareMetres >= 3.5, "rule minimum area above the brief target wins", F(Find(ruled, "laundry")?.AreaSquareMetres ?? -1));
    Check(ShortSide(Find(ruled, "hall")) >= 1.5 - 1e-9 && Has(ruled, PlanCodes.SideMin, "hall", true, true), "'?' wildcard rule matches the hall");
    Check(Has(ruled, PlanCodes.SideMin, "bedroom_main", true, true) && Has(ruled, PlanCodes.SideMin, "bedroom_2", true, true) && Has(ruled, PlanCodes.SideMin, "bathroom", true, true),
        "rule minimum sides are reported as satisfied constraints");
    Check(!ruled.Constraints.Any(c => c.Code == PlanCodes.SideMin && (c.Subject == "kitchen" || c.Subject == "living_dining" || c.Subject == "laundry")),
        "a rule with id and pattern needs both to match; unmatched rooms get no side constraint");
    Check(ruled.Constraints.Any(c => c.Code == PlanCodes.AreaMin && c.Subject == "laundry" && c.Detail.Contains("rules[2]", StringComparison.Ordinal)), "area constraint names the binding rule");

    // ---- determinism ----
    var canonical = SpacePlanSerializer.Serialize(Attempt(example, 7));
    var reparsed = SpacePlanSerializer.Serialize(Attempt(BriefParser.Parse(File.ReadAllText(examplePath)), 7));
    Check(canonical == reparsed, "same brief and seed give byte-identical canonical output");
    Check(SpacePlanSerializer.Serialize(Attempt(example, 7)) == canonical && SpacePlanSerializer.Serialize(Attempt(example, 7, new PlannerOptions())) == canonical,
        "repeated runs and explicit default options reproduce the same bytes");
    var ruledCanonical = SpacePlanSerializer.Serialize(Attempt(example, 3, ruleOptions));
    Check(ruledCanonical == SpacePlanSerializer.Serialize(Attempt(example, 3, new PlannerOptions(Rules: new PlannerRules(rules.Rules.ToArray())))), "plans with rules are deterministic");
    var culture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        Check(SpacePlanSerializer.Serialize(Attempt(example, 7)) == canonical, "canonical output does not depend on the current culture");
    }
    finally { CultureInfo.CurrentCulture = culture; }
    var layouts = new HashSet<string>(StringComparer.Ordinal);
    for (var seed = 0; seed < 12; seed++) layouts.Add(Layout(Attempt(example, seed)));
    Check(layouts.Count >= 2, "different seeds explore different layouts", layouts.Count.ToString(CultureInfo.InvariantCulture));
    Check(Layout(Attempt(example, -5)) == Layout(Attempt(example, -5)) && Attempt(example, int.MinValue).IsFeasible && Attempt(example, int.MaxValue).IsFeasible, "negative and extreme seeds are valid and deterministic");

    // ---- canonical JSON ----
    using (var json = JsonDocument.Parse(canonical))
    {
        var top = json.RootElement;
        Check(top.GetProperty("plan_version").GetInt32() == 1 && top.GetProperty("status").GetString() == "feasible" && top.GetProperty("brief").GetString() == example.Name
            && top.GetProperty("seed").GetInt32() == 7 && top.GetProperty("levels").GetInt32() == 1 && top.GetProperty("door_width_m").GetDouble() == 0.9,
            "canonical JSON carries version, status, brief, seed, levels and door width");
        var rooms = top.GetProperty("rooms");
        var plan7 = Attempt(example, 7);
        Check(rooms.GetArrayLength() == 7 && rooms.EnumerateArray().Zip(plan7.Rooms).All(pair => pair.First.GetProperty("instance").GetString() == pair.Second.InstanceId
            && pair.First.GetProperty("x_m").GetDouble() == pair.Second.Rect.X && pair.First.GetProperty("y_m").GetDouble() == pair.Second.Rect.Y
            && pair.First.GetProperty("width_m").GetDouble() == pair.Second.Rect.Width && pair.First.GetProperty("depth_m").GetDouble() == pair.Second.Rect.Depth
            && pair.First.GetProperty("area_m2").GetDouble() == pair.Second.AreaSquareMetres), "canonical JSON rooms reproduce the plan rectangles exactly");
        Check(top.GetProperty("constraints").GetArrayLength() == plan7.Constraints.Count && top.GetProperty("footprint").GetProperty("width_m").GetDouble() == 8.66,
            "canonical JSON lists every constraint and the footprint");
        Check(!canonical.Contains('\n', StringComparison.Ordinal) && canonical.StartsWith("{\"plan_version\":1,\"status\":\"feasible\",", StringComparison.Ordinal), "canonical JSON is compact with fixed member order");
    }

    // ---- invariants over many seeds ----
    string? firstSeedProblem = null;
    var feasibleSeeds = 0;
    for (var seed = 0; seed < 40; seed++)
    {
        var plan = Attempt(example, seed);
        if (plan.IsFeasible) feasibleSeeds++;
        var problem = Invariants(example, plan);
        if (problem is not null && firstSeedProblem is null) firstSeedProblem = "seed " + seed.ToString(CultureInfo.InvariantCulture) + ": " + problem;
    }
    Check(feasibleSeeds == 40 && firstSeedProblem is null, "apartment-2br is feasible, non-overlapping and in-site for 40 seeds", firstSeedProblem ?? "");
    string? firstRuledProblem = null;
    for (var seed = 100; seed < 120; seed++)
        if (Invariants(example, Attempt(example, seed, ruleOptions), ruleOptions) is { } problem && firstRuledProblem is null) firstRuledProblem = "seed " + seed.ToString(CultureInfo.InvariantCulture) + ": " + problem;
    Check(firstRuledProblem is null, "apartment-2br with rules honours every invariant for 20 seeds", firstRuledProblem ?? "");

    // Generated briefs: a feasible plan must honour every invariant; an infeasible one carries only violations.
    var lcg = 0x2545F4914F6CDD1DUL;
    int Next(int bound)
    {
        lcg = lcg * 6364136223846793005UL + 1442695040888963407UL;
        return (int)((lcg >> 33) % (ulong)bound);
    }
    int generatedFeasible = 0, generatedTotal = 0;
    string? generatedProblem = null;
    for (var index = 0; index < 30; index++)
    {
        var count = 3 + Next(6);
        var rooms = new List<BriefRoom>();
        double total = 0;
        for (var r = 0; r < count; r++)
        {
            var target = 4 + Next(12);
            var copies = Next(5) == 0 ? 2 : 1;
            rooms.Add(Room("g" + r.ToString(CultureInfo.InvariantCulture), target, target * 0.8, r % 2 == 0 ? target * 1.25 : null, copies));
            total += target * copies;
        }
        var adjacencies = new List<BriefAdjacency>();
        for (var r = 0; r + 1 < count; r++)
        {
            var kind = Next(4);
            if (kind > 0) adjacencies.Add(Adj("g" + r.ToString(CultureInfo.InvariantCulture), "g" + (r + 1).ToString(CultureInfo.InvariantCulture), (BriefAdjacencyKind)(kind - 1)));
        }
        var width = Math.Round(Math.Sqrt(total * 1.5) + Next(4), 1);
        var depth = Math.Round(total * 1.5 / width + Next(4), 1);
        var generated = Brief("generated " + index.ToString(CultureInfo.InvariantCulture), width, depth, rooms.ToArray(), adjacencies.ToArray());
        var plan = Attempt(generated, index);
        generatedTotal++;
        string? problem;
        if (plan.IsFeasible) { generatedFeasible++; problem = Invariants(generated, plan); }
        else problem = plan.Rooms.Count == 0 && plan.Constraints.Count > 0 && plan.Constraints.All(c => !c.Satisfied) ? null : "malformed infeasible result: " + Violations(plan);
        if (SpacePlanSerializer.Serialize(Attempt(generated, index)) != SpacePlanSerializer.Serialize(plan)) problem ??= "not deterministic";
        if (problem is not null && generatedProblem is null) generatedProblem = generated.Name + ": " + problem;
    }
    Check(generatedProblem is null, "30 generated briefs give valid, deterministic feasible or infeasible results", generatedProblem ?? "");
    Check(generatedFeasible * 3 >= generatedTotal, "at least a third of the generated briefs plan feasibly", generatedFeasible.ToString(CultureInfo.InvariantCulture) + "/" + generatedTotal.ToString(CultureInfo.InvariantCulture));

    // ---- constraint types ----
    var ranged = Brief("ranged", 6, 6, new[] { Room("study", min: 12, max: 14), Room("store", min: 3), Room("spare") });
    var rangedPlan = Attempt(ranged, 2);
    Check(rangedPlan.IsFeasible && Invariants(ranged, rangedPlan) is null, "rooms with only min/max or no areas plan feasibly", Invariants(ranged, rangedPlan) ?? "");
    Check(Find(rangedPlan, "study") is { } study && study.AreaSquareMetres >= 12 && study.AreaSquareMetres <= 14 && Has(rangedPlan, PlanCodes.AreaMax, "study", true, true),
        "min/max range bounds the study area", F(Find(rangedPlan, "study")?.AreaSquareMetres ?? -1));
    Check(Find(rangedPlan, "store") is { } store && store.AreaSquareMetres >= 3 && Find(rangedPlan, "spare") is { AreaSquareMetres: > 0 }, "min-only and area-free rooms get positive area");

    var doorBrief = Brief("door", 8, 8, new[] { Room("a", 12, 10), Room("b", 12, 10), Room("c", 6, 5) }, new[] { Adj("a", "b", BriefAdjacencyKind.Required) });
    var doorOptions = new PlannerOptions(DoorWidthMetres: 2.0);
    var doorPlan = Attempt(doorBrief, 4, doorOptions);
    Check(doorPlan.IsFeasible && Shared(doorPlan, "a", "b") >= 2.0 - 1e-6 && doorPlan.DoorWidthMetres == 2.0, "configurable door width: required pair shares at least 2.0 m", F(Shared(doorPlan, "a", "b")));

    var forbiddenBrief = Brief("forbidden", 10, 10, new[] { Room("a", 10, 9), Room("b", 10, 9), Room("c", 10, 9), Room("d", 10, 9) },
        new[] { Adj("a", "b", BriefAdjacencyKind.Forbidden), Adj("c", "d", BriefAdjacencyKind.Forbidden) });
    var forbiddenPlan = Attempt(forbiddenBrief, 5);
    Check(forbiddenPlan.IsFeasible && Shared(forbiddenPlan, "a", "b") is >= 0 and <= 1e-6 && Shared(forbiddenPlan, "c", "d") is >= 0 and <= 1e-6
        && Has(forbiddenPlan, PlanCodes.AdjacencyForbidden, "a/b", true, true), "forbidden pairs share no edge", Layout(forbiddenPlan));

    var preferredBrief = Brief("preferred", 8, 8, new[] { Room("a", 10), Room("b", 10), Room("c", 10) }, new[] { Adj("a", "c", BriefAdjacencyKind.Preferred) });
    var preferredPlan = Attempt(preferredBrief, 6);
    Check(preferredPlan.IsFeasible && Has(preferredPlan, PlanCodes.AdjacencyPreferred, "a/c", false, true) && Shared(preferredPlan, "a", "c") >= 0.9 - 1e-6,
        "an achievable preferred adjacency is met and reported as a satisfied soft constraint", Violations(preferredPlan));

    var noSite = Brief("no site", null, null, new[] { Room("a", 10), Room("b", 8) }, new[] { Adj("a", "b", BriefAdjacencyKind.Required) });
    var footprintOptions = new PlannerOptions(Footprint: new PlanFootprint(6, 5));
    var noSitePlan = Attempt(noSite, 1, footprintOptions);
    Check(noSitePlan.IsFeasible && noSitePlan.Footprint == new PlanFootprint(6, 5) && Invariants(noSite, noSitePlan, footprintOptions) is null,
        "explicit footprint is used when the brief has no site", Invariants(noSite, noSitePlan, footprintOptions) ?? "");
    var inner = Brief("inner footprint", 20, 20, new[] { Room("a", 12), Room("b", 12), Room("c", 6) });
    var innerOptions = new PlannerOptions(Footprint: new PlanFootprint(7, 6));
    var innerPlan = Attempt(inner, 1, innerOptions);
    Check(innerPlan.IsFeasible && innerPlan.Rooms.All(r => r.Rect.Right <= 7 + 1e-9 && r.Rect.Top <= 6 + 1e-9), "a footprint inside the site bounds the rooms", Layout(innerPlan));

    var levelled = Brief("two levels", 6, 6, new[] { Room("a", 10, level: 0), Room("b", 10, level: 1), Room("c", 8), Room("d", 8) },
        new[] { Adj("b", "c", BriefAdjacencyKind.Required) }, levels: 2);
    var levelledPlan = Attempt(levelled, 2);
    Check(levelledPlan.IsFeasible && Invariants(levelled, levelledPlan) is null && levelledPlan.Levels == 2, "two-level brief plans feasibly", Invariants(levelled, levelledPlan) ?? "");
    Check(Find(levelledPlan, "a")?.Level == 0 && Find(levelledPlan, "b")?.Level == 1 && Find(levelledPlan, "c")?.Level == 1, "preferred levels are honoured and a required partner follows its room", Layout(levelledPlan));
    Check(Has(levelledPlan, PlanCodes.NoOverlap, "level 0", true, true) && Has(levelledPlan, PlanCodes.NoOverlap, "level 1", true, true), "non-overlap is checked per level");

    var counted = Brief("counted", 8, 8, new[] { Room("bedroom", 10, 9, count: 2), Room("hall", 5, 4) }, new[] { Adj("bedroom", "hall", BriefAdjacencyKind.Required) });
    var countedPlan = Attempt(counted, 3);
    Check(countedPlan.IsFeasible && countedPlan.Rooms.Select(r => r.InstanceId).SequenceEqual(new[] { "bedroom#1", "bedroom#2", "hall" })
        && Find(countedPlan, "bedroom#2") is { RoomId: "bedroom", Instance: 2 }, "count 2 gives two numbered instances", Layout(countedPlan));
    Check(Shared(countedPlan, "bedroom#1", "hall") >= 0.9 - 1e-6 && Shared(countedPlan, "bedroom#2", "hall") >= 0.9 - 1e-6 && Invariants(counted, countedPlan) is null,
        "every instance of a required room touches its partner", Layout(countedPlan));

    var budgeted = Brief("budget", 10, 10, new[] { Room("a", 20, 15), Room("b", 20, 15) }, budget: 34);
    var budgetedPlan = Attempt(budgeted, 1);
    Check(budgetedPlan.IsFeasible && budgetedPlan.Rooms.Sum(r => r.AreaSquareMetres) <= 34 && budgetedPlan.Rooms.All(r => r.AreaSquareMetres >= 15) && Has(budgetedPlan, PlanCodes.AreaBudget, "plan", true, true),
        "areas shrink toward their minimums to meet the budget", Layout(budgetedPlan));

    // ---- infeasible briefs: typed results, never exceptions ----
    Infeasible("minimum areas larger than the footprint are infeasible", Attempt(Brief("crowded", 4, 4, new[] { Room("a", min: 10), Room("b", min: 10) })), PlanCodes.LevelAreaInsufficient, "level 0");
    Infeasible("no site and no footprint is infeasible", Attempt(Brief("nowhere", null, null, new[] { Room("a", 10) })), PlanCodes.FootprintMissing);
    Infeasible("an empty brief is refused as invalid", Attempt(Brief("empty", 5, 5, Array.Empty<BriefRoom>())), PlanCodes.BriefInvalid, "rooms");
    Infeasible("a contradictory brief is refused as invalid",
        Attempt(Brief("contradiction", 5, 5, new[] { Room("a", 5), Room("b", 5) }, new[] { Adj("a", "b", BriefAdjacencyKind.Required), Adj("b", "a", BriefAdjacencyKind.Forbidden) })),
        PlanCodes.BriefInvalid, "adjacencies[1]");
    Infeasible("a rule minimum area above the brief maximum conflicts",
        Attempt(example, 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(RoomId: "kitchen", MinAreaSquareMetres: 12) }))), PlanCodes.AreaRangeConflict, "kitchen");
    Infeasible("a rule minimum side whose square exceeds the maximum conflicts",
        Attempt(example, 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(RoomId: "kitchen", MinSideMetres: 4) }))), PlanCodes.AreaRangeConflict, "kitchen");
    Infeasible("a minimum side wider than the footprint is infeasible",
        Attempt(Brief("narrow", 5, 30, new[] { Room("big", 30), Room("small", 6) }), 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(RoomId: "big", MinSideMetres: 6) }))),
        PlanCodes.SideMin, "big");
    Infeasible("a door wider than any wall makes the required adjacency infeasible",
        Attempt(Brief("tiny", 3, 3, new[] { Room("a", 4), Room("b", 4) }, new[] { Adj("a", "b", BriefAdjacencyKind.Required) }), 1, new PlannerOptions(DoorWidthMetres: 4)),
        PlanCodes.AdjacencyRequired, "a/b");
    Infeasible("the planner tiles one rectangle per level and cannot separate its only two rooms, so forbidding their contact is reported infeasible",
        Attempt(Brief("pair", 10, 10, new[] { Room("a", 10), Room("b", 10) }, new[] { Adj("a", "b", BriefAdjacencyKind.Forbidden) })), PlanCodes.AdjacencyForbidden, "a/b");
    Infeasible("a required adjacency across levels is infeasible",
        Attempt(Brief("split", 6, 6, new[] { Room("a", 10, level: 0), Room("b", 10, level: 1) }, new[] { Adj("a", "b", BriefAdjacencyKind.Required) }, levels: 2)), PlanCodes.AdjacencyRequired, "a/b");
    Infeasible("rule minimums beyond the budget are infeasible",
        Attempt(Brief("tight", 10, 10, new[] { Room("a", min: 10), Room("b", min: 10) }, budget: 30), 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(NamePattern: "*", MinAreaSquareMetres: 16) }))),
        PlanCodes.BudgetInsufficient, "plan");
    var simple = Brief("simple", 6, 6, new[] { Room("a", 10), Room("b", 10) });
    Infeasible("a rule without selector is refused", Attempt(simple, 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(MinSideMetres: 2) }))), PlanCodes.RuleInvalid, "rules[0]");
    Infeasible("a rule without minimums is refused", Attempt(simple, 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(RoomId: "a") }))), PlanCodes.RuleInvalid, "rules[0]");
    Infeasible("a rule with a negative minimum is refused",
        Attempt(simple, 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(RoomId: "a", MinSideMetres: 1), new PlannerRule(RoomId: "b", MinAreaSquareMetres: -1) }))), PlanCodes.RuleInvalid, "rules[1]");
    Infeasible("a zero door width is refused", Attempt(simple, 1, new PlannerOptions(DoorWidthMetres: 0)), PlanCodes.OptionsInvalid, "door_width_m");
    var nanDoor = Attempt(simple, 1, new PlannerOptions(DoorWidthMetres: double.NaN));
    Infeasible("a NaN door width is refused", nanDoor, PlanCodes.OptionsInvalid, "door_width_m");
    var nanJson = SpacePlanSerializer.Serialize(nanDoor);
    using (var nanDocument = JsonDocument.Parse(nanJson))
        Check(nanDocument.RootElement.GetProperty("door_width_m").ValueKind == JsonValueKind.Null && nanDocument.RootElement.GetProperty("status").GetString() == "infeasible"
            && nanDocument.RootElement.GetProperty("rooms").GetArrayLength() == 0, "an infeasible plan serializes as valid JSON with non-finite numbers as null", nanJson);
    Infeasible("a footprint larger than the site is refused", Attempt(simple, 1, new PlannerOptions(Footprint: new PlanFootprint(7, 5))), PlanCodes.FootprintInvalid, "footprint");
    Infeasible("a non-positive footprint is refused", Attempt(noSite, 1, new PlannerOptions(Footprint: new PlanFootprint(0, 5))), PlanCodes.FootprintInvalid, "footprint");
    var crowded = Brief("crowded", 4, 4, new[] { Room("a", min: 10), Room("b", min: 10) });
    Check(SpacePlanSerializer.Serialize(Attempt(crowded, 9)) == SpacePlanSerializer.Serialize(Attempt(crowded, 9)), "infeasible results are deterministic too");
    Check(SpacePlanSerializer.Serialize(Attempt(crowded, 9)).StartsWith("{\"plan_version\":1,\"status\":\"infeasible\",", StringComparison.Ordinal), "infeasible results serialize with status infeasible");
    var nullBrief = false;
    try { SpacePlanner.Plan(null!, 1); }
    catch (ArgumentNullException) { nullBrief = true; }
    Check(nullBrief, "a null brief is a programming error (ArgumentNullException)");

    // ---- search exhaustion versus provable refusals ----
    bool Exhausted(SpacePlan plan) => plan.Violations.Any(c => c.Code == PlanCodes.SearchExhausted && c.Subject == "plan" && c.Hard && c.Detail.Length > 0);
    var exhaustedCases = new[]
    {
        Attempt(Brief("narrow", 5, 30, new[] { Room("big", 30), Room("small", 6) }), 1, new PlannerOptions(Rules: new PlannerRules(new[] { new PlannerRule(RoomId: "big", MinSideMetres: 6) }))),
        Attempt(Brief("tiny", 3, 3, new[] { Room("a", 4), Room("b", 4) }, new[] { Adj("a", "b", BriefAdjacencyKind.Required) }), 1, new PlannerOptions(DoorWidthMetres: 4)),
        Attempt(Brief("pair", 10, 10, new[] { Room("a", 10), Room("b", 10) }, new[] { Adj("a", "b", BriefAdjacencyKind.Forbidden) })),
    };
    Check(exhaustedCases.All(Exhausted) && exhaustedCases.All(plan => plan.HardViolations.Count >= 2), "a search that finds no layout reports search_exhausted with the violated constraints",
        string.Join(" || ", exhaustedCases.Select(Violations)));
    var refusedCases = new[] { Attempt(crowded, 1), Attempt(Brief("nowhere", null, null, new[] { Room("a", 10) })), Attempt(simple, 1, new PlannerOptions(DoorWidthMetres: 0)) };
    Check(refusedCases.All(plan => !plan.IsFeasible && !Exhausted(plan)), "provable input refusals never report search_exhausted", string.Join(" || ", refusedCases.Select(Violations)));

    // ---- size caps: typed plan_too_large, fast, never an exception ----
    void TooLarge(string name, ProjectBrief brief, string subject)
    {
        var timer = Stopwatch.StartNew();
        var plan = Attempt(brief, 1);
        timer.Stop();
        Infeasible(name, plan, PlanCodes.PlanTooLarge, subject);
        Check(timer.Elapsed.TotalSeconds < 2, name + " returns within 2 s", timer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
    }
    TooLarge("2 000 000 000 levels are refused as plan_too_large", Brief("tall", 6, 6, new[] { Room("a", 10) }, levels: 2000000000), "constraints.levels");
    TooLarge("preferred level int.MaxValue without a level count is refused as plan_too_large", Brief("high", 6, 6, new[] { Room("a", 10, level: int.MaxValue) }), "rooms[0].preferred_level");
    TooLarge("a count of 1 000 000 is refused as plan_too_large", Brief("many", 6, 6, new[] { Room("a", 1, count: 1000000) }), "rooms");
    TooLarge("more than SpacePlanner.MaxInstances instances are refused", Brief("over", 100, 100, new[] { Room("a", 1, count: 400), Room("b", 1, count: 101) }), "rooms");
    TooLarge("a site wider than SpacePlanner.MaxFootprintMetres is refused", Brief("vast", 1e12, 6, new[] { Room("a", 10) }), "footprint");
    Check(SpacePlanner.MaxLevels == 200 && SpacePlanner.MaxInstances == 500, "the documented caps are 200 levels and 500 instances");
    var capLevels = Brief("cap levels", 6, 6, new[] { Room("a", 10, level: 0), Room("b", 10, level: 199) }, levels: 200);
    var capPlan = Attempt(capLevels, 1);
    Check(capPlan.IsFeasible && Find(capPlan, "b")?.Level == 199 && Invariants(capLevels, capPlan) is null, "200 levels, the cap, still plan", Violations(capPlan));

    // ---- narrow and degenerate area ranges stay inside the range ----
    string? narrowProblem = null;
    foreach (var (low, high) in new[] { (10.0, 10.005), (10.0, 10.02), (7.5, 7.5001) })
    {
        var narrow = Brief("narrow range", 6, 6, new[] { Room("tight", min: low, max: high), Room("free", 8) });
        for (var seed = 1; seed <= 4; seed++)
        {
            var plan = Attempt(narrow, seed);
            var tight = Find(plan, "tight");
            var problem = plan.IsFeasible
                ? (tight is not null && tight.AreaSquareMetres >= low && tight.AreaSquareMetres <= high + 1e-9 ? Invariants(narrow, plan) : "tight area " + F(tight?.AreaSquareMetres ?? -1))
                : high - low >= 0.005 ? "infeasible: " + Violations(plan) : Exhausted(plan) ? null : "infeasible without search_exhausted: " + Violations(plan);
            if (problem is not null && narrowProblem is null) narrowProblem = F(low) + ".." + F(high) + " seed " + seed.ToString(CultureInfo.InvariantCulture) + ": " + problem;
        }
    }
    Check(narrowProblem is null, "ranges of 0.005 m2 and wider plan inside the range; narrower ones are inside the range or search_exhausted", narrowProblem ?? "");
    string? exactProblem = null;
    var exact = Brief("exact", 6, 6, new[] { Room("fixed", min: 10, max: 10), Room("free", 8) });
    for (var seed = 1; seed <= 4; seed++)
    {
        var plan = Attempt(exact, seed);
        var problem = plan.IsFeasible
            ? (Find(plan, "fixed") is { } fixedRoom && fixedRoom.AreaSquareMetres >= 10 && fixedRoom.AreaSquareMetres <= 10 + 1e-9 ? null : "fixed area outside 10..10")
            : Exhausted(plan) && plan.Violations.Any(c => (c.Code == PlanCodes.AreaMin || c.Code == PlanCodes.AreaMax) && c.Subject == "fixed") ? null : "unexpected: " + Violations(plan);
        if (problem is not null && exactProblem is null) exactProblem = "seed " + seed.ToString(CultureInfo.InvariantCulture) + ": " + problem;
    }
    Check(exactProblem is null, "min = max is never reported satisfied outside the range", exactProblem ?? "");

    // ---- level assignment ----
    var balanced = Brief("balanced levels", 6, 6, new[] { Room("a", min: 19), Room("b", min: 16), Room("c", min: 16), Room("d", min: 19) }, levels: 2);
    var balancedPlan = Attempt(balanced, 1);
    Check(balancedPlan.IsFeasible && Invariants(balanced, balancedPlan) is null && Find(balancedPlan, "a")?.Level != Find(balancedPlan, "d")?.Level,
        "largest-first assignment fits minimums {19,16,16,19} into two 6 x 6 levels", Violations(balancedPlan) + " " + Layout(balancedPlan));

    // ---- budget shrinking never refills a level ----
    var refill = Brief("refill", 4, 5, new[] { Room("a", min: 10, level: 0), Room("b", min: 9.995, level: 0), Room("c", 5, 5, level: 1) }, levels: 2, budget: 25.01);
    var refillPlan = Attempt(refill, 1);
    var levelZero = refillPlan.Rooms.Where(r => r.Level == 0).Sum(r => r.AreaSquareMetres);
    Check(refillPlan.IsFeasible && Invariants(refill, refillPlan) is null && levelZero <= 20 && refillPlan.Rooms.Sum(r => r.AreaSquareMetres) <= 25.01,
        "budget shrinking keeps a full level within its footprint", Violations(refillPlan) + " level 0 " + F(levelZero));

    // ---- an impossible preferred adjacency is only a soft miss ----
    var softBrief = Brief("soft", 6, 6, new[] { Room("a", 10, level: 0), Room("b", 10, level: 1) }, new[] { Adj("a", "b", BriefAdjacencyKind.Preferred) }, levels: 2);
    var softPlan = Attempt(softBrief, 1);
    Check(softPlan.IsFeasible && softPlan.HardViolations.Count == 0 && Has(softPlan, PlanCodes.AdjacencyPreferred, "a/b", false, false) && softPlan.Violations.Count == 1,
        "an impossible preferred adjacency still plans feasibly with one unsatisfied soft constraint", Violations(softPlan));

    // ---- tiny budgets: typed refusals, never exceptions ----
    Infeasible("a budget below the grid slack is refused as budget_insufficient",
        Attempt(Brief("tiny budget", 10, 10, new[] { Room("a", 10), Room("b", 8) }, budget: 0.001)), PlanCodes.BudgetInsufficient, "plan");
    var deepTimer = Stopwatch.StartNew();
    var deep = Attempt(Brief("deep tiny budget", 100, 100, new[] { Room("a", 2), Room("b", 1, level: 199) }, levels: 200, budget: 3));
    deepTimer.Stop();
    Infeasible("200 levels on a 100 x 100 footprint with a 3 m2 budget are refused as budget_insufficient", deep, PlanCodes.BudgetInsufficient, "plan");
    Check(deepTimer.Elapsed.TotalSeconds < 2, "the 200-level tiny-budget brief returns within 2 s", deepTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
    string? tinyProblem = null;
    var tinyCount = 0;
    foreach (var tinyBudget in new[] { 1e-9, 0.001, 0.01, 0.1, 1, 5, 20 })
        foreach (var (tinyWidth, tinyDepth) in new[] { (0.5, 0.5), (3.0, 3.0), (10.0, 10.0) })
            for (var roomCount = 1; roomCount <= 5; roomCount++)
            {
                var tinyRooms = Enumerable.Range(0, roomCount).Select(i => Room("t" + i.ToString(CultureInfo.InvariantCulture), 1 + i, i == 0 ? Math.Min(0.5, tinyBudget) : null)).ToArray();
                var tinyBrief = Brief("tiny", tinyWidth, tinyDepth, tinyRooms, budget: tinyBudget);
                var plan = Attempt(tinyBrief, roomCount);
                tinyCount++;
                var problem = plan.IsFeasible ? Invariants(tinyBrief, plan)
                    : plan.Rooms.Count == 0 && plan.Constraints.Count > 0 && plan.Constraints.All(c => !c.Satisfied && !c.Code.StartsWith("threw", StringComparison.Ordinal)) ? null : "malformed or thrown: " + Violations(plan);
                if (problem is not null && tinyProblem is null)
                    tinyProblem = "budget " + F(tinyBudget) + " site " + F(tinyWidth) + " rooms " + roomCount.ToString(CultureInfo.InvariantCulture) + ": " + problem;
            }
    Check(tinyProblem is null && tinyCount == 105, "Plan never throws over tiny budgets, 1-5 rooms and small footprints", tinyProblem ?? "");

    // ---- 30-room performance ----
    string Id(int i) => "r" + i.ToString("00", CultureInfo.InvariantCulture);
    var bigRooms = Enumerable.Range(0, 30).Select(i => { var target = 6.0 + (i * 7 % 15); return Room(Id(i), target, target * 0.8, target * 1.3, name: "Room " + Id(i)); }).ToArray();
    var bigAdjacencies = new List<BriefAdjacency>();
    for (var i = 0; i < 10; i++) bigAdjacencies.Add(Adj(Id(2 * i), Id(2 * i + 1), BriefAdjacencyKind.Required));
    foreach (var k in new[] { 21, 22, 23 }) bigAdjacencies.Add(Adj(Id(20), Id(k), BriefAdjacencyKind.Required));
    bigAdjacencies.Add(Adj(Id(0), Id(29), BriefAdjacencyKind.Forbidden));
    bigAdjacencies.Add(Adj(Id(5), Id(20), BriefAdjacencyKind.Forbidden));
    bigAdjacencies.Add(Adj(Id(1), Id(2), BriefAdjacencyKind.Forbidden));
    for (var i = 0; i < 27; i += 4) bigAdjacencies.Add(Adj(Id(i), Id(i + 3), BriefAdjacencyKind.Preferred));
    var big = Brief("thirty rooms", 20, 30, bigRooms, bigAdjacencies.ToArray());
    var bigOptions = new PlannerOptions(Rules: new PlannerRules(Enumerable.Range(0, 30).Where(i => i % 3 == 0).Select(i => new PlannerRule(RoomId: Id(i), MinSideMetres: 1.5)).ToArray()));
    Check(!BriefValidator.HasErrors(BriefValidator.Validate(big)), "30-room brief validates");
    var watch = Stopwatch.StartNew();
    var bigPlan = Attempt(big, 3, bigOptions);
    watch.Stop();
    Check(bigPlan.IsFeasible && bigPlan.Rooms.Count == 30, "30-room brief plans feasibly", Violations(bigPlan));
    Check(Invariants(big, bigPlan, bigOptions) is null, "30-room plan honours every invariant", Invariants(big, bigPlan, bigOptions) ?? "");
    Check(bigPlan.Rooms.Where((_, i) => i % 3 == 0).All(r => ShortSide(r) >= 1.5 - 1e-9), "30-room plan honours rule minimum sides");
    Check(watch.Elapsed.TotalSeconds < 10, "30-room brief plans in under 10 s", watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
    Check(SpacePlanSerializer.Serialize(Attempt(big, 3, bigOptions)) == SpacePlanSerializer.Serialize(bigPlan), "30-room plan is deterministic");

    // ---- P7-02 alternatives with scoring (AlternativeTests.cs) ----
    AlternativeTests.Run(Check, root);

    // ---- P7-03 apartment prototype generator (PrototypeTests.cs) ----
    PrototypeTests.Run(Check, root);

    // ---- P7-04 circulation model (CirculationTests.cs) ----
    CirculationTests.Run(Check, root);

    // ---- P7-05 facade openings for illumination (FacadeTests.cs) ----
    FacadeTests.Run(Check, root);

    // ---- P7-06 facade windows into the prototype BIM-IR (FacadeIrTests.cs) ----
    FacadeIrTests.Run(Check, root);
}
catch (Exception error)
{
    Console.WriteLine("::error::generative FAIL unexpected " + error.GetType().Name + ": " + error.Message);
    failed++;
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, generative_contract = failed == 0 }));
Environment.ExitCode = failed == 0 && passed > 0 ? 0 : 1;

static double SharedEdge(PlanRect a, PlanRect b)
{
    const double tolerance = 1e-6;
    if (Math.Abs(a.Right - b.X) < tolerance || Math.Abs(b.Right - a.X) < tolerance) return Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
    if (Math.Abs(a.Top - b.Y) < tolerance || Math.Abs(b.Top - a.Y) < tolerance) return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
    return 0;
}

// Independent wildcard matcher for rule name patterns: '*' any run, '?' one character, case-insensitive.
static bool Wildcard(string pattern, string text) =>
    Regex.IsMatch(text, @"\A" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + @"\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

static bool Overlaps(PlanRect a, PlanRect b) => Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 1e-6 && Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y) > 1e-6;
