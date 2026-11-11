using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P5-14 typology pipeline with composed pack rules oracle: the opt-in TypologyPipelineOptions.ComposedRules plans the
// TypologyPipeline candidates with the P5-13 TypologyRules of the skill resolution (pack + composed code packs) instead of
// the fixed PrototypeTemplates.Rules and records the planner rules and their traces in the result JSON. Without the option
// (or with it off) the output is byte-identical to P5-12 (both P5-12 pins unchanged); with it the typology stage is
// TypologyRules.Generate bit for bit, the pipeline stage PrototypePipeline.Rank of its plans, a resolution equal to the
// defaults gives the P5-12 bytes plus the rules member, stricter composed minimums reach the gated prototypes, the
// composed run has its own pinned digest, and the P5-13 refusals (typology_minimum_conflict among them) pass through typed,
// before the option refusals. Unit-tested evidence only.
static class TypologyPipelineRulesTests
{
    const string Pack = "bim.typology.vivienda-unifamiliar";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stricter = "xx.test.stricter-bedroom";
    const double Tolerance = 1e-9;
    // TypologyPipelineTests.PinnedSha256 (P5-12): family 4, 12 x 12 m lot, 6 alternatives, seed 0 (infeasible).
    const string P512PinnedSha256 = "3a55177415f42f4d324cf3e4bd298223595c429006595a635aa12e9fb1a3d20b";
    // TypologyPipelineTests.PinnedAcceptedSha256 (P5-12): family 3, 16 x 16 m lot, 4 alternatives, seed 0 (ranked).
    const string P512PinnedAcceptedSha256 = "eb8720c1a98a749048df6ac69f1794b965e833efc963eb9ddfe58db0ae886f4e";
    // TypologyBriefTests.PinnedSha256 (P5-11): family 4, 12 x 12 m lot, 6 alternatives, seed 0 under PrototypeTemplates.Rules.
    const string P511PinnedSha256 = "85102ea05864ea67a8dad79bed06b2ce4a643056a09d8aee7d8044127340b513";
    // SHA-256 of TypologyPipelineSerializer output for the CABA + typology resolution with ComposedRules, family 3, the
    // 16 x 16 m lot, 4 alternatives, seed 0, the CABA pack and ProjectId with default stage options (regression pin, identical on Linux and Windows CI; ranked, two accepted
    // where the fixed rules accept one).
    const string PinnedComposedSha256 = "a8c62da59e0eee2d25af4d27bc37fcf2f67ae366e7e48446f1edfe3c87f7c0dd";
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-4000-8000-0000000007a7");

    // A code pack whose minimum rules are exactly PrototypeTemplates.CabaRules (TypologyRulesTests.DefaultsPack).
    const string DefaultsPack = """
    {"manifest_version":1,"id":"ar.caba.codigo-edificacion","version":"0.1.0","kind":"code","description":"test copy of the prototype CABA minimums","priority":100,
     "parameters":[{"name":"corredor_lado_min_m","type":"number","default":1.0}],
     "rules":[
      {"id":"living-area-min","severity":"error","target":{"room":"living*"},"check":{"op":"min","property":"area_m2","value":9},"message":"m"},
      {"id":"living-side-min","severity":"error","target":{"room":"living*"},"check":{"op":"min","property":"min_side_m","value":2.8},"message":"m"},
      {"id":"kitchen-area-min","severity":"error","target":{"room":"kitchen*"},"check":{"op":"min","property":"area_m2","value":3},"message":"m"},
      {"id":"kitchen-side-min","severity":"error","target":{"room":"kitchen*"},"check":{"op":"min","property":"min_side_m","value":1.5},"message":"m"},
      {"id":"bedroom-area-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"area_m2","value":7.5},"message":"m"},
      {"id":"bedroom-side-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"min_side_m","value":2.5},"message":"m"},
      {"id":"bathroom-area-min","severity":"error","target":{"room":"bath*"},"check":{"op":"min","property":"area_m2","value":1.4},"message":"m"},
      {"id":"bathroom-side-min","severity":"error","target":{"room":"bath*"},"check":{"op":"min","property":"min_side_m","value":0.9},"message":"m"},
      {"id":"corredor-side-min","severity":"error","target":{"room":"corredor*"},"check":{"op":"min","property":"min_side_m","parameter":"corredor_lado_min_m"},"message":"m"}
     ]}
    """;

    // A stricter code pack: bedrooms at least 3.2 m on their short side (TypologyRulesTests.StricterPack).
    const string StricterPack = """
    {"manifest_version":1,"id":"xx.test.stricter-bedroom","version":"1.0.0","kind":"code","description":"test pack with a stricter bedroom side","priority":100,
     "parameters":[{"name":"bedroom_side_min_m","type":"number","default":3.2}],
     "rules":[
      {"id":"bedroom-side-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"min_side_m","parameter":"bedroom_side_min_m"},"message":"m"}
     ]}
    """;

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "typology-pipeline-rules: " + name, detail);

        var catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills"));
        var pack = catalog.Find(Pack);
        var caba = catalog.Find(Caba);
        Check(pack is not null && caba is not null, "the typology and CABA packs load from skills/");
        if (pack is null || caba is null) return;
        var options = new PrototypePipelineOptions(caba, ProjectId);
        var composedRules = new TypologyPipelineOptions(ComposedRules: true);
        var lot = new PrototypeLot(12, 12);
        var bigLot = new PrototypeLot(16, 16);
        var request = new TypologyBriefRequest(4, lot, 6);
        var request3 = new TypologyBriefRequest(3, bigLot, 4);
        string? error = null;

        // ---- default output byte-identical: no option, or the option off, gives the P5-12 bytes and both pins ----
        var typologyOnly = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Pack) });
        Check(new TypologyPipelineOptions().ComposedRules == false && TypologyPipelineOptions.Default.ComposedRules == false, "the composed-rules option is off by default");
        var plain = Safe(() => TypologyPipeline.Run(pack, request, options), ref error);
        var off = Safe(() => TypologyPipeline.Run(typologyOnly, request, options, new TypologyPipelineOptions()), ref error);
        var plain3 = Safe(() => TypologyPipeline.Run(pack, request3, options), ref error);
        var off3 = Safe(() => TypologyPipeline.Run(typologyOnly, request3, options, TypologyPipelineOptions.Default), ref error);
        Check(plain is not null && off is not null && Sha(TypologyPipelineSerializer.Serialize(plain)) == P512PinnedSha256 && Sha(TypologyPipelineSerializer.Serialize(off)) == P512PinnedSha256,
            "without composed rules the family-4 12 x 12 m run keeps the P5-12 pinned digest (infeasible)",
            (plain is null ? error ?? "" : Sha(TypologyPipelineSerializer.Serialize(plain))) + " / " + (off is null ? error ?? "" : Sha(TypologyPipelineSerializer.Serialize(off))));
        Check(plain3 is not null && off3 is not null && Sha(TypologyPipelineSerializer.Serialize(plain3)) == P512PinnedAcceptedSha256
            && Sha(TypologyPipelineSerializer.Serialize(off3)) == P512PinnedAcceptedSha256,
            "without composed rules the family-3 16 x 16 m run keeps the P5-12 pinned digest (ranked)",
            (plain3 is null ? error ?? "" : Sha(TypologyPipelineSerializer.Serialize(plain3))) + " / " + (off3 is null ? error ?? "" : Sha(TypologyPipelineSerializer.Serialize(off3))));
        Check(plain is { ComposedRules: false, Rules: null } && off is { ComposedRules: false, Rules: null }
            && !TypologyPipelineSerializer.Serialize(off).Contains("\"planner_rules\"", StringComparison.Ordinal) && !HasTopRules(TypologyPipelineSerializer.Serialize(off)),
            "without composed rules the result holds no rule set and the JSON no rules member");
        var composed = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack) });
        var composedOff = Safe(() => TypologyPipeline.Run(composed, request3, options, new TypologyPipelineOptions(ComposedRules: false)), ref error);
        var composedPlain = Safe(() => TypologyPipeline.Run(composed, request3, options), ref error);
        Check(composedOff is not null && composedPlain is not null && TypologyPipelineSerializer.Serialize(composedOff) == TypologyPipelineSerializer.Serialize(composedPlain),
            "with the option off a composed resolution gives the P5-12 Run(resolution) bytes", error ?? "");

        // ---- a resolution equal to the defaults gives the P5-12 bytes plus the rules member ----
        var defaults = SkillResolver.Resolve(new SkillCatalog(new[] { SkillParser.Parse(DefaultsPack), pack }), new[] { new SkillRequest(Caba), new SkillRequest(Pack) });
        var fromDefaults = Safe(() => TypologyPipeline.Run(defaults, request, options, composedRules), ref error);
        var defaultsJson = fromDefaults is null ? "" : TypologyPipelineSerializer.Serialize(fromDefaults);
        var defaultsRules = RulesRaw(defaultsJson);
        Check(fromDefaults is { ComposedRules: true, Rules: { Succeeded: true } } && fromDefaults.Rules.Rules.Rules.SequenceEqual(PrototypeTemplates.Rules(lot).Rules)
            && BriefDigest(fromDefaults.Typology) == P511PinnedSha256,
            "composed rules equal to the defaults are PrototypeTemplates.Rules and keep the P5-11 typology digest", fromDefaults is null ? error ?? "" : BriefDigest(fromDefaults.Typology));
        Check(defaultsRules is not null && Sha(defaultsJson.Replace(",\"rules\":" + defaultsRules, "", StringComparison.Ordinal)) == P512PinnedSha256,
            "without its rules member the defaults composed run is the P5-12 pinned JSON byte for byte",
            defaultsRules is null ? "no rules member" : Sha(defaultsJson.Replace(",\"rules\":" + defaultsRules, "", StringComparison.Ordinal)));

        // ---- the composed CABA + typology run: TypologyRules plans, PrototypePipeline gates ----
        var run = Safe(() => TypologyPipeline.Run(composed, request3, options, composedRules), ref error);
        Check(run is not null, "TypologyPipeline.Run with composed rules runs the CABA + typology family-3 request", error ?? "");
        if (run is null) return;
        var expectedRules = TypologyRules.Generate(composed, request3);
        Check(BriefDigest(run.Typology) == BriefDigest(expectedRules.Result) && run.Typology.Status == expectedRules.Result.Status && run.Typology.Status != PrototypeStatus.Refused,
            "the typology stage is TypologyRules.Generate of the resolution and request, bit for bit", BriefDigest(run.Typology) + " vs " + BriefDigest(expectedRules.Result));
        Check(run is { ComposedRules: true, Rules: { Succeeded: true } } && expectedRules.Rules is not null && run.Rules.Rules.Rules.SequenceEqual(expectedRules.Rules.Rules.Rules)
            && run.Rules.Traces.SequenceEqual(expectedRules.Rules.Traces) && run.Rules.Rules.Rules.Any(rule => rule.NamePattern == "dining*")
            && !run.Rules.Rules.Rules.SequenceEqual(PrototypeTemplates.Rules(bigLot).Rules),
            "the result keeps the composed TypologyRules set (the CABA dining minimums among them), not the fixed template rules", Rules(run));
        if (run.Typology.Brief is null || run.Typology.Alternatives is null) return;
        var plans = run.Typology.Alternatives.Alternatives.OrderBy(a => a.Index).Select(a => a.Plan).ToArray();
        Check(run.Status != PrototypePipelineStatus.Refused && run.Alternatives.Count == 4 && run.Alternatives.All(a => a.Strategy == PrototypeStrategies.WholeLot)
            && PrototypePipelineSerializer.Serialize(run.Pipeline) == PrototypePipelineSerializer.Serialize(WithStrategy(PrototypePipeline.Rank(run.Typology.Brief, plans, options, request3.Weights))),
            "the gates and ranking are PrototypePipeline.Rank of the composed-rule plans (whole_lot strategy), bit for bit", Summary(run));
        Check(run.Accepted.All(a => a.Plan.Rooms.Where(room => room.RoomId == "dining").All(room => ShortSide(room.Rect) >= 2.8 - Tolerance)),
            "every accepted composed-rule prototype has its dining room at least 2.8 m wide", Summary(run));
        Console.WriteLine("::notice::generative typology pipeline with composed CABA rules, family 3 on 16 x 16 m: " + Summary(run));

        // ---- the rules are recorded in the result JSON ----
        var canonical = TypologyPipelineSerializer.Serialize(run);
        using (var document = JsonDocument.Parse(canonical))
        {
            var top = document.RootElement;
            var hasRules = top.TryGetProperty("rules", out var rules);
            var plannerRules = hasRules && rules.ValueKind == JsonValueKind.Object ? rules.GetProperty("planner_rules").EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            var traces = hasRules && rules.ValueKind == JsonValueKind.Object ? rules.GetProperty("traces").EnumerateArray().ToArray() : Array.Empty<JsonElement>();
            var set = run.Rules!;
            Check(hasRules && rules.GetProperty("succeeded").GetBoolean() && plannerRules.Length == set.Rules.Rules.Count
                && plannerRules.Zip(set.Rules.Rules).All(pair => Str(pair.First, "reference") == pair.Second.Reference && Str(pair.First, "name_pattern") == pair.Second.NamePattern
                    && Str(pair.First, "room_id") == pair.Second.RoomId && Num(pair.First, "min_area_m2") == pair.Second.MinAreaSquareMetres && Num(pair.First, "min_side_m") == pair.Second.MinSideMetres),
                "the JSON records every planner rule (room id, name pattern, minimum area and side, reference) in order", hasRules ? rules.GetRawText() : "no rules member");
            Check(traces.Length == set.Traces.Count && traces.Zip(set.Traces).All(pair => pair.First.GetProperty("planner_rule").GetInt32() == pair.Second.PlannerRuleIndex
                    && Str(pair.First, "property") == pair.Second.Property && Num(pair.First, "threshold") == pair.Second.Threshold && Str(pair.First, "rule") == pair.Second.Rule?.ToString()
                    && Str(pair.First, "threshold_source") == pair.Second.ThresholdSource?.ToString() && Str(pair.First, "reference") == pair.Second.Reference)
                && traces.Any(trace => Str(trace, "rule") == Caba + ":rule:dining-side-min"),
                "the JSON records every rule trace with its planner rule index, property, threshold, pack rule, parameter source and reference",
                hasRules ? rules.GetRawText() : "no rules member");
            Check(canonical.StartsWith("{\"typology_pipeline_version\":1,\"status\":", StringComparison.Ordinal) && !canonical.Contains('\n', StringComparison.Ordinal)
                && top.GetProperty("pipeline").GetRawText() == PrototypePipelineSerializer.Serialize(run.Pipeline)
                && top.GetProperty("typology").GetProperty("alternatives").GetRawText() == AlternativeSetSerializer.Serialize(run.Typology.Alternatives),
                "the composed JSON keeps the P5-12 members and embeds the canonical alternative set and pipeline JSON");
        }

        // ---- determinism and the pinned digest ----
        var again = Safe(() => TypologyPipeline.Run(composed, request3, options, composedRules), ref error);
        var reresolved = Safe(() => TypologyPipeline.Run(SkillResolver.Resolve(SkillLoader.LoadDirectory(Path.Combine(root, "skills")), new[] { new SkillRequest(Caba), new SkillRequest(Pack) }),
            request3, options, new TypologyPipelineOptions(true)), ref error);
        string? german = null;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var local = Safe(() => TypologyPipeline.Run(composed, request3, options, composedRules), ref error);
            german = local is null ? null : TypologyPipelineSerializer.Serialize(local);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(again is not null && reresolved is not null && TypologyPipelineSerializer.Serialize(again) == canonical && TypologyPipelineSerializer.Serialize(reresolved) == canonical
            && german == canonical, "same resolution + family size + lot + seed with composed rules give byte-identical JSON across runs, re-resolutions and cultures", error ?? "");
        Check(Sha(canonical) == PinnedComposedSha256, "composed-rules family-3 16 x 16 m seed-0 typology pipeline JSON matches its pinned SHA-256",
            "actual " + Sha(canonical) + " | " + Summary(run));
        Check(Sha(canonical) != P512PinnedAcceptedSha256 && canonical != TypologyPipelineSerializer.Serialize(composedPlain!), "the composed-rules run differs from the fixed-rules run");

        // ---- composed minimums change the gated prototypes visibly ----
        var cabaRun = Safe(() => TypologyPipeline.Run(composed, request, options, composedRules), ref error);
        Check(cabaRun is not null && BriefDigest(cabaRun.Typology) != P511PinnedSha256 && BriefDigest(cabaRun.Typology) == BriefDigest(TypologyRules.Generate(composed, request).Result),
            "on the family-4 12 x 12 m request the composed CABA rules give other typology candidates than the fixed rules", cabaRun is null ? error ?? "" : BriefDigest(cabaRun.Typology));
        var strictCatalog = new SkillCatalog(catalog.Skills.Append(SkillParser.Parse(StricterPack)));
        var strict = SkillResolver.Resolve(strictCatalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(Stricter) });
        var strictRun = Safe(() => TypologyPipeline.Run(strict, request, options, composedRules), ref error);
        var strictBedrooms = strictRun?.Typology.Alternatives?.Ranked.SelectMany(a => a.Plan.Rooms.Where(room => room.RoomId == "bedroom")).ToArray() ?? Array.Empty<PlannedRoom>();
        Check(strictRun is { Typology.Status: PrototypeStatus.Ranked } && strictRun.Status != PrototypePipelineStatus.Refused && strictBedrooms.Length > 0
            && strictBedrooms.All(room => ShortSide(room.Rect) >= 3.2 - Tolerance)
            && strictRun.Alternatives.All(a => a.Plan.Rooms.Where(room => room.RoomId == "bedroom").All(room => ShortSide(room.Rect) >= 3.2 - Tolerance) || a.RejectedAt == PrototypePipelineGate.Prototype)
            && strictRun.Rules is { } strictRules && strictRules.Traces.Any(trace => trace.Rule?.ToString() == Stricter + ":rule:bedroom-side-min"),
            "a stricter composed bedroom side (3.2 m) reaches every ranked candidate that enters the gates, traced to its pack rule",
            strictRun is null ? error ?? "" : Summary(strictRun) + " | " + string.Join(" ", strictBedrooms.Select(room => F(ShortSide(room.Rect)))));
        var strictPlain = Safe(() => TypologyPipeline.Run(strict, request, options), ref error);
        Check(strictPlain is not null && strictRun is not null && BriefDigest(strictPlain.Typology) != BriefDigest(strictRun.Typology),
            "without the option the same stricter resolution plans with the fixed rules (other candidates)");

        // ---- typed refusals pass through (typology_minimum_conflict among them), before the option refusals ----
        var strictArea = strictCatalog.Skills.First(skill => skill.Id == Stricter);
        var tooWide = strictArea with { Parameters = new[] { new SkillParameter("bedroom_side_min_m", SkillParameterType.Number, SkillValue.Of(4.1)) } };
        var wide = Safe(() => TypologyPipeline.Run(Resolve(catalog, tooWide), request, options, composedRules), ref error);
        Check(wide is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, Typology.Brief: null, Typology.Status: PrototypeStatus.Refused, Rules.Succeeded: false }
            && wide.Refusals.Count == 1 && wide.Refusals[0] is { Code: SkillTypologyCodes.MinimumConflict, Subject: "bedroom", Hard: true, Satisfied: false }
            && wide.Refusals[0].Detail.Contains(Stricter + " bedroom-side-min", StringComparison.Ordinal),
            "a composed bedroom side above the programme maximum is a typed typology_minimum_conflict refusal with no alternatives", Show(wide, error));
        var wideJson = wide is null ? "" : TypologyPipelineSerializer.Serialize(wide);
        Check(wideJson.Contains("\"rules\":{\"succeeded\":false,", StringComparison.Ordinal) && wideJson.Contains("typology_minimum_conflict", StringComparison.Ordinal),
            "the refused composed run still records its rule set in the JSON", wideJson.Length > 300 ? wideJson[..300] : wideJson);
        var wideBoth = Safe(() => TypologyPipeline.Run(Resolve(catalog, tooWide), request, options with { ProjectId = Guid.Empty }, composedRules), ref error);
        Check(wideBoth is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 } && wideBoth.Refusals.Count == 2
            && wideBoth.Refusals[0].Code == SkillTypologyCodes.MinimumConflict && wideBoth.Refusals[1].Code == PrototypePipelineCodes.ProjectInvalid,
            "the composed rule refusals come first, then the option refusals", Show(wideBoth, error));
        var areaPack = strictArea with
        {
            Rules = new[] { new SkillRule("bedroom-area-min", SkillSeverity.Error, new SkillTarget(null, "bedroom*"), new SkillCheck(SkillCheckOp.Min, "area_m2", SkillValue.Of(10)), "m") },
        };
        var area = Safe(() => TypologyPipeline.Run(Resolve(catalog, areaPack), request, options, composedRules), ref error);
        Check(area is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, ComposedRules: true, Rules: null }
            && area.Refusals.Any(r => r is { Code: SkillTypologyCodes.MinimumConflict, Subject: "bedroom", Hard: true, Satisfied: false })
            && TypologyPipelineSerializer.Serialize(area).Contains("\"rules\":null", StringComparison.Ordinal),
            "a composed area minimum above the programme minimum is typology_minimum_conflict before any rule set (rules null in the JSON)", Show(area, error));
        var questionPack = strictArea with
        {
            Rules = new[] { new SkillRule("bed-side", SkillSeverity.Error, new SkillTarget(null, "bed?oom*"), new SkillCheck(SkillCheckOp.Min, "min_side_m", SkillValue.Of(3)), "m") },
        };
        var question = Safe(() => TypologyPipeline.Run(Resolve(catalog, questionPack), request, options, composedRules), ref error);
        Check(question is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 }
            && question.Refusals.Any(r => r.Code == TypologyRuleCodes.PatternUnsupported && r.Subject == Stricter + "/bed-side"),
            "a composed rule the planner cannot apply is a typed typology_rule_pattern_unsupported refusal", Show(question, error));
        var sauna = Safe(() => TypologyPipeline.Run(composed, request with { Include = new[] { "sauna" } }, options, composedRules), ref error);
        Check(sauna is { Status: PrototypePipelineStatus.Refused, Rules: null } && sauna.Refusals.Any(r => r.Code == SkillTypologyCodes.RoomKindUnknown && r.Subject == "sauna"),
            "programme refusals pass through unchanged with composed rules", Show(sauna, error));
        Check(Throws(() => TypologyPipeline.Run(composed, request, options, null!)) && Throws(() => TypologyPipeline.Run((SkillResolution)null!, request, options, composedRules))
            && Throws(() => TypologyPipeline.Run(composed, null!, options, composedRules)) && Throws(() => TypologyPipeline.Run(composed, request, null!, composedRules))
            && Throws(() => TypologyPipeline.Run(composed, request, options with { Pack = null! }, composedRules)),
            "null arguments of the composed-rules overload are programming errors (ArgumentNullException)");

        // ---- the P5-12 pins are unchanged after composed runs ----
        Check(Sha(TypologyPipelineSerializer.Serialize(TypologyPipeline.Run(pack, request, options))) == P512PinnedSha256
            && Sha(TypologyPipelineSerializer.Serialize(TypologyPipeline.Run(pack, request3, options))) == P512PinnedAcceptedSha256,
            "after composed-rules runs both P5-12 pinned digests are unchanged");
    }

    static SkillResolution Resolve(SkillCatalog catalog, SkillManifest extra) =>
        SkillResolver.Resolve(new SkillCatalog(catalog.Skills.Append(extra)), new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(extra.Id) });

    static bool HasTopRules(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("rules", out _);
    }

    static string? RulesRaw(string json)
    {
        if (json.Length == 0) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("rules", out var rules) ? rules.GetRawText() : null;
    }

    static string? Str(JsonElement element, string name) => element.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    static double? Num(JsonElement element, string name) => element.GetProperty(name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    static PrototypePipelineResult WithStrategy(PrototypePipelineResult result) =>
        result with { Alternatives = result.Alternatives.Select(a => a with { Strategy = PrototypeStrategies.WholeLot }).ToArray() };

    static string Rules(TypologyPipelineResult result) => result.Rules is null ? "no rules" : string.Join(" | ", result.Rules.Rules.Rules.Select(rule => rule.NamePattern ?? "#" + rule.RoomId));

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
        : result.Status + " [" + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject + " " + r.Detail)) + "]";

    static double ShortSide(PlanRect rect) => Math.Min(rect.Width, rect.Depth);
    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
