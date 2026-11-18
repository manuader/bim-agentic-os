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
        var commandCode = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateCommand.cs")));
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
