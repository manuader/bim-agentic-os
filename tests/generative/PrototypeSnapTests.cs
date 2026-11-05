using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Generative;

// P7-08 stagger snap oracle: crafted staggered plans (a 1/16 m jog between neighbouring rooms) that IntentCompiler refuses
// as wall_clash are snapped onto one line (the line with the longer edges, ties to the lower coordinate, a lot edge
// first) and then compile clean; clean plans are returned unchanged; typed refusals (a room thinner than the wall
// collapsing, a snap below a minimum side, invalid options); independence from room order, levels and culture. Pipeline:
// without the option the P7-07 canonical JSON is byte-identical (digests pinned from the P7-07 code, CI 42022995164); with
// it, the 16 real 1/2/3-bedroom prototypes have no wall_clash rejection, every snapped plan is checked independently
// (no stagger left, every move thinner than the wall, CABA minimums, in lot, no overlap) and every accepted BIM-IR
// validates clean. Unit-tested evidence only.
static class PrototypeSnapTests
{
    const string PackId = "ar.caba.codigo-edificacion";
    const double Thickness = FacadeIrOptions.DefaultWallThicknessMetres;
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-4000-8000-0000000007a8");
    // Canonical PrototypePipeline JSON of P7-07 (before P7-08) with the default options and project 00000000-0000-4000-8000-0000000007a7.
    const string PinnedRunSha256 = "6221bab685fa0e14eecf5d3d287bf20ca3137d29dfca597bf02426c5e37ca3b1";
    static readonly (ApartmentTypology Typology, PrototypeLot Lot, string Sha)[] RealLots =
    {
        (ApartmentTypology.OneBedroom, new PrototypeLot(8.66, 20), "6793fcbd724f5257e4439ae419d6c480040b14ec625c7d8f552f75fe6531ce49"),
        (ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), "03cbbac8a81711d9abac425c40b104fd641183fbfbe0e7aa6c870e3e0c215ab9"),
        (ApartmentTypology.ThreeBedroom, new PrototypeLot(8, 20), "e3ef916340bba1e084dd7b46decfcac5b7e7661d88484c78caac6b84b54a7480"),
        (ApartmentTypology.TwoBedroom, new PrototypeLot(12, 30), "18622ecb93b71a84a67fecf0a9bb394917e8e108da03353f34b7afe9d0f29ff0"),
    };

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "snap: " + name, detail);
        var brief = Brief("a", "b", "c", "d");

        // ---- crafted staggers ----
        // b and d sit 1/16 m east of a and c: equal edge lengths on both lines, so the lower line (x = 4) wins.
        var tie = Plan(10, 6, P("a", 0, 0, 4, 3), P("b", 0, 3, 4.0625, 3), P("c", 4, 0, 6, 3), P("d", 4.0625, 3, 5.9375, 3));
        Check(Clash(tie), "the crafted 1/16 m jog is refused by IntentCompiler as wall_clash without the snap");
        var snappedTie = PrototypeSnap.Snap(brief, tie);
        Check(snappedTie.Status == PrototypeSnapStatus.Snapped && Rects(snappedTie.Plan) == "a(0,0,4,3) b(0,3,4,3) c(4,0,6,3) d(4,3,6,3)" && snappedTie.Refusals.Count == 0,
            "equal edge lengths: the jog snaps onto the lower line", Describe(snappedTie));
        Check(snappedTie.Moves.Count == 2 && snappedTie.Moves.Any(m => m is { Instance: "b", Edge: "east", FromMetres: 4.0625, ToMetres: 4.0, Level: 0 })
            && snappedTie.Moves.Any(m => m is { Instance: "d", Edge: "west", FromMetres: 4.0625, ToMetres: 4.0 }), "the moves name the room, edge, level and both lines", Describe(snappedTie));
        Check(snappedTie.Plan.Constraints.Count(c => c.Code == PrototypeSnapCodes.Moved) == 2 && snappedTie.Plan.Constraints.Where(c => c.Code == PrototypeSnapCodes.Moved).All(c => !c.Hard && c.Satisfied && c.Detail.Contains("->", StringComparison.Ordinal))
            && snappedTie.Plan.IsFeasible && snappedTie.Plan.Seed == tie.Seed && snappedTie.Plan.Footprint == tie.Footprint && snappedTie.Plan.Score == tie.Score
            && snappedTie.Plan.Rooms.Select(r => r.InstanceId).SequenceEqual(tie.Rooms.Select(r => r.InstanceId)),
            "a snapped plan keeps the plan's fields and room order and records one soft prototype_snap_moved constraint per moved room");
        Check(Compiles(snappedTie.Plan), "the snapped plan compiles through FacadeIrBuilder.Prototype and validates clean");
        Check(Stagger(snappedTie.Plan) is null, "no stagger is left (independent finder)");

        // The east line of b and d is twice as long: it wins.
        var longer = Plan(10, 6, P("a", 0, 0, 4, 2), P("b", 0, 2, 4.0625, 4), P("c", 4, 0, 6, 2), P("d", 4.0625, 2, 5.9375, 4));
        var snappedLonger = PrototypeSnap.Snap(brief, longer);
        Check(snappedLonger.Status == PrototypeSnapStatus.Snapped && Rects(snappedLonger.Plan) == "a(0,0,4.0625,2) b(0,2,4.0625,4) c(4.0625,0,5.9375,2) d(4.0625,2,5.9375,4)" && Compiles(snappedLonger.Plan),
            "the line carrying the longest total edge length wins", Describe(snappedLonger));

        // b's west edge is 1/16 m off the lot edge x = 0 and longer than a's: the lot edge still wins.
        var edge = Plan(10, 8, P("a", 0, 0, 3, 3), P("b", 0.0625, 3, 2.9375, 5));
        Check(Clash(edge), "a jog next to the lot edge is refused as wall_clash without the snap");
        var snappedEdge = PrototypeSnap.Snap(brief, edge);
        Check(snappedEdge.Status == PrototypeSnapStatus.Snapped && Rects(snappedEdge.Plan) == "a(0,0,3,3) b(0,3,3,5)" && Compiles(snappedEdge.Plan),
            "a lot edge in the group is the target line, keeping the plan inside the lot", Describe(snappedEdge));

        // A horizontal jog: b's south edge 1/16 m above a's north edge (a thin gap), joined onto one line.
        var gap = Plan(6, 10, P("a", 0, 0, 6, 4), P("b", 0, 4.0625, 6, 5.9375));
        var snappedGap = PrototypeSnap.Snap(brief, gap);
        Check(Clash(gap) && snappedGap.Status == PrototypeSnapStatus.Snapped && Rects(snappedGap.Plan) == "a(0,0,6,4) b(0,4,6,6)" && Compiles(snappedGap.Plan),
            "a gap thinner than the wall between two rooms closes onto one line (lot edge 10 keeps b's north edge)", Describe(snappedGap));

        // ---- unchanged ----
        var clean = Plan(10, 6, P("a", 0, 0, 4, 3), P("b", 0, 3, 4, 3), P("c", 4, 0, 6, 3), P("d", 4, 3, 6, 3));
        var unchanged = PrototypeSnap.Snap(brief, clean);
        Check(unchanged.Status == PrototypeSnapStatus.Unchanged && ReferenceEquals(unchanged.Plan, clean) && unchanged.Moves.Count == 0 && unchanged.Refusals.Count == 0,
            "a plan without staggers is returned unchanged (the same object)");
        var wide = Plan(10, 6, P("a", 0, 0, 4, 3), P("b", 0, 3, 4.25, 3), P("c", 4, 0, 6, 3), P("d", 4.25, 3, 5.75, 3));
        Check(PrototypeSnap.Snap(brief, wide).Status == PrototypeSnapStatus.Unchanged && !Clash(wide), "a jog of at least the wall thickness is left alone (it compiles)");
        var levels = Plan(10, 6, P("a", 0, 0, 4, 3), P("b", 0, 0, 4.0625, 3, level: 1)) with { Levels = 2 };
        Check(PrototypeSnap.Snap(brief, levels).Status == PrototypeSnapStatus.Unchanged, "edges on different levels are never joined");
        var infeasible = new SpacePlan(SpacePlanStatus.Infeasible, "none", 4, 1, null, 0.9, Array.Empty<PlannedRoom>(),
            new[] { new PlanConstraint(PlanCodes.SearchExhausted, "plan", true, false, "no layout") }, 0);
        Check(PrototypeSnap.Snap(brief, infeasible) is { Status: PrototypeSnapStatus.Unchanged } kept && ReferenceEquals(kept.Plan, infeasible), "an infeasible plan is returned unchanged");

        // ---- refusals ----
        // t is 1/8 m wide: its two edges join through a and c, so it would collapse.
        var thin = Plan(10, 3, P("a", 0, 0, 4, 3), P("t", 4, 0, 0.125, 3), P("c", 4.125, 0, 5.875, 3));
        var collapsed = PrototypeSnap.Snap(Brief("a", "t", "c"), thin);
        Check(collapsed.Status == PrototypeSnapStatus.Refused && ReferenceEquals(collapsed.Plan, thin) && collapsed.Moves.Count == 0
            && collapsed.Refusals.Count == 1 && collapsed.Refusals[0] is { Code: PrototypeSnapCodes.Collapsed, Subject: "t", Hard: true, Satisfied: false } && collapsed.Refusals[0].Detail.Length > 0,
            "a room thinner than the wall is refused prototype_snap_collapsed, the plan kept as given", Describe(collapsed));
        var strict = new PrototypeSnapOptions(new PlannerRules(new[] { new PlannerRule(RoomId: "b", MinAreaSquareMetres: 12.1875, Reference: "test") }));
        var below = PrototypeSnap.Snap(brief, tie, null, strict);
        Check(below.Status == PrototypeSnapStatus.Refused && ReferenceEquals(below.Plan, tie)
            && below.Refusals.Any(r => r.Code == PrototypeSnapCodes.Noncompliant && r.Subject == "b" && r.Detail.Contains("below minimum 12.1875 m2", StringComparison.Ordinal) && r.Detail.Contains("test", StringComparison.Ordinal)),
            "a snap taking a room below its minimum area is refused prototype_snap_noncompliant", Describe(below));
        var budget = new ProjectBrief(1, "snap", new BriefConstraints(null, null, null, new BriefValue<double>(36)), Brief("a", "b").Rooms, Array.Empty<BriefAdjacency>());
        var growing = Plan(10, 8, P("a", 0, 0, 6, 3), P("b", 0, 3.0625, 6, 3));
        Check(PrototypeSnap.Snap(budget, growing) is { Status: PrototypeSnapStatus.Refused } over && over.Refusals.Any(r => r.Code == PrototypeSnapCodes.Noncompliant && r.Subject == "plan"),
            "a snap growing the plan above the area budget is refused prototype_snap_noncompliant@plan");
        var forbidden = new ProjectBrief(1, "snap", new BriefConstraints(null, null, null, null), Brief("a", "b").Rooms, new[] { new BriefAdjacency("a", "b", BriefAdjacencyKind.Forbidden) });
        Check(PrototypeSnap.Snap(forbidden, growing) is { Status: PrototypeSnapStatus.Refused } touching && touching.Refusals.Any(r => r.Code == PrototypeSnapCodes.Noncompliant && r.Subject == "a/b"),
            "a snap making a forbidden pair touch is refused prototype_snap_noncompliant");
        Check(PrototypeSnap.Snap(brief, tie, new FacadeIrOptions(WallThicknessMetres: 0)) is { Status: PrototypeSnapStatus.Refused } zero
            && zero.Refusals.Any(r => r.Code == PrototypeSnapCodes.OptionsInvalid && r.Subject == "wall_thickness_m"), "a zero wall thickness is refused prototype_snap_options_invalid");
        var negative = new PrototypeSnapOptions(new PlannerRules(new[] { new PlannerRule(NamePattern: "*", MinSideMetres: -1) }));
        Check(PrototypeSnap.OptionRefusals(negative).Any(r => r.Code == PrototypeSnapCodes.OptionsInvalid && r.Subject == "snap.rules[0]")
            && PrototypeSnap.Snap(brief, tie, null, negative).Status == PrototypeSnapStatus.Refused && PrototypeSnap.OptionRefusals(new PrototypeSnapOptions()).Count == 0,
            "a negative rule minimum is refused prototype_snap_options_invalid");
        Check(Throws(() => PrototypeSnap.Snap(null!, tie)) && Throws(() => PrototypeSnap.Snap(brief, null!)) && Throws(() => PrototypeSnap.OptionRefusals(null!)), "null arguments throw ArgumentNullException");

        // ---- determinism ----
        var reversed = PrototypeSnap.Snap(brief, tie with { Rooms = Enumerable.Reverse(tie.Rooms).ToArray() });
        Check(reversed.Status == PrototypeSnapStatus.Snapped && Sorted(reversed.Plan) == Sorted(snappedTie.Plan) && Sorted(PrototypeSnap.Snap(brief, longer with { Rooms = Enumerable.Reverse(longer.Rooms).ToArray() }).Plan) == Sorted(snappedLonger.Plan),
            "the snapped rectangles do not depend on room order");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = PrototypeSnap.Snap(brief, tie);
            Check(SpacePlanSerializer.Serialize(german.Plan) == SpacePlanSerializer.Serialize(snappedTie.Plan) && SpacePlanSerializer.Serialize(PrototypeSnap.Snap(brief, tie).Plan) == SpacePlanSerializer.Serialize(snappedTie.Plan),
                "the snapped plan is byte-identical across runs and cultures");
        }
        finally { CultureInfo.CurrentCulture = culture; }

        // ---- pipeline: default output unchanged ----
        var caba = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", PackId, "skill.json")));
        var p707 = new PrototypePipelineOptions(caba, Guid.Parse("00000000-0000-4000-8000-0000000007a7"));
        var pinned = new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 6, Seed: 0);
        var defaultJson = PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(pinned, p707));
        Check(p707.Snap is null && Sha(defaultJson) == PinnedRunSha256 && Sha(PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(pinned, p707 with { Snap = null }))) == PinnedRunSha256
            && !defaultJson.Contains("\"snap\"", StringComparison.Ordinal), "without the option the pipeline JSON is the P7-07 bytes (pinned digest, no snap member)", Sha(defaultJson));
        foreach (var (typology, lot, sha) in RealLots)
        {
            var actual = Sha(PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(new PrototypeRequest(typology, lot, 4, Seed: 0), p707)));
            Check(actual == sha, "without the option the real prototype pipeline JSON is the P7-07 bytes: " + typology + " " + Num(lot.WidthMetres) + "x" + Num(lot.DepthMetres), actual);
        }

        // ---- pipeline: with the pre-pass on the 16 real prototypes ----
        var options = new PrototypePipelineOptions(caba, ProjectId, Snap: new PrototypeSnapOptions());
        string? problem = null;
        int accepted = 0, total = 0, snappedCount = 0, moves = 0;
        double largest = 0;
        var gates = new Dictionary<PrototypePipelineGate, int>();
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (typology, lot, _) in RealLots)
        {
            var request = new PrototypeRequest(typology, lot, 4, Seed: 0);
            var label = typology + " " + Num(lot.WidthMetres) + "x" + Num(lot.DepthMetres) + ": ";
            var result = PrototypePipeline.Run(request, options);
            var generated = PrototypeGenerator.Generate(request);
            if (generated.Brief is null || generated.Alternatives is null || result.Prototype is null) { problem ??= label + "not generated"; continue; }
            if (PrototypeSerializer.Serialize(result.Prototype) != PrototypeSerializer.Serialize(generated)) problem ??= label + "the prototype stage changed";
            var rules = PrototypeTemplates.Rules(lot).Rules;
            foreach (var alternative in generated.Alternatives.Alternatives.OrderBy(a => a.Index))
            {
                total++;
                var where = label + "alternative " + Text(alternative.Index) + ": ";
                var actual = result.Alternatives.FirstOrDefault(a => a.Index == alternative.Index);
                if (actual is null) { problem ??= where + "missing"; continue; }
                var original = alternative.Plan;
                var expected = PrototypeSnap.Snap(generated.Brief, original);
                if (expected.Status == PrototypeSnapStatus.Refused) problem ??= where + "snap refused: " + string.Join("; ", expected.Refusals.Select(r => r.Code + "@" + r.Subject + " " + r.Detail));
                if (original.IsFeasible && SpacePlanSerializer.Serialize(actual.Plan) != SpacePlanSerializer.Serialize(expected.Plan)) problem ??= where + "the pipeline plan is not PrototypeSnap.Snap of the generated plan";
                if (expected.Status == PrototypeSnapStatus.Snapped)
                {
                    snappedCount++;
                    moves += expected.Moves.Count;
                    var plan = expected.Plan;
                    if (Stagger(plan) is { } left) problem ??= where + "stagger left: " + left;
                    foreach (var move in expected.Moves)
                    {
                        largest = Math.Max(largest, Math.Abs(move.ToMetres - move.FromMetres));
                        if (!(Math.Abs(move.ToMetres - move.FromMetres) < Thickness)) problem ??= where + "a move of at least the wall thickness: " + move;
                    }
                    if (OracleProblem(generated.Brief, original, plan, lot, rules) is { } oracle) problem ??= where + oracle;
                }
                else if (original.IsFeasible && Stagger(original) is { } missed) problem ??= where + "a stagger was not snapped: " + missed;
                if (actual.Rejections.Any(r => r.Code == IntentCodes.WallClash)) problem ??= where + "wall_clash rejection " + Describe(actual);
                if (actual.RejectedAt is { } gate)
                {
                    gates[gate] = gates.TryGetValue(gate, out var seen) ? seen + 1 : 1;
                    foreach (var rejection in actual.Rejections) codes[gate + ":" + rejection.Code] = codes.TryGetValue(gate + ":" + rejection.Code, out var times) ? times + 1 : 1;
                    continue;
                }
                accepted++;
                if (actual.Project is null || Compiler.Validate(actual.Project).Length > 0) problem ??= where + "accepted BIM-IR does not validate clean";
                else
                {
                    var independent = FacadeIrBuilder.AddWindows(FacadeIrBuilder.Prototype(ProjectId, 0, expected.Plan), FacadePlanner.Place(expected.Plan, caba), CirculationAnalyzer.Analyze(generated.Brief, expected.Plan));
                    if (independent.Project is null || JsonSerializer.Serialize(independent.Project) != JsonSerializer.Serialize(actual.Project)) problem ??= where + "accepted BIM-IR differs from the stages run on the snapped plan";
                }
            }
            var json = PrototypePipelineSerializer.Serialize(result);
            if (json != PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(request, options))) problem ??= label + "not deterministic";
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.GetProperty("options").GetProperty("snap").GetProperty("rules").ValueKind != JsonValueKind.Null) problem ??= label + "options.snap not written";
        }
        var counts = "accepted " + Text(accepted) + " of " + Text(total) + "; snapped " + Text(snappedCount) + " plans, " + Text(moves) + " edge moves, largest " + Num(largest) + " m; rejected at "
            + string.Join(", ", gates.OrderBy(g => g.Key).Select(g => g.Key + " " + Text(g.Value))) + "; codes " + string.Join(", ", codes.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + " x" + Text(c.Value)));
        Console.WriteLine("::notice::generative pipeline with stagger snap on real prototypes: " + counts);
        Check(problem is null && total == 16, "real prototypes with the snap: each plan is PrototypeSnap.Snap of the generated one, no stagger left, moves thinner than the wall, CABA minimums kept, accepted BIM-IR clean",
            (problem ?? "") + " | " + counts);
        Check(total == 16 && !codes.Keys.Any(k => k.EndsWith(":" + IntentCodes.WallClash, StringComparison.Ordinal)) && snappedCount >= 4 && accepted >= 6 + 4,
            "real prototypes with the snap: 0 wall_clash rejections, the 4 staggered alternatives accepted", counts);

        // ---- pipeline: crafted plans, refusals and JSON ----
        var ranked = PrototypePipeline.Rank(brief, new[] { tie }, options);
        var first = ranked.Alternatives.FirstOrDefault();
        Check(ranked.Status != PrototypePipelineStatus.Refused && first is not null && first.Plan.IsFeasible && Rects(first.Plan) == Rects(snappedTie.Plan) && first.Rejections.All(r => r.Code != IntentCodes.WallClash),
            "Rank runs caller-supplied plans through the snap too", Describe(ranked));
        var refusedSnap = PrototypePipeline.Rank(brief, new[] { tie }, options with { Snap = strict });
        var refusedAlternative = refusedSnap.Alternatives.FirstOrDefault();
        Check(refusedSnap.Status == PrototypePipelineStatus.Infeasible && refusedAlternative is { RejectedAt: PrototypePipelineGate.Prototype, Circulation: null, Rank: 0 }
            && refusedAlternative.Rejections.All(r => r.Gate == PrototypePipelineGate.Prototype) && refusedAlternative.Rejections.Any(r => r.Code == PrototypeSnapCodes.Noncompliant && r.Subject == "b"),
            "a refused snap rejects its alternative at the prototype gate with the typed prototype_snap reasons", Describe(refusedSnap));
        var refusedOptions = PrototypePipeline.Rank(brief, new[] { tie }, options with { Snap = negative });
        Check(refusedOptions.Status == PrototypePipelineStatus.Refused && refusedOptions.Refusals.Any(r => r.Code == PrototypeSnapCodes.OptionsInvalid && r.Subject == "snap.rules[0]"),
            "invalid snap options refuse the pipeline", Describe(refusedOptions));
        using (var document = JsonDocument.Parse(PrototypePipelineSerializer.Serialize(refusedSnap)))
        {
            var snap = document.RootElement.GetProperty("options").GetProperty("snap").GetProperty("rules");
            Check(snap.GetArrayLength() == 1 && snap[0].GetProperty("room_id").GetString() == "b" && snap[0].GetProperty("min_area_m2").GetDouble() == 12.1875
                && snap[0].GetProperty("name_pattern").ValueKind == JsonValueKind.Null && snap[0].GetProperty("min_side_m").ValueKind == JsonValueKind.Null,
                "pipeline JSON writes the snap rules when given");
        }
    }

    // Independent checks of a snapped real prototype: every room inside the lot, no overlap, the CABA minimum area and side
    // (brief minimum raised by the matching rules, matched with an independent wildcard) and the brief maximum, the
    // budget, the required adjacencies sharing at least the door width.
    static string? OracleProblem(ProjectBrief brief, SpacePlan original, SpacePlan plan, PrototypeLot lot, IReadOnlyList<PlannerRule> rules)
    {
        if (plan.Rooms.Count != original.Rooms.Count) return "room count changed";
        foreach (var room in plan.Rooms)
        {
            var r = room.Rect;
            if (r.X < 0 || r.Y < 0 || r.Right > lot.WidthMetres + 1e-9 || r.Top > lot.DepthMetres + 1e-9) return room.InstanceId + " outside the lot";
            var spec = brief.Rooms.Single(b => b.Id == room.RoomId);
            var minArea = spec.MinAreaSquareMetres?.Value ?? 0;
            double minSide = 0;
            foreach (var rule in rules.Where(rule => (rule.RoomId is null || rule.RoomId == room.RoomId) && (rule.NamePattern is null || Wildcard(rule.NamePattern, spec.Name))))
            {
                minArea = Math.Max(minArea, rule.MinAreaSquareMetres ?? 0);
                minSide = Math.Max(minSide, rule.MinSideMetres ?? 0);
            }
            minArea = Math.Max(minArea, minSide * minSide);
            if (r.Area < minArea || Math.Min(r.Width, r.Depth) < minSide) return room.InstanceId + " below its minimum " + Num(minArea) + " m2 / " + Num(minSide) + " m: " + Num(r.Width) + " x " + Num(r.Depth);
            if (spec.MaxAreaSquareMetres is { } max && r.Area > max.Value + 1e-6) return room.InstanceId + " above its maximum";
        }
        for (var i = 0; i < plan.Rooms.Count; i++)
            for (var j = i + 1; j < plan.Rooms.Count; j++)
            {
                var (a, b) = (plan.Rooms[i].Rect, plan.Rooms[j].Rect);
                if (Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 1e-9 && Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y) > 1e-9) return plan.Rooms[i].InstanceId + "/" + plan.Rooms[j].InstanceId + " overlap";
            }
        if (brief.Constraints.TotalAreaBudgetSquareMetres is { } budget && plan.Rooms.Sum(r => r.Rect.Area) > budget.Value + 1e-6) return "above the area budget";
        foreach (var adjacency in brief.Adjacencies.Where(a => a.Kind == BriefAdjacencyKind.Required))
        {
            var a = plan.Rooms.Single(r => r.RoomId == adjacency.A).Rect;
            var b = plan.Rooms.Single(r => r.RoomId == adjacency.B).Rect;
            double shared = 0;
            if (Math.Abs(a.Right - b.X) < 1e-9 || Math.Abs(b.Right - a.X) < 1e-9) shared = Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
            else if (Math.Abs(a.Top - b.Y) < 1e-9 || Math.Abs(b.Top - a.Y) < 1e-9) shared = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
            if (shared < plan.DoorWidthMetres) return adjacency.A + "/" + adjacency.B + " shares " + Num(shared) + " m";
        }
        return null;
    }

    // Independent stagger finder (as FacadeIrTests): two parallel room edges of one level on lines closer than the wall
    // thickness but not equal, whose spans meet.
    static string? Stagger(SpacePlan plan)
    {
        var edges = plan.Rooms.SelectMany(r => new (string Id, int Level, bool Horizontal, double Line, double Low, double High)[]
        {
            (r.InstanceId, r.Level, true, r.Rect.Y, r.Rect.X, r.Rect.Right), (r.InstanceId, r.Level, true, r.Rect.Top, r.Rect.X, r.Rect.Right),
            (r.InstanceId, r.Level, false, r.Rect.X, r.Rect.Y, r.Rect.Top), (r.InstanceId, r.Level, false, r.Rect.Right, r.Rect.Y, r.Rect.Top),
        }).ToArray();
        for (var i = 0; i < edges.Length; i++)
            for (var j = i + 1; j < edges.Length; j++)
            {
                var (a, b) = (edges[i], edges[j]);
                if (a.Level != b.Level || a.Horizontal != b.Horizontal || a.Id == b.Id) continue;
                var gap = Math.Abs(a.Line - b.Line);
                if (gap > 1e-7 && gap < Thickness && Math.Min(a.High, b.High) - Math.Max(a.Low, b.Low) >= -1e-7) return a.Id + "/" + b.Id + " edges " + Num(gap) + " m apart";
            }
        return null;
    }

    static bool Clash(SpacePlan plan) => IntentCompiler.Validate(FacadeIrBuilder.Intent(plan)).Any(issue => issue.Code == IntentCodes.WallClash);

    static bool Compiles(SpacePlan plan)
    {
        try { return Compiler.Validate(FacadeIrBuilder.Prototype(ProjectId, 0, plan)).Length == 0; }
        catch (IntentException) { return false; }
    }

    static ProjectBrief Brief(params string[] ids) =>
        new(1, "snap", new BriefConstraints(null, null, null, null), ids.Select(id => new BriefRoom(id, id, null, null, null, null, null)).ToArray(), Array.Empty<BriefAdjacency>());

    static PlannedRoom P(string id, double x, double y, double width, double depth, int level = 0) => new(id, id, id, 1, level, new PlanRect(x, y, width, depth));

    static SpacePlan Plan(double width, double depth, params PlannedRoom[] rooms) =>
        new(SpacePlanStatus.Feasible, "snap", 7, 1, new PlanFootprint(width, depth), 0.9, rooms, Array.Empty<PlanConstraint>(), 0.5);

    static string Rects(SpacePlan plan) => string.Join(" ", plan.Rooms.Select(r => r.InstanceId + "(" + Num(r.Rect.X) + "," + Num(r.Rect.Y) + "," + Num(r.Rect.Width) + "," + Num(r.Rect.Depth) + ")"));
    static string Sorted(SpacePlan plan) => string.Join(" ", Rects(plan).Split(' ').OrderBy(s => s, StringComparer.Ordinal));

    static string Describe(PrototypeSnapResult result) => result.Status + " " + Rects(result.Plan) + " | " + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject + " " + r.Detail));

    static string Describe(PrototypePipelineResult result) => result.Status + " [" + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject)) + "] " + string.Join(" | ", result.Alternatives.Select(Describe));

    static string Describe(PrototypePipelineAlternative alternative) => "#" + Text(alternative.Index) + " " + (alternative.Accepted ? "accepted"
        : "rejected at " + alternative.RejectedAt + ": " + string.Join("; ", alternative.Rejections.Select(r => r.Code + "@" + r.Subject + " " + r.Detail)));

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentNullException) { return true; }
    }

    static bool Wildcard(string pattern, string text) =>
        Regex.IsMatch(text, @"\A" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + @"\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
