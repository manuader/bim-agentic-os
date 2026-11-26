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
//   9. P3-34: the real Release Bim.Mcp.dll process over stdio, the catch-all forced through a startup hook, with a stderr
//      pipe the reader closed or a throwing / disposed stderr: the typed isError text, the diagnostic attempted once; the
//      in-process disposed-StringWriter case counts its one attempt. P3-36: every process case runs through one launch helper
//      with a per-case and a total limit (case count asserted), a reader-side partial stdout, the closed-pipe fact per OS.
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
            if (!process.WaitForExit(180000))
            {
                // P3-40: contained as Server's (section 9): the process may exit, or the kill be denied, after the timeout.
                try { process.Kill(true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw new Exception("CLI timed out: generate-typology " + requestPath);
            }
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

        // ---- 9. P3-34: the same over stdio from the real Release Bim.Mcp.dll process, and the disposed stderr's attempt ----
        StdioStderrRun(root, work, dotnet, call, check);
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

    // P3-34. a. In process (as section 8): the disposed-StringWriter stderr now sits behind an AttemptWriter, so the one
    // diagnostic attempt is counted, on Document's catch-all (UnguardedWorkProbe) and on InternalRefusal (GuardedWorkProbe):
    // isError, exactly the typed line, attempts exactly [the diagnostic], nothing escapes, stdout unwritten, exit untouched.
    // b. The real Release interfaces/mcp/bin/Release/net8.0/Bim.Mcp.dll (dotnet, stdin one tools/call line then EOF). The
    // production seams are null and have no switch, so the catch-all is forced by DOTNET_STARTUP_HOOKS naming this test
    // assembly: the runtime calls StartupHook.Initialize() (below) inside the server process before Main, and it sets
    // UnguardedWorkProbe or GuardedWorkProbe (by reflection on the loaded Bim.Mcp assembly) to throw, and optionally replaces
    // Console.Error with an AttemptWriter that logs every write attempt to a file: forwarding to the real stderr ("pipe"),
    // throwing IOException / InvalidOperationException, or forwarding to a disposed StringWriter. stderr is either read
    // (accepting) or its pipe is closed by the reader right after start. Oracle for each forced run: exit 0, stdout exactly
    // one JSON-RPC line, a tools/call result (no "error"), isError true, the text exactly
    // {"status":"failed","code":"generate_typology_internal_error"} (no exception message); the log is the hook line then
    // exactly one attempt, the diagnostic IntentPlan.InternalDiagnostic("generate-typology", e). Measured: a write to the
    // closed stderr pipe does not throw (the runtime's console stream drops EPIPE / ERROR_NO_DATA), so the closed pipe
    // alone gives the same result as an accepting one; the throwing and disposed writers stand in for a stderr that does
    // throw. Controls: without the hook, and with the hook but no seam, a closed stderr pipe still answers the P5-12 pinned
    // success bytes with an empty log of attempts; an accepting stderr receives exactly the diagnostic line.
    // P3-36. Every process case is one row of a case table run by one loop through one launch helper (Server): CaseCount
    // rows with distinct keys, each waiting at most PerCaseMs and none past the shared TotalMs deadline (a late case is
    // killed and reported, never thrown); the contract states the same three numbers. Partial stdout in the real process:
    // the reader takes the first half of the success line or of the typed line (or no byte) and closes its stdout pipe;
    // exit 0, the bytes read are that line's prefix, stderr only the forced diagnostic. The server's stdout is an AutoFlush
    // StreamWriter over the console stream (interfaces/mcp/Program.cs), whose write to a closed pipe is dropped like
    // stderr's, so the server never sees the cut: the P3-31 "accept part of a line, then throw" path is unreachable in the
    // real MCP process and stays covered in process (tests/cli section 14). The closed-pipe "does not throw" fact is per
    // OS: measured on Linux and Windows (the CI jobs that run tests/mcp); macOS unmeasured (the macOS CI job runs only
    // tests/cli and tests/mirror-journal), so the case label names the OS it ran on.
    // P3-38. macOS measured: the macOS CI job runs this section alone (SectionNineOnly below, entered through the same
    // startup hook with SectionVariable=9, so no other tests/mcp section runs there), so the closed-pipe fact is measured
    // on Linux, Windows and macOS. The partial-stdout claim is split by size: the success case now takes CutBytes and
    // leaves more than PipeBufferBound (64 KiB: the default Linux pipe capacity, the macOS maximum; Windows' anonymous
    // pipe is smaller) unread, so the server's write cannot have completed into the buffer before the reader closed and
    // the server did meet the cut (and dropped it); the typed line is shorter than the bound (asserted), so it may sit
    // whole in the pipe buffer before the close and that case pins only the reader-side prefix, exit 0 and the one
    // diagnostic, not that the server met the cut. Server's Kill also contains Win32Exception (the process may exit or
    // be denied between the timeout and the kill).
    // P3-40. The 64 KiB bound is measured (Capacity: an unread stdout pipe of a hooked Bim.Mcp.dll filled in 1 KiB and
    // 1 MiB writes, counted after the blocked writer is killed) and cited (Linux pipe(7), xnu sys/pipe.h); the
    // before-any-byte case closes stdout before the request is written (the server cannot have written first); every kill
    // in this file, the CLI helper's included, contains InvalidOperationException and Win32Exception (pinned); the macOS
    // step uploads its output and posts a notice and builds interfaces/mcp and tests/mcp once each (checked here).
    // P3-42. The 1 MiB count must be above 0 on Linux and macOS (a blocking write there moves what fits into the pipe
    // first); only Windows accepts 0 (a write larger than the anonymous pipe's free buffer stays pending whole). Every OS
    // posts its measured capacity as an Actions notice (Linux and Windows through tests/mcp, macOS through section 9). The
    // two capacity runs share an explicit CapacityMs budget besides the cases' TotalMs. The contract records the Windows
    // figures and the P3-40 runs; the macOS step's notice grep is guarded so pipefail cannot fail it when nothing matches.
    // P3-44. The macOS notice grep matches the capacity check's PASS line and the closed-pipe lines only (not the P3-40
    // contract check, whose name also says "measured pipe capacity"); macOS shows its capacity twice, stated: the test's
    // mcp-pipe-capacity-macOS notice and the step's notice of the capacity PASS line. BareGrepPipes scans the P3-38 step
    // alone and flags an unguarded grep anywhere in a pipeline or inside $(...). Each capacity run must finish within
    // CapacityRunMs (the 1.5 s settle plus a stated slack); the budget check keeps only non-redundant comparisons.
    // P3-46. BareGrepPipes masks quoted text (a `)` or `}` inside a double-quoted argument no longer ends the command) and
    // accepts `|| true` after the whole pipeline for a grep inside it; a grep piped inside $(...) has its own self-tests.
    // The notice-grep check runs over the names this section's checks printed in the run, not literal samples; each
    // capacity run's margin under CapacityRunMs is printed in the capacity notice and the budget check.
    // Duplication recorded (not removed): AttemptWriter overlaps tests/cli's RejectingWriter (linked here) and the
    // CountingWriter nested in tests/cli/GenerateTypologyTests.cs. It stays separate because it must run inside the server
    // process (this assembly is the startup hook) and report its attempts across the process boundary through a log file,
    // which neither in-memory counter does, and it records an inner writer's own failure; CountingWriter is private to the
    // Bim.Cli.Tests assembly. The overlap is checked instead: per overload AttemptWriter rejects exactly as RejectingWriter
    // (one attempt, the same exception) and forwards byte-exact as a plain writer.
    static void StdioStderrRun(string root, string work, string dotnet, Func<string, JsonObject> call, Action<bool, string> check)
    {
        // P3-46: every name this section's checks print (after the prefix), in order, for the notice-grep check below.
        var names = new List<string>();
        void Check(bool condition, string name) { names.Add(name); check(condition, "generate_typology P3-34 stdio stderr: " + name); }
        static string Short(string text) => text.Length > 400 ? text[..400] + "..." : text;
        static string Hex(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();
        static string? TextOf(JsonObject? response) => response?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        static bool? IsErrorOf(JsonObject? response) => response?["result"]?["isError"]?.GetValue<bool>();
        var nl = Environment.NewLine;
        // P3-46: the names of the checks the macOS step's notice grep must post (the closed-pipe cases and the capacity check).
        var noticed = new HashSet<string>(StringComparer.Ordinal);
        var scratch = Path.Combine(work, "p3-34-generate-typology-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var requestPath = Path.Combine(scratch, "request.json");
            File.WriteAllText(requestPath, "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}", new UTF8Encoding(false));
            var line = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 34, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = GenerateTypologyTool.Name, ["arguments"] = new JsonObject { [GenerateTypologyTool.RequestParameter] = requestPath } } }.ToJsonString();
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", new InvalidOperationException(StartupHook.FailureMessage));
            var diagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-typology", new InvalidOperationException(StartupHook.FailureMessage));
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}"
                && diagnostic == "generate-typology internal error: System.InvalidOperationException: " + StartupHook.FailureMessage && envelope.Diagnostic == diagnostic,
                "fixture: the typed line and the diagnostic of the hook's failure | " + envelope.Line + " / " + diagnostic);
            bool Typed(string? text, bool? isError) => isError == true && text is { } t && t == envelope.Line
                && !t.Contains(StartupHook.FailureMessage, StringComparison.Ordinal) && !t.Contains(StartupHook.StderrMessage, StringComparison.Ordinal);

            // ---- a. In process: the disposed StringWriter's one attempt ----
            foreach (var (path, before, guardedWork) in new (string, Action?, Action?)[]
            {
                ("Document's own catch-all (UnguardedWorkProbe)", () => throw new InvalidOperationException(StartupHook.FailureMessage), null),
                ("the guarded block's InternalRefusal (GuardedWorkProbe)", null, () => throw new InvalidOperationException(StartupHook.FailureMessage)),
            })
            {
                var closed = new StringWriter(CultureInfo.InvariantCulture);
                closed.Dispose();
                var attempts = new List<string>();
                var stderr = new AttemptWriter(closed, null, attempts.Add);
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
                var restored = ReferenceEquals(Console.Out, savedOut) && ReferenceEquals(Console.Error, savedErr) && Environment.ExitCode == savedExit
                    && Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe is null && Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe is null;
                Check(escaped is null && restored && exit == savedExit && stdout.ToString().Length == 0 && response is { } r && r["error"] is null && Typed(TextOf(r), IsErrorOf(r))
                    && attempts.SequenceEqual(new[] { "attempt:" + AttemptWriter.Escape(diagnostic), "forward-threw:System.ObjectDisposedException" }),
                    "in process, " + path + " + a disposed StringWriter as stderr behind an AttemptWriter: isError with the typed internal-error line, the diagnostic attempted exactly once (then ObjectDisposedException), nothing escapes, stdout unwritten, exit untouched | response "
                    + Short(response?.ToJsonString() ?? "<none>") + " escaped " + (escaped?.GetType().FullName ?? "nothing") + " attempts [" + string.Join(" | ", attempts) + "] restored " + restored);
            }

            // ---- P3-36: AttemptWriter against the shared RejectingWriter and a plain forward (the recorded duplication) ----
            // Each overload is one attempt: with a failure it rejects exactly as RejectingWriter (one attempt, the same
            // exception instance thrown); with an inner writer it forwards byte-exact and records the value once.
            foreach (var (overload, write, value) in new (string, Action<TextWriter>, string)[]
            {
                ("WriteLine(string)", w => w.WriteLine("ab"), "ab"),
                ("Write(string)", w => w.Write("ab"), "ab"),
                ("Write(char)", w => w.Write('a'), "a"),
                ("Write(char[], int, int)", w => w.Write("xaby".ToCharArray(), 1, 2), "ab"),
                ("Write(char[])", w => w.Write(new[] { 'a', 'b' }), "ab"),
                ("WriteLine()", w => w.WriteLine(), ""),
            })
            {
                var failure = new IOException(StartupHook.StderrMessage);
                var shared = new RejectingWriter(failure);
                var sharedThrew = Throws(() => write(shared));
                var log = new List<string>();
                var rejecting = new AttemptWriter(null, () => failure, log.Add);
                var attemptThrew = Throws(() => write(rejecting));
                var inner = new StringWriter(CultureInfo.InvariantCulture);
                var reference = new StringWriter(CultureInfo.InvariantCulture);
                var forwardLog = new List<string>();
                write(new AttemptWriter(inner, null, forwardLog.Add));
                write(reference);
                Check(ReferenceEquals(sharedThrew, failure) && ReferenceEquals(attemptThrew, failure) && shared.Attempts == 1 && log.Count == 1
                    && inner.ToString() == reference.ToString() && forwardLog.Count == 1 && forwardLog[0] == "attempt:" + AttemptWriter.Escape(value),
                    "P3-36 AttemptWriter " + overload + ": one attempt, rejected as the shared RejectingWriter, forwarded byte-exact | shared attempts " + shared.Attempts.ToString(CultureInfo.InvariantCulture)
                    + " log [" + string.Join(" | ", log) + "] forward [" + string.Join(" | ", forwardLog) + "] inner [" + AttemptWriter.Escape(inner.ToString()) + "]");
            }
            static Exception? Throws(Action action) { try { action(); return null; } catch (Exception error) { return error; } }

            // ---- b. The real Release Bim.Mcp.dll process over stdio ----
            var mcpDll = Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll");
            var hookAssembly = typeof(StartupHook).Assembly.Location;
            Check(File.Exists(mcpDll) && File.Exists(hookAssembly), "fixture: the Release Bim.Mcp.dll and this test assembly (the startup hook) exist | " + mcpDll + " / " + hookAssembly);
            if (!File.Exists(mcpDll) || !File.Exists(hookAssembly)) return;
            const int CaseCount = 17;
            const int PerCaseMs = 60000;
            const int TotalMs = 360000;
            var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "other";
            var clock = Stopwatch.StartNew();
            var runs = 0;
            // P3-36: the one launch helper every process case goes through. Stdin is the tools/call line then EOF. hook null =
            // no DOTNET_STARTUP_HOOKS; closeStderr closes the reader's end of the stderr pipe right after start; take null
            // reads stdout to EOF, take k reads exactly the first k bytes (fewer at EOF) and then closes the reader's end of
            // the stdout pipe (k = 0: closed before any byte). Each case waits at most PerCaseMs and never past the shared
            // TotalMs deadline; a case over either is killed and reported (InTime false), never thrown. Returns exit, stdout
            // (UTF-8; for a take the bytes read as Latin-1, one char per byte), stderr (null when closed) and the hook's log lines.
            (int Exit, string Out, string? Err, List<string> Log, bool InTime) Server((string Seam, string Stderr)? hook, bool closeStderr, int? take)
            {
                var log = Path.Combine(scratch, "hook-" + (++runs).ToString(CultureInfo.InvariantCulture) + ".log");
                var limit = (int)Math.Min(PerCaseMs, TotalMs - clock.ElapsedMilliseconds);
                if (limit <= 0) return (-1, "", null, new List<string>(), false);
                var start = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                start.ArgumentList.Add(mcpDll);
                start.Environment.Remove("DOTNET_STARTUP_HOOKS");
                foreach (var name in StartupHook.Variables) start.Environment.Remove(name);
                if (hook is { } h)
                {
                    start.Environment["DOTNET_STARTUP_HOOKS"] = hookAssembly;
                    start.Environment[StartupHook.SeamVariable] = h.Seam;
                    start.Environment[StartupHook.StderrVariable] = h.Stderr;
                    start.Environment[StartupHook.LogVariable] = log;
                }
                using var process = Process.Start(start) ?? throw new Exception("MCP process did not start: " + dotnet);
                Task<string>? stderr = null;
                if (closeStderr) process.StandardError.Close();
                else stderr = process.StandardError.ReadToEndAsync();
                Task<string> stdout;
                if (take is not { } k) stdout = process.StandardOutput.ReadToEndAsync();
                else if (k == 0)
                {
                    // P3-40: closed before any byte, enforced: the reader's end closes here, before the request is written,
                    // and the server writes nothing before it has read a request, so the close cannot follow a write.
                    process.StandardOutput.Close();
                    stdout = Task.FromResult("");
                }
                else
                {
                    var raw = process.StandardOutput.BaseStream;
                    stdout = Task.Run(() =>
                    {
                        var buffer = new byte[k];
                        var got = 0;
                        while (got < k && raw.Read(buffer, got, k - got) is var n && n > 0) got += n;
                        process.StandardOutput.Close();
                        return Encoding.Latin1.GetString(buffer, 0, got); // byte-preserving: the cut may split a UTF-8 sequence
                    });
                }
                try { process.StandardInput.Write(line + "\n"); process.StandardInput.Close(); } catch (IOException) { }
                if (!process.WaitForExit(limit))
                {
                    try { process.Kill(true); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    return (-1, "", null, new List<string>(), false);
                }
                process.WaitForExit();
                var rest = (int)Math.Max(1, Math.Min(PerCaseMs, TotalMs - clock.ElapsedMilliseconds));
                if (!stdout.Wait(rest) || (stderr is not null && !stderr.Wait(rest))) return (process.ExitCode, "", null, new List<string>(), false);
                var lines = File.Exists(log) ? File.ReadAllLines(log).ToList() : new List<string>();
                return (process.ExitCode, stdout.Result, stderr?.Result, lines, true);
            }
            static string D((int Exit, string Out, string? Err, List<string> Log, bool InTime) r) =>
                "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " in time " + r.InTime + " stdout [" + Short(r.Out) + "] stderr " + (r.Err is null ? "<closed>" : "[" + Short(r.Err) + "]") + " log [" + string.Join(" | ", r.Log) + "]";
            static JsonObject? Response(string stdout)
            {
                var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length != 1) return null;
                try { return JsonNode.Parse(lines[0]) as JsonObject; }
                catch (JsonException) { return null; }
            }
            bool Success((int Exit, string Out, string? Err, List<string> Log, bool InTime) r) =>
                r.InTime && r.Exit == 0 && Response(r.Out) is { } ok && ok["error"] is null && IsErrorOf(ok) == false && TextOf(ok) is { } text && Hex(text) == PinnedAcceptedSha256;
            bool TypedRun((int Exit, string Out, string? Err, List<string> Log, bool InTime) r) =>
                r.InTime && r.Exit == 0 && Response(r.Out) is { } typed && typed["error"] is null && Typed(TextOf(typed), IsErrorOf(typed));
            static string[] Log(params string[] entries) => entries;
            var attempt = "attempt:" + AttemptWriter.Escape(diagnostic);
            // Whole stdout lines of earlier cases, the expected prefixes of the partial-stdout cases (keyed by case key).
            var whole = new Dictionary<string, string>(StringComparer.Ordinal);
            byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
            int Half(string key) => whole.TryGetValue(key, out var full) ? Utf8(full).Length / 2 : 1;
            // P3-38: the success cut leaves more than the pipe buffer unread; the typed line fits in it.
            const int CutBytes = 4096;
            const int PipeBufferBound = 65536;
            int Length(string key) => whole.TryGetValue(key, out var full) ? Utf8(full).Length : -1;
            bool Prefix(string key, string read, int k) => whole.TryGetValue(key, out var full) && k > 0 && k < Utf8(full).Length
                && Encoding.Latin1.GetBytes(read).AsSpan().SequenceEqual(Utf8(full).AsSpan(0, k));

            // The case table (P3-36): every process case, run in this order by one loop through Server.
            var cases = new List<(string Key, string Label, (string Seam, string Stderr)? Hook, bool CloseStderr, Func<int>? Take,
                Func<(int Exit, string Out, string? Err, List<string> Log, bool InTime), bool> Oracle)>
            {
                // Controls: a closed stderr pipe without a forced failure answers the pinned success bytes.
                ("success", "control: the real process without the hook and a stderr pipe closed by the reader answers the P5-12 pinned success bytes, no stderr attempt",
                    null, true, null, r => Success(r) && r.Log.Count == 0),
                ("success-hook", "control: the real process with the hook but no seam (stderr forwarded to the closed pipe) and a stderr pipe closed by the reader answers the P5-12 pinned success bytes, no stderr attempt",
                    ("none", "pipe"), true, null, r => Success(r) && r.Log.SequenceEqual(Log("hook:none:pipe"))),
            };
            foreach (var (path, seam) in new[] { ("Document's own catch-all (UnguardedWorkProbe)", "unguarded"), ("the guarded block's InternalRefusal (GuardedWorkProbe)", "guarded") })
            {
                cases.Add(("typed-" + seam, "control: the real process, " + path + " forced, an accepting stderr pipe: isError with the typed internal-error line, stderr exactly the diagnostic line",
                    (seam, "none"), false, null, r => TypedRun(r) && r.Err == diagnostic + nl && r.Log.SequenceEqual(Log("hook:" + seam + ":none"))));
                foreach (var (label, stderrMode, closeStderr, tail) in new (string, string, bool, string[])[]
                {
                    ("the real stderr pipe closed by the reader (the write forwarded to it)", "pipe", true, Array.Empty<string>()),
                    ("the real stderr pipe closed by the reader and nothing replaced", "none", true, Array.Empty<string>()),
                    ("a stderr throwing IOException", "io", false, Array.Empty<string>()),
                    ("a stderr throwing InvalidOperationException", "invalid", false, Array.Empty<string>()),
                    ("a disposed StringWriter as stderr", "disposed", false, new[] { "forward-threw:System.ObjectDisposedException" }),
                })
                {
                    var expectedLog = new[] { "hook:" + seam + ":" + stderrMode }.Concat(stderrMode == "none" ? Array.Empty<string>() : new[] { attempt }).Concat(tail).ToArray();
                    cases.Add((seam + "-" + stderrMode, "the real process over stdio, " + path + " forced + " + label + ": nothing escapes, exit 0, one JSON-RPC line, isError with the typed internal-error line (no exception message)"
                        + (stderrMode == "none" ? "" : ", the diagnostic attempted exactly once") + (stderrMode == "pipe" ? " and the closed pipe's write did not throw (measured on " + os + ")" : ""),
                        (seam, stderrMode), closeStderr, null, r => TypedRun(r) && (r.Err is null || r.Err.Length == 0) && r.Log.SequenceEqual(expectedLog)));
                }
            }
            // P3-36: partial stdout in the real process. The reader takes a prefix of the response line's bytes (or none) and
            // closes its end of the stdout pipe; the server's stdout is an AutoFlush StreamWriter over the console stream,
            // whose write to a closed pipe is dropped (EPIPE / ERROR_NO_DATA): exit 0, nothing on stderr but the forced
            // diagnostic once. The P3-31 path (stdout accepts part of a line and then throws) is unreachable here; it stays
            // covered in process (tests/cli section 14). P3-38: only the success case proves the server met the cut (more
            // than PipeBufferBound left unread); the typed line fits the pipe buffer, so its case claims the prefix only.
            cases.Add(("partial-success", "P3-38 partial stdout: the real process without the hook, the reader takes the first " + CutBytes.ToString(CultureInfo.InvariantCulture)
                + " bytes of the success line and closes stdout with more than " + PipeBufferBound.ToString(CultureInfo.InvariantCulture) + " bytes unwritten (the server's write met the closed pipe): exit 0, the bytes read are the success line's prefix, stderr empty",
                null, false, () => CutBytes, r => r.InTime && r.Exit == 0 && Length("success") - CutBytes > PipeBufferBound && Prefix("success", r.Out, CutBytes) && r.Err is { Length: 0 } && r.Log.Count == 0));
            cases.Add(("partial-typed", "P3-38 partial stdout: the real process, Document's catch-all forced (stderr forwarded), the reader takes half of the typed line (shorter than " + PipeBufferBound.ToString(CultureInfo.InvariantCulture)
                + " bytes, so it may sit whole in the pipe buffer: the prefix only, no claim that the server met the cut) and closes stdout: exit 0, the prefix of the typed tools/call line, the diagnostic attempted once and written once",
                ("unguarded", "pipe"), false, () => Half("typed-unguarded"), r => r.InTime && r.Exit == 0 && Length("typed-unguarded") is > 0 and < PipeBufferBound
                    && Prefix("typed-unguarded", r.Out, Half("typed-unguarded")) && r.Err == diagnostic + nl
                    && r.Log.SequenceEqual(Log("hook:unguarded:pipe", attempt))));
            cases.Add(("closed-stdout", "P3-36 partial stdout: the real process, Document's catch-all forced (stderr forwarded), stdout closed by the reader before any byte (P3-40: closed before the request is written): exit 0, the diagnostic attempted once and written once",
                ("unguarded", "pipe"), false, () => 0, r => r.InTime && r.Exit == 0 && r.Out.Length == 0 && r.Err == diagnostic + nl && r.Log.SequenceEqual(Log("hook:unguarded:pipe", attempt))));

            Check(cases.Count == CaseCount && cases.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() == CaseCount,
                "P3-36 the process cases share one launch helper: " + CaseCount.ToString(CultureInfo.InvariantCulture) + " cases (distinct keys), " + PerCaseMs.ToString(CultureInfo.InvariantCulture) + " ms each, "
                + TotalMs.ToString(CultureInfo.InvariantCulture) + " ms in total | " + cases.Count.ToString(CultureInfo.InvariantCulture) + " cases");
            var late = new List<string>();
            foreach (var (key, label, hook, closeStderr, take, oracle) in cases)
            {
                var r = Server(hook, closeStderr, take?.Invoke());
                if (!r.InTime) late.Add(key);
                if (take is null && r.InTime) whole[key] = r.Out;
                var caseName = label + " | " + os + " " + D(r);
                // P3-46: the two closed-pipe cases (stderr forwarded to the pipe the reader closed) are the notice's measured fact.
                if (key is "unguarded-pipe" or "guarded-pipe") noticed.Add(caseName);
                Check(oracle(r), caseName);
            }
            Check(runs == CaseCount && late.Count == 0 && clock.ElapsedMilliseconds <= TotalMs,
                "P3-36 every process case ran through the launch helper within its limit and the total deadline | runs " + runs.ToString(CultureInfo.InvariantCulture)
                + " late [" + string.Join(", ", late) + "] elapsed " + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms on " + os);

            // ---- P3-40: the pipe capacity behind PipeBufferBound, measured ----
            // A hooked Bim.Mcp.dll (FillVariable: the hook writes FillLimit bytes to stdout in chunk-sized writes before Main,
            // logging before each write) runs with nobody reading its stdout; once the log has stood still for SettleMs the
            // writer is blocked on a full pipe, it is killed, and only then the reader counts every byte that reached the
            // pipe (the buffered bytes, then EOF). Two chunk sizes: 1 KiB (the server's AutoFlush writes are small) and
            // 1 MiB (a large write is what grows the macOS pipe to its 64 KiB maximum). Documented capacities: Linux pipe(7),
            // 16 pages = 65536 bytes by default; macOS (xnu sys/pipe.h) PIPE_SIZE 16384 growing to BIG_PIPE_SIZE 65536;
            // Windows .NET Process pipes use the default (smaller) buffer: measured 4096 bytes in 1 KiB writes, and 0 in 1 MiB
            // writes (a write larger than the free buffer stays pending whole, so the reader sees none of it); the 1 KiB
            // count above 0 shows the after-kill read sees buffered bytes on every OS. The bound holds if every measurement is at most
            // PipeBufferBound and the success cut leaves more than that unread.
            const int SettleMs = 1500;
            const int FillMs = 30000;
            // P3-42: both capacity runs (fill, kill, count) share CapacityMs, outside the 17 cases' TotalMs: each run fills
            // for at most FillMs and every wait stops at the shared deadline, so section 9's process time stays within
            // TotalMs + CapacityMs.
            const int CapacityMs = 90000;
            // P3-44: each run is the SettleMs stand-still plus process start, fill, kill and count (measured 1.6 to 1.9 s
            // per run on Linux, Windows and macOS in P3-42); the stated slack over the settle is 3 s.
            const int CapacityRunMs = SettleMs + 3000;
            var capacityClock = Stopwatch.StartNew();
            int CapacityLeft(int cap) => (int)Math.Max(0, Math.Min(cap, CapacityMs - capacityClock.ElapsedMilliseconds));
            (long Bytes, bool Settled, string Log, long Ms) Capacity(int chunk)
            {
                var began = capacityClock.ElapsedMilliseconds;
                if (CapacityLeft(FillMs) <= 0) return (-1, false, "not run: the capacity budget is spent", 0);
                var fillLog = Path.Combine(scratch, "fill-" + chunk.ToString(CultureInfo.InvariantCulture) + ".log");
                var start = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add(mcpDll);
                start.Environment.Remove("DOTNET_STARTUP_HOOKS");
                foreach (var name in StartupHook.Variables) start.Environment.Remove(name);
                start.Environment["DOTNET_STARTUP_HOOKS"] = hookAssembly;
                start.Environment[StartupHook.FillVariable] = fillLog;
                start.Environment[StartupHook.FillChunkVariable] = chunk.ToString(CultureInfo.InvariantCulture);
                using var process = Process.Start(start) ?? throw new Exception("fill process did not start: " + dotnet);
                try { process.StandardInput.Close(); } catch (IOException) { }
                var stderr = process.StandardError.ReadToEndAsync();
                var watch = Stopwatch.StartNew();
                var still = Stopwatch.StartNew();
                long seen = -1;
                var settled = false;
                while (watch.ElapsedMilliseconds < FillMs && CapacityLeft(FillMs) > 0 && !process.HasExited)
                {
                    var length = File.Exists(fillLog) ? new FileInfo(fillLog).Length : -1;
                    if (length != seen) { seen = length; still.Restart(); }
                    else if (length > 0 && still.ElapsedMilliseconds >= SettleMs) { settled = true; break; }
                    Thread.Sleep(50);
                }
                var blocked = settled && !process.HasExited;
                try { process.Kill(true); }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.WaitForExit(Math.Max(1, CapacityLeft(PerCaseMs)));
                var raw = process.StandardOutput.BaseStream;
                var count = Task.Run(() =>
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int n;
                    while ((n = raw.Read(buffer, 0, buffer.Length)) > 0) total += n;
                    return total;
                });
                var counted = count.Wait(Math.Max(1, CapacityLeft(PerCaseMs)));
                var lines = File.Exists(fillLog) ? File.ReadAllLines(fillLog) : Array.Empty<string>();
                return (counted ? count.Result : -1, blocked && counted, lines.Length.ToString(CultureInfo.InvariantCulture) + " log lines, last " + (lines.LastOrDefault() ?? "<none>"),
                    capacityClock.ElapsedMilliseconds - began);
            }
            var small = Capacity(1024);
            var large = Capacity(1 << 20);
            var capacityElapsed = capacityClock.ElapsedMilliseconds;
            var measured = Math.Max(small.Bytes, large.Bytes);
            static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
            // P3-42: the measured figures as an Actions notice on every OS (tests/mcp's output reaches the Linux and
            // Windows logs; the macOS step tees it), so the Linux capacity is visible without the log.
            // P3-46: the margin of each run under the CapacityRunMs bound is printed (notice and budget check).
            var margin = N(CapacityRunMs - small.Ms) + " + " + N(CapacityRunMs - large.Ms) + " ms margin under the " + N(CapacityRunMs) + " ms per-run bound";
            var capacityNotice = "::notice title=mcp-pipe-capacity-" + os + "::P3-42 measured pipe capacity on " + os + ": " + N(small.Bytes) + " bytes in 1 KiB writes, "
                + N(large.Bytes) + " bytes in 1 MiB writes; capacity runs " + N(small.Ms) + " + " + N(large.Ms) + " ms of the " + N(CapacityMs) + " ms budget; P3-46 " + margin;
            Console.WriteLine(capacityNotice);
            // P3-42: a 1 MiB write moves what fits into the pipe before it blocks on Linux and macOS, so 0 there is a
            // failure; Windows keeps a write larger than the anonymous pipe's free buffer pending whole (P3-40 measured 0).
            var largeFloor = OperatingSystem.IsWindows() ? large.Bytes >= 0 : large.Bytes > 0;
            var capacityName = "P3-40 measured pipe capacity on " + os + ": an unread stdout pipe took " + N(small.Bytes) + " bytes in 1 KiB writes and "
                + N(large.Bytes) + " bytes in 1 MiB writes before the writer blocked, at most PipeBufferBound " + N(PipeBufferBound)
                + ", and the success cut leaves " + N(Length("success") - CutBytes) + " bytes unread; P3-42 the 1 MiB count is "
                + (OperatingSystem.IsWindows() ? ">= 0 on Windows (a write larger than the anonymous pipe's free buffer stays pending whole, so 0 is valid)" : "> 0 on " + os + " (a blocking write moves what fits into the pipe first)")
                + " | settled " + small.Settled + "/" + large.Settled + " logs [" + small.Log + "] [" + large.Log + "]";
            noticed.Add(capacityName);
            Check(small.Settled && large.Settled && small.Bytes > 0 && largeFloor && measured <= PipeBufferBound && Length("success") - CutBytes > measured, capacityName);
            Check(capacityElapsed <= CapacityMs && small.Ms <= CapacityRunMs && large.Ms <= CapacityRunMs,
                "P3-42 the two capacity runs count toward an explicit " + N(CapacityMs / 1000) + " s budget besides the cases' " + N(TotalMs / 1000) + " s (each fill at most "
                + N(FillMs / 1000) + " s, every wait stopped at the budget); P3-44 each run within " + N(CapacityRunMs) + " ms (the " + N(SettleMs) + " ms settle plus "
                + N(CapacityRunMs - SettleMs) + " ms slack) | " + N(small.Ms) + " + " + N(large.Ms) + " = " + N(capacityElapsed) + " ms on " + os + "; P3-46 " + margin);

            // ---- P3-40: every process kill in this file is contained (the CLI helper's as Server's) ----
            // The process may exit, or the kill be denied, between the timeout and the kill: InvalidOperationException or
            // Win32Exception must not replace the case's own report. Pinned over this source: each kill call sits alone in a
            // try whose catch filters exactly those two; mutation self-tests below.
            var kill = "." + "Kill" + "(";
            const string Contained = "try { process" + "." + "Kill(true); }";
            const string Filter = "catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }";
            static int Count(string text, string part) { var n = 0; for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++; return n; }
            int Uncontained(string text) => Count(text, kill) - Regex.Matches(text, Regex.Escape(Contained) + @"\s*" + Regex.Escape(Filter)).Count;
            var source = File.ReadAllText(Path.Combine(root, "tests", "mcp", "GenerateTypologyToolTests.cs"));
            var sample = "if (late) { " + Contained + "\n    " + Filter + " }";
            Check(Count(source, kill) >= 3 && Uncontained(source) == 0
                && Uncontained(sample) == 0
                && Uncontained("if (late) { process" + kill + "true); throw new Exception(\"late\"); }") == 1
                && Uncontained(sample.Replace(" or System.ComponentModel.Win32Exception", "", StringComparison.Ordinal)) == 1
                && Uncontained(sample.Replace("InvalidOperationException or ", "", StringComparison.Ordinal)) == 1
                && Uncontained(sample + " process" + kill + "true);") == 1,
                "P3-40 every process kill in tests/mcp/GenerateTypologyToolTests.cs (the CLI helper's, Server's and the capacity writer's) contains InvalidOperationException and Win32Exception; an uncontained kill, or one missing either type, is rejected | "
                + Count(source, kill).ToString(CultureInfo.InvariantCulture) + " kills, " + Uncontained(source).ToString(CultureInfo.InvariantCulture) + " uncontained");

            // P3-36 / P3-38: the contract records the P3-34 and P3-36 (relay270) runs, the closed-pipe fact measured on all
            // three OSes in one unsplit sentence, the partial stdout result split by size and the launch helper's bounds
            // (docs/contracts/module-contracts.md, the P3-34 paragraph).
            var contracts = File.ReadAllText(Path.Combine(root, "docs", "contracts", "module-contracts.md"));
            var paragraph = contracts.Split('\n').FirstOrDefault(l => l.StartsWith("P3-34: ", StringComparison.Ordinal)) ?? "";
            Check(paragraph.Contains("CI red 44938717123, green 44940471877", StringComparison.Ordinal)
                && paragraph.Contains("relay270 CI red 45284714663, green 45286868396", StringComparison.Ordinal)
                && paragraph.Contains("a write to the closed stderr pipe does not throw, because the runtime's console stream drops the broken-pipe error (measured on Linux, Windows and macOS)", StringComparison.Ordinal)
                && !paragraph.Contains("macOS unmeasured", StringComparison.Ordinal)
                && paragraph.Contains("more than 64 KiB of the success line unread", StringComparison.Ordinal)
                && paragraph.Contains("the typed line fits the pipe buffer, so its case pins only the prefix read", StringComparison.Ordinal)
                && paragraph.Contains("the P3-31 partial-write-then-throw path is unreachable in the real MCP process", StringComparison.Ordinal)
                && paragraph.Contains("one launch helper: " + CaseCount.ToString(CultureInfo.InvariantCulture) + " cases, " + (PerCaseMs / 1000).ToString(CultureInfo.InvariantCulture) + " s each, "
                    + (TotalMs / 1000).ToString(CultureInfo.InvariantCulture) + " s in total", StringComparison.Ordinal),
                "P3-38 docs/contracts P3-34 paragraph: P3-34 and relay270 runs, closed pipe measured on Linux, Windows and macOS (one sentence), partial stdout split by size, launch helper bounds | " + Short(paragraph));
            // P3-40: the paragraph states the measured capacity with its documented sources, the enforced close order, the
            // contained kills and the macOS evidence; the macOS step keeps its PASS lines (artifact + notice).
            Check(paragraph.Contains("an unread stdout pipe takes at most 64 KiB in 1 KiB and in 1 MiB writes before its writer blocks", StringComparison.Ordinal)
                && paragraph.Contains("Linux pipe(7) 65536 bytes by default, macOS 16 KiB growing to 64 KiB", StringComparison.Ordinal)
                && paragraph.Contains("the before-any-byte reader closes stdout before the request is written", StringComparison.Ordinal)
                && paragraph.Contains("as in the CLI helper and the capacity writer (every kill in the file is pinned)", StringComparison.Ordinal)
                && paragraph.Contains("the `mcp-section-9-macos` artifact", StringComparison.Ordinal)
                && paragraph.Contains("builds `interfaces/mcp` and `tests/mcp` once each", StringComparison.Ordinal),
                "P3-40 docs/contracts P3-34 paragraph: measured pipe capacity with sources, close-before-request order, contained kills, macOS artifact and single builds | " + Short(paragraph));
            // P3-42: the Windows figures and the P3-40 runs, the large-write floor per OS and the capacity budget.
            Check(paragraph.Contains("Windows' anonymous pipe takes 4096 bytes in 1 KiB writes and 0 in 1 MiB writes (a write larger than the free buffer stays pending whole)", StringComparison.Ordinal)
                && paragraph.Contains("P3-40 relay278 CI red 45420094697, green 45425212215 and 45434537615", StringComparison.Ordinal)
                && paragraph.Contains("the 1 MiB count must be above 0 on Linux and macOS", StringComparison.Ordinal)
                && paragraph.Contains("each OS posts its measured capacity as a notice", StringComparison.Ordinal)
                && paragraph.Contains("the two capacity runs share a " + N(CapacityMs / 1000) + " s budget besides the cases' " + N(TotalMs / 1000) + " s", StringComparison.Ordinal),
                "P3-42 docs/contracts P3-34 paragraph: Windows figures (4096 / 0), P3-40 runs, 1 MiB floor on Linux and macOS, capacity notices and budget | " + Short(paragraph));
            // P3-44: the Windows figures are labelled measured (not under "documented"), the macOS duplicate notice and the
            // per-run bound are stated, and the P3-42 runs are cited.
            var documented = paragraph.IndexOf("documented: Linux pipe(7)", StringComparison.Ordinal);
            var measuredWindows = paragraph.IndexOf("measured, not documented: Windows' anonymous pipe takes 4096 bytes", StringComparison.Ordinal);
            static string S(int ms) => (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
            Check(documented >= 0 && measuredWindows > documented && !paragraph[documented..measuredWindows].Contains("Windows", StringComparison.Ordinal)
                && paragraph.Contains("macOS posts it twice: the test's `mcp-pipe-capacity-macOS` notice and the step's notice of the capacity check's PASS line", StringComparison.Ordinal)
                && paragraph.Contains("each run within " + S(CapacityRunMs) + " s (the " + S(SettleMs) + " s settle plus " + S(CapacityRunMs - SettleMs) + " s slack", StringComparison.Ordinal)
                && paragraph.Contains("P3-42 relay282 CI red 45521841849, green 45523306595", StringComparison.Ordinal),
                "P3-44 docs/contracts P3-34 paragraph: Windows figures labelled measured, the macOS duplicate capacity notice stated, the per-run bound and the P3-42 runs | " + Short(paragraph));
            // P3-46: the grep scan's or-true and quoting rules and its remaining limit, the printed margin, the notice grep
            // tested against the printed names, and the P3-44 runs.
            Check(paragraph.Contains("an or-true guard after a whole pipeline guards every grep in it, and a `)` or `}` inside a double-quoted argument does not end the command", StringComparison.Ordinal)
                && paragraph.Contains("only a literal or-true counts as a guard and a grep on a backslash-continued line counts as unguarded (both on the safe side)", StringComparison.Ordinal)
                && paragraph.Contains("the margin of each run under it is printed in the capacity notice and check", StringComparison.Ordinal)
                && paragraph.Contains("the notice grep is tested against the PASS names the section's checks print in that run", StringComparison.Ordinal)
                && paragraph.Contains("P3-44 relay286 CI red 45580685232, green 45583083273", StringComparison.Ordinal),
                "P3-46 docs/contracts P3-34 paragraph: the grep scan's guard and quoting rules and limit, the printed per-run margin, the notice grep against the printed names and the P3-44 runs | " + Short(paragraph));
            // P3-48: continued pipelines are joined, redirect forms do not end the command, the accepted guards are stated,
            // names that differ by OS are covered only by that OS's job, and the P3-46 runs are cited.
            Check(paragraph.Contains("backslash-continued lines and a line ending in `|`, `||` or `&&` are joined before the scan, so a grep on the next line of a pipeline is checked (a continuation left open at the end of the step counts as unguarded)", StringComparison.Ordinal)
                && paragraph.Contains("a `&>`, `&>>`, `>&` or `<&` redirection and a `|&` pipe do not end the command", StringComparison.Ordinal)
                && paragraph.Contains("only a literal `|| true` or `|| :` counts as a guard; any other or-list (`|| echo`, `|| exit 0`) counts as unguarded (safe side)", StringComparison.Ordinal)
                && paragraph.Contains("a check name that differs by OS (the capacity check's Windows or Linux and macOS wording, and every name carrying the OS) is covered by the notice-grep check only in that OS's job", StringComparison.Ordinal)
                && paragraph.Contains("P3-46 relay290 CI red 45659554265, green 45662889300", StringComparison.Ordinal)
                && !paragraph.Contains("a grep on a backslash-continued line counts as unguarded", StringComparison.Ordinal),
                "P3-48 docs/contracts P3-34 paragraph: continued lines joined, redirect forms, accepted guards, per-OS names covered by their own job, the P3-46 runs | " + Short(paragraph));
            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
            var macStep = workflow.IndexOf("- name: MCP generate_typology stdio section 9 alone (macOS, P3-38)", StringComparison.Ordinal);
            var macTail = macStep < 0 ? "" : workflow[macStep..];
            var nextJob = macTail.IndexOf("\n  native-build:", StringComparison.Ordinal);
            if (nextJob > 0) macTail = macTail[..nextJob];
            Check(macStep >= 0 && Count(macTail, "dotnet build interfaces/mcp ") == 1 && Count(macTail, "dotnet build tests/mcp ") == 1
                && macTail.Contains("::notice title=mcp-section-9-macos::", StringComparison.Ordinal)
                && macTail.Contains("uses: actions/upload-artifact@v4", StringComparison.Ordinal) && macTail.Contains("name: mcp-section-9-macos", StringComparison.Ordinal)
                && macTail.Contains("if: always() && runner.os == 'macOS' && steps.mcp-section-9.outcome != 'skipped'", StringComparison.Ordinal),
                "P3-40 .github/workflows/ci.yml macOS section 9: interfaces/mcp and tests/mcp built once each, the summary posted as a notice, the output uploaded as mcp-section-9-macos even on failure | step found " + (macStep >= 0));
            // P3-42: under `set -o pipefail` a grep that heads a pipeline fails the step when nothing matches; the notice
            // and summary greps are guarded (`|| true`).
            // P3-44: BareGrepPipes scans the P3-38 step alone (up to the next step) and counts every grep that sits in a
            // pipeline (first, middle or last) or inside $(...) without its own `|| true`; such a grep fails the pipeline
            // or the assignment when nothing matches.
            // P3-46: each line is first masked (Mask): single-quoted text, double-quoted text outside a nested $(...),
            // escaped characters and comments become inert, so a `|`, `)` or `}` inside a quoted argument is no operator and
            // a grep inside quoted text is no command. From each grep the scan walks forward: nested groups are skipped, a
            // `)` or `}` closing the grep's own group continues into the enclosing command (a group's or substitution's
            // status is its last pipeline's), `;` before such a `}` continues too, a `|` makes it piped, `|| true` guards
            // it (after the whole pipeline as well), and any other `||`, `;`, `&&`, `&` or the line end stops it (2>&1 is a
            // redirection). Remaining limits, both on the safe side: only a literal `|| true` counts as a guard, and a grep
            // on a backslash-continued line counts as unguarded.
            var stepEnd = macTail.IndexOf("\n      - name: ", 1, StringComparison.Ordinal);
            var macStepText = stepEnd > 0 ? macTail[..stepEnd] : macTail;
            static string Mask(string line)
            {
                var masked = line.ToCharArray();
                var frames = new Stack<char>();
                for (var i = 0; i < line.Length; i++)
                {
                    var c = line[i];
                    var next = i + 1 < line.Length ? line[i + 1] : '\0';
                    if (c == '\\' && i + 1 < line.Length) { masked[i] = masked[i + 1] = '_'; i++; continue; }
                    if (frames.Count > 0 && frames.Peek() == '"')
                    {
                        if (c == '"') frames.Pop();
                        else if (c == '$' && next == '(') { frames.Push('('); i++; }
                        else masked[i] = '_';
                        continue;
                    }
                    if (c == '\'')
                    {
                        var close = line.IndexOf('\'', i + 1);
                        if (close < 0) close = line.Length;
                        for (var j = i + 1; j < close; j++) masked[j] = '_';
                        i = close;
                        continue;
                    }
                    if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                    {
                        for (var j = i; j < line.Length; j++) masked[j] = ' ';
                        break;
                    }
                    if (c == '"') frames.Push('"');
                    else if (c == '(') frames.Push('(');
                    else if (c == ')' && frames.Count > 0) frames.Pop();
                }
                return new string(masked);
            }
            static int BareGrepPipes(string text)
            {
                var bare = 0;
                foreach (var raw in text.Split('\n'))
                {
                    var line = Mask(raw.TrimEnd('\r'));
                    var continued = line.TrimEnd().EndsWith('\\');
                    foreach (Match grep in Regex.Matches(line, @"(?<=^|[\s;|&({])grep\s"))
                    {
                        var before = line[..grep.Index];
                        var open = new Stack<bool>();
                        for (var i = 0; i < before.Length; i++)
                        {
                            if (before[i] == '(') open.Push(i > 0 && before[i - 1] == '$');
                            else if (before[i] == ')' && open.Count > 0) open.Pop();
                        }
                        var substituted = open.Contains(true);
                        var piped = Regex.IsMatch(before, @"(?<!\|)\|\s*(?:(?<!\$)\(\s*|\{\s*)*$");
                        var guarded = false;
                        var depth = 0;
                        for (var i = grep.Index + 4; i < line.Length; i++)
                        {
                            var c = line[i];
                            var next = i + 1 < line.Length ? line[i + 1] : '\0';
                            if (c is '(' or '{') { depth++; continue; }
                            if (c is ')' or '}') { if (depth > 0) depth--; continue; }
                            if (depth > 0) continue;
                            if (c == '|' && next == '|') { guarded = Regex.IsMatch(line[(i + 2)..], @"^\s*true(?![\w-])"); break; }
                            if (c == '|') { piped = true; continue; }
                            if (c == '&' && i > 0 && line[i - 1] is '>' or '<') continue;
                            if (c == '&') break;
                            if (c == ';') { if (line[(i + 1)..].TrimStart().StartsWith('}')) continue; break; }
                        }
                        if (continued) guarded = false;
                        if (!guarded && (piped || substituted || continued)) bare++;
                    }
                }
                return bare;
            }
            const string NoticeGrep = "{ grep -E '^PASS generate_typology P3-34 stdio stderr: (P3-40 measured pipe capacity on |the real process over stdio, .*closed pipe.s write did not throw)' \"$out\" || true; } | cut -c1-900";
            const string Pad = "          ";
            var selfTests = new (string Line, int Bare)[]
            {
                (Pad + "grep -E 'x' \"$out\" | cut -c1-9", 1),
                (Pad + "tail -n 5 \"$out\" | grep PASS | cut -c1-9", 1),
                (Pad + "cat \"$out\" | grep PASS", 1),
                (Pad + "n=$(grep -c '^PASS ' \"$out\")", 1),
                (Pad + "echo \"$(grep -c '^PASS ' \"$out\") PASS lines\"", 1),
                (Pad + "echo \"$(grep -c '^PASS ' \"$out\" || true) PASS lines\"", 0),
                (Pad + "{ tail -n 5 \"$out\" | grep PASS || true; } | cut -c1-9", 0),
                (Pad + NoticeGrep + " | sed 's/^//'", 0),
                (Pad + "grep -q 'a|b' \"$out\"", 0),
                (Pad + "# grep x | cut", 0),
                // P3-46: an or-true after the whole pipeline guards a grep inside it (also after a brace group).
                (Pad + "tail -n 5 \"$out\" | grep PASS | cut -c1-9 || true", 0),
                (Pad + "{ grep PASS \"$out\"; } | cut -c1-9 || true", 0),
                // P3-46: a brace group's last grep is part of the outer pipeline.
                (Pad + "{ grep PASS \"$out\"; } | cut -c1-9", 1),
                // P3-46: a ) or } inside a double-quoted argument does not end the grep's command.
                (Pad + "grep -E \"a)b\" \"$out\" | cut -c1-9", 1),
                (Pad + "grep -E \"a}b\" \"$out\" | cut -c1-9", 1),
                (Pad + "grep -E \"a)b\" \"$out\" | cut -c1-9 || true", 0),
                (Pad + "n=$(grep -c \"a)b\" \"$out\")", 1),
                // P3-46: a grep piped inside $(...), unguarded, guarded inside, guarded after the assignment.
                (Pad + "n=$(tail -n 5 \"$out\" | grep -c PASS)", 1),
                (Pad + "n=$(grep PASS \"$out\" | wc -l)", 1),
                (Pad + "n=$(tail -n 5 \"$out\" | grep -c PASS || true)", 0),
                (Pad + "n=$(grep PASS \"$out\" | wc -l) || true", 0),
                // P3-46: grep inside quoted text is no command; 2>&1 does not end it; a comment is no guard; a continued line is unguarded.
                (Pad + "echo \"a grep x | cut\"", 0),
                (Pad + "grep PASS \"$out\" 2>&1 | cut -c1-9", 1),
                (Pad + "grep PASS \"$out\" | cut -c1-9 # || true", 1),
                (Pad + "grep PASS \"$out\" \\", 1),
                // P3-48: backslash-continued lines and a line ending in a bare pipe (or || / &&) are joined before the scan,
                // so a grep on the next line of a pipeline is checked and a guard on the next line counts.
                (Pad + "tail -n 5 \"$out\" |\n" + Pad + "  grep PASS", 1),
                (Pad + "tail -n 5 \"$out\" |\n" + Pad + "  grep PASS || true", 0),
                (Pad + "grep PASS \"$out\" |\n" + Pad + "  cut -c1-9 || true", 0),
                (Pad + "grep PASS \"$out\" \\\n" + Pad + "  | cut -c1-9", 1),
                (Pad + "grep PASS \"$out\" \\\n" + Pad + "  | cut -c1-9 || true", 0),
                (Pad + "tail -n 5 \"$out\" \\\n" + Pad + "  | grep PASS \\\n" + Pad + "  || true", 0),
                (Pad + "grep PASS \"$out\" | cut -c1-9 ||\n" + Pad + "  true", 0),
                (Pad + "run: |\n" + Pad + "  grep -q PASS \"$out\"", 0),
                // P3-48: &>, >&, <& and |& are redirections or a pipe, not a background &: the walk goes on past them.
                (Pad + "grep PASS \"$out\" &>/dev/null | cut -c1-9", 1),
                (Pad + "grep PASS \"$out\" &>>/dev/null | cut -c1-9", 1),
                (Pad + "grep PASS \"$out\" >&2 | cut -c1-9", 1),
                (Pad + "grep PASS \"$out\" &>/dev/null | cut -c1-9 || true", 0),
                (Pad + "grep PASS \"$out\" |& cut -c1-9", 1),
                (Pad + "grep PASS \"$out\" |& cut -c1-9 || true", 0),
                (Pad + "cat \"$out\" |& grep PASS", 1),
                // P3-48: or-colon guards as or-true does; any other or-list (|| echo, || exit 0) does not.
                (Pad + "tail -n 5 \"$out\" | grep PASS || :", 0),
                (Pad + "n=$(grep -c PASS \"$out\" || :)", 0),
                (Pad + "{ grep PASS \"$out\" || :; } | cut -c1-9", 0),
                (Pad + "tail -n 5 \"$out\" | grep PASS || echo none", 1),
                (Pad + "tail -n 5 \"$out\" | grep PASS || exit 0", 1),
                (Pad + "tail -n 5 \"$out\" | grep PASS || :x", 1),
            };
            var failedSelfTests = selfTests.Where(t => BareGrepPipes(t.Line) != t.Bare).Select(t => t.Line.Trim().Replace("\n", " <NL> ", StringComparison.Ordinal)).ToList();
            Check(macTail.Contains("set -o pipefail", StringComparison.Ordinal) && macStepText.Contains(NoticeGrep, StringComparison.Ordinal)
                && macStepText.Contains("grep '^PASS ' \"$out\" || true;", StringComparison.Ordinal) && BareGrepPipes(macStepText) == 0
                && !macStepText.Contains("upload-artifact", StringComparison.Ordinal) && macStepText.Contains("id: mcp-section-9", StringComparison.Ordinal)
                && failedSelfTests.Count == 0,
                "P3-42 .github/workflows/ci.yml macOS section 9: the notice and summary greps cannot fail the step under pipefail when nothing matches; P3-44 only the P3-38 step is scanned and a grep anywhere in a pipeline or inside $(...) must be guarded ("
                + N(selfTests.Length) + " self-tests) | bare greps " + N(BareGrepPipes(macStepText)) + ", failed self-tests [" + string.Join(" ; ", failedSelfTests) + "]");
            // P3-44: the notice grep matches the capacity check's PASS line and the closed-pipe lines, not the P3-40 contract
            // check (its name also says "measured pipe capacity"); the macOS duplicate capacity notice is stated in the step.
            // P3-46: tested against the real PASS lines: every name this section's checks printed in this run (the PASS
            // prefix as SectionNineOnly and tests/mcp print it) matches exactly when it is one of the three noticed checks
            // (the two closed-pipe cases and the capacity check), and the printed capacity notice does not match.
            var noticePattern = new Regex(Regex.Match(NoticeGrep, "grep -E '([^']*)'").Groups[1].Value);
            const string PassPrefix = "PASS generate_typology P3-34 stdio stderr: ";
            var misnoticed = names.Where(n => noticePattern.IsMatch(PassPrefix + n) != noticed.Contains(n)).Select(n => Short(n)).ToList();
            Check(noticed.Count == 3 && noticed.All(names.Contains) && names.Count > noticed.Count + 10 && misnoticed.Count == 0
                && names.Count(n => n.Contains("docs/contracts", StringComparison.Ordinal)) >= 5
                && !noticePattern.IsMatch(capacityNotice)
                && macStepText.Contains("# P3-44: macOS posts its capacity twice", StringComparison.Ordinal),
                "P3-44 .github/workflows/ci.yml macOS section 9: the notice grep matches the capacity PASS line and the closed-pipe lines only (not the contract checks); the duplicate macOS capacity notice is stated; P3-46 tested against the "
                + N(names.Count) + " PASS names printed in this run (" + N(noticed.Count) + " noticed) | pattern " + noticePattern + " misnoticed [" + string.Join(" ; ", misnoticed) + "]");
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // P3-38: section 9 alone, for the macOS CI job (which runs no other tests/mcp section). StartupHook.Initialize enters it
    // when SectionVariable is "9" (the job runs `dotnet Bim.Mcp.Tests.dll` with DOTNET_STARTUP_HOOKS naming the same
    // assembly) and exits with its code before Main. The in-process server is a default McpServer, as Program.cs's; Check
    // throws on the first failure and prints an Actions annotation, as Program.cs does. Server removes SectionVariable from
    // every child, so the hooked Bim.Mcp.dll never re-enters this.
    public static int SectionNineOnly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        var work = Path.Combine(Path.GetTempPath(), "mcp-section-9-" + Guid.NewGuid().ToString("N"));
        var passed = 0;
        try
        {
            if (dir is null) throw new Exception("repository root with global.json not found from " + AppContext.BaseDirectory);
            Directory.CreateDirectory(work);
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
            var server = new McpServer();
            JsonObject Call(string request) => (JsonNode.Parse(server.Handle(request) ?? throw new Exception("expected response for: " + request)) ?? throw new Exception("null json")).AsObject();
            StdioStderrRun(dir.FullName, work, dotnet, Call, (condition, name) =>
            {
                if (!condition) throw new Exception(name);
                Console.WriteLine("PASS " + name);
                passed++;
            });
            if (passed == 0) throw new Exception("section 9 ran no check");
            Console.WriteLine(JsonSerializer.Serialize(new { passed, section = 9, os = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : "Linux" }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Console.WriteLine("::error title=mcp-generate-typology-section-9::" + exception.Message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

// P3-34: the DOTNET_STARTUP_HOOKS entry GenerateTypologyToolTests section 9 lends to the real Release Bim.Mcp.dll process
// (the runtime calls Initialize() before the server's Main; type in the global namespace, as the runtime requires). It
// does nothing unless BIM_TEST_P3_34_LOG is set (or, P3-38, BIM_TEST_P3_38_SECTION is "9": it then runs section 9 alone and
// exits; Server never passes that variable on). It appends "hook:<seam>:<stderr>" to that log, sets the server's
// GenerateTypologyCommand.UnguardedWorkProbe ("unguarded") or GuardedWorkProbe ("guarded") to throw
// InvalidOperationException(FailureMessage) by reflection on the loaded Bim.Mcp assembly (the production seams have no
// other switch), and replaces Console.Error with an AttemptWriter logging to the file: "pipe" forwards to the real stderr,
// "io" / "invalid" throw IOException / InvalidOperationException(StderrMessage), "disposed" forwards to a disposed
// StringWriter; "none" keeps the real stderr. Test-only: Bim.Mcp.dll and its sources are unchanged.
internal static class StartupHook
{
    public const string SeamVariable = "BIM_TEST_P3_34_SEAM";
    public const string StderrVariable = "BIM_TEST_P3_34_STDERR";
    public const string LogVariable = "BIM_TEST_P3_34_LOG";
    public const string FailureMessage = "boom generate_typology P3-34 stdio original";
    public const string StderrMessage = "p3-34 stderr rejects the diagnostic";
    public const string SectionVariable = "BIM_TEST_P3_38_SECTION";
    public const string FillVariable = "BIM_TEST_P3_40_FILL";
    public const string FillChunkVariable = "BIM_TEST_P3_40_CHUNK";
    public const long FillLimit = 16L << 20;
    // Every variable a child must not inherit from the test process (Server and the capacity writer remove them first).
    public static readonly string[] Variables = { SeamVariable, StderrVariable, LogVariable, SectionVariable, FillVariable, FillChunkVariable };

    public static void Initialize()
    {
        // P3-38: the macOS CI job's section-9-only entry (GenerateTypologyToolTests.SectionNineOnly); never set in a child.
        if (Environment.GetEnvironmentVariable(SectionVariable) == "9") Environment.Exit(GenerateTypologyToolTests.SectionNineOnly());
        // P3-40: the pipe-capacity writer (section 9's Capacity): FillLimit bytes to stdout in chunk-sized writes, a log
        // line before each write, then exit before Main. Its reader reads nothing until the writer is blocked and killed.
        var fill = Environment.GetEnvironmentVariable(FillVariable);
        if (!string.IsNullOrEmpty(fill)) Environment.Exit(Fill(fill, int.Parse(Environment.GetEnvironmentVariable(FillChunkVariable) ?? "1024", System.Globalization.CultureInfo.InvariantCulture)));
        var log = Environment.GetEnvironmentVariable(LogVariable);
        if (string.IsNullOrEmpty(log)) return;
        var seam = Environment.GetEnvironmentVariable(SeamVariable) ?? "none";
        var stderr = Environment.GetEnvironmentVariable(StderrVariable) ?? "none";
        File.AppendAllText(log, "hook:" + seam + ":" + stderr + "\n");
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var command = System.Reflection.Assembly.Load(new System.Reflection.AssemblyName("Bim.Mcp")).GetType("Bim.Cli.GenerateTypologyCommand", throwOnError: true)!;
        Action fail = () => throw new InvalidOperationException(FailureMessage);
        if (seam == "unguarded") command.GetProperty("UnguardedWorkProbe", Any)!.SetValue(null, fail);
        else if (seam == "guarded") command.GetProperty("GuardedWorkProbe", Any)!.SetValue(null, fail);
        void Record(string entry) => File.AppendAllText(log, entry + "\n");
        TextWriter? replacement = stderr switch
        {
            "pipe" => new AttemptWriter(Console.Error, null, Record),
            "io" => new AttemptWriter(null, () => new IOException(StderrMessage), Record),
            "invalid" => new AttemptWriter(null, () => new InvalidOperationException(StderrMessage), Record),
            "disposed" => new AttemptWriter(Disposed(), null, Record),
            _ => null,
        };
        if (replacement is not null) Console.SetError(replacement);
    }

    static int Fill(string log, int chunk)
    {
        var bytes = new byte[chunk];
        Array.Fill(bytes, (byte)'x');
        using var stdout = Console.OpenStandardOutput();
        for (long total = 0; total < FillLimit; total += chunk)
        {
            File.AppendAllText(log, "write:" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            stdout.Write(bytes, 0, chunk);
        }
        File.AppendAllText(log, "done\n");
        return 0;
    }

    static StringWriter Disposed()
    {
        var closed = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        closed.Dispose();
        return closed;
    }
}

// P3-34: a stderr that records each write attempt ("attempt:<escaped value>") through log, then throws failure() or
// forwards the same overload to inner (an exception from inner is recorded as "forward-threw:<type>" and rethrown).
internal sealed class AttemptWriter(TextWriter? inner, Func<Exception>? failure, Action<string> log) : TextWriter
{
    public override Encoding Encoding => new UTF8Encoding(false);
    void Attempt(string value, Action<TextWriter> forward)
    {
        log("attempt:" + Escape(value));
        if (failure is not null) throw failure();
        if (inner is null) return;
        try { forward(inner); }
        catch (Exception error) { log("forward-threw:" + error.GetType().FullName); throw; }
    }
    public override void Write(char value) => Attempt(value.ToString(), w => w.Write(value));
    public override void Write(string? value) => Attempt(value ?? "", w => w.Write(value));
    public override void Write(char[] buffer, int index, int count) => Attempt(new string(buffer, index, count), w => w.Write(buffer, index, count));
    public override void WriteLine() => Attempt("", w => w.WriteLine());
    public override void WriteLine(string? value) => Attempt(value ?? "", w => w.WriteLine(value));
    public override void Flush() { }
    public static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
