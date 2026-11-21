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
//   7. P7-19: "include":[] with a non-empty skills list is the tool text, CLI file and CLI stdout of the run without include;
//      a duplicate include member stays request_invalid (request.include) in three member orders.
//   8. P3-32: with a throwing or disposed stderr on Document's catch-all (and InternalRefusal) the in-process tool is
//      isError with the typed generate_typology_internal_error line, the diagnostic attempted once, nothing escapes.
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
        // P7-16: include and code_pack reach TypologyPipeline.Run through the shared parser.
        var exampleCode = Skill("xx.example.codigo-ejemplo");
        var includeCode = Expect("include [study] with code_pack xx.example.codigo-ejemplo (P7-16)",
            Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Ids("study") + ",\"code_pack\":\"xx.example.codigo-ejemplo\""),
            Document(TypologyPipeline.Run(pack, new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 0, new[] { "study" }), new PrototypePipelineOptions(exampleCode, Guid.Parse(Id)))));
        Check(!includeCode.SequenceEqual(accepted), "the include + code_pack document differs from the P7-14 one (both members really reached)");
        // P7-18: "include":[] is the absent member through the shared parser (plain and composed pins).
        var includeEmpty = Expect("\"include\":[] is the absent member (P7-18)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":[]"),
            Document(TypologyPipeline.Run(pack, request3, options)), PinnedAcceptedSha256);
        Check(includeEmpty.SequenceEqual(accepted), "\"include\":[] gives the P7-14 family-3 document");
        Expect("\"include\":[] on the infeasible family-4 lot (P7-18)", Request(TypologyPack, 4, 12, 12, 6, 0, ",\"include\":[]"),
            Document(TypologyPipeline.Run(pack, request4, options)), PinnedInfeasibleSha256);
        Expect("\"include\":[] with \"skills\":[] (P7-18)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":[],\"skills\":[]"),
            Document(TypologyPipeline.RunComposed(pack, request3, options, Array.Empty<SkillManifest>(), null)), PinnedComposedSha256);
        // P7-19: "include":[] with a NON-EMPTY skills list: the tool text equals the text without include, the in-process
        // RunComposed with Include absent and the CLI output file; the CLI stdout of both requests is the same line. In process
        // Include: [] is recorded and differs, so only the shared parser's normalisation makes them equal.
        foreach (var (label, absentExtra, emptyExtra, request, extras, precedence, skillIds) in new (string, string, string, TypologyBriefRequest, SkillManifest[], string[]?, string[])[]
        {
            ("skills [compact-modern] on family 3", ",\"skills\":" + Ids(CompactStyle), ",\"skills\":" + Ids(CompactStyle) + ",\"include\":[]",
                request3, new[] { compact }, null, new[] { CompactStyle }),
            ("include [] before skills [compact-modern] on the family-4 lot", ",\"skills\":" + Ids(CompactStyle), ",\"include\":[],\"skills\":" + Ids(CompactStyle),
                request4, new[] { compact }, null, new[] { CompactStyle }),
            ("skills naming the code and typology packs", ",\"skills\":" + Ids(CodePack, TypologyPack), ",\"skills\":" + Ids(CodePack, TypologyPack) + ",\"include\":[]",
                request3, new[] { caba, pack }, null, new[] { CodePack, TypologyPack }),
            ("skills [compact-modern], precedence and tuned weights", ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"precedence\":" + Ids(CompactStyle, TypologyPack) + ",\"skills\":" + Ids(CompactStyle),
                ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"precedence\":" + Ids(CompactStyle, TypologyPack) + ",\"include\":[],\"skills\":" + Ids(CompactStyle),
                tunedRequest, new[] { compact }, new[] { CompactStyle, TypologyPack }, new[] { CompactStyle }),
        })
        {
            var lot = request.Lot;
            string RequestText(string extra) => Request(TypologyPack, request.FamilySize, lot.WidthMetres, lot.DepthMetres, request.Alternatives, request.Seed, extra);
            var library = TypologyPipeline.RunComposed(pack, request, options, extras, precedence);
            var emptyLibrary = TypologyPipeline.RunComposed(pack, request with { Include = Array.Empty<string>() }, options, extras, precedence);
            Check(library.Status != PrototypePipelineStatus.Refused && !Document(emptyLibrary).SequenceEqual(Document(library)),
                label + ": fixture: a result, and in process Include: [] differs from the absent member (P7-19)");
            var absentText = RequestText(absentExtra);
            var emptyText = RequestText(emptyExtra);
            var absentDocument = Expect(label + " without include (P7-19)", absentText, Document(library));
            var emptyDocument = Expect(label + " with \"include\":[] (P7-19)", emptyText, Document(library));
            Check(emptyDocument.SequenceEqual(absentDocument), label + ": the \"include\":[] tool text is the text without include byte for byte (P7-19)");
            var absentCli = Cli(Input("p7-19-absent.json", absentText));
            var emptyCli = Cli(Input("p7-19-empty.json", emptyText));
            Check(absentCli.Exit == 0 && emptyCli.Exit == 0 && emptyCli.Out == absentCli.Out && absentCli.File is not null && emptyCli.File is not null
                && emptyCli.File.SequenceEqual(absentCli.File) && absentCli.Out.Contains("\"skills\":" + Ids(skillIds), StringComparison.Ordinal),
                label + ": the CLI file and stdout are the same with and without \"include\":[] (P7-19) | " + Short(absentCli.Out) + " / " + Short(emptyCli.Out));
        }

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
        // P7-19: a duplicate include member stays request_invalid with a non-empty skills list.
        Refused("a duplicate empty include member with skills (P7-19)", Input("include-duplicate.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle) + ",\"include\":[],\"include\":[]")),
            Code("generate_typology_request_invalid", "request.include"));
        Refused("a duplicate include member ([study] then []) with skills (P7-19)", Input("include-duplicate-2.json",
            Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle) + ",\"include\":[\"study\"],\"include\":[]")), Code("generate_typology_request_invalid", "request.include"));
        Refused("a duplicate include member ([] then [study]) before skills (P7-19)", Input("include-duplicate-3.json",
            Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":[],\"include\":[\"study\"],\"skills\":" + Ids(CompactStyle))), Code("generate_typology_request_invalid", "request.include"));
        Refused("a code_pack that is not a code skill (P7-16)", Input("code-pack-kind.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + CompactStyle + "\"")),
            Code("generate_typology_code_pack_invalid", "kind"));
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
        // P4-26: Execute (the verb's Generate(ReadRequest(...)) call) lives in the CLI-only write part GenerateTypologyWrite.cs.
        var commandCode = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateTypologyCommand.cs")) + "\n"
            + File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateTypologyWrite.cs")));
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

        // ---- 8. P3-32: a throwing stderr on Document's catch-all, through the in-process server ----
        ThrowingStderrRun(work, call, check);
    }

    // P3-32: the in-process McpServer (Program.cs's server.Handle) runs generate_typology while Console.Error throws on the
    // diagnostic. Document's own catch-all (UnguardedWorkProbe, the parse / pack-load stretch) and the guarded block's
    // InternalRefusal (GuardedWorkProbe) are forced with stderr a RejectingWriter (IOException, non-IO
    // InvalidOperationException, ObjectDisposedException; attempts counted) or a disposed StringWriter: the response is a
    // tools/call result (no JSON-RPC error, nothing escapes Handle), isError true, the text exactly the typed
    // generate_typology_internal_error line (IntentPlan.Internal("generate-typology", e).Line, never McpServer's generic
    // Failed carrying the stderr or original exception message), the diagnostic attempted exactly once, stdout never
    // written, the exit code untouched; Console and both seams restored in finally. Controls: an accepting stderr gets the
    // diagnostic once with the same text; with the seams reset the next call answers the P5-12 pinned success bytes.
    static void ThrowingStderrRun(string work, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "generate_typology P3-32 throwing stderr: " + name);
        static string Short(string text) => text.Length > 400 ? text[..400] + "..." : text;
        static string Hex(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();
        static string? TextOf(JsonObject? response) => response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        static bool? IsErrorOf(JsonObject? response) => response?["result"]?["isError"]?.GetValue<bool>();
        var nl = Environment.NewLine;
        var scratch = Path.Combine(work, "p3-32-generate-typology-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var requestPath = Path.Combine(scratch, "request.json");
            File.WriteAllText(requestPath, "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}", new UTF8Encoding(false));
            var line = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 32, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = GenerateTypologyTool.Name, ["arguments"] = new JsonObject { [GenerateTypologyTool.RequestParameter] = requestPath } } }.ToJsonString();
            bool SeamsNull() => Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe is null && Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe is null;
            Check(SeamsNull(), "fixture: the generate_typology unguarded and guarded test seams are null unless a test sets them");
            var baseline = call(line);
            Check(IsErrorOf(baseline) == false && TextOf(baseline) is { } baseText && Hex(baseText) == PinnedAcceptedSha256,
                "fixture: with the seams unset the tool answers the P5-12 pinned document | " + Short(TextOf(baseline) ?? baseline.ToJsonString()));

            var failure = new InvalidOperationException("boom generate_typology P3-32 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}"
                && envelope.Diagnostic == "generate-typology internal error: System.InvalidOperationException: boom generate_typology P3-32 original",
                "fixture: IntentPlan.Internal(\"generate-typology\", e) is the literal typed line and diagnostic | " + envelope.Line + " / " + envelope.Diagnostic);
            const string StderrMessage = "p3-32 stderr rejects the diagnostic";

            // One tools/call with Console.Out / Console.Error replaced and the seams set; all restored in finally.
            (JsonObject? Response, Exception? Escaped, string Stdout, int Exit, bool Restored) Forced(TextWriter stderr, Action? before, Action? guardedWork)
            {
                var stdout = new StringWriter(CultureInfo.InvariantCulture);
                var savedOut = Console.Out;
                var savedErr = Console.Error;
                var savedExit = Environment.ExitCode;
                JsonObject? response = null;
                Exception? escaped = null;
                int exit;
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe = before;
                    Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe = guardedWork;
                    try { response = call(line); }
                    catch (Exception error) { escaped = error; }
                    finally
                    {
                        Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe = null;
                        Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe = null;
                    }
                    exit = Environment.ExitCode;
                }
                finally
                {
                    Console.SetOut(savedOut);
                    Console.SetError(savedErr);
                    Environment.ExitCode = savedExit;
                }
                var restored = ReferenceEquals(Console.Out, savedOut) && ReferenceEquals(Console.Error, savedErr) && Environment.ExitCode == savedExit && SeamsNull();
                return (response, escaped, stdout.ToString(), exit - savedExit, restored);
            }
            string D((JsonObject? Response, Exception? Escaped, string Stdout, int Exit, bool Restored) r) =>
                "response " + Short(r.Response?.ToJsonString() ?? "<none>") + " escaped " + (r.Escaped is { } e ? e.GetType().FullName + ": " + e.Message : "nothing")
                + " stdout [" + Short(r.Stdout) + "] exit delta " + r.Exit.ToString(CultureInfo.InvariantCulture) + " restored " + r.Restored;
            // A tools/call result (not a JSON-RPC error), isError, exactly the typed line, no exception message in it.
            bool Typed((JsonObject? Response, Exception? Escaped, string Stdout, int Exit, bool Restored) r) =>
                r.Escaped is null && r.Restored && r.Exit == 0 && r.Stdout.Length == 0 && r.Response is { } response && response["error"] is null
                && IsErrorOf(response) == true && TextOf(response) is { } text && text == envelope.Line
                && !text.Contains(StderrMessage, StringComparison.Ordinal) && !text.Contains(failure.Message, StringComparison.Ordinal);

            var paths = new (string Label, Action? Before, Action? GuardedWork)[]
            {
                ("Document's own catch-all (UnguardedWorkProbe)", () => throw failure, null),
                ("the guarded block's InternalRefusal (GuardedWorkProbe)", null, () => throw failure),
            };
            foreach (var (path, before, guardedWork) in paths)
            {
                {
                    var accepting = new StringWriter(CultureInfo.InvariantCulture);
                    var r = Forced(accepting, before, guardedWork);
                    Check(Typed(r) && accepting.ToString() == envelope.Diagnostic + nl,
                        "control: " + path + " with an accepting stderr is isError with the typed internal-error line, the diagnostic written once | " + D(r) + " stderr [" + Short(accepting.ToString()) + "]");
                }
                foreach (var (kind, make) in new (string, Func<Exception>)[]
                {
                    ("IOException", () => new IOException(StderrMessage)),
                    ("InvalidOperationException", () => new InvalidOperationException(StderrMessage)),
                    ("ObjectDisposedException", () => new ObjectDisposedException(StderrMessage)),
                })
                {
                    var rejecting = new RejectingWriter(make());
                    var r = Forced(rejecting, before, guardedWork);
                    Check(Typed(r) && rejecting.Attempts == 1,
                        path + " + a stderr throwing " + kind + " on the diagnostic: nothing escapes McpServer, isError with the typed internal-error line (no exception message), the diagnostic attempted once, stdout never written, exit untouched | "
                        + D(r) + " stderr attempts " + rejecting.Attempts.ToString(CultureInfo.InvariantCulture));
                }
                {
                    var closed = new StringWriter(CultureInfo.InvariantCulture);
                    closed.Dispose();
                    var r = Forced(closed, before, guardedWork);
                    Check(Typed(r), path + " + a disposed StringWriter as stderr: nothing escapes McpServer, isError with the typed internal-error line, stdout never written, exit untouched | " + D(r));
                }
            }

            var after = call(line);
            Check(SeamsNull() && IsErrorOf(after) == false && TextOf(after) is { } afterText && afterText == TextOf(baseline) && Hex(afterText) == PinnedAcceptedSha256,
                "the seams are reset: the next call answers the unchanged pinned success bytes | " + Short(TextOf(after) ?? after.ToJsonString()));
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
