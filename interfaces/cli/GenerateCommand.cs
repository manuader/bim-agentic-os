#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;
using Bim.Gltf;

namespace Bim.Cli;

// P7-09 `generate-prototypes request.json output.json`: PrototypePipeline.Run (core/Bim.Generative, P7-07) on one JSON
// request, its canonical PrototypePipelineSerializer JSON written to a NEW file (UTF-8, no BOM, exactly the serializer's
// string). Nothing is committed to a project store; the BIM-IR prototypes are a proposal (only described in the file).
//
// Request (one JSON object, snake_case, unknown or duplicate members refused; read through BoundedInput, 8 MiB cap):
//   typology       required integer, the ApartmentTypology value (1, 2 or 3; any other integer is the pipeline's refusal)
//   lot            required {"width_m": number, "depth_m": number}
//   alternatives   integer, default 8                seed     integer, default 0
//   door_width_m   number, default 0.9
//   weights        {"area_fit","adjacency","proportions","compactness","perimeter","compliance"}: numbers, each one
//                  absent taking its ScoringWeights default ({} and an absent member both mean the default weights)
//   fixed_rooms    [{"room_id": string, "x","y","width","depth": numbers}]   absent = [] (no approved rooms)
//   fixed_walls    [{"id": string, "x0","y0","x1","y1": numbers}]             absent = [] (no approved walls)
//   pack           skill id of the code pack, default ar.caba.codigo-edificacion, read from <root>/skills/<id>/skill.json
//                  through BoundedInput. The root (P7-12) is first the release package root: the parent of the CLI
//                  binary's directory (<package>/cli) when it holds a skills/ directory (scripts/package.ps1 ships every
//                  tracked skills file there, listed in manifest.json); else the repository, the first ancestor
//                  directory of the CLI binary holding global.json. A CLI with neither (copied outside a repository
//                  without a skills/ beside its directory) refuses every request with generate_prototypes_repository_not_found.
//                  The same request gives byte-identical output from the package and from the repository.
//   project_id     GUID string ("D" form); default derived (below)        revision  integer, default 0
//   circulation_weight number, default 0.15   facade_weight number, default 0   snap boolean, default false
//                  (true = PrototypeSnapOptions() with its default rules)
// Every number must be a finite JSON number (-0 is read as 0); integers must be JSON integer literals (no fraction or
// exponent) in the int32 range. Strings holding an unpaired UTF-16 surrogate escape are refused like any malformed member. Value ranges (negative lot, count,
// weights or revision, typology 4...) are left to PrototypeGenerator / PrototypePipeline, whose refusals are reported.
// The stage options (circulation, facade, BIM-IR, pack parameter overrides) keep their defaults (not exposed in v1).
//
// Deterministic default project id (project_id absent): the canonical request is the compact JSON
//   {"typology","lot":{"width_m","depth_m"},"alternatives","seed","door_width_m","weights","fixed_rooms","fixed_walls",
//    "pack","revision","circulation_weight","facade_weight","snap"}
// in that member order with every default resolved, no project_id, no whitespace: weights always the full object in the
// order listed above; fixed_rooms / fixed_walls always arrays ([] when absent) of objects in the member orders listed
// above, items in request order; numbers as System.Text.Json's Utf8JsonWriter.WriteNumber(double) formats them (shortest
// round-trip text, exponent form such as 1E-07 for very small or large magnitudes; -0 already read as 0); strings with
// System.Text.Json's default escaping (non-ASCII and HTML-sensitive characters as \uXXXX with upper-case hex).
// The id is the first 16 bytes of SHA-256(UTF-8 "bim-agentic-os/generate-prototypes/v1\n" + canonical request), read
// big-endian as an RFC 9562 UUID with version 8 and the RFC variant bits set. Member order, whitespace, -0 and explicitly
// written defaults never change it; any value that changes the run does. It covers the pack id, not the pack manifest's
// content: the same request against an edited manifest derives the same id. Never Guid.NewGuid: reruns are byte-identical.
// The pipeline gets null weights when they equal the defaults and null approval lists when they are empty (the generator
// and serializers treat null and those values alike), so equal canonical requests give byte-identical files.
//
// Success: the file is written and stdout is {"status":"succeeded","scope":"proposal_only","persisted":false,
// "pipeline_status":"ranked"|"infeasible","project_id","project_id_source":"request"|"derived","alternatives":n,
// "accepted":k,"bytes":n,"sha256":lower-case hex of the file}; exit 0. An infeasible result (every alternative rejected
// at a gate) is a result, not a failure: it is written with its typed rejections.
// Failures print one {"status":"failed","code":...[,"detail"]} line, exit 1 and leave no output file:
//   generate_prototypes_usage (detail: the usage text), output_* (GltfExportCommand.OutputProblem with .json, checked
//   before the request is read; output_exists and output_io_error at the write), input_* (BoundedInput, the request),
//   generate_prototypes_request_invalid (detail: the offending member), generate_prototypes_repository_not_found,
//   generate_prototypes_pack_unavailable (detail: the pack's input_* code), generate_prototypes_pack_invalid (detail:
//   the skill code, or "id" when the manifest names another pack), generate_prototypes_refused (with
//   "refusals":[{"code","subject","detail"}] = PrototypePipelineResult.Refusals in order, no detail),
//   generate_prototypes_internal_error (stderr gets the exception type and message).
// Precedence: usage, output path, request read, request parse, repository, pack, pipeline, write.
// The file is created exclusively (GltfExporter.WriteNew: FileMode.CreateNew, removed again if the write fails).
internal static class GenerateCommand
{
    public const string Usage = "usage: generate-prototypes request.json output.json (default project_id: UUIDv8 from the first 16 bytes of "
        + "SHA-256(\"bim-agentic-os/generate-prototypes/v1\\n\" + canonical request JSON without project_id); the pack is read from "
        + "<package>/skills/<pack>/skill.json when the release package root (the parent of the CLI's cli/ directory) holds skills/, else from "
        + "<repository>/skills/<pack>/skill.json, the repository being the first ancestor of the CLI holding global.json, else generate_prototypes_repository_not_found)";
    public const string UsageCode = "generate_prototypes_usage";
    public const string RequestInvalid = "generate_prototypes_request_invalid";
    public const string RepositoryNotFound = "generate_prototypes_repository_not_found";
    public const string PackUnavailable = "generate_prototypes_pack_unavailable";
    public const string PackInvalid = "generate_prototypes_pack_invalid";
    public const string Refused = "generate_prototypes_refused";
    public const string InternalError = "generate_prototypes_internal_error";
    public const string DefaultPack = "ar.caba.codigo-edificacion";
    const string IdPrefix = "bim-agentic-os/generate-prototypes/v1\n";

    static readonly string[] RootMembers = { "typology", "lot", "alternatives", "seed", "door_width_m", "weights", "fixed_rooms", "fixed_walls", "pack",
        "project_id", "revision", "circulation_weight", "facade_weight", "snap" };
    static readonly string[] LotMembers = { "width_m", "depth_m" };
    static readonly string[] WeightMembers = { "area_fit", "adjacency", "proportions", "compactness", "perimeter", "compliance" };
    static readonly string[] RoomMembers = { "room_id", "x", "y", "width", "depth" };
    static readonly string[] WallMembers = { "id", "x0", "y0", "x1", "y1" };

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    sealed record Parsed(PrototypeRequest Request, string Pack, Guid? ProjectId, int Revision, double CirculationWeight, double FacadeWeight, bool Snap);

    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
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
        var projectId = parsed.ProjectId ?? DerivedId(Canonical(parsed));

        // Package root, then repository; bounded manifest read, parse, id (SkillPackLookup, shared with plan-layouts --pack,
        // which keeps the repository-only lookup).
        SkillManifest pack;
        try { pack = SkillPackLookup.Load(parsed.Pack, packageFirst: true); }
        catch (SkillPackException error)
        {
            throw error.Problem switch
            {
                SkillPackProblem.RepositoryNotFound => new Refusal(RepositoryNotFound),
                SkillPackProblem.Unavailable => new Refusal(PackUnavailable, error.Detail),
                _ => new Refusal(PackInvalid, error.Detail),
            };
        }

        var options = new PrototypePipelineOptions(pack, projectId, parsed.Revision, parsed.CirculationWeight,
            Snap: parsed.Snap ? new PrototypeSnapOptions() : null, FacadeWeight: parsed.FacadeWeight);
        PrototypePipelineResult result;
        string json;
        try
        {
            result = PrototypePipeline.Run(parsed.Request, options);
            json = result.Status == PrototypePipelineStatus.Refused ? "" : PrototypePipelineSerializer.Serialize(result);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("generate-prototypes internal error: " + error.GetType().FullName + ": " + error.Message);
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
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            scope = "proposal_only",
            persisted = false,
            pipeline_status = result.Status == PrototypePipelineStatus.Ranked ? "ranked" : "infeasible",
            project_id = projectId.ToString("D", CultureInfo.InvariantCulture),
            project_id_source = parsed.ProjectId is null ? "derived" : "request",
            alternatives = result.Alternatives.Count,
            accepted = result.Alternatives.Count(alternative => alternative.Accepted),
            bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        }));
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
            if (!root.TryGetValue("typology", out var typology)) throw new Refusal(RequestInvalid, "typology");
            if (!root.TryGetValue("lot", out var lotElement)) throw new Refusal(RequestInvalid, "lot");
            var lot = Members(lotElement, "lot", LotMembers);
            if (lot.Count != LotMembers.Length) throw new Refusal(RequestInvalid, "lot");
            var parsedWeights = root.TryGetValue("weights", out var weights) ? Weights(weights) : new ScoringWeights();
            var parsedRooms = root.TryGetValue("fixed_rooms", out var rooms) ? FixedRooms(rooms) : Array.Empty<FixedRoom>();
            var parsedWalls = root.TryGetValue("fixed_walls", out var walls) ? FixedWalls(walls) : Array.Empty<FixedWall>();
            var request = new PrototypeRequest(
                (ApartmentTypology)Int(typology, "typology"),
                new PrototypeLot(Number(lot["width_m"], "lot.width_m"), Number(lot["depth_m"], "lot.depth_m")),
                root.TryGetValue("alternatives", out var count) ? Int(count, "alternatives") : 8,
                root.TryGetValue("seed", out var seed) ? Int(seed, "seed") : 0,
                parsedRooms.Length == 0 ? null : parsedRooms,
                parsedWalls.Length == 0 ? null : parsedWalls,
                parsedWeights == new ScoringWeights() ? null : parsedWeights,
                root.TryGetValue("door_width_m", out var door) ? Number(door, "door_width_m") : PlannerOptions.DefaultDoorWidthMetres);
            var pack = DefaultPack;
            if (root.TryGetValue("pack", out var packElement))
            {
                pack = packElement.ValueKind == JsonValueKind.String ? StringValue(packElement, "pack") : "";
                if (!SkillParser.IsId(pack)) throw new Refusal(RequestInvalid, "pack");
            }
            Guid? projectId = null;
            if (root.TryGetValue("project_id", out var idElement))
            {
                if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParseExact(StringValue(idElement, "project_id"), "D", out var id)) throw new Refusal(RequestInvalid, "project_id");
                projectId = id;
            }
            var snap = false;
            if (root.TryGetValue("snap", out var snapElement))
            {
                if (snapElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new Refusal(RequestInvalid, "snap");
                snap = snapElement.GetBoolean();
            }
            return new Parsed(request, pack, projectId,
                root.TryGetValue("revision", out var revision) ? Int(revision, "revision") : 0,
                root.TryGetValue("circulation_weight", out var circulation) ? Number(circulation, "circulation_weight") : CirculationScoring.DefaultCirculationWeight,
                root.TryGetValue("facade_weight", out var facade) ? Number(facade, "facade_weight") : 0,
                snap);
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
        // -0 is 0: the canonical request and the pipeline never see a negative zero.
        return value == 0 ? 0 : value;
    }

    static int Int(JsonElement element, string at)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value)) throw new Refusal(RequestInvalid, at);
        return value;
    }

    static string Text(JsonElement element, string at)
    {
        if (element.ValueKind != JsonValueKind.String) throw new Refusal(RequestInvalid, at);
        return StringValue(element, at);
    }

    // GetString throws InvalidOperationException for an unpaired surrogate escape ("\ud800"): a malformed member.
    static string StringValue(JsonElement element, string at)
    {
        try { return element.GetString() ?? throw new Refusal(RequestInvalid, at); }
        catch (InvalidOperationException) { throw new Refusal(RequestInvalid, at); }
    }

    static ScoringWeights Weights(JsonElement element)
    {
        var members = Members(element, "weights", WeightMembers);
        var defaults = new ScoringWeights();
        double W(string name, double fallback) => members.TryGetValue(name, out var value) ? Number(value, "weights." + name) : fallback;
        return new ScoringWeights(W("area_fit", defaults.AreaFit), W("adjacency", defaults.Adjacency), W("proportions", defaults.Proportions),
            W("compactness", defaults.Compactness), W("perimeter", defaults.Perimeter), W("compliance", defaults.Compliance));
    }

    static FixedRoom[] FixedRooms(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new Refusal(RequestInvalid, "fixed_rooms");
        var result = new List<FixedRoom>();
        foreach (var item in element.EnumerateArray())
        {
            var at = "fixed_rooms[" + result.Count.ToString(CultureInfo.InvariantCulture) + "]";
            var members = Members(item, at, RoomMembers);
            if (members.Count != RoomMembers.Length) throw new Refusal(RequestInvalid, at);
            result.Add(new FixedRoom(Text(members["room_id"], at + ".room_id"),
                new PlanRect(Number(members["x"], at + ".x"), Number(members["y"], at + ".y"), Number(members["width"], at + ".width"), Number(members["depth"], at + ".depth"))));
        }
        return result.ToArray();
    }

    static FixedWall[] FixedWalls(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new Refusal(RequestInvalid, "fixed_walls");
        var result = new List<FixedWall>();
        foreach (var item in element.EnumerateArray())
        {
            var at = "fixed_walls[" + result.Count.ToString(CultureInfo.InvariantCulture) + "]";
            var members = Members(item, at, WallMembers);
            if (members.Count != WallMembers.Length) throw new Refusal(RequestInvalid, at);
            result.Add(new FixedWall(Text(members["id"], at + ".id"), Number(members["x0"], at + ".x0"), Number(members["y0"], at + ".y0"),
                Number(members["x1"], at + ".x1"), Number(members["y1"], at + ".y1")));
        }
        return result.ToArray();
    }

    // ---- deterministic default project id ----

    // The canonical request: every resolved value in a fixed member order, without project_id (see the header).
    static string Canonical(Parsed parsed)
    {
        var request = parsed.Request;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("typology", (int)request.Typology);
            writer.WriteStartObject("lot");
            writer.WriteNumber("width_m", request.Lot.WidthMetres);
            writer.WriteNumber("depth_m", request.Lot.DepthMetres);
            writer.WriteEndObject();
            writer.WriteNumber("alternatives", request.Alternatives);
            writer.WriteNumber("seed", request.Seed);
            writer.WriteNumber("door_width_m", request.DoorWidthMetres);
            {
                var weights = request.Weights ?? new ScoringWeights();
                writer.WriteStartObject("weights");
                writer.WriteNumber("area_fit", weights.AreaFit);
                writer.WriteNumber("adjacency", weights.Adjacency);
                writer.WriteNumber("proportions", weights.Proportions);
                writer.WriteNumber("compactness", weights.Compactness);
                writer.WriteNumber("perimeter", weights.Perimeter);
                writer.WriteNumber("compliance", weights.Compliance);
                writer.WriteEndObject();
            }
            {
                var rooms = request.FixedRooms ?? Array.Empty<FixedRoom>();
                writer.WriteStartArray("fixed_rooms");
                foreach (var room in rooms)
                {
                    writer.WriteStartObject();
                    writer.WriteString("room_id", room.RoomId);
                    writer.WriteNumber("x", room.Rect.X);
                    writer.WriteNumber("y", room.Rect.Y);
                    writer.WriteNumber("width", room.Rect.Width);
                    writer.WriteNumber("depth", room.Rect.Depth);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            {
                var walls = request.FixedWalls ?? Array.Empty<FixedWall>();
                writer.WriteStartArray("fixed_walls");
                foreach (var wall in walls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", wall.Id);
                    writer.WriteNumber("x0", wall.X0);
                    writer.WriteNumber("y0", wall.Y0);
                    writer.WriteNumber("x1", wall.X1);
                    writer.WriteNumber("y1", wall.Y1);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteString("pack", parsed.Pack);
            writer.WriteNumber("revision", parsed.Revision);
            writer.WriteNumber("circulation_weight", parsed.CirculationWeight);
            writer.WriteNumber("facade_weight", parsed.FacadeWeight);
            writer.WriteBoolean("snap", parsed.Snap);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static Guid DerivedId(string canonical)
    {
        var hash = SHA256.HashData(new UTF8Encoding(false).GetBytes(IdPrefix + canonical));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
