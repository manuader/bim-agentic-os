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

// P7-11 `plan-layouts brief.json output.json [--alternatives n] [--seed s]`: a P6-03 project brief (BriefParser, strict)
// is planned into n space plans (SpacePlanner, seeds s + i for i = 0..n-1, 32-bit wrap-around) that AlternativeRanker
// scores and ranks; the canonical AlternativeSetSerializer JSON is written to a NEW file (UTF-8, no BOM, exactly the
// serializer's string). This is AlternativeGenerator.Generate(brief, new AlternativeOptions(n, s)) with the default
// planner options (0.9 m door, the site as footprint, no planner rules) and the default ScoringWeights. Nothing is
// persisted to a project store.
//
// Options, each at most once, after the two paths, in any order:
//   --alternatives n  integer AlternativeGenerator.MinCount..MaxCount (1..64), default DefaultAlternatives (4)
//   --seed s          int32 integer, default 0
// Values are plain decimal integer literals (optional leading '-', no '+', fraction, exponent or whitespace).
//
// Input: read through BoundedInput (8 MiB cap, input_* codes), then BriefParser.Parse (its own 262144-character cap).
// The brief must have constraints.site (the planner's footprint) and no BriefValidator error.
//
// Success: the file is written and stdout is {"status":"succeeded","kind":"plan-layouts","scope":"proposal_only",
// "persisted":false,"set_status":"ranked"|"infeasible","requested":n,"seed":s,"ranked":k,"alternatives":m,"bytes":b,
// "sha256":lower-case hex of the file}; exit 0. An infeasible set (no plan feasible) is a result, not a failure: it is
// written with the planner's reasons.
// Failures print one {"status":"failed","code":...[,"detail"]} line, exit 1 and leave no output file:
//   plan_layouts_usage (detail: the usage text), plan_layouts_option_unknown, plan_layouts_option_duplicate,
//   plan_layouts_option_value_missing (detail: the option), plan_layouts_alternatives_invalid,
//   plan_layouts_seed_invalid (detail: the offending value), output_* (GltfExportCommand.OutputProblem with .json; output_exists
//   and output_io_error at the write), input_* (BoundedInput), plan_layouts_brief_invalid (detail: "<BriefCodes code> at
//   <field>" from BriefParser or the first BriefValidator error), plan_layouts_site_required,
//   plan_layouts_refused (with "refusals":[{"code","subject","detail"}], the set's refusals; unreachable with the bounds
//   above), plan_layouts_internal_error (stderr gets the exception type and message).
// Precedence: usage, options, output path, input read, brief parse, site, brief validation, planning, write.
// The file is created exclusively (GltfExporter.WriteNew: FileMode.CreateNew, removed again if the write fails).
internal static class PlanLayoutsCommand
{
    public const string Usage = "usage: plan-layouts brief.json output.json [--alternatives n] [--seed s] (n in 1..64, default 4; s an int32, default 0; "
        + "seeds s+i; the brief needs constraints.site)";
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
        var (count, seed) = ParseOptions(args);
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

        AlternativeSet set;
        string json;
        try
        {
            set = AlternativeGenerator.Generate(brief, new AlternativeOptions(count, seed));
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
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            kind = "plan-layouts",
            scope = "proposal_only",
            persisted = false,
            set_status = set.Status == AlternativeSetStatus.Ranked ? "ranked" : "infeasible",
            requested = count,
            seed,
            ranked = set.Ranked.Count,
            alternatives = set.Alternatives.Count,
            bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        }));
    }

    static (int Count, int Seed) ParseOptions(string[] args)
    {
        int? count = null, seed = null;
        for (var index = 3; index < args.Length; index++)
        {
            var name = args[index];
            if (name is not ("--alternatives" or "--seed")) throw new Refusal(OptionUnknown, name);
            if ((name == "--alternatives" ? count : seed) is not null) throw new Refusal(OptionDuplicate, name);
            if (index + 1 >= args.Length) throw new Refusal(OptionValueMissing, name);
            var value = args[++index];
            if (name == "--alternatives")
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
        return (count ?? DefaultAlternatives, seed ?? 0);
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
