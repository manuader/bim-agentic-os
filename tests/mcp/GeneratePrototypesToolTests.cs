using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Generative;

// P7-21 generate_prototypes: the read-only MCP face of the P7-09/P7-12 CLI verb `generate-prototypes request.json
// output.json`, built on the verb's own request reader, parser, default project id, package-first pack lookup and pipeline
// path (interfaces/cli/GenerateCommand.cs, linked into the server, no copied parser). Oracles:
//   1. On success the tool text is the canonical PrototypePipelineSerializer document: its UTF-8 bytes equal the file the
//      real CLI process writes for the same request file, and the in-process PrototypePipeline.Run serialization (explicit
//      and derived project ids, member order / whitespace / -0 / explicit defaults, tuned weights with snap, revision,
//      circulation and facade weights, approvals, another code pack).
//   2. Every refusal is isError and byte-equal to the CLI stdout line for the same request (input_*, request_invalid,
//      pack unavailable, generate_prototypes_refused with the library's typed refusals).
//   3. It writes nothing (the request directory is unchanged; it takes no output path), leaves the process exit code
//      untouched and writes nothing to Console.Out; tools/list labels it permission level read with readOnlyHint true only.
//   4. MCP-only path guard: generate_prototypes_path_invalid {detail request}; missing / mistyped / extra arguments are -32602.
//   5. The real Release Bim.Mcp.dll answers the same bytes over stdio (packs found from the server binary).
//   6. A forced exception in the verb's guarded pipeline work (GenerateCommand.GuardedWorkProbe) is the typed
//      generate_prototypes_internal_error line with the IntentPlan diagnostic on stderr once, not McpServer's generic failure.
//   7. Static: the tool calls GenerateCommand.Document and contains no parser, pipeline, pack lookup or file access of its
//      own; the verb and Document share one Generate(ReadRequest(...)) path; the MCP projects link the CLI source.
//   8. P7-23: an infeasible result (three bedrooms on a 3 x 3 m lot) is the CLI output file byte for byte; refusals over the
//      real Bim.Mcp.dll stdio equal the CLI line; copies of Bim.Mcp.dll and the CLI in a scratch repository, a scratch
//      release package and outside any repository give the same generate_prototypes_pack_invalid / _repository_not_found
//      line; the lone-surrogate and -32602 calls leave Console.Out, the inputs and the exit code alone; a non-refusal
//      exception before the guarded block (GenerateCommand.UnguardedStageProbe, null in production, statically pinned)
//      is the typed generate_prototypes_internal_error line with the diagnostic on stderr once.
// The tool name, parameter and codes are literals here (not the tool's constants) so this file compiles before the tool exists.
internal static class GeneratePrototypesToolTests
{
    const string ToolName = "generate_prototypes";
    const string RequestParameter = "request";
    const string PathInvalid = "generate_prototypes_path_invalid";
    const string CodePack = "ar.caba.codigo-edificacion";
    const string ExampleCodePack = "xx.example.codigo-ejemplo";
    const string Id = "00000000-0000-4000-8000-0000000007a9";

    public static void Run(string root, string work, string dotnet, string cliDll, Func<string, JsonObject> call, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "generate_prototypes: " + name);
        var scratch = Path.Combine(work, "generate-prototypes-mcp");
        var inputs = Path.Combine(scratch, "in");
        var outputs = Path.Combine(scratch, "cli-out");
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(outputs);
        var counter = 0;
        string Next() => (++counter).ToString(CultureInfo.InvariantCulture);
        string Input(string name, string content)
        {
            var path = Path.Combine(inputs, Next() + "-" + name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        string Listing() => string.Join("\n", Directory.GetFileSystemEntries(inputs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(e => Path.GetRelativePath(inputs, e) + (File.Exists(e) ? ":" + Hex(File.ReadAllBytes(e)) + ":" + File.GetLastWriteTimeUtc(e).Ticks.ToString(CultureInfo.InvariantCulture) : "/")));
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static string Short(string text) => text.Length > 400 ? text[..400] + "..." : text;
        static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        // The real CLI process with a fresh output path: (exit, stdout line, written bytes or null).
        (int Exit, string Out, byte[]? File) Cli(string requestPath)
        {
            var output = Path.Combine(outputs, Next() + "-out.json");
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            start.ArgumentList.Add("generate-prototypes");
            start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new Exception("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("CLI timed out: generate-prototypes " + requestPath); }
            process.WaitForExit();
            _ = stderr.Result;
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, File.Exists(output) ? File.ReadAllBytes(output) : null);
        }
        string ToolLine(JsonObject arguments) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 19, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = ToolName, ["arguments"] = arguments } }.ToJsonString();
        JsonObject Tool(string requestPath) => call(ToolLine(new JsonObject { [RequestParameter] = requestPath }));
        static string Text(JsonObject response) => response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "<no text: " + response.ToJsonString() + ">";
        static bool IsError(JsonObject response) => response["result"]?["isError"]?.GetValue<bool>() ?? true;
        static int? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<int>();

        var exitBefore = Environment.ExitCode;
        // One MCP call with Console.Out redirected to a StringWriter: the response, with "nothing written to the inputs or to
        // Console.Out, exit code untouched" checked around it.
        JsonObject Call(string label, string requestPath)
        {
            var before = Listing();
            var stdout = Console.Out;
            var captured = new StringWriter();
            JsonObject response;
            Console.SetOut(captured);
            try { response = Tool(requestPath); }
            finally { Console.SetOut(stdout); }
            Check(Listing() == before && Environment.ExitCode == exitBefore && captured.ToString().Length == 0, label + ": writes nothing (inputs, Console.Out), exit code untouched | " + Short(captured.ToString()));
            return response;
        }

        // ---- tools/list ----
        var listed = call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }.ToJsonString())["result"]!["tools"]!.AsArray();
        var tools = listed.Where(t => t!["name"]!.GetValue<string>() == ToolName).ToArray();
        var tool = tools.Length == 1 ? tools[0]!.AsObject() : new JsonObject();
        var description = tool["description"]?.GetValue<string>() ?? "";
        Check(tools.Length == 1 && description.Contains("generate-prototypes", StringComparison.Ordinal) && description.Contains("read-only", StringComparison.OrdinalIgnoreCase),
            "listed once, read-only, naming the CLI verb generate-prototypes");
        Check(tool["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>() == "read" && tool["annotations"] is JsonObject annotations
            && annotations["readOnlyHint"]?.GetValue<bool>() == true && annotations.Count == 1, "lists _meta permission_level read and annotations readOnlyHint true only");
        Check(tool["inputSchema"] is JsonObject schema && schema["required"] is JsonArray required && string.Join(",", required.Select(x => x!.GetValue<string>())) == RequestParameter
            && schema["properties"] is JsonObject properties && properties.Count == 1 && properties[RequestParameter]?["type"]?.GetValue<string>() == "string"
            && schema["additionalProperties"]?.GetValue<bool>() == false, "schema: exactly request (string), required");

        // ---- 1. documents: CLI output file == tool text == in-process PrototypePipeline.Run ----
        SkillManifest Skill(string id) => SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", id, "skill.json")));
        var caba = Skill(CodePack);
        var explicitId = Guid.Parse(Id);
        byte[] Document(PrototypeRequest request, PrototypePipelineOptions options)
        {
            var result = PrototypePipeline.Run(request, options);
            Check(result.Status != PrototypePipelineStatus.Refused, "fixture: the in-process run is a result, not a refusal (" + result.Status + ")");
            return Utf8(PrototypePipelineSerializer.Serialize(result));
        }
        byte[] Expect(string label, string requestText, byte[] library)
        {
            var requestPath = Input("request.json", requestText);
            var response = Call(label, requestPath);
            var cli = Cli(requestPath);
            var text = Utf8(Text(response));
            Check(!IsError(response) && text.SequenceEqual(library), label + ": the text is the in-process pipeline document byte for byte | " + Short(Text(response)));
            Check(cli.Exit == 0 && cli.File is not null && text.SequenceEqual(cli.File) && cli.Out.Contains("\"sha256\":\"" + Hex(text) + "\"", StringComparison.Ordinal),
                label + ": the text is byte-equal to the CLI output file | exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli.Out));
            return text;
        }
        var twoBed = new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 2, 0);
        var twoBedDocument = Expect("two-bedroom with an explicit project id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"" + Id + "\"}",
            Document(twoBed, new PrototypePipelineOptions(caba, explicitId)));
        // The documented canonical request of tests/cli/GeneratePrototypesTests.cs and its derived UUIDv8.
        const string canonical = "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":0,\"door_width_m\":0.9,"
            + "\"weights\":{\"area_fit\":0.3,\"adjacency\":0.25,\"proportions\":0.15,\"compactness\":0.1,\"perimeter\":0.1,\"compliance\":0.1},"
            + "\"fixed_rooms\":[],\"fixed_walls\":[],\"pack\":\"ar.caba.codigo-edificacion\",\"revision\":0,\"circulation_weight\":0.15,\"facade_weight\":0,\"snap\":false}";
        var derivedId = DerivedId(canonical);
        var oneBed = new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 0);
        var derived = Expect("one-bedroom with the derived default project id", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2}",
            Document(oneBed, new PrototypePipelineOptions(caba, derivedId)));
        var reordered = Expect("member order, whitespace, -0 and explicit defaults", "{\n  \"snap\": false, \"facade_weight\": -0.0, \"circulation_weight\": 0.15, \"revision\": 0,\n"
            + "  \"pack\": \"ar.caba.codigo-edificacion\", \"door_width_m\": 0.9, \"weights\": {}, \"fixed_rooms\": [], \"fixed_walls\": [],\n"
            + "  \"seed\": -0, \"alternatives\": 2, \"lot\": {\"depth_m\": 12, \"width_m\": 7.0}, \"typology\": 1\n}\n", Document(oneBed, new PrototypePipelineOptions(caba, derivedId)));
        Check(reordered.SequenceEqual(derived), "the reordered request gives the minimal request's derived id and bytes");
        Check(!derived.SequenceEqual(twoBedDocument), "different requests give different documents");
        const string tunedCanonical = "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":3,\"door_width_m\":0.9,"
            + "\"weights\":{\"area_fit\":0.5,\"adjacency\":0.25,\"proportions\":0.15,\"compactness\":0.1,\"perimeter\":0.1,\"compliance\":0.2},"
            + "\"fixed_rooms\":[],\"fixed_walls\":[],\"pack\":\"ar.caba.codigo-edificacion\",\"revision\":2,\"circulation_weight\":0.3,\"facade_weight\":0.2,\"snap\":true}";
        Expect("partial weights, snap, revision, circulation and facade weights", "{\"snap\":true,\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"facade_weight\":0.2,"
            + "\"circulation_weight\":0.3,\"revision\":2,\"seed\":3,\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2}",
            Document(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 3, Weights: new ScoringWeights(0.5, 0.25, 0.15, 0.1, 0.1, 0.2)),
                new PrototypePipelineOptions(caba, DerivedId(tunedCanonical), 2, 0.3, Snap: new PrototypeSnapOptions(), FacadeWeight: 0.2)));
        // Approvals: a real room of a generated layout and a lot-edge wall with a non-ASCII id.
        var generated = PrototypeGenerator.Generate(oneBed);
        var sourceRoom = generated.Alternatives?.Alternatives.Select(a => a.Plan).FirstOrDefault(p => p.Status == SpacePlanStatus.Feasible)?.Rooms.FirstOrDefault(r => r.Instance == 1);
        Check(sourceRoom is not null, "fixture: a generated 1-bedroom layout provides a room to approve");
        if (sourceRoom is not null)
        {
            var rect = sourceRoom.Rect;
            Expect("fixed rooms and walls (non-ASCII wall id)", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"project_id\":\"" + Id + "\","
                + "\"fixed_walls\":[{\"y1\":0,\"x1\":7,\"y0\":0,\"x0\":0,\"id\":\"muro-\u00F1\"}],"
                + "\"fixed_rooms\":[{\"depth\":" + R(rect.Depth) + ",\"width\":" + R(rect.Width) + ",\"y\":" + R(rect.Y) + ",\"x\":" + R(rect.X) + ",\"room_id\":\"" + sourceRoom.RoomId + "\"}]}",
                Document(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 0, new[] { new FixedRoom(sourceRoom.RoomId, rect) }, new[] { new FixedWall("muro-\u00F1", 0, 0, 7, 0) }),
                    new PrototypePipelineOptions(caba, explicitId)));
        }
        var example = Expect("another code pack (" + ExampleCodePack + ", package-first lookup)", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"pack\":\"" + ExampleCodePack + "\",\"project_id\":\"" + Id + "\"}",
            Document(twoBed, new PrototypePipelineOptions(Skill(ExampleCodePack), explicitId)));
        Check(!example.SequenceEqual(twoBedDocument), "the " + ExampleCodePack + " document differs from the CABA one (the pack member really reached)");

        // ---- 2. refusals: isError, byte-equal to the CLI stdout line, no CLI file ----
        void Refused(string label, string requestPath, string? expected = null)
        {
            var response = Call(label, requestPath);
            var cli = Cli(requestPath);
            Check(IsError(response) && cli.Exit == 1 && cli.File is null && Text(response) == cli.Out && (expected is null || cli.Out == expected),
                "refuses " + label + " byte-equal to the CLI envelope | " + Short(Text(response)) + " / " + Short(cli.Out));
        }
        static string Code(string code, string? detail = null) => detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
        static string RefusedEnvelope(PrototypePipelineResult result) =>
            JsonSerializer.Serialize(new { status = "failed", code = "generate_prototypes_refused", refusals = result.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) });
        Refused("malformed JSON", Input("bad.json", "{"), Code("generate_prototypes_request_invalid", "json"));
        Refused("an unknown member", Input("unknown.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"family_size\":3}"), Code("generate_prototypes_request_invalid", "request.family_size"));
        Refused("a duplicate member", Input("duplicate.json", "{\"typology\":2,\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20}}"), Code("generate_prototypes_request_invalid", "request.typology"));
        Refused("a missing typology", Input("no-typology.json", "{\"lot\":{\"width_m\":8.66,\"depth_m\":20}}"), Code("generate_prototypes_request_invalid", "typology"));
        Refused("a missing lot side", Input("lot-side.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66}}"), Code("generate_prototypes_request_invalid", "lot"));
        Refused("a fractional alternatives", Input("alternatives.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2.5}"), Code("generate_prototypes_request_invalid", "alternatives"));
        Refused("a non-boolean snap", Input("snap.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"snap\":1}"), Code("generate_prototypes_request_invalid", "snap"));
        Refused("a malformed pack id", Input("pack-id.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"pack\":\"Not An Id\"}"), Code("generate_prototypes_request_invalid", "pack"));
        Refused("a lone surrogate room id", Input("surrogate.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"fixed_rooms\":[{\"room_id\":\"\\udc00\",\"x\":0,\"y\":0,\"width\":3,\"depth\":3}]}"),
            Code("generate_prototypes_request_invalid", "fixed_rooms[0].room_id"));
        Refused("an unknown pack", Input("unknown-pack.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"pack\":\"xx.no-such.pack\"}"), Code("generate_prototypes_pack_unavailable", "input_not_found"));
        var typology4 = PrototypePipeline.Run(new PrototypeRequest((ApartmentTypology)4, new PrototypeLot(8.66, 20), 2, 0), new PrototypePipelineOptions(caba, explicitId));
        Check(typology4.Status == PrototypePipelineStatus.Refused && typology4.Refusals.Count > 0, "fixture: typology 4 is refused in process");
        Refused("an unsupported typology (pipeline refusal)", Input("typology-4.json", "{\"typology\":4,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"" + Id + "\"}"), RefusedEnvelope(typology4));
        var negativeLot = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(-1, 20), 2, 0), new PrototypePipelineOptions(caba, explicitId));
        Refused("a negative lot width (pipeline refusal)", Input("negative-lot.json", "{\"typology\":2,\"lot\":{\"width_m\":-1,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"" + Id + "\"}"), RefusedEnvelope(negativeLot));
        var zeroId = PrototypePipeline.Run(twoBed, new PrototypePipelineOptions(caba, Guid.Empty));
        Refused("an all-zero project id (pipeline refusal)", Input("zero-id.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"00000000-0000-0000-0000-000000000000\"}"), RefusedEnvelope(zeroId));
        Refused("a missing request file", Path.Combine(inputs, "absent.json"), Code("input_not_found"));
        Check(!File.Exists(Path.Combine(inputs, "absent.json")), "creates no missing request");
        var directory = Path.Combine(inputs, Next() + "-a-directory.json");
        Directory.CreateDirectory(directory);
        Refused("a directory as request", directory, Code("input_not_regular_file"));
        Refused("a request over the 8 MiB input cap", Input("huge.json", new string(' ', 8 * 1024 * 1024 + 1)), Code("input_too_large"));

        // ---- 4. MCP-only path guard and argument errors ----
        var pathInvalid = Code(PathInvalid, "request");
        foreach (var (label, bad) in new[] { ("empty", ""), ("blank", "   "), ("with NUL", "a\0b"), ("of 5000 characters", new string('x', 5000)) })
        {
            var response = Call("request " + label, bad);
            Check(IsError(response) && Text(response) == pathInvalid, "refuses a request path " + label + " as generate_prototypes_path_invalid | " + Short(Text(response)));
        }
        var surrogate = call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + ToolName + "\",\"arguments\":{\"" + RequestParameter + "\":\"\\ud800\"}}}");
        Check(IsError(surrogate) && Text(surrogate) == pathInvalid && Environment.ExitCode == exitBefore, "a lone-surrogate request path is generate_prototypes_path_invalid, never a crash | " + Short(Text(surrogate)));
        var okRequest = Input("ok.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"" + Id + "\"}");
        Check(ErrorCode(call(ToolLine(new JsonObject()))) == -32602, "without request returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { [RequestParameter] = 1 }))) == -32602, "a non-string request returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { [RequestParameter] = JsonNode.Parse("{\"typology\":2}") }))) == -32602, "an inline request object (not a path) returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { [RequestParameter] = okRequest, ["output"] = Path.Combine(inputs, "never.json") }))) == -32602 && !File.Exists(Path.Combine(inputs, "never.json")),
            "takes no output argument (-32602): it never writes a file");

        // ---- 5. the real Release Bim.Mcp.dll over stdio ----
        var processOut = mcpProcess(ToolLine(new JsonObject { [RequestParameter] = okRequest }) + "\n");
        var processResponse = JsonNode.Parse(processOut.Split('\n')[0])!.AsObject();
        Check(!IsError(processResponse) && Utf8(Text(processResponse)).SequenceEqual(twoBedDocument),
            "the real Bim.Mcp.dll process returns the two-bedroom document (pack found from the server binary) | " + Short(processOut));

        // ---- 6. the verb's guarded pipeline work forced through GuardedWorkProbe ----
        {
            var command = typeof(GeneratePrototypesToolTests).Assembly.GetType("Bim.Cli.GenerateCommand");
            var probe = command?.GetProperty("GuardedWorkProbe", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Check(probe is not null && probe.PropertyType == typeof(Action) && probe.CanWrite, "the linked GenerateCommand exposes the settable GuardedWorkProbe test seam");
            if (probe is not null && probe.PropertyType == typeof(Action) && probe.CanWrite)
            {
                Check(probe.GetValue(null) is null, "fixture: the seam is null unless a test sets it");
                var failure = new InvalidOperationException("boom generate_prototypes P7-21");
                var expectedLine = "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}";
                var expectedErr = "generate-prototypes internal error: System.InvalidOperationException: boom generate_prototypes P7-21" + Environment.NewLine;
                var errors = new StringWriter();
                var savedErr = Console.Error;
                JsonObject forced;
                probe.SetValue(null, (Action)(() => throw failure));
                try
                {
                    Console.SetError(errors);
                    try { forced = Call("forced internal error", okRequest); }
                    finally { Console.SetError(savedErr); }
                }
                finally { probe.SetValue(null, null); }
                Check(IsError(forced) && Text(forced) == expectedLine, "a forced exception in the guarded pipeline is the typed generate_prototypes_internal_error line, not McpServer's generic failure | " + Short(Text(forced)));
                Check(errors.ToString() == expectedErr, "the forced internal error writes the IntentPlan diagnostic to stderr once | " + Short(errors.ToString()));
                var after = Call("after the forced internal error", okRequest);
                Check(probe.GetValue(null) is null && !IsError(after) && Utf8(Text(after)).SequenceEqual(twoBedDocument), "the seam is reset: the next call answers the unchanged document");
            }
        }

        // ---- 7. static: the shared path, linked, not copied ----
        // Comments stripped: /* */ blocks first, then // to end of line.
        static string CodeOf(string text) => string.Join("\n", Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline).Split('\n')
            .Select(line => { var at = line.IndexOf("//", StringComparison.Ordinal); return at < 0 ? line : line[..at]; }));
        var toolPath = Path.Combine(root, "interfaces", "mcp", "GeneratePrototypesTool.cs");
        Check(File.Exists(toolPath), "interfaces/mcp/GeneratePrototypesTool.cs exists");
        var toolCode = File.Exists(toolPath) ? CodeOf(File.ReadAllText(toolPath)) : "";
        Check(Regex.IsMatch(toolCode, @"\bGenerateCommand\s*\.\s*Document\s*\("), "the tool runs the CLI verb's shared GenerateCommand.Document");
        var inlined = new[] { "JsonDocument", "Utf8JsonReader", "\"typology\"", "\"lot\"", "\"weights\"", "\"fixed_rooms\"", "PrototypePipeline", "PrototypeRequest", "ScoringWeights", "SHA256",
            "SkillPackLookup", "SkillParser", "BoundedInput", "File.", "FileStream", "Directory.", "GltfExporter", "Console.SetOut" }
            .Where(api => toolCode.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(inlined.Length == 0, "the tool inlines no request parser, id derivation, pipeline, pack lookup, file access or Console capture [" + string.Join(", ", inlined) + "]");
        var owners = Directory.GetFiles(Path.Combine(root, "interfaces", "mcp"), "*.cs")
            .Where(f => new[] { "PrototypePipeline.", "PrototypeRequest(", "PrototypePipelineSerializer" }.Any(api => CodeOf(File.ReadAllText(f)).Contains(api, StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f)).ToArray();
        Check(owners.Length == 0, "no interfaces/mcp source builds a prototype request or runs the pipeline itself [" + string.Join(", ", owners) + "]");
        // P4-27: Execute (the verb's Generate(ReadRequest(...)) call) lives in the CLI-only write part GenerateWrite.cs.
        var commandCode = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateCommand.cs")) + "\n"
            + File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateWrite.cs")));
        Check(Regex.Matches(commandCode, @"\bGenerate\s*\(\s*ReadRequest\s*\(").Count == 2 && Regex.Matches(commandCode, @"\bParse\s*\(\s*text\s*\)").Count == 1
            && Regex.Matches(commandCode, @"\bPrototypePipeline\s*\.\s*Run\s*\(").Count == 1,
            "the CLI verb and Document share one path: both call Generate(ReadRequest(...)); the request is parsed and the pipeline run in one place");
        foreach (var project in new[] { "interfaces/mcp/Bim.Mcp.csproj", "tests/mcp/Bim.Mcp.Tests.csproj", "tests/cli/Bim.Cli.Tests.csproj", "tests/revit-commands/Bim.RevitCommands.Tests.csproj" })
        {
            var projectPath = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            var projectDir = Path.GetDirectoryName(projectPath)!;
            var includes = System.Xml.Linq.XDocument.Load(projectPath).Descendants().Where(e => e.Name.LocalName == "Compile")
                .Select(e => (string?)e.Attribute("Include")).Where(include => include is not null)
                .Select(include => Path.GetFullPath(Path.Combine(projectDir, include!.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)))).ToArray();
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var shared in new[] { "interfaces/cli/GenerateCommand.cs", "interfaces/mcp/GeneratePrototypesTool.cs" })
            {
                if (project.StartsWith("interfaces/mcp/", StringComparison.Ordinal) && shared.StartsWith("interfaces/mcp/", StringComparison.Ordinal)) continue; // compiled by default
                var full = Path.GetFullPath(Path.Combine(root, shared.Replace('/', Path.DirectorySeparatorChar)));
                Check(includes.Any(include => string.Equals(include, full, comparison)), project + " compiles " + shared + " (shared source, not a copy)");
            }
        }

        // ---- 8. P7-23 refusal coverage ----
        {
            void Check723(bool condition, string name) => check(condition, "P7-23 generate_prototypes refusal coverage: " + name);
            var nl = Environment.NewLine;
            static bool UnderRepository(string directory)
            {
                for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return true;
                return false;
            }
            static string CopyBinary(string dll, string target)
            {
                var source = Path.GetDirectoryName(dll)!;
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
                return Path.Combine(target, Path.GetFileName(dll));
            }
            // A CLI copy: (exit, stdout line, written bytes or null), a fresh output path each time.
            (int Exit, string Out, byte[]? File) CliAt(string dll, string requestPath)
            {
                var output = Path.Combine(outputs, Next() + "-copy-out.json");
                var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
                start.ArgumentList.Add(dll);
                start.ArgumentList.Add("generate-prototypes");
                start.ArgumentList.Add(requestPath);
                start.ArgumentList.Add(output);
                using var process = Process.Start(start) ?? throw new Exception("CLI copy did not start: " + dotnet);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("CLI copy timed out: " + dll); }
                process.WaitForExit();
                _ = stderr.Result;
                return (process.ExitCode, stdout.Result.TrimEnd('\r', '\n'), File.Exists(output) ? File.ReadAllBytes(output) : null);
            }
            // An MCP server copy over stdio: one response object per input line.
            JsonObject[] McpAt(string dll, params string[] lines)
            {
                var start = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
                start.ArgumentList.Add(dll);
                using var process = Process.Start(start) ?? throw new Exception("MCP copy did not start: " + dotnet);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                try { process.StandardInput.Write(string.Concat(lines.Select(line => line + "\n"))); process.StandardInput.Close(); } catch (IOException) { }
                if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("MCP copy timed out: " + dll); }
                process.WaitForExit();
                _ = stderr.Result;
                return stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
            }
            void SameRefusal(string label, JsonObject[] responses, (int Exit, string Out, byte[]? File) cli, string expected)
            {
                var response = responses.Length == 1 ? responses[0] : new JsonObject();
                Check723(responses.Length == 1 && IsError(response) && cli.Exit == 1 && cli.File is null && Text(response) == cli.Out && cli.Out == expected,
                    label + ": the copied Bim.Mcp.dll over stdio answers the CLI copy's refusal line byte for byte | " + Short(Text(response)) + " / " + Short(cli.Out));
            }
            var mcpDll = Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll");
            Check723(File.Exists(mcpDll), "fixture: the Release Bim.Mcp.dll exists (" + mcpDll + ")");

            // 8a. An infeasible pipeline result (a 3 x 3 m lot for three bedrooms: every alternative rejected) is a
            // result, not a refusal: the tool text is the CLI output file and the in-process document byte for byte.
            var tinyResult = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.ThreeBedroom, new PrototypeLot(3, 3), 2, 0), new PrototypePipelineOptions(caba, explicitId));
            Check723(tinyResult.Status == PrototypePipelineStatus.Infeasible && tinyResult.Accepted.Count == 0 && tinyResult.Alternatives.Count > 0,
                "fixture: a three-bedroom request on a 3 x 3 m lot is infeasible in process (" + tinyResult.Status + ", " + tinyResult.Alternatives.Count.ToString(CultureInfo.InvariantCulture) + " alternatives)");
            var tinyPath = Input("tiny-lot.json", "{\"typology\":3,\"lot\":{\"width_m\":3,\"depth_m\":3},\"alternatives\":2,\"seed\":0,\"project_id\":\"" + Id + "\"}");
            var tinyResponse = Call("an infeasible lot", tinyPath);
            var tinyCli = Cli(tinyPath);
            var tinyText = Utf8(Text(tinyResponse));
            Check723(!IsError(tinyResponse) && tinyText.SequenceEqual(Utf8(PrototypePipelineSerializer.Serialize(tinyResult))),
                "an infeasible lot: the tool text (isError false) is the in-process infeasible document byte for byte | " + Short(Text(tinyResponse)));
            Check723(tinyCli.Exit == 0 && tinyCli.File is not null && tinyText.SequenceEqual(tinyCli.File)
                && tinyCli.Out.Contains("\"pipeline_status\":\"infeasible\"", StringComparison.Ordinal) && tinyCli.Out.Contains("\"accepted\":0", StringComparison.Ordinal)
                && tinyCli.Out.Contains("\"sha256\":\"" + Hex(tinyText) + "\"", StringComparison.Ordinal),
                "an infeasible lot: the tool text is byte-equal to the CLI output file (pipeline_status infeasible, accepted 0) | exit " + tinyCli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(tinyCli.Out));

            // 8b. A refusal over the real Release Bim.Mcp.dll stdio (pipeline refusal and request_invalid), equal to the CLI line.
            var stdioTypology4 = Input("stdio-typology-4.json", "{\"typology\":4,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"" + Id + "\"}");
            var stdioBad = Input("stdio-bad.json", "{");
            var stdioOut = mcpProcess(ToolLine(new JsonObject { [RequestParameter] = stdioTypology4 }) + "\n" + ToolLine(new JsonObject { [RequestParameter] = stdioBad }) + "\n");
            var stdioLines = stdioOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
            var stdioTypology4Cli = Cli(stdioTypology4);
            var stdioBadCli = Cli(stdioBad);
            Check723(stdioLines.Length == 2 && IsError(stdioLines[0]) && Text(stdioLines[0]) == stdioTypology4Cli.Out && stdioTypology4Cli.Out == RefusedEnvelope(typology4) && stdioTypology4Cli.Exit == 1 && stdioTypology4Cli.File is null,
                "the real Bim.Mcp.dll over stdio answers the generate_prototypes_refused line of the CLI byte for byte | " + Short(stdioOut));
            Check723(stdioLines.Length == 2 && IsError(stdioLines[1]) && Text(stdioLines[1]) == stdioBadCli.Out && stdioBadCli.Out == Code("generate_prototypes_request_invalid", "json") && stdioBadCli.File is null,
                "the real Bim.Mcp.dll over stdio answers the generate_prototypes_request_invalid line of the CLI byte for byte | " + Short(stdioOut));

            // 8c. generate_prototypes_pack_invalid from a scratch repository (global.json, its own skills/): a copied
            // Bim.Mcp.dll in <repo>/mcp and a copied CLI in <repo>/cli give the same line.
            var scratchRepo = Path.Combine(scratch, "p7-23-repo-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratchRepo);
                File.WriteAllText(Path.Combine(scratchRepo, "global.json"), "{}");
                var cabaText = File.ReadAllText(Path.Combine(root, "skills", CodePack, "skill.json"));
                void RepoPack(string folder, string text) { Directory.CreateDirectory(Path.Combine(scratchRepo, "skills", folder)); File.WriteAllText(Path.Combine(scratchRepo, "skills", folder, "skill.json"), text, new UTF8Encoding(false)); }
                RepoPack("zz.test.bad-json", "{");
                RepoPack("zz.test.mismatch", cabaText.Replace("\"id\": \"" + CodePack + "\"", "\"id\": \"zz.test.other\"", StringComparison.Ordinal));
                RepoPack(CodePack, cabaText);
                var repoMcp = CopyBinary(mcpDll, Path.Combine(scratchRepo, "mcp"));
                var repoCli = CopyBinary(cliDll, Path.Combine(scratchRepo, "cli"));
                var badJson = Input("pack-bad-json.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"pack\":\"zz.test.bad-json\",\"project_id\":\"" + Id + "\"}");
                var badJsonCli = CliAt(repoCli, badJson);
                var badJsonCode = badJsonCli.Out.Length > 0 && badJsonCli.Out[0] == '{' ? JsonNode.Parse(badJsonCli.Out)?["code"]?.GetValue<string>() : null;
                Check723(badJsonCode == "generate_prototypes_pack_invalid", "fixture: the scratch repository's malformed pack is generate_prototypes_pack_invalid from the copied CLI | " + Short(badJsonCli.Out));
                SameRefusal("a malformed pack manifest in a scratch repository", McpAt(repoMcp, ToolLine(new JsonObject { [RequestParameter] = badJson })), badJsonCli,
                    "{\"status\":\"failed\",\"code\":\"generate_prototypes_pack_invalid\",\"detail\":\"json_invalid\"}");
                var mismatch = Input("pack-mismatch.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"pack\":\"zz.test.mismatch\",\"project_id\":\"" + Id + "\"}");
                SameRefusal("a pack manifest naming another id in a scratch repository", McpAt(repoMcp, ToolLine(new JsonObject { [RequestParameter] = mismatch })), CliAt(repoCli, mismatch),
                    "{\"status\":\"failed\",\"code\":\"generate_prototypes_pack_invalid\",\"detail\":\"id\"}");
                // The scratch repository's own CABA pack: the copied server answers the in-repository document (the lookup really reached the scratch skills/).
                var repoOk = McpAt(repoMcp, ToolLine(new JsonObject { [RequestParameter] = okRequest }));
                Check723(repoOk.Length == 1 && !IsError(repoOk[0]) && Utf8(Text(repoOk[0])).SequenceEqual(twoBedDocument),
                    "fixture: the copied server in the scratch repository answers the two-bedroom document from the scratch skills/ | " + Short(repoOk.Length == 1 ? Text(repoOk[0]) : "<none>"));
            }
            finally { try { Directory.Delete(scratchRepo, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

            // 8d. generate_prototypes_repository_not_found (copies outside any repository, no package markers) and
            // _pack_invalid from a scratch release package (<pkg>/mcp and <pkg>/cli beside VERSION, manifest.json, skills/).
            var outsideBase = new[] { Environment.GetEnvironmentVariable("RUNNER_TEMP"), Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows() ? null : "/tmp" }
                .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !UnderRepository(candidate));
            // On CI (CI, GITHUB_ACTIONS or RUNNER_TEMP set) the outside-repository cases must run on every OS; a local run may skip with its reason.
            var onCi = new[] { "CI", "GITHUB_ACTIONS", "RUNNER_TEMP" }.Any(variable => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)));
            if (onCi) Check723(outsideBase is not null, "on CI a writable directory outside any repository exists, so repository_not_found and the scratch package run (no skip)");
            if (outsideBase is null) Console.WriteLine("SKIP P7-23 generate_prototypes: no writable directory outside a repository for the copied server (local run)");
            else
            {
                var outsideRoot = Path.Combine(outsideBase, "bim-p7-23-outside-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var outsideMcp = CopyBinary(mcpDll, Path.Combine(outsideRoot, "mcp"));
                    var outsideCli = CopyBinary(cliDll, Path.Combine(outsideRoot, "cli"));
                    Check723(!UnderRepository(outsideRoot), "fixture: the outside copies have no global.json above them");
                    SameRefusal("copies outside any repository", McpAt(outsideMcp, ToolLine(new JsonObject { [RequestParameter] = okRequest })), CliAt(outsideCli, okRequest),
                        "{\"status\":\"failed\",\"code\":\"generate_prototypes_repository_not_found\"}");
                }
                finally { try { Directory.Delete(outsideRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

                var packageRoot = Path.Combine(outsideBase, "bim-p7-23-package-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var packageMcp = CopyBinary(mcpDll, Path.Combine(packageRoot, "mcp"));
                    var packageCli = CopyBinary(cliDll, Path.Combine(packageRoot, "cli"));
                    File.WriteAllText(Path.Combine(packageRoot, "VERSION"), "0.0.0-test\n");
                    File.WriteAllText(Path.Combine(packageRoot, "manifest.json"), "{}\n");
                    Directory.CreateDirectory(Path.Combine(packageRoot, "skills", CodePack));
                    File.WriteAllText(Path.Combine(packageRoot, "skills", CodePack, "skill.json"), "{", new UTF8Encoding(false));
                    var packageCliRun = CliAt(packageCli, okRequest);
                    var packageCode = packageCliRun.Out.Length > 0 && packageCliRun.Out[0] == '{' ? JsonNode.Parse(packageCliRun.Out)?["code"]?.GetValue<string>() : null;
                    Check723(packageCode == "generate_prototypes_pack_invalid", "fixture: a malformed default pack in the scratch package is generate_prototypes_pack_invalid from the copied CLI | " + Short(packageCliRun.Out));
                    SameRefusal("a malformed default pack in a scratch release package", McpAt(packageMcp, ToolLine(new JsonObject { [RequestParameter] = okRequest })), packageCliRun,
                        "{\"status\":\"failed\",\"code\":\"generate_prototypes_pack_invalid\",\"detail\":\"json_invalid\"}");
                }
                finally { try { Directory.Delete(packageRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }

            // 8e. The lone-surrogate and -32602 calls leave Console.Out empty, the input listing unchanged and the exit code.
            JsonObject Quiet(string label, string line)
            {
                var before = Listing();
                var stdout = Console.Out;
                var captured = new StringWriter();
                JsonObject response;
                Console.SetOut(captured);
                try { response = call(line); }
                finally { Console.SetOut(stdout); }
                Check723(Listing() == before && captured.ToString().Length == 0 && Environment.ExitCode == exitBefore,
                    label + ": Console.Out empty, the input listing unchanged, exit code untouched | " + Short(captured.ToString()));
                return response;
            }
            var quietSurrogate = Quiet("a lone-surrogate request path", "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + ToolName + "\",\"arguments\":{\"" + RequestParameter + "\":\"\\ud800\"}}}");
            Check723(IsError(quietSurrogate) && Text(quietSurrogate) == pathInvalid, "a lone-surrogate request path is generate_prototypes_path_invalid | " + Short(Text(quietSurrogate)));
            // Argument objects are built from JSON text (JsonObject creations inside a tuple array crash Roslyn's AD0001
            // UseCollectionInitializer analyzer, ATTEMPTS P10-12).
            static JsonObject Arguments(string json) => JsonNode.Parse(json)!.AsObject();
            foreach (var (label, arguments) in new[]
            {
                ("no request", "{}"),
                ("a non-string request", "{\"" + RequestParameter + "\":1}"),
                ("an inline request object", "{\"" + RequestParameter + "\":{\"typology\":2}}"),
                ("an output argument", "{\"" + RequestParameter + "\":" + JsonSerializer.Serialize(okRequest) + ",\"output\":" + JsonSerializer.Serialize(Path.Combine(inputs, "never-p7-23.json")) + "}"),
            })
                Check723(ErrorCode(Quiet(label + " (-32602)", ToolLine(Arguments(arguments)))) == -32602, label + " returns -32602");

            // 8f. Static: the unguarded-stage seam is declared once in GenerateCommand.cs and invoked once, inside Generate
            // before its Parse( call; nothing under interfaces/ assigns it. (Named apart from GenerateTypologyCommand's
            // UnguardedWorkProbe, whose P3-23 pin counts that name over interfaces/.)
            static List<string> StageSeamProblems(IEnumerable<(string Name, string Code)> files)
            {
                var problems = new List<string>();
                var total = 0;
                var sawCommand = false;
                foreach (var (name, code) in files)
                {
                    total += Regex.Matches(code, @"\bUnguardedStageProbe\b").Count;
                    if (Regex.IsMatch(code, @"\bUnguardedStageProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": UnguardedStageProbe assigned");
                    if (name != "interfaces/cli/GenerateCommand.cs") continue;
                    sawCommand = true;
                    var declarations = Regex.Matches(code, @"\binternal\s+static\s+Action\s*\?\s*UnguardedStageProbe\s*\{\s*get\s*;\s*set\s*;\s*\}").Count;
                    if (declarations != 1) problems.Add(name + ": the declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
                    var invokes = Regex.Matches(code, @"\bUnguardedStageProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;");
                    var generate = Regex.Match(code, @"\bstatic\s+Generated\s+Generate\s*\(");
                    if (invokes.Count != 1 || !generate.Success) { problems.Add(name + ": " + invokes.Count.ToString(CultureInfo.InvariantCulture) + " invokes, Generate " + (generate.Success ? "found" : "missing")); continue; }
                    var parse = Regex.Match(code[(generate.Index + generate.Length)..], @"\bParse\s*\(");
                    var parseAt = parse.Success ? generate.Index + generate.Length + parse.Index : -1;
                    if (parseAt < 0 || invokes[0].Index < generate.Index || invokes[0].Index > parseAt) problems.Add(name + ": UnguardedStageProbe?.Invoke() not inside Generate before its Parse(");
                }
                if (!sawCommand) problems.Add("interfaces/cli/GenerateCommand.cs not found");
                if (total != 2) problems.Add("UnguardedStageProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
                return problems;
            }
            var interfaceSources = Directory.GetFiles(Path.Combine(root, "interfaces"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), CodeOf(File.ReadAllText(file)))).ToArray();
            var stageSeam = StageSeamProblems(interfaceSources);
            Check723(stageSeam.Count == 0, "interfaces/**/*.cs names UnguardedStageProbe only in its declaration and one ?.Invoke() before Parse( in Generate, never assigned [" + string.Join("; ", stageSeam) + "]");
            const string SeamFile = "interfaces/cli/GenerateCommand.cs";
            const string SeamDecl = "internal static Action? UnguardedStageProbe { get; set; }";
            static string SeamCommand(string body, string extra = "") => "internal static class GenerateCommand { static Generated Generate(string text) { " + body + " } " + SeamDecl + " " + extra + " }";
            const string SeamGood = "UnguardedStageProbe?.Invoke(); var parsed = Parse(text); return null;";
            Check723(StageSeamProblems(new[] { (SeamFile, SeamCommand(SeamGood)) }).Count == 0, "self-test: the stage seam checker accepts the real shape");
            foreach (var (label, files) in new (string, (string, string)[])[]
            {
                ("an assignment in the command", new[] { (SeamFile, SeamCommand(SeamGood, "static void Set() { UnguardedStageProbe = null; }")) }),
                ("an assignment in another interfaces file", new[] { (SeamFile, SeamCommand(SeamGood)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateCommand.UnguardedStageProbe ??= null; } }") }),
                ("the invoke after Parse(", new[] { (SeamFile, SeamCommand("var parsed = Parse(text); UnguardedStageProbe?.Invoke(); return null;")) }),
                ("the invoke missing", new[] { (SeamFile, SeamCommand("var parsed = Parse(text); return null;")) }),
                ("the declaration missing", new[] { (SeamFile, "internal static class GenerateCommand { static Generated Generate(string text) { " + SeamGood + " } }") }),
            })
                Check723(StageSeamProblems(files).Count > 0, "self-test: the stage seam checker flags " + label);

            // 8g. A non-refusal exception outside Generate's guarded pipeline block (before Parse, forced through
            // GenerateCommand.UnguardedStageProbe, null in production) is the typed generate_prototypes_internal_error
            // line (IntentPlan.Internal), not McpServer's generic Failed carrying the exception message; the diagnostic
            // goes to stderr once and nothing to stdout; with the seam reset the next call answers the unchanged document.
            var commandType = typeof(GeneratePrototypesToolTests).Assembly.GetType("Bim.Cli.GenerateCommand");
            var stageProbe = commandType?.GetProperty("UnguardedStageProbe", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var guardedProbe = commandType?.GetProperty("GuardedWorkProbe", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Check723(stageProbe is not null && stageProbe.PropertyType == typeof(Action) && stageProbe.CanWrite && guardedProbe is not null,
                "the linked GenerateCommand exposes the settable UnguardedStageProbe test seam");
            if (stageProbe is not null && stageProbe.PropertyType == typeof(Action) && stageProbe.CanWrite && guardedProbe is not null)
            {
                Check723(stageProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null, "fixture: the unguarded and guarded seams are null unless a test sets them");
                var unguarded = new InvalidOperationException("boom generate_prototypes P7-23 unguarded");
                var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", unguarded);
                var savedError = Console.Error;
                var savedOut = Console.Out;
                var toolErrors = new StringWriter();
                JsonObject viaTool;
                stageProbe.SetValue(null, (Action)(() => throw unguarded));
                try
                {
                    Console.SetError(toolErrors);
                    try { viaTool = Call("forced unguarded internal error", okRequest); }
                    finally { Console.SetError(savedError); }
                }
                finally { stageProbe.SetValue(null, null); }
                Check723(IsError(viaTool) && Text(viaTool) == envelope.Line && !Text(viaTool).Contains(unguarded.Message, StringComparison.Ordinal),
                    "a forced exception before the guarded block reaches the MCP tool as the typed generate_prototypes_internal_error line, not McpServer's generic failure | " + Short(Text(viaTool)));
                Check723(toolErrors.ToString() == envelope.Diagnostic + nl, "the forced unguarded internal error writes the IntentPlan diagnostic to stderr once | " + Short(toolErrors.ToString()));
                Check723(Text(viaTool) == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}"
                    && toolErrors.ToString() == "generate-prototypes internal error: System.InvalidOperationException: boom generate_prototypes P7-23 unguarded" + nl,
                    "literal bytes: the tool text is {\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"} and stderr the literal diagnostic line | " + Short(Text(viaTool)) + " / " + Short(toolErrors.ToString()));

                var directErrors = new StringWriter();
                var directOut = new StringWriter();
                (bool Succeeded, string Text) direct;
                stageProbe.SetValue(null, (Action)(() => throw unguarded));
                try
                {
                    Console.SetError(directErrors);
                    Console.SetOut(directOut);
                    try { direct = Bim.Cli.GenerateCommand.Document(okRequest); }
                    catch (Exception escaped) { direct = (true, "escaped: " + escaped.GetType().FullName + ": " + escaped.Message); }
                    finally { Console.SetError(savedError); Console.SetOut(savedOut); }
                }
                finally { stageProbe.SetValue(null, null); }
                Check723(!direct.Succeeded && direct.Text == envelope.Line, "GenerateCommand.Document returns the typed line instead of throwing | " + Short(direct.Text));
                Check723(directErrors.ToString() == envelope.Diagnostic + nl && directOut.ToString().Length == 0,
                    "Document writes the diagnostic to stderr once and nothing to stdout | stderr=" + Short(directErrors.ToString()) + " stdout=" + Short(directOut.ToString()));
                var reset = Call("after the forced unguarded internal error", okRequest);
                Check723(stageProbe.GetValue(null) is null && !IsError(reset) && Utf8(Text(reset)).SequenceEqual(twoBedDocument), "the seam is reset: the next call answers the unchanged document");
            }
        }
    }

    // The documented rule (tests/cli/GeneratePrototypesTests.cs): the first 16 bytes of SHA-256(UTF-8
    // "bim-agentic-os/generate-prototypes/v1\n" + canonical request), read big-endian as an RFC 9562 UUID v8.
    static Guid DerivedId(string canonical)
    {
        var hash = SHA256.HashData(new UTF8Encoding(false).GetBytes("bim-agentic-os/generate-prototypes/v1\n" + canonical));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
