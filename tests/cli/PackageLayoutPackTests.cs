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
//   5. P5-18: the packaged MCP server (mcp/Bim.Mcp.dll over stdio, the same package) finds the packaged skills/ too:
//      generate_typology returns the packaged CLI output file bytes ("skills":[] gives the pin a8c62da5..., and the
//      request with skills), every refusal is the packaged CLI stdout line byte for byte (malformed packaged skill.json
//      with a repository above -> generate_typology_pack_invalid json_invalid; a removed marker -> the repository or
//      generate_typology_repository_not_found, exactly like the CLI).
//   4. Skipped (a counted SKIP) only where pwsh is absent; on GitHub Actions on Linux a missing pwsh is a failure. P10-18:
//      packaging runs on macOS too (its first-party Bim.*.dll attributed through deps.json), so no macOS skip remains.
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
        static bool UnderRepository(string directory)
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return true;
            return false;
        }
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
        static (int Exit, string Out, string Err) Exec(string file, IEnumerable<string> arguments, int timeoutMilliseconds)
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            // The harness runs under `dotnet run`; a nested build must not inherit an outer MSBuild's resolution.
            foreach (var name in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" }) start.Environment.Remove(name);
            // No MSBuild node reuse or build server: a nested build leaves no process behind the harness.
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("process did not start: " + file);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMilliseconds)) { process.Kill(true); process.WaitForExit(); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        (int Exit, string Out, string Err) CliAt(string dll, params string[] arguments) => Exec(dotnet, [dll, .. arguments], 180000);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        // P5-18: one generate_typology tools/call line to a real MCP server process over stdio; the first stdout line parsed.
        (bool Ok, bool IsError, string Text, string Raw) McpTypology(string mcpDll, string requestPath)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(mcpDll);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var line = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 18, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "generate_typology", ["arguments"] = new JsonObject { ["request_path"] = requestPath } } }.ToJsonString();
            try { process.StandardInput.Write(line + "\n"); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(180000)) { process.Kill(true); process.WaitForExit(); return (false, true, "", "<timed out>"); }
            process.WaitForExit();
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
            var requiredFirstParty = new[] { "cli/Bim.Cad.dll", "cli/Bim.Cli.dll", "cli/Bim.Core.dll", "cli/Bim.Evaluation.dll", "cli/Bim.Generative.dll", "cli/Bim.Gltf.dll", "cli/Bim.Ifc.Compiler.dll", "mcp/Bim.Core.dll", "mcp/Bim.Generative.dll", "mcp/Bim.Gltf.dll", "mcp/Bim.Mcp.dll" };
            var notFirstParty = requiredFirstParty.Where(required => !bimLines.ContainsKey(required))
                .Concat(bimLines.Where(pair => !pair.Value.TryGetProperty("license", out var license) || license.ValueKind != JsonValueKind.String || license.GetString() != "NOASSERTION" || pair.Value.TryGetProperty("package", out _)).Select(pair => pair.Key)).ToArray();
            Check(notFirstParty.Length == 0, "P10-18: sbom.json lists every cli/ and mcp/ Bim.* assembly (Cad, Cli, Core, Evaluation, Generative, Gltf, Ifc.Compiler; Core, Generative, Gltf, Mcp) first-party: NOASSERTION without a package", string.Join(", ", notFirstParty));

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
            void McpEqualsCli(string label, string requestPath, Func<(int Exit, string Out, string Err), bool> expected)
            {
                var output = Output();
                var cliRun = CliAt(packageDll, "generate-typology", requestPath, output);
                var cliBytes = Read(output);
                var mcp = McpTypology(packageMcp, requestPath);
                var same = cliRun.Exit == 0 ? mcp.Ok && !mcp.IsError && Utf8(mcp.Text).SequenceEqual(cliBytes) : mcp.Ok && mcp.IsError && mcp.Text == cliRun.Out;
                Check(expected(cliRun) && same, "P5-18 " + label, "mcp " + mcp.Raw + " | cli " + D(cliRun));
            }

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
                    composedRequest, r => r.Exit == 0);
                File.Move(aside, marker);
            }
        }
        finally { try { if (Directory.Exists(caseRoot)) Directory.Delete(caseRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
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
