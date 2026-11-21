using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;

// P5-22: the read-only MCP tool skill_list, the MCP face of the P5-21 CLI verb `skill-list [--skills skills-directory]`
// (interfaces/cli/SkillListCommand.cs), run through the verb's shared handler SkillListCommand.List.
// Oracles:
//   1. tools/list: listed once right after jurisdiction_resolve, read-only (description names the CLI verb skill-list),
//      _meta permission_level read and annotations readOnlyHint true only; the schema has exactly the optional string
//      property skills, required [] and no other property.
//   2. Byte equality with the real CLI process: for every pinned argument set (no arguments = the package-first default,
//      the repository skills/ by absolute path, with a trailing separator and relative to the working directory, a
//      generated temp skills directory, a malformed skill.json (json_invalid at bim.broken.pack/skill.json), a malformed
//      jurisdiction.json sidecar (json_invalid, subject <CABA>/jurisdiction.json), a missing directory (root_missing))
//      the tool text equals the CLI stdout line without its newline and isError equals (CLI exit != 0); the repository
//      listing names skills_path and the skills/ directory names in ordinal order.
//   3. Usage-equivalent arguments are JSON-RPC -32602 (never a tool result): a blank or "--"-prefixed skills value, null
//      or wrongly typed skills, an unknown argument, skills given twice, a lone-surrogate string; each case with a CLI
//      spelling is the CLI's skill_list_usage line.
//   4. A NUL skills path (cannot travel in a process argument) is the in-process CLI Run line, byte for byte: the typed
//      skill_list_internal_error, with the server's stderr equal to the CLI's IntentPlan diagnostic (non-empty).
//   5. Read-only: the repository skills tree and the fixture trees are unchanged (paths and bytes); a missing directory is
//      not created.
//   6. The real Release Bim.Mcp.dll over stdio answers the default listing, one success, one refusal and one -32602 with
//      the CLI bytes.
//   7. Static: the tool calls SkillListCommand.List and has no loader, registry, serializer, file or Console.Out path of
//      its own; no interfaces/mcp source calls SkillListCommand.Run; the four csprojs compiling McpServer.cs compile the
//      CLI SkillListCommand.cs and the tool; the Release Bim.Mcp.dll defines Bim.Cli.SkillListCommand with List.
//   8. P5-29 internal error through the seam: with SkillListCommand.ListingWorkProbe throwing a non-refusal exception
//      (in process), the tool answers isError with exactly the CLI's skill_list_internal_error line (equal to the
//      in-process CLI Run line) for the default, an explicit and a missing skills directory, the server's stderr carries the
//      IntentPlan diagnostic once (equal to the in-process CLI's), the defect message never reaches the text, and
//      usage-equivalent arguments stay -32602 without reaching the probe. The seam is null before and after; with it
//      reset the next calls answer the CLI listing and refusal bytes. The tool source never names ListingWorkProbe.
//   9. P5-31 post-load seam and a throwing stderr: with SkillListCommand.LoadedProbe (invoked with the load right after
//      JurisdictionRegistry.Load) throwing, the tool answers isError with exactly the typed skill_list_internal_error line
//      for the default, an explicit and a missing skills directory, the probe running once, the server's stderr the
//      IntentPlan diagnostic once, both equal to the in-process CLI Run, the defect message never in the text; usage stays
//      -32602 without reaching the probe. With a Console.Error whose every write throws, the internal error (post-load
//      probe, listing probe, NUL path) is still isError with the typed line through tools/call and through
//      SkillListTool.Execute directly (nothing escapes the tool), one stderr write attempt; a listing and a root_missing
//      refusal keep the CLI bytes and never write stderr. The tool source never names LoadedProbe; seams null after.
internal static class SkillListToolTests
{
    const string ToolName = "skill_list";
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stub = "xx.example.codigo-ejemplo";
    const string InternalLine = "{\"status\":\"failed\",\"code\":\"skill_list_internal_error\"}";
    const string UsageLine = "{\"status\":\"failed\",\"code\":\"skill_list_usage\",\"detail\":\"usage: skill-list [--skills skills-directory]\"}";

    public static void Run(string root, string work, string dotnet, string cliDll, Func<string, JsonObject> call, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "skill_list: " + name);
        static string Short(string text) => text.Length > 600 ? text[..600] + "..." : text;
        var scratch = Path.Combine(work, "skill-list-mcp");
        Directory.CreateDirectory(scratch);
        var skills = Path.GetFullPath(Path.Combine(root, "skills"));
        var skillsDigest = TreeDigest(skills);

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            start.ArgumentList.Add("skill-list");
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
        var id = 2900;
        string Line(JsonObject arguments) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = ToolName, ["arguments"] = arguments },
        }.ToJsonString();
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        static bool IsResult(JsonObject response) => response["result"] is JsonObject result && result["content"] is JsonArray && !response.ContainsKey("error");
        static int? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<int>();
        static JsonObject A(params (string Key, JsonNode? Value)[] pairs) { var o = new JsonObject(); foreach (var (k, v) in pairs) o[k] = v; return o; }

        // ---- 1. tools/list ----
        var listed = call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }.ToJsonString())["result"]!["tools"]!.AsArray();
        var names = listed.Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Check(names.Count(n => n == ToolName) == 1 && Array.IndexOf(names, ToolName) == Array.IndexOf(names, "jurisdiction_resolve") + 1,
            "listed once, right after jurisdiction_resolve [" + string.Join(",", names.Take(18)) + "]");
        var tool = listed.FirstOrDefault(t => t!["name"]!.GetValue<string>() == ToolName);
        if (tool is null) return;
        Check(tool["description"]!.GetValue<string>().Contains("skill-list", StringComparison.Ordinal)
            && tool["description"]!.GetValue<string>().Contains("read-only", StringComparison.OrdinalIgnoreCase), "described read-only, naming the CLI verb skill-list");
        Check(tool["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>() == "read" && tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true
            && tool["annotations"]!.AsObject().Count == 1, "lists _meta permission_level read and annotations readOnlyHint true only");
        var schema = tool["inputSchema"]!.AsObject();
        var properties = schema["properties"]!.AsObject();
        Check(string.Join(",", properties.Select(p => p.Key + ":" + p.Value!["type"]!.GetValue<string>())) == "skills:string"
            && schema["required"]!.AsArray().Count == 0 && schema["additionalProperties"]!.GetValue<bool>() == false,
            "schema: optional skills (string), nothing required, no other property | " + schema.ToJsonString());

        // ---- 2. byte equality with the CLI process ----
        var generated = Path.Combine(scratch, "generated");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(generated, Caba));
        CopyDirectory(Path.Combine(skills, Stub), Path.Combine(generated, Stub));
        var malformed = Path.Combine(scratch, "malformed");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(malformed, Caba));
        Directory.CreateDirectory(Path.Combine(malformed, "bim.broken.pack"));
        File.WriteAllText(Path.Combine(malformed, "bim.broken.pack", "skill.json"), "{ \"manifest_version\": 1, ", new UTF8Encoding(false));
        var badSidecar = Path.Combine(scratch, "bad-sidecar");
        CopyDirectory(Path.Combine(skills, Caba), Path.Combine(badSidecar, Caba));
        File.WriteAllText(Path.Combine(badSidecar, Caba, "jurisdiction.json"), "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));
        var absent = Path.Combine(scratch, "absent-skills");
        var fixtures = new[] { generated, malformed, badSidecar }.Select(d => (Dir: d, Digest: TreeDigest(d))).ToArray();
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, skills);
        var cases = new (string Label, JsonObject Arguments, string[] CliArguments, string? Code, string? Contains)[]
        {
            ("default (no arguments)", A(), [], null, "\"skills_path\":" + JsonSerializer.Serialize(skills) + ","),
            ("repository skills by absolute path", A(("skills", skills)), ["--skills", skills], null, "\"skills_path\":" + JsonSerializer.Serialize(skills) + ","),
            ("repository skills with a trailing separator", A(("skills", skills + Path.DirectorySeparatorChar)), ["--skills", skills + Path.DirectorySeparatorChar], null, "\"skills_path\":" + JsonSerializer.Serialize(skills) + ","),
            ("repository skills relative to the working directory", A(("skills", relative)), ["--skills", relative], null, "\"skills_path\":" + JsonSerializer.Serialize(skills) + ","),
            ("generated temp skills directory", A(("skills", generated)), ["--skills", generated], null, "\"packs\":[{\"id\":\"" + Caba + "\","),
            ("malformed skill.json", A(("skills", malformed)), ["--skills", malformed], SkillCodes.JsonInvalid, "\"file\":\"bim.broken.pack/skill.json\""),
            ("malformed jurisdiction.json sidecar", A(("skills", badSidecar)), ["--skills", badSidecar], JurisdictionCodes.JsonInvalid, "\"subject\":\"" + Caba + "/jurisdiction.json\""),
            ("missing directory", A(("skills", absent)), ["--skills", absent], SkillCodes.RootMissing, null),
        };
        string? repositoryLine = null;
        foreach (var (label, arguments, cliArguments, code, contains) in cases)
        {
            var cli = Cli(cliArguments);
            var response = call(Line(arguments));
            var ok = IsResult(response) && Text(response) == cli.Out && IsError(response) == (cli.Exit != 0) && (cli.Exit is 0 or 1);
            Check(ok, label + ": text byte-equal to the CLI stdout, isError = (exit 1) | mcp " + Short(response.ToJsonString()) + " | cli exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli.Out));
            if (code is null) Check(cli.Exit == 0 && cli.Out.StartsWith("{\"status\":\"succeeded\",\"skills_path\":", StringComparison.Ordinal), label + ": succeeds | " + Short(cli.Out));
            else Check(cli.Exit == 1 && cli.Out.StartsWith("{\"status\":\"failed\",\"code\":\"" + code + "\"", StringComparison.Ordinal), label + ": pinned code " + code + " | " + Short(cli.Out));
            if (contains is not null) Check(cli.Out.Contains(contains, StringComparison.Ordinal), label + ": names " + contains + " | " + Short(cli.Out));
            if (label == "default (no arguments)") repositoryLine = cli.Out;
            else if (code is null && arguments.ContainsKey("skills") && label.StartsWith("repository", StringComparison.Ordinal))
                Check(cli.Out == repositoryLine, label + ": the same listing as the default");
        }
        try
        {
            using var document = JsonDocument.Parse(repositoryLine ?? "{}");
            var ids = document.RootElement.GetProperty("packs").EnumerateArray().Select(pack => pack.GetProperty("id").GetString()!).ToArray();
            var directories = Directory.GetDirectories(skills).Select(directory => Path.GetFileName(directory)).Order(StringComparer.Ordinal).ToArray();
            Check(ids.SequenceEqual(directories), "the repository listing names the skills/ directories in ordinal order [" + string.Join(",", ids) + "]");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { Check(false, "the repository listing is a JSON envelope | " + error.Message); }

        // ---- 3. usage-equivalent arguments are -32602 ----
        var usage = new (string Label, string Arguments, string[]? CliArguments)[]
        {
            ("empty skills", "{\"skills\":\"\"}", ["--skills", ""]),
            ("blank skills", "{\"skills\":\"   \"}", ["--skills", "   "]),
            ("flag-shaped skills", "{\"skills\":\"--skills\"}", ["--skills", "--skills"]),
            ("flag-shaped skills (--x)", "{\"skills\":\"--x\"}", ["--skills", "--x"]),
            ("skills given twice", "{\"skills\":" + JsonSerializer.Serialize(skills) + ",\"skills\":" + JsonSerializer.Serialize(skills) + "}", ["--skills", skills, "--skills", skills]),
            ("null skills", "{\"skills\":null}", null),
            ("numeric skills", "{\"skills\":1}", null),
            ("array skills", "{\"skills\":[\"x\"]}", null),
            ("unknown argument", "{\"skills_path\":\"x\"}", null),
            ("lone-surrogate skills", "{\"skills\":\"\\ud800\"}", null),
        };
        foreach (var (label, arguments, cliArguments) in usage)
        {
            var response = call("{\"jsonrpc\":\"2.0\",\"id\":" + (++id).ToString(CultureInfo.InvariantCulture) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"skill_list\",\"arguments\":" + arguments + "}}");
            Check(ErrorCode(response) == -32602 && response["result"] is null && response["error"]!["message"]!.GetValue<string>().StartsWith("invalid_params", StringComparison.Ordinal),
                "usage-equivalent " + label + " is -32602 | " + Short(response.ToJsonString()));
            if (cliArguments is null) continue;
            var cli = Cli(cliArguments);
            Check(cli.Exit == 1 && cli.Out == UsageLine, "usage-equivalent " + label + " is the CLI skill_list_usage | exit " + cli.Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli.Out));
        }

        // ---- 4. a NUL skills path: the in-process CLI handler's bytes ----
        {
            var nul = Path.Combine(scratch, "a\0b");
            var (exit, line, cliError) = InProcessCli(["skill-list", "--skills", nul]);
            var errors = new StringWriter();
            var savedError = Console.Error;
            JsonObject response;
            Console.SetError(errors);
            try { response = call(Line(A(("skills", nul)))); }
            finally { Console.SetError(savedError); }
            Check(exit == 1 && line == InternalLine && cliError.StartsWith("skill-list internal error: ", StringComparison.Ordinal),
                "fixture: the in-process CLI answers a NUL skills path with the typed skill_list_internal_error line and its diagnostic | " + line + " / " + cliError);
            Check(IsResult(response) && IsError(response) && Text(response) == line && Text(response).Contains("\"code\":\"skill_list_internal_error\"", StringComparison.Ordinal),
                "NUL skills path: text equals the in-process CLI skill_list_internal_error line | " + Short(response.ToJsonString()));
            Check(errors.ToString().Length > 0 && errors.ToString() == cliError, "NUL skills path: the server's stderr is the in-process CLI diagnostic once | " + errors + " / " + cliError);
        }

        // ---- 5. read-only ----
        Check(TreeDigest(skills) == skillsDigest, "the repository skills tree is unchanged (paths and bytes)");
        foreach (var (dir, digest) in fixtures) Check(TreeDigest(dir) == digest, "the fixture " + Path.GetFileName(dir) + " is unchanged");
        Check(!Directory.Exists(absent), "a missing skills directory is not created");

        // ---- 6. the real Release Bim.Mcp.dll over stdio ----
        {
            var standard = Cli();
            var success = Cli("--skills", generated);
            var refusal = Cli("--skills", absent);
            var stdin = Line(A()) + "\n" + Line(A(("skills", generated))) + "\n" + Line(A(("skills", absent))) + "\n" + Line(A(("skills", " "))) + "\n";
            var lines = mcpProcess(stdin).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(lines.Length == 4, "the real Bim.Mcp.dll answers four lines | " + Short(string.Join(" / ", lines)));
            if (lines.Length == 4)
            {
                var parsed = lines.Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
                Check(standard.Exit == 0 && IsResult(parsed[0]) && !IsError(parsed[0]) && Text(parsed[0]) == standard.Out && standard.Out == repositoryLine,
                    "real server: the default listing equals the CLI stdout | " + Short(lines[0]));
                Check(success.Exit == 0 && IsResult(parsed[1]) && !IsError(parsed[1]) && Text(parsed[1]) == success.Out, "real server: the generated listing equals the CLI stdout | " + Short(lines[1]));
                Check(refusal.Exit == 1 && IsResult(parsed[2]) && IsError(parsed[2]) && Text(parsed[2]) == refusal.Out && refusal.Out.Contains("\"code\":\"" + SkillCodes.RootMissing + "\"", StringComparison.Ordinal),
                    "real server: the root_missing refusal equals the CLI stdout | " + Short(lines[2]));
                Check(ErrorCode(parsed[3]) == -32602, "real server: a blank skills value is -32602 | " + Short(lines[3]));
            }
            Check(TreeDigest(skills) == skillsDigest, "the repository skills tree is unchanged after the real server ran");
        }

        // ---- 7. static ----
        {
            static string CodeOf(string text) => DocUpdateLinkTests.CodeOf(text);
            var toolPath = Path.Combine(root, "interfaces", "mcp", "SkillListTool.cs");
            Check(File.Exists(toolPath), "interfaces/mcp/SkillListTool.cs exists");
            var toolCode = File.Exists(toolPath) ? CodeOf(File.ReadAllText(toolPath)) : "";
            Check(System.Text.RegularExpressions.Regex.Matches(toolCode, @"\bSkillListCommand\s*\.\s*List\s*\(").Count == 1, "the tool calls the CLI handler SkillListCommand.List once");
            var inlined = new[] { "JurisdictionRegistry", "SkillLoader", "LoadDirectory", "SkillParser", "SkillPackLookup", "RepositoryRoot", "JsonSerializer", "JsonObject", "Utf8JsonWriter",
                "File.", "FileStream", "Directory.", "Path.", "Console.SetOut", "Console.Out", "Environment.ExitCode", "SkillListCommand.Run(", "IntentPlan." }
                .Where(api => toolCode.Contains(api, StringComparison.Ordinal)).ToArray();
            Check(inlined.Length == 0, "the tool has no loader, registry, serializer, path, file or Console path of its own [" + string.Join(", ", inlined) + "]");
            var mcpDir = Path.Combine(root, "interfaces", "mcp");
            var runUsers = Directory.GetFiles(mcpDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(mcpDir, f).Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(CodeOf(File.ReadAllText(f)), @"\bSkillListCommand\s*\.\s*Run\b|\busing\s+static\s+(?:global\s*::\s*)?(?:Bim\s*\.\s*Cli\s*\.\s*)?SkillListCommand\b"))
                .Select(f => Path.GetFileName(f)).ToArray();
            Check(runUsers.Length == 0, "no interfaces/mcp source calls SkillListCommand.Run or imports it with using static [" + string.Join(", ", runUsers) + "]");
            var command = System.Text.RegularExpressions.Regex.Replace(CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "SkillListCommand.cs"))), @"\s+", "");
            Check(command.Contains("returnList(skills);", StringComparison.Ordinal) && command.Contains("publicstaticIntentEnvelopeList(string?skillsDirectory)", StringComparison.Ordinal),
                "the CLI verb prints the envelope of the very handler the tool runs (Execute returns List(skills))");
            foreach (var project in new[] { Path.Combine(root, "interfaces", "mcp", "Bim.Mcp.csproj"), Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj"),
                Path.Combine(root, "tests", "cli", "Bim.Cli.Tests.csproj"), Path.Combine(root, "tests", "revit-commands", "Bim.RevitCommands.Tests.csproj") })
            {
                var files = DocUpdateLinkTests.Compiled(File.ReadAllText(project), Path.GetDirectoryName(project)!).SelectMany(c => c.Files).Select(f => f.Replace('\\', '/')).ToArray();
                var server = Path.GetFileName(project) == "Bim.Mcp.csproj";
                Check(files.Any(f => f.EndsWith("interfaces/cli/SkillListCommand.cs", StringComparison.Ordinal))
                    && (server ? File.Exists(toolPath) : files.Any(f => f.EndsWith("interfaces/mcp/SkillListTool.cs", StringComparison.Ordinal))),
                    Path.GetFileName(project) + " compiles the CLI SkillListCommand.cs and the tool (shared source, not a copy)");
            }
            var mcpDll = Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll");
            var methods = PlanLayoutsWriteLinkTests.MethodsOf(mcpDll, "Bim.Cli", "SkillListCommand");
            Check(methods is not null && methods.Contains("List"), "the Release Bim.Mcp.dll defines Bim.Cli.SkillListCommand with List [" + string.Join(", ", methods ?? []) + "]");
        }

        // ---- 8. P5-29 typed internal error through the ListingWorkProbe seam ----
        {
            Check(Bim.Cli.SkillListCommand.ListingWorkProbe is null, "P5-29: the SkillListCommand.ListingWorkProbe seam is null in production");
            var toolPath = Path.Combine(root, "interfaces", "mcp", "SkillListTool.cs");
            Check(!System.Text.RegularExpressions.Regex.IsMatch(File.Exists(toolPath) ? File.ReadAllText(toolPath) : "", @"\bListingWorkProbe\b"), "P5-29: the tool source never names ListingWorkProbe");
            var listing = Cli("--skills", generated);
            var refusal = Cli("--skills", absent);
            var probes = 0;
            foreach (var failure in new Exception[] { new InvalidOperationException("boom skill_list P5-29"), new IOException("disk skill_list P5-29") })
            {
                var diagnostic = "skill-list internal error: " + failure.GetType().FullName + ": " + failure.Message;
                Bim.Cli.SkillListCommand.ListingWorkProbe = () => { probes++; throw failure; };
                try
                {
                    foreach (var (label, arguments, cliArguments) in new (string, JsonObject, string[])[]
                    {
                        ("default directory", A(), ["skill-list"]),
                        ("explicit skills", A(("skills", generated)), ["skill-list", "--skills", generated]),
                        ("missing directory", A(("skills", absent)), ["skill-list", "--skills", absent]),
                    })
                    {
                        var errors = new StringWriter();
                        var savedError = Console.Error;
                        JsonObject response;
                        var before = probes;
                        Console.SetError(errors);
                        try { response = call(Line(arguments)); }
                        finally { Console.SetError(savedError); }
                        var toolProbes = probes - before;
                        var cli = InProcessCli(cliArguments);
                        Check(IsResult(response) && IsError(response) && Text(response) == InternalLine && toolProbes == 1,
                            "P5-29 " + failure.GetType().Name + " " + label + ": isError with the typed skill_list_internal_error line | " + Short(response.ToJsonString()));
                        Check(!Text(response).Contains("P5-29", StringComparison.Ordinal), "P5-29 " + failure.GetType().Name + " " + label + ": the defect message is not in the tool text");
                        Check(errors.ToString() == diagnostic + Environment.NewLine, "P5-29 " + failure.GetType().Name + " " + label + ": the IntentPlan diagnostic is on the server's stderr once | " + errors);
                        Check(cli.Exit == 1 && cli.Line == Text(response) && cli.Error == errors.ToString(),
                            "P5-29 " + failure.GetType().Name + " " + label + ": the in-process CLI Run prints the same line and diagnostic, exit 1 | " + cli.Line + " / " + cli.Error);
                    }
                    foreach (var arguments in new[] { A(("skills", " ")), A(("skills", "--x")), A(("other", "x")) })
                    {
                        var before = probes;
                        var response = call(Line(arguments));
                        Check(ErrorCode(response) == -32602 && probes == before, "P5-29: usage-equivalent " + arguments.ToJsonString() + " stays -32602 and never reaches the probe | " + Short(response.ToJsonString()));
                    }
                }
                finally { Bim.Cli.SkillListCommand.ListingWorkProbe = null; }
            }
            Check(Bim.Cli.SkillListCommand.ListingWorkProbe is null, "P5-29: the seam is null again after the forced runs");
            var again = call(Line(A(("skills", generated))));
            var againRefusal = call(Line(A(("skills", absent))));
            Check(listing.Exit == 0 && IsResult(again) && !IsError(again) && Text(again) == listing.Out, "P5-29: with the seam reset the next call answers the CLI listing bytes | " + Short(again.ToJsonString()));
            Check(refusal.Exit == 1 && IsResult(againRefusal) && IsError(againRefusal) && Text(againRefusal) == refusal.Out, "P5-29: with the seam reset a refusal answers the CLI bytes | " + Short(againRefusal.ToJsonString()));
            Check(!Directory.Exists(absent), "P5-29: the missing directory is not created");
        }

        // ---- 9. P5-31 post-load seam and a throwing stderr ----
        {
            Check(Bim.Cli.SkillListCommand.LoadedProbe is null && Bim.Cli.SkillListCommand.ListingWorkProbe is null, "P5-31: both seams are null in production");
            var toolPath = Path.Combine(root, "interfaces", "mcp", "SkillListTool.cs");
            Check(!System.Text.RegularExpressions.Regex.IsMatch(File.Exists(toolPath) ? File.ReadAllText(toolPath) : "", @"\bLoadedProbe\b"), "P5-31: the tool source never names LoadedProbe");
            var listing = Cli("--skills", generated);
            var refusal = Cli("--skills", absent);
            var probes = 0;
            foreach (var failure in new Exception[] { new InvalidOperationException("boom skill_list P5-31"), new IOException("disk skill_list P5-31") })
            {
                var name = failure.GetType().Name;
                var diagnostic = "skill-list internal error: " + failure.GetType().FullName + ": " + failure.Message;
                Bim.Cli.SkillListCommand.LoadedProbe = load => { probes++; throw failure; };
                try
                {
                    foreach (var (label, arguments, cliArguments) in new (string, JsonObject, string[])[]
                    {
                        ("default directory", A(), ["skill-list"]),
                        ("explicit skills", A(("skills", generated)), ["skill-list", "--skills", generated]),
                        ("missing directory", A(("skills", absent)), ["skill-list", "--skills", absent]),
                    })
                    {
                        var errors = new StringWriter();
                        var savedError = Console.Error;
                        JsonObject response;
                        var before = probes;
                        Console.SetError(errors);
                        try { response = call(Line(arguments)); }
                        finally { Console.SetError(savedError); }
                        var toolProbes = probes - before;
                        var cli = InProcessCli(cliArguments);
                        Check(IsResult(response) && IsError(response) && Text(response) == InternalLine && toolProbes == 1,
                            "P5-31 " + name + " after the load, " + label + ": isError with the typed skill_list_internal_error line, the probe once | " + Short(response.ToJsonString()) + " probes " + toolProbes.ToString(CultureInfo.InvariantCulture));
                        Check(!Text(response).Contains("P5-31", StringComparison.Ordinal), "P5-31 " + name + " " + label + ": the defect message is not in the tool text");
                        Check(errors.ToString() == diagnostic + Environment.NewLine, "P5-31 " + name + " " + label + ": the IntentPlan diagnostic is on the server's stderr once | " + errors);
                        Check(cli.Exit == 1 && cli.Line == Text(response) && cli.Error == errors.ToString(),
                            "P5-31 " + name + " " + label + ": the in-process CLI Run prints the same line and diagnostic, exit 1 | " + cli.Line + " / " + cli.Error);
                    }
                    foreach (var arguments in new[] { A(("skills", " ")), A(("skills", "--x")), A(("other", "x")) })
                    {
                        var before = probes;
                        var response = call(Line(arguments));
                        Check(ErrorCode(response) == -32602 && probes == before, "P5-31: usage-equivalent " + arguments.ToJsonString() + " stays -32602 and never reaches the post-load probe | " + Short(response.ToJsonString()));
                    }
                }
                finally { Bim.Cli.SkillListCommand.LoadedProbe = null; }
            }

            // A Console.Error whose every write throws: the typed line through tools/call and SkillListTool.Execute.
            var nul = Path.Combine(scratch, "p531\0nul");
            foreach (var rejection in new Exception[] { new IOException("stderr closed P5-31"), new ObjectDisposedException("stderr P5-31"), new InvalidOperationException("stderr rejected P5-31") })
            {
                var name = rejection.GetType().Name;
                foreach (var (label, arguments, arm) in new (string, JsonObject, Action)[]
                {
                    ("post-load probe", A(("skills", generated)), () => Bim.Cli.SkillListCommand.LoadedProbe = _ => throw new InvalidOperationException("post-load P5-31")),
                    ("listing probe", A(("skills", absent)), () => Bim.Cli.SkillListCommand.ListingWorkProbe = () => throw new IOException("listing P5-31")),
                    ("NUL skills path", A(("skills", nul)), () => { }),
                })
                {
                    foreach (var direct in new[] { false, true })
                    {
                        var writer = new RejectingWriter(rejection);
                        var savedError = Console.Error;
                        string text = "";
                        bool isError = false, result = false;
                        var escaped = "";
                        arm();
                        Console.SetError(writer);
                        try
                        {
                            if (direct)
                            {
                                using var document = JsonDocument.Parse(arguments.ToJsonString());
                                (text, isError) = Bim.Mcp.SkillListTool.Execute(document.RootElement);
                                result = true;
                            }
                            else
                            {
                                var response = call(Line(arguments));
                                result = IsResult(response);
                                if (result) { text = Text(response); isError = IsError(response); }
                            }
                        }
                        catch (Exception error) { escaped = error.GetType().FullName + ": " + error.Message; }
                        finally
                        {
                            Console.SetError(savedError);
                            Bim.Cli.SkillListCommand.LoadedProbe = null;
                            Bim.Cli.SkillListCommand.ListingWorkProbe = null;
                        }
                        Check(escaped.Length == 0 && result && isError && text == InternalLine && writer.Attempts == 1,
                            "P5-31 stderr throws " + name + " (" + label + ", " + (direct ? "SkillListTool.Execute" : "tools/call") + "): isError with the typed line, nothing escapes, one write attempt | "
                            + Short(text) + " escaped " + escaped + " attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture));
                    }
                }
                foreach (var (label, arguments, expected) in new (string, JsonObject, (int Exit, string Out, string Err))[] { ("listing", A(("skills", generated)), listing), ("root_missing", A(("skills", absent)), refusal) })
                {
                    var writer = new RejectingWriter(rejection);
                    var savedError = Console.Error;
                    JsonObject response;
                    Console.SetError(writer);
                    try { response = call(Line(arguments)); }
                    finally { Console.SetError(savedError); }
                    Check(IsResult(response) && IsError(response) == (expected.Exit != 0) && Text(response) == expected.Out && writer.Attempts == 0,
                        "P5-31 stderr throws " + name + " (" + label + "): the CLI bytes, stderr never written | " + Short(response.ToJsonString()));
                }
            }
            Check(Bim.Cli.SkillListCommand.LoadedProbe is null && Bim.Cli.SkillListCommand.ListingWorkProbe is null, "P5-31: both seams are null again after the forced runs");
            var again = call(Line(A(("skills", generated))));
            Check(listing.Exit == 0 && IsResult(again) && !IsError(again) && Text(again) == listing.Out, "P5-31: with the seams reset the next call answers the CLI listing bytes | " + Short(again.ToJsonString()));
            Check(!Directory.Exists(absent), "P5-31: the missing directory is not created");
        }
    }

    // P5-33: RejectingWriter is the shared tests/cli/RejectingWriter.cs (linked by Bim.Mcp.Tests.csproj).

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    // Ordinal relative paths with the SHA-256 of each file's bytes (directories listed too).
    static string TreeDigest(string directory)
    {
        if (!Directory.Exists(directory)) return "<absent>";
        var builder = new StringBuilder();
        foreach (var entry in Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            builder.Append(Path.GetRelativePath(directory, entry).Replace('\\', '/'));
            if (File.Exists(entry)) builder.Append(' ').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    // The CLI verb in process: stdout line (without newline), stderr and exit code captured and restored in a finally.
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
            Bim.Cli.SkillListCommand.Run(args);
            exit = Environment.ExitCode;
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedError);
            Environment.ExitCode = savedExit;
        }
        var line = output.ToString();
        if (line.EndsWith('\n')) line = line[..^1];
        if (line.EndsWith('\r')) line = line[..^1];
        return (exit, line, errors.ToString());
    }
}
