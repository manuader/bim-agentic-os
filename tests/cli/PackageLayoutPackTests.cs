using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

// P7-17: skill pack lookup against a package really built by scripts/package.ps1 (not a hand-made layout). Oracles,
// all against real CLI processes:
//   1. scripts/package.ps1 -OutputDirectory <outside>/<case>/pkg (run with pwsh, the dotnet the harness uses) exits 0,
//      prints PACKAGED, and leaves cli/Bim.Cli.dll, VERSION, manifest.json and skills/ with no global.json above it.
//   2. From that package's own cli/ (outside any repository), plan-layouts --pack, generate-prototypes (its CABA pack)
//      and generate-typology with skills write the repository runs' bytes and stdout (same inputs, the repository's
//      Release CLI), and the --pack run differs from the run without it (the pack is applied).
//   3. Marker removal: with VERSION or manifest.json removed the folder is no package root. Outside any repository
//      that is *_repository_not_found; once a global.json and a copy of the repository skills/ sit above the package
//      (whose own skills/ are then made malformed), the run falls back to that repository and writes the repository
//      runs' bytes and stdout again, while the package with both markers reads its malformed skills (*_pack_invalid),
//      so the fallback is observable.
//   4. Skipped (a counted SKIP) only where pwsh is absent; on GitHub Actions on Linux a missing pwsh is a failure. P10-18:
//      packaging runs on macOS too (its first-party Bim.*.dll attributed through deps.json), so no macOS skip remains.
//   5. P5-18: the packaged MCP server (mcp/Bim.Mcp.dll over stdio, the same package) finds the packaged skills/ too:
//      generate_typology returns the packaged CLI output file bytes ("skills":[] gives the pin a8c62da5..., and the
//      request with skills), every refusal is the packaged CLI stdout line byte for byte (malformed packaged skill.json
//      with a repository above -> generate_typology_pack_invalid json_invalid; a removed marker -> the repository or
//      generate_typology_repository_not_found, exactly like the CLI). P5-19: the marker-removed fallback to the
//      repository above also has the composed pin a8c62da5... (packaged CLI file and packaged MCP text).
//   6. P5-19: the process helpers (Exec, the MCP stdio call) tolerate a child that exits between a timed-out WaitForExit
//      and Kill: an InvalidOperationException/Win32Exception from Kill on an exited child is no unhandled exception (the
//      run is still reported timed out), injected through a real short-lived child (dotnet --version); a Kill failure on
//      a child that is still running is not swallowed.
//   7. P5-20: skills-lock over the packaged skills/: the packaged CLI `skills-lock pin <pkg>/skills lock.json <CABA>` (lock
//      in a temp folder outside any repository) succeeds, its content_sha256/version and lock bytes equal SkillLocking.Pin
//      over the same directory in process; packaged CLI verify succeeds and the packaged MCP skills_lock_verify text (and
//      the in-process SkillLockTool.Verify text) equals its stdout; a tampered packaged skill.json (one byte appended ->
//      skill_lock_drift; malformed -> skills_invalid) makes both refuse with the same envelope; restored, verify passes.
internal static class PackageLayoutPackTests
{
    const string Caba = "ar.caba.codigo-edificacion";
    const string TypologyPack = "bim.typology.vivienda-unifamiliar";
    const string CompactStyle = "bim.style.compact-modern";
    // P5-14/P5-15 composed CABA + typology pin (tests/cli/GenerateTypologyTests.cs PinnedComposedSha256).
    const string PinnedComposedSha256 = "a8c62da59e0eee2d25af4d27bc37fcf2f67ae366e7e48446f1edfe3c87f7c0dd";

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "package-layout-pack: " + name, detail);
        void Skip(string name, string why) => skip("package-layout-pack: " + name, why);
        var onCi = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

        var pwsh = FindPwsh();
        if (pwsh is null)
        {
            if (onCi && OperatingSystem.IsLinux()) Check(false, "pwsh is available on Linux CI (BIM_PWSH or PATH)");
            else Skip("scripts/package.ps1 package checks", "pwsh not found (BIM_PWSH unset and no pwsh on PATH)");
            return;
        }
        // P5-19 control: a Kill failure while the child is still running is rethrown, not swallowed.
        Process? alive = null;
        string? aliveEscaped = null;
        try
        {
            Exec(pwsh, ["-NoProfile", "-Command", "Start-Sleep -Seconds 120"], 180000, wait: (_, _) => false,
                kill: process =>
                {
                    // A second handle so the harness can stop the child afterwards; a child that already exited (or
                    // whose id is gone) leaves none, and the Check below then fails instead of the section crashing.
                    try { alive = Process.GetProcessById(process.Id); }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { alive = null; }
                    throw new InvalidOperationException("injected: kill failed on a running child");
                });
        }
        catch (InvalidOperationException error) { aliveEscaped = error.Message; }
        catch (Exception error) { aliveEscaped = "unexpected " + error.GetType().Name + ": " + error.Message; }
        finally { if (alive is not null) { try { alive.Kill(true); alive.WaitForExit(30000); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { } alive.Dispose(); } }
        Check(aliveEscaped == "injected: kill failed on a running child",
            "P5-19 control: Exec rethrows a Kill failure while the child is still running (only an exited child's Kill failure is tolerated)", aliveEscaped ?? "<nothing thrown>");
        static bool UnderRepository(string directory)
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return true;
            return false;
        }
        // ---- 6. P5-19: a child that exits between a timed-out WaitForExit and Kill (a real short-lived child) ----
        (int Exit, string Out, string Err)? Raced(Action<Process>? kill, out string? escaped)
        {
            escaped = null;
            // The injected wait lets the child finish (bounded), then reports a timeout: the race of a child that exits
            // between WaitForExit(timeout) and Kill. A child that does not finish is killed and the Check fails.
            try
            {
                return Exec(dotnet, ["--version"], 180000, kill: kill, wait: (process, _) =>
                {
                    if (process.WaitForExit(120000)) return false;
                    try { process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                    throw new TimeoutException("the short-lived child (dotnet --version) did not exit within 120 s");
                });
            }
            catch (Exception error) { escaped = error.GetType().Name + ": " + error.Message; return null; }
        }
        static bool TimedOut((int Exit, string Out, string Err)? result) => result.HasValue && result.Value.Exit == -1 && result.Value.Out == "<timed out>";
        static string Shown((int Exit, string Out, string Err)? result, string? escaped) => escaped ?? (result.HasValue ? D(result.Value) : "<null>");
        var injected = Raced(_ => throw new InvalidOperationException("injected: the process exited between WaitForExit and Kill"), out var injectedEscaped);
        Check(injectedEscaped is null && TimedOut(injected),
            "P5-19: Exec tolerates an InvalidOperationException from Kill on a child that exited after WaitForExit timed out (reported timed out, nothing unhandled)",
            Shown(injected, injectedEscaped));
        var injectedWin32 = Raced(_ => throw new Win32Exception(5, "injected: access denied on an exited process"), out var injectedWin32Escaped);
        Check(injectedWin32Escaped is null && TimedOut(injectedWin32),
            "P5-19: Exec tolerates a Win32Exception from Kill on a child that already exited (reported timed out)",
            Shown(injectedWin32, injectedWin32Escaped));
        // Smoke check only, not evidence for the fix: .NET's own Kill(true) on an already-exited child is a no-op on the
        // runners (it raised nothing before P5-19 too); the injected exceptions above are what exercise the tolerance.
        var realKill = Raced(null, out var realKillEscaped);
        Check(realKillEscaped is null && TimedOut(realKill),
            "P5-19: Exec's own Kill(true) on a child that exited after WaitForExit timed out raises nothing unhandled (reported timed out)",
            Shown(realKill, realKillEscaped));

        var outsideBase = new[] { Environment.GetEnvironmentVariable("RUNNER_TEMP"), Path.GetTempPath(), OperatingSystem.IsWindows() ? null : "/tmp" }
            .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !UnderRepository(candidate));
        if (outsideBase is null)
        {
            if (onCi) Check(false, "a writable directory outside any repository exists on CI");
            else Skip("scripts/package.ps1 package checks", "no writable directory outside a repository");
            return;
        }

        var scratch = Path.Combine(work, "package-layout-pack");
        Directory.CreateDirectory(scratch);
        var counter = 0;
        string Input(string name, string content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        string Output() => Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-out.json");
        byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        static string? Str(string text, string name)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            }
            catch (JsonException) { return null; }
        }
        static string D((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + (r.Err.Length > 600 ? r.Err[..600] + "..." : r.Err) : "");
        (int Exit, string Out, string Err) CliAt(string dll, params string[] arguments) => Exec(dotnet, [dll, .. arguments], 180000);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        // P5-18: one generate_typology tools/call line to a real MCP server process over stdio; the first stdout line parsed.
        (bool Ok, bool IsError, string Text, string Raw) McpTypology(string mcpDll, string requestPath) =>
            McpTool(mcpDll, "generate_typology", new JsonObject { ["request_path"] = requestPath });
        // P5-20: any one tools/call line (name, arguments) to a real MCP server process over stdio.
        (bool Ok, bool IsError, string Text, string Raw) McpTool(string mcpDll, string toolName, JsonObject arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(mcpDll);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var line = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 18, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments } }.ToJsonString();
            try { process.StandardInput.Write(line + "\n"); process.StandardInput.Close(); } catch (IOException) { }
            if (!WaitOrKill(process, 180000)) return (false, true, "", "<timed out>");
            var raw = stdout.Result;
            var err = stderr.Result.ReplaceLineEndings(" ").Trim();
            var described = "exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + " " + (raw.Length > 600 ? raw[..600] + "..." : raw) + (err.Length > 0 ? " stderr " + (err.Length > 600 ? err[..600] + "..." : err) : "");
            try
            {
                var response = JsonNode.Parse(raw.Split('\n')[0])!.AsObject();
                var result = response["result"]!.AsObject();
                return (true, result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>(), described);
            }
            catch (Exception error) when (error is JsonException or NullReferenceException or InvalidOperationException or ArgumentException) { return (false, true, "", described); }
        }

        var caseRoot = Path.Combine(outsideBase, "bim-package-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            // ---- 1. build the real package ----
            Directory.CreateDirectory(caseRoot);
            var package = Path.Combine(caseRoot, "pkg");
            var packager = Path.Combine(root, "scripts", "package.ps1");
            static string[] BuildTrees() { try { return Directory.GetDirectories(Path.GetTempPath(), "bim-package-src-*"); } catch (IOException) { return []; } catch (UnauthorizedAccessException) { return []; } }
            var treesBefore = BuildTrees();
            var stopwatch = Stopwatch.StartNew();
            var built = Exec(pwsh, ["-NoProfile", "-File", packager, "-OutputDirectory", package, "-Dotnet", dotnet], 900000);
            stopwatch.Stop();
            // A killed package.ps1 never reaches its finally: best-effort removal of the build tree this run created.
            if (built.Exit == -1 && built.Out == "<timed out>")
                foreach (var tree in BuildTrees().Except(treesBefore, StringComparer.Ordinal))
                    try { Directory.Delete(tree, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.WriteLine("package-layout-pack: scripts/package.ps1 took " + stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
            var buildTail = built.Out.Length > 1500 ? built.Out[^1500..] : built.Out;
            var packageDll = Path.Combine(package, "cli", Path.GetFileName(cliDll));
            Check(built.Exit == 0 && built.Out.Contains("PACKAGED ", StringComparison.Ordinal) && File.Exists(packageDll),
                "scripts/package.ps1 builds a package (exit 0, PACKAGED, cli/" + Path.GetFileName(cliDll) + ")",
                "exit " + built.Exit.ToString(CultureInfo.InvariantCulture) + " stdout tail " + buildTail.ReplaceLineEndings(" | ") + (built.Err.Length > 0 ? " stderr " + (built.Err.Length > 900 ? built.Err[^900..] : built.Err) : ""));
            if (built.Exit != 0 || !File.Exists(packageDll)) return;
            var version = Path.Combine(package, "VERSION");
            var manifest = Path.Combine(package, "manifest.json");
            var packageSkills = Path.Combine(package, "skills");
            Check(File.Exists(version) && File.Exists(manifest) && File.Exists(Path.Combine(packageSkills, Caba, "skill.json"))
                && File.Exists(Path.Combine(packageSkills, TypologyPack, "skill.json")) && File.Exists(Path.Combine(packageSkills, CompactStyle, "skill.json")) && !UnderRepository(package),
                "the package has VERSION, manifest.json and skills/ (CABA, typology, compact-modern) and no global.json above it", package);
            // P10-18: the shipped Bim.* assemblies are first-party on every OS (macOS included): project entries of each
            // folder's deps.json, and NOASSERTION sbom.json lines without a "package" field.
            foreach (var (depsFolder, depsEntry) in new[] { ("cli", "Bim.Cli"), ("mcp", "Bim.Mcp") })
            {
                var depsPath = Path.Combine(package, depsFolder, depsEntry + ".deps.json");
                var notProject = new List<string>();
                if (File.Exists(depsPath))
                {
                    using var depsDocument = JsonDocument.Parse(File.ReadAllText(depsPath));
                    if (depsDocument.RootElement.ValueKind != JsonValueKind.Object || !depsDocument.RootElement.TryGetProperty("libraries", out var depsLibraries) || depsLibraries.ValueKind != JsonValueKind.Object)
                        notProject.Add("<no libraries object>");
                    else foreach (var depsLibrary in depsLibraries.EnumerateObject())
                        if (depsLibrary.Name.StartsWith("Bim.", StringComparison.Ordinal) && (depsLibrary.Value.ValueKind != JsonValueKind.Object || !depsLibrary.Value.TryGetProperty("type", out var depsType) || depsType.ValueKind != JsonValueKind.String || depsType.GetString() != "project"))
                            notProject.Add(depsLibrary.Name);
                }
                Check(File.Exists(depsPath) && notProject.Count == 0, "P10-18: " + depsFolder + "/" + depsEntry + ".deps.json lists every Bim.* library as a project", string.Join(", ", notProject));
            }
            var sbomPath = Path.Combine(package, "sbom.json");
            var bimLines = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            using (var sbomDocument = JsonDocument.Parse(File.Exists(sbomPath) ? File.ReadAllText(sbomPath) : "{\"assemblies\":[]}"))
                if (sbomDocument.RootElement.ValueKind == JsonValueKind.Object && sbomDocument.RootElement.TryGetProperty("assemblies", out var sbomAssemblies) && sbomAssemblies.ValueKind == JsonValueKind.Array)
                    foreach (var sbomAssembly in sbomAssemblies.EnumerateArray())
                        if (sbomAssembly.ValueKind == JsonValueKind.Object && sbomAssembly.TryGetProperty("path", out var sbomPathValue) && sbomPathValue.ValueKind == JsonValueKind.String && sbomPathValue.GetString() is { } shipped
                            && System.Text.RegularExpressions.Regex.IsMatch(shipped, @"^(cli|mcp)/Bim\.[A-Za-z.]+\.dll\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                            bimLines[shipped] = sbomAssembly.Clone();
            var requiredFirstParty = new[] { "cli/Bim.Cad.dll", "cli/Bim.Cli.dll", "cli/Bim.Core.dll", "cli/Bim.Evaluation.dll", "cli/Bim.Generative.dll", "cli/Bim.Gltf.dll", "cli/Bim.Ifc.Compiler.dll", "mcp/Bim.Core.dll", "mcp/Bim.Generative.dll", "mcp/Bim.Mcp.dll" };
            var notFirstParty = requiredFirstParty.Where(required => !bimLines.ContainsKey(required))
                .Concat(bimLines.Where(pair => !pair.Value.TryGetProperty("license", out var license) || license.ValueKind != JsonValueKind.String || license.GetString() != "NOASSERTION" || pair.Value.TryGetProperty("package", out _)).Select(pair => pair.Key)).ToArray();
            Check(notFirstParty.Length == 0, "P10-18: sbom.json lists every cli/ and mcp/ Bim.* assembly (Cad, Cli, Core, Evaluation, Generative, Gltf, Ifc.Compiler; Core, Generative, Mcp) first-party: NOASSERTION without a package", string.Join(", ", notFirstParty));
            McpWithoutGltf(package, caseRoot, Check);

            // ---- 2. the same inputs through the repository CLI and the packaged CLI ----
            const string small = "{\"brief_version\":1,\"name\":\"Small studio\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
                + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}],"
                + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"bath\",\"kind\":\"required\"}]}";
            var brief = Input("small.json", small);
            var prototypes = Input("two-bed.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}");
            var typology = Input("typology-skills.json", "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,"
                + "\"project_id\":\"00000000-0000-4000-8000-0000000007a7\",\"skills\":[\"" + Caba + "\",\"" + CompactStyle + "\"]}");
            var cases = new (string Label, string Verb, string[] Tail, string InvalidCode, string MissingCode)[]
            {
                ("plan-layouts --pack", "plan-layouts", new[] { "--pack", Caba, "--alternatives", "2", "--seed", "3" }, "plan_layouts_pack_invalid", "plan_layouts_pack_repository_not_found"),
                ("generate-prototypes (CABA pack)", "generate-prototypes", Array.Empty<string>(), "generate_prototypes_pack_invalid", "generate_prototypes_repository_not_found"),
                ("generate-typology with skills", "generate-typology", Array.Empty<string>(), "generate_typology_pack_invalid", "generate_typology_repository_not_found"),
            };
            string InputOf(string verb) => verb switch { "plan-layouts" => brief, "generate-prototypes" => prototypes, _ => typology };
            (int Exit, string Out, string Err, byte[] Bytes) RunVerb(string dll, string verb, string[] tail)
            {
                var output = Output();
                var result = CliAt(dll, [verb, InputOf(verb), output, .. tail]);
                return (result.Exit, result.Out, result.Err, Read(output));
            }
            var repository = new Dictionary<string, (int Exit, string Out, string Err, byte[] Bytes)>();
            foreach (var (label, verb, tail2, _, _) in cases)
            {
                var repo = RunVerb(cliDll, verb, tail2);
                repository[label] = repo;
                Check(repo.Exit == 0 && repo.Bytes.Length > 0 && Str(repo.Out, "status") == "succeeded", label + ": the repository run succeeds and writes a file", D((repo.Exit, repo.Out, repo.Err)));
                var packaged = RunVerb(packageDll, verb, tail2);
                Check(packaged.Exit == 0 && packaged.Bytes.SequenceEqual(repo.Bytes) && packaged.Out == repo.Out,
                    label + ": from the package scripts/package.ps1 built (outside any repository) the bytes and stdout equal the repository run",
                    D((packaged.Exit, packaged.Out, packaged.Err)) + " written " + packaged.Bytes.Length.ToString(CultureInfo.InvariantCulture) + " repository " + repo.Bytes.Length.ToString(CultureInfo.InvariantCulture));
            }
            var noPack = RunVerb(packageDll, "plan-layouts", ["--alternatives", "2", "--seed", "3"]);
            Check(noPack.Exit == 0 && !noPack.Bytes.SequenceEqual(repository["plan-layouts --pack"].Bytes),
                "plan-layouts from the package without --pack differs from the --pack run (the packaged pack is applied)", D((noPack.Exit, noPack.Out, noPack.Err)));

            // ---- 5. P5-18: the packaged MCP server over stdio, against the packaged CLI ----
            var packageMcp = Path.Combine(package, "mcp", "Bim.Mcp.dll");
            Check(File.Exists(packageMcp), "P5-18: the package has mcp/Bim.Mcp.dll", packageMcp);
            var composedRequest = Input("typology-composed.json", "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,"
                + "\"project_id\":\"00000000-0000-4000-8000-0000000007a7\",\"skills\":[]}");
            var mcpCases = new (string Label, string Request)[] { ("\"skills\":[] (composed pin)", composedRequest), ("generate-typology with skills", typology) };
            foreach (var (label, requestPath) in mcpCases)
            {
                var output = Output();
                var cliRun = CliAt(packageDll, "generate-typology", requestPath, output);
                var cliBytes = Read(output);
                var mcp = McpTypology(packageMcp, requestPath);
                Check(cliRun.Exit == 0 && cliBytes.Length > 0, "P5-18 " + label + ": the packaged CLI writes the document", D(cliRun));
                Check(mcp.Ok && !mcp.IsError && Utf8(mcp.Text).SequenceEqual(cliBytes),
                    "P5-18 " + label + ": the packaged mcp/Bim.Mcp.dll generate_typology over stdio (outside any repository) returns the packaged CLI output bytes",
                    "mcp " + mcp.Raw + " | cli " + D(cliRun) + " sha cli " + Hex(cliBytes) + " mcp " + Hex(Utf8(mcp.Text)));
                if (requestPath == composedRequest)
                    Check(Hex(cliBytes) == PinnedComposedSha256 && Hex(Utf8(mcp.Text)) == PinnedComposedSha256,
                        "P5-18: the packaged CLI file and the packaged MCP text have the composed pin a8c62da5...", "cli " + Hex(cliBytes) + " mcp " + Hex(Utf8(mcp.Text)));
            }
            // The refusal envelope of the packaged CLI for request (its stdout line) equals the packaged MCP text, isError.
            void McpEqualsCli(string label, string requestPath, Func<(int Exit, string Out, string Err), bool> expected, string? pinLabel = null)
            {
                var output = Output();
                var cliRun = CliAt(packageDll, "generate-typology", requestPath, output);
                var cliBytes = Read(output);
                var mcp = McpTypology(packageMcp, requestPath);
                var same = cliRun.Exit == 0 ? mcp.Ok && !mcp.IsError && Utf8(mcp.Text).SequenceEqual(cliBytes) : mcp.Ok && mcp.IsError && mcp.Text == cliRun.Out;
                Check(expected(cliRun) && same, "P5-18 " + label, "mcp " + mcp.Raw + " | cli " + D(cliRun));
                if (pinLabel is not null)
                    Check(Hex(cliBytes) == PinnedComposedSha256 && mcp.Ok && Hex(Utf8(mcp.Text)) == PinnedComposedSha256,
                        "P5-19 " + pinLabel + ": the packaged CLI file and the packaged MCP text have the composed pin a8c62da5...", "cli " + Hex(cliBytes) + " mcp " + Hex(Utf8(mcp.Text)) + " | " + mcp.Raw);
            }

            // ---- 7. P5-20: skills-lock over the packaged skills/ (packaged CLI pin/verify, packaged MCP skills_lock_verify) ----
            var lockDirectory = Path.Combine(caseRoot, "lock");
            Directory.CreateDirectory(lockDirectory);
            var lockPath = Path.Combine(lockDirectory, "lock.json");
            var pin = CliAt(packageDll, "skills-lock", "pin", packageSkills, lockPath, Caba);
            var inProcess = Bim.Core.SkillLocking.Pin(packageSkills, [Caba]);
            var expectedSha = inProcess.Packs.Count == 1 ? inProcess.Packs[0].ContentSha256 : "<in-process pin has " + inProcess.Packs.Count.ToString(CultureInfo.InvariantCulture) + " packs>";
            string? pinnedSha = null, pinnedId = null, pinnedVersion = null, pinSummarySha = null;
            try
            {
                using var pinDocument = JsonDocument.Parse(pin.Out);
                var pinRoot = pinDocument.RootElement;
                if (pinRoot.ValueKind == JsonValueKind.Object && pinRoot.TryGetProperty("packs", out var pinPacks) && pinPacks.ValueKind == JsonValueKind.Array && pinPacks.GetArrayLength() == 1)
                {
                    pinnedId = Str(pinPacks[0].GetRawText(), "id");
                    pinnedVersion = Str(pinPacks[0].GetRawText(), "version");
                    pinnedSha = Str(pinPacks[0].GetRawText(), "content_sha256");
                }
                pinSummarySha = Str(pin.Out, "sha256");
            }
            catch (JsonException) { }
            var lockBytes = Read(lockPath);
            Check(pin.Exit == 0 && Str(pin.Out, "status") == "succeeded" && Str(pin.Out, "mode") == "pin" && Str(pin.Out, "lock") == lockPath && pinnedId == Caba && lockBytes.Length > 0 && pinSummarySha == Hex(lockBytes),
                "P5-20: the packaged CLI skills-lock pin <pkg>/skills lock.json " + Caba + " (outside any repository) succeeds and writes the lock it reports (lock path, sha256 of the lock bytes)", D(pin) + " lock sha " + Hex(lockBytes));
            Check(pinnedSha is not null && pinnedSha == expectedSha && inProcess.Packs.Count == 1 && pinnedVersion == inProcess.Packs[0].Version.ToString()
                    && lockBytes.SequenceEqual(Bim.Core.SkillLockFile.Serialize(inProcess)),
                "P5-20: the packaged pin's content_sha256 and version equal SkillLocking.Pin over the same packaged skills/ in process, and the lock bytes equal its serialization",
                "packaged " + (pinnedSha ?? "<none>") + " " + (pinnedVersion ?? "<none>") + " in process " + expectedSha + " | " + D(pin));
            // Packaging preserves the pack's bytes: the same pack pinned over the repository skills/ has the same hash.
            var repositoryPin = Bim.Core.SkillLocking.Pin(Path.Combine(root, "skills"), [Caba]);
            var repositorySha = repositoryPin.Packs.Count == 1 ? repositoryPin.Packs[0].ContentSha256 : "<repository pin has " + repositoryPin.Packs.Count.ToString(CultureInfo.InvariantCulture) + " packs>";
            Check(pinnedSha is not null && pinnedSha == repositorySha,
                "P5-20: the packaged pin's content_sha256 equals SkillLocking.Pin over the repository skills/ for " + Caba + " (packaging preserved the pack bytes)",
                "packaged " + (pinnedSha ?? "<none>") + " repository " + repositorySha);
            // verify: packaged CLI stdout, packaged MCP skills_lock_verify text and the in-process SkillLockTool.Verify text.
            void LockParity(string label, bool expectSuccess, string? expectedCode, params (string Code, string Subject)[] expectedIssues)
            {
                var cliVerify = CliAt(packageDll, "skills-lock", "verify", packageSkills, lockPath);
                var mcpVerify = McpTool(packageMcp, "skills_lock_verify", new JsonObject { ["skills_path"] = packageSkills, ["lock_path"] = lockPath });
                var local = Bim.Mcp.SkillLockTool.Verify(packageSkills, lockPath);
                string? state = null;
                bool? verified = null;
                var issues = new List<(string? Code, string? Subject)>();
                var issuesRead = false;
                try
                {
                    using var verifyDocument = JsonDocument.Parse(cliVerify.Out);
                    var verifyRoot = verifyDocument.RootElement;
                    if (verifyRoot.ValueKind == JsonValueKind.Object)
                    {
                        if (verifyRoot.TryGetProperty("packs", out var verifyPacks) && verifyPacks.ValueKind == JsonValueKind.Array && verifyPacks.GetArrayLength() == 1)
                            state = Str(verifyPacks[0].GetRawText(), "state");
                        if (verifyRoot.TryGetProperty("verified", out var verifiedValue) && verifiedValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            verified = verifiedValue.GetBoolean();
                        if (verifyRoot.TryGetProperty("issues", out var verifyIssues) && verifyIssues.ValueKind == JsonValueKind.Array)
                        {
                            issuesRead = true;
                            foreach (var issue in verifyIssues.EnumerateArray())
                                issues.Add((Str(issue.GetRawText(), "code"), Str(issue.GetRawText(), "subject")));
                        }
                    }
                }
                catch (JsonException) { }
                var issuesExpected = issuesRead && issues.Count == expectedIssues.Length
                    && issues.Zip(expectedIssues).All(pair => pair.First.Code == pair.Second.Code && pair.First.Subject == pair.Second.Subject);
                var cliExpected = Str(cliVerify.Out, "mode") == "verify" && verified == expectSuccess && issuesExpected && (expectSuccess
                    ? cliVerify.Exit == 0 && Str(cliVerify.Out, "status") == "succeeded" && state == "ok"
                    : cliVerify.Exit == 1 && Str(cliVerify.Out, "status") == "failed" && Str(cliVerify.Out, "code") == expectedCode && state == expectedCode);
                Check(cliExpected, "P5-20 " + label + ": the packaged CLI skills-lock verify " + (expectSuccess ? "succeeds (exit 0, state ok)" : "refuses (exit 1, " + expectedCode + ")")
                    + " with mode verify, verified " + (expectSuccess ? "true" : "false") + " and exactly the issues ["
                    + string.Join(", ", expectedIssues.Select(issue => issue.Code + " " + issue.Subject)) + "]", D(cliVerify));
                Check(mcpVerify.Ok && mcpVerify.IsError == !expectSuccess && mcpVerify.Text == cliVerify.Out,
                    "P5-20 " + label + ": the packaged mcp/Bim.Mcp.dll skills_lock_verify text equals the packaged CLI verify stdout" + (expectSuccess ? "" : " (isError, the same envelope)"),
                    "mcp " + mcpVerify.Raw + " | cli " + D(cliVerify));
                Check(local.Succeeded == expectSuccess && local.Text == cliVerify.Out,
                    "P5-20 " + label + ": the in-process SkillLockTool.Verify over the same packaged skills/ and lock gives the packaged CLI stdout", "in process " + local.Text + " | cli " + D(cliVerify));
            }
            LockParity("the unchanged packaged skills/", true, null);
            var cabaManifest = Path.Combine(packageSkills, Caba, "skill.json");
            var cabaOriginal = File.ReadAllBytes(cabaManifest);
            try
            {
                // A still well-formed manifest with other bytes: drift against the pin.
                File.WriteAllBytes(cabaManifest, [.. cabaOriginal, (byte)'\n']);
                LockParity("a tampered (well-formed, one byte appended) packaged skill.json", false, "skill_lock_drift", ("skill_lock_drift", Caba));
                // A malformed manifest: the packaged skills directory itself is refused.
                File.WriteAllText(cabaManifest, "{");
                // Two issues in Core's (subject, code) order: the pinned pack's snapshot (subject the pack id) and the
                // skills directory load (subject the manifest path relative to the skills root).
                LockParity("a tampered (malformed) packaged skill.json", false, "skills_invalid", ("skills_invalid", Caba), ("skills_invalid", Caba + "/skill.json"));
            }
            finally
            {
                try { File.WriteAllBytes(cabaManifest, cabaOriginal); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Check(false, "P5-20: the tampered packaged skill.json is restored", error.GetType().Name + ": " + error.Message);
                }
            }
            LockParity("the restored packaged skill.json", true, null);
            Check(Read(lockPath).SequenceEqual(lockBytes), "P5-20: skills-lock verify (CLI, MCP, in process) never rewrites the lock: its bytes are those the pin wrote", "lock sha " + Hex(Read(lockPath)) + " pinned " + Hex(lockBytes));

            // ---- 3. a removed marker: outside any repository there is nothing to fall back to ----
            foreach (var marker in new[] { version, manifest })
            {
                var aside = marker + ".off";
                File.Move(marker, aside);
                foreach (var (label, verb, tail2, _, missingCode) in cases)
                {
                    var result = RunVerb(packageDll, verb, tail2);
                    Check(result.Exit == 1 && Str(result.Out, "code") == missingCode && result.Bytes.Length == 0,
                        label + ": without " + Path.GetFileName(marker) + " (outside any repository) the folder is no package root -> " + missingCode + ", no file", D((result.Exit, result.Out, result.Err)));
                }
                McpEqualsCli("without " + Path.GetFileName(marker) + " (outside any repository) the packaged MCP text is the packaged CLI envelope generate_typology_repository_not_found",
                    composedRequest, r => r.Exit == 1 && Str(r.Out, "code") == "generate_typology_repository_not_found");
                File.Move(aside, marker);
            }

            // A repository above the package (global.json and a copy of the repository skills/), the package's own skills
            // made malformed: with both markers the package is read (refused), with either removed the repository is.
            File.WriteAllText(Path.Combine(caseRoot, "global.json"), "{}\n");
            var repoSkills = Path.Combine(root, "skills");
            foreach (var file in Directory.GetFiles(repoSkills, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(caseRoot, "skills", Path.GetRelativePath(repoSkills, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            foreach (var file in Directory.GetFiles(packageSkills, "skill.json", SearchOption.AllDirectories)) File.WriteAllText(file, "{");
            foreach (var (label, verb, tail2, invalidCode, _) in cases)
            {
                var result = RunVerb(packageDll, verb, tail2);
                Check(result.Exit == 1 && Str(result.Out, "code") == invalidCode && Str(result.Out, "detail") == "json_invalid" && result.Bytes.Length == 0,
                    label + ": with both markers the package's (malformed) skills/ is read before the repository above it -> " + invalidCode + " (json_invalid), no file", D((result.Exit, result.Out, result.Err)));
            }
            McpEqualsCli("a malformed packaged skill.json (repository above) is refused by the packaged MCP with the packaged CLI envelope generate_typology_pack_invalid (json_invalid)",
                composedRequest, r => r.Exit == 1 && Str(r.Out, "code") == "generate_typology_pack_invalid" && Str(r.Out, "detail") == "json_invalid");
            foreach (var marker in new[] { version, manifest })
            {
                var aside = marker + ".off";
                File.Move(marker, aside);
                foreach (var (label, verb, tail2, _, _) in cases)
                {
                    var repo = repository[label];
                    var result = RunVerb(packageDll, verb, tail2);
                    Check(result.Exit == 0 && result.Bytes.SequenceEqual(repo.Bytes) && result.Out == repo.Out,
                        label + ": without " + Path.GetFileName(marker) + " the lookup falls back to the repository above the package: the repository run's bytes and stdout",
                        D((result.Exit, result.Out, result.Err)) + " written " + result.Bytes.Length.ToString(CultureInfo.InvariantCulture) + " repository " + repo.Bytes.Length.ToString(CultureInfo.InvariantCulture));
                }
                McpEqualsCli("without " + Path.GetFileName(marker) + " the packaged MCP falls back to the repository above the package like the packaged CLI (composed pin)",
                    composedRequest, r => r.Exit == 0, "without " + Path.GetFileName(marker) + " (repository fallback)");
                File.Move(aside, marker);
            }
        }
        finally { try { if (Directory.Exists(caseRoot)) Directory.Delete(caseRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    // P4-30: the package scripts/package.ps1 built ships no Bim.Gltf in mcp/ (P4-29 dropped the MCP reference): no
    // mcp/**/Bim.Gltf.* file, no mcp/ Bim.Gltf sbom.json assembly line and no mcp/ Bim.Gltf manifest.json file line, while
    // cli/Bim.Gltf.dll, its sbom line and its manifest line (both the file's sha256) stay (positive control). Fail-closed
    // (a missing mcp/ or an unreadable sbom/manifest is a leak). Self-tested on a copy of the layout (planted file, planted
    // sbom line, planted manifest line, unreadable sbom, removed control); the package's own files are fingerprinted
    // before and after and must be unchanged.
    static void McpWithoutGltf(string package, string caseRoot, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail) => check(condition, "P4-30: " + name, detail);
        static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        static bool IsMcpGltf(string relative) => relative.Replace('\\', '/').StartsWith("mcp/", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(relative).StartsWith("Bim.Gltf.", StringComparison.OrdinalIgnoreCase);
        // (path, sha256) of every entry of a JSON array property (sbom "assemblies", manifest "files"); null when unreadable.
        static List<(string Path, string? Sha)>? Entries(string file, string property)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return null;
                var entries = new List<(string Path, string? Sha)>();
                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) return null;
                    entries.Add((path.GetString()!, entry.TryGetProperty("sha256", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null));
                }
                return entries;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
        // Every way Bim.Gltf reaches mcp/ in a layout; empty when there is none.
        static List<string> Leaks(string layout)
        {
            var leaks = new List<string>();
            var mcp = Path.Combine(layout, "mcp");
            if (!Directory.Exists(mcp)) leaks.Add("<no mcp/ folder>");
            else foreach (var file in Directory.GetFiles(mcp, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(layout, file).Replace('\\', '/');
                    if (IsMcpGltf(relative)) leaks.Add("file " + relative);
                }
            foreach (var (name, property) in new[] { ("sbom.json", "assemblies"), ("manifest.json", "files") })
            {
                var entries = Entries(Path.Combine(layout, name), property);
                if (entries is null) leaks.Add("<" + name + " " + property + " unreadable>");
                else leaks.AddRange(entries.Where(entry => IsMcpGltf(entry.Path)).Select(entry => name + " line " + entry.Path));
            }
            return leaks;
        }
        // The positive control: cli/Bim.Gltf.dll with exactly one sbom line and one manifest line carrying its sha256.
        static List<string> ControlMissing(string layout)
        {
            var missing = new List<string>();
            var cliGltf = Path.Combine(layout, "cli", "Bim.Gltf.dll");
            var sha = File.Exists(cliGltf) ? Sha(cliGltf) : null;
            if (sha is null) missing.Add("file cli/Bim.Gltf.dll");
            foreach (var (name, property) in new[] { ("sbom.json", "assemblies"), ("manifest.json", "files") })
            {
                var lines = (Entries(Path.Combine(layout, name), property) ?? []).Where(entry => entry.Path == "cli/Bim.Gltf.dll").ToList();
                if (lines.Count != 1 || sha is null || lines[0].Sha != sha)
                    missing.Add(name + " line cli/Bim.Gltf.dll (" + lines.Count.ToString(CultureInfo.InvariantCulture) + " lines, sha " + string.Join("/", lines.Select(line => line.Sha ?? "<none>")) + ", file " + (sha ?? "<none>") + ")");
            }
            return missing;
        }
        static string Fingerprint(string layout) => string.Join("\n", Directory.GetFiles(layout, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(layout, file).Replace('\\', '/') + " " + Sha(file)).OrderBy(line => line, StringComparer.Ordinal));
        static string Show(List<string> items) => "[" + string.Join(", ", items) + "]";
        static bool Exactly(List<string> items, params string[] expected) => items.SequenceEqual(expected, StringComparer.Ordinal);

        var before = Fingerprint(package);
        var leaks = Leaks(package);
        Check(leaks.Count == 0, "the package scripts/package.ps1 built has no mcp/ Bim.Gltf file, sbom.json line or manifest.json line", Show(leaks));
        var control = ControlMissing(package);
        Check(control.Count == 0, "positive control: the package keeps cli/Bim.Gltf.dll with one sbom.json and one manifest.json line carrying its sha256", Show(control));

        // Self-test on a copy of the layout (mcp/, cli/Bim.Gltf.dll, sbom.json, manifest.json), never the package itself.
        var copy = Path.Combine(caseRoot, "p4-30-layout-copy");
        try
        {
            foreach (var file in Directory.GetFiles(Path.Combine(package, "mcp"), "*", SearchOption.AllDirectories)
                .Concat([Path.Combine(package, "cli", "Bim.Gltf.dll"), Path.Combine(package, "sbom.json"), Path.Combine(package, "manifest.json")]))
            {
                if (!File.Exists(file)) continue;
                var destination = Path.Combine(copy, Path.GetRelativePath(package, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var copied = Leaks(copy);
            var copiedControl = ControlMissing(copy);
            Check(copied.Count == 0 && copiedControl.Count == 0, "self-test: the unplanted copy of the layout reads like the package (no leak, control present)", Show(copied) + " control " + Show(copiedControl));

            var planted = Path.Combine(copy, "mcp", "Bim.Gltf.dll");
            File.Copy(Path.Combine(copy, "cli", "Bim.Gltf.dll"), planted);
            var plantedFile = Leaks(copy);
            Check(Exactly(plantedFile, "file mcp/Bim.Gltf.dll"), "self-test: a planted mcp/Bim.Gltf.dll file in a copy of the layout is caught (and only it)", Show(plantedFile));
            File.Delete(planted);

            foreach (var (name, property) in new[] { ("sbom.json", "assemblies"), ("manifest.json", "files") })
            {
                var path = Path.Combine(copy, name);
                var original = File.ReadAllBytes(path);
                var node = JsonNode.Parse(new UTF8Encoding(false).GetString(original))!.AsObject();
                node[property]!.AsArray().Add(new JsonObject { ["path"] = "mcp/Bim.Gltf.dll", ["sha256"] = "00" });
                File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(false));
                var plantedLine = Leaks(copy);
                Check(Exactly(plantedLine, name + " line mcp/Bim.Gltf.dll"), "self-test: a planted mcp/Bim.Gltf.dll " + name + " line is caught (and only it)", Show(plantedLine));
                File.WriteAllText(path, "{");
                var unreadable = Leaks(copy);
                Check(Exactly(unreadable, "<" + name + " " + property + " unreadable>"), "self-test: an unreadable " + name + " is a leak (fail-closed)", Show(unreadable));
                File.WriteAllBytes(path, original);
            }

            File.Delete(Path.Combine(copy, "cli", "Bim.Gltf.dll"));
            var noControl = ControlMissing(copy);
            Check(noControl.Contains("file cli/Bim.Gltf.dll") && noControl.Count == 3, "self-test: a copy without cli/Bim.Gltf.dll fails the positive control (file, sbom and manifest lines)", Show(noControl));
        }
        finally { try { if (Directory.Exists(copy)) Directory.Delete(copy, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        var after = Fingerprint(package);
        Check(after == before, "the check never modifies the package (every file's path and sha256 unchanged)", before.Length.ToString(CultureInfo.InvariantCulture) + " -> " + after.Length.ToString(CultureInfo.InvariantCulture) + " chars");
    }

    // A real child process; `wait`/`kill` replace WaitForExit(timeout)/Kill(true) only so a test can inject the race of a
    // child that exits between a timed-out wait and the kill (P5-19).
    static (int Exit, string Out, string Err) Exec(string file, IEnumerable<string> arguments, int timeoutMilliseconds, Func<Process, int, bool>? wait = null, Action<Process>? kill = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
        // The harness runs under `dotnet run`; a nested build must not inherit an outer MSBuild's resolution.
        foreach (var name in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" }) start.Environment.Remove(name);
        // No MSBuild node reuse or build server: a nested build leaves no process behind the harness.
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("process did not start: " + file);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!WaitOrKill(process, timeoutMilliseconds, wait, kill)) return (-1, "<timed out>", "");
        var text = stdout.Result;
        if (text.EndsWith('\n')) text = text[..^1];
        if (text.EndsWith('\r')) text = text[..^1];
        return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
    }

    // True when the child exited within the timeout (then fully waited); false when it timed out and was killed. P5-19:
    // the child can exit between the timed-out wait and Kill; Kill may then throw (InvalidOperationException, or
    // Win32Exception on Windows). That failure is tolerated only when the child really has exited, and the run is still
    // reported timed out; a Kill failure on a running child propagates.
    static bool WaitOrKill(Process process, int timeoutMilliseconds, Func<Process, int, bool>? wait = null, Action<Process>? kill = null)
    {
        if ((wait ?? ((child, timeout) => child.WaitForExit(timeout)))(process, timeoutMilliseconds)) { process.WaitForExit(); return true; }
        try { (kill ?? (child => child.Kill(true)))(process); }
        catch (Exception error) when ((error is InvalidOperationException or Win32Exception) && Exited(process)) { }
        process.WaitForExit();
        return false;
    }

    static bool Exited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return false; }
    }

    // verify.ps1 exports BIM_PWSH (its own pwsh); otherwise the first pwsh on PATH.
    static string? FindPwsh()
    {
        if (Environment.GetEnvironmentVariable("BIM_PWSH") is { Length: > 0 } exported && File.Exists(exported)) return exported;
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe" } : new[] { "pwsh" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                string candidate;
                try { candidate = Path.Combine(directory, name); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
