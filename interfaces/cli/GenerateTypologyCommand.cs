#nullable enable
using System.Globalization;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

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
//   precedence     optional array of at most 64 skill id strings (P5-16): the resolver's precedence order (earlier
//                  wins among equal priorities), each entry a well-formed skill id (else request_invalid precedence[i]),
//                  passed through in order; an id that is not a composed skill or one listed twice is the resolver's
//                  typed precedence_unknown / precedence_duplicate refusal
//   include        optional array of at most 64 strings (P7-16): optional rooms, TypologyBriefRequest.Include as given (the
//                  pipeline sorts and de-duplicates them; P7-18: [] is normalised to the absent member, same file and stdout);
//                  a room kind that is unknown or not offered by the pack is the pipeline's typed typology_room_kind_unknown /
//                  typology_room_not_offered refusal (generate_typology_refused); a non-string entry, one over 64 UTF-16 units or one holding a
//                  control character is request_invalid include[i]
//   code_pack      optional skill id string (P7-16), default ar.caba.codigo-edificacion: the facade gate's code pack
//                  (PrototypePipelineOptions.Pack, also the composed code pack); it must be a skill of kind code, else
//                  generate_typology_code_pack_invalid with detail "kind"; naming the default is the absent member
// Every number must be a finite JSON number (-0 is read as 0); integers must be JSON integer literals in the int32 range;
// strings holding an unpaired UTF-16 surrogate escape are refused like any malformed member. Value ranges (family size,
// lot sides, count, weights, an all-zero project id, a non-typology pack) are left to TypologyPipeline, whose refusals
// are reported. Not in v1 (refused as unknown members): revision, circulation / facade weights and snap; the pipeline gets
// PrototypePipelineOptions(code pack, project_id)
// with every other option at its default, so the output is TypologyPipeline.Run(pack, request, options) of the P5-12
// in-process API.
// P5-16 composed rules: when skills or precedence is present (even as []), the pipeline is instead
// TypologyPipeline.RunComposed(pack, request, options, extra manifests in request order, precedence or null) of the P5-15
// in-process API: the typology pack, the code pack and the extras are resolved (SkillResolver) and the composed pack rules
// plan the candidates, so "skills":[] gives the CABA + typology composed bytes (pin a8c62da5...). Its resolution refusals
// (skill_conflict, dependency_missing, precedence_unknown, default_conflict, ...) are generate_typology_refused with the
// typed issues exactly as RunComposed returns them. With both members absent the output and stdout are the P7-14 ones.
// With include and code_pack absent the output and stdout are the P7-14 ones (pins eb8720c1... / 3a551774...).
// Packs: the typology pack and the code pack (code_pack, default ar.caba.codigo-edificacion: the facade gate's) are each read with
// SkillPackLookup.Load(id, packageFirst: true), as generate-prototypes reads its pack: <package>/skills/<id>/skill.json
// when the CLI sits in the release package layout, else <repository>/skills/<id>/skill.json.
//
// Success: the file is written and stdout is {"status":"succeeded","scope":"proposal_only","persisted":false,
// "pipeline_status":"ranked"|"infeasible","pack","code_pack" (the code pack used),"project_id","alternatives":n,"accepted":k,"bytes":n,
// "sha256":lower-case hex of the file}; exit 0. An infeasible result is a result: it is written with its rejections.
// Failures print one {"status":"failed","code":...[,"detail"]} line, exit 1 and leave no output file:
//   generate_typology_usage (detail: the usage text), output_* (GltfExportCommand.OutputProblem with .json, checked
//   before the request is read; output_exists and output_io_error at the write), input_* (BoundedInput, the request),
//   generate_typology_request_invalid (detail: the offending member), generate_typology_repository_not_found,
//   generate_typology_pack_unavailable / generate_typology_code_pack_unavailable (detail: the manifest's input_* code),
//   generate_typology_pack_invalid / generate_typology_code_pack_invalid (detail: the skill code, or "id" when the
//   manifest names another pack; "kind" when the code pack is not a code skill), generate_typology_skill_unavailable / generate_typology_skill_invalid (an extra skill;
//   detail as for the packs, plus "skill": the extra id), generate_typology_refused (with "refusals":[{"code","subject","detail"}] =
//   TypologyPipelineResult.Refusals in order, no detail), generate_typology_internal_error (stderr: exception type, message;
//   P3-25: an unexpected exception after GltfExporter.WriteNew succeeded removes the just-written file again).
// Composed-mode stdout adds "rules":"composed" and "skills":[the extra ids as given].
// Precedence: usage, output path, request read, request parse, repository, typology pack, code pack, extra skills (in
// request order), pipeline, write.
// The file is created exclusively (GltfExporter.WriteNew: FileMode.CreateNew, removed again if the write fails).
// Everything is culture-invariant: the same request gives the same bytes and stdout under any current culture.
// P4-26: Run, Execute (the output path, the exclusive GltfExporter.WriteNew write, the P3-25 removal, the success line), the
// P3-25 seam PostWriteProbe and Fail live in GenerateTypologyWrite.cs (the CLI only); interfaces/mcp/Bim.Mcp.csproj links
// this part alone (Document and the shared Generate path the generate_typology tool runs).
internal static partial class GenerateTypologyCommand
{
    public const string CodePack = "ar.caba.codigo-edificacion";
    public const string Usage = "usage: generate-typology request.json output.json (request: {\"pack\": typology skill id, \"family_size\": int, "
        + "\"lot\": {\"width_m\", \"depth_m\"}, \"alternatives\": int, \"seed\": int, \"project_id\": GUID, optional \"weights\", optional \"skills\": [at most 64 extra skill ids] "
        + "and \"precedence\": [at most 64 skill ids], either of which runs the composed pack rules, optional \"include\": [at most 64 optional room kinds] "
        + "and optional \"code_pack\": a code skill id}; the facade code pack defaults to " + CodePack + "; packs are read from <package>/skills/<id>/skill.json in the release package layout, else from <repository>/skills/<id>/skill.json, "
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
    public const int MaxRoomLength = 64;
    public const string InternalError = "generate_typology_internal_error";

    static readonly string[] RootMembers = { "pack", "family_size", "lot", "alternatives", "seed", "project_id", "weights", "skills", "precedence", "include", "code_pack" };
    static readonly string[] LotMembers = { "width_m", "depth_m" };
    static readonly string[] WeightMembers = { "area_fit", "adjacency", "proportions", "compactness", "perimeter", "compliance" };

    sealed class Refusal(string code, string? detail = null, string? skill = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
        public string? Skill { get; } = skill;
    }

    // Skills and Precedence are null when absent; Composed when either is present. CodePack is the default when absent.
    sealed record Parsed(string Pack, TypologyBriefRequest Request, Guid ProjectId, IReadOnlyList<string>? Skills, IReadOnlyList<string>? Precedence, string CodePack)
    {
        public bool Composed => Skills is not null || Precedence is not null;
    }

    // P5-17: the request-to-document path shared with the MCP generate_typology tool (interfaces/mcp/GenerateTypologyTool.cs
    // links this file): the request file read like the CLI reads it, then the same Parse, pack loads and pipeline as the
    // verb (Generate). Writes nothing. Succeeded: Text is the canonical document, exactly the string whose UTF-8 bytes the
    // verb writes to its output file. Otherwise Text is the verb's failure stdout line without its newline (the verb
    // would exit 1). There is no output path, so no generate_typology_usage or output_* refusal comes from here.
    internal static (bool Succeeded, string Text) Document(string requestPath)
    {
        try
        {
            var generated = Generate(ReadRequest(requestPath));
            return generated.RefusedLine is { } refused ? (false, refused) : (true, generated.Json);
        }
        catch (Refusal refusal) { return (false, FailureLine(refusal.Code, refusal.Detail, refusal.Skill)); }
        // P3-23: a non-refusal exception outside Generate's guarded pipeline block (the parse / pack-load stretch, forced in
        // tests through UnguardedWorkProbe) is the typed internal error too, not an exception escaping to McpServer's
        // generic Failed(message). The guarded block's own failures already arrive here as Refusal(InternalError) with their
        // diagnostic written once.
        catch (Exception error)
        {
            var envelope = IntentPlan.Internal("generate-typology", error);
            Console.Error.WriteLine(envelope.Diagnostic);
            return (false, envelope.Line);
        }
    }

    // RefusedLine is the generate_typology_refused stdout line when the pipeline refused (Json is then empty).
    sealed record Generated(Parsed Parsed, TypologyPipelineResult Result, string Json, string? RefusedLine);

    // The request file through BoundedInput (8 MiB cap); its input_* code is the refusal.
    static string ReadRequest(string path)
    {
        try { return BoundedInput.ReadText(path); }
        catch (InputRejectedException error) { throw new Refusal(error.Message); }
    }

    // Parse, typology pack, code pack, extra skills (request order), pipeline: everything after the request read and
    // before the write, in the verb's precedence order. Throws Refusal; a pipeline refusal is returned as RefusedLine.
    static Generated Generate(string text)
    {
        UnguardedWorkProbe?.Invoke();
        var parsed = Parse(text);
        var pack = Load(parsed.Pack, PackUnavailable, PackInvalid);
        var code = Load(parsed.CodePack, CodePackUnavailable, CodePackInvalid);
        if (code.Kind != SkillKind.Code) throw new Refusal(CodePackInvalid, "kind");
        var extras = new List<SkillManifest>();
        foreach (var id in parsed.Skills ?? Array.Empty<string>()) extras.Add(Load(id, SkillUnavailable, SkillInvalid, id));

        var options = new PrototypePipelineOptions(code, parsed.ProjectId);
        TypologyPipelineResult result;
        string json;
        try
        {
            GuardedWorkProbe?.Invoke();
            result = parsed.Composed
                ? TypologyPipeline.RunComposed(pack, parsed.Request, options, extras, parsed.Precedence)
                : TypologyPipeline.Run(pack, parsed.Request, options);
            json = result.Status == PrototypePipelineStatus.Refused ? "" : TypologyPipelineSerializer.Serialize(result);
        }
        catch (Exception error) { throw InternalRefusal(error); }
        if (result.Status == PrototypePipelineStatus.Refused)
            return new Generated(parsed, result, "", JsonSerializer.Serialize(new { status = "failed", code = Refused, refusals = result.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) }));
        return new Generated(parsed, result, json, null);
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
            var include = root.TryGetValue("include", out var includeElement) ? Rooms(includeElement) : null;
            // P7-18: an empty list is the absent member, so "include":[] keeps the P7-14 / composed bytes.
            if (include is { Count: 0 }) include = null;
            var request = new TypologyBriefRequest(familySize, lotValue, alternatives, seed, include, weights == new ScoringWeights() ? null : weights);
            var skills = root.TryGetValue("skills", out var skillsElement) ? Strings(skillsElement, "skills") : null;
            var precedence = root.TryGetValue("precedence", out var precedenceElement) ? Strings(precedenceElement, "precedence") : null;
            var codePack = CodePack;
            if (root.TryGetValue("code_pack", out var codeElement))
            {
                codePack = codeElement.ValueKind == JsonValueKind.String ? StringValue(codeElement, "code_pack") : "";
                if (!SkillParser.IsId(codePack)) throw new Refusal(RequestInvalid, "code_pack");
            }
            return new Parsed(pack, request, projectId, skills, precedence, codePack);
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

    // A JSON array of at most MaxListEntries skill id strings (SkillParser.IsId), offending entries reported as at[i].
    static IReadOnlyList<string> Strings(JsonElement element, string at)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxListEntries) throw new Refusal(RequestInvalid, at);
        var values = new List<string>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var name = at + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (item.ValueKind != JsonValueKind.String) throw new Refusal(RequestInvalid, name);
            var value = StringValue(item, name);
            if (!SkillParser.IsId(value)) throw new Refusal(RequestInvalid, name);
            values.Add(value);
            index++;
        }
        return values.AsReadOnly();
    }

    // P7-16 include: a JSON array of at most MaxListEntries strings, in request order; the room kinds are the pipeline's.
    static IReadOnlyList<string> Rooms(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxListEntries) throw new Refusal(RequestInvalid, "include");
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var name = "include[" + values.Count.ToString(CultureInfo.InvariantCulture) + "]";
            if (item.ValueKind != JsonValueKind.String) throw new Refusal(RequestInvalid, name);
            var value = StringValue(item, name);
            // Bounded and printable, so no long or control-character text is passed on (or echoed in a refusal).
            if (value.Length > MaxRoomLength || value.Any(char.IsControl)) throw new Refusal(RequestInvalid, name);
            values.Add(value);
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

    // P3-20 test seam: null in production. tests/cli/InternalErrorDelegationTests.cs sets it (by reflection) to an Action
    // that throws, so the guarded library work below fails inside Run's real catch-all; it is reset after the test.
    internal static Action? GuardedWorkProbe { get; set; }

    // P3-23 test seam: null in production. tests/mcp/InternalErrorEnvelopeToolTests.cs sets it to an Action that throws,
    // so a non-refusal exception arises in Generate before the guarded pipeline block (the parse / pack-load stretch);
    // it is reset after the test.
    internal static Action? UnguardedWorkProbe { get; set; }

    // P3-19: the catch-all's internal error: IntentPlan's stderr detail, then the Refusal Run prints as
    // {"status":"failed","code":InternalError}, exit 1 (IntentPlan.Internal's bytes). P3-30: the diagnostic is written
    // guarded, so a stderr that throws (any exception) drops only that line and the typed Refusal(InternalError) is still
    // returned (no second internal error about the stderr exception), on the CLI and on the MCP Document path.
    static Refusal InternalRefusal(Exception error)
    {
        try { Console.Error.WriteLine(IntentPlan.InternalDiagnostic("generate-typology", error)); } catch (Exception) { }
        return new Refusal(InternalError);
    }

    static string FailureLine(string code, string? detail, string? skill) =>
        skill is not null ? JsonSerializer.Serialize(new { status = "failed", code, detail, skill })
            : detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
}
