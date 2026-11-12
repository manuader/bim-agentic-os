#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;
using Bim.Gltf;

namespace Bim.Cli;

// P7-14 `generate-typology request.json output.json`: TypologyPipeline.Run (core/Bim.Generative, P5-12) on one JSON
// request, its canonical TypologyPipelineSerializer JSON written to a NEW file (UTF-8, no BOM, exactly the serializer's
// string). Nothing is committed to a project store; the BIM-IR prototypes are a proposal (only described in the file).
//
// Request (one JSON object, snake_case, unknown or duplicate members refused; read through BoundedInput, 8 MiB cap):
//   pack           required string, the typology skill id (for example bim.typology.vivienda-unifamiliar)
//   family_size    required integer          lot  required {"width_m": number, "depth_m": number}
//   alternatives   required integer          seed required integer
//   project_id     required GUID string ("D" form)
//   weights        optional {"area_fit","adjacency","proportions","compactness","perimeter","compliance"}: numbers, each
//                  one absent taking its ScoringWeights default ({}, explicit defaults and an absent member all mean the
//                  default weights, passed to the pipeline as null so the bytes are the same)
//   skills         optional array of at most 64 skill id strings (P5-16): extra packs, each read like the typology pack
//   precedence     optional array of at most 64 strings (P5-16): the resolver's precedence order (earlier wins among
//                  equal priorities), passed through as given; an entry that is not a composed skill is the resolver's
//                  typed precedence_unknown refusal
// Every number must be a finite JSON number (-0 is read as 0); integers must be JSON integer literals in the int32 range;
// strings holding an unpaired UTF-16 surrogate escape are refused like any malformed member. Value ranges (family size,
// lot sides, count, weights, an all-zero project id, a non-typology pack) are left to TypologyPipeline, whose refusals
// are reported. Not in v1 (refused as unknown members): include (optional rooms), the code pack, revision, circulation /
// facade weights and snap; the pipeline gets PrototypePipelineOptions(code pack, project_id)
// with every other option at its default, so the output is TypologyPipeline.Run(pack, request, options) of the P5-12
// in-process API.
// P5-16 composed rules: when skills or precedence is present (even as []), the pipeline is instead
// TypologyPipeline.RunComposed(pack, request, options, extra manifests in request order, precedence or null) of the P5-15
// in-process API: the typology pack, the code pack and the extras are resolved (SkillResolver) and the composed pack rules
// plan the candidates, so "skills":[] gives the CABA + typology composed bytes (pin a8c62da5...). Its resolution refusals
// (skill_conflict, dependency_missing, precedence_unknown, default_conflict, ...) are generate_typology_refused with the
// typed issues exactly as RunComposed returns them. With both members absent the output and stdout are the P7-14 ones.
// Packs: the typology pack and the fixed code pack ar.caba.codigo-edificacion (the facade gate's) are each read with
// SkillPackLookup.Load(id, packageFirst: true), as generate-prototypes reads its pack: <package>/skills/<id>/skill.json
// when the CLI sits in the release package layout, else <repository>/skills/<id>/skill.json.
//
// Success: the file is written and stdout is {"status":"succeeded","scope":"proposal_only","persisted":false,
// "pipeline_status":"ranked"|"infeasible","pack","code_pack","project_id","alternatives":n,"accepted":k,"bytes":n,
// "sha256":lower-case hex of the file}; exit 0. An infeasible result is a result: it is written with its rejections.
// Failures print one {"status":"failed","code":...[,"detail"]} line, exit 1 and leave no output file:
//   generate_typology_usage (detail: the usage text), output_* (GltfExportCommand.OutputProblem with .json, checked
//   before the request is read; output_exists and output_io_error at the write), input_* (BoundedInput, the request),
//   generate_typology_request_invalid (detail: the offending member), generate_typology_repository_not_found,
//   generate_typology_pack_unavailable / generate_typology_code_pack_unavailable (detail: the manifest's input_* code),
//   generate_typology_pack_invalid / generate_typology_code_pack_invalid (detail: the skill code, or "id" when the
//   manifest names another pack), generate_typology_skill_unavailable / generate_typology_skill_invalid (an extra skill;
//   detail as for the packs, plus "skill": the extra id), generate_typology_refused (with "refusals":[{"code","subject","detail"}] =
//   TypologyPipelineResult.Refusals in order, no detail), generate_typology_internal_error (stderr: exception type, message).
// Composed-mode stdout adds "rules":"composed" and "skills":[the extra ids as given].
// Precedence: usage, output path, request read, request parse, repository, typology pack, code pack, extra skills (in
// request order), pipeline, write.
// The file is created exclusively (GltfExporter.WriteNew: FileMode.CreateNew, removed again if the write fails).
// Everything is culture-invariant: the same request gives the same bytes and stdout under any current culture.
internal static class GenerateTypologyCommand
{
    public const string CodePack = "ar.caba.codigo-edificacion";
    public const string Usage = "usage: generate-typology request.json output.json (request: {\"pack\": typology skill id, \"family_size\": int, "
        + "\"lot\": {\"width_m\", \"depth_m\"}, \"alternatives\": int, \"seed\": int, \"project_id\": GUID, optional \"weights\", optional \"skills\": [extra skill ids] "
        + "and \"precedence\": [skill ids], either of which runs the composed pack rules}; the facade code pack is "
        + CodePack + "; packs are read from <package>/skills/<id>/skill.json in the release package layout, else from <repository>/skills/<id>/skill.json, "
        + "else generate_typology_repository_not_found; the output must be a new .json file)";
    public const string UsageCode = "generate_typology_usage";
    public const string RequestInvalid = "generate_typology_request_invalid";
    public const string RepositoryNotFound = "generate_typology_repository_not_found";
    public const string PackUnavailable = "generate_typology_pack_unavailable";
    public const string PackInvalid = "generate_typology_pack_invalid";
    public const string CodePackUnavailable = "generate_typology_code_pack_unavailable";
    public const string CodePackInvalid = "generate_typology_code_pack_invalid";
    public const string SkillUnavailable = "generate_typology_skill_unavailable";
    public const string SkillInvalid = "generate_typology_skill_invalid";
    public const string Refused = "generate_typology_refused";
    public const int MaxListEntries = 64;
    public const string InternalError = "generate_typology_internal_error";

    static readonly string[] RootMembers = { "pack", "family_size", "lot", "alternatives", "seed", "project_id", "weights", "skills", "precedence" };
    static readonly string[] LotMembers = { "width_m", "depth_m" };
    static readonly string[] WeightMembers = { "area_fit", "adjacency", "proportions", "compactness", "perimeter", "compliance" };

    sealed class Refusal(string code, string? detail = null, string? skill = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
        public string? Skill { get; } = skill;
    }

    // Skills and Precedence are null when absent; Composed when either is present.
    sealed record Parsed(string Pack, TypologyBriefRequest Request, Guid ProjectId, IReadOnlyList<string>? Skills, IReadOnlyList<string>? Precedence)
    {
        public bool Composed => Skills is not null || Precedence is not null;
    }

    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail, refusal.Skill); }
    }

    static void Execute(string[] args)
    {
        if (args.Length != 3 || args[1].Length == 0 || args[2].Length == 0) throw new Refusal(UsageCode, Usage);
        var output = args[2];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string badOutput) throw new Refusal(badOutput);
        string text;
        try { text = BoundedInput.ReadText(args[1]); }
        catch (InputRejectedException error) { throw new Refusal(error.Message); }
        var parsed = Parse(text);
        var pack = Load(parsed.Pack, PackUnavailable, PackInvalid);
        var code = Load(CodePack, CodePackUnavailable, CodePackInvalid);
        var extras = new List<SkillManifest>();
        foreach (var id in parsed.Skills ?? Array.Empty<string>()) extras.Add(Load(id, SkillUnavailable, SkillInvalid, id));

        var options = new PrototypePipelineOptions(code, parsed.ProjectId);
        TypologyPipelineResult result;
        string json;
        try
        {
            result = parsed.Composed
                ? TypologyPipeline.RunComposed(pack, parsed.Request, options, extras, parsed.Precedence)
                : TypologyPipeline.Run(pack, parsed.Request, options);
            json = result.Status == PrototypePipelineStatus.Refused ? "" : TypologyPipelineSerializer.Serialize(result);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("generate-typology internal error: " + error.GetType().FullName + ": " + error.Message);
            throw new Refusal(InternalError);
        }
        if (result.Status == PrototypePipelineStatus.Refused)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = Refused, refusals = result.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) }));
            Environment.ExitCode = 1;
            return;
        }
        var bytes = new UTF8Encoding(false).GetBytes(json);
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException error) { throw new Refusal(error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", error.GetType().Name); }
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var pipelineStatus = result.Status == PrototypePipelineStatus.Ranked ? "ranked" : "infeasible";
        var projectId = parsed.ProjectId.ToString("D", CultureInfo.InvariantCulture);
        var acceptedCount = result.Alternatives.Count(alternative => alternative.Accepted);
        if (parsed.Composed)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "succeeded",
                scope = "proposal_only",
                persisted = false,
                pipeline_status = pipelineStatus,
                rules = "composed",
                pack = parsed.Pack,
                code_pack = CodePack,
                skills = parsed.Skills ?? Array.Empty<string>(),
                project_id = projectId,
                alternatives = result.Alternatives.Count,
                accepted = acceptedCount,
                bytes = bytes.LongLength,
                sha256,
            }));
            return;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            scope = "proposal_only",
            persisted = false,
            pipeline_status = pipelineStatus,
            pack = parsed.Pack,
            code_pack = CodePack,
            project_id = projectId,
            alternatives = result.Alternatives.Count,
            accepted = acceptedCount,
            bytes = bytes.LongLength,
            sha256,
        }));
    }

    // Package root, then repository; bounded manifest read, parse, id (SkillPackLookup, shared with generate-prototypes).
    static SkillManifest Load(string id, string unavailable, string invalid, string? skill = null)
    {
        try { return SkillPackLookup.Load(id, packageFirst: true); }
        catch (SkillPackException error)
        {
            throw error.Problem switch
            {
                SkillPackProblem.RepositoryNotFound => new Refusal(RepositoryNotFound),
                SkillPackProblem.Unavailable => new Refusal(unavailable, error.Detail, skill),
                _ => new Refusal(invalid, error.Detail, skill),
            };
        }
    }

    // ---- request parsing: strict members and types; value ranges are the pipeline's ----

    static Parsed Parse(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new Refusal(RequestInvalid, "json"); }
        using (document)
        {
            var root = Members(document.RootElement, "request", RootMembers);
            foreach (var required in new[] { "pack", "family_size", "lot", "alternatives", "seed", "project_id" })
                if (!root.ContainsKey(required)) throw new Refusal(RequestInvalid, required);
            var packElement = root["pack"];
            var pack = packElement.ValueKind == JsonValueKind.String ? StringValue(packElement, "pack") : "";
            if (!SkillParser.IsId(pack)) throw new Refusal(RequestInvalid, "pack");
            var familySize = Int(root["family_size"], "family_size");
            var lot = Members(root["lot"], "lot", LotMembers);
            if (lot.Count != LotMembers.Length) throw new Refusal(RequestInvalid, "lot");
            var lotValue = new PrototypeLot(Number(lot["width_m"], "lot.width_m"), Number(lot["depth_m"], "lot.depth_m"));
            var alternatives = Int(root["alternatives"], "alternatives");
            var seed = Int(root["seed"], "seed");
            var idElement = root["project_id"];
            if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParseExact(StringValue(idElement, "project_id"), "D", out var projectId))
                throw new Refusal(RequestInvalid, "project_id");
            var weights = root.TryGetValue("weights", out var weightsElement) ? Weights(weightsElement) : new ScoringWeights();
            var request = new TypologyBriefRequest(familySize, lotValue, alternatives, seed, Weights: weights == new ScoringWeights() ? null : weights);
            var skills = root.TryGetValue("skills", out var skillsElement) ? Strings(skillsElement, "skills", ids: true) : null;
            var precedence = root.TryGetValue("precedence", out var precedenceElement) ? Strings(precedenceElement, "precedence", ids: false) : null;
            return new Parsed(pack, request, projectId, skills, precedence);
        }
    }

    // The members of an object, each one allowed and present at most once.
    static Dictionary<string, JsonElement> Members(JsonElement element, string at, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new Refusal(RequestInvalid, at);
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            string name;
            // A name holding an unpaired surrogate escape cannot be read as a string.
            try { name = property.Name; }
            catch (InvalidOperationException) { throw new Refusal(RequestInvalid, at); }
            if (Array.IndexOf(allowed, name) < 0 || members.ContainsKey(name)) throw new Refusal(RequestInvalid, at + "." + name);
            members[name] = property.Value;
        }
        return members;
    }

    static double Number(JsonElement element, string at)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || !double.IsFinite(value)) throw new Refusal(RequestInvalid, at);
        // -0 is 0: the pipeline and the canonical output never see a negative zero.
        return value == 0 ? 0 : value;
    }

    static int Int(JsonElement element, string at)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value)) throw new Refusal(RequestInvalid, at);
        return value;
    }

    // GetString throws InvalidOperationException for an unpaired surrogate escape ("\ud800"): a malformed member.
    static string StringValue(JsonElement element, string at)
    {
        try { return element.GetString() ?? throw new Refusal(RequestInvalid, at); }
        catch (InvalidOperationException) { throw new Refusal(RequestInvalid, at); }
    }

    // A JSON array of at most MaxListEntries strings (skill ids when ids), offending entries reported as at[i].
    static IReadOnlyList<string> Strings(JsonElement element, string at, bool ids)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxListEntries) throw new Refusal(RequestInvalid, at);
        var values = new List<string>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var name = at + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (item.ValueKind != JsonValueKind.String) throw new Refusal(RequestInvalid, name);
            var value = StringValue(item, name);
            if (ids && !SkillParser.IsId(value)) throw new Refusal(RequestInvalid, name);
            values.Add(value);
            index++;
        }
        return values.AsReadOnly();
    }

    static ScoringWeights Weights(JsonElement element)
    {
        var members = Members(element, "weights", WeightMembers);
        var defaults = new ScoringWeights();
        double W(string name, double fallback) => members.TryGetValue(name, out var value) ? Number(value, "weights." + name) : fallback;
        return new ScoringWeights(W("area_fit", defaults.AreaFit), W("adjacency", defaults.Adjacency), W("proportions", defaults.Proportions),
            W("compactness", defaults.Compactness), W("perimeter", defaults.Perimeter), W("compliance", defaults.Compliance));
    }

    static void Fail(string code, string? detail, string? skill = null)
    {
        Console.WriteLine(skill is not null ? JsonSerializer.Serialize(new { status = "failed", code, detail, skill })
            : detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
