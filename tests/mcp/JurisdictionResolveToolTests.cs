using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;

// P10-27: the read-only MCP tool jurisdiction_resolve, the MCP face of the P10-21 CLI verb `jurisdiction-resolve --code c |
// --country cc --municipality m [--lock skills.lock.json] [--allow-stub]` (interfaces/cli/JurisdictionResolveCommand.cs).
// Oracles:
//   1. tools/list: listed once after generate_prototypes, read-only (description names the CLI verb), _meta
//      permission_level read and annotations readOnlyHint true only; the schema has exactly the optional properties
//      code, country, municipality, lock_path (strings) and allow_stub (boolean), required [] and no other property.
//   2. Byte equality with the real CLI process: for every pinned argument set (CABA by code and by country+municipality,
//      allow_stub, the stub refused and allowed, jurisdiction_unknown by code and by municipality, jurisdiction_invalid,
//      a lock over the repository skills (success, stub refused, CABA unpinned), a forged hash (skill_lock_drift), an
//      absent lock (lock_missing), an over-long lock path (skills_lock_path_invalid)) the tool text equals the CLI stdout
//      line without its newline and isError equals (CLI exit != 0); the CABA success is pinned literally.
//   3. Usage-equivalent arguments are JSON-RPC -32602 (never a tool result): no selector, code with country or
//      municipality, only one of country/municipality, a blank or "--"-prefixed string, null or wrongly typed values, an
//      unknown argument, a lone-surrogate string; each case with a CLI spelling is the CLI's jurisdiction_resolve_usage.
//   4. A NUL lock path (cannot travel in a process argument) is the in-process CLI handler's skills_lock_path_invalid
//      line, byte for byte.
//   5. Typed internal error: with the command's WorkProbe seam throwing, the tool is isError with exactly
//      {"status":"failed","code":"jurisdiction_resolve_internal_error"} (the in-process CLI Run prints the same line),
//      the IntentPlan diagnostic on the server's stderr once, never McpServer's generic failure; reset, CABA again.
//   6. The real Release Bim.Mcp.dll over stdio answers one success and one refusal with the CLI bytes.
//   7. Static: the tool runs JurisdictionResolveCommand.Resolve (no registry, lock, selection, serializer or file call
//      of its own); the CLI Run prints Resolve's envelope; the three csprojs compiling McpServer.cs link the command
//      source; the Release Bim.Mcp.dll defines Bim.Cli.JurisdictionResolveCommand with Resolve.
internal static class JurisdictionResolveToolTests
{
    const string ToolName = "jurisdiction_resolve";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stub = "xx.example.codigo-ejemplo";
    const string InternalLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_resolve_internal_error\"}";
    const string PathInvalidLine = "{\"status\":\"failed\",\"code\":\"skills_lock_path_invalid\",\"detail\":\"lock\"}";
    const string UsageLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_resolve_usage\",\"detail\":\"usage: jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]\"}";

    public static void Run(string root, string work, string dotnet, string cliDll, Func<string, JsonObject> call, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "jurisdiction_resolve: " + name);
        static string Short(string text) => text.Length > 600 ? text[..600] + "..." : text;
        var scratch = Path.Combine(work, "jurisdiction-resolve-mcp");
        Directory.CreateDirectory(scratch);
        var skills = Path.Combine(root, "skills");

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            start.ArgumentList.Add("jurisdiction-resolve");
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result);
        }
        var id = 2700;
        string Line(JsonObject arguments) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = ToolName, ["arguments"] = arguments },
        }.ToJsonString();
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        static bool IsResult(JsonObject response) => response["result"] is JsonObject result && result["content"] is JsonArray && !response.ContainsKey("error");
        static int? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<int>();

        // ---- 1. tools/list ----
        var listed = call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }.ToJsonString())["result"]!["tools"]!.AsArray();
        var names = listed.Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Check(names.Count(n => n == ToolName) == 1 && Array.IndexOf(names, ToolName) == Array.IndexOf(names, "generate_prototypes") + 1,
            "listed once, right after generate_prototypes [" + string.Join(",", names.Take(17)) + "]");
        var tool = listed.FirstOrDefault(t => t!["name"]!.GetValue<string>() == ToolName);
        Check(tool is not null && tool["description"]!.GetValue<string>().Contains("jurisdiction-resolve", StringComparison.Ordinal)
            && tool["description"]!.GetValue<string>().Contains("read-only", StringComparison.OrdinalIgnoreCase), "described read-only, naming the CLI verb jurisdiction-resolve");
        if (tool is null) return;
        Check(tool["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>() == "read" && tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true
            && tool["annotations"]!.AsObject().Count == 1, "lists _meta permission_level read and annotations readOnlyHint true only");
        var schema = tool["inputSchema"]!.AsObject();
        var properties = schema["properties"]!.AsObject();
        Check(string.Join(",", properties.Select(p => p.Key + ":" + p.Value!["type"]!.GetValue<string>())) == "code:string,country:string,municipality:string,lock_path:string,allow_stub:boolean"
            && schema["required"]!.AsArray().Count == 0 && schema["additionalProperties"]!.GetValue<bool>() == false,
            "schema: optional code, country, municipality, lock_path (string), allow_stub (boolean), nothing required, no other property | " + schema.ToJsonString());

        // ---- 2. byte equality with the CLI process ----
        var cabaVersion = SkillLoader.LoadDirectory(skills).Find(Caba)!.Version.ToString();
        var cabaLine = JsonSerializer.Serialize(new { status = "succeeded", code = "ar.caba", pack = Caba, version = cabaVersion, stub = false });
        var lockPath = Path.Combine(scratch, "skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Caba, Stub })));
        var stubOnly = Path.Combine(scratch, "stub-only.lock.json");
        File.WriteAllBytes(stubOnly, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Stub })));
        var forged = Path.Combine(scratch, "forged.lock.json");
        File.WriteAllBytes(forged, SkillLockFile.Serialize(new SkillLock(new[] { new SkillLockEntry(Caba, SkillLoader.LoadDirectory(skills).Find(Caba)!.Version, new string('0', 64)) })));
        var absent = Path.Combine(scratch, "absent.lock.json");
        var longLock = Path.Combine(scratch, new string('l', Bim.Mcp.SkillLockTool.MaxPathChars + 1));
        JsonObject A(params (string Key, JsonNode? Value)[] pairs) { var o = new JsonObject(); foreach (var (k, v) in pairs) o[k] = v; return o; }
        var cases = new (string Label, JsonObject Arguments, string[] CliArguments, string? Code)[]
        {
            ("CABA by code", A(("code", "ar.caba")), ["--code", "ar.caba"], null),
            ("CABA by country and municipality", A(("country", "Argentina"), ("municipality", "Ciudad Autonoma de Buenos Aires")), ["--country", "Argentina", "--municipality", "Ciudad Autonoma de Buenos Aires"], null),
            ("CABA with allow_stub true", A(("code", "ar.caba"), ("allow_stub", true)), ["--code", "ar.caba", "--allow-stub"], null),
            ("CABA with allow_stub false", A(("code", "ar.caba"), ("allow_stub", false)), ["--code", "ar.caba"], null),
            ("stub refused", A(("code", "xx.example")), ["--code", "xx.example"], "jurisdiction_stub_refused"),
            ("stub allowed", A(("code", "xx.example"), ("allow_stub", true)), ["--code", "xx.example", "--allow-stub"], null),
            ("unknown code", A(("code", "zz.nowhere")), ["--code", "zz.nowhere"], "jurisdiction_unknown"),
            ("unknown municipality", A(("country", "Argentina"), ("municipality", "Rosario")), ["--country", "Argentina", "--municipality", "Rosario"], "jurisdiction_unknown"),
            ("malformed code", A(("code", "Not A Code")), ["--code", "Not A Code"], "jurisdiction_invalid"),
            ("CABA under a lock", A(("code", "ar.caba"), ("lock_path", lockPath)), ["--code", "ar.caba", "--lock", lockPath], null),
            ("stub refused under a lock", A(("code", "xx.example"), ("lock_path", lockPath)), ["--code", "xx.example", "--lock", lockPath], "jurisdiction_stub_refused"),
            ("CABA unpinned under a lock", A(("code", "ar.caba"), ("lock_path", stubOnly)), ["--code", "ar.caba", "--lock", stubOnly], "jurisdiction_unknown"),
            ("forged hash under a lock", A(("code", "ar.caba"), ("lock_path", forged)), ["--code", "ar.caba", "--lock", forged], "skill_lock_drift"),
            ("absent lock", A(("code", "ar.caba"), ("lock_path", absent)), ["--code", "ar.caba", "--lock", absent], "lock_missing"),
            ("over-long lock path", A(("code", "ar.caba"), ("lock_path", longLock)), ["--code", "ar.caba", "--lock", longLock], "skills_lock_path_invalid"),
        };
        foreach (var (label, arguments, cliArguments, code) in cases)
        {
            var cli = Cli(cliArguments);
            var response = call(Line(arguments));
            var ok = IsResult(response) && Text(response) == cli.Out && IsError(response) == (cli.Exit != 0) && (cli.Exit is 0 or 1);
            Check(ok, label + ": text byte-equal to the CLI stdout, isError = (exit 1) | mcp " + Short(response.ToJsonString()) + " | cli exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli.Out));
            if (code is null) Check(cli.Exit == 0 && cli.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal), label + ": succeeds | " + Short(cli.Out));
            else Check(cli.Exit == 1 && cli.Out.Contains("\"code\":\"" + code + "\"", StringComparison.Ordinal), label + ": pinned code " + code + " | " + Short(cli.Out));
        }
        var caba = call(Line(A(("code", "ar.caba"))));
        Check(IsResult(caba) && !IsError(caba) && Text(caba) == cabaLine, "CABA success bytes pinned | " + Short(caba.ToJsonString()) + " | expected " + cabaLine);
        Check(!File.Exists(absent), "an absent lock is not created");

        // ---- 3. usage-equivalent arguments are -32602 ----
        var usage = new (string Label, string Arguments, string[]? CliArguments)[]
        {
            ("no selector", "{}", []),
            ("only lock_path", "{\"lock_path\":" + JsonSerializer.Serialize(lockPath) + "}", ["--lock", lockPath]),
            ("only allow_stub", "{\"allow_stub\":true}", ["--allow-stub"]),
            ("code and country", "{\"code\":\"ar.caba\",\"country\":\"Argentina\"}", ["--code", "ar.caba", "--country", "Argentina"]),
            ("code and municipality", "{\"code\":\"ar.caba\",\"municipality\":\"CABA\"}", ["--code", "ar.caba", "--municipality", "CABA"]),
            ("code, country and municipality", "{\"code\":\"ar.caba\",\"country\":\"Argentina\",\"municipality\":\"CABA\"}", ["--code", "ar.caba", "--country", "Argentina", "--municipality", "CABA"]),
            ("country without municipality", "{\"country\":\"Argentina\"}", ["--country", "Argentina"]),
            ("municipality without country", "{\"municipality\":\"CABA\"}", ["--municipality", "CABA"]),
            ("empty code", "{\"code\":\"\"}", ["--code", ""]),
            ("blank code", "{\"code\":\"   \"}", ["--code", "   "]),
            ("flag-shaped code", "{\"code\":\"--allow-stub\"}", ["--code", "--allow-stub"]),
            ("blank country", "{\"country\":\" \",\"municipality\":\"CABA\"}", ["--country", " ", "--municipality", "CABA"]),
            ("flag-shaped municipality", "{\"country\":\"Argentina\",\"municipality\":\"--x\"}", ["--country", "Argentina", "--municipality", "--x"]),
            ("empty lock_path", "{\"code\":\"ar.caba\",\"lock_path\":\"\"}", ["--code", "ar.caba", "--lock", ""]),
            ("flag-shaped lock_path", "{\"code\":\"ar.caba\",\"lock_path\":\"--lock\"}", ["--code", "ar.caba", "--lock", "--lock"]),
            ("null code", "{\"code\":null}", null),
            ("numeric code", "{\"code\":1}", null),
            ("string allow_stub", "{\"code\":\"ar.caba\",\"allow_stub\":\"true\"}", null),
            ("null lock_path", "{\"code\":\"ar.caba\",\"lock_path\":null}", null),
            ("unknown argument", "{\"code\":\"ar.caba\",\"skills\":\"x\"}", null),
            ("lone-surrogate code", "{\"code\":\"\\ud800\"}", null),
            ("lone-surrogate lock_path", "{\"code\":\"ar.caba\",\"lock_path\":\"\\udc00\"}", null),
        };
        foreach (var (label, arguments, cliArguments) in usage)
        {
            var response = call("{\"jsonrpc\":\"2.0\",\"id\":" + (++id).ToString(CultureInfo.InvariantCulture) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"jurisdiction_resolve\",\"arguments\":" + arguments + "}}");
            Check(ErrorCode(response) == -32602 && response["result"] is null && response["error"]!["message"]!.GetValue<string>().StartsWith("invalid_params", StringComparison.Ordinal),
                "usage-equivalent " + label + " is -32602 | " + Short(response.ToJsonString()));
            if (cliArguments is null) continue;
            var cli = Cli(cliArguments);
            Check(cli.Exit == 1 && cli.Out == UsageLine, "usage-equivalent " + label + " is the CLI jurisdiction_resolve_usage | exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli.Out));
        }

        // ---- 4. a NUL lock path: the in-process CLI handler's bytes ----
        {
            var nul = Path.Combine(scratch, "a\0b.json");
            var (exit, line, error) = InProcessCli(["jurisdiction-resolve", "--code", "ar.caba", "--lock", nul]);
            var response = call(Line(A(("code", "ar.caba"), ("lock_path", nul))));
            Check(exit == 1 && line == PathInvalidLine && error.Length == 0, "fixture: the in-process CLI prints skills_lock_path_invalid for a NUL lock | " + line + " / " + error);
            Check(IsResult(response) && IsError(response) && Text(response) == line, "NUL lock path: text equals the in-process CLI line | " + Short(response.ToJsonString()));
        }

        // ---- 5. typed internal error through the command's seam ----
        {
            var probe = typeof(Bim.Cli.JurisdictionResolveCommand).GetField("WorkProbe", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Check(probe is not null && probe.FieldType == typeof(Action) && probe.GetValue(null) is null, "JurisdictionResolveCommand has a static Action WorkProbe seam, null in production");
            if (probe is not null)
            {
                var failure = new InvalidOperationException("boom jurisdiction_resolve P10-27");
                var diagnostic = "jurisdiction-resolve internal error: System.InvalidOperationException: boom jurisdiction_resolve P10-27";
                var errors = new StringWriter();
                var savedError = Console.Error;
                JsonObject response;
                (int Exit, string Line, string Error) cli;
                probe.SetValue(null, (Action)(() => throw failure));
                try
                {
                    Console.SetError(errors);
                    try { response = call(Line(A(("code", "ar.caba")))); }
                    finally { Console.SetError(savedError); }
                    cli = InProcessCli(["jurisdiction-resolve", "--code", "ar.caba"]);
                }
                finally { probe.SetValue(null, null); }
                Check(IsResult(response) && IsError(response) && Text(response) == InternalLine, "a defect is isError with the typed jurisdiction_resolve_internal_error line | " + Short(response.ToJsonString()));
                Check(!Text(response).Contains("boom", StringComparison.Ordinal), "the defect message is not in the tool text (not McpServer's generic failure)");
                Check(errors.ToString() == diagnostic + Environment.NewLine, "the IntentPlan diagnostic is on the server's stderr once | " + errors);
                Check(cli.Exit == 1 && cli.Line == InternalLine && cli.Error == diagnostic + Environment.NewLine, "the in-process CLI Run prints the same line and diagnostic | " + cli.Line + " / " + cli.Error);
                var again = call(Line(A(("code", "ar.caba"))));
                Check(IsResult(again) && !IsError(again) && Text(again) == cabaLine, "with the seam reset the next call answers the CABA bytes");
            }
        }

        // ---- 6. the real Release Bim.Mcp.dll over stdio ----
        {
            var success = Cli("--code", "ar.caba");
            var refusal = Cli("--code", "zz.nowhere");
            var stdin = Line(A(("code", "ar.caba"))) + "\n" + Line(A(("code", "zz.nowhere"))) + "\n" + Line(A()) + "\n";
            var lines = mcpProcess(stdin).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(lines.Length == 3, "the real Bim.Mcp.dll answers three lines | " + Short(string.Join(" / ", lines)));
            if (lines.Length == 3)
            {
                var first = JsonNode.Parse(lines[0])!.AsObject();
                var second = JsonNode.Parse(lines[1])!.AsObject();
                var third = JsonNode.Parse(lines[2])!.AsObject();
                Check(success.Exit == 0 && IsResult(first) && !IsError(first) && Text(first) == success.Out && Text(first) == cabaLine, "real server: CABA success bytes equal the CLI stdout | " + Short(lines[0]));
                Check(refusal.Exit == 1 && IsResult(second) && IsError(second) && Text(second) == refusal.Out && refusal.Out.Contains("\"code\":\"jurisdiction_unknown\"", StringComparison.Ordinal),
                    "real server: jurisdiction_unknown refusal bytes equal the CLI stdout | " + Short(lines[1]));
                Check(ErrorCode(third) == -32602, "real server: no selector is -32602 | " + Short(lines[2]));
            }
        }

        // ---- 7. static ----
        {
            static string CodeOf(string text) => DocUpdateLinkTests.CodeOf(text);
            var toolPath = Path.Combine(root, "interfaces", "mcp", "JurisdictionResolveTool.cs");
            Check(File.Exists(toolPath), "interfaces/mcp/JurisdictionResolveTool.cs exists");
            var toolCode = File.Exists(toolPath) ? CodeOf(File.ReadAllText(toolPath)) : "";
            Check(toolCode.Contains("JurisdictionResolveCommand.Resolve(", StringComparison.Ordinal), "the tool runs the CLI handler JurisdictionResolveCommand.Resolve");
            var inlined = new[] { "JurisdictionRegistry", "ProjectJurisdiction", "LoadLocked", "SkillLockFile", "SkillLockTool", "SkillPackLookup", "RepositoryRoot", ".Select(", "JsonSerializer",
                "Utf8JsonWriter", "File.", "FileStream", "Directory.", "Console.SetOut", "Console.Out", "Environment.ExitCode", "JurisdictionResolveCommand.Run(", "WorkProbe" }
                .Where(api => toolCode.Contains(api, StringComparison.Ordinal)).ToArray();
            Check(inlined.Length == 0, "the tool has no registry, lock, selection, serializer, file or Console path of its own [" + string.Join(", ", inlined) + "]");
            var owners = Directory.GetFiles(Path.Combine(root, "interfaces", "mcp"), "*.cs")
                .Where(f => new[] { "JurisdictionRegistry.Load(", "JurisdictionRegistry.LoadLocked(", "new ProjectJurisdiction(" }.Any(api => CodeOf(File.ReadAllText(f)).Contains(api, StringComparison.Ordinal)))
                .Select(f => Path.GetFileName(f)).ToArray();
            Check(owners.Length == 0, "no interfaces/mcp source loads or selects a jurisdiction itself [" + string.Join(", ", owners) + "]");
            var command = System.Text.RegularExpressions.Regex.Replace(CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "JurisdictionResolveCommand.cs"))), @"\s+", "");
            Check(command.Contains("Resolve(args).Print()", StringComparison.Ordinal), "the CLI verb prints the very envelope the tool runs (Run is Resolve(args).Print())");
            foreach (var project in new[] { Path.Combine(root, "interfaces", "mcp", "Bim.Mcp.csproj"), Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj"),
                Path.Combine(root, "tests", "cli", "Bim.Cli.Tests.csproj"), Path.Combine(root, "tests", "revit-commands", "Bim.RevitCommands.Tests.csproj") })
            {
                var files = DocUpdateLinkTests.Compiled(File.ReadAllText(project), Path.GetDirectoryName(project)!).SelectMany(c => c.Files).Select(f => f.Replace('\\', '/')).ToArray();
                // Bim.Mcp.csproj compiles interfaces/mcp/*.cs through the SDK default glob; the others link the tool.
                var server = Path.GetFileName(project) == "Bim.Mcp.csproj";
                Check(files.Any(f => f.EndsWith("interfaces/cli/JurisdictionResolveCommand.cs", StringComparison.Ordinal))
                    && (server ? File.Exists(toolPath) : files.Any(f => f.EndsWith("interfaces/mcp/JurisdictionResolveTool.cs", StringComparison.Ordinal))),
                    Path.GetFileName(project) + " compiles the CLI JurisdictionResolveCommand.cs and the tool (shared source, not a copy)");
            }
            var mcpDll = Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll");
            var methods = PlanLayoutsWriteLinkTests.MethodsOf(mcpDll, "Bim.Cli", "JurisdictionResolveCommand");
            Check(methods is not null && methods.Contains("Resolve"), "the Release Bim.Mcp.dll defines Bim.Cli.JurisdictionResolveCommand with Resolve [" + string.Join(", ", methods ?? []) + "]");
        }
    }

    // The CLI handler in process: stdout line (without newline), stderr and exit code captured and restored in a finally.
    static (int Exit, string Line, string Error) InProcessCli(string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var savedOut = Console.Out;
        var savedError = Console.Error;
        var savedExit = Environment.ExitCode;
        int exit;
        try
        {
            Environment.ExitCode = 0;
            Console.SetOut(output);
            Console.SetError(errors);
            Bim.Cli.JurisdictionResolveCommand.Run(args);
            exit = Environment.ExitCode;
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedError);
            Environment.ExitCode = savedExit;
        }
        var line = output.ToString();
        if (line.EndsWith(Environment.NewLine, StringComparison.Ordinal)) line = line[..^Environment.NewLine.Length];
        return (exit, line, errors.ToString());
    }
}
