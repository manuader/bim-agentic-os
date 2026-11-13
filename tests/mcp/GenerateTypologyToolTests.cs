using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Generative;
using Bim.Mcp;

// P5-17 generate_typology: the read-only MCP face of the P7-14/P5-16 CLI verb `generate-typology request.json output.json`,
// built on the verb's own request parser and pipeline path (interfaces/cli/GenerateTypologyCommand.cs, linked into the
// server, no copied parser). Oracles:
//   1. On success the tool text is the canonical document: its UTF-8 bytes equal the file the real CLI process writes for the
//      same request file, and the in-process TypologyPipeline.Run / RunComposed serialization; the P5-12 pin eb8720c1... and
//      the P5-14/P5-15 composed pin a8c62da5... hold through the tool; skills / precedence / weights reach RunComposed.
//   2. Every refusal is isError and byte-equal to the CLI stdout line for the same request (input_*, request_invalid,
//      pack / skill unavailable, generate_typology_refused with the library's typed refusals).
//   3. It writes nothing (the request directory is unchanged; it takes no output path), and leaves the process exit code
//      and Console.Out untouched; tools/list labels it permission level read with readOnlyHint true only.
//   4. MCP-only path guard: generate_typology_path_invalid {detail request}; missing / mistyped / extra arguments are -32602.
//   5. The real Release Bim.Mcp.dll answers the same bytes over stdio.
//   6. Static: the tool calls GenerateTypologyCommand.Document and contains no parser, pipeline, pack lookup or file access
//      of its own; the MCP projects link the CLI source.
internal static class GenerateTypologyToolTests
{
    const string TypologyPack = "bim.typology.vivienda-unifamiliar";
    const string CodePack = "ar.caba.codigo-edificacion";
    const string CompactStyle = "bim.style.compact-modern";
    const string MasonryStyle = "bim.style.warm-masonry";
    const string Id = "00000000-0000-4000-8000-0000000007a7";
    // tests/cli/GenerateTypologyTests.cs: TypologyPipelineTests.PinnedAcceptedSha256 / PinnedSha256 (P5-12) and
    // TypologyPipelineManifestsTests.PinnedComposedSha256 (P5-14/P5-15).
    const string PinnedAcceptedSha256 = "eb8720c1a98a749048df6ac69f1794b965e833efc963eb9ddfe58db0ae886f4e";
    const string PinnedInfeasibleSha256 = "3a55177415f42f4d324cf3e4bd298223595c429006595a635aa12e9fb1a3d20b";
    const string PinnedComposedSha256 = "a8c62da59e0eee2d25af4d27bc37fcf2f67ae366e7e48446f1edfe3c87f7c0dd";

    public static void Run(string root, string work, string dotnet, string cliDll, Func<string, JsonObject> call, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "generate_typology: " + name);
        var scratch = Path.Combine(work, "generate-typology-mcp");
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

        string Request(string packId, int family, double width, double depth, int alternatives, int seed, string extra = "") =>
            "{\"pack\":\"" + packId + "\",\"family_size\":" + family.ToString(CultureInfo.InvariantCulture) + ",\"lot\":{\"width_m\":" + width.ToString("R", CultureInfo.InvariantCulture)
            + ",\"depth_m\":" + depth.ToString("R", CultureInfo.InvariantCulture) + "},\"alternatives\":" + alternatives.ToString(CultureInfo.InvariantCulture)
            + ",\"seed\":" + seed.ToString(CultureInfo.InvariantCulture) + ",\"project_id\":\"" + Id + "\"" + extra + "}";
        static string Ids(params string[] ids) => "[" + string.Join(",", ids.Select(id => "\"" + id + "\"")) + "]";

        // The real CLI process with a fresh output path: (exit, stdout line, written bytes or null).
        (int Exit, string Out, byte[]? File) Cli(string requestPath)
        {
            var output = Path.Combine(outputs, Next() + "-out.json");
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            start.ArgumentList.Add("generate-typology");
            start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new Exception("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("CLI timed out: generate-typology " + requestPath); }
            process.WaitForExit();
            _ = stderr.Result;
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, File.Exists(output) ? File.ReadAllBytes(output) : null);
        }
        string ToolLine(JsonObject arguments) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 17, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = GenerateTypologyTool.Name, ["arguments"] = arguments } }.ToJsonString();
        JsonObject Tool(string requestPath) => call(ToolLine(new JsonObject { [GenerateTypologyTool.RequestParameter] = requestPath }));
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        static int? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<int>();

        var exitBefore = Environment.ExitCode;
        // One MCP call: the response, with "nothing written, exit code and Console.Out untouched" checked around it.
        JsonObject Call(string label, string requestPath)
        {
            var before = Listing();
            var stdout = Console.Out;
            var response = Tool(requestPath);
            Check(Listing() == before && Environment.ExitCode == exitBefore && ReferenceEquals(Console.Out, stdout), label + ": writes nothing, exit code and Console.Out untouched");
            return response;
        }

        // ---- tools/list ----
        var listed = call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }.ToJsonString())["result"]!["tools"]!.AsArray();
        var tool = listed.SingleOrDefault(t => t!["name"]!.GetValue<string>() == "generate_typology");
        Check(tool is not null && tool["description"]!.GetValue<string>().Contains("generate-typology", StringComparison.Ordinal)
            && tool["description"]!.GetValue<string>().Contains("read-only", StringComparison.OrdinalIgnoreCase), "listed once, read-only, naming the CLI verb generate-typology");
        Check(tool!["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>() == "read" && tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true
            && tool["annotations"]!.AsObject().Count == 1, "lists _meta permission_level read and annotations readOnlyHint true only");
        Check(string.Join(",", tool["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>())) == "request_path" && tool["inputSchema"]!["properties"]!.AsObject().Count == 1
            && tool["inputSchema"]!["additionalProperties"]!.GetValue<bool>() == false, "schema: exactly request_path, required");

        // ---- 1. documents: CLI output file == tool text == in-process Run / RunComposed; pins ----
        SkillManifest Skill(string id) => SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", id, "skill.json")));
        var pack = Skill(TypologyPack);
        var caba = Skill(CodePack);
        var compact = Skill(CompactStyle);
        var masonry = Skill(MasonryStyle);
        var options = new PrototypePipelineOptions(caba, Guid.Parse(Id));
        var request3 = new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 0);
        var request4 = new TypologyBriefRequest(4, new PrototypeLot(12, 12), 6, 0);
        var tunedRequest = new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 2, Weights: new ScoringWeights(0.5, 0.25, 0.15, 0.1, 0.1, 0.2));
        byte[] Document(TypologyPipelineResult result) => Utf8(TypologyPipelineSerializer.Serialize(result));
        byte[] Expect(string label, string requestText, byte[] library, string? pin = null)
        {
            var requestPath = Input("request.json", requestText);
            var response = Call(label, requestPath);
            var cli = Cli(requestPath);
            var text = Utf8(Text(response));
            Check(!IsError(response) && text.SequenceEqual(library), label + ": the text is the in-process pipeline document byte for byte | " + Short(Text(response)));
            Check(cli.Exit == 0 && cli.File is not null && text.SequenceEqual(cli.File), label + ": the text is byte-equal to the CLI output file | exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli.Out));
            if (pin is not null) Check(Hex(text) == pin, label + ": the pinned SHA-256 " + pin[..8] + "... | " + Hex(text));
            return text;
        }
        var accepted = Expect("family 3 on 16 x 16 (TypologyPipeline.Run)", Request(TypologyPack, 3, 16, 16, 4, 0), Document(TypologyPipeline.Run(pack, request3, options)), PinnedAcceptedSha256);
        Expect("family 4 on 12 x 12, infeasible (a result, not an error)", Request(TypologyPack, 4, 12, 12, 6, 0), Document(TypologyPipeline.Run(pack, request4, options)), PinnedInfeasibleSha256);
        var reordered = Expect("member order, whitespace, -0 and weights {}", "{\n \"project_id\": \"" + Id + "\", \"seed\": -0, \"alternatives\": 4,\n \"lot\": {\"depth_m\": 16, \"width_m\": 16.0},"
            + " \"weights\": {}, \"family_size\": 3, \"pack\": \"" + TypologyPack + "\"\n}\n", Document(TypologyPipeline.Run(pack, request3, options)));
        Check(reordered.SequenceEqual(accepted), "the reordered request gives the family-3 bytes");
        Expect("partial weights and another seed", Request(TypologyPack, 3, 16, 16, 4, 2, ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5}"), Document(TypologyPipeline.Run(pack, tunedRequest, options)));
        var composed = Expect("\"skills\":[] (TypologyPipeline.RunComposed, CABA + typology)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":[]"),
            Document(TypologyPipeline.RunComposed(pack, request3, options, Array.Empty<SkillManifest>(), null)), PinnedComposedSha256);
        Check(!composed.SequenceEqual(accepted), "the composed document differs from the P7-14 one (composed mode really reached)");
        Expect("precedence alone selects composed mode", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":" + Ids(TypologyPack, CodePack)),
            Document(TypologyPipeline.RunComposed(pack, request3, options, null, new[] { TypologyPack, CodePack })), PinnedComposedSha256);
        Expect("skills and precedence with tuned weights", Request(TypologyPack, 3, 16, 16, 4, 2, ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"precedence\":"
            + Ids(CompactStyle, TypologyPack) + ",\"skills\":" + Ids(CompactStyle)), Document(TypologyPipeline.RunComposed(pack, tunedRequest, options, new[] { compact }, new[] { CompactStyle, TypologyPack })));
        Expect("an extra style pack on the family-4 lot", Request(TypologyPack, 4, 12, 12, 6, 0, ",\"skills\":" + Ids(CompactStyle)),
            Document(TypologyPipeline.RunComposed(pack, request4, options, new[] { compact }, null)));

        // ---- 2. refusals: isError, byte-equal to the CLI stdout line, no CLI file ----
        string Refused(string label, string requestPath, string? expected = null)
        {
            var response = Call(label, requestPath);
            var cli = Cli(requestPath);
            Check(IsError(response) && cli.Exit == 1 && cli.File is null && Text(response) == cli.Out && (expected is null || cli.Out == expected),
                "refuses " + label + " byte-equal to the CLI envelope | " + Short(Text(response)) + " / " + Short(cli.Out));
            return Text(response);
        }
        static string Code(string code, string? detail = null) => detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
        static string RefusedEnvelope(TypologyPipelineResult result) =>
            JsonSerializer.Serialize(new { status = "failed", code = "generate_typology_refused", refusals = result.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) });
        Refused("malformed JSON", Input("bad.json", "{"), Code("generate_typology_request_invalid", "json"));
        Refused("an unknown member", Input("unknown.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"revision\":0")), Code("generate_typology_request_invalid", "request.revision"));
        Refused("a missing pack", Input("no-pack.json", "{\"family_size\":3}"), Code("generate_typology_request_invalid", "pack"));
        Refused("skills that is not an array", Input("skills-object.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":{}")), Code("generate_typology_request_invalid", "skills"));
        Refused("a malformed precedence entry", Input("precedence-bad.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":[\"" + TypologyPack + "\",\"Not An Id\"]")),
            Code("generate_typology_request_invalid", "precedence[1]"));
        Refused("65 skills (over the limit)", Input("skills-65.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(Enumerable.Repeat(CompactStyle, 65).ToArray()))),
            Code("generate_typology_request_invalid", "skills"));
        Refused("an unknown typology pack", Input("unknown-pack.json", Request("xx.no-such.pack", 3, 16, 16, 4, 0)), Code("generate_typology_pack_unavailable", "input_not_found"));
        Refused("an unknown extra skill", Input("unknown-skill.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle, "xx.no-such.skill"))),
            JsonSerializer.Serialize(new { status = "failed", code = "generate_typology_skill_unavailable", detail = "input_not_found", skill = "xx.no-such.skill" }));
        var conflict = TypologyPipeline.RunComposed(pack, request3, options, new[] { compact, masonry }, null);
        Check(conflict.Status == PrototypePipelineStatus.Refused && conflict.Refusals.Any(r => r.Code == SkillCodes.SkillConflict), "fixture: two style packs conflict in process (skill_conflict)");
        Refused("two conflicting style packs (resolution refusal)", Input("conflict.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle, MasonryStyle))), RefusedEnvelope(conflict));
        var unknownPrecedence = TypologyPipeline.RunComposed(pack, request3, options, null, new[] { "xx.no-such.skill" });
        Refused("a precedence entry that is not a composed skill", Input("precedence-unknown.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":" + Ids("xx.no-such.skill"))),
            RefusedEnvelope(unknownPrecedence));
        Check(unknownPrecedence.Refusals.Any(r => r.Code == SkillCodes.PrecedenceUnknown), "fixture: the unknown precedence entry is precedence_unknown in process");
        var family0 = TypologyPipeline.Run(pack, new TypologyBriefRequest(0, new PrototypeLot(16, 16), 4, 0), options);
        Check(family0.Status == PrototypePipelineStatus.Refused, "fixture: family size 0 is refused in process");
        Refused("family size 0 (pipeline refusal)", Input("family-0.json", Request(TypologyPack, 0, 16, 16, 4, 0)), RefusedEnvelope(family0));
        Refused("a missing request file", Path.Combine(inputs, "absent.json"), Code("input_not_found"));
        Check(!File.Exists(Path.Combine(inputs, "absent.json")), "creates no missing request");
        var directory = Path.Combine(inputs, Next() + "-a-directory.json");
        Directory.CreateDirectory(directory);
        Refused("a directory as request", directory, Code("input_not_regular_file"));
        Refused("a request over the 8 MiB input cap", Input("huge.json", new string(' ', 8 * 1024 * 1024 + 1)), Code("input_too_large"));

        // ---- 4. MCP-only path guard and argument errors ----
        var pathInvalid = Code(GenerateTypologyTool.PathInvalid, "request");
        Check(GenerateTypologyTool.PathInvalid == "generate_typology_path_invalid", "the MCP-only path refusal code is generate_typology_path_invalid");
        foreach (var (label, bad) in new[] { ("empty", ""), ("blank", "   "), ("with NUL", "a\0b"), ("of 5000 characters", new string('x', 5000)) })
        {
            var response = Call("request_path " + label, bad);
            Check(IsError(response) && Text(response) == pathInvalid, "refuses a request_path " + label + " as generate_typology_path_invalid | " + Short(Text(response)));
        }
        var surrogate = call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"generate_typology\",\"arguments\":{\"request_path\":\"\\ud800\"}}}");
        Check(IsError(surrogate) && Text(surrogate) == pathInvalid && Environment.ExitCode == exitBefore, "a lone-surrogate request_path is generate_typology_path_invalid, never a crash | " + Short(Text(surrogate)));
        var okRequest = Input("ok.json", Request(TypologyPack, 3, 16, 16, 4, 0));
        Check(ErrorCode(call(ToolLine(new JsonObject()))) == -32602, "without request_path returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["request_path"] = 1 }))) == -32602, "a non-string request_path returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["request_path"] = okRequest, ["output_path"] = Path.Combine(inputs, "never.json") }))) == -32602 && !File.Exists(Path.Combine(inputs, "never.json")),
            "takes no output_path (-32602): it never writes a file");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["request"] = JsonNode.Parse(Request(TypologyPack, 3, 16, 16, 4, 0)) }))) == -32602, "takes no inline request object (-32602)");

        // ---- 5. the real Release Bim.Mcp.dll over stdio ----
        var composedRequest = Input("process.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":[]"));
        var processOut = mcpProcess(ToolLine(new JsonObject { ["request_path"] = composedRequest }) + "\n");
        var processResponse = JsonNode.Parse(processOut.Split('\n')[0])!.AsObject();
        Check(!IsError(processResponse) && Utf8(Text(processResponse)).SequenceEqual(composed) && Hex(Utf8(Text(processResponse))) == PinnedComposedSha256,
            "the real Bim.Mcp.dll process returns the composed pin a8c62da5... (packs found from the server binary) | " + Short(processOut));

        // ---- 6. static: the shared parser, linked, not copied ----
        // Comments stripped: /* */ blocks first, then // to end of line.
        static string CodeOf(string text) => string.Join("\n", Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline).Split('\n')
            .Select(line => { var at = line.IndexOf("//", StringComparison.Ordinal); return at < 0 ? line : line[..at]; }));
        var toolPath = Path.Combine(root, "interfaces", "mcp", "GenerateTypologyTool.cs");
        Check(File.Exists(toolPath), "interfaces/mcp/GenerateTypologyTool.cs exists");
        var toolCode = CodeOf(File.ReadAllText(toolPath));
        Check(toolCode.Contains("GenerateTypologyCommand.Document(", StringComparison.Ordinal), "the tool runs the CLI verb's shared GenerateTypologyCommand.Document");
        var inlined = new[] { "JsonDocument", "Utf8JsonReader", "\"family_size\"", "\"skills\"", "\"precedence\"", "\"weights\"", "TypologyPipeline", "TypologyBriefRequest", "ScoringWeights",
            "SkillPackLookup", "SkillParser", "SkillResolver", "BoundedInput", "File.", "FileStream", "Directory.", "GltfExporter", "Console.SetOut" }
            .Where(api => toolCode.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(inlined.Length == 0, "the tool inlines no request parser, pipeline, pack lookup, file access or Console capture [" + string.Join(", ", inlined) + "]");
        var owners = Directory.GetFiles(Path.Combine(root, "interfaces", "mcp"), "*.cs")
            .Where(f => new[] { "\"family_size\"", "TypologyPipeline.", "TypologyBriefRequest(" }.Any(api => CodeOf(File.ReadAllText(f)).Contains(api, StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f)).ToArray();
        Check(owners.Length == 0, "no interfaces/mcp source parses a typology request or runs the pipeline itself [" + string.Join(", ", owners) + "]");
        var commandCode = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateTypologyCommand.cs")));
        Check(Regex.Matches(commandCode, @"Generate\(ReadRequest\(").Count == 2 && Regex.Matches(commandCode, @"\bParse\(text\)").Count == 1,
            "the CLI verb and Document share one path: both call Generate(ReadRequest(...)), and the request is parsed in one place");
        foreach (var project in new[] { Path.Combine(root, "interfaces", "mcp", "Bim.Mcp.csproj"), Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj") })
        {
            var projectDir = Path.GetDirectoryName(project)!;
            var shared = Path.GetFullPath(Path.Combine(root, "interfaces", "cli", "GenerateTypologyCommand.cs"));
            var linked = System.Xml.Linq.XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "Compile")
                .Select(e => (string?)e.Attribute("Include")).Where(include => include is not null)
                .Any(include => string.Equals(Path.GetFullPath(Path.Combine(projectDir, include!.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))), shared,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Check(linked, Path.GetFileName(project) + " compiles interfaces/cli/GenerateTypologyCommand.cs (shared source, not a copy)");
        }
    }
}
