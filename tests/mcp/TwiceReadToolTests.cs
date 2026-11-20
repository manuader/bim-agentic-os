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
// `skill-list --skills <pkg>/skills`, `jurisdiction-resolve --code xx.p530m` and the same with `--lock <lock>`; the success
// lines are pinned literally. Then
//   1. in process: the harness's one McpServer answers skill_list {skills}, jurisdiction_resolve {code} and
//      jurisdiction_resolve {code, lock_path} twice each, with AppContext.BaseDirectory pointed at <pkg>/cli through
//      APP_CONTEXT_BASE_DIRECTORY for the calls (restored in a finally; a negative control without it answers
//      jurisdiction_unknown): both texts byte-equal to the CLI line, isError = (CLI exit 1);
//   2. one real process of the packaged server <pkg>/mcp/Bim.Mcp.dll (package-first through SkillPackLookup.PackageRoot of
//      its mcp/ directory) answers the same six calls on one stdin with the same texts and isError;
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
            _ = stderr.Result;
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
            _ = stderr.Result;
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
        string JsonInvalidPrefix() => "{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.JsonInvalid + "\",\"subject\":\"" + Pack + "/jurisdiction.json\",\"detail\":\"";
        string DriftPrefix() => "{\"status\":\"failed\",\"code\":\"" + JurisdictionCodes.SkillLockDrift + "\",\"subject\":\"" + Pack + "\",\"detail\":\"";

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
                Check(cli[index].Exit is 0 or 1 && expected[index](cli[index].Out) && (cli[index].Exit == 0) == cli[index].Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal),
                    phase + ": " + cases[index].Label + ": the packaged CLI line is the expected one | exit " + cli[index].Exit.ToString(CultureInfo.InvariantCulture) + " " + Short(cli[index].Out));
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
            foreach (var c in cases) for (var turn = 0; turn < 2; turn++) stdin.Append(Line(c.Tool, c.Arguments.DeepClone().AsObject())).Append('\n');
            var lines = McpProcess(stdin.ToString()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            Check(lines.Length == cases.Length * 2, phase + ": the packaged Bim.Mcp.dll answers six lines | " + Short(string.Join(" / ", lines)));
            for (var at = 0; at < lines.Length && at < cases.Length * 2; at++)
            {
                var index = at / 2;
                var response = JsonNode.Parse(lines[at])!.AsObject();
                Check(IsResult(response) && Text(response) == cli[index].Out && IsError(response) == (cli[index].Exit != 0),
                    phase + ": " + cases[index].Label + " packaged-server call #" + (at % 2 + 1).ToString(CultureInfo.InvariantCulture) + " text byte-equal to the CLI line, isError = (exit 1) | "
                    + Short(lines[at]) + " | cli " + Short(cli[index].Out));
            }
            Check(TreeDigest(skills) == digest && File.ReadAllBytes(lockFile).AsSpan().SequenceEqual(lockBytes), phase + ": the skills tree and the lock are unchanged");
        }

        Phase("success", lockPath, [line => line == listLine, line => line == ResolveLine, line => line == ResolveLine]);

        File.WriteAllText(sidecar, Sidecar("edited after pinning"), new UTF8Encoding(false));
        Phase("sidecar drift", lockPath, [line => line == listLine, line => line == ResolveLine, line => line.StartsWith(DriftPrefix(), StringComparison.Ordinal)]);

        File.WriteAllText(sidecar, "{ \"jurisdiction_version\": 1, ", new UTF8Encoding(false));
        var repinned = Path.Combine(work, "twice-read-mcp", "p530m.repinned.skills.lock.json");
        File.WriteAllBytes(repinned, SkillLockFile.Serialize(SkillLocking.Pin(skills, new[] { Pack })));
        Phase("sidecar json_invalid", repinned, [line => line.StartsWith(JsonInvalidPrefix(), StringComparison.Ordinal), line => line.StartsWith(JsonInvalidPrefix(), StringComparison.Ordinal),
            line => line.StartsWith(JsonInvalidPrefix(), StringComparison.Ordinal)]);
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
