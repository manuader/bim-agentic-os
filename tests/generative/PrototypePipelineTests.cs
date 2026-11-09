using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-07 prototype pipeline oracle: the opt-in entry point chains prototype -> circulation -> facade -> BIM-IR windows
// and ranks the alternatives that pass every gate. Crafted plans exercise each gate with typed reasons (a bedroom
// reachable only through another bedroom fails circulation_bedroom_access; an enclosed bedroom fails
// facade_no_exterior_edge; party walls only fail facade_lot_line_only; walls thicker than the rooms fail the BIM-IR
// compile; an infeasible plan and a duplicate stop at the prototype gate); refusals of the options and of the prototype
// request; the PrototypeGenerator regression digest is unchanged by a pipeline run and is the pipeline's prototype stage
// bit for bit; on real 1/2/3-bedroom prototypes an independent recomputation of every stage (AlternativeRanker,
// CirculationAnalyzer, FacadePlanner, FacadeIrBuilder, CirculationRanker) must agree with the accepted set, the gate of
// each rejection, the ranking and the BIM-IR; byte-identical canonical JSON across runs and cultures.
// Unit-tested evidence only.
static class PrototypePipelineTests
{
    const string PackId = "ar.caba.codigo-edificacion";
    // PrototypeTests.PinnedSha256: canonical JSON of the 2-bedroom prototype on the 8.66 x 20 m lot, 6 alternatives, seed 0.
    const string PinnedPrototypeSha256 = "eb6da9ef00b170918354c2823fa5d5490e4eb0ac564bd1944e0ece4fff2c8c70";
    // P7-10: SHA-256 of PrototypePipelineSerializer output before the facade criterion (main f671157).
    const string PinnedRunSha256 = "6221bab685fa0e14eecf5d3d287bf20ca3137d29dfca597bf02426c5e37ca3b1";
    const string PinnedCraftedSha256 = "2f0acdcb616483c7ae817bcfdb4785e42408e3e2da413dfd66afcf0a8cbc868c";
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-4000-8000-0000000007a7");

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "pipeline: " + name, detail);
        var caba = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", PackId, "skill.json")));
        var options = new PrototypePipelineOptions(caba, ProjectId);
        Check(options.Revision == 0 && options.CirculationWeight == CirculationScoring.DefaultCirculationWeight && options.Circulation is null && options.Facade is null
            && options.Ir is null && options.Parameters is null, "documented defaults: revision 0, default circulation weight, default stage options");

        // ---- regression pin: the prototype stage is PrototypeGenerator, unchanged ----
        var pinned = new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 6, Seed: 0);
        var before = PrototypeSerializer.Serialize(PrototypeGenerator.Generate(pinned));
        var run = PrototypePipeline.Run(pinned, options);
        var after = PrototypeSerializer.Serialize(PrototypeGenerator.Generate(pinned));
        Check(Sha(before) == PinnedPrototypeSha256 && Sha(after) == PinnedPrototypeSha256, "PrototypeGenerator digest is unchanged before and after a pipeline run",
            Sha(before) + " / " + Sha(after));
        Check(run.Prototype is not null && PrototypeSerializer.Serialize(run.Prototype) == before, "the pipeline's prototype stage is PrototypeGenerator.Generate of the request, bit for bit");
        Check(run.Status != PrototypePipelineStatus.Refused && run.Refusals.Count == 0 && run.Ranking is not null && run.Alternatives.Count == 6,
            "the pinned request runs through the pipeline: every generated alternative is reported", Describe(run));

        // ---- crafted plans: one per gate ----
        var brief = Brief();
        // Hall entrance on the north lot edge; living and bedroom windows on the south facade.
        var flat = Plan("flat", 1, 10, 6, P("living", "Living", 0, 0, 6, 4), P("bedroom_1", "Bedroom 1", 6, 0, 4, 4), P("bathroom", "Bathroom", 0, 4, 3, 2),
            P("hall", "Hall", 3, 4, 3, 2), P("kitchen", "Kitchen", 6, 4, 4, 2));
        // bedroom_2 touches only bedroom_1: reachable only through a bedroom.
        var through = Plan("through-bedroom", 2, 13, 6, P("living", "Living", 0, 0, 6, 6), P("bedroom_1", "Bedroom 1", 6, 0, 4, 6), P("bedroom_2", "Bedroom 2", 10, 0, 3, 6));
        // bedroom_1 enclosed by living (south), hall (west), kitchen (east) and bathroom (north): no exterior edge.
        var dark = Plan("dark", 3, 10, 7, P("living", "Living", 0, 0, 10, 2), P("hall", "Hall", 0, 2, 3, 3), P("bedroom_1", "Bedroom 1", 3, 2, 4, 3),
            P("kitchen", "Kitchen", 7, 2, 3, 3), P("bathroom", "Bathroom", 0, 5, 10, 2));
        var infeasible = new SpacePlan(SpacePlanStatus.Infeasible, "none", 4, 1, null, 0.9, Array.Empty<PlannedRoom>(),
            new[] { new PlanConstraint(PlanCodes.SearchExhausted, "plan", true, false, "no layout met every hard constraint") }, 0);
        var copy = flat with { Seed = 5 };
        var plans = new[] { through, flat, dark, infeasible, copy };
        var ranked = PrototypePipeline.Rank(brief, plans, options);
        Check(ranked.Status == PrototypePipelineStatus.Ranked && ranked.Prototype is null && ranked.Refusals.Count == 0 && ranked.Alternatives.Count == 5
            && ranked.Alternatives.Select(a => a.Index).SequenceEqual(new[] { 1, 0, 2, 3, 4 }) && ranked.Accepted.Count == 1 && ranked.Best is { Index: 1, Rank: 1 },
            "crafted plans: only the flat is accepted (rank 1), the rejected follow in generation order", Describe(ranked));
        var best = ranked.Alternatives.FirstOrDefault(a => a.Index == 1);
        Check(best is { Accepted: true, Rank: 1, RejectedAt: null, Strategy: null, Circulation.IsValid: true, Facade.IsComplete: true, Ir.IsComplete: true, Project: not null }
            && best.Criteria.Count == 7 && best.Criteria[6].Criterion == CirculationCodes.Criterion && best.Total > 0 && best.Total <= 1
            && Math.Abs(best.Criteria.Sum(c => c.Contribution) - best.Total) <= 1e-12 && best.Plan == flat,
            "the accepted flat carries its circulation model, complete facade, hosted windows and seven ranking criteria", Describe(best));
        var flatProject = best?.Project;
        Check(flatProject is not null && Compiler.Validate(flatProject).Length == 0 && flatProject.Id == ProjectId && flatProject.Revision == 0
            && flatProject.Elements.Count(e => e.Kind == "window") == 2 && best!.Ir!.Windows.Count == 2,
            "the accepted flat's BIM-IR validates clean with its two windows", flatProject is null ? "" : Kinds(flatProject));
        var expectedFlat = Safe(() => FacadeIrBuilder.AddWindows(FacadeIrBuilder.Prototype(ProjectId, 0, flat), FacadePlanner.Place(flat, caba), CirculationAnalyzer.Analyze(brief, flat)));
        Check(flatProject is not null && expectedFlat?.Project is not null && Json(flatProject) == Json(expectedFlat.Project),
            "the accepted BIM-IR equals FacadeIrBuilder.AddWindows(Prototype(plan), FacadePlanner.Place(plan), CirculationAnalyzer.Analyze(plan))");
        var expectedRanking = CirculationRanker.Rank(brief, plans, new CirculationScoring(null, CirculationScoring.DefaultCirculationWeight, true, null));
        var expectedFlatTotal = expectedRanking.Alternatives.First(a => a.Index == 1).Total;
        Check(best is not null && best.Total == expectedFlatTotal && ranked.Ranking is not null
            && AlternativeSetSerializer.Serialize(ranked.Ranking) == AlternativeSetSerializer.Serialize(expectedRanking),
            "the ranking is CirculationRanker with RequireValid, bit for bit", Num(best?.Total ?? double.NaN) + " vs " + Num(expectedFlatTotal));

        var circulationReject = ranked.Alternatives.FirstOrDefault(a => a.Index == 0);
        Check(circulationReject is { Accepted: false, Rank: 0, Total: 0, RejectedAt: PrototypePipelineGate.Circulation, Circulation.IsValid: false, Facade: null, Ir: null }
            && circulationReject.Criteria.Count == 0 && circulationReject.Rejections.All(r => r.Gate == PrototypePipelineGate.Circulation && r.Detail.Length > 0)
            && circulationReject.Rejections.Any(r => r.Code == CirculationCodes.BedroomAccess && r.Subject == "bedroom_2")
            && circulationReject.Rejections.All(r => r.Subject != "bedroom_1" && r.Subject != "living"),
            "a bedroom reachable only through another bedroom is rejected at the circulation gate: circulation_bedroom_access@bedroom_2", Describe(circulationReject));
        var facadeReject = ranked.Alternatives.FirstOrDefault(a => a.Index == 2);
        Check(facadeReject is { Accepted: false, Rank: 0, RejectedAt: PrototypePipelineGate.Facade, Circulation.IsValid: true, Facade.Status: FacadeStatus.Incomplete, Ir: null }
            && facadeReject.Rejections.Count == 1 && facadeReject.Rejections[0] is { Gate: PrototypePipelineGate.Facade, Code: FacadeCodes.NoExteriorEdge, Subject: "bedroom_1" }
            && facadeReject.Rejections[0].Detail.Length > 0,
            "an enclosed bedroom is rejected at the facade (illumination) gate: facade_no_exterior_edge@bedroom_1", Describe(facadeReject));
        var prototypeReject = ranked.Alternatives.FirstOrDefault(a => a.Index == 3);
        Check(prototypeReject is { Accepted: false, RejectedAt: PrototypePipelineGate.Prototype, Circulation: null, Facade: null, Ir: null }
            && prototypeReject.Rejections.Count == 1 && prototypeReject.Rejections[0] is { Code: PlanCodes.SearchExhausted, Subject: "plan" },
            "an infeasible plan is rejected at the prototype gate with its own violation", Describe(prototypeReject));
        var duplicate = ranked.Alternatives.FirstOrDefault(a => a.Index == 4);
        Check(duplicate is { Accepted: false, RejectedAt: PrototypePipelineGate.Prototype, Circulation: null }
            && duplicate.Rejections.Count == 1 && duplicate.Rejections[0] is { Code: AlternativeCodes.DuplicateGeometry, Subject: "plan" }
            && duplicate.Rejections[0].Detail.Contains("index 1", StringComparison.Ordinal),
            "a duplicate of the flat is rejected at the prototype gate naming its original", Describe(duplicate));

        // Party walls only: the south and west/east lot lines refused for openings, so no habitable room can get a window.
        var partyWalls = PrototypePipeline.Rank(brief, new[] { flat }, options with { Facade = new FacadeOptions(OpeningEdges: new[] { LotEdge.North }) });
        var party = partyWalls.Alternatives.FirstOrDefault();
        Check(partyWalls.Status == PrototypePipelineStatus.Infeasible && party is { RejectedAt: PrototypePipelineGate.Facade }
            && party.Rejections.Any(r => r.Code == FacadeCodes.LotLineOnly && r.Subject == "living") && party.Rejections.Any(r => r.Code == FacadeCodes.LotLineOnly && r.Subject == "bedroom_1"),
            "rooms on party walls only are rejected at the facade gate: facade_lot_line_only", Describe(partyWalls));
        // Walls thicker than the 2 m rooms: IntentCompiler cannot build the prototype BIM-IR.
        var thick = PrototypePipeline.Rank(brief, new[] { flat }, options with { Ir = new FacadeIrOptions(WallThicknessMetres: 2.5) });
        var thickAlternative = thick.Alternatives.FirstOrDefault();
        Check(thick.Status == PrototypePipelineStatus.Infeasible && thickAlternative is { RejectedAt: PrototypePipelineGate.FacadeIr, Facade.IsComplete: true, Ir: null }
            && thickAlternative.Rejections.All(r => r.Gate == PrototypePipelineGate.FacadeIr && r.Code == IntentCodes.RoomTooSmall && r.Detail.Length > 0)
            && thickAlternative.Rejections.Select(r => r.Subject).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(new[] { "bathroom", "hall", "kitchen" }),
            "a plan IntentCompiler refuses is rejected at the BIM-IR gate with the compiler's typed issues: room_too_small on the 2 m deep rooms", Describe(thick));

        // IntentCompiler failures that are not IntentException: its internal invariant intent_stretch_unbounded
        // (InvalidOperationException, IntentCompiler.cs) is unreachable for the plans that reach this gate (overlapping rooms
        // are refused as room_overlap and skip the wall pass, and every unshared stretch that does not start at its room's
        // corner follows a shared one), so the compile seam is tested directly.
        var stretch = Attempt(() => PrototypePipeline.CompilePrototype(() => throw new InvalidOperationException("intent_stretch_unbounded")));
        Check(stretch.Error is null && stretch.Value is { Project: null } unbounded && unbounded.Rejections.Count == 1
            && unbounded.Rejections[0] is { Gate: PrototypePipelineGate.FacadeIr, Code: PrototypePipelineCodes.IrCompileFailed, Subject: "plan" }
            && unbounded.Rejections[0].Detail.Contains("intent_stretch_unbounded", StringComparison.Ordinal),
            "an InvalidOperationException from IntentCompiler is a typed pipeline_ir_compile_failed rejection, not a throw", stretch.Error?.Message ?? "");
        var keyed = Attempt(() => PrototypePipeline.CompilePrototype(() => throw new ArgumentException(FacadeIrCodes.LayoutInvalid + ": instance a#1 has no unique intent key")));
        Check(keyed.Error is null && keyed.Value is { Project: null } keyedResult && keyedResult.Rejections.Count == 1
            && keyedResult.Rejections[0] is { Gate: PrototypePipelineGate.FacadeIr, Code: FacadeIrCodes.LayoutInvalid, Subject: "plan", Detail: "instance a#1 has no unique intent key" },
            "a facade_ir-prefixed ArgumentException is a typed rejection with its code", keyed.Error?.Message ?? "");
        var foreign = Attempt(() => PrototypePipeline.CompilePrototype(() => throw new ArgumentException("not a facade_ir code")));
        Check(foreign.Error is ArgumentException && foreign.Value is null, "any other ArgumentException is not swallowed", foreign.Error?.GetType().Name ?? "no throw");
        var issued = Attempt(() => PrototypePipeline.CompilePrototype(() => throw new IntentException(IntentCodes.WallClash, null, "x",
            new[] { new IntentIssue(IntentCodes.WallClash, "hall", "hall clashes with kitchen"), new IntentIssue(IntentCodes.RoomTooSmall, "wc", "small") })));
        Check(issued.Error is null && issued.Value is { Project: null } issuedResult
            && string.Join(";", issuedResult.Rejections.Select(r => r.Gate + ":" + r.Code + "@" + r.Subject)) == "FacadeIr:wall_clash@hall;FacadeIr:room_too_small@wc",
            "an IntentException gives one typed rejection per issue", issued.Error?.Message ?? "");
        var seamCompiled = Attempt(() => PrototypePipeline.CompilePrototype(() => FacadeIrBuilder.Prototype(ProjectId, 0, flat)));
        Check(seamCompiled.Error is null && seamCompiled.Value is { Project: not null } compiledResult && compiledResult.Rejections.Count == 0, "a compiled prototype passes the seam");
        var allRejected = PrototypePipeline.Rank(brief, new[] { through, dark }, options);
        Check(allRejected.Status == PrototypePipelineStatus.Infeasible && allRejected.Accepted.Count == 0 && allRejected.Best is null && allRejected.Alternatives.Count == 2,
            "every alternative rejected: infeasible, no best", Describe(allRejected));

        // ---- refusals ----
        void Refused(PrototypePipelineResult refused, string refusedCode, string refusedSubject, string refusedName) =>
            Check(refused.Status == PrototypePipelineStatus.Refused && refused.Alternatives.Count == 0 && refused.Ranking is null
                && refused.Refusals.Any(r => r.Code == refusedCode && r.Subject.StartsWith(refusedSubject, StringComparison.Ordinal) && r.Hard && !r.Satisfied && r.Detail.Length > 0),
                refusedName, Describe(refused));
        Refused(PrototypePipeline.Rank(brief, plans, options with { ProjectId = Guid.Empty }), PrototypePipelineCodes.ProjectInvalid, "project_id", "an empty project id is refused");
        Refused(PrototypePipeline.Rank(brief, plans, options with { Revision = -1 }), PrototypePipelineCodes.ProjectInvalid, "revision", "a negative revision is refused");
        Refused(PrototypePipeline.Rank(brief, plans, options with { CirculationWeight = -1 }), AlternativeCodes.WeightsInvalid, "weights.circulation", "a negative circulation weight is refused");
        Refused(PrototypePipeline.Rank(brief, plans, options with { CirculationWeight = double.NaN }), AlternativeCodes.WeightsInvalid, "weights.circulation", "a non-finite circulation weight is refused");
        Refused(PrototypePipeline.Rank(brief, plans, options with { Circulation = new CirculationOptions(EntranceEdges: Array.Empty<LotEdge>()) }), CirculationCodes.OptionsInvalid,
            "entrance_edges", "circulation options are refused");
        Refused(PrototypePipeline.Rank(brief, plans, options with { Facade = new FacadeOptions(SillHeightMetres: 2, HeadHeightMetres: 1) }), FacadeCodes.OptionsInvalid, "head_height_m",
            "facade options are refused");
        Refused(PrototypePipeline.Rank(brief, plans, options with { Ir = new FacadeIrOptions(WallThicknessMetres: 0) }), FacadeIrCodes.OptionsInvalid, "ir_options",
            "BIM-IR options are refused");
        Refused(PrototypePipeline.Rank(brief, plans, options, new ScoringWeights(-1)), AlternativeCodes.WeightsInvalid, "weights.area_fit", "invalid ranking weights are refused");
        // Option refusals and ranking refusals (weights, count) are listed together.
        foreach (var (listed, label) in new[] { (plans, "with plans"), (Array.Empty<SpacePlan>(), "with an empty plan list") })
        {
            var merged = PrototypePipeline.Rank(brief, listed, options with { ProjectId = Guid.Empty }, new ScoringWeights(-1));
            Check(merged.Status == PrototypePipelineStatus.Refused && merged.Ranking is null && merged.Alternatives.Count == 0
                && merged.Refusals.Any(r => r.Code == PrototypePipelineCodes.ProjectInvalid && r.Subject == "project_id")
                && merged.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid && r.Subject == "weights.area_fit")
                && merged.Refusals.Any(r => r.Code == AlternativeCodes.CountInvalid) == (listed.Length == 0),
                "option and ranking refusals are listed together " + label, Describe(merged));
        }
        var zero = PrototypePipeline.Rank(brief, plans, options with { CirculationWeight = 0, Revision = -1 }, new ScoringWeights(0, 0, 0, 0, 0, 0));
        Check(zero.Status == PrototypePipelineStatus.Refused && zero.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid && r.Subject == "weights")
            && zero.Refusals.Any(r => r.Code == PrototypePipelineCodes.ProjectInvalid && r.Subject == "revision"), "all seven weights zero is refused with the other problems", Describe(zero));
        var many = PrototypePipeline.Rank(brief, plans, options with { ProjectId = Guid.Empty, CirculationWeight = -1 });
        Check(many.Status == PrototypePipelineStatus.Refused && many.Refusals.Any(r => r.Code == PrototypePipelineCodes.ProjectInvalid)
            && many.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid), "every problem is listed", Describe(many));
        var badRequest = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 0), options);
        Refused(badRequest, PrototypeCodes.CountInvalid, "alternatives", "a refused prototype request refuses the pipeline with the prototype's reasons");
        Check(badRequest.Prototype is { Status: PrototypeStatus.Refused }, "the refused prototype result is reported");
        var both = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(1, 20)), options with { Revision = -2 });
        Check(both.Status == PrototypePipelineStatus.Refused && both.Refusals.Any(r => r.Code == PrototypeCodes.LotInvalid) && both.Refusals.Any(r => r.Code == PrototypePipelineCodes.ProjectInvalid),
            "prototype and pipeline refusals are listed together", Describe(both));
        Check(Throws(() => PrototypePipeline.Run(null!, options)) && Throws(() => PrototypePipeline.Run(pinned, null!)) && Throws(() => PrototypePipeline.Rank(null!, plans, options))
            && Throws(() => PrototypePipeline.Rank(brief, null!, options)) && Throws(() => PrototypePipeline.Rank(brief, plans, options with { Pack = null! }))
            && Throws(() => PrototypePipelineSerializer.Serialize(null!)), "null arguments throw ArgumentNullException");

        // ---- real prototypes: an independent recomputation of every stage agrees ----
        string? problem = null;
        int accepted = 0, total = 0;
        var gates = new Dictionary<PrototypePipelineGate, int>();
        var histogram = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (typology, lot) in new[] { (ApartmentTypology.OneBedroom, new PrototypeLot(8.66, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20)),
            (ApartmentTypology.ThreeBedroom, new PrototypeLot(8, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(12, 30)) })
        {
            var request = new PrototypeRequest(typology, lot, 4, Seed: 0);
            var label = typology + " " + Num(lot.WidthMetres) + "x" + Num(lot.DepthMetres) + ": ";
            var result = PrototypePipeline.Run(request, options);
            var generated = PrototypeGenerator.Generate(request);
            if (generated.Brief is null || generated.Alternatives is null) { problem ??= label + "not generated"; continue; }
            if (result.Prototype is null || PrototypeSerializer.Serialize(result.Prototype) != PrototypeSerializer.Serialize(generated)) problem ??= label + "prototype stage differs";
            var plansOf = generated.Alternatives.Alternatives.OrderBy(a => a.Index).Select(a => a.Plan).ToArray();
            var ranking = CirculationRanker.Rank(generated.Brief, plansOf, new CirculationScoring(request.Weights, options.CirculationWeight, true, null));
            var expectedAccepted = new List<int>();
            foreach (var alternative in generated.Alternatives.Alternatives.OrderBy(a => a.Index))
            {
                total++;
                var actual = result.Alternatives.FirstOrDefault(a => a.Index == alternative.Index);
                var where = label + "alternative " + Text(alternative.Index) + ": ";
                if (actual is null) { problem ??= where + "missing"; continue; }
                if (actual.Seed != alternative.Seed || actual.Strategy != generated.Candidates.Single(c => c.Index == alternative.Index).Strategy || SpacePlanSerializer.Serialize(actual.Plan) != SpacePlanSerializer.Serialize(alternative.Plan))
                    problem ??= where + "seed, strategy or plan differs";
                PrototypePipelineGate? gate = null;
                Project? project = null;
                // The expected reasons, computed independently from each stage: code@subject in the stage's own order.
                var expected = new List<string>();
                if (alternative.Status != AlternativeStatus.Ranked)
                {
                    gate = PrototypePipelineGate.Prototype;
                    expected.AddRange(!alternative.Plan.IsFeasible && alternative.Plan.Violations.Count > 0
                        ? alternative.Plan.Violations.Select(v => v.Code + "@" + v.Subject)
                        : alternative.Reasons.Select(r => r.Contains(": ", StringComparison.Ordinal) ? r[..r.IndexOf(": ", StringComparison.Ordinal)] : r));
                }
                else
                {
                    var model = CirculationAnalyzer.Analyze(generated.Brief, alternative.Plan);
                    if (!model.IsValid)
                    {
                        gate = PrototypePipelineGate.Circulation;
                        expected.AddRange(model.Checks.Where(c => c.Hard && !c.Satisfied).Select(c => c.Code + "@" + c.Subject));
                    }
                    else
                    {
                        var layout = FacadePlanner.Place(alternative.Plan, caba);
                        if (!layout.IsComplete)
                        {
                            gate = PrototypePipelineGate.Facade;
                            expected.AddRange(layout.Checks.Where(c => c.Hard && !c.Satisfied).Select(c => c.Code + "@" + c.Subject));
                        }
                        else
                        {
                            Project? compiled = null;
                            try { compiled = FacadeIrBuilder.Prototype(ProjectId, 0, alternative.Plan); }
                            catch (IntentException error)
                            {
                                gate = PrototypePipelineGate.FacadeIr;
                                expected.AddRange(error.Issues.Count > 0 ? error.Issues.Select(i => i.Code + "@" + i.Key) : new[] { error.Code + "@" + (error.Field ?? "plan") });
                            }
                            if (compiled is not null)
                            {
                                var ir = FacadeIrBuilder.AddWindows(compiled, layout, model);
                                if (ir is not { IsComplete: true, Project: not null })
                                {
                                    gate = PrototypePipelineGate.FacadeIr;
                                    expected.AddRange(ir.Checks.Where(c => c.Hard && !c.Satisfied).Select(c => c.Code + "@" + c.Subject));
                                }
                                else project = ir.Project;
                            }
                        }
                    }
                }
                if (actual.RejectedAt != gate) { problem ??= where + "gate " + (actual.RejectedAt?.ToString() ?? "accepted") + " expected " + (gate?.ToString() ?? "accepted") + " " + Describe(actual); continue; }
                if (gate is { } stopped)
                {
                    gates[stopped] = gates.TryGetValue(stopped, out var seen) ? seen + 1 : 1;
                    foreach (var rejection in actual.Rejections)
                    {
                        var key = stopped + ":" + rejection.Code;
                        histogram[key] = histogram.TryGetValue(key, out var times) ? times + 1 : 1;
                    }
                    if (actual.Rank != 0 || actual.Total != 0 || actual.Criteria.Count != 0 || actual.Ir is not null || actual.Rejections.Count == 0
                        || actual.Rejections.Any(r => r.Gate != stopped || r.Code.Length == 0 || r.Detail.Length == 0))
                        problem ??= where + "rejection not typed: " + Describe(actual);
                    var reasons = string.Join(";", actual.Rejections.Select(r => r.Code + "@" + r.Subject));
                    if (reasons != string.Join(";", expected)) problem ??= where + "reasons [" + reasons + "] expected [" + string.Join(";", expected) + "]";
                    continue;
                }
                accepted++;
                expectedAccepted.Add(alternative.Index);
                var expectedTotal = ranking.Alternatives.Single(a => a.Index == alternative.Index).Total;
                if (actual.Project is null || project is null || Json(actual.Project) != Json(project)) problem ??= where + "BIM-IR differs from the independent stages";
                else if (Compiler.Validate(actual.Project).Length > 0) problem ??= where + "Compiler.Validate not clean";
                if (actual.Total != expectedTotal || actual.Criteria.Count != 7) problem ??= where + "total " + Num(actual.Total) + " expected " + Num(expectedTotal);
            }
            var order = result.Alternatives.Where(a => a.Accepted).ToArray();
            for (var k = 0; k < order.Length; k++)
            {
                if (order[k].Rank != k + 1) problem ??= label + "ranks are not 1..k in order";
                if (k > 0 && (order[k].Total > order[k - 1].Total || (order[k].Total == order[k - 1].Total && order[k].Index < order[k - 1].Index))) problem ??= label + "not ranked by total, then index";
            }
            if (!order.Select(a => a.Index).OrderBy(i => i).SequenceEqual(expectedAccepted)) problem ??= label + "accepted set differs";
            var rest = result.Alternatives.Skip(order.Length).Select(a => a.Index).ToArray();
            if (rest.Any(index => expectedAccepted.Contains(index)) || !rest.SequenceEqual(rest.OrderBy(i => i))) problem ??= label + "rejected alternatives are not after the accepted ones in generation order";
            if (result.Status != (expectedAccepted.Count > 0 ? PrototypePipelineStatus.Ranked : PrototypePipelineStatus.Infeasible)) problem ??= label + "status " + result.Status;
        }
        int Gate(PrototypePipelineGate gate) => gates.TryGetValue(gate, out var value) ? value : 0;
        var counts = "accepted " + Text(accepted) + " of " + Text(total) + "; rejected at " + string.Join(", ", gates.OrderBy(g => g.Key).Select(g => g.Key + " " + Text(g.Value)))
            + "; codes " + string.Join(", ", histogram.OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => h.Key + " x" + Text(h.Value)));
        Console.WriteLine("::notice::generative pipeline on real prototypes: " + counts);
        Check(problem is null && total == 16, "real 1/2/3-bedroom prototypes: accepted set, rejection gates and typed reasons, ranking and BIM-IR agree with an independent recomputation",
            (problem ?? "") + " | " + counts);
        // Regression pin of the seed-0 prototypes (4 alternatives each on the four lots above).
        Check(accepted == 6 && Gate(PrototypePipelineGate.Prototype) == 6 && Gate(PrototypePipelineGate.Circulation) == 0 && Gate(PrototypePipelineGate.Facade) == 0
            && Gate(PrototypePipelineGate.FacadeIr) == 4, "real prototypes: 6 accepted, 6 rejected at the prototype gate, 4 at the BIM-IR gate (pinned)", counts);

        // ---- determinism: byte-identical canonical JSON ----
        var first = PrototypePipelineSerializer.Serialize(run);
        var second = PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(pinned, options));
        string german;
        PrototypePipelineResult germanRun;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            germanRun = PrototypePipeline.Run(pinned, options);
            german = PrototypePipelineSerializer.Serialize(germanRun);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(first.Length > 200 && first == second && first == german, "same input gives byte-identical pipeline JSON across runs and cultures", Text(first.Length));
        Check(run.Alternatives.Zip(germanRun.Alternatives).All(pair => (pair.First.Project is null) == (pair.Second.Project is null)
            && (pair.First.Project is null || Json(pair.First.Project) == Json(pair.Second.Project!))), "the BIM-IR projects are identical across runs and cultures");
        var crafted = PrototypePipelineSerializer.Serialize(ranked);
        // P7-10 byte pin: the P7-07 canonical JSON of the pinned run and of the crafted plans, recorded before the facade
        // criterion existed; an absent or zero facade weight must keep exactly these bytes.
        Check(Sha(first) == PinnedRunSha256, "P7-07 pipeline JSON of the pinned 2-bedroom run is byte-identical to the pre-P7-10 pin", Sha(first));
        Check(Sha(crafted) == PinnedCraftedSha256, "P7-07 pipeline JSON of the crafted plans is byte-identical to the pre-P7-10 pin", Sha(crafted));
        // The stage options are part of the canonical form: runs differing only in them serialize differently.
        var standard = PrototypePipelineSerializer.Serialize(PrototypePipeline.Rank(brief, new[] { flat }, options));
        var tallResult = PrototypePipeline.Rank(brief, new[] { flat }, options with { Ir = new FacadeIrOptions(WallHeightMetres: 3) });
        var tall = PrototypePipelineSerializer.Serialize(tallResult);
        var hallEntrance = PrototypePipelineSerializer.Serialize(PrototypePipeline.Rank(brief, new[] { flat }, options with { Circulation = new CirculationOptions(EntranceRoomId: "hall") }));
        Check(tallResult.Best is not null && tall != standard && hallEntrance != standard, "JSON differs when only the wall height or only the circulation options differ");
        using (var document = JsonDocument.Parse(tall))
        {
            var stage = document.RootElement.GetProperty("options");
            Check(stage.GetProperty("ir").GetProperty("wall_height_m").GetDouble() == 3 && stage.GetProperty("ir").GetProperty("wall_thickness_m").GetDouble() == 0.15
                && stage.GetProperty("ir").GetProperty("entrance_clearance_m").GetDouble() == 0.3 && stage.GetProperty("ir").GetProperty("provenance").ValueKind == JsonValueKind.Null
                && stage.GetProperty("facade").GetProperty("sill_height_m").GetDouble() == 0.9 && stage.GetProperty("facade").GetProperty("opening_edges").ValueKind == JsonValueKind.Null
                && stage.GetProperty("circulation").GetProperty("entrance_room").ValueKind == JsonValueKind.Null
                && stage.GetProperty("parameters").ValueKind == JsonValueKind.Object && !stage.GetProperty("parameters").EnumerateObject().Any(),
                "pipeline JSON writes the circulation, facade and BIM-IR options and the pack parameters", tall.Length > 400 ? tall[..400] : tall);
        }
        Check(crafted == PrototypePipelineSerializer.Serialize(PrototypePipeline.Rank(brief, plans, options)), "ranking crafted plans is deterministic");
        using (var document = JsonDocument.Parse(crafted))
        {
            var json = document.RootElement;
            var alternatives = json.GetProperty("alternatives");
            var first0 = alternatives[0];
            var rejected = alternatives[1];
            Check(json.GetProperty("pipeline_version").GetInt32() == PrototypePipelineResult.CurrentVersion && json.GetProperty("status").GetString() == "ranked"
                && json.GetProperty("pack").GetString() == PackId && json.GetProperty("project_id").GetString() == ProjectId.ToString("D")
                && json.GetProperty("revision").GetInt32() == 0 && json.GetProperty("prototype").ValueKind == JsonValueKind.Null
                && json.GetProperty("refusals").GetArrayLength() == 0 && alternatives.GetArrayLength() == 5,
                "pipeline JSON states version, status, pack, project identity, refusals and every alternative", crafted.Length > 600 ? crafted[..600] : crafted);
            Check(first0.GetProperty("index").GetInt32() == 1 && first0.GetProperty("accepted").GetBoolean() && first0.GetProperty("rank").GetInt32() == 1
                && first0.GetProperty("criteria").GetArrayLength() == 7 && first0.GetProperty("rejections").GetArrayLength() == 0
                && first0.GetProperty("circulation").GetProperty("circulation_version").GetInt32() == CirculationModel.CurrentVersion
                && first0.GetProperty("facade").ValueKind == JsonValueKind.Object && first0.GetProperty("windows").GetArrayLength() == 2
                && first0.GetProperty("windows")[0].GetProperty("element_id").GetString() is { Length: 36 } && first0.GetProperty("element_count").GetInt32() == flatProject?.Elements.Length,
                "an accepted alternative serializes its criteria, circulation, facade, hosted windows and element count");
            Check(rejected.GetProperty("index").GetInt32() == 0 && !rejected.GetProperty("accepted").GetBoolean() && rejected.GetProperty("rank").GetInt32() == 0
                && rejected.GetProperty("rejected_at").GetString() == "circulation" && rejected.GetProperty("rejections")[0].GetProperty("gate").GetString() == "circulation"
                && rejected.GetProperty("rejections").EnumerateArray().Any(r => r.GetProperty("code").GetString() == CirculationCodes.BedroomAccess)
                && rejected.GetProperty("facade").ValueKind == JsonValueKind.Null && rejected.GetProperty("element_count").ValueKind == JsonValueKind.Null,
                "a rejected alternative serializes its gate and typed reasons");
        }
        using (var document = JsonDocument.Parse(first))
            Check(document.RootElement.GetProperty("prototype").GetProperty("prototype_version").GetInt32() == PrototypeResult.CurrentVersion
                && document.RootElement.GetProperty("prototype").GetRawText() == before,
                "a run embeds the canonical prototype JSON unchanged");

        // ---- P7-10: facade quality as an opt-in ranking criterion ----
        Check(options.FacadeWeight == 0, "the facade weight is off (0) by default");
        var zeroFacade = options with { FacadeWeight = 0 };
        Check(Sha(PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(pinned, zeroFacade))) == PinnedRunSha256
            && Sha(PrototypePipelineSerializer.Serialize(PrototypePipeline.Rank(brief, plans, zeroFacade))) == PinnedCraftedSha256,
            "an explicit facade weight 0 keeps the P7-07 bytes of the pinned run and of the crafted plans");
        Refused(PrototypePipeline.Rank(brief, plans, options with { FacadeWeight = -1 }), AlternativeCodes.WeightsInvalid, "weights.facade", "a negative facade weight is refused");
        Refused(PrototypePipeline.Rank(brief, plans, options with { FacadeWeight = double.NaN }), AlternativeCodes.WeightsInvalid, "weights.facade", "a NaN facade weight is refused");
        Refused(PrototypePipeline.Run(pinned, options with { FacadeWeight = double.PositiveInfinity }), AlternativeCodes.WeightsInvalid, "weights.facade", "an infinite facade weight is refused");
        var overflow = PrototypePipeline.Rank(brief, plans, options with { CirculationWeight = double.MaxValue, FacadeWeight = double.MaxValue });
        Check(overflow.Status == PrototypePipelineStatus.Refused && overflow.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid && r.Subject == "weights"),
            "weights whose sum with the facade weight is not finite are refused", Describe(overflow));

        // Two accepted plans that tie on the only base criterion (proportions: every room within 2:1, circulation weight 0):
        // the flat's windows are sized to the CABA minimum (24 m2 living 2.000 m, 16 m2 bedroom 1.334 m), the compact plan's
        // 6.72 m2 bedroom gets the 0.60 m minimum window width, 0.72 m2 against the 0.672 m2 minimum (+7.1 %).
        var compact = Plan("compact", 6, 6.4, 4.5, P("living", "Living", 0, 0, 4, 2.8), P("bedroom_1", "Bedroom 1", 4, 0, 2.4, 2.8), P("bathroom", "Bathroom", 0, 2.8, 2, 1.7),
            P("hall", "Hall", 2, 2.8, 2, 1.7), P("kitchen", "Kitchen", 4, 2.8, 2.4, 1.7));
        var proportionsOnly = new ScoringWeights(0, 0, 1, 0, 0, 0);
        var pair = new[] { flat, compact };
        var plain = PrototypePipeline.Rank(brief, pair, options with { CirculationWeight = 0 }, proportionsOnly);
        var withFacade = PrototypePipeline.Rank(brief, pair, options with { CirculationWeight = 0, FacadeWeight = 1 }, proportionsOnly);
        Check(plain.Status == PrototypePipelineStatus.Ranked && plain.Alternatives.Select(a => a.Index).SequenceEqual(new[] { 0, 1 }) && plain.Accepted.Count == 2
            && plain.Alternatives.All(a => a.Total == 1 && a.Criteria.Count == 7),
            "without the facade weight the flat and the compact plan tie (total 1) and keep generation order", Describe(plain));
        Check(withFacade.Status == PrototypePipelineStatus.Ranked && withFacade.Alternatives.Select(a => a.Index).SequenceEqual(new[] { 1, 0 })
            && withFacade.Alternatives.Select(a => a.Rank).SequenceEqual(new[] { 1, 2 }) && withFacade.Accepted.Count == 2,
            "facade weight 1 re-ranks: the compact plan with more window margin over the CABA minimum comes first", Describe(withFacade));
        string? facadeProblem = null;
        foreach (var alternative in withFacade.Alternatives)
        {
            var without = plain.Alternatives.Single(a => a.Index == alternative.Index);
            var why = ExpectFacade(alternative, without, proportionsOnly, 0, 1);
            facadeProblem ??= why is null ? null : "#" + Text(alternative.Index) + ": " + why;
        }
        Check(facadeProblem is null, "the facade criterion and the re-weighted totals agree with an independent recomputation from the facade layout", facadeProblem ?? "");
        var compactFacade = withFacade.Alternatives.FirstOrDefault(a => a.Index == 1)?.Criteria.LastOrDefault();
        var flatFacade = withFacade.Alternatives.FirstOrDefault(a => a.Index == 0)?.Criteria.LastOrDefault();
        var bedroomMargin = 0.72 / 0.672 - 1;
        Check(compactFacade is { Criterion: PrototypePipelineCodes.FacadeCriterion } && flatFacade is { Criterion: PrototypePipelineCodes.FacadeCriterion }
            && compactFacade.Raw > bedroomMargin / 2 - 1e-9 && compactFacade.Raw < bedroomMargin / 2 + 0.001 && flatFacade.Raw >= 0 && flatFacade.Raw < 0.001
            && compactFacade.Normalized > flatFacade.Normalized && compactFacade.Weight == 0.5 && flatFacade.Weight == 0.5,
            "facade raw is the mean room margin: about 3.6 % for the compact plan (its bedroom +7.1 %), about 0 for the flat", (compactFacade?.Raw.ToString("R", CultureInfo.InvariantCulture) ?? "") + " / " + (flatFacade?.Raw.ToString("R", CultureInfo.InvariantCulture) ?? ""));
        Check(compactFacade is not null && compactFacade.Detail.Contains("bedroom_1", StringComparison.Ordinal) && compactFacade.Detail.Contains("living", StringComparison.Ordinal)
            && compactFacade.Detail.Contains("bedroom-illumination-ratio", StringComparison.Ordinal) && compactFacade.Detail.Contains("ventilation", StringComparison.Ordinal)
            && compactFacade.Detail.Contains("facade weight 1 given", StringComparison.Ordinal),
            "the facade criterion explains each room's margin with its pack rules and the weight", compactFacade?.Detail ?? "");
        Check(withFacade.Ranking is not null && plain.Ranking is not null
            && AlternativeSetSerializer.Serialize(withFacade.Ranking) == AlternativeSetSerializer.Serialize(PrototypePipeline.Rank(brief, pair, options with { CirculationWeight = 0 }, proportionsOnly).Ranking!)
            && withFacade.Alternatives.All(a => plain.Alternatives.Single(other => other.Index == a.Index) is var twin && Json(a.Project) == Json(twin.Project)
                && a.Facade is not null && twin.Facade is not null && FacadeSerializer.Serialize(a.Facade) == FacadeSerializer.Serialize(twin.Facade)),
            "the facade criterion changes only totals, criteria and ranks: the CirculationRanker set, layouts and BIM-IR are those of the plain run");
        // A dominant facade weight orders by facade score alone; rejected alternatives get no facade criterion.
        var dominant = PrototypePipeline.Rank(brief, new[] { flat, through, compact }, options with { FacadeWeight = 1e6 });
        Check(dominant.Alternatives.Select(a => a.Index).SequenceEqual(new[] { 2, 0, 1 }) && dominant.Alternatives[2] is { Accepted: false, Rank: 0, Total: 0, Criteria.Count: 0 }
            && dominant.Alternatives.Take(2).All(a => a.Criteria.Count == 8),
            "a dominant facade weight orders the accepted plans by facade margin; rejected ones stay unscored", Describe(dominant));
        var facadeJson = PrototypePipelineSerializer.Serialize(withFacade);
        using (var document = JsonDocument.Parse(facadeJson))
        {
            var json = document.RootElement;
            var top = json.GetProperty("alternatives")[0];
            Check(json.GetProperty("facade_weight").GetDouble() == 1 && top.GetProperty("index").GetInt32() == 1 && top.GetProperty("criteria").GetArrayLength() == 8
                && top.GetProperty("criteria")[7].GetProperty("criterion").GetString() == "facade" && top.GetProperty("criteria")[7].GetProperty("weight").GetDouble() == 0.5,
                "pipeline JSON writes facade_weight and the facade criterion when the weight is positive", facadeJson.Length > 400 ? facadeJson[..400] : facadeJson);
        }
        using (var document = JsonDocument.Parse(crafted))
            Check(!document.RootElement.TryGetProperty("facade_weight", out _), "pipeline JSON has no facade_weight member when the weight is 0");
        string germanFacade;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            germanFacade = PrototypePipelineSerializer.Serialize(PrototypePipeline.Rank(brief, pair, options with { CirculationWeight = 0, FacadeWeight = 1 }, proportionsOnly));
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(facadeJson == germanFacade && facadeJson != PrototypePipelineSerializer.Serialize(plain), "facade-ranked JSON is byte-identical across runs and cultures and differs from the plain run");

        // Real prototypes with facade weight 0.5: the same accepted set, the criterion and totals recomputed independently.
        string? realProblem = null;
        var reordered = 0;
        foreach (var (typology, lot) in new[] { (ApartmentTypology.OneBedroom, new PrototypeLot(8.66, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20)),
            (ApartmentTypology.ThreeBedroom, new PrototypeLot(8, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(12, 30)) })
        {
            var request = new PrototypeRequest(typology, lot, 4, Seed: 0);
            var label = typology + " " + Num(lot.WidthMetres) + "x" + Num(lot.DepthMetres) + ": ";
            var basic = PrototypePipeline.Run(request, options);
            var scored = PrototypePipeline.Run(request, options with { FacadeWeight = 0.5 });
            if (!scored.Accepted.Select(a => a.Index).OrderBy(i => i).SequenceEqual(basic.Accepted.Select(a => a.Index).OrderBy(i => i))) realProblem ??= label + "accepted set differs";
            if (!scored.Alternatives.Where(a => !a.Accepted).Select(a => a.Index + ":" + a.RejectedAt).SequenceEqual(basic.Alternatives.Where(a => !a.Accepted).Select(a => a.Index + ":" + a.RejectedAt)))
                realProblem ??= label + "rejections differ";
            var acceptedScored = scored.Accepted;
            for (var k = 0; k < acceptedScored.Count; k++)
            {
                var alternative = acceptedScored[k];
                var why = ExpectFacade(alternative, basic.Accepted.Single(a => a.Index == alternative.Index), request.Weights ?? new ScoringWeights(), options.CirculationWeight, 0.5);
                if (why is not null) realProblem ??= label + "#" + Text(alternative.Index) + ": " + why;
                if (alternative.Rank != k + 1) realProblem ??= label + "ranks are not 1..k";
                if (k > 0 && (alternative.Total > acceptedScored[k - 1].Total || (alternative.Total == acceptedScored[k - 1].Total && alternative.Index < acceptedScored[k - 1].Index)))
                    realProblem ??= label + "not ranked by total, then index";
            }
            if (!acceptedScored.Select(a => a.Index).SequenceEqual(basic.Accepted.Select(a => a.Index))) reordered++;
            if (PrototypePipelineSerializer.Serialize(scored) != PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(request, options with { FacadeWeight = 0.5 })))
                realProblem ??= label + "not deterministic";
        }
        Console.WriteLine("::notice::generative pipeline facade criterion on real prototypes: " + Text(reordered) + " of 4 requests re-ranked with facade weight 0.5");
        Check(realProblem is null, "real prototypes with facade weight 0.5: same accepted set and rejections, facade criterion and totals recomputed, ranked by total then index", realProblem ?? "");
    }

    // P7-10 oracle, written from the contract rather than the implementation: over the facade rooms that are Compliant
    // with a positive illumination or ventilation ratio, a room's margin is min(achieved / required) - 1 over its ratios
    // (achieved = window or ventilation area / room area), at least 0; raw = mean margin (0 without such rooms),
    // normalized = raw / (1 + raw). Every criterion's weight is its given weight over the sum of the eight given weights;
    // the base criteria keep their normalized values; total = in-order sum of the contributions, at most 1.
    static string? ExpectFacade(PrototypePipelineAlternative scored, PrototypePipelineAlternative plain, ScoringWeights weights, double circulationWeight, double facadeWeight)
    {
        if (scored.Facade is not { } layout) return "no facade layout";
        var margins = new List<double>();
        foreach (var room in layout.Rooms.Where(r => r.Status == FacadeRoomStatus.Compliant))
        {
            var factors = new List<double>();
            if (room.IlluminationRatio is { } light && light > 0) factors.Add(room.WindowAreaSquareMetres / room.AreaSquareMetres / light);
            if (room.VentilationRatio is { } air && air > 0) factors.Add(room.VentilationAreaSquareMetres / room.AreaSquareMetres / air);
            if (factors.Count > 0) margins.Add(Math.Max(0, factors.Min() - 1));
        }
        var raw = margins.Count == 0 ? 0 : margins.Average();
        var normalized = raw / (1 + raw);
        var given = new[] { weights.AreaFit, weights.Adjacency, weights.Proportions, weights.Compactness, weights.Perimeter, weights.Compliance, circulationWeight, facadeWeight };
        var sum = given.Sum();
        if (scored.Criteria.Count != 8 || plain.Criteria.Count != 7) return "criteria count " + Text(scored.Criteria.Count);
        var facade = scored.Criteria[7];
        if (facade.Criterion != PrototypePipelineCodes.FacadeCriterion || Math.Abs(facade.Raw - raw) > 1e-12 || Math.Abs(facade.Normalized - normalized) > 1e-12)
            return "facade raw " + Num(facade.Raw) + " expected " + Num(raw);
        double total = 0;
        for (var k = 0; k < 8; k++)
        {
            var value = k < 7 ? plain.Criteria[k].Normalized : normalized;
            if (k < 7 && (scored.Criteria[k].Criterion != plain.Criteria[k].Criterion || scored.Criteria[k].Normalized != value || scored.Criteria[k].Raw != plain.Criteria[k].Raw))
                return "base criterion " + plain.Criteria[k].Criterion + " changed";
            var weight = given[k] / sum;
            if (Math.Abs(scored.Criteria[k].Weight - weight) > 1e-12 || Math.Abs(scored.Criteria[k].Contribution - weight * value) > 1e-12)
                return "criterion " + scored.Criteria[k].Criterion + " weight " + Num(scored.Criteria[k].Weight) + " expected " + Num(weight);
            total += weight * value;
        }
        if (Math.Abs(scored.Total - Math.Min(1, total)) > 1e-12) return "total " + Num(scored.Total) + " expected " + Num(total);
        if (Math.Abs(scored.Criteria.Sum(c => c.Contribution) - scored.Total) > 1e-12) return "contributions do not add up to the total";
        return facade.Detail.Length == 0 ? "facade detail empty" : null;
    }

    static ProjectBrief Brief()
    {
        var rooms = new[] { "living", "bedroom_1", "bedroom_2", "bathroom", "hall", "kitchen" }
            .Select(id => new BriefRoom(id, id, null, null, null, null, null)).ToArray();
        return new ProjectBrief(1, "pipeline", new BriefConstraints(null, null, null, null), rooms, Array.Empty<BriefAdjacency>());
    }

    static PlannedRoom P(string id, string name, double x, double y, double width, double depth) => new(id, id, name, 1, 0, new PlanRect(x, y, width, depth));

    static SpacePlan Plan(string name, int seed, double width, double depth, params PlannedRoom[] rooms) =>
        new(SpacePlanStatus.Feasible, name, seed, 1, new PlanFootprint(width, depth), 0.9, rooms, Array.Empty<PlanConstraint>(), 0);

    static T? Safe<T>(Func<T> make) where T : class
    {
        try { return make(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException) { return null; }
    }

    static (T? Value, Exception? Error) Attempt<T>(Func<T> make) where T : class
    {
        try { return (make(), null); }
        catch (Exception error) { return (null, error); }
    }

    static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArgumentNullException) { return true; }
    }

    static string Describe(PrototypePipelineResult? result) => result is null ? "null"
        : result.Status + " refusals [" + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject)) + "] " + string.Join(" | ", result.Alternatives.Select(Describe));

    static string Describe(PrototypePipelineAlternative? alternative) => alternative is null ? "null"
        : "#" + Text(alternative.Index) + " rank " + Text(alternative.Rank) + " total " + Num(alternative.Total) + " "
          + (alternative.Accepted ? "accepted" : "rejected at " + alternative.RejectedAt + ": " + string.Join("; ", alternative.Rejections.Select(r => r.Code + "@" + r.Subject + " " + r.Detail)));

    static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    static string Json<T>(T value) => JsonSerializer.Serialize(value);
    static string Kinds(Project project) => string.Join(",", project.Elements.GroupBy(e => e.Kind).Select(g => g.Key + "=" + Text(g.Count())));
    static string Num(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
