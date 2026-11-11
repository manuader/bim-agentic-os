using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P5-12 typology prototypes through the pipeline gates oracle: the opt-in TypologyPipeline.Run turns the checked-in
// bim.typology.vivienda-unifamiliar pack, a family size and a lot into TypologyBrief candidates and runs them through the
// P7-07 prototype -> circulation -> facade -> BIM-IR window gates of the CABA code pack. The typology stage must be
// TypologyBrief.Generate bit for bit (its P5-11 pinned digest unchanged), the pipeline stage PrototypePipeline.Rank of its
// plans bit for bit (with the whole_lot strategy), and an independent recomputation of every gate (AlternativeRanker,
// CirculationAnalyzer, FacadePlanner, FacadeIrBuilder) must agree with the gate of each rejection and the accepted set;
// rejections are explained, the ranking is the PrototypePipeline order, the same pack + size + lot + seed give a
// byte-identical pinned digest across runs, re-parses, resolutions and cultures, typology and option refusals are listed
// together, and the existing PrototypePipeline and TypologyBrief digests are unchanged. Unit-tested evidence only.
static class TypologyPipelineTests
{
    const string Pack = "bim.typology.vivienda-unifamiliar";
    const string Caba = "ar.caba.codigo-edificacion";
    // SHA-256 of TypologyPipelineSerializer output for the pack, family 4, the 12 x 12 m lot, 6 alternatives, seed 0, the
    // CABA pack and ProjectId with default stage options (regression pin).
    const string PinnedSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
    // TypologyBriefTests.PinnedSha256 (P5-11): the same request through TypologyBrief alone.
    const string TypologyBriefPinnedSha256 = "85102ea05864ea67a8dad79bed06b2ce4a643056a09d8aee7d8044127340b513";
    // PrototypePipelineTests.PinnedRunSha256 (P7-07): the 2-bedroom apartment pipeline run on the 8.66 x 20 m lot.
    const string PrototypePipelinePinnedSha256 = "6221bab685fa0e14eecf5d3d287bf20ca3137d29dfca597bf02426c5e37ca3b1";
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-4000-8000-0000000007a7");

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "typology-pipeline: " + name, detail);

        var catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills"));
        var pack = catalog.Find(Pack);
        var caba = catalog.Find(Caba);
        Check(pack is { Kind: SkillKind.Typology } && caba is not null, "the vivienda unifamiliar typology pack and the CABA code pack load from skills/");
        if (pack is null || caba is null) return;
        var options = new PrototypePipelineOptions(caba, ProjectId);
        var lot = new PrototypeLot(12, 12);
        var request = new TypologyBriefRequest(4, lot, 6);

        // ---- the typology stage is TypologyBrief.Generate, unchanged ----
        var briefBefore = BriefDigest(TypologyBrief.Generate(pack, request));
        string? error = null;
        var run = Safe(() => TypologyPipeline.Run(pack, request, options), ref error);
        var briefAfter = BriefDigest(TypologyBrief.Generate(pack, request));
        Check(run is not null, "TypologyPipeline.Run runs the family-4 request", error ?? "");
        if (run is null) return;
        Check(briefBefore == TypologyBriefPinnedSha256 && briefAfter == TypologyBriefPinnedSha256, "the TypologyBrief P5-11 pinned digest is unchanged before and after a typology pipeline run",
            briefBefore + " / " + briefAfter);
        Check(BriefDigest(run.Typology) == TypologyBriefPinnedSha256 && run.Typology.Status == PrototypeStatus.Ranked,
            "the pipeline's typology stage is TypologyBrief.Generate of the request, bit for bit", BriefDigest(run.Typology));
        Check(run.Status != PrototypePipelineStatus.Refused && run.Refusals.Count == 0 && run.Alternatives.Count == 6 && run.Pipeline.Prototype is null && run.Pipeline.Ranking is not null
            && ReferenceEquals(run.Pipeline.Options, options), "every typology candidate goes through the gates", Describe(run.Pipeline));
        Check(run.Alternatives.All(a => a.Strategy == PrototypeStrategies.WholeLot) && run.Alternatives.Select(a => a.Index).OrderBy(i => i).SequenceEqual(Enumerable.Range(0, 6))
            && run.Alternatives.All(a => a.Seed == a.Index), "the alternatives carry the typology candidates' whole_lot strategy and seeds 0..5", Describe(run.Pipeline));

        // ---- the pipeline stage is PrototypePipeline.Rank of the typology plans, bit for bit ----
        var brief = run.Typology.Brief!;
        var plans = run.Typology.Alternatives!.Alternatives.OrderBy(a => a.Index).Select(a => a.Plan).ToArray();
        var expected = WithStrategy(PrototypePipeline.Rank(brief, plans, options, request.Weights));
        Check(PrototypePipelineSerializer.Serialize(run.Pipeline) == PrototypePipelineSerializer.Serialize(expected),
            "the gates and ranking are PrototypePipeline.Rank of the typology brief and plans (whole_lot strategy), bit for bit");
        Check(run.Pipeline.Ranking is not null && expected.Ranking is not null
            && AlternativeSetSerializer.Serialize(run.Pipeline.Ranking) == AlternativeSetSerializer.Serialize(CirculationRanker.Rank(brief, plans,
                new CirculationScoring(null, CirculationScoring.DefaultCirculationWeight, true, null))),
            "the ranking the gates start from is CirculationRanker with RequireValid over the typology plans");

        // ---- independent recomputation of every gate ----
        var origins = AlternativeRanker.Rank(brief, plans, new ScoringWeights(1, 1, 1, 1, 1, 1)).Alternatives.ToDictionary(a => a.Index);
        foreach (var alternative in run.Alternatives)
        {
            var label = "candidate " + Text(alternative.Index);
            var plan = plans[alternative.Index];
            var gate = ExpectedGate(brief, plan, origins[alternative.Index], caba, out var failures, out var project);
            Check(alternative.Plan == plan && alternative.RejectedAt == gate, label + " stops at the gate an independent recomputation gives (" + (gate?.ToString() ?? "accepted") + ")",
                Describe(alternative) + " | expected " + (gate?.ToString() ?? "accepted") + " " + string.Join("; ", failures));
            if (alternative.Accepted)
                Check(alternative.Rank >= 1 && alternative.Criteria.Count == 7 && alternative.Total > 0 && alternative.Total <= 1 && alternative.Project is not null
                    && Compiler.Validate(alternative.Project).Length == 0 && project is not null && Json(alternative.Project) == Json(project)
                    && alternative.Project.Elements.Count(e => e.Kind == "window") == alternative.Ir!.Windows.Count && alternative.Ir.Windows.Count > 0,
                    label + " is accepted with seven criteria and a clean BIM-IR with its hosted windows equal to FacadeIrBuilder's", Describe(alternative));
            else
                Check(alternative.Rank == 0 && alternative.Total == 0 && alternative.Criteria.Count == 0 && alternative.Ir is null && alternative.Rejections.Count > 0
                    && alternative.Rejections.All(r => r.Gate == alternative.RejectedAt && r.Code.Length > 0 && r.Detail.Length > 0)
                    && (gate is PrototypePipelineGate.Prototype or PrototypePipelineGate.FacadeIr || alternative.Rejections.All(r => failures.Contains(r.Code + "@" + r.Subject))),
                    label + " rejection is explained by the failed checks of its gate", Describe(alternative) + " | " + string.Join("; ", failures));
        }
        var accepted = run.Alternatives.Where(a => a.Accepted).ToArray();
        var rejected = run.Alternatives.Where(a => !a.Accepted).ToArray();
        Check(run.Alternatives.SequenceEqual(accepted.Concat(rejected)) && accepted.Select(a => a.Rank).SequenceEqual(Enumerable.Range(1, accepted.Length))
            && accepted.Zip(accepted.Skip(1)).All(pair => pair.First.Total > pair.Second.Total || (pair.First.Total == pair.Second.Total && pair.First.Index < pair.Second.Index))
            && rejected.Select(a => a.Index).SequenceEqual(rejected.Select(a => a.Index).OrderBy(i => i))
            && run.Status == (accepted.Length > 0 ? PrototypePipelineStatus.Ranked : PrototypePipelineStatus.Infeasible) && run.Best == accepted.FirstOrDefault(),
            "accepted alternatives are ranked 1..k by total (ties by generation index), the rejected follow in generation order", Describe(run.Pipeline));

        // ---- opt-in stage options pass through unchanged ----
        var facadeOptions = options with { FacadeWeight = 1 };
        var facadeRun = Safe(() => TypologyPipeline.Run(pack, request, facadeOptions), ref error);
        Check(facadeRun is not null && PrototypePipelineSerializer.Serialize(facadeRun.Pipeline) == PrototypePipelineSerializer.Serialize(WithStrategy(PrototypePipeline.Rank(brief, plans, facadeOptions, request.Weights))),
            "the P7-10 facade weight option gives the PrototypePipeline.Rank result with the same options", error ?? "");
        var weighted = new TypologyBriefRequest(4, lot, 6, Weights: new ScoringWeights(1, 0, 0, 0, 0, 0));
        var weightedRun = Safe(() => TypologyPipeline.Run(pack, weighted, options), ref error);
        Check(weightedRun is not null && weightedRun.Typology.Request.Weights == weighted.Weights
            && PrototypePipelineSerializer.Serialize(weightedRun.Pipeline) == PrototypePipelineSerializer.Serialize(WithStrategy(PrototypePipeline.Rank(brief, plans, options, weighted.Weights))),
            "the request's ranking weights are the pipeline ranking weights", error ?? "");

        // ---- determinism and the pinned digest ----
        var canonical = TypologyPipelineSerializer.Serialize(run);
        var again = Safe(() => TypologyPipeline.Run(pack, request, options), ref error);
        var reparsed = Safe(() => TypologyPipeline.Run(SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", Pack, "skill.json"))), request,
            options with { Pack = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", Caba, "skill.json"))) }), ref error);
        var resolved = Safe(() => TypologyPipeline.Run(SkillResolver.Resolve(catalog, new[] { new SkillRequest(Pack) }), request, options), ref error);
        string? german = null;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var local = Safe(() => TypologyPipeline.Run(pack, request, options), ref error);
            german = local is null ? null : TypologyPipelineSerializer.Serialize(local);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(again is not null && reparsed is not null && resolved is not null && TypologyPipelineSerializer.Serialize(again) == canonical
            && TypologyPipelineSerializer.Serialize(reparsed) == canonical && TypologyPipelineSerializer.Serialize(resolved) == canonical && german == canonical,
            "same pack + family size + lot + seed give byte-identical canonical JSON across runs, re-parses, skill resolutions and cultures", error ?? "");
        Check(Sha(canonical) == PinnedSha256, "family-4 12 x 12 m seed-0 typology pipeline JSON matches its pinned SHA-256", "actual " + Sha(canonical) + " | " + Summary(run) + " | sweep " + Sweep(pack, options));
        var shifted = Safe(() => TypologyPipeline.Run(pack, request with { Seed = 6 }, options), ref error);
        Check(shifted is not null && shifted.Alternatives.Select(a => a.Seed).OrderBy(s => s).SequenceEqual(Enumerable.Range(6, 6))
            && Sha(TypologyPipelineSerializer.Serialize(shifted)) != Sha(canonical), "another seed gives other candidates and another digest", error ?? "");
        using (var document = JsonDocument.Parse(canonical))
        {
            var top = document.RootElement;
            Check(canonical.StartsWith("{\"typology_pipeline_version\":1,\"status\":", StringComparison.Ordinal) && !canonical.Contains('\n', StringComparison.Ordinal)
                && top.GetProperty("pack").GetString() == Pack && top.GetProperty("request").GetProperty("family_size").GetInt32() == 4
                && top.GetProperty("request").GetProperty("seed").GetInt32() == 0 && top.GetProperty("typology").GetProperty("candidates").GetArrayLength() == 6
                && top.GetProperty("pipeline").GetRawText() == PrototypePipelineSerializer.Serialize(run.Pipeline)
                && top.GetProperty("typology").GetProperty("alternatives").GetRawText() == AlternativeSetSerializer.Serialize(run.Typology.Alternatives!)
                && top.GetProperty("typology").GetProperty("brief").GetRawText() == BriefSerializer.Serialize(brief),
                "canonical JSON is compact with fixed member order and embeds the canonical brief, alternative set and pipeline JSON");
        }

        // ---- refusals: typology and options listed together ----
        var badFamily = Safe(() => TypologyPipeline.Run(pack, new TypologyBriefRequest(0, lot, 6), options), ref error);
        Check(badFamily is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, Typology.Status: PrototypeStatus.Refused, Typology.Brief: null }
            && badFamily.Refusals.Any(r => r.Code == SkillTypologyCodes.FamilySizeInvalid && r.Hard && !r.Satisfied),
            "a family size outside the pack bounds is a typed refusal with no alternatives", Show(badFamily, error));
        var both = Safe(() => TypologyPipeline.Run(pack, new TypologyBriefRequest(0, lot, 6), options with { ProjectId = Guid.Empty, CirculationWeight = -1 }), ref error);
        var firstOption = both?.Refusals.Select((r, i) => (r, i)).FirstOrDefault(p => p.r.Code == PrototypePipelineCodes.ProjectInvalid).i ?? -1;
        Check(both is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 } && firstOption > 0 && both.Refusals[0].Code == SkillTypologyCodes.FamilySizeInvalid
            && both.Refusals.Any(r => r.Code == AlternativeCodes.WeightsInvalid && r.Subject == "weights.circulation"),
            "typology refusals come first, then every option refusal", Show(both, error));
        var badOptions = Safe(() => TypologyPipeline.Run(pack, request, options with { ProjectId = Guid.Empty }), ref error);
        Check(badOptions is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, Pipeline.Ranking: null } && BriefDigest(badOptions.Typology) == TypologyBriefPinnedSha256
            && badOptions.Refusals.Count == 1 && badOptions.Refusals[0] is { Code: PrototypePipelineCodes.ProjectInvalid, Subject: "project_id" },
            "an option refusal refuses the pipeline and keeps the typology stage as generated", Show(badOptions, error));
        var badLot = Safe(() => TypologyPipeline.Run(pack, new TypologyBriefRequest(4, new PrototypeLot(2, 12), 6), options), ref error);
        Check(badLot is { Status: PrototypePipelineStatus.Refused } && badLot.Refusals.Any(r => r.Code == PrototypeCodes.LotInvalid && r.Subject == "lot"),
            "an out-of-range lot is a typed refusal", Show(badLot, error));
        var refusedJson = badFamily is null ? "" : TypologyPipelineSerializer.Serialize(badFamily);
        Check(refusedJson.Contains("\"status\":\"refused\"", StringComparison.Ordinal) && refusedJson.Contains("\"brief\":null", StringComparison.Ordinal)
            && refusedJson.Contains("\"alternatives\":null", StringComparison.Ordinal), "a refused result serializes with a null brief and alternative set", refusedJson.Length > 300 ? refusedJson[..300] : refusedJson);
        Check(Throws(() => TypologyPipeline.Run((SkillManifest)null!, request, options)) && Throws(() => TypologyPipeline.Run(pack, null!, options))
            && Throws(() => TypologyPipeline.Run(pack, request, null!)) && Throws(() => TypologyPipeline.Run((SkillResolution)null!, request, options))
            && Throws(() => TypologyPipelineSerializer.Serialize(null!)), "null arguments are programming errors (ArgumentNullException)");

        // ---- the existing pipelines are untouched ----
        var apartment = PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 6, Seed: 0), options));
        Check(Sha(apartment) == PrototypePipelinePinnedSha256, "after typology pipeline runs the P7-07 PrototypePipeline pinned digest is unchanged", Sha(apartment));
        Check(BriefDigest(TypologyBrief.Generate(pack, request)) == TypologyBriefPinnedSha256, "after typology pipeline runs the P5-11 TypologyBrief pinned digest is unchanged");
    }

    // The gate an independent recomputation stops a typology plan at (null when it passes every gate), with the failed
    // checks as code@subject and, when it passes, the BIM-IR project FacadeIrBuilder gives.
    static PrototypePipelineGate? ExpectedGate(ProjectBrief brief, SpacePlan plan, Alternative origin, SkillManifest caba, out List<string> failures, out Project? project)
    {
        failures = new List<string>();
        project = null;
        if (origin.Status != AlternativeStatus.Ranked)
        {
            failures.AddRange(origin.Reasons);
            return PrototypePipelineGate.Prototype;
        }
        var circulation = CirculationAnalyzer.Analyze(brief, plan);
        if (!circulation.IsValid)
        {
            failures.AddRange(circulation.Checks.Where(c => c.Hard && !c.Satisfied).Select(c => c.Code + "@" + c.Subject));
            return PrototypePipelineGate.Circulation;
        }
        var facade = FacadePlanner.Place(plan, caba);
        if (!facade.IsComplete)
        {
            failures.AddRange(facade.Checks.Where(c => c.Hard && !c.Satisfied).Select(c => c.Code + "@" + c.Subject));
            return PrototypePipelineGate.Facade;
        }
        try
        {
            var ir = FacadeIrBuilder.AddWindows(FacadeIrBuilder.Prototype(ProjectId, 0, plan), facade, circulation);
            if (!ir.IsComplete)
            {
                failures.AddRange(ir.Checks.Where(c => c.Hard && !c.Satisfied).Select(c => c.Code + "@" + c.Subject));
                return PrototypePipelineGate.FacadeIr;
            }
            project = ir.Project;
            return null;
        }
        catch (Exception error) when (error is IntentException or ArgumentException or InvalidOperationException)
        {
            failures.Add(error.GetType().Name + ": " + error.Message);
            return PrototypePipelineGate.FacadeIr;
        }
    }

    static PrototypePipelineResult WithStrategy(PrototypePipelineResult result) =>
        result with { Alternatives = result.Alternatives.Select(a => a with { Strategy = PrototypeStrategies.WholeLot }).ToArray() };

    // Families 1..5 on a 16 x 16 m lot, 4 candidates each: accepted count and the gates of the rejected (diagnostics).
    static string Sweep(SkillManifest pack, PrototypePipelineOptions options)
    {
        var parts = new List<string>();
        for (var family = 1; family <= 5; family++)
        {
            string? error = null;
            var result = Safe(() => TypologyPipeline.Run(pack, new TypologyBriefRequest(family, new PrototypeLot(16, 16), 4), options), ref error);
            parts.Add("f" + Text(family) + " " + (result is null ? "error" : Summary(result)));
        }
        return string.Join(" / ", parts);
    }

    static string Summary(TypologyPipelineResult result) => result.Status + " accepted " + Text(result.Accepted.Count) + " rejected "
        + string.Join(",", result.Alternatives.Where(a => !a.Accepted).Select(a => a.RejectedAt + ":" + string.Join("+", a.Rejections.Select(r => r.Code + "@" + r.Subject).Distinct().Take(3))));

    static TypologyPipelineResult? Safe(Func<TypologyPipelineResult> run, ref string? error)
    {
        try { return run(); }
        catch (Exception failure)
        {
            error = failure.GetType().Name + ": " + failure.Message;
            return null;
        }
    }

    static bool Throws(Action action)
    {
        try { action(); }
        catch (ArgumentNullException) { return true; }
        catch (Exception) { return false; }
        return false;
    }

    // TypologyBriefTests.Digest: canonical brief JSON, canonical alternative set JSON and candidates.
    static string BriefDigest(TypologyBriefResult result)
    {
        var text = (result.Brief is null ? "" : BriefSerializer.Serialize(result.Brief)) + "\n"
            + (result.Alternatives is null ? "" : AlternativeSetSerializer.Serialize(result.Alternatives)) + "\n"
            + string.Join(";", result.Candidates.Select(c => c.Index.ToString(CultureInfo.InvariantCulture) + c.Strategy + c.Seed.ToString(CultureInfo.InvariantCulture)));
        return Sha(text);
    }

    static string Show(TypologyPipelineResult? result, string? error) => result is null ? "error " + error
        : result.Status + " [" + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject)) + "]";

    static string Describe(PrototypePipelineResult result) =>
        result.Status + " refusals [" + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject)) + "] " + string.Join(" | ", result.Alternatives.Select(Describe));

    static string Describe(PrototypePipelineAlternative alternative) =>
        "#" + Text(alternative.Index) + " " + (alternative.Strategy ?? "-") + " rank " + Text(alternative.Rank) + " total " + alternative.Total.ToString("0.######", CultureInfo.InvariantCulture) + " "
        + (alternative.Accepted ? "accepted" : "rejected at " + alternative.RejectedAt + ": " + string.Join("; ", alternative.Rejections.Select(r => r.Code + "@" + r.Subject).Take(4)));

    static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    static string Json<T>(T value) => JsonSerializer.Serialize(value);
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
