using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Generative;

// P7-20 plan_layouts: the read-only MCP face of the P7-11/P7-13/P7-15 CLI verb `plan-layouts brief.json output.json
// [--alternatives n] [--seed s] [--pack id]` (interfaces/cli/PlanLayoutsCommand.cs, linked into the server, no copied
// parser). Oracles:
//   1. On success the tool text is the canonical AlternativeSet JSON: its UTF-8 bytes equal the file the real Release CLI
//      process writes for the same brief file and options, and AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate)
//      computed in process (defaults, explicit options, seed wrap-around, a negative seed, an infeasible brief, --pack with
//      the CABA pack's literal planner rules, another pack, a pack matching no room). Packs are found as the CLI finds
//      them (SkillPackLookup.Load(id, packageFirst: true)).
//   2. Every refusal is isError and byte-equal to the CLI stdout line for the same brief and options (plan_layouts_*
//      option, brief, site, pack codes; input_*), with the CLI precedence (options before the brief is read).
//   3. It writes nothing (the input directory is unchanged; it takes no output path), and leaves the exit code and
//      Console.Out untouched; tools/list labels it permission level read with readOnlyHint true only; the schema requires
//      exactly brief (alternatives, seed integers and pack optional).
//   4. MCP-only path guard: plan_layouts_path_invalid {detail brief}; missing / mistyped / extra arguments are -32602.
//   5. The real Release Bim.Mcp.dll answers the same bytes over stdio (pack found from the server binary).
//   6. Static: the tool calls PlanLayoutsCommand.DryPlan and contains no parser, planner, pack lookup or file access of its
//      own; the CLI verb and DryPlan share one option validation and one Plan path; the MCP projects link the CLI source.
internal static class PlanLayoutsToolTests
{
    const string ToolName = "plan_layouts";
    const string Caba = "ar.caba.codigo-edificacion";
    const string ExamplePack = "xx.example.codigo-ejemplo";

    public static void Run(string root, string work, string dotnet, string cliDll, Func<string, JsonObject> call, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "plan_layouts: " + name);
        var scratch = Path.Combine(work, "plan-layouts-mcp");
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
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

        // The real CLI process with a fresh output path and the options in the order alternatives, seed, pack.
        (int Exit, string Out, byte[]? File) Cli(string briefPath, int? alternatives, int? seed, string? pack)
        {
            var output = Path.Combine(outputs, Next() + "-out.json");
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            start.ArgumentList.Add("plan-layouts");
            start.ArgumentList.Add(briefPath);
            start.ArgumentList.Add(output);
            if (alternatives is { } n) { start.ArgumentList.Add("--alternatives"); start.ArgumentList.Add(N(n)); }
            if (seed is { } s) { start.ArgumentList.Add("--seed"); start.ArgumentList.Add(N(s)); }
            if (pack is not null) { start.ArgumentList.Add("--pack"); start.ArgumentList.Add(pack); }
            using var process = Process.Start(start) ?? throw new Exception("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("CLI timed out: plan-layouts " + briefPath); }
            process.WaitForExit();
            _ = stderr.Result;
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, File.Exists(output) ? File.ReadAllBytes(output) : null);
        }
        static JsonObject Arguments(string briefPath, int? alternatives, int? seed, string? pack)
        {
            var arguments = new JsonObject { ["brief"] = briefPath };
            if (alternatives is { } n) arguments["alternatives"] = n;
            if (seed is { } s) arguments["seed"] = s;
            if (pack is not null) arguments["pack"] = pack;
            return arguments;
        }
        string ToolLine(JsonObject arguments) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 19, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = ToolName, ["arguments"] = arguments } }.ToJsonString();
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        static int? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<int>();

        var exitBefore = Environment.ExitCode;
        // One MCP call: the response, with "nothing written, exit code and Console.Out untouched" checked around it.
        JsonObject Call(string label, JsonObject arguments)
        {
            var before = Listing();
            var stdout = Console.Out;
            var response = call(ToolLine(arguments));
            Check(Listing() == before && Environment.ExitCode == exitBefore && ReferenceEquals(Console.Out, stdout), label + ": writes nothing, exit code and Console.Out untouched");
            return response;
        }

        // ---- tools/list ----
        var listed = call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }.ToJsonString())["result"]!["tools"]!.AsArray();
        var matches = listed.Where(t => t!["name"]!.GetValue<string>() == ToolName).ToArray();
        Check(matches.Length == 1, "listed exactly once");
        var tool = matches[0]!;
        Check(tool["description"]!.GetValue<string>().Contains("plan-layouts", StringComparison.Ordinal)
            && tool["description"]!.GetValue<string>().Contains("read-only", StringComparison.OrdinalIgnoreCase), "described as read-only, naming the CLI verb plan-layouts");
        Check(tool["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>() == "read" && tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true
            && tool["annotations"]!.AsObject().Count == 1, "lists _meta permission_level read and annotations readOnlyHint true only");
        var schema = tool["inputSchema"]!;
        var properties = schema["properties"]!.AsObject();
        Check(string.Join(",", schema["required"]!.AsArray().Select(x => x!.GetValue<string>())) == "brief"
            && string.Join(",", properties.Select(p => p.Key + ":" + p.Value!["type"]!.GetValue<string>())) == "brief:string,alternatives:integer,seed:integer,pack:string"
            && schema["additionalProperties"]!.GetValue<bool>() == false, "schema: brief (string, required), alternatives and seed (integer), pack (string), nothing else");

        // ---- 1. sets: CLI output file == tool text == in-process AlternativeGenerator ----
        static byte[] Generated(ProjectBrief brief, int count, int seed, IReadOnlyList<PlannerRule>? rules = null) =>
            Utf8(AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(brief, rules is null ? new AlternativeOptions(count, seed)
                : new AlternativeOptions(count, seed, new PlannerOptions(Rules: new PlannerRules(rules))))));
        byte[] Expect(string label, string briefPath, int? alternatives, int? seed, string? pack, byte[] library)
        {
            var response = Call(label, Arguments(briefPath, alternatives, seed, pack));
            var cli = Cli(briefPath, alternatives, seed, pack);
            var text = Utf8(Text(response));
            Check(!IsError(response) && text.SequenceEqual(library), label + ": the text is the in-process AlternativeGenerator set byte for byte | " + Short(Text(response)));
            Check(cli.Exit == 0 && cli.File is not null && text.SequenceEqual(cli.File), label + ": the text is byte-equal to the CLI output file | exit " + N(cli.Exit) + " " + Short(cli.Out));
            return text;
        }
        const string small = "{\"brief_version\":1,\"name\":\"Small studio\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
            + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}],"
            + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"bath\",\"kind\":\"required\"}]}";
        const string cramped = "{\"brief_version\":1,\"name\":\"Cramped\",\"constraints\":{\"site\":{\"width_m\":2,\"depth_m\":2}},"
            + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"min_area_m2\":15}]}";
        const string unmatched = "{\"brief_version\":1,\"name\":\"Unmatched\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
            + "\"rooms\":[{\"id\":\"office\",\"name\":\"Office\",\"target_area_m2\":20,\"min_area_m2\":15}]}";
        var smallBrief = BriefParser.Parse(small);
        var smallPath = Input("small.json", small);
        var exampleText = File.ReadAllText(Path.Combine(root, "examples", "brief", "apartment-2br.json"));
        var examplePath = Input("apartment-2br.json", exampleText);

        var defaults = Expect("defaults (4 alternatives from seed 0)", smallPath, null, null, null, Generated(smallBrief, 4, 0));
        Expect("the example brief, 2 alternatives from seed 1", examplePath, 2, 1, null, Generated(BriefParser.Parse(exampleText), 2, 1));
        Expect("seed 2147483647 with 2 alternatives wraps", smallPath, 2, int.MaxValue, null, Generated(smallBrief, 2, int.MaxValue));
        Expect("a negative seed and a single alternative", smallPath, 1, -7, null, Generated(smallBrief, 1, -7));
        var explicitDefaults = Expect("explicit alternatives 4 and seed 0", smallPath, 4, 0, null, Generated(smallBrief, 4, 0));
        Check(explicitDefaults.SequenceEqual(defaults), "explicit defaults give the default bytes");
        var crampedSet = AlternativeGenerator.Generate(BriefParser.Parse(cramped), new AlternativeOptions(2, 0));
        Check(crampedSet.Status == AlternativeSetStatus.Infeasible, "fixture: the cramped brief is infeasible in process");
        Expect("an infeasible brief (a result, not an error)", Input("cramped.json", cramped), 2, null, null, Generated(BriefParser.Parse(cramped), 2, 0));
        // --pack: the CLI tests' literal CABA planner rules of the small brief (tests/cli/PlanLayoutsTests.cs).
        var smallCaba = new[]
        {
            new PlannerRule(NamePattern: "living*", MinAreaSquareMetres: 9, MinSideMetres: 2.8, Reference: Caba + " living-area-min living-side-min"),
            new PlannerRule(NamePattern: "bath*", MinAreaSquareMetres: 1.4, MinSideMetres: 0.9, Reference: Caba + " bathroom-area-min bathroom-side-min"),
        };
        var withPack = Expect("--pack " + Caba + " on the small brief", smallPath, 2, 3, Caba, Generated(smallBrief, 2, 3, smallCaba));
        Check(!withPack.SequenceEqual(Generated(smallBrief, 2, 3)), "the pack changes the plans (its rules really reach the planner)");
        Check(Encoding.UTF8.GetString(withPack).Contains("rules[0] " + Caba + " living-area-min living-side-min", StringComparison.Ordinal), "the pack rules are traced in the constraint details");
        Expect("--pack " + ExamplePack + " on the small brief", smallPath, 1, null, ExamplePack,
            Generated(smallBrief, 1, 0, new[] { new PlannerRule(NamePattern: "living*", MinAreaSquareMetres: 10, MinSideMetres: null, Reference: ExamplePack + " living-area-min") }));
        Expect("a pack whose rules match no brief room plans with no rules", Input("unmatched.json", unmatched), 1, null, ExamplePack, Generated(BriefParser.Parse(unmatched), 1, 0));

        // ---- 2. refusals: isError, byte-equal to the CLI stdout line, no CLI file ----
        static string Code(string code, string? detail = null) => detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
        void Refused(string label, string briefPath, int? alternatives, int? seed, string? pack, string expected)
        {
            var response = Call(label, Arguments(briefPath, alternatives, seed, pack));
            var cli = Cli(briefPath, alternatives, seed, pack);
            Check(IsError(response) && cli.Exit == 1 && cli.File is null && Text(response) == cli.Out && cli.Out == expected,
                "refuses " + label + " byte-equal to the CLI envelope | " + Short(Text(response)) + " / " + Short(cli.Out));
        }
        var missing = Path.Combine(inputs, "absent.json");
        Refused("alternatives 0", smallPath, 0, null, null, Code("plan_layouts_alternatives_invalid", "0"));
        Refused("alternatives 65", smallPath, 65, null, null, Code("plan_layouts_alternatives_invalid", "65"));
        Refused("alternatives -1", smallPath, -1, null, null, Code("plan_layouts_alternatives_invalid", "-1"));
        Refused("a malformed pack id", smallPath, null, null, "../skills", Code("plan_layouts_pack_id_invalid", "../skills"));
        Refused("an empty pack id", smallPath, null, null, "", Code("plan_layouts_pack_id_invalid", ""));
        Refused("a pack id with a path separator", smallPath, null, null, "ar.caba/x", Code("plan_layouts_pack_id_invalid", "ar.caba/x"));
        Refused("an unknown pack", smallPath, null, null, "xx.no-such.pack", Code("plan_layouts_pack_unavailable", "input_not_found"));
        Refused("a pack with no planner minimum rules", smallPath, null, null, "bim.typology.vivienda-unifamiliar", Code("plan_layouts_pack_rules_none", "bim.typology.vivienda-unifamiliar"));
        Refused("a pack with only warning minimums", smallPath, null, null, "bim.typology.dwelling", Code("plan_layouts_pack_rules_none", "bim.typology.dwelling"));
        Refused("malformed brief JSON", Input("bad.json", "{"), null, null, null, Code("plan_layouts_brief_invalid", "json_invalid at brief"));
        Refused("a brief without a site", Input("nosite.json", small.Replace("\"site\":{\"width_m\":6,\"depth_m\":8},", "", StringComparison.Ordinal)), null, null, null,
            Code("plan_layouts_site_required", "constraints.site"));
        Refused("an adjacency naming an unknown room", Input("adjacency.json", small.Replace("\"b\":\"bath\"", "\"b\":\"kitchen\"", StringComparison.Ordinal)), null, null, null,
            Code("plan_layouts_brief_invalid", BriefCodes.AdjacencyRoomUnknown + " at adjacencies[0].b"));
        Refused("a missing brief file", missing, null, null, null, Code("input_not_found"));
        Check(!File.Exists(missing), "creates no missing brief");
        var directory = Path.Combine(inputs, Next() + "-a-directory.json");
        Directory.CreateDirectory(directory);
        Refused("a directory as brief", directory, null, null, null, Code("input_not_regular_file"));
        Refused("a brief over the 8 MiB input cap", Input("huge.json", new string(' ', 8 * 1024 * 1024 + 1)), null, null, null, Code("input_too_large"));
        // Precedence: the options are checked before the brief is read; the pack is read after the brief.
        Refused("alternatives 0 with a missing brief", missing, 0, null, null, Code("plan_layouts_alternatives_invalid", "0"));
        Refused("a malformed pack id with a missing brief", missing, null, null, "../x", Code("plan_layouts_pack_id_invalid", "../x"));
        Refused("an unknown pack with a missing brief", missing, null, null, "xx.no-such.pack", Code("input_not_found"));

        // ---- 4. MCP-only path guard and argument errors ----
        var pathInvalid = Code("plan_layouts_path_invalid", "brief");
        foreach (var (label, bad) in new[] { ("empty", ""), ("blank", "   "), ("with NUL", "a\0b"), ("of 5000 characters", new string('x', 5000)) })
        {
            var response = Call("brief " + label, Arguments(bad, null, null, null));
            Check(IsError(response) && Text(response) == pathInvalid, "refuses a brief path " + label + " as plan_layouts_path_invalid | " + Short(Text(response)));
        }
        var surrogate = call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"plan_layouts\",\"arguments\":{\"brief\":\"\\ud800\"}}}");
        Check(IsError(surrogate) && Text(surrogate) == pathInvalid && Environment.ExitCode == exitBefore, "a lone-surrogate brief path is plan_layouts_path_invalid, never a crash | " + Short(Text(surrogate)));
        Check(ErrorCode(call(ToolLine(new JsonObject()))) == -32602, "without brief returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["brief"] = 1 }))) == -32602, "a non-string brief returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["brief"] = JsonNode.Parse(small) }))) == -32602, "takes no inline brief object (-32602)");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["brief"] = smallPath, ["alternatives"] = "2" }))) == -32602, "a string alternatives returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["brief"] = smallPath, ["alternatives"] = 2.5 }))) == -32602, "a fractional alternatives returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["brief"] = smallPath, ["seed"] = 2147483648L }))) == -32602, "a seed outside int32 returns -32602");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["brief"] = smallPath, ["pack"] = 1 }))) == -32602, "a non-string pack returns -32602");
        var never = Path.Combine(inputs, "never.json");
        Check(ErrorCode(call(ToolLine(new JsonObject { ["brief"] = smallPath, ["output"] = never }))) == -32602 && !File.Exists(never), "takes no output path (-32602): it never writes a file");

        // ---- 5. the real Release Bim.Mcp.dll over stdio ----
        var processOut = mcpProcess(ToolLine(Arguments(smallPath, 2, 3, Caba)) + "\n");
        var processResponse = JsonNode.Parse(processOut.Split('\n')[0])!.AsObject();
        Check(!IsError(processResponse) && Utf8(Text(processResponse)).SequenceEqual(withPack),
            "the real Bim.Mcp.dll process returns the --pack set byte for byte (pack found from the server binary) | " + Short(processOut));

        // ---- 5b. plan_layouts_pack_invalid: a scratch repository with its own skills/ (global.json, no package markers),
        // the Release CLI and Bim.Mcp.dll copied under it, so both find these packs as the repository's ----
        var fakeRepo = Path.Combine(scratch, "fake-repo");
        Directory.CreateDirectory(fakeRepo);
        File.WriteAllText(Path.Combine(fakeRepo, "global.json"), "{}");
        string CopyBinaries(string dll, string target)
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
        var fakeCli = CopyBinaries(cliDll, Path.Combine(fakeRepo, "cli-bin"));
        var fakeMcp = CopyBinaries(Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll"), Path.Combine(fakeRepo, "mcp-bin"));
        void FakePack(string folder, string text) { Directory.CreateDirectory(Path.Combine(fakeRepo, "skills", folder)); File.WriteAllText(Path.Combine(fakeRepo, "skills", folder, "skill.json"), text, new UTF8Encoding(false)); }
        static string Manifest(string id) => "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"0.0.1\",\"kind\":\"code\",\"description\":\"test pack\",\"priority\":100,\"rules\":["
            + "{\"id\":\"living-area\",\"severity\":\"error\",\"target\":{\"room\":\"living*\"},\"check\":{\"op\":\"min\",\"property\":\"area_m2\",\"value\":10},\"message\":\"m\"}]}";
        FakePack("zz.test.bad-json", "{");
        FakePack("zz.test.mismatch", Manifest("zz.test.other"));
        FakePack("zz.test.good", Manifest("zz.test.good"));
        (int Exit, string Out) RunProcess(string dll, string stdin, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(dll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new Exception("process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(stdin); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("timed out: " + dll); }
            process.WaitForExit();
            _ = stderr.Result;
            return (process.ExitCode, stdout.Result.TrimEnd('\r', '\n'));
        }
        string FakeTool(string pack) => RunProcess(fakeMcp, ToolLine(Arguments(smallPath, 1, null, pack)) + "\n").Out.Split('\n')[0];
        (int Exit, string Out, bool Written) FakeCli(string pack)
        {
            var output = Path.Combine(outputs, Next() + "-fake-out.json");
            var result = RunProcess(fakeCli, "", "plan-layouts", smallPath, output, "--alternatives", "1", "--pack", pack);
            return (result.Exit, result.Out, File.Exists(output));
        }
        var goodTool = JsonNode.Parse(FakeTool("zz.test.good"))!.AsObject();
        var goodCli = FakeCli("zz.test.good");
        Check(!IsError(goodTool) && goodCli.Exit == 0 && goodCli.Written && Utf8(Text(goodTool)).SequenceEqual(Generated(smallBrief, 1, 0,
            new[] { new PlannerRule(NamePattern: "living*", MinAreaSquareMetres: 10, MinSideMetres: null, Reference: "zz.test.good living-area") })),
            "fixture: the copied server and CLI read the scratch repository's skills/ (zz.test.good plans with its rule) | " + Short(Text(goodTool)) + " / " + Short(goodCli.Out));
        foreach (var (label, pack, expected) in new[]
        {
            ("a malformed pack manifest", "zz.test.bad-json", Code("plan_layouts_pack_invalid", "json_invalid")),
            ("a manifest naming another pack id", "zz.test.mismatch", Code("plan_layouts_pack_invalid", "id")),
        })
        {
            var response = JsonNode.Parse(FakeTool(pack))!.AsObject();
            var cli = FakeCli(pack);
            Check(IsError(response) && cli.Exit == 1 && !cli.Written && Text(response) == cli.Out && cli.Out == expected,
                "refuses " + label + " byte-equal to the CLI envelope | " + Short(Text(response)) + " / " + Short(cli.Out));
        }

        // ---- 6. static: the shared handler, linked, not copied ----
        // Comments stripped: /* */ blocks first, then // to end of line.
        static string CodeOf(string text) => string.Join("\n", Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline).Split('\n')
            .Select(line => { var at = line.IndexOf("//", StringComparison.Ordinal); return at < 0 ? line : line[..at]; }));
        var toolPath = Path.Combine(root, "interfaces", "mcp", "PlanLayoutsTool.cs");
        Check(File.Exists(toolPath), "interfaces/mcp/PlanLayoutsTool.cs exists");
        var toolCode = CodeOf(File.ReadAllText(toolPath));
        Check(toolCode.Contains("PlanLayoutsCommand.DryPlan(", StringComparison.Ordinal), "the tool runs the CLI verb's shared PlanLayoutsCommand.DryPlan");
        var inlined = new[] { "BriefParser", "BriefValidator", "AlternativeGenerator", "AlternativeSetSerializer", "SpacePlanner", "PlannerRule", "SkillPackLookup", "SkillParser",
            "BoundedInput", "int.TryParse", "int.Parse", "Regex", "File.", "FileStream", "Directory.", "GltfExporter", "Console.SetOut" }
            .Where(api => toolCode.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(inlined.Length == 0, "the tool inlines no brief parser, planner, option parser, pack lookup, file access or Console capture [" + string.Join(", ", inlined) + "]");
        var owners = Directory.GetFiles(Path.Combine(root, "interfaces", "mcp"), "*.cs")
            .Where(f => new[] { "BriefParser.Parse(", "AlternativeGenerator.Generate(", "AlternativeSetSerializer.Serialize(" }.Any(api => CodeOf(File.ReadAllText(f)).Contains(api, StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f)).ToArray();
        Check(owners.Length == 0, "no interfaces/mcp source parses a brief or generates a set itself [" + string.Join(", ", owners) + "]");
        var commandCode = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "PlanLayoutsCommand.cs")));
        Check(Regex.Matches(commandCode, @"BriefParser\.Parse\(").Count == 1 && Regex.Matches(commandCode, @"AlternativeGenerator\.Generate\(").Count == 1
            && Regex.Matches(commandCode, @"SkillPackLookup\.Load\(").Count == 1 && Regex.Matches(commandCode, @"\bPlan\(").Count == 3,
            "the CLI verb and DryPlan share one path: the brief is parsed, the pack loaded and the set generated in one place, which both call (Plan)");
        Check(Regex.Matches(commandCode, @"\bAlternativesValue\(").Count == 3 && Regex.Matches(commandCode, @"\bSeedValue\(").Count == 3 && Regex.Matches(commandCode, @"\bPackValue\(").Count == 3,
            "the CLI option loop and DryPlan validate alternatives, seed and pack through the same helpers");
        foreach (var project in new[] { Path.Combine(root, "interfaces", "mcp", "Bim.Mcp.csproj"), Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj") })
        {
            var projectDir = Path.GetDirectoryName(project)!;
            var shared = Path.GetFullPath(Path.Combine(root, "interfaces", "cli", "PlanLayoutsCommand.cs"));
            var linked = System.Xml.Linq.XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "Compile")
                .Select(e => (string?)e.Attribute("Include")).Where(include => include is not null)
                .Any(include => string.Equals(Path.GetFullPath(Path.Combine(projectDir, include!.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))), shared,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Check(linked, Path.GetFileName(project) + " compiles interfaces/cli/PlanLayoutsCommand.cs (shared source, not a copy)");
        }
    }
}
