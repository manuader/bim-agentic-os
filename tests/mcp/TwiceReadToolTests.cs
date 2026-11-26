using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;

// P5-30: twice-read bytes through the MCP tools skill_list and jurisdiction_resolve over one generated package fixture.
// Fixture: <pkg>/cli (a copy of the Release CLI) and <pkg>/mcp (a copy of the Release MCP server) beside VERSION,
// manifest.json and skills/, whose skills/ hold one code pack xx.p530m.pack with a jurisdiction.json sidecar (code
// xx.p530m, not a stub), and a skills.lock.json pinning it outside the package. Three phases:
//   success                 the valid sidecar; every line succeeds;
//   sidecar drift           the sidecar's note edited after pinning: skill-list and unlocked resolve still succeed, the
//                           locked resolve is skill_lock_drift on the pack;
//   sidecar json_invalid    the sidecar rewritten malformed and the pack pinned again in a second lock: every line is
//                           json_invalid at xx.p530m.pack/jurisdiction.json.
// Oracles, per phase: the CLI lines are the stdout (without the newline) of real processes of the packaged CLI for
// `skill-list --skills <pkg>/skills`, `jurisdiction-resolve --code xx.p530m` and the same with `--lock <lock>`; every
// line (success, skill_lock_drift and json_invalid) is pinned literally. Then
//   1. in process: the harness's one McpServer answers skill_list {skills}, jurisdiction_resolve {code} and
//      jurisdiction_resolve {code, lock_path} twice each, with AppContext.BaseDirectory pointed at <pkg>/cli through
//      APP_CONTEXT_BASE_DIRECTORY for the calls (restored in a finally; a negative control without it answers
//      jurisdiction_unknown): both texts byte-equal to the CLI line, isError = (CLI exit 1);
//   2. one real process of the packaged server <pkg>/mcp/Bim.Mcp.dll (package-first through SkillPackLookup.PackageRoot of
//      its mcp/ directory) answers the same six calls on one stdin, each response carrying its request id, with the same texts and isError;
//   3. the skills tree and the lock bytes are unchanged by every phase.
// Two in-process LoadLocked reads of the success inputs are unequal registries (P5-27 reference identity).
internal static class TwiceReadToolTests
{
    const string Pack = "xx.p530m.pack";
    const string Code = "xx.p530m";

    public static void Run(string root, string work, string dotnet, string cliDll, string mcpDll, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P5-30 twice-read: " + name);
        static string Short(string text) => text.Length > 600 ? text[..600] + "..." : text;
        var package = Path.Combine(work, "twice-read-mcp", "pkg");
        var packageCli = Path.Combine(package, "cli");
        var packageMcp = Path.Combine(package, "mcp");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        CopyDirectory(Path.GetDirectoryName(mcpDll)!, packageMcp);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var skills = Path.Combine(package, "skills");
        Directory.CreateDirectory(Path.Combine(skills, Pack));
        File.WriteAllText(Path.Combine(skills, Pack, "skill.json"),
            "{\"manifest_version\":1,\"id\":\"" + Pack + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fictitious test fixture\",\"priority\":10,\"rules\":[]}\n", new UTF8Encoding(false));
        var sidecar = Path.Combine(skills, Pack, "jurisdiction.json");
        static string Sidecar(string note) =>
            "{\"jurisdiction_version\":1,\"code\":\"" + Code + "\",\"pack\":\"" + Pack + "\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"Fixture Town\"],\"stub\":false,\"note\":\"" + note + "\"}\n";
        File.WriteAllText(sidecar, Sidecar("fictitious test fixture"), new UTF8Encoding(false));
        var lockPath = Path.Combine(work, "twice-read-mcp", "p530m.skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Pack })));
        var packageCliDll = Path.Combine(packageCli, "Bim.Cli.dll");
        var packageMcpDll = Path.Combine(packageMcp, "Bim.Mcp.dll");
        var packageBase = packageCli + Path.DirectorySeparatorChar;

        (int Exit, string Out) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(packageCliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>"); }
            process.WaitForExit();
            // P5-34: the packaged CLI writes nothing on stderr for any line these sections compare.
            Check(stderr.Result.Length == 0, "the packaged CLI " + string.Join(" ", arguments) + " writes nothing on stderr | " + Short(stderr.Result));
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text);
        }
        string McpProcess(string stdin)
        {
            var start = new ProcessStartInfo(dotnet)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(packageMcpDll);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(stdin); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(120000)) { process.Kill(true); throw new InvalidOperationException("packaged MCP server timed out"); }
            process.WaitForExit();
            // P5-36: the packaged server writes nothing on stderr for any exchange these sections compare.
            Check(stderr.Result.Length == 0, "the packaged MCP server writes nothing on stderr | " + Short(stderr.Result));
            return stdout.Result;
        }
        var id = 3000;
        string Line(string tool, JsonObject arguments) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        }.ToJsonString();
        static bool IsResult(JsonObject response) => response["result"] is JsonObject result && result["content"] is JsonArray && !response.ContainsKey("error");
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        // One in-process call with the base-directory override held for the call only.
        JsonObject InProcess(string line, bool overridden)
        {
            var previous = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
            try
            {
                if (overridden) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", packageBase);
                return call(line);
            }
            finally { if (overridden) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", previous); }
        }
        var originalBase = AppContext.BaseDirectory;
        Check(Bim.Cli.SkillPackLookup.PackageRoot(packageCli) == Path.GetFullPath(package) && Bim.Cli.SkillPackLookup.PackageRoot(packageMcp) == Path.GetFullPath(package),
            "the generated package is a package root from both cli/ and mcp/");

        // P5-27 reference identity behind the bytes: two LoadLocked reads of the same inputs are unequal registries.
        var first = JurisdictionRegistry.LoadLocked(skills, lockPath);
        var second = JurisdictionRegistry.LoadLocked(skills, lockPath);
        Check(first.Registry is { } a && second.Registry is { } b && !ReferenceEquals(a, b) && !a.Equals(b) && !a.Equals((object)b)
            && !EqualityComparer<JurisdictionRegistry>.Default.Equals(a, b) && a.Codes.SequenceEqual(b.Codes) && a.Codes.SequenceEqual(new[] { Code }) && first != second,
            "two in-process LoadLocked reads of the same skills directory and lock succeed with the same codes and are unequal registries and loads | "
            + (first.Refusal?.ToString() ?? "") + " / " + (second.Refusal?.ToString() ?? ""));

        // Negative control: without the override the in-process server reads the repository skills (no xx.p530m claim).
        {
            var plain = InProcess(Line("jurisdiction_resolve", new JsonObject { ["code"] = Code }), overridden: false);
            Check(IsResult(plain) && IsError(plain) && Text(plain).StartsWith("{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.JurisdictionUnknown + "\"", StringComparison.Ordinal),
                "negative control: without the base-directory override in-process jurisdiction_resolve is jurisdiction_unknown (the override is load-bearing) | " + Short(plain.ToJsonString()));
        }

        var listLine = "{\"status\":\"succeeded\",\"skills_path\":" + JsonSerializer.Serialize(Path.TrimEndingDirectorySeparator(Path.GetFullPath(skills)))
            + ",\"packs\":[{\"id\":\"" + Pack + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"stub\":false,"
            + "\"source_confidence\":{\"rules\":0,\"cited\":0,\"owner-confirmed\":0,\"to-verify\":0,\"lowest\":\"none\"}}]}";
        const string ResolveLine = "{\"status\":\"succeeded\",\"code\":\"" + Code + "\",\"pack\":\"" + Pack + "\",\"version\":\"1.2.3\",\"stub\":false}";
        // Refusal lines pinned literally (red phase run 44731149194 probed placeholders and observed these on Windows; the
        // drift hashes are bim-skill-pack-sha256-v1 of the fixture bytes as pinned and as edited; the json_invalid detail is
        // the System.Text.Json message for the truncated sidecar, the same unlocked and under the lock).
        const string JsonInvalidLine = "{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.JsonInvalid + "\",\"subject\":\"" + Pack + "/jurisdiction.json\",\"detail\":\"$: Expected start of a property name or value, but instead reached end of data. LineNumber: 0 | BytePositionInLine: 29.\"}";
        const string DriftLine = "{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.SkillLockDrift + "\",\"subject\":\"" + Pack + "\",\"detail\":\"pinned 1.2.3 sha256 99a728983c2aed823b3864d14264a0d907c00da37623f299d69d2e4d834514ef, found 1.2.3 sha256 3ac41cdd3374f25fa76390ba53321908158bedcbdda75acb6bf40fe680b5b77c; an explicit lock update is required to accept it\"}";

        void Phase(string phase, string lockFile, Func<string, bool>[] expected)
        {
            var digest = TreeDigest(skills);
            var lockBytes = File.ReadAllBytes(lockFile);
            var cases = new (string Label, string Tool, JsonObject Arguments, string[] CliArguments)[]
            {
                ("skill_list", "skill_list", new JsonObject { ["skills"] = skills }, ["skill-list", "--skills", skills]),
                ("jurisdiction_resolve", "jurisdiction_resolve", new JsonObject { ["code"] = Code }, ["jurisdiction-resolve", "--code", Code]),
                ("jurisdiction_resolve with lock_path", "jurisdiction_resolve", new JsonObject { ["code"] = Code, ["lock_path"] = lockFile }, ["jurisdiction-resolve", "--code", Code, "--lock", lockFile]),
            };
            var cli = cases.Select(c => Cli(c.CliArguments)).ToArray();
            for (var index = 0; index < cases.Length; index++)
            {
                var expectedLine = cli[index].Exit is 0 or 1 && expected[index](cli[index].Out) && (cli[index].Exit == 0) == cli[index].Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal);
                Check(expectedLine,
                    phase + ": " + cases[index].Label + ": the packaged CLI line is the expected one | exit " + cli[index].Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli[index].Out));
            }
            // 1. in process, two calls each on the one harness server.
            for (var index = 0; index < cases.Length; index++)
            {
                var responses = new[] { InProcess(Line(cases[index].Tool, cases[index].Arguments.DeepClone().AsObject()), true), InProcess(Line(cases[index].Tool, cases[index].Arguments.DeepClone().AsObject()), true) };
                for (var turn = 0; turn < responses.Length; turn++)
                    Check(IsResult(responses[turn]) && Text(responses[turn]) == cli[index].Out && IsError(responses[turn]) == (cli[index].Exit != 0),
                        phase + ": " + cases[index].Label + " in-process call #" + (turn + 1).ToString(CultureInfo.InvariantCulture) + " text byte-equal to the CLI line, isError = (exit 1) | "
                        + Short(responses[turn].ToJsonString()) + " | cli " + Short(cli[index].Out));
            }
            Check(AppContext.BaseDirectory == originalBase, phase + ": AppContext.BaseDirectory is restored after the in-process calls");
            // 2. one real packaged server, two calls each.
            var stdin = new StringBuilder();
            var requestIds = new List<int>();
            foreach (var c in cases) for (var turn = 0; turn < 2; turn++) { stdin.Append(Line(c.Tool, c.Arguments.DeepClone().AsObject())).Append('\n'); requestIds.Add(id); }
            var lines = McpProcess(stdin.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(lines.Length == cases.Length * 2, phase + ": the packaged Bim.Mcp.dll answers six lines | " + Short(string.Join(" / ", lines)));
            for (var at = 0; at < lines.Length && at < cases.Length * 2; at++)
            {
                var index = at / 2;
                var response = JsonNode.Parse(lines[at])!.AsObject();
                Check(response["id"] is JsonValue responseId && responseId.TryGetValue<int>(out var answered) && answered == requestIds[at]
                    && IsResult(response) && Text(response) == cli[index].Out && IsError(response) == (cli[index].Exit != 0),
                    phase + ": " + cases[index].Label + " packaged-server call #" + (at % 2 + 1).ToString(CultureInfo.InvariantCulture) + " answers its request id " + requestIds[at].ToString(CultureInfo.InvariantCulture) + " with text byte-equal to the CLI line, isError = (exit 1) | "
                    + Short(lines[at]) + " | cli " + Short(cli[index].Out));
            }
            Check(TreeDigest(skills) == digest && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(lockBytes), phase + ": the skills tree and the lock are unchanged");
        }

        Phase("success", lockPath, [line => line == listLine, line => line == ResolveLine, line => line == ResolveLine]);

        File.WriteAllText(sidecar, Sidecar("edited after pinning"), new UTF8Encoding(false));
        Phase("sidecar drift", lockPath, [line => line == listLine, line => line == ResolveLine, line => line == DriftLine]);

        File.WriteAllText(sidecar, "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));
        var repinned = Path.Combine(work, "twice-read-mcp", "p530m.repinned.skills.lock.json");
        File.WriteAllBytes(repinned, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Pack })));
        Phase("sidecar json_invalid", repinned, [line => line == JsonInvalidLine, line => line == JsonInvalidLine, line => line == JsonInvalidLine]);

        // P5-32: the selection arguments and allow_stub under lock_path over a second generated package.
        Selection(work, dotnet, cliDll, mcpDll, call, check);

        // P5-34: jurisdiction_ambiguous, jurisdiction_conflict (as -32602) and a drifted lock under lock_path.
        Ambiguity(root, work, dotnet, cliDll, mcpDll, call, check);
    }

    // P5-32: jurisdiction_resolve with the selection arguments (country, municipality) and allow_stub under lock_path, over a
    // generated package (copied CLI + MCP) whose skills hold a real code pack xx.p532m.pack (Fixture Country / Fixture Town)
    // and a stub code pack xx.p532t.pack (Fixture Country / Stub Village), both pinned in an outside lock. Every case runs
    // the packaged CLI once for its line, then the tool twice on the harness's in-process server (base-directory override,
    // restored) and twice on one real packaged mcp/Bim.Mcp.dll process; each text is byte-equal to the CLI line and to the
    // literal pin, isError = (CLI exit 1). Then the stub sidecar is truncated and both packs pinned again: the selection
    // and allow_stub calls are json_invalid whose detail is "$: " + the documented System.Text.Json message.
    static void Selection(string work, string dotnet, string cliDll, string mcpDll, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P5-32 selection twice-read: " + name);
        static string Short(string text) => text.Length > 700 ? text[..700] + "..." : text;
        const string RealPack = "xx.p532m.pack";
        const string RealCode = "xx.p532m";
        const string StubPack = "xx.p532t.pack";
        const string StubCode = "xx.p532t";
        const string Truncated = "{ \"jurisdiction_version\": 1, ";
        var package = Path.Combine(work, "twice-read-mcp-p532", "pkg");
        var packageCli = Path.Combine(package, "cli");
        var packageMcp = Path.Combine(package, "mcp");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        CopyDirectory(Path.GetDirectoryName(mcpDll)!, packageMcp);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var skills = Path.Combine(package, "skills");
        foreach (var (packId, code, municipality, stub) in new[] { (RealPack, RealCode, "Fixture Town", false), (StubPack, StubCode, "Stub Village", true) })
        {
            Directory.CreateDirectory(Path.Combine(skills, packId));
            File.WriteAllText(Path.Combine(skills, packId, "skill.json"),
                "{\"manifest_version\":1,\"id\":\"" + packId + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fictitious test fixture\",\"priority\":10,\"rules\":[]}\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(skills, packId, "jurisdiction.json"),
                "{\"jurisdiction_version\":1,\"code\":\"" + code + "\",\"pack\":\"" + packId + "\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"" + municipality + "\"],\"stub\":" + (stub ? "true" : "false") + ",\"note\":\"fictitious test fixture\"}\n", new UTF8Encoding(false));
        }
        var lockPath = Path.Combine(work, "twice-read-mcp-p532", "p532m.skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { RealPack, StubPack })));
        var packageCliDll = Path.Combine(packageCli, "Bim.Cli.dll");
        var packageMcpDll = Path.Combine(packageMcp, "Bim.Mcp.dll");
        var packageBase = packageCli + Path.DirectorySeparatorChar;

        (int Exit, string Out) Cli(string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(packageCliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>"); }
            process.WaitForExit();
            // P5-34: the packaged CLI writes nothing on stderr for any line these sections compare.
            Check(stderr.Result.Length == 0, "the packaged CLI " + string.Join(" ", arguments) + " writes nothing on stderr | " + Short(stderr.Result));
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text);
        }
        string McpProcess(string stdin)
        {
            var start = new ProcessStartInfo(dotnet)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(packageMcpDll);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(stdin); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(120000)) { process.Kill(true); throw new InvalidOperationException("packaged MCP server timed out"); }
            process.WaitForExit();
            // P5-36: the packaged server writes nothing on stderr for any exchange these sections compare.
            Check(stderr.Result.Length == 0, "the packaged MCP server writes nothing on stderr | " + Short(stderr.Result));
            return stdout.Result;
        }
        var id = 3500;
        string Line(JsonObject arguments) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "jurisdiction_resolve", ["arguments"] = arguments },
        }.ToJsonString();
        static bool IsResult(JsonObject response) => response["result"] is JsonObject result && result["content"] is JsonArray && !response.ContainsKey("error");
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        JsonObject InProcess(string line, bool overridden)
        {
            var previous = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
            try
            {
                if (overridden) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", packageBase);
                return call(line);
            }
            finally { if (overridden) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", previous); }
        }
        var originalBase = AppContext.BaseDirectory;
        Check(Bim.Cli.SkillPackLookup.PackageRoot(packageMcp) == Path.GetFullPath(package), "the generated package is a package root from mcp/");
        {
            var plain = InProcess(Line(new JsonObject { ["country"] = "Fixture Country", ["municipality"] = "Fixture Town", ["lock_path"] = lockPath }), overridden: false);
            Check(IsResult(plain) && IsError(plain) && Text(plain).StartsWith("{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.SkillLockMissing + "\",\"subject\":\"xx.p532", StringComparison.Ordinal),
                "negative control: without the base-directory override the in-process selection under lock_path is skill_lock_missing (the override is load-bearing) | " + Short(plain.ToJsonString()));
        }

        static string Succeeded(string code, string pack, bool stub) =>
            "{\"status\":\"succeeded\",\"code\":\"" + code + "\",\"pack\":\"" + pack + "\",\"version\":\"1.2.3\",\"stub\":" + (stub ? "true" : "false") + "}";
        // Pinned from red run 44841766427 (the MCP jurisdiction_unknown placeholder reported this line; registered codes in
        // ordinal order). The json_invalid message is the .NET 8 System.Text.Json text first observed in P5-30 run 44731149194.
        const string UnknownLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_unknown\",\"subject\":\"fixture country/nowhere else\",\"detail\":\"no registered jurisdiction matches; registered: xx.p532m, xx.p532t\"}";
        const string StubRefusedLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_stub_refused\",\"subject\":\"xx.p532t\",\"detail\":\"jurisdiction xx.p532t is a stub example pack (xx.p532t.pack), not real law; pass allowStub to use it\"}";
        // P5-34: aliased on purpose, not a missing pin. JurisdictionRegistry.Select refuses a stub after either route has
        // matched it, with the claim's code as subject and no mention of the route, so the by-metadata refusal is the same
        // bytes as the by-code one.
        const string StubRefusedByMetadataLine = StubRefusedLine;
        const string JsonMessage = "Expected start of a property name or value, but instead reached end of data. LineNumber: 0 | BytePositionInLine: 29.";

        void Phase(string phase, (string Label, JsonObject Arguments, string[] CliArguments, string Pinned)[] cases)
        {
            var digest = TreeDigest(skills);
            var cli = cases.Select(c => Cli(["jurisdiction-resolve", .. c.CliArguments])).ToArray();
            for (var index = 0; index < cases.Length; index++)
                Check(cli[index].Out == cases[index].Pinned && cli[index].Exit == (cases[index].Pinned.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) ? 0 : 1),
                    phase + ": " + cases[index].Label + ": the packaged CLI line is the pinned one | exit " + cli[index].Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli[index].Out));
            for (var index = 0; index < cases.Length; index++)
                for (var turn = 1; turn <= 2; turn++)
                {
                    var response = InProcess(Line(cases[index].Arguments.DeepClone().AsObject()), true);
                    Check(IsResult(response) && Text(response) == cli[index].Out && Text(response) == cases[index].Pinned && IsError(response) == (cli[index].Exit != 0),
                        phase + ": " + cases[index].Label + " in-process call #" + turn.ToString(CultureInfo.InvariantCulture) + " text byte-equal to the CLI line and the pin, isError = (exit 1) | " + Short(response.ToJsonString()));
                }
            Check(AppContext.BaseDirectory == originalBase, phase + ": AppContext.BaseDirectory is restored after the in-process calls");
            var stdin = new StringBuilder();
            var requestIds = new List<int>();
            foreach (var c in cases) for (var turn = 0; turn < 2; turn++) { stdin.Append(Line(c.Arguments.DeepClone().AsObject())).Append('\n'); requestIds.Add(id); }
            var lines = McpProcess(stdin.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(lines.Length == cases.Length * 2, phase + ": the packaged Bim.Mcp.dll answers " + (cases.Length * 2).ToString(CultureInfo.InvariantCulture) + " lines | " + Short(string.Join(" / ", lines)));
            for (var at = 0; at < lines.Length && at < cases.Length * 2; at++)
            {
                var index = at / 2;
                var response = JsonNode.Parse(lines[at])!.AsObject();
                Check(response["id"] is JsonValue responseId && responseId.TryGetValue<int>(out var answered) && answered == requestIds[at]
                    && IsResult(response) && Text(response) == cli[index].Out && Text(response) == cases[index].Pinned && IsError(response) == (cli[index].Exit != 0),
                    phase + ": " + cases[index].Label + " packaged-server call #" + (at % 2 + 1).ToString(CultureInfo.InvariantCulture) + " answers its request id with text byte-equal to the CLI line and the pin | " + Short(lines[at]));
            }
            Check(TreeDigest(skills) == digest, phase + ": the skills tree is unchanged");
        }
        JsonObject Args(string lockFile, params (string Name, JsonNode Value)[] values)
        {
            var arguments = new JsonObject();
            foreach (var (name, value) in values) arguments[name] = value;
            arguments["lock_path"] = lockFile;
            return arguments;
        }

        Phase("valid", new (string, JsonObject, string[], string)[]
        {
            ("selection success", Args(lockPath, ("country", "Fixture Country"), ("municipality", "Fixture Town")), ["--country", "Fixture Country", "--municipality", "Fixture Town", "--lock", lockPath], Succeeded(RealCode, RealPack, false)),
            ("selection refusal", Args(lockPath, ("country", "Fixture Country"), ("municipality", "Nowhere Else")), ["--country", "Fixture Country", "--municipality", "Nowhere Else", "--lock", lockPath], UnknownLine),
            ("allow_stub success", Args(lockPath, ("code", StubCode), ("allow_stub", true)), ["--code", StubCode, "--allow-stub", "--lock", lockPath], Succeeded(StubCode, StubPack, true)),
            ("allow_stub success by selection", Args(lockPath, ("country", "Fixture Country"), ("municipality", "Stub Village"), ("allow_stub", true)), ["--country", "Fixture Country", "--municipality", "Stub Village", "--allow-stub", "--lock", lockPath], Succeeded(StubCode, StubPack, true)),
            ("allow_stub refusal (absent)", Args(lockPath, ("code", StubCode)), ["--code", StubCode, "--lock", lockPath], StubRefusedLine),
            ("allow_stub refusal (false)", Args(lockPath, ("code", StubCode), ("allow_stub", false)), ["--code", StubCode, "--lock", lockPath], StubRefusedLine),
            ("allow_stub refusal by selection", Args(lockPath, ("country", "Fixture Country"), ("municipality", "Stub Village")), ["--country", "Fixture Country", "--municipality", "Stub Village", "--lock", lockPath], StubRefusedByMetadataLine),
        });

        File.WriteAllText(Path.Combine(skills, StubPack, "jurisdiction.json"), Truncated, new UTF8Encoding(false));
        var repinned = Path.Combine(work, "twice-read-mcp-p532", "p532m.repinned.skills.lock.json");
        File.WriteAllBytes(repinned, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { RealPack, StubPack })));
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        string observed;
        try { using var parsed = JsonDocument.Parse(Truncated); observed = "<parsed>"; }
        catch (JsonException error) { observed = error.Message; }
        Check(runtime.StartsWith(".NET 8.", StringComparison.Ordinal) && observed == JsonMessage,
            "json_invalid: System.Text.Json reports the documented message for the truncated sidecar on " + runtime + " | " + JsonSerializer.Serialize(observed));
        var jsonInvalidLine = "{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.JsonInvalid + "\",\"subject\":\"" + StubPack + "/jurisdiction.json\",\"detail\":" + JsonSerializer.Serialize("$: " + JsonMessage) + "}";
        Phase("json_invalid", new (string, JsonObject, string[], string)[]
        {
            ("selection", Args(repinned, ("country", "Fixture Country"), ("municipality", "Fixture Town")), ["--country", "Fixture Country", "--municipality", "Fixture Town", "--lock", repinned], jsonInvalidLine),
            ("allow_stub", Args(repinned, ("code", StubCode), ("allow_stub", true)), ["--code", StubCode, "--allow-stub", "--lock", repinned], jsonInvalidLine),
        });
    }

    // P5-34: jurisdiction_resolve under lock_path over a generated package (copied CLI + MCP) whose skills hold
    // xx.p534ma.pack and xx.p534mb.pack (distinct codes, both Fixture Country / Fixture Town: alias_overlap) and
    // xx.p534md.one and xx.p534md.two (both claiming code xx.p534md, Twin Town: claim_duplicate), all pinned in an outside
    // lock. Every case runs the packaged CLI once for its line (exit and empty stderr checked), then the tool twice on the
    // harness's in-process server (base-directory override, restored) and twice on one real packaged mcp/Bim.Mcp.dll
    // process. Result cases: the text is byte-equal to the CLI line and the literal pin, isError = (exit 1):
    // jurisdiction_ambiguous by metadata (overlap), by code and by metadata (duplicate), the code route success on an
    // overlapping pack. jurisdiction_conflict is not reachable: code with a mismatched country (or a matching country and a
    // mismatched municipality) is the verb's usage refusal, so the tool answers JSON-RPC -32602 with
    // JurisdictionResolveTool.UsageMessage (no result) and the CLI prints the jurisdiction_resolve_usage line, exit 1.
    // Then xx.p534ma.pack's sidecar note is edited after pinning: a metadata and a code selection are the literal
    // skill_lock_drift line on xx.p534ma.pack. P5-36: the packaged server's stderr is empty; drift refuses before
    // selection (controls: --code xx.p534mb succeeds and an unclaimed code is jurisdiction_unknown on the valid lock, both
    // are the drift line over the drifted lock); with xx.p534mb.pack drifted too the text is xx.p534ma.pack's line with the
    // literal " (+1 more lock issue(s))" suffix (packaged CLI, in-process and packaged MCP, twice each); the fixture
    // builders duplicate tests/cli/SkillListTests.cs section 16 on purpose and are pinned as the same text. P5-38: the
    // pin compares whole builder bodies within the two sections (mutant self-test: a builder grown by a line fails it);
    // an invalid code (xx.p538.bad: jurisdiction_invalid on the valid lock) is the drift line over the drifted lock (drift
    // before validation); the escaped '+' is pinned as the current encoder behaviour (text \u002B, raw server line
    // \\u002B); the provenance paragraph's words are pinned literally across its reflow. P5-40: Section() brace-matches
    // the method on comment- and literal-blanked code (reformat self-test, the P5-38 line scan as mutant); the grown-builder
    // mutant also runs on this file's builders; the depth check carries its own stand-in (independent of section 14); the
    // escaped '+' is also checked on the code-route request; a provenance addendum names the P5-38 runs. P5-42: Span
    // masks with the shared DeadlineSourceScan.CodeMask (no lexer of its own; raw-string edge self-test: a comment in a
    // raw-string hole, a longer quote run); the RawChild comment and the contract's P5-40 sentence are checked here.
    static void Ambiguity(string root, string work, string dotnet, string cliDll, string mcpDll, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P5-34 ambiguity twice-read: " + name);
        static string Short(string text) => text.Length > 700 ? text[..700] + "..." : text;
        const string PackA = "xx.p534ma.pack";
        const string CodeA = "xx.p534ma";
        const string PackB = "xx.p534mb.pack";
        const string CodeB = "xx.p534mb";
        const string DuplicateOne = "xx.p534md.one";
        const string DuplicateTwo = "xx.p534md.two";
        const string DuplicateCode = "xx.p534md";
        var package = Path.Combine(work, "twice-read-mcp-p534", "pkg");
        var packageCli = Path.Combine(package, "cli");
        var packageMcp = Path.Combine(package, "mcp");
        CopyDirectory(Path.GetDirectoryName(cliDll)!, packageCli);
        CopyDirectory(Path.GetDirectoryName(mcpDll)!, packageMcp);
        File.WriteAllText(Path.Combine(package, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{}\n");
        var skills = Path.Combine(package, "skills");
        // P5-36: these builders duplicate tests/cli/SkillListTests.cs section 16 on purpose (separate test executables with
        // no common fixture source; this copy uses its own pack ids); the check below pins both texts as present there.
        static string Manifest(string id) =>
            "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"1.2.3\",\"kind\":\"code\",\"description\":\"fictitious test fixture\",\"priority\":10,\"rules\":[]}\n";
        static string Sidecar(string code, string pack, string municipality, string note) =>
            "{\"jurisdiction_version\":1,\"code\":\"" + code + "\",\"pack\":\"" + pack + "\",\"country_names\":[\"Fixture Country\"],\"municipality_names\":[\"" + municipality + "\"],\"stub\":false,\"note\":\"" + note + "\"}\n";
        {
            // P5-38: the pin compares whole builder bodies: each builder's header line through the line that ends its
            // expression with ';' (lines trimmed, joined by "\n"), read only inside the four-pack section (here Ambiguity,
            // there section 16 AmbiguityTwiceRead, up to the method's closing brace), so a builder grown by a line differs and
            // section 14's identical Manifest cannot stand in for section 16's. ShallowPinned is the P5-36 pin (header +
            // next line anywhere in the file), kept only to prove the grown Manifest mutant below passed it.
            // P5-40: Span finds the method by brace matching: on the source with comments and string and char literals
            // blanked (line breaks kept), the one `static void <method>(` and the '}' closing the first '{' after it;
            // Section is the source lines from the declaration through that closing brace's line (reformat self-test below,
            // with the P5-38 line scan, OldSection, as its mutant). P5-42: the blanking is the shared
            // DeadlineSourceScan.CodeMask (one literal lexer; this file's own P5-40 Blank/Token copy is removed): comments
            // and every literal are spaced out at their positions, the code inside an interpolation hole is kept, and a
            // comment or a longer-quote raw string inside a raw-string hole is read as such (raw-string edge self-test).
            static (int Start, int End) Span(string source, string method)
            {
                var text = source.ReplaceLineEndings("\n");
                var code = DeadlineSourceScan.CodeMask(text);
                var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\bstatic\s+void\s+" + System.Text.RegularExpressions.Regex.Escape(method) + @"\s*\(",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant);
                if (declarations.Count != 1) return (-1, -1);
                var open = code.IndexOf('{', declarations[0].Index);
                var depth = 0;
                for (var at = open; open >= 0 && at < code.Length; at++)
                {
                    if (code[at] == '{') depth++;
                    else if (code[at] == '}' && --depth == 0)
                        return (text.Take(declarations[0].Index).Count(c => c == '\n'), text.Take(at).Count(c => c == '\n'));
                }
                return (-1, -1);
            }
            static string[] Section(string source, string method)
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n');
                var (start, end) = Span(source, method);
                return start < 0 ? Array.Empty<string>() : lines[start..(end + 1)];
            }
            // The P5-38 Section: from the declaration line to the first later line that is exactly "    }" (excluded).
            static string[] OldSection(string source, string method)
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n');
                var start = Array.FindIndex(lines, line => line.Trim().StartsWith("static void " + method + "(", StringComparison.Ordinal));
                var end = start < 0 ? -1 : Array.FindIndex(lines, start + 1, line => line.TrimEnd() == "    }");
                return start < 0 || end < 0 ? Array.Empty<string>() : lines[start..end];
            }
            static bool IsHeader(string line) =>
                line.Trim() is "static string Manifest(string id) =>" or "static string Sidecar(string code, string pack, string municipality, string note) =>";
            static string[] Builders(string source, string method)
            {
                var lines = Section(source, method);
                var found = new List<string>();
                for (var at = 0; at < lines.Length; at++)
                {
                    if (!IsHeader(lines[at])) continue;
                    var body = new List<string> { lines[at].Trim() };
                    var next = at + 1;
                    while (next < lines.Length && !body[^1].EndsWith(';')) body.Add(lines[next++].Trim());
                    found.Add(string.Join("\n", body) + (body[^1].EndsWith(';') ? "" : "\n<unterminated>"));
                }
                return found.ToArray();
            }
            // The P5-36 pin: header + next line anywhere in each file, here's two contained in there's.
            static bool ShallowPinned(string mcpSource, string cliSource)
            {
                static string[] Shallow(string source)
                {
                    var lines = source.ReplaceLineEndings("\n").Split('\n');
                    return Enumerable.Range(0, Math.Max(0, lines.Length - 1)).Where(at => IsHeader(lines[at])).Select(at => lines[at].Trim() + " " + lines[at + 1].Trim()).ToArray();
                }
                var here = Shallow(mcpSource);
                var there = Shallow(cliSource);
                return here.Length == 2 && here.All(there.Contains);
            }
            var hereSource = File.ReadAllText(Path.Combine(root, "tests", "mcp", "TwiceReadToolTests.cs"));
            var thereSource = File.ReadAllText(Path.Combine(root, "tests", "cli", "SkillListTests.cs"));
            static bool Pinned(string mcpSource, string cliSource)
            {
                var here = Builders(mcpSource, "Ambiguity");
                return here.Length == 2 && here.SequenceEqual(Builders(cliSource, "AmbiguityTwiceRead"));
            }
            Check(Pinned(hereSource, thereSource),
                "P5-36/P5-38: the four-pack fixture's Manifest and Sidecar builders are the same whole bodies as in tests/cli/SkillListTests.cs section 16 (duplicated on purpose) | "
                + Short(string.Join(" / ", Builders(hereSource, "Ambiguity"))) + " | there " + Short(string.Join(" / ", Builders(thereSource, "AmbiguityTwiceRead"))));
            // P5-40 reformat self-test, both files: Section spans the method from its declaration through its closing brace
            // and gives the same lines as the P5-38 scan (plus the closing brace) on the sources as they are; on three
            // reformatted copies of the method (dedented by four spaces, so an inner block closes at "    }"; the opening
            // brace moved onto the declaration line and the method indented four more; a comment, char literals, an
            // interpolated string and a verbatim string whose own line is "    }" inserted after the opening brace) it gives
            // exactly the reformatted method and the same builders, while the P5-38 scan (OldSection) does not.
            static string[] Dedented(string[] body) => body.Select(line => line.StartsWith("    ", StringComparison.Ordinal) ? line[4..] : line).ToArray();
            static string[] BraceOnDeclaration(string[] body) =>
                new[] { "    " + body[0].TrimEnd() + " {" }.Concat(body.Skip(2).Select(line => line.Length == 0 ? line : "    " + line)).ToArray();
            static string[] Inserted(string[] body) => body.Take(2).Concat(new[]
            {
                "        // P5-40 reformat probe: } } {",
                "        /* } */ _ = '}' + \"}\" + $\"{{\" + $\"{('}')}\" + '\\'' + @\"",
                "    }",
                "\";",
            }).Concat(body.Skip(2)).ToArray();
            foreach (var (file, source, method) in new[] { ("tests/mcp/TwiceReadToolTests.cs", hereSource, "Ambiguity"), ("tests/cli/SkillListTests.cs", thereSource, "AmbiguityTwiceRead") })
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n');
                var (start, end) = Span(source, method);
                var section = Section(source, method);
                Check(start >= 0 && end > start && lines[start].Trim().StartsWith("static void " + method + "(", StringComparison.Ordinal) && lines[end].Trim() == "}"
                    && lines[start + 1].Trim() == "{" && section.Length == end - start + 1 && section[..^1].SequenceEqual(OldSection(source, method)) && Builders(source, method).Length == 2,
                    "P5-40: Section(" + method + ") in " + file + " spans the declaration through its closing brace, the P5-38 scan's lines plus that brace | lines "
                    + start.ToString(CultureInfo.InvariantCulture) + ".." + end.ToString(CultureInfo.InvariantCulture) + " old " + OldSection(source, method).Length.ToString(CultureInfo.InvariantCulture));
                if (start < 0 || end <= start) continue;
                var body = lines[start..(end + 1)];
                foreach (var (name, reformatted) in new[] { ("dedented by four spaces", Dedented(body)), ("brace on the declaration line, indented four more", BraceOnDeclaration(body)), ("comment, literals and a verbatim \"    }\" line inserted", Inserted(body)) })
                {
                    var variant = string.Join("\n", lines[..start].Concat(reformatted).Concat(lines[(end + 1)..]));
                    var found = Section(variant, method);
                    Check(found.SequenceEqual(reformatted) && Builders(variant, method).SequenceEqual(Builders(source, method)) && !OldSection(variant, method).SequenceEqual(reformatted[..^1]),
                        "P5-40 reformat self-test: " + file + " with " + method + " " + name + ": Section finds exactly the reformatted method by brace matching and the same builders, the P5-38 line scan does not | found "
                        + found.Length.ToString(CultureInfo.InvariantCulture) + " of " + reformatted.Length.ToString(CultureInfo.InvariantCulture) + " lines, last " + (found.Length > 0 ? found[^1].Trim() : "<none>")
                        + ", old " + OldSection(variant, method).Length.ToString(CultureInfo.InvariantCulture));
                }
            }
            // P5-42 raw-string edge self-test, both files: two more copies of the method with one line inserted after its
            // opening brace: a comment inside a raw-string interpolation hole (the comment holds a three-quote run and a
            // '}'), and raw strings with a longer quote run (four quotes holding a three-quote run and braces, alone and as
            // the hole of a three-quote interpolated raw string). Section finds exactly that method and the same builders.
            foreach (var (file, source, method) in new[] { ("tests/mcp/TwiceReadToolTests.cs", hereSource, "Ambiguity"), ("tests/cli/SkillListTests.cs", thereSource, "AmbiguityTwiceRead") })
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n');
                var (start, end) = Span(source, method);
                if (start < 0 || end <= start) { Check(false, "P5-42 raw-string edge self-test: Section(" + method + ") in " + file + " is found"); continue; }
                var body = lines[start..(end + 1)];
                foreach (var (name, inserted) in new[]
                {
                    ("a comment inside a raw-string hole", "        _ = $\"\"\"{0 /* \"\"\" } */}\"\"\";"),
                    ("raw strings with a longer quote run", "        _ = \"\"\"\" } \"\"\" { \"\"\"\" + $\"\"\"{\"\"\"\" } \"\"\"\"}\"\"\";"),
                })
                {
                    var reformatted = body.Take(2).Append(inserted).Concat(body.Skip(2)).ToArray();
                    var variant = string.Join("\n", lines[..start].Concat(reformatted).Concat(lines[(end + 1)..]));
                    var found = Section(variant, method);
                    Check(found.SequenceEqual(reformatted) && Builders(variant, method).SequenceEqual(Builders(source, method)),
                        "P5-42 raw-string edge self-test: " + file + " with " + method + " and " + name + ": Section finds exactly that method and the same builders | found "
                        + found.Length.ToString(CultureInfo.InvariantCulture) + " of " + reformatted.Length.ToString(CultureInfo.InvariantCulture) + " lines, last " + (found.Length > 0 ? found[^1].Trim() : "<none>"));
                }
            }
            // P5-42: one literal lexer: Span masks the source with the shared DeadlineSourceScan.CodeMask (tests/cli/
            // DeadlineSourceScan.cs, linked here) and this file declares no lexer of its own (read on its own CodeMask, so
            // the names in this check's literals do not count).
            {
                var mask = DeadlineSourceScan.CodeMask(hereSource.ReplaceLineEndings("\n"));
                var span = DeadlineSourceScan.MemberBody(hereSource.ReplaceLineEndings("\n"), "static (int Start, int End) Span(string source, string method)", raw: true) ?? "";
                Check(span.Contains("DeadlineSourceScan.CodeMask(", StringComparison.Ordinal) && !span.Contains("Blank(", StringComparison.Ordinal)
                    && !System.Text.RegularExpressions.Regex.IsMatch(mask, @"\bstatic\s+(string\s+Blank|int\s+Token)\s*\("),
                    "P5-42: Span masks with DeadlineSourceScan.CodeMask and this file declares no Blank or Token lexer | Span " + Short(span));
            }
            // P5-42: the comment above tests/cli/SkillListTests.cs section 16's RawChild names the six escaped bytes without a
            // literal plus character.
            {
                var lines = thereSource.ReplaceLineEndings("\n").Split('\n');
                var at = Array.FindIndex(lines, line => line.TrimStart().StartsWith("(int Exit", StringComparison.Ordinal) && line.Contains(" RawChild(", StringComparison.Ordinal));
                var first = at;
                while (first > 0 && lines[first - 1].TrimStart().StartsWith("//", StringComparison.Ordinal)) first--;
                var comment = at < 0 ? "" : string.Join("\n", lines[first..at]);
                Check(comment.Length > 0 && comment.Contains("u002B", StringComparison.Ordinal) && !comment.Contains('+'),
                    "P5-42: the RawChild comment in tests/cli/SkillListTests.cs names the escaped bytes with no literal plus | " + Short(comment));
            }
            // P5-42: the contract's P5-38 paragraph carries a P5-40 sentence naming the P5-40 red and green runs.
            {
                var contract = File.ReadAllText(Path.Combine(root, "docs", "contracts", "module-contracts.md")).ReplaceLineEndings("\n").Split('\n');
                var heading = Array.FindIndex(contract, line => line == "## jurisdiction-resolve drift before validation and the escaped '+' in refusal details (P5-38)");
                var next = heading < 0 ? -1 : Array.FindIndex(contract, heading + 1, line => line.StartsWith("## ", StringComparison.Ordinal));
                var section = heading < 0 ? "" : string.Join("\n", contract[heading..(next < 0 ? contract.Length : next)]);
                Check(section.Contains("P5-40: ", StringComparison.Ordinal) && section.Contains("45619198150", StringComparison.Ordinal) && section.Contains("45621488770", StringComparison.Ordinal),
                    "P5-42: docs/contracts/module-contracts.md P5-38 section has a P5-40 sentence with runs 45619198150 and 45621488770 | " + Short(section));
            }
            // P5-38 mutant self-test: a copy of tests/cli/SkillListTests.cs whose section-16 builder grew by one line (the
            // terminating ';' moved to an added `+ "..."` line) must fail the pin, for the Sidecar and for the Manifest.
            // P5-40: Grown takes the file and the method; the builder is looked up inside that method's Span only.
            static string Grown(string source, string method, string header)
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n').ToList();
                var (start, end) = Span(source, method);
                var at = start < 0 ? -1 : lines.FindIndex(start, end - start + 1, line => line.Trim() == header);
                if (at < 0 || !lines[at + 1].TrimEnd().EndsWith(';')) return "";
                lines[at + 1] = lines[at + 1].TrimEnd()[..^1];
                lines.Insert(at + 2, "            + \"\";");
                return string.Join("\n", lines);
            }
            foreach (var header in new[] { "static string Sidecar(string code, string pack, string municipality, string note) =>", "static string Manifest(string id) =>" })
            {
                var mutant = Grown(thereSource, "AmbiguityTwiceRead", header);
                Check(mutant.Length > 0 && mutant != thereSource.ReplaceLineEndings("\n") && !Pinned(hereSource, mutant),
                    "P5-38 mutant self-test: the builder pin fails when section 16's " + header + " body grows by a line");
                // P5-40: the same mutant on the tests/mcp side: this file's Ambiguity builder grown by a line fails the pin.
                var hereMutant = Grown(hereSource, "Ambiguity", header);
                Check(hereMutant.Length > 0 && hereMutant != hereSource.ReplaceLineEndings("\n") && !Pinned(hereMutant, thereSource),
                    "P5-40 mutant self-test (tests/mcp side): the builder pin fails when Ambiguity's " + header + " body grows by a line");
            }
            // Depth (P5-40, independent of section 14): the grown mutant plus an explicit stand-in, the original header and
            // next line of section 16's builder appended outside section 16 (the role section 14's identical Manifest played
            // when red run 45485947608 failed the P5-38 self-test), passes the P5-36 header + next line pin and fails the
            // whole-body pin, for both builders; the real sources pass both pins.
            static string StandIn(string source, string method, string header)
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n');
                var (start, end) = Span(source, method);
                var at = start < 0 ? -1 : Array.FindIndex(lines, start, end - start + 1, line => line.Trim() == header);
                return at < 0 ? "" : "\ninternal static class P540StandIn\n{\n" + lines[at] + "\n" + lines[at + 1] + "\n}\n";
            }
            foreach (var header in new[] { "static string Sidecar(string code, string pack, string municipality, string note) =>", "static string Manifest(string id) =>" })
            {
                var standIn = StandIn(thereSource, "AmbiguityTwiceRead", header);
                var grown = Grown(thereSource, "AmbiguityTwiceRead", header);
                var depth = grown + standIn;
                Check(standIn.Length > 0 && grown.Length > 0 && ShallowPinned(hereSource, thereSource) && Pinned(hereSource, thereSource)
                    && ShallowPinned(hereSource, depth) && !Pinned(hereSource, depth),
                    "P5-40 depth (independent of section 14): with an explicit stand-in outside section 16, the P5-36 header + next line pin passes the grown " + header + " mutant the whole-body pin refuses");
            }
            // The drift-hash provenance paragraph (above the DriftLine pins) is the same text in both files.
            static string Provenance(string source)
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).ToArray();
                var start = Array.FindIndex(lines, line => line.StartsWith("// Provenance (P5-36;", StringComparison.Ordinal));
                var end = start < 0 ? -1 : Array.FindIndex(lines, start, line => line.EndsWith("are the same on every OS.", StringComparison.Ordinal));
                return start < 0 || end < 0 ? "" : string.Join("\n", lines[start..(end + 1)]);
            }
            var provenance = Provenance(File.ReadAllText(Path.Combine(root, "tests", "mcp", "TwiceReadToolTests.cs")));
            Check(provenance.Length > 0 && provenance.Contains("45318167428", StringComparison.Ordinal)
                && provenance == Provenance(File.ReadAllText(Path.Combine(root, "tests", "cli", "SkillListTests.cs"))),
                "P5-36: the drift-hash provenance paragraph is the same text (same runs and OSes) in both files | " + Short(provenance));
            // P5-38: the paragraph was reflowed (line breaks only): its words, with the comment markers removed and runs of
            // whitespace collapsed, are still the P5-36 text, literally.
            const string ProvenanceText = "Provenance (P5-36; this paragraph is the same in tests/cli/SkillListTests.cs and tests/mcp/TwiceReadToolTests.cs): "
                + "first pinned after the P5-34 red probe run 44911652955; both files' drift lines were re-observed byte for byte, as the prefix of their "
                + "two-drift lines, in P5-36 probe failure annotations: tests/cli on macOS in run 45316035463, tests/mcp on Linux and Windows in run "
                + "45318167428; they hash fixture bytes only, so they are the same on every OS.";
            var words = string.Join(" ", provenance.Split('\n').Select(line => line.StartsWith("//", StringComparison.Ordinal) ? line[2..] : line)
                .SelectMany(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            Check(words == ProvenanceText, "P5-38: the reflowed provenance paragraph keeps the P5-36 words exactly | " + Short(words));
            // P5-40: the addendum below the paragraph names the P5-38 runs and states why they re-printed no drift line; it
            // is the same text in both files (the P5-36 paragraph above is unchanged).
            static string Addendum(string source)
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).ToArray();
                var start = Array.FindIndex(lines, line => line.StartsWith("// Provenance addendum (P5-40;", StringComparison.Ordinal));
                var end = start < 0 ? -1 : Array.FindIndex(lines, start, line => line.EndsWith("as passing checks print no annotation.", StringComparison.Ordinal));
                return start < 0 || end < 0 ? "" : string.Join("\n", lines[start..(end + 1)]);
            }
            var addendum = Addendum(hereSource);
            Check(addendum.Length > 0 && addendum.Contains("45485947608", StringComparison.Ordinal) && addendum.Contains("45489441146", StringComparison.Ordinal)
                && addendum == Addendum(thereSource) && Provenance(hereSource) == provenance,
                "P5-40: the provenance addendum names the P5-38 red and green runs and is the same text in both files | " + Short(addendum));
        }
        foreach (var (packId, code, municipality) in new[] { (PackA, CodeA, "Fixture Town"), (PackB, CodeB, "Fixture Town"), (DuplicateOne, DuplicateCode, "Twin Town"), (DuplicateTwo, DuplicateCode, "Twin Town") })
        {
            Directory.CreateDirectory(Path.Combine(skills, packId));
            File.WriteAllText(Path.Combine(skills, packId, "skill.json"), Manifest(packId), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(skills, packId, "jurisdiction.json"), Sidecar(code, packId, municipality, "fictitious test fixture"), new UTF8Encoding(false));
        }
        var lockPath = Path.Combine(work, "twice-read-mcp-p534", "p534m.skills.lock.json");
        File.WriteAllBytes(lockPath, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { PackA, PackB, DuplicateOne, DuplicateTwo })));
        var packageCliDll = Path.Combine(packageCli, "Bim.Cli.dll");
        var packageMcpDll = Path.Combine(packageMcp, "Bim.Mcp.dll");
        var packageBase = packageCli + Path.DirectorySeparatorChar;

        (int Exit, string Out) Cli(string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(packageCliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>"); }
            process.WaitForExit();
            Check(stderr.Result.Length == 0, "the packaged CLI " + string.Join(" ", arguments) + " writes nothing on stderr | " + Short(stderr.Result));
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text);
        }
        string McpProcess(string stdin)
        {
            var start = new ProcessStartInfo(dotnet)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(packageMcpDll);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(stdin); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(120000)) { process.Kill(true); throw new InvalidOperationException("packaged MCP server timed out"); }
            process.WaitForExit();
            // P5-36: the packaged server writes nothing on stderr for any exchange these sections compare.
            Check(stderr.Result.Length == 0, "the packaged MCP server writes nothing on stderr | " + Short(stderr.Result));
            return stdout.Result;
        }
        var id = 4000;
        string Line(JsonObject arguments) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "jurisdiction_resolve", ["arguments"] = arguments },
        }.ToJsonString();
        static bool IsResult(JsonObject response) => response["result"] is JsonObject result && result["content"] is JsonArray && !response.ContainsKey("error");
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        // A usage-equivalent call: JSON-RPC -32602 with the tool's usage message and no result.
        static bool IsUsage(JsonObject response) =>
            response["result"] is null && response["error"] is JsonObject error && error["code"] is JsonValue errorCode && errorCode.TryGetValue<int>(out var number) && number == -32602
            && error["message"] is JsonValue message && message.TryGetValue<string>(out var messageText) && messageText == Bim.Mcp.JurisdictionResolveTool.UsageMessage;
        JsonObject InProcess(string line, bool overridden)
        {
            var previous = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY");
            try
            {
                if (overridden) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", packageBase);
                return call(line);
            }
            finally { if (overridden) AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", previous); }
        }
        var originalBase = AppContext.BaseDirectory;
        Check(Bim.Cli.SkillPackLookup.PackageRoot(packageMcp) == Path.GetFullPath(package), "the generated package is a package root from mcp/");
        {
            var plain = InProcess(Line(new JsonObject { ["country"] = "Fixture Country", ["municipality"] = "Fixture Town", ["lock_path"] = lockPath }), overridden: false);
            Check(IsResult(plain) && IsError(plain) && Text(plain).StartsWith("{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.SkillLockMissing + "\",\"subject\":\"xx.p534m", StringComparison.Ordinal),
                "negative control: without the base-directory override the in-process selection under lock_path is skill_lock_missing (the override is load-bearing) | " + Short(plain.ToJsonString()));
        }

        const string UsageLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_resolve_usage\",\"detail\":\"usage: jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]\"}";
        // Pinned is the CLI line; Usage marks a case the tool answers with -32602 (its CLI line is then UsageLine).
        void Phase(string phase, (string Label, JsonObject Arguments, string[] CliArguments, string Pinned, bool Usage)[] cases)
        {
            var digest = TreeDigest(skills);
            var lockBytes = File.ReadAllBytes(lockPath);
            var cli = cases.Select(c => Cli(["jurisdiction-resolve", .. c.CliArguments])).ToArray();
            for (var index = 0; index < cases.Length; index++)
                Check(cli[index].Out == cases[index].Pinned && cli[index].Exit == (cases[index].Pinned.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) ? 0 : 1)
                    && (!cases[index].Usage || cases[index].Pinned == UsageLine),
                    phase + ": " + cases[index].Label + ": the packaged CLI line is the pinned one | exit " + cli[index].Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli[index].Out));
            bool Answers(JsonObject response, int index) => cases[index].Usage
                ? IsUsage(response)
                : IsResult(response) && Text(response) == cli[index].Out && Text(response) == cases[index].Pinned && IsError(response) == (cli[index].Exit != 0);
            for (var index = 0; index < cases.Length; index++)
                for (var turn = 1; turn <= 2; turn++)
                {
                    var response = InProcess(Line(cases[index].Arguments.DeepClone().AsObject()), true);
                    Check(Answers(response, index),
                        phase + ": " + cases[index].Label + " in-process call #" + turn.ToString(CultureInfo.InvariantCulture) + (cases[index].Usage ? " is -32602 with the usage message" : " text byte-equal to the CLI line and the pin, isError = (exit 1)") + " | " + Short(response.ToJsonString()));
                }
            Check(AppContext.BaseDirectory == originalBase, phase + ": AppContext.BaseDirectory is restored after the in-process calls");
            var stdin = new StringBuilder();
            var requestIds = new List<int>();
            foreach (var c in cases) for (var turn = 0; turn < 2; turn++) { stdin.Append(Line(c.Arguments.DeepClone().AsObject())).Append('\n'); requestIds.Add(id); }
            var lines = McpProcess(stdin.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(lines.Length == cases.Length * 2, phase + ": the packaged Bim.Mcp.dll answers " + (cases.Length * 2).ToString(CultureInfo.InvariantCulture) + " lines | " + Short(string.Join(" / ", lines)));
            for (var at = 0; at < lines.Length && at < cases.Length * 2; at++)
            {
                var index = at / 2;
                var response = JsonNode.Parse(lines[at])!.AsObject();
                Check(response["id"] is JsonValue responseId && responseId.TryGetValue<int>(out var answered) && answered == requestIds[at] && Answers(response, index),
                    phase + ": " + cases[index].Label + " packaged-server call #" + (at % 2 + 1).ToString(CultureInfo.InvariantCulture) + " answers its request id " + (cases[index].Usage ? "with -32602 and the usage message" : "with text byte-equal to the CLI line and the pin") + " | " + Short(lines[at]));
            }
            Check(TreeDigest(skills) == digest && File.ReadAllBytes(lockPath).AsSpan().SequenceEqual(lockBytes), phase + ": the skills tree and the lock are unchanged");
        }
        JsonObject Args(params (string Name, JsonNode Value)[] values)
        {
            var arguments = new JsonObject();
            foreach (var (name, value) in values) arguments[name] = value;
            arguments["lock_path"] = lockPath;
            return arguments;
        }

        // Derived from JurisdictionRegistry.SelectAny (claims ordered by code, then pack, ordinal; normalised metadata subject).
        const string AmbiguousByMetadataLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_ambiguous\",\"subject\":\"fixture country/fixture town\",\"detail\":\"matches xx.p534ma (xx.p534ma.pack), xx.p534mb (xx.p534mb.pack)\"}";
        const string AmbiguousByCodeLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_ambiguous\",\"subject\":\"xx.p534md\",\"detail\":\"claimed by xx.p534md.one, xx.p534md.two\"}";
        const string AmbiguousDuplicateByMetadataLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_ambiguous\",\"subject\":\"fixture country/twin town\",\"detail\":\"matches xx.p534md (xx.p534md.one), xx.p534md (xx.p534md.two)\"}";
        const string OverlapCodeSuccessLine = "{\"status\":\"succeeded\",\"code\":\"xx.p534ma\",\"pack\":\"xx.p534ma.pack\",\"version\":\"1.2.3\",\"stub\":false}";
        // P5-36 controls for the drifted-lock phases: --code xx.p534mb succeeds and an unclaimed code is jurisdiction_unknown.
        const string OtherOverlapCodeSuccessLine = "{\"status\":\"succeeded\",\"code\":\"xx.p534mb\",\"pack\":\"xx.p534mb.pack\",\"version\":\"1.2.3\",\"stub\":false}";
        const string UnclaimedCode = "xx.p536m-unclaimed";
        // P5-38: a code that is not a jurisdiction code (two labels after the country) is jurisdiction_invalid on the valid
        // lock (JurisdictionRegistry.SelectAny), so the drift line it prints over the drifted lock comes from the load.
        const string InvalidCode = "xx.p538.bad";
        const string InvalidCodeLine = "{\"status\":\"failed\",\"code\":\"jurisdiction_invalid\",\"subject\":\"xx.p538.bad\",\"detail\":\"jurisdiction code such as ar.caba expected\"}";
        {
            var unclaimed = JurisdictionRegistry.LoadLocked(skills, lockPath).Registry?.Select(new ProjectJurisdiction(UnclaimedCode));
            Check(unclaimed?.Refusal is { } refusal && refusal.Code == JurisdictionCodes.JurisdictionUnknown && unclaimed.Claim is null,
                "P5-36 control: on the valid lock the in-process Select of the unclaimed code " + UnclaimedCode + " is jurisdiction_unknown | " + (unclaimed?.Refusal?.ToString() ?? "<no refusal>"));
        }
        Phase("valid lock", new (string, JsonObject, string[], string, bool)[]
        {
            ("ambiguous by metadata (alias overlap)", Args(("country", "Fixture Country"), ("municipality", "Fixture Town")), ["--country", "Fixture Country", "--municipality", "Fixture Town", "--lock", lockPath], AmbiguousByMetadataLine, false),
            ("ambiguous by code (claim duplicate)", Args(("code", DuplicateCode)), ["--code", DuplicateCode, "--lock", lockPath], AmbiguousByCodeLine, false),
            ("ambiguous by metadata (claim duplicate)", Args(("country", "Fixture Country"), ("municipality", "Twin Town")), ["--country", "Fixture Country", "--municipality", "Twin Town", "--lock", lockPath], AmbiguousDuplicateByMetadataLine, false),
            ("code route success on an overlapping pack", Args(("code", CodeA)), ["--code", CodeA, "--lock", lockPath], OverlapCodeSuccessLine, false),
            ("P5-36 control: code route success on the other overlapping pack", Args(("code", CodeB)), ["--code", CodeB, "--lock", lockPath], OtherOverlapCodeSuccessLine, false),
            ("P5-38 control: an invalid code", Args(("code", InvalidCode)), ["--code", InvalidCode, "--lock", lockPath], InvalidCodeLine, false),
            ("conflict: code with a mismatched country", Args(("code", CodeA), ("country", "Other Country")), ["--code", CodeA, "--country", "Other Country", "--lock", lockPath], UsageLine, true),
            ("conflict: code with a mismatched municipality", Args(("code", CodeA), ("country", "Fixture Country"), ("municipality", "Other Town")), ["--code", CodeA, "--country", "Fixture Country", "--municipality", "Other Town", "--lock", lockPath], UsageLine, true),
        });

        File.WriteAllText(Path.Combine(skills, PackA, "jurisdiction.json"), Sidecar(CodeA, PackA, "Fixture Town", "edited after pinning"), new UTF8Encoding(false));
        // The hashes are bim-skill-pack-sha256-v1 of the xx.p534ma.pack fixture bytes as pinned and as edited.
        // Provenance (P5-36; this paragraph is the same in tests/cli/SkillListTests.cs and tests/mcp/TwiceReadToolTests.cs):
        // first pinned after the P5-34 red probe run 44911652955; both files' drift lines were re-observed byte for byte, as
        // the prefix of their two-drift lines, in P5-36 probe failure annotations: tests/cli on macOS in run 45316035463,
        // tests/mcp on Linux and Windows in run 45318167428; they hash fixture bytes only, so they are the same on every OS.
        // Provenance addendum (P5-40; the same in both files): the P5-38 runs re-printed no drift line. Red run
        // 45485947608 failed only the P5-38 builder mutant self-test (tests/mcp annotations, Linux and Windows) and green
        // run 45489441146 passed every drift pin (tests/cli on Linux, Windows and macOS, tests/mcp on Linux and Windows):
        // the lines were matched byte for byte there, not re-printed, as passing checks print no annotation.
        const string DriftLine = "{\"status\":\"failed\",\"code\":\"skill_lock_drift\",\"subject\":\"xx.p534ma.pack\",\"detail\":\"pinned 1.2.3 sha256 9e32485998aa48cebc4968ac580b1d14c6c2d387badcf3984f77056ff6eda544, found 1.2.3 sha256 5175a2e01235ae2dc54392a9bd79904df5e9ea97c45d5f264e1e43cddf879f6f; an explicit lock update is required to accept it\"}";
        Phase("drifted lock", new (string, JsonObject, string[], string, bool)[]
        {
            ("selection", Args(("country", "Fixture Country"), ("municipality", "Fixture Town")), ["--country", "Fixture Country", "--municipality", "Fixture Town", "--lock", lockPath], DriftLine, false),
            // P5-36: drift refuses before selection. LoadLocked returns no registry for a drifted lock and the tool selects
            // only from a registry, so this code (jurisdiction_ambiguous on the valid lock) is the drift line; the controls
            // prove it: a code that succeeded on the valid lock (on a pack that did not drift) and a code no pack claims
            // (jurisdiction_unknown on the valid lock) are the same drift line.
            ("code selection", Args(("code", DuplicateCode)), ["--code", DuplicateCode, "--lock", lockPath], DriftLine, false),
            ("P5-36 control: a code that succeeded on the valid lock", Args(("code", CodeB)), ["--code", CodeB, "--lock", lockPath], DriftLine, false),
            ("P5-36 control: an unclaimed code", Args(("code", UnclaimedCode)), ["--code", UnclaimedCode, "--lock", lockPath], DriftLine, false),
            // P5-38: drift before validation: an invalid code (jurisdiction_invalid on the valid lock) is the drift line.
            ("P5-38: an invalid code", Args(("code", InvalidCode)), ["--code", InvalidCode, "--lock", lockPath], DriftLine, false),
        });

        // P5-36: two drifted packs. xx.p534mb.pack's sidecar note is edited after pinning too; the refusal is the first lock
        // issue in (pack id, code, detail) order (xx.p534ma.pack, the line above) with the further-issues suffix
        // " (+1 more lock issue(s))" appended to its detail (JurisdictionRegistry.LockRefused). The text carries the '+' as
        // the JSON escape \u002B (System.Text.Json's default encoder; observed by the P5-36 probe runs 45316035463 and 45318167428).
        File.WriteAllText(Path.Combine(skills, PackB, "jurisdiction.json"), Sidecar(CodeB, PackB, "Fixture Town", "edited after pinning"), new UTF8Encoding(false));
        const string TwoDriftLine = "{\"status\":\"failed\",\"code\":\"skill_lock_drift\",\"subject\":\"xx.p534ma.pack\",\"detail\":\"pinned 1.2.3 sha256 9e32485998aa48cebc4968ac580b1d14c6c2d387badcf3984f77056ff6eda544, found 1.2.3 sha256 5175a2e01235ae2dc54392a9bd79904df5e9ea97c45d5f264e1e43cddf879f6f; an explicit lock update is required to accept it (\\u002B1 more lock issue(s))\"}";
        Phase("two drifted packs", new (string, JsonObject, string[], string, bool)[]
        {
            ("selection", Args(("country", "Fixture Country"), ("municipality", "Fixture Town")), ["--country", "Fixture Country", "--municipality", "Fixture Town", "--lock", lockPath], TwoDriftLine, false),
            ("code selection", Args(("code", DuplicateCode)), ["--code", DuplicateCode, "--lock", lockPath], TwoDriftLine, false),
        });

        // P5-38: the escaped '+' is the current encoder behaviour, pinned (no encoder change without a product task). The
        // refusal detail holds a raw '+'; the verb serializes the envelope with JsonSerializer defaults
        // (JavaScriptEncoder.Default escapes '+' as \u002B), so the tool text carries the six characters \u002B and no
        // '+'; McpServer's envelope (UnsafeRelaxedJsonEscaping) writes that text's backslash as \\, so the raw packaged
        // server line carries \\u002B and no '+'. Twice in process and twice on one packaged server.
        {
            const string Suffix = " (+1 more lock issue(s))";
            var refusal = JurisdictionRegistry.LoadLocked(skills, lockPath).Refusal;
            Check(refusal is { } twoDrift && twoDrift.Code == JurisdictionCodes.SkillLockDrift && twoDrift.Detail.EndsWith(Suffix, StringComparison.Ordinal)
                && JsonSerializer.Serialize(Suffix) == "\" (\\u002B1 more lock issue(s))\"" && JsonSerializer.Serialize("+") == "\"\\u002B\""
                && TwoDriftLine.Contains("(\\u002B1 more", StringComparison.Ordinal) && !TwoDriftLine.Contains('+'),
                "P5-38: the two-drift detail holds a raw '+' and JsonSerializer's default encoder writes it as \\u002B (the pinned line has no '+') | " + (refusal?.ToString() ?? "<no refusal>"));
            var request = Args(("country", "Fixture Country"), ("municipality", "Fixture Town"));
            for (var turn = 1; turn <= 2; turn++)
            {
                var response = InProcess(Line(request.DeepClone().AsObject()), true);
                Check(IsResult(response) && Text(response) == TwoDriftLine && Text(response).Contains("(\\u002B1 more", StringComparison.Ordinal) && !Text(response).Contains('+'),
                    "P5-38: two drifted packs in-process call #" + turn.ToString(CultureInfo.InvariantCulture) + " text carries \\u002B, no '+' | " + Short(response.ToJsonString()));
            }
            var raw = McpProcess(Line(request.DeepClone().AsObject()) + "\n" + Line(request.DeepClone().AsObject()) + "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(raw.Length == 2 && raw.All(l => l.Contains("(\\\\u002B1 more", StringComparison.Ordinal) && !l.Contains('+')
                    && JsonNode.Parse(l) is JsonObject parsed && Text(parsed) == TwoDriftLine),
                "P5-38: two drifted packs on one packaged server, twice: each raw line carries \\\\u002B and no '+', its text is the pinned line | " + Short(string.Join(" / ", raw)));
            // P5-40: the same on the code route (code xx.p534md over the two drifted packs): twice in process (text +,
            // no '+') and twice on one packaged server (raw line \\u002B, no '+', text the pinned line).
            var codeRequest = Args(("code", DuplicateCode));
            for (var turn = 1; turn <= 2; turn++)
            {
                var response = InProcess(Line(codeRequest.DeepClone().AsObject()), true);
                Check(IsResult(response) && IsError(response) && Text(response) == TwoDriftLine && Text(response).Contains("(\\u002B1 more", StringComparison.Ordinal) && !Text(response).Contains('+'),
                    "P5-40: two drifted packs, code route, in-process call #" + turn.ToString(CultureInfo.InvariantCulture) + " text carries \\u002B, no '+' | " + Short(response.ToJsonString()));
            }
            var codeRaw = McpProcess(Line(codeRequest.DeepClone().AsObject()) + "\n" + Line(codeRequest.DeepClone().AsObject()) + "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(codeRaw.Length == 2 && codeRaw.All(l => l.Contains("(\\\\u002B1 more", StringComparison.Ordinal) && !l.Contains('+')
                    && JsonNode.Parse(l) is JsonObject parsed && Text(parsed) == TwoDriftLine && IsError(parsed)),
                "P5-40: two drifted packs, code route, on one packaged server, twice: each raw line carries \\\\u002B and no '+', its text is the pinned line | " + Short(string.Join(" / ", codeRaw)));
        }
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    static string TreeDigest(string directory)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                     .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(Path.Combine(directory, file));
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\n" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n"));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
