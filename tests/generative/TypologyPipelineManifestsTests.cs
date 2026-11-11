using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bim.Core;
using Bim.Generative;

// P5-15 composed rules from a manifest list oracle: TypologyPipeline.RunComposed takes the typology pack, the options (whose
// Pack is the facade gate's code pack) and optional extra skill manifests, resolves them itself (SkillResolver over a
// catalog of exactly those manifests, every one requested) and runs the P5-14 composed-rules path, so a caller without a
// SkillResolution gets it. On the CABA + typology case it is byte-equal to the resolution overload and keeps the P5-14
// pinned digest; an extra stricter pack equals the resolution overload on the same three packs; extras are order
// independent and a manifest passed twice by reference counts once. Resolution refusals (skill_conflict,
// dependency_missing, id_duplicate) pass through typed with their own code and subject, before the request and option
// refusals, with no typology_resolution_failed summary and no pack mismatch; composed rule refusals
// (typology_minimum_conflict) pass through as in the resolution overload. Review fixes: a typology pack of another kind is
// refused up front (typology_kind_mismatch on its id), an id passed as several versions gives a deterministic
// id_duplicate detail, a copy or re-parse of a manifest with equal contents counts once, a precedence order is forwarded
// to the resolver (default_conflict without it, resolved with it), and version_mismatch and parameter_unknown pass
// through typed. Unit-tested evidence only.
static class TypologyPipelineManifestsTests
{
    const string Pack = "bim.typology.vivienda-unifamiliar";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stricter = "xx.test.stricter-bedroom";
    // TypologyPipelineRulesTests.PinnedComposedSha256 (P5-14): CABA + typology, ComposedRules, family 3, 16 x 16 m, 4 alternatives, seed 0.
    const string PinnedComposedSha256 = "a8c62da59e0eee2d25af4d27bc37fcf2f67ae366e7e48446f1edfe3c87f7c0dd";
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-4000-8000-0000000007a7");

    // TypologyPipelineRulesTests.StricterPack: bedrooms at least 3.2 m on their short side.
    const string StricterPack = """
    {"manifest_version":1,"id":"xx.test.stricter-bedroom","version":"1.0.0","kind":"code","description":"test pack with a stricter bedroom side","priority":100,
     "parameters":[{"name":"bedroom_side_min_m","type":"number","default":3.2}],
     "rules":[
      {"id":"bedroom-side-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"min_side_m","parameter":"bedroom_side_min_m"},"message":"m"}
     ]}
    """;

    // A rule-free code pack, to check that extras compose in any order.
    const string EmptyPack = """
    {"manifest_version":1,"id":"xx.test.empty-code","version":"1.0.0","kind":"code","description":"test pack without rules","priority":10}
    """;

    // A code pack that declares a conflict with the CABA pack.
    const string ConflictPack = """
    {"manifest_version":1,"id":"xx.test.conflicting","version":"1.0.0","kind":"code","description":"test pack conflicting with CABA","priority":10,
     "conflicts_with":["ar.caba.codigo-edificacion"]}
    """;

    // A code pack that requires a skill that is not passed.
    const string NeedyPack = """
    {"manifest_version":1,"id":"xx.test.needy","version":"1.0.0","kind":"code","description":"test pack with a missing dependency","priority":10,
     "requires":[{"id":"xx.test.absent","range":">=1.0.0"}]}
    """;

    // A code pack at the typology pack's priority (20) with another value for one of its defaults.
    const string RivalPack = """
    {"manifest_version":1,"id":"xx.test.rival-defaults","version":"1.0.0","kind":"code","description":"test pack with a rival programme default","priority":20,
     "defaults":{"programme.adjacency.hall.toilet":"required"}}
    """;

    // A code pack that requires a CABA version that is not passed.
    const string NewerCabaPack = """
    {"manifest_version":1,"id":"xx.test.newer-caba","version":"1.0.0","kind":"code","description":"test pack requiring a newer CABA pack","priority":10,
     "requires":[{"id":"ar.caba.codigo-edificacion","range":">=0.2.0"}]}
    """;

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "typology-pipeline-manifests: " + name, detail);

        var catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills"));
        var pack = catalog.Find(Pack);
        var caba = catalog.Find(Caba);
        Check(pack is not null && caba is not null, "the typology and CABA packs load from skills/");
        if (pack is null || caba is null) return;
        var options = new PrototypePipelineOptions(caba, ProjectId);
        var composedRules = new TypologyPipelineOptions(ComposedRules: true);
        var request = new TypologyBriefRequest(4, new PrototypeLot(12, 12), 6);
        var request3 = new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4);
        var stricter = SkillParser.Parse(StricterPack);
        var empty = SkillParser.Parse(EmptyPack);
        string? error = null;

        error = null;
        // ---- CABA + typology: byte-equal to the resolution overload, P5-14 pin unchanged ----
        var resolution = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack) });
        var expected = Safe(() => TypologyPipeline.Run(resolution, request3, options, composedRules), ref error);
        var run = Safe(() => TypologyPipeline.RunComposed(pack, request3, options), ref error);
        var expectedJson = expected is null ? "" : TypologyPipelineSerializer.Serialize(expected);
        var json = run is null ? "" : TypologyPipelineSerializer.Serialize(run);
        Check(run is { ComposedRules: true, Rules.Succeeded: true } && expected is not null && json == expectedJson,
            "RunComposed(typology pack, request, options) is byte-equal to Run(CABA + typology resolution, ComposedRules)", run is null ? error ?? "" : Sha(json) + " vs " + Sha(expectedJson));
        Check(Sha(json) == PinnedComposedSha256 && Sha(expectedJson) == PinnedComposedSha256, "the CABA + typology composed run keeps the P5-14 pinned digest", "actual " + Sha(json));
        var withEmptyList = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, Array.Empty<SkillManifest>()), ref error);
        var withNull = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, null), ref error);
        var withSame = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { caba, pack }), ref error);
        Check(withEmptyList is not null && withNull is not null && withSame is not null && TypologyPipelineSerializer.Serialize(withEmptyList) == json
            && TypologyPipelineSerializer.Serialize(withNull) == json && TypologyPipelineSerializer.Serialize(withSame) == json,
            "no extras, an empty list, and the typology and code packs repeated by reference give the same bytes", error ?? "");
        var german = (string?)null;
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var local = Safe(() => TypologyPipeline.RunComposed(pack, request3, options), ref error);
            german = local is null ? null : TypologyPipelineSerializer.Serialize(local);
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Check(german == json, "the manifest-list run is culture independent", error ?? "");

        error = null;
        // ---- extras compose like the resolution overload, in any order ----
        var strictResolution = SkillResolver.Resolve(new SkillCatalog(catalog.Skills.Append(stricter)), new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(Stricter) });
        var strictExpected = Safe(() => TypologyPipeline.Run(strictResolution, request, options, composedRules), ref error);
        var strictRun = Safe(() => TypologyPipeline.RunComposed(pack, request, options, new[] { stricter }), ref error);
        Check(strictExpected is not null && strictRun is { ComposedRules: true } && TypologyPipelineSerializer.Serialize(strictRun) == TypologyPipelineSerializer.Serialize(strictExpected)
            && strictRun.Rules is { } strictRules && strictRules.Traces.Any(trace => trace.Rule?.ToString() == Stricter + ":rule:bedroom-side-min"),
            "an extra stricter pack is byte-equal to the resolution overload on CABA + typology + stricter, traced to its rule", strictRun is null ? error ?? "" : Show(strictRun, null));
        var ordered = Safe(() => TypologyPipeline.RunComposed(pack, request, options, new[] { stricter, empty }), ref error);
        var reversed = Safe(() => TypologyPipeline.RunComposed(pack, request, options, new[] { empty, stricter, empty }), ref error);
        Check(strictRun is not null && ordered is not null && reversed is not null && TypologyPipelineSerializer.Serialize(ordered) == TypologyPipelineSerializer.Serialize(reversed)
            && TypologyPipelineSerializer.Serialize(ordered) == TypologyPipelineSerializer.Serialize(strictRun!),
            "extras compose in any order and a rule-free extra changes nothing", error ?? "");

        error = null;
        // ---- resolution refusals pass through typed, then the request and option refusals ----
        var conflict = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { SkillParser.Parse(ConflictPack) }), ref error);
        Check(conflict is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, ComposedRules: true, Rules: null, Typology.Brief: null, Typology.Status: PrototypeStatus.Refused }
            && conflict.Refusals.Count == 1 && conflict.Refusals[0] is { Code: SkillCodes.SkillConflict, Subject: "xx.test.conflicting", Hard: true, Satisfied: false }
            && conflict.Refusals[0].Detail.Contains(Caba, StringComparison.Ordinal),
            "a declared skill conflict is one typed skill_conflict refusal (no typology_resolution_failed summary, no pack mismatch), no alternatives", Show(conflict, error));
        var conflictJson = conflict is null ? "" : TypologyPipelineSerializer.Serialize(conflict);
        Check(conflictJson.Contains("\"skill_conflict\"", StringComparison.Ordinal) && conflictJson.Contains("\"rules\":null", StringComparison.Ordinal)
            && !conflictJson.Contains(SkillTypologyCodes.ResolutionFailed, StringComparison.Ordinal),
            "the refused manifest-list run serializes the typed refusal and a null rules member", conflictJson.Length > 400 ? conflictJson[..400] : conflictJson);
        var needy = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { SkillParser.Parse(NeedyPack) }), ref error);
        Check(needy is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 } && needy.Refusals.Count == 1
            && needy.Refusals[0] is { Code: SkillCodes.DependencyMissing, Subject: "xx.test.absent", Hard: true, Satisfied: false },
            "a missing dependency of an extra is a typed dependency_missing refusal on the missing id", Show(needy, error));
        var twin = caba with { Version = new SkillVersion(0, 2, 0) };
        var duplicate = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { twin }), ref error);
        Check(duplicate is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, ComposedRules: true, Rules: null } && duplicate.Refusals.Count == 1
            && duplicate.Refusals[0] is { Code: SkillCodes.IdDuplicate, Subject: Caba, Hard: true, Satisfied: false } && duplicate.Refusals[0].Detail.Contains("0.2.0", StringComparison.Ordinal),
            "a second manifest with the code pack's id is a typed id_duplicate refusal, not an exception", Show(duplicate, error));
        var both = Safe(() => TypologyPipeline.RunComposed(pack, request3 with { Alternatives = 0 }, options with { ProjectId = Guid.Empty }, new[] { SkillParser.Parse(ConflictPack) }), ref error);
        Check(both is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 }
            && both.Refusals.Select(r => r.Code).SequenceEqual(new[] { SkillCodes.SkillConflict, PrototypeCodes.CountInvalid, PrototypePipelineCodes.ProjectInvalid }),
            "the resolution refusals come first, then the request refusals, then the option refusals", Show(both, error));

        error = null;
        // ---- more resolution refusals: version_mismatch, parameter_unknown, default_conflict and precedence ----
        var newer = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { SkillParser.Parse(NewerCabaPack) }), ref error);
        Check(newer is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 } && newer.Refusals.Count == 1
            && newer.Refusals[0] is { Code: SkillCodes.VersionMismatch, Subject: Caba, Hard: true, Satisfied: false } && newer.Refusals[0].Detail.Contains(">=0.2.0", StringComparison.Ordinal),
            "an extra requiring another CABA version is a typed version_mismatch refusal on the CABA id", Show(newer, error));
        var unknownParameter = stricter with { Parameters = Array.Empty<SkillParameter>() };
        var unknown = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { unknownParameter }), ref error);
        Check(unknown is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 } && unknown.Refusals.Count == 1
            && unknown.Refusals[0] is { Code: SkillCodes.ParameterUnknown, Subject: Stricter + "/bedroom-side-min", Hard: true, Satisfied: false },
            "a code-built extra whose rule names a missing parameter is a typed parameter_unknown refusal", Show(unknown, error));
        var rival = SkillParser.Parse(RivalPack);
        var rivalRun = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { rival }), ref error);
        Check(rivalRun is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, Rules: null } && rivalRun.Refusals.Count == 1
            && rivalRun.Refusals[0] is { Code: SkillCodes.DefaultConflict, Subject: "programme.adjacency.hall.toilet", Hard: true, Satisfied: false },
            "an extra at the typology pack's priority with another value for its default is a typed default_conflict on that key", Show(rivalRun, error));
        var precedence = new[] { Pack, "xx.test.rival-defaults" };
        var ranked = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { rival }, precedence), ref error);
        var rankedExpected = Safe(() => TypologyPipeline.Run(SkillResolver.Resolve(new SkillCatalog(new[] { pack, caba, rival }),
            new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(rival.Id) }, precedence), request3, options, composedRules), ref error);
        Check(ranked is { ComposedRules: true } && ranked.Refusals.All(r => r.Code != SkillCodes.DefaultConflict) && rankedExpected is not null
            && TypologyPipelineSerializer.Serialize(ranked) == TypologyPipelineSerializer.Serialize(rankedExpected) && TypologyPipelineSerializer.Serialize(ranked) == json,
            "a precedence order is forwarded to the resolver: the typology default wins and the run is the CABA + typology bytes", Show(ranked, error));
        var badPrecedence = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, null, new[] { "xx.test.absent" }), ref error);
        Check(badPrecedence is { Status: PrototypePipelineStatus.Refused } && badPrecedence.Refusals.Count == 1 && badPrecedence.Refusals[0] is { Code: SkillCodes.PrecedenceUnknown, Subject: "xx.test.absent" },
            "a precedence entry that is not a passed skill is a typed precedence_unknown refusal", Show(badPrecedence, error));

        error = null;
        // ---- the typology pack must be a typology pack ----
        var cabaFirst = Safe(() => TypologyPipeline.RunComposed(caba, request3, options), ref error);
        var cabaWithTypology = Safe(() => TypologyPipeline.RunComposed(caba, request3, options, new[] { pack }), ref error);
        foreach (var (label, result) in new[] { ("alone", cabaFirst), ("with the typology pack as an extra", cabaWithTypology) })
            Check(result is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0, ComposedRules: true, Rules: null, Typology.Brief: null } && result.Refusals.Count == 1
                && result.Refusals[0] is { Code: SkillTypologyCodes.KindMismatch, Subject: Caba, Hard: true, Satisfied: false },
                "a code pack given as the typology pack (" + label + ") is refused up front as typology_kind_mismatch on its id", Show(result, error));
        var kindBoth = Safe(() => TypologyPipeline.RunComposed(caba, request3 with { Alternatives = 0 }, options with { ProjectId = Guid.Empty }), ref error);
        Check(kindBoth is { Status: PrototypePipelineStatus.Refused }
            && kindBoth.Refusals.Select(r => r.Code).SequenceEqual(new[] { SkillTypologyCodes.KindMismatch, PrototypeCodes.CountInvalid, PrototypePipelineCodes.ProjectInvalid }),
            "the kind refusal comes first, then the request and option refusals", Show(kindBoth, error));

        error = null;
        // ---- duplicate ids: deterministic detail, equal contents count once ----
        var stricter2 = stricter with { Version = new SkillVersion(2, 0, 0) };
        var up = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { stricter, stricter2 }), ref error);
        var down = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { stricter2, stricter }), ref error);
        Check(up is { Status: PrototypePipelineStatus.Refused } && down is not null && up.Refusals.Count == 1 && up.Refusals[0] is { Code: SkillCodes.IdDuplicate, Subject: Stricter }
            && up.Refusals[0].Detail.Contains("1.0.0, 2.0.0", StringComparison.Ordinal) && TypologyPipelineSerializer.Serialize(up) == TypologyPipelineSerializer.Serialize(down),
            "two versions of one extra give one id_duplicate whose detail and JSON do not depend on argument order", Show(up, error) + " / " + Show(down, error));
        var copy = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { caba with { } }), ref error);
        var reparsed = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", Caba, "skill.json")));
        var parsed = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { reparsed, pack with { } }), ref error);
        Check(copy is not null && parsed is not null && !ReferenceEquals(reparsed, caba) && TypologyPipelineSerializer.Serialize(copy) == json && TypologyPipelineSerializer.Serialize(parsed) == json,
            "a with-copy or a re-parsed manifest with equal contents counts once and gives the CABA + typology bytes", Show(copy, error) + " / " + Show(parsed, error));
        var edited = caba with { Description = caba.Description + " (edited)" };
        var changed = Safe(() => TypologyPipeline.RunComposed(pack, request3, options, new[] { edited }), ref error);
        Check(changed is { Status: PrototypePipelineStatus.Refused } && changed.Refusals.Count == 1 && changed.Refusals[0] is { Code: SkillCodes.IdDuplicate, Subject: Caba }
            && changed.Refusals[0].Detail.Contains("different contents", StringComparison.Ordinal),
            "same id and version with other contents is id_duplicate, said to have different contents", Show(changed, error));

        error = null;
        // ---- composed rule refusals pass through as in the resolution overload ----
        var tooWide = stricter with { Parameters = new[] { new SkillParameter("bedroom_side_min_m", SkillParameterType.Number, SkillValue.Of(4.1)) } };
        var wideExpected = Safe(() => TypologyPipeline.Run(SkillResolver.Resolve(new SkillCatalog(catalog.Skills.Append(tooWide)),
            new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(Stricter) }), request, options, composedRules), ref error);
        var wide = Safe(() => TypologyPipeline.RunComposed(pack, request, options, new[] { tooWide }), ref error);
        Check(wide is { Status: PrototypePipelineStatus.Refused, Alternatives.Count: 0 } && wideExpected is not null
            && wide.Refusals.Count == 1 && wide.Refusals[0] is { Code: SkillTypologyCodes.MinimumConflict, Subject: "bedroom" }
            && TypologyPipelineSerializer.Serialize(wide) == TypologyPipelineSerializer.Serialize(wideExpected),
            "a composed typology_minimum_conflict passes through typed, byte-equal to the resolution overload", Show(wide, error));

        error = null;
        // ---- programming errors ----
        Check(Throws(() => TypologyPipeline.RunComposed(null!, request, options)) && Throws(() => TypologyPipeline.RunComposed(pack, null!, options))
            && Throws(() => TypologyPipeline.RunComposed(pack, request, null!)) && Throws(() => TypologyPipeline.RunComposed(pack, request, options with { Pack = null! }))
            && Throws(() => TypologyPipeline.RunComposed(pack, request, options, new SkillManifest[] { null! })),
            "null arguments (and a null extra manifest) are programming errors (ArgumentNullException)");
    }

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

    static string Show(TypologyPipelineResult? result, string? error) => result is null ? "error " + error
        : result.Status + " [" + string.Join("; ", result.Refusals.Select(r => r.Code + "@" + r.Subject + " " + r.Detail)) + "]";

    static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
