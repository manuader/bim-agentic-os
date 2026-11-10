using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bim.Core;
using Bim.Generative;

// P5-13 composed pack rules constrain the typology planner oracle: the error-severity minimum area_m2 / min_side_m rules
// of a composed skill resolution (CABA + typology, plus a stricter pack) become the SpacePlanner rules of the typology
// prototypes with per-rule pack/rule-id traces instead of the fixed PrototypeTemplates.Rules; a resolution whose rules
// equal those defaults reproduces the P5-11 pinned digest; stricter composed minimums change the plans visibly; bad rule
// patterns and thresholds are typed refusals; the opt-in path leaves TypologyBrief unchanged. Unit-tested evidence only.
static class TypologyRulesTests
{
    const string Pack = "bim.typology.vivienda-unifamiliar";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stricter = "xx.test.stricter-bedroom";
    const double Tolerance = 1e-9;
    // TypologyBriefTests.PinnedSha256: family 4, 12 x 12 m lot, 6 alternatives, seed 0 under PrototypeTemplates.Rules (P5-11 pin).
    const string P511PinnedSha256 = "85102ea05864ea67a8dad79bed06b2ce4a643056a09d8aee7d8044127340b513";

    // A code pack whose minimum rules are exactly PrototypeTemplates.CabaRules (same id, patterns, values and rule ids).
    const string DefaultsPack = """
    {"manifest_version":1,"id":"ar.caba.codigo-edificacion","version":"0.1.0","kind":"code","description":"test copy of the prototype CABA minimums","priority":100,
     "rules":[
      {"id":"living-area-min","severity":"error","target":{"room":"living*"},"check":{"op":"min","property":"area_m2","value":9},"message":"m"},
      {"id":"living-side-min","severity":"error","target":{"room":"living*"},"check":{"op":"min","property":"min_side_m","value":2.8},"message":"m"},
      {"id":"kitchen-area-min","severity":"error","target":{"room":"kitchen*"},"check":{"op":"min","property":"area_m2","value":3},"message":"m"},
      {"id":"kitchen-side-min","severity":"error","target":{"room":"kitchen*"},"check":{"op":"min","property":"min_side_m","value":1.5},"message":"m"},
      {"id":"bedroom-area-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"area_m2","value":7.5},"message":"m"},
      {"id":"bedroom-side-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"min_side_m","value":2.5},"message":"m"},
      {"id":"bathroom-area-min","severity":"error","target":{"room":"bath*"},"check":{"op":"min","property":"area_m2","value":1.4},"message":"m"},
      {"id":"bathroom-side-min","severity":"error","target":{"room":"bath*"},"check":{"op":"min","property":"min_side_m","value":0.9},"message":"m"}
     ]}
    """;

    // A stricter code pack: bedrooms at least 3.5 m on their short side (parameter threshold), plus a warning rule and a
    // rule for a room the programme does not have, neither of which may reach the planner.
    const string StricterPack = """
    {"manifest_version":1,"id":"xx.test.stricter-bedroom","version":"1.0.0","kind":"code","description":"test pack with a stricter bedroom side","priority":100,
     "parameters":[{"name":"bedroom_side_min_m","type":"number","default":3.5}],
     "rules":[
      {"id":"bedroom-side-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"min_side_m","parameter":"bedroom_side_min_m"},"message":"m"},
      {"id":"bedroom-side-wish","severity":"warning","target":{"room":"bedroom*"},"check":{"op":"min","property":"min_side_m","value":5},"message":"m"},
      {"id":"sauna-area-min","severity":"error","target":{"room":"sauna*"},"check":{"op":"min","property":"area_m2","value":20},"message":"m"}
     ]}
    """;

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "typology-rules: " + name, detail);

        var catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills"));
        var pack = catalog.Find(Pack);
        var caba = catalog.Find(Caba);
        Check(pack is not null && caba is not null, "the typology and CABA packs load from skills/");
        if (pack is null || caba is null) return;
        var lot = new PrototypeLot(12, 12);
        var request = new TypologyBriefRequest(4, lot, 6);

        // ---- a resolution equal to the defaults reproduces the P5-11 pinned digest ----
        var defaultsCatalog = new SkillCatalog(new[] { SkillParser.Parse(DefaultsPack), pack });
        var defaults = SkillResolver.Resolve(defaultsCatalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack) });
        var fromDefaults = Safe(() => TypologyRules.Generate(defaults, request));
        Check(defaults.Succeeded && fromDefaults.Rules is { Succeeded: true } set0 && set0.Rules.Rules.SequenceEqual(PrototypeTemplates.Rules(lot).Rules),
            "rules composed from a pack equal to the CABA defaults are exactly PrototypeTemplates.Rules(lot)", Show(fromDefaults));
        Check(fromDefaults.Result.Status == PrototypeStatus.Ranked && Digest(fromDefaults.Result) == P511PinnedSha256,
            "a resolution equal to the defaults reproduces the P5-11 pinned digest", "actual " + Digest(fromDefaults.Result) + " " + Show(fromDefaults));
        var p511 = TypologyBrief.Generate(defaults, request);
        Check(Digest(p511) == P511PinnedSha256, "TypologyBrief.Generate is unchanged (P5-11 pinned digest)", Digest(p511));
        if (fromDefaults.Rules is { } defaultSet)
        {
            var pairs = defaultSet.Traces.Where(trace => trace.Rule is not null).Select(trace => trace.Rule!.ToString() + "@" + trace.PlannerRuleIndex.ToString(CultureInfo.InvariantCulture)).ToArray();
            Check(pairs.SequenceEqual(new[]
                {
                    "ar.caba.codigo-edificacion:rule:living-area-min@0", "ar.caba.codigo-edificacion:rule:living-side-min@0",
                    "ar.caba.codigo-edificacion:rule:kitchen-area-min@1", "ar.caba.codigo-edificacion:rule:kitchen-side-min@1",
                    "ar.caba.codigo-edificacion:rule:bedroom-area-min@2", "ar.caba.codigo-edificacion:rule:bedroom-side-min@2",
                    "ar.caba.codigo-edificacion:rule:bathroom-area-min@3", "ar.caba.codigo-edificacion:rule:bathroom-side-min@3",
                }), "every pack minimum is traced to its planner rule index by pack and rule id", string.Join(" ", pairs));
            var templates = defaultSet.Traces.Where(trace => trace.Rule is null).Select(trace => trace.PlannerRuleIndex).ToArray();
            Check(templates.SequenceEqual(new[] { 4, 5 }) && defaultSet.Traces.All(trace => trace.Reference == defaultSet.Rules.Rules[trace.PlannerRuleIndex].Reference),
                "the hall and laundry template rules are traced as template rules and every trace carries its planner rule reference", string.Join(",", templates));
        }

        // ---- the real CABA pack composed with the typology: its matching minimums (dining too) constrain the planner ----
        var composed = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack) });
        var fromCaba = Safe(() => TypologyRules.Generate(composed, request));
        var patterns = fromCaba.Rules is null ? "" : string.Join(" ", fromCaba.Rules.Rules.Rules.Select(rule => rule.NamePattern ?? "#" + rule.RoomId));
        Check(fromCaba.Rules is { Succeeded: true } && patterns == "living* dining* bedroom* bath* kitchen* #hall #laundry",
            "CABA + typology gives the CABA minimum rules whose pattern matches a programme room, in pack order, then the template rules", patterns);
        var dining = fromCaba.Rules?.Rules.Rules.FirstOrDefault(rule => rule.NamePattern == "dining*");
        Check(dining is { MinAreaSquareMetres: 9, MinSideMetres: 2.8, Reference: "ar.caba.codigo-edificacion dining-area-min dining-side-min" },
            "the CABA dining minimums (9 m2, 2.8 m) reach the planner with their pack and rule ids", dining?.ToString() ?? "none");
        Check(fromCaba.Result.Status == PrototypeStatus.Ranked && Digest(fromCaba.Result) != P511PinnedSha256 && fromCaba.Result.Alternatives is { } cabaSet
            && cabaSet.Ranked.All(alternative => alternative.Plan.Rooms.Where(room => room.RoomId == "dining").All(room => ShortSide(room.Rect) >= 2.8 - Tolerance)),
            "the composed CABA minimums constrain the ranked plans (every dining room at least 2.8 m wide), unlike the fixed rules", Show(fromCaba));
        Check(fromCaba.Result.Alternatives is { } traced && traced.Ranked.SelectMany(alternative => alternative.Plan.Constraints)
                .Any(constraint => constraint.Code == PlanCodes.SideMin && constraint.Subject == "dining" && constraint.Detail.Contains("ar.caba.codigo-edificacion dining-area-min dining-side-min", StringComparison.Ordinal)),
            "the plan side constraint of the dining room names the pack and rule ids");
        var study = Safe(() => TypologyRules.Generate(composed, request with { Include = new[] { "study" } }));
        Check(study.Rules?.Rules.Rules.Any(rule => rule.NamePattern == "estudio*" && rule.MinSideMetres == 2.5) == true,
            "a requested study brings the CABA estudio minimums", Show(study));
        var typologyOnly = Safe(() => TypologyRules.Generate(SkillResolver.Resolve(catalog, new[] { new SkillRequest(Pack) }), request));
        Check(typologyOnly.Rules is { Succeeded: true } onlySet && onlySet.Rules.Rules.All(rule => rule.RoomId is not null) && onlySet.Rules.Rules.Count == 2
            && onlySet.Traces.All(trace => trace.Rule is null), "a resolution without code rules plans with the template rules only", Show(typologyOnly));

        // ---- stricter composed minimums change the plans visibly ----
        var strictCatalog = new SkillCatalog(catalog.Skills.Append(SkillParser.Parse(StricterPack)));
        var strict = SkillResolver.Resolve(strictCatalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(Stricter) });
        var fromStrict = Safe(() => TypologyRules.Generate(strict, request));
        var strictRule = fromStrict.Rules?.Rules.Rules.FirstOrDefault(rule => rule.Reference?.StartsWith(Stricter, StringComparison.Ordinal) == true);
        Check(strictRule is { NamePattern: "bedroom*", MinSideMetres: 3.5, MinAreaSquareMetres: null, Reference: Stricter + " bedroom-side-min (bedroom_side_min_m 3.5)" },
            "the stricter pack's parameter minimum reaches the planner, naming its rule and parameter", strictRule?.ToString() ?? Show(fromStrict));
        Check(fromStrict.Rules is { } strictSet && strictSet.Traces.Any(trace => trace.Rule?.ToString() == Stricter + ":rule:bedroom-side-min" && trace.Threshold == 3.5
                && trace.Property == TypologyRules.SideProperty && trace.ThresholdSource?.ToString() == Stricter + ":parameter:bedroom_side_min_m")
            && !strictSet.Rules.Rules.Any(rule => rule.Reference?.Contains("bedroom-side-wish", StringComparison.Ordinal) == true || rule.Reference?.Contains("sauna", StringComparison.Ordinal) == true),
            "the stricter rule is traced with its parameter source; warning rules and rules for rooms outside the programme are not planner rules");
        var baseBedrooms = p511.Alternatives?.Ranked.SelectMany(alternative => alternative.Plan.Rooms.Where(room => room.RoomId == "bedroom")).ToArray() ?? Array.Empty<PlannedRoom>();
        Check(baseBedrooms.Any(room => ShortSide(room.Rect) < 3.5 - Tolerance), "under the default rules some P5-11 bedroom is narrower than 3.5 m",
            string.Join(" ", baseBedrooms.Select(room => F(ShortSide(room.Rect)))));
        var strictBedrooms = fromStrict.Result.Alternatives?.Ranked.SelectMany(alternative => alternative.Plan.Rooms.Where(room => room.RoomId == "bedroom")).ToArray() ?? Array.Empty<PlannedRoom>();
        Check(fromStrict.Result.Status == PrototypeStatus.Ranked && strictBedrooms.Length > 0
            && strictBedrooms.All(room => ShortSide(room.Rect) >= 3.5 - Tolerance && room.Rect.Area >= 12.25 - Tolerance),
            "with the stricter pack every ranked bedroom is at least 3.5 m wide and 12.25 m2", Show(fromStrict) + " | " + string.Join(" ", strictBedrooms.Select(room => F(ShortSide(room.Rect)) + "/" + F(room.Rect.Area))));
        Check(Digest(fromStrict.Result) != Digest(fromCaba.Result) && Digest(fromStrict.Result) == Digest(Safe(() => TypologyRules.Generate(strict, request)).Result),
            "the stricter composition gives different, deterministic prototypes");
        string german;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            german = Digest(Safe(() => TypologyRules.Generate(strict, request)).Result);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(german == Digest(fromStrict.Result), "composed rule planning does not depend on the culture");

        // ---- stricter composed area minimums above the programme are the P5-07 typed conflict ----
        var strictArea = strictCatalog.Skills.First(skill => skill.Id == Stricter);
        var areaPack = strictArea with
        {
            Rules = new[] { new SkillRule("bedroom-area-min", SkillSeverity.Error, new SkillTarget(null, "bedroom*"), new SkillCheck(SkillCheckOp.Min, "area_m2", SkillValue.Of(10)), "m") },
        };
        var areaConflict = Safe(() => TypologyRules.Generate(Resolve(catalog, areaPack), request));
        Check(IsRefused(areaConflict.Result, SkillTypologyCodes.MinimumConflict, "bedroom") && areaConflict.Rules is null,
            "a composed area minimum above the programme minimum is refused as typology_minimum_conflict", Show(areaConflict));

        // ---- typed refusals of rules the planner cannot apply ----
        var questionPack = strictArea with
        {
            Rules = new[] { new SkillRule("bed-side", SkillSeverity.Error, new SkillTarget(null, "bed?oom*"), new SkillCheck(SkillCheckOp.Min, "min_side_m", SkillValue.Of(3)), "m") },
        };
        var question = Safe(() => TypologyRules.Generate(Resolve(catalog, questionPack), request));
        Check(IsRefused(question.Result, TypologyRuleCodes.PatternUnsupported, Stricter + "/bed-side") && question.Rules is { Succeeded: false },
            "a rule pattern with '?' (literal for skills, a wildcard for the planner) is a typed refusal", Show(question));
        var zeroPack = strictArea with
        {
            Rules = new[]
            {
                new SkillRule("bedroom-side-zero", SkillSeverity.Error, new SkillTarget(null, "bedroom*"), new SkillCheck(SkillCheckOp.Min, "min_side_m", SkillValue.Of(0)), "m"),
                new SkillRule("bedroom-side-text", SkillSeverity.Error, new SkillTarget(null, "bedroom*"), new SkillCheck(SkillCheckOp.Min, "min_side_m", SkillValue.Of("wide")), "m"),
            },
        };
        var zero = Safe(() => TypologyRules.Generate(Resolve(catalog, zeroPack), request));
        Check(IsRefused(zero.Result, TypologyRuleCodes.ThresholdInvalid, Stricter + "/bedroom-side-zero") && IsRefused(zero.Result, TypologyRuleCodes.ThresholdInvalid, Stricter + "/bedroom-side-text"),
            "non-positive and non-numeric minimum thresholds are typed refusals, each listed", Show(zero));
        var refusedProgramme = Safe(() => TypologyRules.Generate(composed, request with { Include = new[] { "sauna" } }));
        Check(IsRefused(refusedProgramme.Result, SkillTypologyCodes.RoomKindUnknown, "sauna") && refusedProgramme.Rules is null, "programme refusals pass through unchanged", Show(refusedProgramme));
        var badLot = Safe(() => TypologyRules.Generate(composed, new TypologyBriefRequest(4, new PrototypeLot(2, 2), 6)));
        Check(IsRefused(badLot.Result, PrototypeCodes.LotInvalid, "lot"), "request refusals pass through unchanged", Show(badLot));
    }

    static SkillResolution Resolve(SkillCatalog catalog, SkillManifest extra) =>
        SkillResolver.Resolve(new SkillCatalog(catalog.Skills.Append(extra)), new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(extra.Id) });

    static TypologyRulesResult Safe(Func<TypologyRulesResult> run)
    {
        try { return run(); }
        catch (Exception error)
        {
            var programme = new TypologyProgramme("", 0, Array.Empty<TypologyRoom>(), Array.Empty<BriefAdjacency>(), null, new[] { new SkillIssue("exception", error.GetType().Name, error.Message) });
            return new TypologyRulesResult(new TypologyBriefResult(PrototypeStatus.Refused, new TypologyBriefRequest(0, new PrototypeLot(0, 0)), programme, null,
                new[] { new PlanConstraint("exception", error.GetType().Name, true, false, error.Message) }, Array.Empty<PrototypeCandidate>(), null), null);
        }
    }

    static bool IsRefused(TypologyBriefResult result, string code, string subject) =>
        result.Status == PrototypeStatus.Refused && result.Brief is null && result.Alternatives is null && result.Candidates.Count == 0
        && result.Refusals.Any(refusal => refusal.Code == code && refusal.Subject == subject && refusal.Hard && !refusal.Satisfied);

    static double ShortSide(PlanRect rect) => Math.Min(rect.Width, rect.Depth);

    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    static string Show(TypologyRulesResult result) => result.Result.Status + " " + string.Join("; ", result.Result.Refusals.Select(r => r.Code + "@" + r.Subject + " " + r.Detail).Take(6))
        + (result.Result.Alternatives is { } set ? " ranked " + set.Ranked.Count.ToString(CultureInfo.InvariantCulture) + " " + string.Join("; ", set.Alternatives.SelectMany(a => a.Reasons).Distinct().Take(4)) : "")
        + (result.Rules is { } rules ? " rules " + string.Join(" | ", rules.Rules.Rules.Select(rule => rule.Reference)) : "");

    // Same digest as TypologyBriefTests.Digest.
    static string Digest(TypologyBriefResult result)
    {
        var text = (result.Brief is null ? "" : BriefSerializer.Serialize(result.Brief)) + "\n"
            + (result.Alternatives is null ? "" : AlternativeSetSerializer.Serialize(result.Alternatives)) + "\n"
            + string.Join(";", result.Candidates.Select(c => c.Index.ToString(CultureInfo.InvariantCulture) + c.Strategy + c.Seed.ToString(CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
