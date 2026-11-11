#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Generative;
using Bim.Gltf;

namespace Bim.Cli;

// P7-11 `plan-layouts brief.json output.json [--alternatives n] [--seed s] [--pack id]`: a P6-03 project brief (BriefParser, strict)
// is planned into n space plans (SpacePlanner, seeds s + i for i = 0..n-1, 32-bit wrap-around) that AlternativeRanker
// scores and ranks; the canonical AlternativeSetSerializer JSON is written to a NEW file (UTF-8, no BOM, exactly the
// serializer's string). This is AlternativeGenerator.Generate(brief, new AlternativeOptions(n, s)) with the default
// planner options (0.9 m door, the site as footprint, no planner rules) and the default ScoringWeights. Nothing is
// persisted to a project store. With --pack (P7-13) the planner options carry the pack's rules (below) instead of none.
//
// Options, each at most once, after the two paths, in any order:
//   --alternatives n  integer AlternativeGenerator.MinCount..MaxCount (1..64), default DefaultAlternatives (4)
//   --seed s          int32 integer, default 0
//   --pack id         a skill pack id (SkillParser.IsId, else plan_layouts_pack_id_invalid with the value as detail)
// Values are plain decimal integer literals (optional leading '-', no '+', fraction, exponent or whitespace).
//
// Input: read through BoundedInput (8 MiB cap, input_* codes), then BriefParser.Parse (its own 262144-character cap).
// The brief must have constraints.site (the planner's footprint) and no BriefValidator error.
//
// Pack (P7-13, only with --pack): the manifest is found exactly as generate-prototypes finds its pack (SkillPackLookup:
// <repository>/skills/<id>/skill.json, the repository being the first ancestor of the CLI binary holding global.json;
// BoundedInput 8 MiB read, SkillParser 65536-character cap, the manifest's id must be the requested one). Its rules
// become SpacePlanner rules the way TypologyRules.From (P5-13) maps one pack, without the typology template rules:
// 1. Candidates are the pack's rules with severity error, op min, property area_m2 or min_side_m and a room target;
//    the threshold is the literal value or the default of the named parameter. A pack with no candidate is
//    plan_layouts_pack_rules_none (detail: the pack id). A candidate whose threshold is not a positive finite number is
//    plan_layouts_pack_rule_invalid (detail "typology_rule_threshold_invalid at <pack>/<rule>", the first such rule in
//    pack order; checked on every candidate, matching a brief room or not). A room glob holding '?' would be
//    "typology_rule_pattern_unsupported at <pack>/<rule>" like TypologyRules, but SkillParser already refuses '?' in
//    room globs, so that branch is a defensive check unreachable from a skill.json.
// 2. Candidates whose glob matches no brief room name (SkillTarget.MatchesRoom) are dropped; the rest are grouped by
//    glob in pack order, each group one PlannerRule(NamePattern: glob, the largest area and side minimums of the
//    group, Reference "<pack> <rule id> <rule id>", a parameter threshold adding " (<parameter> <value>)" after its
//    rule id, the value rounded to 6 decimals, shortest round-trip, invariant). The planner cites
//    "rules[<index>] <Reference>" in the area_min / side_min constraint details of the written plans (the trace).
// A pack whose candidates match no brief room plans with no rules (today's bytes) and reports pack_rules 0. Minimums
// above a brief room's maximum are the planner's area_range_conflict (an infeasible set), not a refusal.
// Without --pack nothing is read from the repository and the file and stdout are byte-identical to P7-11.
//
// Success: the file is written and stdout is {"status":"succeeded","kind":"plan-layouts","scope":"proposal_only",
// "persisted":false,"set_status":"ranked"|"infeasible","requested":n,"seed":s,"ranked":k,"alternatives":m,"bytes":b,
// "sha256":lower-case hex of the file[,"pack":id,"pack_rules":planner rule count (only with --pack)]}; exit 0. An infeasible set (no plan feasible) is a result, not a failure: it is
// written with the planner's reasons.
// Failures print one {"status":"failed","code":...[,"detail"]} line, exit 1 and leave no output file:
//   plan_layouts_usage (detail: the usage text), plan_layouts_option_unknown, plan_layouts_option_duplicate,
//   plan_layouts_option_value_missing (detail: the option), plan_layouts_alternatives_invalid,
//   plan_layouts_seed_invalid (detail: the offending value), plan_layouts_pack_id_invalid (detail: the value; a repeated
//   --pack is plan_layouts_option_duplicate, a missing value plan_layouts_option_value_missing), output_* (GltfExportCommand.OutputProblem with .json; output_exists
//   and output_io_error at the write), input_* (BoundedInput), plan_layouts_brief_invalid (detail: "<BriefCodes code> at
//   <field>" from BriefParser or the first BriefValidator error), plan_layouts_site_required,
//   plan_layouts_pack_repository_not_found, plan_layouts_pack_unavailable (detail: the manifest's input_* code, e.g.
//   input_not_found for an unknown pack), plan_layouts_pack_invalid (detail: the SkillCodes code, or "id"),
//   plan_layouts_pack_rules_none, plan_layouts_pack_rule_invalid,
//   plan_layouts_refused (with "refusals":[{"code","subject","detail"}], the set's refusals; unreachable with the bounds
//   above), plan_layouts_internal_error (stderr gets the exception type and message).
// Precedence: usage, options, output path, input read, brief parse, site, brief validation, pack (repository, read,
// parse, id, rules), planning, write.
// The file is created exclusively (GltfExporter.WriteNew: FileMode.CreateNew, removed again if the write fails).
internal static class PlanLayoutsCommand
{
    public const string Usage = "usage: plan-layouts brief.json output.json [--alternatives n] [--seed s] [--pack id] (n in 1..64, default 4; s an int32, default 0; "
        + "seeds s+i; the brief needs constraints.site; --pack plans with the error-severity area_m2/min_side_m room minimums of <repository>/skills/<id>/skill.json)";
    public const string UsageCode = "plan_layouts_usage";
    public const string OptionUnknown = "plan_layouts_option_unknown";
    public const string OptionDuplicate = "plan_layouts_option_duplicate";
    public const string OptionValueMissing = "plan_layouts_option_value_missing";
    public const string AlternativesInvalid = "plan_layouts_alternatives_invalid";
    public const string SeedInvalid = "plan_layouts_seed_invalid";
    public const string BriefInvalid = "plan_layouts_brief_invalid";
    public const string SiteRequired = "plan_layouts_site_required";
    public const string Refused = "plan_layouts_refused";
    public const string InternalError = "plan_layouts_internal_error";
    public const string PackIdInvalid = "plan_layouts_pack_id_invalid";
    public const string PackRepositoryNotFound = "plan_layouts_pack_repository_not_found";
    public const string PackUnavailable = "plan_layouts_pack_unavailable";
    public const string PackInvalid = "plan_layouts_pack_invalid";
    public const string PackRulesNone = "plan_layouts_pack_rules_none";
    public const string PackRuleInvalid = "plan_layouts_pack_rule_invalid";
    public const int DefaultAlternatives = 4;

    static readonly Regex IntegerLiteral = new(@"\A-?[0-9]{1,11}\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
    }

    static void Execute(string[] args)
    {
        if (args.Length < 3 || args[1].Length == 0 || args[2].Length == 0 || args[1].StartsWith("--", StringComparison.Ordinal) || args[2].StartsWith("--", StringComparison.Ordinal))
            throw new Refusal(UsageCode, Usage);
        var (count, seed, packId) = ParseOptions(args);
        var output = args[2];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string badOutput) throw new Refusal(badOutput);
        string text;
        try { text = BoundedInput.ReadText(args[1]); }
        catch (InputRejectedException error) { throw new Refusal(error.Message); }

        ProjectBrief brief;
        try { brief = BriefParser.Parse(text); }
        catch (BriefException error) { throw new Refusal(BriefInvalid, error.Code + " at " + (error.Field ?? "brief")); }
        if (brief.Constraints.Site is null) throw new Refusal(SiteRequired, "constraints.site");
        if (BriefValidator.Validate(brief).FirstOrDefault(issue => issue.Severity == BriefSeverity.Error) is { } issue)
            throw new Refusal(BriefInvalid, issue.Code + " at " + issue.Field);

        PlannerRules? rules = null;
        if (packId is not null)
        {
            SkillManifest pack;
            try { pack = SkillPackLookup.Load(packId); }
            catch (SkillPackException error)
            {
                throw error.Problem switch
                {
                    SkillPackProblem.RepositoryNotFound => new Refusal(PackRepositoryNotFound),
                    SkillPackProblem.Unavailable => new Refusal(PackUnavailable, error.Detail),
                    _ => new Refusal(PackInvalid, error.Detail),
                };
            }
            rules = PackRules(pack, brief);
        }

        AlternativeSet set;
        string json;
        try
        {
            set = AlternativeGenerator.Generate(brief, rules is null ? new AlternativeOptions(count, seed) : new AlternativeOptions(count, seed, new PlannerOptions(Rules: rules)));
            json = set.Status == AlternativeSetStatus.Refused ? "" : AlternativeSetSerializer.Serialize(set);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("plan-layouts internal error: " + error.GetType().FullName + ": " + error.Message);
            throw new Refusal(InternalError);
        }
        if (set.Status == AlternativeSetStatus.Refused)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = Refused, refusals = set.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) }));
            Environment.ExitCode = 1;
            return;
        }
        var bytes = new UTF8Encoding(false).GetBytes(json);
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException error) { throw new Refusal(error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", error.GetType().Name); }
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var setStatus = set.Status == AlternativeSetStatus.Ranked ? "ranked" : "infeasible";
        if (packId is not null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "succeeded",
                kind = "plan-layouts",
                scope = "proposal_only",
                persisted = false,
                set_status = setStatus,
                requested = count,
                seed,
                ranked = set.Ranked.Count,
                alternatives = set.Alternatives.Count,
                bytes = bytes.LongLength,
                sha256,
                pack = packId,
                pack_rules = rules!.Rules.Count,
            }));
            return;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            kind = "plan-layouts",
            scope = "proposal_only",
            persisted = false,
            set_status = setStatus,
            requested = count,
            seed,
            ranked = set.Ranked.Count,
            alternatives = set.Alternatives.Count,
            bytes = bytes.LongLength,
            sha256,
        }));
    }

    // The pack's minimum room rules as planner rules (see the header; TypologyRules.From steps 1 and 2 for one pack).
    static PlannerRules PackRules(SkillManifest pack, ProjectBrief brief)
    {
        var names = brief.Rooms.Select(room => room.Name).ToArray();
        var candidates = 0;
        string? invalid = null;
        var groups = new List<(string Pattern, double? Area, double? Side, List<string> Parts)>();
        foreach (var rule in pack.Rules)
        {
            var check = rule.Check;
            if (rule.Severity != SkillSeverity.Error || check.Op != SkillCheckOp.Min || (check.Property != TypologyRules.AreaProperty && check.Property != TypologyRules.SideProperty)) continue;
            if (rule.Target.RoomPattern is not { } pattern) continue;
            candidates++;
            SkillValue? threshold = check.Value;
            string? parameter = null;
            if (check.Parameter is { } name)
            {
                parameter = name;
                threshold = pack.Parameters.FirstOrDefault(item => item.Name == name)?.Default;
            }
            // Defensive: SkillParser refuses '?' in room globs (TypologyRules refuses it for manifests built in code).
            if (pattern.Contains('?', StringComparison.Ordinal)) { invalid ??= TypologyRuleCodes.PatternUnsupported + " at " + pack.Id + "/" + rule.Id; continue; }
            if (threshold is not { Kind: SkillValueKind.Number } number || !(double.IsFinite(number.Number) && number.Number > 0))
            {
                invalid ??= TypologyRuleCodes.ThresholdInvalid + " at " + pack.Id + "/" + rule.Id;
                continue;
            }
            if (invalid is not null || !names.Any(rule.Target.MatchesRoom)) continue;
            var value = number.Number;
            var index = groups.FindIndex(group => group.Pattern == pattern);
            if (index < 0) { groups.Add((pattern, null, null, new List<string>())); index = groups.Count - 1; }
            var current = groups[index];
            if (check.Property == TypologyRules.AreaProperty) current.Area = Math.Max(current.Area ?? 0, value);
            else current.Side = Math.Max(current.Side ?? 0, value);
            current.Parts.Add(rule.Id + (parameter is null ? "" : " (" + parameter + " " + Format(value) + ")"));
            groups[index] = current;
        }
        if (candidates == 0) throw new Refusal(PackRulesNone, pack.Id);
        if (invalid is not null) throw new Refusal(PackRuleInvalid, invalid);
        return new PlannerRules(groups.Select(group => new PlannerRule(NamePattern: group.Pattern, MinAreaSquareMetres: group.Area, MinSideMetres: group.Side,
            Reference: pack.Id + " " + string.Join(" ", group.Parts))).ToArray());
    }

    // SpacePlanSerializer.Format (constraint detail numbers): rounded to 6 decimals, shortest round-trip, invariant.
    static string Format(double value)
    {
        var rounded = Math.Round(value, 6, MidpointRounding.ToEven);
        return rounded == 0 ? "0" : rounded.ToString("R", CultureInfo.InvariantCulture);
    }

    static (int Count, int Seed, string? Pack) ParseOptions(string[] args)
    {
        int? count = null, seed = null;
        string? pack = null;
        for (var index = 3; index < args.Length; index++)
        {
            var name = args[index];
            if (name is not ("--alternatives" or "--seed" or "--pack")) throw new Refusal(OptionUnknown, name);
            var repeated = name == "--alternatives" ? (count is not null) : name == "--seed" ? (seed is not null) : (pack is not null);
            if (repeated) throw new Refusal(OptionDuplicate, name);
            if (index + 1 >= args.Length) throw new Refusal(OptionValueMissing, name);
            var value = args[++index];
            if (name == "--pack")
            {
                if (!SkillParser.IsId(value)) throw new Refusal(PackIdInvalid, value);
                pack = value;
            }
            else if (name == "--alternatives")
            {
                if (!Integer(value, out var parsed) || parsed < AlternativeGenerator.MinCount || parsed > AlternativeGenerator.MaxCount) throw new Refusal(AlternativesInvalid, value);
                count = parsed;
            }
            else
            {
                if (!Integer(value, out var parsed)) throw new Refusal(SeedInvalid, value);
                seed = parsed;
            }
        }
        return (count ?? DefaultAlternatives, seed ?? 0, pack);
    }

    static bool Integer(string text, out int value)
    {
        value = 0;
        return IntegerLiteral.IsMatch(text) && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
