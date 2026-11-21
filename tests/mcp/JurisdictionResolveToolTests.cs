using System.Diagnostics;
using System.Globalization;
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
//      unknown argument, a lone-surrogate string, an argument name given twice; each case with a CLI spelling is the
//      CLI's jurisdiction_resolve_usage.
//   4. A NUL lock path (cannot travel in a process argument) is the in-process CLI handler's skills_lock_path_invalid
//      line, byte for byte.
//   5. Typed internal error: with the command's WorkProbe seam throwing, the tool is isError with exactly
//      {"status":"failed","code":"jurisdiction_resolve_internal_error"} (the in-process CLI Run prints the same line),
//      the IntentPlan diagnostic on the server's stderr once, never McpServer's generic failure; reset, CABA again.
//   6. The real Release Bim.Mcp.dll over stdio answers one success and one refusal with the CLI bytes.
//   7. Static: the tool runs JurisdictionResolveCommand.Resolve (no registry, lock, selection, serializer or file call
//      of its own); the CLI Run prints Resolve's envelope; the three csprojs compiling McpServer.cs link the command
//      source; the Release Bim.Mcp.dll defines Bim.Cli.JurisdictionResolveCommand with Resolve.
//   8. Static pins, each checker self-tested on synthetic sources: no source Bim.Mcp compiles (linked items and
//      interfaces/mcp/**/*.cs) calls JurisdictionResolveCommand.Run or imports it with using static; over
//      interfaces/**/*.cs (comments and literals stripped) WorkProbe is only its initializer-free declaration and one
//      WorkProbe?.Invoke(); in Execute before Parse(, never assigned; McpInvalidParamsException is constructed only in
//      interfaces/mcp/JurisdictionResolveTool.cs (and, since P5-22, interfaces/mcp/SkillListTool.cs).
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
            ("CABA by country and municipality under a lock", A(("country", "Argentina"), ("municipality", "CABA"), ("lock_path", lockPath)),
                ["--country", "Argentina", "--municipality", "CABA", "--lock", lockPath], null),
            ("stub allowed under a lock", A(("code", "xx.example"), ("lock_path", lockPath), ("allow_stub", true)), ["--code", "xx.example", "--lock", lockPath, "--allow-stub"], null),
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
            ("code given twice", "{\"code\":\"ar.caba\",\"code\":\"ar.caba\"}", ["--code", "ar.caba", "--code", "ar.caba"]),
            ("allow_stub given twice", "{\"code\":\"ar.caba\",\"allow_stub\":true,\"allow_stub\":true}", ["--code", "ar.caba", "--allow-stub", "--allow-stub"]),
            ("lock_path given twice", "{\"code\":\"ar.caba\",\"lock_path\":" + JsonSerializer.Serialize(lockPath) + ",\"lock_path\":" + JsonSerializer.Serialize(lockPath) + "}",
                ["--code", "ar.caba", "--lock", lockPath, "--lock", lockPath]),
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
            Check(Bim.Cli.JurisdictionResolveCommand.WorkProbe is null, "the JurisdictionResolveCommand.WorkProbe seam is null in production");
            {
                var failure = new InvalidOperationException("boom jurisdiction_resolve P10-27");
                var diagnostic = "jurisdiction-resolve internal error: System.InvalidOperationException: boom jurisdiction_resolve P10-27";
                var errors = new StringWriter();
                var savedError = Console.Error;
                JsonObject response;
                (int Exit, string Line, string Error) cli;
                Bim.Cli.JurisdictionResolveCommand.WorkProbe = () => throw failure;
                try
                {
                    Console.SetError(errors);
                    try { response = call(Line(A(("code", "ar.caba")))); }
                    finally { Console.SetError(savedError); }
                    cli = InProcessCli(["jurisdiction-resolve", "--code", "ar.caba"]);
                }
                finally { Bim.Cli.JurisdictionResolveCommand.WorkProbe = null; }
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
            Check(command.Contains("varenvelope=Resolve(args);SkillListCommand.WriteDiagnostic(envelope);(envelopewith{Diagnostic=null}).Print();", StringComparison.Ordinal),
                "the CLI verb prints the very envelope the tool runs (P5-33: Run prints Resolve(args) without its diagnostic after the guarded SkillListCommand.WriteDiagnostic)");
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

        // ---- 8. static pins (review) ----
        {
            // 8a. No source Bim.Mcp.csproj compiles (its linked items and interfaces/mcp/**/*.cs outside bin/obj) calls
            // JurisdictionResolveCommand.Run or imports the class with using static (comments stripped).
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mcpDir = Path.Combine(root, "interfaces", "mcp");
            static bool Built(string path) => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
            var testsDir = Path.GetFullPath(Path.Combine(root, "tests")) + Path.DirectorySeparatorChar;
            var compiled = DocUpdateLinkTests.Compiled(File.ReadAllText(Path.Combine(mcpDir, "Bim.Mcp.csproj")), mcpDir).SelectMany(c => c.Files)
                .Concat(Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories).Where(path => !Built(Path.GetRelativePath(mcpDir, path))))
                .Select(path => Path.GetFullPath(path)).Where(path => !path.StartsWith(testsDir, comparison) && File.Exists(path)).Distinct().ToArray();
            var commandFile = Path.GetFullPath(Path.Combine(root, "interfaces", "cli", "JurisdictionResolveCommand.cs"));
            Check(compiled.Contains(commandFile, StringComparer.FromComparison(comparison)) && compiled.Any(path => Path.GetFileName(path) == "JurisdictionResolveTool.cs"),
                "the Bim.Mcp source set includes JurisdictionResolveCommand.cs and JurisdictionResolveTool.cs");
            var runUsers = compiled.Select(path => (Name: Path.GetFileName(path), Found: RunUses(File.ReadAllText(path)))).Where(u => u.Found.Length > 0)
                .Select(u => u.Name + " " + string.Join(" ", u.Found)).ToArray();
            Check(runUsers.Length == 0, "no source Bim.Mcp compiles calls JurisdictionResolveCommand.Run or imports it with using static [" + string.Join(", ", runUsers) + "]");
            Check(RunUses("class T { void X() { var e = JurisdictionResolveCommand.Resolve(a); } } // JurisdictionResolveCommand.Run(a)").Length == 0
                && RunUses("class T { void X() { JurisdictionResolveCommand . Run ( a ); } }").Length == 1
                && RunUses("class T { void X() { Bim.Cli.JurisdictionResolveCommand.Run(a); } }").Length == 1
                && RunUses("using static Bim.Cli.JurisdictionResolveCommand;\nclass T { }").Length == 1
                && RunUses("using static global::Bim.Cli.JurisdictionResolveCommand;").Length == 1
                && RunUses("/* JurisdictionResolveCommand.Run(a) */ class T { }").Length == 0,
                "self-test: the Run checker flags qualified calls at any spacing and using static, and ignores Resolve and comments");

            // 8b and 8c over interfaces/**/*.cs (comments and literals stripped).
            var interfaces = Path.Combine(root, "interfaces");
            var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories).Where(path => !Built(Path.GetRelativePath(interfaces, path))).Order(StringComparer.Ordinal)
                .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file))).ToArray();
            var seam = SeamProblems(sources);
            Check(seam.Count == 0, "interfaces/**/*.cs names WorkProbe only in its declaration (no initializer) and one WorkProbe?.Invoke(); in Execute before Parse(, never assigned [" + string.Join("; ", seam) + "]");
            const string SeamPath = "interfaces/cli/JurisdictionResolveCommand.cs";
            const string Decl = "internal static Action? WorkProbe { get; set; }";
            static string Command(string executeBody, string declaration = Decl, string extra = "") =>
                "internal static class JurisdictionResolveCommand { " + declaration + " public static IntentEnvelope Resolve(string[] args) { try { return Execute(args); } catch (Exception e) { return null; } } static IntentEnvelope Execute(string[] args) { " + executeBody + " } static Options? Parse(string[] args) => null; " + extra + " }";
            const string Good = "WorkProbe?.Invoke(); if (Parse(args) is not { } options) return UsageRefusal; return null;";
            Check(SeamProblems([(SeamPath, Command(Good))]).Count == 0
                && SeamProblems([(SeamPath, Command("WorkProbe ?. Invoke ( ) ;\n if (Parse ( args ) is null) return null; // WorkProbe = null;\n var s = \"WorkProbe = x\"; return null;"))]).Count == 0
                && SeamProblems([(SeamPath, Command(Good)), ("interfaces/cli/GenerateTypologyCommand.cs", "class G { internal static Action? GuardedWorkProbe { get; set; } void X() { GuardedWorkProbe?.Invoke(); } }")]).Count == 0,
                "self-test: the seam checker accepts the real shape at any spacing, ignoring comments, literals and GuardedWorkProbe");
            var seamEvasions = new (string Label, (string, string)[] Files)[]
            {
                ("an initializer", [(SeamPath, Command(Good, "internal static Action? WorkProbe { get; set; } = null;"))]),
                ("a field declaration", [(SeamPath, Command(Good, "internal static Action? WorkProbe;"))]),
                ("the declaration missing", [(SeamPath, Command(Good, ""))]),
                ("an assignment in the command", [(SeamPath, Command(Good, Decl, "static void Set() { WorkProbe = () => throw null!; }"))]),
                ("an assignment in another interfaces file", [(SeamPath, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { JurisdictionResolveCommand.WorkProbe = null; } }")]),
                ("a += assignment", [(SeamPath, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { JurisdictionResolveCommand.WorkProbe += null; } }")]),
                ("a ??= assignment", [(SeamPath, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { JurisdictionResolveCommand.WorkProbe ??= null; } }")]),
                ("the invoke after Parse(", [(SeamPath, Command("if (Parse(args) is null) return null; WorkProbe?.Invoke(); return null;"))]),
                ("the invoke outside Execute", [(SeamPath, Command("if (Parse(args) is null) return null; return null;", Decl, "static void Other() { WorkProbe?.Invoke(); }"))]),
                ("the invoke missing", [(SeamPath, Command("if (Parse(args) is null) return null; return null;"))]),
                ("a second invoke", [(SeamPath, Command("WorkProbe?.Invoke(); WorkProbe?.Invoke(); if (Parse(args) is null) return null; return null;"))]),
                ("a use in the tool", [(SeamPath, Command(Good)), ("interfaces/mcp/JurisdictionResolveTool.cs", "class T { void X() { JurisdictionResolveCommand.WorkProbe?.Invoke(); } }")]),
            };
            foreach (var (label, files) in seamEvasions)
                Check(SeamProblems(files).Count > 0, "self-test: the seam checker flags " + label);

            var raisers = ExceptionProblems(sources);
            Check(raisers.Count == 0, "McpInvalidParamsException is constructed only in interfaces/mcp/JurisdictionResolveTool.cs and interfaces/mcp/SkillListTool.cs, at least once in each [" + string.Join("; ", raisers) + "]");
            const string ToolFile = "interfaces/mcp/JurisdictionResolveTool.cs";
            const string SkillFile = "interfaces/mcp/SkillListTool.cs";
            const string Throws = "class T { void X() { throw new McpInvalidParamsException(M); } }";
            Check(ExceptionProblems([(ToolFile, Throws), (SkillFile, Throws), ("interfaces/mcp/McpServer.cs", "class S { void X() { try { } catch (McpInvalidParamsException e) { } var s = \"new McpInvalidParamsException(x)\"; } }")]).Count == 0
                && ExceptionProblems([(ToolFile, "class T { }"), (SkillFile, Throws)]).Count > 0
                && ExceptionProblems([(ToolFile, Throws), (SkillFile, "class S { }")]).Count > 0
                && ExceptionProblems([(ToolFile, Throws)]).Count > 0
                && ExceptionProblems([(ToolFile, Throws), (SkillFile, Throws), ("interfaces/mcp/DocUpdateTool.cs", "class D { void X() { throw new Bim.Mcp.McpInvalidParamsException ( M ); } }")]).Count > 0
                && ExceptionProblems([(ToolFile, Throws), (SkillFile, Throws), ("interfaces/mcp/DocUpdateTool.cs", "class D { void X() { McpInvalidParamsException e = new(M); } }")]).Count > 0,
                "self-test: the constructor checker accepts both allowed tools' throws and a catch, and flags another file's new (qualified or target-typed) and an allowed tool without one or missing");
        }
    }

    static readonly System.Text.RegularExpressions.Regex RunUse = new(
        @"\bJurisdictionResolveCommand\s*\.\s*Run\b|\busing\s+static\s+(?:global\s*::\s*)?(?:Bim\s*\.\s*Cli\s*\.\s*)?JurisdictionResolveCommand\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The calls of JurisdictionResolveCommand.Run (or a using static of the class) in the code of a source.
    static string[] RunUses(string source) =>
        RunUse.Matches(DocUpdateLinkTests.CodeOf(source)).Select(m => System.Text.RegularExpressions.Regex.Replace(m.Value, @"\s+", " ")).ToArray();

    // Over (relative path, source) pairs under interfaces/, comments and literals stripped: WorkProbe (whole word, so not
    // GuardedWorkProbe) is named exactly twice, both in interfaces/cli/JurisdictionResolveCommand.cs: the declaration
    // internal static Action? WorkProbe { get; set; } with no initializer and one WorkProbe?.Invoke(); inside Execute
    // before Execute's first Parse( call; no assignment (=, +=, ??=, ...) anywhere.
    static List<string> SeamProblems(IEnumerable<(string Name, string Source)> files)
    {
        const string Command = "interfaces/cli/JurisdictionResolveCommand.cs";
        var problems = new List<string>();
        var total = 0;
        var sawCommand = false;
        foreach (var (name, source) in files)
        {
            var code = InternalErrorEnvelopeToolTests.CodeOf(source, keepLiterals: false);
            total += System.Text.RegularExpressions.Regex.Matches(code, @"\bWorkProbe\b").Count;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bWorkProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": WorkProbe assigned");
            if (name != Command) continue;
            sawCommand = true;
            var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+static\s+Action\s*\?\s*WorkProbe\s*\{\s*get\s*;\s*set\s*;\s*\}(?!\s*=)").Count;
            if (declarations != 1) problems.Add(name + ": the declaration without initializer found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = System.Text.RegularExpressions.Regex.Matches(code, @"\bWorkProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;");
            var execute = System.Text.RegularExpressions.Regex.Match(code, @"\bstatic\s+IntentEnvelope\s+Execute\s*\(");
            if (invokes.Count != 1 || !execute.Success) { problems.Add(name + ": " + invokes.Count.ToString(CultureInfo.InvariantCulture) + " WorkProbe?.Invoke() calls, Execute " + (execute.Success ? "found" : "missing")); continue; }
            var parse = System.Text.RegularExpressions.Regex.Match(code[(execute.Index + execute.Length)..], @"\bParse\s*\(");
            var parseAt = parse.Success ? execute.Index + execute.Length + parse.Index : -1;
            if (parseAt < 0 || invokes[0].Index < execute.Index || invokes[0].Index > parseAt) problems.Add(name + ": WorkProbe?.Invoke() not inside Execute before its Parse(");
        }
        if (!sawCommand) problems.Add(Command + " not found");
        if (total != 2) problems.Add("WorkProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // Over (relative path, source) pairs under interfaces/, comments and literals stripped: McpInvalidParamsException is
    // constructed (new McpInvalidParamsException(, qualified or not, or a target-typed new( of a variable of that type)
    // only in the allowed files interfaces/mcp/JurisdictionResolveTool.cs and (P5-22) interfaces/mcp/SkillListTool.cs, and
    // at least once in each of them (an allowed file missing from the set is flagged too).
    static List<string> ExceptionProblems(IEnumerable<(string Name, string Source)> files)
    {
        string[] allowed = ["interfaces/mcp/JurisdictionResolveTool.cs", "interfaces/mcp/SkillListTool.cs"];
        var problems = new List<string>();
        var constructions = allowed.ToDictionary(file => file, _ => 0, StringComparer.Ordinal);
        foreach (var (name, source) in files)
        {
            var code = InternalErrorEnvelopeToolTests.CodeOf(source, keepLiterals: false);
            var count = System.Text.RegularExpressions.Regex.Matches(code, @"\bnew\s+(?:global\s*::\s*)?(?:Bim\s*\.\s*Mcp\s*\.\s*)?McpInvalidParamsException\s*\(").Count
                + System.Text.RegularExpressions.Regex.Matches(code, @"\bMcpInvalidParamsException\s+\w+\s*=\s*new\s*\(").Count;
            if (constructions.ContainsKey(name)) constructions[name] += count;
            else if (count > 0) problems.Add(name + ": constructs McpInvalidParamsException " + count.ToString(CultureInfo.InvariantCulture) + " times");
        }
        foreach (var (file, count) in constructions)
            if (count == 0) problems.Add(file + ": no construction");
        return problems;
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
