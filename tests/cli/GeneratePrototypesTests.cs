using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-09 `generate-prototypes request.json output.json`: PrototypePipeline.Run on a JSON request, the canonical
// PrototypePipelineSerializer JSON written to a new file. Oracles, all against the real Release CLI process:
//   1. The output file is byte-identical to PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(...)) computed in
//      process for the same request and options (UTF-8, no BOM), and byte-identical across runs.
//   2. Without "project_id" the project id is the documented UUIDv8 of SHA-256 of the canonical request (computed here
//      from a hand-written canonical string); member order, whitespace and explicit defaults do not change it, a
//      different seed does. The usage text documents the rule.
//   3. Never-overwrite: an existing output is refused (output_exists) and left byte- and mtime-identical.
//   4. Every failure (usage, output path, oversize/missing/malformed request, unknown pack, pipeline refusal) prints a
//      typed {"status":"failed","code":...} envelope, exits 1 and leaves no output file; a pipeline refusal carries the
//      library's refusals exactly.
//   5. P7-12: a CLI in the release package layout (<package>/cli next to <package>/skills, outside any repository) reads
//      <package>/skills/<pack>/skill.json and writes the repository run's bytes and stdout; without skills/ it still
//      refuses generate_prototypes_repository_not_found; plan-layouts --pack keeps its repository-only lookup.
//   6. P7-25: a non-refusal exception outside the guarded block (forced in process through GenerateCommand.UnguardedStageProbe)
//      is the typed generate_prototypes_internal_error line, diagnostic once, exit 1, no file (UnguardedStage below).
//   7. P7-26: a non-refusal exception after GltfExporter.WriteNew succeeded (forced in process through
//      GenerateCommand.PostWriteStageProbe) is the same typed line, and the just-written file is deleted (PostWriteStage).
internal static class GeneratePrototypesTests
{
    const string PackId = "ar.caba.codigo-edificacion";

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "generate-prototypes");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments) => CliAt(cliDll, arguments);
        (int Exit, string Out, string Err) CliAt(string dll, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(dll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        static string D((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        var counter = 0;
        string Input(string name, string content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        string Output(string name = "out.json") => Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
        static JsonElement? Parse(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        static string? Str(JsonElement? element, string name) =>
            element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        // A typed failure: exit 1, one JSON envelope with status "failed" and the code, and no output file.
        void ExpectFailure(string name, string code, string output, params string[] arguments)
        {
            var result = Cli(arguments);
            var envelope = Parse(result.Out);
            Check(result.Exit == 1 && Str(envelope, "status") == "failed" && Str(envelope, "code") == code && !File.Exists(output) && !Directory.Exists(output),
                name + " -> " + code + ", exit 1, no output file", D(result));
        }

        var caba = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", PackId, "skill.json")));
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // ---- registration and usage ----
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"generate-prototypes\": GenerateCommand.Run(args); break;", StringComparison.Ordinal), "Program.cs registers generate-prototypes");
        var usageOut = Path.Combine(scratch, "usage-never.json");
        ExpectFailure("no arguments", "generate_prototypes_usage", usageOut, "generate-prototypes");
        ExpectFailure("one argument", "generate_prototypes_usage", usageOut, "generate-prototypes", Input("one.json", "{}"));
        ExpectFailure("three arguments", "generate_prototypes_usage", usageOut, "generate-prototypes", Input("a.json", "{}"), usageOut, "extra");
        var usage = Cli("generate-prototypes");
        var usageDetail = Str(Parse(usage.Out), "detail") ?? "";
        Check(usageDetail.Contains("generate-prototypes request.json output.json", StringComparison.Ordinal) && usageDetail.Contains("project_id", StringComparison.Ordinal)
            && usageDetail.Contains("SHA-256", StringComparison.Ordinal) && usageDetail.Contains("UUIDv8", StringComparison.Ordinal)
            && usageDetail.Contains("repository", StringComparison.Ordinal) && usageDetail.Contains("generate_prototypes_repository_not_found", StringComparison.Ordinal)
            && usageDetail.Contains("package", StringComparison.Ordinal),
            "the usage text documents the verb, the deterministic default project id rule and the package-then-repository pack lookup", D(usage));

        // ---- success: equals the in-process pipeline, canonical bytes, deterministic ----
        var explicitId = Guid.Parse("00000000-0000-4000-8000-0000000007a9");
        const string twoBed = "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}";
        var twoBedRequest = Input("two-bed.json", twoBed);
        var expected = Utf8(PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 2, 0),
            new PrototypePipelineOptions(caba, explicitId))));
        var first = Output();
        var run1 = Cli("generate-prototypes", twoBedRequest, first);
        var written1 = File.Exists(first) ? File.ReadAllBytes(first) : Array.Empty<byte>();
        Check(run1.Exit == 0 && written1.SequenceEqual(expected), "the output is PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(request, options)) byte for byte",
            D(run1) + " written " + written1.Length.ToString(CultureInfo.InvariantCulture) + " expected " + expected.Length.ToString(CultureInfo.InvariantCulture));
        Check(written1.Length > 3 && !(written1[0] == 0xEF && written1[1] == 0xBB && written1[2] == 0xBF), "the output has no byte order mark");
        var success = Parse(run1.Out);
        Check(Str(success, "status") == "succeeded" && Str(success, "sha256") == Hex(expected) && Str(success, "project_id") == explicitId.ToString("D")
            && Str(success, "project_id_source") == "request" && Str(success, "pipeline_status") is "ranked" or "infeasible"
            && success is { } s && s.TryGetProperty("bytes", out var bytesValue) && bytesValue.TryGetInt64(out var bytesCount) && bytesCount == expected.LongLength,
            "stdout reports succeeded, the explicit project id, the pipeline status, the byte count and the SHA-256 of the file", D(run1));
        var second = Output();
        var run2 = Cli("generate-prototypes", twoBedRequest, second);
        Check(run2.Exit == 0 && File.Exists(second) && File.ReadAllBytes(second).SequenceEqual(written1) && run2.Out == run1.Out,
            "a second run writes byte-identical output and the same stdout", D(run2));

        // ---- deterministic default project id ----
        const string canonical = "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":0,\"door_width_m\":0.9,"
            + "\"weights\":{\"area_fit\":0.3,\"adjacency\":0.25,\"proportions\":0.15,\"compactness\":0.1,\"perimeter\":0.1,\"compliance\":0.1},"
            + "\"fixed_rooms\":[],\"fixed_walls\":[],\"pack\":\"ar.caba.codigo-edificacion\",\"revision\":0,\"circulation_weight\":0.15,\"facade_weight\":0,\"snap\":false}";
        var derivedId = DerivedId(canonical);
        Check(derivedId.ToString("D")[14] == '8' && "89ab".Contains(derivedId.ToString("D")[19]), "the documented default id is an RFC 9562 version 8 UUID", derivedId.ToString("D"));
        var minimal = Input("one-bed.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2}");
        var reordered = Input("one-bed-explicit.json", "{\n  \"snap\": false, \"facade_weight\": 0, \"circulation_weight\": 0.15, \"revision\": 0,\n  \"pack\": \"ar.caba.codigo-edificacion\", \"door_width_m\": 0.9,"
            + " \"seed\": 0, \"alternatives\": 2,\n  \"lot\": { \"depth_m\": 12, \"width_m\": 7 }, \"typology\": 1\n}\n");
        var expectedDerived = Utf8(PrototypePipelineSerializer.Serialize(PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 0),
            new PrototypePipelineOptions(caba, derivedId))));
        var derivedOut = Output();
        var run3 = Cli("generate-prototypes", minimal, derivedOut);
        var derivedBytes = File.Exists(derivedOut) ? File.ReadAllBytes(derivedOut) : Array.Empty<byte>();
        Check(run3.Exit == 0 && derivedBytes.SequenceEqual(expectedDerived) && Str(Parse(run3.Out), "project_id") == derivedId.ToString("D")
            && Str(Parse(run3.Out), "project_id_source") == "derived",
            "without project_id the id is UUIDv8(SHA-256(\"bim-agentic-os/generate-prototypes/v1\\n\" + canonical request)) and the output equals the in-process run with it", D(run3));
        var reorderedOut = Output();
        var run4 = Cli("generate-prototypes", reordered, reorderedOut);
        Check(run4.Exit == 0 && File.Exists(reorderedOut) && File.ReadAllBytes(reorderedOut).SequenceEqual(derivedBytes) && derivedBytes.Length > 0,
            "member order, whitespace and explicit default values give the same default id and byte-identical output", D(run4));
        var otherSeedOut = Output();
        var run5 = Cli("generate-prototypes", Input("one-bed-seed.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":1}"), otherSeedOut);
        var otherSeedId = DerivedId(canonical.Replace("\"seed\":0", "\"seed\":1", StringComparison.Ordinal));
        Check(run5.Exit == 0 && Str(Parse(run5.Out), "project_id") == otherSeedId.ToString("D") && otherSeedId != derivedId,
            "a different seed derives a different default id, by the same rule", D(run5));

        // SF1: -0 is 0, an empty weights object is the default weights, [] is an absent approval list.
        var zeros = Output();
        var run6 = Cli("generate-prototypes", Input("one-bed-zeros.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":-0,"
            + "\"facade_weight\":-0.0,\"weights\":{},\"fixed_rooms\":[],\"fixed_walls\":[]}"), zeros);
        Check(run6.Exit == 0 && File.Exists(zeros) && File.ReadAllBytes(zeros).SequenceEqual(derivedBytes) && Str(Parse(run6.Out), "project_id") == derivedId.ToString("D"),
            "-0 values, \"weights\":{} and empty fixed_rooms / fixed_walls give the minimal request's id and bytes", D(run6));
        var negativeZeroWeights = Output();
        var run7 = Cli("generate-prototypes", Input("one-bed-zero-weight.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,"
            + "\"weights\":{\"area_fit\":0.3,\"adjacency\":0.25,\"proportions\":0.15,\"compactness\":0.1,\"perimeter\":0.1,\"compliance\":0.1}}"), negativeZeroWeights);
        Check(run7.Exit == 0 && File.Exists(negativeZeroWeights) && File.ReadAllBytes(negativeZeroWeights).SequenceEqual(derivedBytes),
            "explicit default weights give the minimal request's id and bytes", D(run7));

        // SF4: weights in another member order, snap, circulation and facade weights: derived id and in-process equality.
        const string tunedCanonical = "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":3,\"door_width_m\":0.9,"
            + "\"weights\":{\"area_fit\":0.5,\"adjacency\":0.25,\"proportions\":0.15,\"compactness\":0.1,\"perimeter\":0.1,\"compliance\":0.2},"
            + "\"fixed_rooms\":[],\"fixed_walls\":[],\"pack\":\"ar.caba.codigo-edificacion\",\"revision\":2,\"circulation_weight\":0.3,\"facade_weight\":0.2,\"snap\":true}";
        var tunedId = DerivedId(tunedCanonical);
        var tunedLibrary = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 3, Weights: new ScoringWeights(0.5, 0.25, 0.15, 0.1, 0.1, 0.2)),
            new PrototypePipelineOptions(caba, tunedId, 2, 0.3, Snap: new PrototypeSnapOptions(), FacadeWeight: 0.2));
        var tunedOut = Output();
        var run8 = Cli("generate-prototypes", Input("one-bed-tuned.json", "{\"snap\":true,\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"facade_weight\":0.2,"
            + "\"circulation_weight\":0.3,\"revision\":2,\"seed\":3,\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2}"), tunedOut);
        Check(tunedLibrary.Status != PrototypePipelineStatus.Refused && run8.Exit == 0 && File.Exists(tunedOut)
            && File.ReadAllBytes(tunedOut).SequenceEqual(Utf8(PrototypePipelineSerializer.Serialize(tunedLibrary))) && Str(Parse(run8.Out), "project_id") == tunedId.ToString("D"),
            "partial weights in any member order, snap, revision, circulation and facade weights: documented derived id and output equal to the in-process run",
            D(run8) + " library " + tunedLibrary.Status);

        // SF4: approvals (a real room of a generated layout, a lot-edge wall with a non-ASCII id): canonical arrays and escaping.
        var generated = PrototypeGenerator.Generate(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 0));
        var sourceRoom = generated.Alternatives?.Alternatives.Select(a => a.Plan).FirstOrDefault(p => p.Status == SpacePlanStatus.Feasible)?.Rooms.FirstOrDefault(r => r.Instance == 1);
        Check(sourceRoom is not null, "a generated 1-bedroom layout provides a room to approve");
        if (sourceRoom is not null)
        {
            string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            var rect = sourceRoom.Rect;
            var approvedCanonical = "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":0,\"door_width_m\":0.9,"
                + "\"weights\":{\"area_fit\":0.3,\"adjacency\":0.25,\"proportions\":0.15,\"compactness\":0.1,\"perimeter\":0.1,\"compliance\":0.1},"
                + "\"fixed_rooms\":[{\"room_id\":\"" + sourceRoom.RoomId + "\",\"x\":" + R(rect.X) + ",\"y\":" + R(rect.Y) + ",\"width\":" + R(rect.Width) + ",\"depth\":" + R(rect.Depth) + "}],"
                + "\"fixed_walls\":[{\"id\":\"muro-\\u00F1\",\"x0\":0,\"y0\":0,\"x1\":7,\"y1\":0}],"
                + "\"pack\":\"ar.caba.codigo-edificacion\",\"revision\":0,\"circulation_weight\":0.15,\"facade_weight\":0,\"snap\":false}";
            var approvedId = DerivedId(approvedCanonical);
            var approvedLibrary = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 0,
                new[] { new FixedRoom(sourceRoom.RoomId, rect) }, new[] { new FixedWall("muro-\u00F1", 0, 0, 7, 0) }), new PrototypePipelineOptions(caba, approvedId));
            var approvedOut = Output();
            var run9 = Cli("generate-prototypes", Input("one-bed-approved.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,"
                + "\"fixed_walls\":[{\"y1\":0,\"x1\":7,\"y0\":0,\"x0\":0,\"id\":\"muro-\u00F1\"}],"
                + "\"fixed_rooms\":[{\"depth\":" + R(rect.Depth) + ",\"width\":" + R(rect.Width) + ",\"y\":" + R(rect.Y) + ",\"x\":" + R(rect.X) + ",\"room_id\":\"" + sourceRoom.RoomId + "\"}]}"), approvedOut);
            Check(approvedLibrary.Status != PrototypePipelineStatus.Refused && run9.Exit == 0 && File.Exists(approvedOut)
                && File.ReadAllBytes(approvedOut).SequenceEqual(Utf8(PrototypePipelineSerializer.Serialize(approvedLibrary))) && Str(Parse(run9.Out), "project_id") == approvedId.ToString("D"),
                "fixed rooms and walls (non-ASCII wall id, escaped as System.Text.Json does): documented derived id and output equal to the in-process run",
                D(run9) + " library " + approvedLibrary.Status + " " + string.Join(",", approvedLibrary.Refusals.Select(r => r.Code)));
        }

        // SF3 / SF4: a CLI copy outside any repository, and a copy inside a scratch repository with its own packs.
        var cliDirectory = Path.GetDirectoryName(cliDll)!;
        string CopyCli(string target)
        {
            foreach (var file in Directory.GetFiles(cliDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(cliDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            return Path.Combine(target, Path.GetFileName(cliDll));
        }
        // scripts/verify.ps1 points TMPDIR inside the checkout (.bim/verification/<run>/tmp), so the first candidate base
        // directory with no global.json in it or any ancestor is used (RUNNER_TEMP on CI runners).
        static bool UnderRepository(string directory)
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return true;
            return false;
        }
        var outsideBase = new[] { Environment.GetEnvironmentVariable("RUNNER_TEMP"), Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows() ? null : "/tmp" }
            .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !UnderRepository(candidate));
        if (outsideBase is null) Console.WriteLine("SKIP generate-prototypes: no writable directory outside a repository for the copied CLI");
        else
        {
            var outsideRoot = Path.Combine(outsideBase, "bim-generate-outside-" + Guid.NewGuid().ToString("N"));
            try
            {
                var outsideDll = CopyCli(Path.Combine(outsideRoot, "cli"));
                var outsideOut = Output();
                var outside = CliAt(outsideDll, "generate-prototypes", twoBedRequest, outsideOut);
                Check(outside.Exit == 1 && Str(Parse(outside.Out), "code") == "generate_prototypes_repository_not_found" && !File.Exists(outsideOut),
                    "a CLI copy outside any repository refuses with generate_prototypes_repository_not_found, exit 1, no file", D(outside) + " copy under " + outsideBase);
            }
            finally { try { Directory.Delete(outsideRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

            // P7-12: the release package layout (<package>/cli next to <package>/skills, no global.json anywhere above):
            // the pack is read from <package>/skills/<id>/skill.json and the run is byte-equal to the repository run.
            var packageRoot = Path.Combine(outsideBase, "bim-generate-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                var packageDll = CopyCli(Path.Combine(packageRoot, "cli"));
                // The package markers scripts/package.ps1 writes at the package root (their content is not read by the lookup).
                File.WriteAllText(Path.Combine(packageRoot, "VERSION"), "0.0.0-test\n");
                File.WriteAllText(Path.Combine(packageRoot, "manifest.json"), "{}\n");
                var repoSkills = Path.Combine(root, "skills");
                foreach (var file in Directory.GetFiles(repoSkills, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(packageRoot, "skills", Path.GetRelativePath(repoSkills, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
                var packagedOut = Output();
                var packaged = CliAt(packageDll, "generate-prototypes", twoBedRequest, packagedOut);
                Check(!UnderRepository(packageRoot) && packaged.Exit == 0 && File.Exists(packagedOut) && File.ReadAllBytes(packagedOut).SequenceEqual(written1) && packaged.Out == run1.Out,
                    "a CLI in the package layout (cli/ next to skills/, outside any repository) reads skills/<pack>/skill.json and writes the repository run's bytes and stdout",
                    D(packaged) + " package under " + outsideBase);
                var packagedDerivedOut = Output();
                var packagedDerived = CliAt(packageDll, "generate-prototypes", minimal, packagedDerivedOut);
                Check(packagedDerived.Exit == 0 && File.Exists(packagedDerivedOut) && File.ReadAllBytes(packagedDerivedOut).SequenceEqual(derivedBytes) && packagedDerived.Out == run3.Out,
                    "the package layout derives the same default project id and bytes as the repository run", D(packagedDerived));
                var packagedMissingOut = Output();
                var packagedMissing = CliAt(packageDll, "generate-prototypes", Input("package-unknown-pack.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"pack\":\"xx.no-such.pack\"}"), packagedMissingOut);
                Check(packagedMissing.Exit == 1 && Str(Parse(packagedMissing.Out), "code") == "generate_prototypes_pack_unavailable" && Str(Parse(packagedMissing.Out), "detail") == "input_not_found" && !File.Exists(packagedMissingOut),
                    "a pack absent from the package's skills/ -> generate_prototypes_pack_unavailable (input_not_found), no file", D(packagedMissing));
                // P7-15: plan-layouts --pack reads the package's skills/ too (SkillPackLookup.Load with packageFirst), byte-equal to the repository run.
                var briefPath = Input("package-brief.json", "{\"brief_version\":1,\"name\":\"Small studio\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
                    + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}],"
                    + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"bath\",\"kind\":\"required\"}]}");
                var planOut = Output();
                var plan = CliAt(packageDll, "plan-layouts", briefPath, planOut, "--alternatives", "1", "--pack", PackId);
                var repoPlanOut = Output();
                var repoPlan = Cli("plan-layouts", briefPath, repoPlanOut, "--alternatives", "1", "--pack", PackId);
                Check(repoPlan.Exit == 0 && plan.Exit == 0 && File.Exists(planOut) && File.Exists(repoPlanOut) && File.ReadAllBytes(planOut).SequenceEqual(File.ReadAllBytes(repoPlanOut)) && plan.Out == repoPlan.Out,
                    "plan-layouts --pack in the package layout reads the package's skills/ and writes the repository run's bytes and stdout", D(plan) + " repository " + D(repoPlan));
                // Without a package marker the folder is not a package root: the repository walk follows (none here).
                File.Move(Path.Combine(packageRoot, "manifest.json"), Path.Combine(packageRoot, "manifest.json.off"));
                var noMarkerOut = Output();
                var noMarker = CliAt(packageDll, "generate-prototypes", twoBedRequest, noMarkerOut);
                Check(noMarker.Exit == 1 && Str(Parse(noMarker.Out), "code") == "generate_prototypes_repository_not_found" && !File.Exists(noMarkerOut),
                    "cli/ next to skills/ without manifest.json is not a package root: generate_prototypes_repository_not_found, no file", D(noMarker));
                File.Move(Path.Combine(packageRoot, "manifest.json.off"), Path.Combine(packageRoot, "manifest.json"));
                Directory.Delete(Path.Combine(packageRoot, "skills"), true);
                var noSkillsOut = Output();
                var noSkills = CliAt(packageDll, "generate-prototypes", twoBedRequest, noSkillsOut);
                Check(noSkills.Exit == 1 && Str(Parse(noSkills.Out), "code") == "generate_prototypes_repository_not_found" && !File.Exists(noSkillsOut),
                    "the package layout without its skills/ folder still refuses generate_prototypes_repository_not_found, no file", D(noSkills));
            }
            finally { try { Directory.Delete(packageRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        var fakeRepo = Path.Combine(scratch, "fake-repo");
        Directory.CreateDirectory(fakeRepo);
        File.WriteAllText(Path.Combine(fakeRepo, "global.json"), "{}");
        var cabaText = File.ReadAllText(Path.Combine(root, "skills", PackId, "skill.json"));
        void Pack(string folder, string text) { Directory.CreateDirectory(Path.Combine(fakeRepo, "skills", folder)); File.WriteAllText(Path.Combine(fakeRepo, "skills", folder, "skill.json"), text); }
        // The CABA manifest text with only its pack id replaced (the one "id": "<pack id>" member), so it stays under the skill size cap.
        string WithId(string id) => cabaText.Replace("\"id\": \"" + PackId + "\"", "\"id\": \"" + id + "\"", StringComparison.Ordinal);
        Pack("zz.test.caba-copy", WithId("zz.test.caba-copy"));
        Pack("zz.test.bad-json", "{");
        Pack("zz.test.mismatch", WithId("zz.test.other"));
        Pack(PackId, cabaText);
        var fakeDll = CopyCli(Path.Combine(fakeRepo, "cli"));

        // P7-12 review SF1: a stray skills/ beside a binary folder that is not a package (tests/out next to the C# test
        // project tests/skills, no package markers) never takes over: the repository's skills/ is read.
        Directory.CreateDirectory(Path.Combine(fakeRepo, "tests", "skills"));
        var strayDll = CopyCli(Path.Combine(fakeRepo, "tests", "out"));
        var strayOut = Output();
        var stray = CliAt(strayDll, "generate-prototypes", twoBedRequest, strayOut);
        Check(stray.Exit == 0 && File.Exists(strayOut) && File.ReadAllBytes(strayOut).SequenceEqual(written1) && stray.Out == run1.Out,
            "a CLI in <repo>/tests/out beside an empty <repo>/tests/skills reads <repo>/skills (no stray package root): the repository run's bytes and stdout", D(stray));

        // P7-12 review SF2: the package root is checked before the repository walk. <repo>/dist is a package (cli/, skills/,
        // VERSION, manifest.json) whose zz.test.mismatch is valid where the repository's names another id, and whose
        // zz.test.package-only is absent from the repository.
        var dist = Path.Combine(fakeRepo, "dist");
        var distDll = CopyCli(Path.Combine(dist, "cli"));
        File.WriteAllText(Path.Combine(dist, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(dist, "manifest.json"), "{}\n");
        void DistPack(string id) { Directory.CreateDirectory(Path.Combine(dist, "skills", id)); File.WriteAllText(Path.Combine(dist, "skills", id, "skill.json"), WithId(id)); }
        DistPack("zz.test.mismatch");
        DistPack("zz.test.package-only");
        foreach (var distId in new[] { "zz.test.mismatch", "zz.test.package-only" })
        {
            var distPackId = DerivedId(canonical.Replace("\"pack\":\"ar.caba.codigo-edificacion\"", "\"pack\":\"" + distId + "\"", StringComparison.Ordinal));
            var distLibrary = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 0), new PrototypePipelineOptions(SkillParser.Parse(WithId(distId)), distPackId));
            var distOut = Output();
            var distRun = CliAt(distDll, "generate-prototypes", Input("dist-" + distId + ".json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"pack\":\"" + distId + "\"}"), distOut);
            Check(distRun.Exit == 0 && File.Exists(distOut) && File.ReadAllBytes(distOut).SequenceEqual(Utf8(PrototypePipelineSerializer.Serialize(distLibrary))) && Str(Parse(distRun.Out), "project_id") == distPackId.ToString("D"),
                "a package inside a repository reads its own skills/" + distId + " before the repository's (output equal to the in-process run with the package's manifest)", D(distRun));
        }
        var copyPack = SkillParser.Parse(WithId("zz.test.caba-copy"));
        var copyCanonical = canonical.Replace("\"pack\":\"ar.caba.codigo-edificacion\"", "\"pack\":\"zz.test.caba-copy\"", StringComparison.Ordinal);
        var copyId = DerivedId(copyCanonical);
        var copyLibrary = PrototypePipeline.Run(new PrototypeRequest(ApartmentTypology.OneBedroom, new PrototypeLot(7, 12), 2, 0), new PrototypePipelineOptions(copyPack, copyId));
        var copyOut = Output();
        var run10 = CliAt(fakeDll, "generate-prototypes", Input("one-bed-copy-pack.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"pack\":\"zz.test.caba-copy\"}"), copyOut);
        Check(copyId != derivedId && run10.Exit == 0 && File.Exists(copyOut) && File.ReadAllBytes(copyOut).SequenceEqual(Utf8(PrototypePipelineSerializer.Serialize(copyLibrary)))
            && Str(Parse(run10.Out), "project_id") == copyId.ToString("D"),
            "a non-default pack from the repository's skills/: derived id covers the pack id; output equal to the in-process run with that manifest", D(run10));
        var o5 = Output();
        var badPack = CliAt(fakeDll, "generate-prototypes", Input("bad-pack.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"pack\":\"zz.test.bad-json\"}"), o5);
        Check(badPack.Exit == 1 && Str(Parse(badPack.Out), "code") == "generate_prototypes_pack_invalid" && !File.Exists(o5), "a malformed pack manifest -> generate_prototypes_pack_invalid, no file", D(badPack));
        var o6 = Output();
        var mismatch = CliAt(fakeDll, "generate-prototypes", Input("mismatch-pack.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"pack\":\"zz.test.mismatch\"}"), o6);
        Check(mismatch.Exit == 1 && Str(Parse(mismatch.Out), "code") == "generate_prototypes_pack_invalid" && Str(Parse(mismatch.Out), "detail") == "id" && !File.Exists(o6),
            "a manifest naming another pack id -> generate_prototypes_pack_invalid (detail id), no file", D(mismatch));

        // ---- never-overwrite ----
        var existing = Output();
        var sentinel = Utf8("{\"sentinel\":true}");
        File.WriteAllBytes(existing, sentinel);
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(existing, stamp);
        var overwrite = Cli("generate-prototypes", twoBedRequest, existing);
        Check(overwrite.Exit == 1 && Str(Parse(overwrite.Out), "code") == "output_exists" && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
            "an existing output is refused (output_exists) and left byte- and mtime-identical", D(overwrite));
        var directoryOut = Path.Combine(scratch, "dir-out.json");
        Directory.CreateDirectory(directoryOut);
        var dirResult = Cli("generate-prototypes", twoBedRequest, directoryOut);
        Check(dirResult.Exit == 1 && Str(Parse(dirResult.Out), "code") == "output_exists" && Directory.GetFileSystemEntries(directoryOut).Length == 0,
            "a directory at the output path is refused (output_exists)", D(dirResult));
        ExpectFailure("an output with another extension", "output_extension_unsupported", Path.Combine(scratch, "out.txt"), "generate-prototypes", twoBedRequest, Path.Combine(scratch, "out.txt"));
        var noDir = Path.Combine(scratch, "missing-directory", "out.json");
        ExpectFailure("an output in a missing directory", "output_directory_not_found", noDir, "generate-prototypes", twoBedRequest, noDir);

        var linkOut = Path.Combine(scratch, "link-out.json");
        var linkTarget = Path.Combine(scratch, "link-target.json");
        var linked = false;
        try { File.CreateSymbolicLink(linkOut, linkTarget); linked = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Console.WriteLine("SKIP generate-prototypes: output_link_rejected: " + error.GetType().Name); }
        if (linked)
        {
            var linkResult = Cli("generate-prototypes", twoBedRequest, linkOut);
            Check(linkResult.Exit == 1 && Str(Parse(linkResult.Out), "code") == "output_link_rejected" && !File.Exists(linkTarget) && new FileInfo(linkOut).LinkTarget is not null,
                "a symbolic link at the output path is refused (output_link_rejected) and nothing is written through it", D(linkResult));
        }

        // ---- bounded and malformed requests ----
        var overCapPath = Path.Combine(scratch, "over-cap.json");
        File.WriteAllBytes(overCapPath, overCap);
        var o1 = Output(); ExpectFailure("an 8 MiB + 1 request", "input_too_large", o1, "generate-prototypes", overCapPath, o1);
        var o2 = Output(); ExpectFailure("a missing request", "input_not_found", o2, "generate-prototypes", Path.Combine(scratch, "no-such-request.json"), o2);
        var o3 = Output(); ExpectFailure("a request directory", "input_not_regular_file", o3, "generate-prototypes", scratch, o3);
        foreach (var (label, text) in new[]
        {
            ("malformed JSON", "{\"typology\":2,"),
            ("a non-object request", "[1,2]"),
            ("an unknown member", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"bedrooms\":2}"),
            ("a duplicate member", "{\"typology\":2,\"typology\":3,\"lot\":{\"width_m\":8.66,\"depth_m\":20}}"),
            ("a missing typology", "{\"lot\":{\"width_m\":8.66,\"depth_m\":20}}"),
            ("a missing lot", "{\"typology\":2}"),
            ("a string typology", "{\"typology\":\"two\",\"lot\":{\"width_m\":8.66,\"depth_m\":20}}"),
            ("a fractional alternative count", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2.5}"),
            ("a non-finite lot width", "{\"typology\":2,\"lot\":{\"width_m\":1e400,\"depth_m\":20}}"),
            ("an unknown lot member", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20,\"height_m\":3}}"),
            ("a malformed project id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"project_id\":\"not-a-guid\"}"),
            ("a malformed pack id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"pack\":\"../skills\"}"),
            ("a non-boolean snap", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"snap\":1}"),
            ("an unknown weight", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"weights\":{\"beauty\":1}}"),
            ("a fixed room without rect", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"fixed_rooms\":[{\"room_id\":\"living\"}]}"),
            ("a lone surrogate pack", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"pack\":\"\\ud800\"}"),
            ("a lone surrogate project id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"project_id\":\"\\ud800\"}"),
            ("a lone surrogate room id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"fixed_rooms\":[{\"room_id\":\"\\udc00\",\"x\":0,\"y\":0,\"width\":3,\"depth\":3}]}"),
            ("a lone surrogate wall id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"fixed_walls\":[{\"id\":\"a\\ud800\",\"x0\":0,\"y0\":0,\"x1\":1,\"y1\":0}]}"),
            ("a lone surrogate member name", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"\\ud800\":1}"),
        })
        {
            var output = Output();
            ExpectFailure(label, "generate_prototypes_request_invalid", output, "generate-prototypes", Input("bad.json", text), output);
        }
        var o4 = Output();
        ExpectFailure("an unknown pack id", "generate_prototypes_pack_unavailable", o4, "generate-prototypes",
            Input("unknown-pack.json", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"pack\":\"xx.no-such.pack\"}"), o4);

        // ---- pipeline refusal: the library's refusals, typed, no file ----
        void ExpectRefusal(string label, string text, PrototypeRequest request, PrototypePipelineOptions options)
        {
            var library = PrototypePipeline.Run(request, options);
            var envelope = JsonSerializer.Serialize(new { status = "failed", code = "generate_prototypes_refused", refusals = library.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) });
            var output = Output();
            var result = Cli("generate-prototypes", Input("refused.json", text), output);
            Check(library.Status == PrototypePipelineStatus.Refused && library.Refusals.Count > 0 && result.Exit == 1 && result.Out == envelope && !File.Exists(output),
                label + ": refused with the library's refusals exactly, exit 1, no output file", D(result) + " expected " + envelope);
        }
        ExpectRefusal("a negative lot width", "{\"typology\":2,\"lot\":{\"width_m\":-1,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}",
            new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(-1, 20), 2, 0), new PrototypePipelineOptions(caba, explicitId));
        ExpectRefusal("a negative revision", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"revision\":-1,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}",
            new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 2, 0), new PrototypePipelineOptions(caba, explicitId, -1));
        ExpectRefusal("an unsupported typology", "{\"typology\":4,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}",
            new PrototypeRequest((ApartmentTypology)4, new PrototypeLot(8.66, 20), 2, 0), new PrototypePipelineOptions(caba, explicitId));
        ExpectRefusal("an all-zero project id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"00000000-0000-0000-0000-000000000000\"}",
            new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 2, 0), new PrototypePipelineOptions(caba, Guid.Empty));
        ExpectRefusal("a non-ASCII unknown approved room id", "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\","
            + "\"fixed_rooms\":[{\"room_id\":\"sal\u00F3n\",\"x\":0,\"y\":0,\"width\":3,\"depth\":3}]}",
            new PrototypeRequest(ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20), 2, 0, new[] { new FixedRoom("sal\u00F3n", new PlanRect(0, 0, 3, 3)) }), new PrototypePipelineOptions(caba, explicitId));

        UnguardedStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
        PostWriteStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
    }

    // ---- 7. P7-26: a non-refusal exception after GltfExporter.WriteNew succeeded leaves no output file ----
    // GenerateCommand.Run of the real Release CLI assembly (reflection, so a missing seam is an assertion failure), in
    // process, with valid requests and the P7-26 test seam PostWriteStageProbe (null in production; Execute calls it once,
    // right after GltfExporter.WriteNew returned) throwing: the probe sees the just-written file (for the explicit-id
    // request the in-process pipeline's exact bytes, so the exception really is post-write), then stdout is
    // IntentPlan.Internal's typed {"status":"failed","code":"generate_prototypes_internal_error"} line, stderr its
    // diagnostic once, exit 1, nothing escapes Run, and the written file is gone. A pre-existing output with the seam set is
    // still output_exists before any write: the probe never runs and the file is byte- and mtime-identical. Every refusal
    // sampled with the seam set never reaches the probe and prints the real process's bytes; with the seam unset the
    // in-process Run is the in-process pipeline's file and the real process's stdout. Static: PostWriteStageProbe is
    // declared once, invoked once inside Execute inside a try opened after GltfExporter.WriteNew( and before the SHA-256,
    // never assigned under interfaces/, its try brace-matched and followed by the deleting catch (with self-tests). Named apart from GenerateTypologyCommand's P3-25 PostWriteProbe,
    // whose static pin counts that name over interfaces/.
    static void PostWriteStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-26 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var nl = Environment.NewLine;
        static (Exception? Thrown, string Out, string Err, int Exit) Quiet(Func<Exception?> run)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(output);
                Console.SetError(error);
                Environment.ExitCode = 0;
                var thrown = run();
                return (thrown, output.ToString(), error.ToString(), Environment.ExitCode);
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                Environment.ExitCode = previousExit;
            }
        }
        static string Q((Exception? Thrown, string Out, string Err, int Exit) q) => "out [" + q.Out + "] err [" + q.Err + "] exit " + q.Exit.ToString(CultureInfo.InvariantCulture)
            + (q.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : "");
        static (int Exit, string Out, string Err) AsProcess((Exception? Thrown, string Out, string Err, int Exit) q)
        {
            var text = q.Out;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (q.Exit, text, q.Err.ReplaceLineEndings(" ").Trim());
        }
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var inputs = Path.Combine(scratch, "p7-26-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            const string good = "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}";
            var request = Input("request.json", good);
            var derivedRequest = Input("derived.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2}");
            var pinned = Hex(expected);

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var postWrite = type?.GetProperty("PostWriteStageProbe", Any);
            var unguarded = type?.GetProperty("UnguardedStageProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p) => p is not null && p.PropertyType == typeof(Action) && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite), "Run(string[]) and the settable static Action seam PostWriteStageProbe found in the Release CLI",
                (run is null ? "<missing Run> " : "") + (postWrite?.PropertyType.FullName ?? "<missing PostWriteStageProbe>"));
            if (run is null || postWrite is null || !Seam(postWrite)) return;
            Check(Seam(unguarded) && Seam(guarded), "the P3-20 / P7-23 seams GuardedWorkProbe / UnguardedStageProbe found in the Release CLI (read by AllNull below)",
                (unguarded?.PropertyType.FullName ?? "<missing UnguardedStageProbe>") + " " + (guarded?.PropertyType.FullName ?? "<missing GuardedWorkProbe>"));
            if (unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo probe = postWrite;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[]? args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            bool AllNull() => probe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-prototypes seams (PostWriteStageProbe, UnguardedStageProbe, GuardedWorkProbe) are null unless a test sets them");

            var failure = new InvalidOperationException("boom generate-prototypes P7-26 post-write");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == "generate-prototypes internal error: System.InvalidOperationException: boom generate-prototypes P7-26 post-write",
                "fixture: IntentPlan.Internal(\"generate-prototypes\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            // Not Program's generic {"status":"failed","code":<message>} line: the forced stdout below never contains the message.

            // A forced post-write exception: the probe records what was on disk when it ran, then throws.
            void ExpectRemoved(string label, string requestPath, string? pin)
            {
                var output = Output();
                var calls = 0;
                byte[]? seen = null;
                var forced = Quiet(() =>
                {
                    probe.SetValue(null, (Action)(() =>
                    {
                        calls++;
                        seen = File.Exists(output) ? File.ReadAllBytes(output) : null;
                        throw failure;
                    }));
                    try { return Invoke(new[] { "generate-prototypes", requestPath, output }); }
                    finally { probe.SetValue(null, null); }
                });
                Check(AllNull(), label + ": PostWriteStageProbe is reset to null after the forced run");
                Check(calls == 1 && seen is { Length: > 0 } && (pin is null || Hex(seen) == pin),
                    label + ": the probe ran once, after GltfExporter.WriteNew wrote the file" + (pin is null ? "" : " (the in-process pipeline's bytes, " + pin[..8] + "...)"),
                    "calls " + calls.ToString(CultureInfo.InvariantCulture) + " seen " + (seen is null ? "<no file>" : Hex(seen)));
                Check(forced.Thrown is null, label + ": the post-write exception does not escape Run to Program's generic catch", Q(forced));
                Check(forced.Out == envelope.Line + nl && !forced.Out.Contains(failure.Message, StringComparison.Ordinal) && !forced.Out.Contains("succeeded", StringComparison.Ordinal),
                    label + ": stdout is only the typed generate_prototypes_internal_error line (no success line, not the exception message)", Q(forced));
                Check(forced.Err == envelope.Diagnostic + nl, label + ": stderr is the IntentPlan diagnostic exactly once", Q(forced));
                Check(forced.Exit == 1 && !File.Exists(output) && !Directory.Exists(output), label + ": exit 1 and the just-written output file is deleted",
                    Q(forced) + (File.Exists(output) ? " output left (" + new FileInfo(output).Length.ToString(CultureInfo.InvariantCulture) + " bytes)" : ""));
            }
            ExpectRemoved("explicit project id run", request, pinned);
            ExpectRemoved("derived project id run", derivedRequest, null);

            // A pre-existing output is never touched: output_exists refuses before any write, so the probe never runs.
            var existing = Output();
            var sentinel = new UTF8Encoding(false).GetBytes("{\"sentinel\":\"p7-26\"}");
            File.WriteAllBytes(existing, sentinel);
            var stamp = new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(existing, stamp);
            var existingCalls = 0;
            var exists = Quiet(() =>
            {
                probe.SetValue(null, (Action)(() => { existingCalls++; throw failure; }));
                try { return Invoke(new[] { "generate-prototypes", request, existing }); }
                finally { probe.SetValue(null, null); }
            });
            Check(exists.Thrown is null && exists.Exit == 1 && exists.Out == "{\"status\":\"failed\",\"code\":\"output_exists\"}" + nl && exists.Err.Length == 0 && existingCalls == 0
                && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
                "with the seam set, an existing output is output_exists before any write (probe not run) and left byte- and mtime-identical", Q(exists) + " calls " + existingCalls.ToString(CultureInfo.InvariantCulture));
            Check(AllNull(), "PostWriteStageProbe is null after the existing-output run");

            // Refusals (every one before the write) never reach the probe and print the real process's bytes.
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-prototypes", request }),
                ("output extension", output => new[] { "generate-prototypes", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-prototypes", request, existing }),
                ("missing request", output => new[] { "generate-prototypes", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-prototypes", Input("json.json", "{"), output }),
                ("unknown member", output => new[] { "generate-prototypes", Input("member.json", good[..^1] + ",\"bedrooms\":2}"), output }),
                ("unknown pack", output => new[] { "generate-prototypes", Input("pack.json", good[..^1] + ",\"pack\":\"xx.no-such.pack\"}"), output }),
                ("pipeline refusal", output => new[] { "generate-prototypes", Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal)), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var a = Output();
                var b = Output();
                var refusalCalls = 0;
                var mine = Quiet(() =>
                {
                    probe.SetValue(null, (Action)(() => { refusalCalls++; throw failure; }));
                    try { return Invoke(argsOf(a)); }
                    finally { probe.SetValue(null, null); }
                });
                var theirs = cli(argsOf(b));
                Check(mine.Thrown is null && refusalCalls == 0 && mine.Exit == 1 && theirs.Exit == 1 && AsProcess(mine) == theirs && mine.Err.Length == 0
                    && !File.Exists(a) && !File.Exists(b) && !File.Exists(a + ".txt") && !File.Exists(b + ".txt")
                    && mine.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !mine.Out.Contains("internal_error", StringComparison.Ordinal),
                    "with the seam set, the " + label + " refusal never reaches the probe and equals the real process bytes", Q(mine) + " calls " + refusalCalls.ToString(CultureInfo.InvariantCulture) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(AllNull() && File.ReadAllBytes(existing).SequenceEqual(sentinel), "the seams are null after the refusal sample and the existing output is untouched");

            // Seams unset: the in-process Run is the real process (the in-process pipeline's file, same stdout, nothing on stderr).
            foreach (var (label, requestPath, pin) in new (string Label, string Request, string? Pin)[] { ("explicit project id", request, pinned), ("derived project id", derivedRequest, null) })
            {
                var inOut = Output();
                var inProcess = Quiet(() => Invoke(new[] { "generate-prototypes", requestPath, inOut }));
                var processOut = Output();
                var process = cli(new[] { "generate-prototypes", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(inProcess.Thrown is null && inProcess.Exit == 0 && inProcess.Err.Length == 0 && inBytes.Length > 0 && (pin is null || Hex(inBytes) == pin) && inBytes.SequenceEqual(processBytes)
                    && process.Exit == 0 && AsProcess(inProcess) == process && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal),
                    "seams unset: the " + label + " in-process Run writes the real process's file" + (pin is null ? "" : " (the in-process pipeline's bytes)") + " and stdout",
                    Q(inProcess) + " vs process " + process.Out + " " + process.Err);
            }

            // Static: the seam over interfaces/**/*.cs, comments and literals stripped, plus self-tests of the checker.
            var interfaces = Path.Combine(root, "interfaces");
            var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file))).ToArray();
            var seam = PostWriteStageSeamProblems(sources);
            Check(seam.Count == 0, "interfaces/**/*.cs names PostWriteStageProbe only in its declaration and one ?.Invoke() inside Execute's body, in a try { opened after GltfExporter.WriteNew( that holds the later SHA256.HashData( and the only post-write Console.WriteLine(, directly followed by catch (Exception) { File.Delete(output) ... throw; }, never assigned ["
                + string.Join("; ", seam) + "]");
            const string Path26 = "interfaces/cli/GenerateCommand.cs";
            const string Decl = "internal static Action? PostWriteStageProbe { get; set; }";
            string Command(string executeBody, string extra = "") => "internal static class GenerateCommand { static void Execute(string[] args) { " + executeBody + " } " + Decl + " " + extra + " }";
            const string Remove = " catch (Exception) { try { File.Delete(output); } catch (Exception removal) when (removal is IOException) { } throw; }";
            const string GoodBody = "try { GltfExporter.WriteNew(bytes, output); } catch (IOException) { throw; } try { PostWriteStageProbe?.Invoke(); var sha256 = SHA256.HashData(bytes); Console.WriteLine(new { sha256 }); }" + Remove;
            Check(PostWriteStageSeamProblems(new[] { (Path26, Command(GoodBody)) }).Count == 0
                && PostWriteStageSeamProblems(new[] { (Path26, Command("GltfExporter . WriteNew ( bytes, output );\n try\n { PostWriteStageProbe ?. Invoke ( ) ; // PostWriteStageProbe = null; }\n var s = \"PostWriteStageProbe = x } {\"; SHA256 . HashData ( bytes ); Console . WriteLine ( s ); }\n catch ( Exception )\n { File . Delete ( output ) ; throw ; }")) }).Count == 0,
                "self-test: the post-write seam checker accepts the real shape at any spacing, ignoring comments and string literals (braces in them too)");
            var evasions = new (string Label, (string, string)[] Files)[]
            {
                ("an assignment in the command", new[] { (Path26, Command(GoodBody, "static void Set() { PostWriteStageProbe = () => throw null!; }")) }),
                ("an assignment in another interfaces file", new[] { (Path26, Command(GoodBody)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateCommand.PostWriteStageProbe = null; } }") }),
                ("a compound assignment", new[] { (Path26, Command(GoodBody)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateCommand.PostWriteStageProbe ??= null; } }") }),
                ("the invoke before WriteNew(", new[] { (Path26, Command("PostWriteStageProbe?.Invoke(); GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes);")) }),
                ("the invoke before the deleting try", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); PostWriteStageProbe?.Invoke(); try { SHA256.HashData(bytes); } catch { throw; }")) }),
                ("the invoke after SHA256.HashData(", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes); PostWriteStageProbe?.Invoke();")) }),
                ("the invoke outside Execute", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes);", "static void Other() { PostWriteStageProbe?.Invoke(); }")) }),
                ("the invoke missing", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes);")) }),
                ("a second invoke", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); PostWriteStageProbe?.Invoke(); PostWriteStageProbe?.Invoke(); SHA256.HashData(bytes);")) }),
                ("the declaration missing", new[] { (Path26, "internal static class GenerateCommand { static void Execute(string[] args) { " + GoodBody + " } }") }),
                ("the command file missing", new[] { ("interfaces/cli/Other.cs", Command(GoodBody)) }),
                ("a closed try before the invoke", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); try { }" + Remove + " PostWriteStageProbe?.Invoke(); SHA256.HashData(bytes); Console.WriteLine(x);")) }),
                ("the success WriteLine outside the try", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); try { PostWriteStageProbe?.Invoke(); var sha256 = SHA256.HashData(bytes); }" + Remove + " Console.WriteLine(sha256);")) }),
                ("a second success WriteLine after the catch", new[] { (Path26, Command(GoodBody + " Console.WriteLine(x);")) }),
                ("SHA256.HashData( outside the try", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); try { PostWriteStageProbe?.Invoke(); Console.WriteLine(x); }" + Remove + " SHA256.HashData(bytes);")) }),
                ("a catch without File.Delete", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); try { PostWriteStageProbe?.Invoke(); SHA256.HashData(bytes); Console.WriteLine(x); } catch (Exception) { throw; }")) }),
                ("a catch without throw;", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); try { PostWriteStageProbe?.Invoke(); SHA256.HashData(bytes); Console.WriteLine(x); } catch (Exception) { File.Delete(output); }")) }),
                ("a finally instead of catch (Exception)", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); try { PostWriteStageProbe?.Invoke(); SHA256.HashData(bytes); Console.WriteLine(x); } finally { File.Delete(output); throw; }")) }),
                ("the invoke in a method after Execute with a later SHA256", new[] { (Path26, Command("GltfExporter.WriteNew(bytes, output); try { SHA256.HashData(bytes); Console.WriteLine(x); }" + Remove,
                    "static void Other() { try { PostWriteStageProbe?.Invoke(); SHA256.HashData(bytes); Console.WriteLine(x); }" + Remove + " }")) }),
            };
            foreach (var (label, files) in evasions)
                Check(PostWriteStageSeamProblems(files).Count > 0, "self-test: the post-write seam checker flags " + label);
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // P7-26: over (relative path, source) pairs under interfaces/, comments stripped and string literals blanked:
    // PostWriteStageProbe is named exactly twice in total, both in interfaces/cli/GenerateCommand.cs: its declaration and
    // one PostWriteStageProbe?.Invoke(); no assignment anywhere. Brace-matched: the invoke is inside Execute's body, after
    // its GltfExporter.WriteNew(, inside a try { opened after WriteNew( that also holds a later SHA256.HashData( and the
    // Console.WriteLine( (no Console.WriteLine( in Execute after WriteNew( outside it), and that try is directly followed
    // by catch (Exception) { ... } whose body holds File.Delete(output) and throw;.
    static List<string> PostWriteStageSeamProblems(IEnumerable<(string Name, string Source)> files)
    {
        static int Close(string code, int open)
        {
            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }
        static System.Text.RegularExpressions.Match Find(string code, string pattern, int from, int to)
        {
            var match = new System.Text.RegularExpressions.Regex(pattern).Match(code, from, to - from);
            return match;
        }
        var problems = new List<string>();
        var total = 0;
        var sawCommand = false;
        foreach (var (name, source) in files)
        {
            var code = ApplyIntentTests.CodeOf(source);
            total += System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteStageProbe\b").Count;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bPostWriteStageProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": PostWriteStageProbe assigned");
            if (name != "interfaces/cli/GenerateCommand.cs") continue;
            sawCommand = true;
            var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+static\s+Action\s*\?\s*PostWriteStageProbe\s*\{\s*get\s*;\s*set\s*;\s*\}").Count;
            if (declarations != 1) problems.Add(name + ": the declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteStageProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;");
            var execute = System.Text.RegularExpressions.Regex.Match(code, @"\bstatic\s+void\s+Execute\s*\([^)]*\)\s*\{");
            if (invokes.Count != 1 || !execute.Success) { problems.Add(name + ": " + invokes.Count.ToString(CultureInfo.InvariantCulture) + " PostWriteStageProbe?.Invoke() calls, Execute " + (execute.Success ? "found" : "missing")); continue; }
            var open = execute.Index + execute.Length - 1;
            var close = Close(code, open);
            if (close < 0) { problems.Add(name + ": Execute's body is not closed"); continue; }
            var at = invokes[0].Index;
            var write = Find(code, @"\bGltfExporter\s*\.\s*WriteNew\s*\(", open, close);
            if (!write.Success || at < write.Index || at > close) { problems.Add(name + ": PostWriteStageProbe?.Invoke() not inside Execute's body after its GltfExporter.WriteNew("); continue; }
            // The innermost try { opened after WriteNew( whose block holds the invoke.
            int tryOpen = -1, tryClose = -1;
            foreach (System.Text.RegularExpressions.Match candidate in new System.Text.RegularExpressions.Regex(@"\btry\s*\{").Matches(code[..at], write.Index))
            {
                var brace = candidate.Index + candidate.Length - 1;
                var end = Close(code, brace);
                if (end > at && end < close) { tryOpen = brace; tryClose = end; }
            }
            if (tryOpen < 0) { problems.Add(name + ": PostWriteStageProbe?.Invoke() not inside a try { opened after GltfExporter.WriteNew("); continue; }
            if (!Find(code, @"\bSHA256\s*\.\s*HashData\s*\(", at, tryClose).Success) problems.Add(name + ": no SHA256.HashData( after the invoke inside its try");
            if (!Find(code, @"\bConsole\s*\.\s*WriteLine\s*\(", tryOpen, tryClose).Success) problems.Add(name + ": no Console.WriteLine( inside the invoke's try");
            if (Find(code, @"\bConsole\s*\.\s*WriteLine\s*\(", write.Index, tryOpen).Success || Find(code, @"\bConsole\s*\.\s*WriteLine\s*\(", tryClose, close).Success)
                problems.Add(name + ": a Console.WriteLine( in Execute after GltfExporter.WriteNew( outside the invoke's try");
            var handler = new System.Text.RegularExpressions.Regex(@"\G\s*catch\s*\(\s*Exception\s*\)\s*\{").Match(code, tryClose + 1);
            if (!handler.Success) { problems.Add(name + ": the invoke's try is not directly followed by catch (Exception) {"); continue; }
            var handlerOpen = handler.Index + handler.Length - 1;
            var handlerClose = Close(code, handlerOpen);
            if (handlerClose < 0 || handlerClose > close) { problems.Add(name + ": the catch (Exception) body is not closed inside Execute"); continue; }
            if (!Find(code, @"\bFile\s*\.\s*Delete\s*\(\s*output\s*\)", handlerOpen, handlerClose).Success) problems.Add(name + ": the catch (Exception) body has no File.Delete(output)");
            if (!Find(code, @"\bthrow\s*;", handlerOpen, handlerClose).Success) problems.Add(name + ": the catch (Exception) body has no throw;");
        }
        if (!sawCommand) problems.Add("interfaces/cli/GenerateCommand.cs not found");
        if (total != 2) problems.Add("PostWriteStageProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // ---- P7-25: a non-refusal exception outside the guarded pipeline block on the CLI path is the typed internal error ----
    // GenerateCommand.Run of the real Release CLI assembly (loaded from cliDll, members by reflection, so a missing seam is
    // an assertion failure), in process, with a valid request and the P7-23 test seam UnguardedStageProbe (null in
    // production; Generate calls it before Parse and the pack load) throwing: stdout is IntentPlan.Internal's
    // {"status":"failed","code":"generate_prototypes_internal_error"} line, stderr its diagnostic once, exit 1, nothing
    // escapes Run (so Program's generic catch, whose line would carry the exception message as the code, is not reached)
    // and no output file. The seams are null before and reset in a finally; afterwards the in-process Run writes the
    // in-process pipeline's bytes and the real process's file and stdout, and a sample of refusals (usage, output path,
    // request, pack, pipeline) prints the real process's bytes. Static: Run is try Execute / catch (Refusal) Fail / one
    // catch-all through IntentPlan.Internal("generate-prototypes", error).Print(). The seam is only read here (by
    // reflection), so the tests/mcp section 8f pin over interfaces/ is unaffected.
    static void UnguardedStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-25 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var nl = Environment.NewLine;
        static (Exception? Thrown, string Out, string Err, int Exit) Quiet(Func<Exception?> run)
        {
            var previousOut = Console.Out;
            var previousError = Console.Error;
            var previousExit = Environment.ExitCode;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(output);
                Console.SetError(error);
                Environment.ExitCode = 0;
                var thrown = run();
                return (thrown, output.ToString(), error.ToString(), Environment.ExitCode);
            }
            finally
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                Environment.ExitCode = previousExit;
            }
        }
        static string Q((Exception? Thrown, string Out, string Err, int Exit) q) => "out [" + q.Out + "] err [" + q.Err + "] exit " + q.Exit.ToString(CultureInfo.InvariantCulture)
            + (q.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : "");
        // The real process's form: (exit, stdout without its final newline, stderr with line ends as spaces, trimmed).
        static (int Exit, string Out, string Err) AsProcess((Exception? Thrown, string Out, string Err, int Exit) q)
        {
            var text = q.Out;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (q.Exit, text, q.Err.ReplaceLineEndings(" ").Trim());
        }

        var inputs = Path.Combine(scratch, "p7-25-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            const string good = "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}";
            var request = Input("request.json", good);

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var unguarded = type?.GetProperty("UnguardedStageProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p) => p is not null && p.PropertyType == typeof(Action) && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(unguarded) && Seam(guarded), "Run(string[]) and the settable static Action seams UnguardedStageProbe / GuardedWorkProbe found in the Release CLI",
                (run is null ? "<missing Run> " : "") + (unguarded?.PropertyType.FullName ?? "<missing UnguardedStageProbe>") + " " + (guarded?.PropertyType.FullName ?? "<missing GuardedWorkProbe>"));
            if (run is null || unguarded is null || guarded is null || !Seam(unguarded) || !Seam(guarded)) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo probe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[]? args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            Check(probe.GetValue(null) is null && guardedProbe.GetValue(null) is null, "the generate-prototypes seams are null unless a test sets them");

            // The typed bytes, and the line Program's generic catch would print for the escaped exception (the old behaviour).
            var failure = new InvalidOperationException("boom generate-prototypes P7-25 unguarded");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == "generate-prototypes internal error: System.InvalidOperationException: boom generate-prototypes P7-25 unguarded",
                "fixture: IntentPlan.Internal(\"generate-prototypes\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            var generic = JsonSerializer.Serialize(new { status = "failed", code = failure.Message });

            var forcedOut = Output();
            var forced = Quiet(() =>
            {
                probe.SetValue(null, (Action)(() => throw failure));
                try { return Invoke(new[] { "generate-prototypes", request, forcedOut }); }
                finally { probe.SetValue(null, null); }
            });
            Check(probe.GetValue(null) is null && guardedProbe.GetValue(null) is null, "UnguardedStageProbe is reset to null after the forced run");
            Check(forced.Thrown is null, "a forced exception before the guarded block does not escape Run to Program's generic catch", Q(forced));
            Check(forced.Out == envelope.Line + nl && forced.Out != generic + nl && !forced.Out.Contains(failure.Message, StringComparison.Ordinal),
                "the forced run prints the typed generate_prototypes_internal_error line (not the exception message)", Q(forced));
            Check(forced.Err == envelope.Diagnostic + nl, "the forced run writes the IntentPlan diagnostic to stderr exactly once", Q(forced));
            Check(forced.Exit == 1 && !File.Exists(forcedOut) && !Directory.Exists(forcedOut), "the forced run exits 1 and writes no output file", Q(forced) + (File.Exists(forcedOut) ? " output written" : ""));

            // A run with null args and the seams unset: Execute itself throws (args.Length, a NullReferenceException) before
            // Generate; Run's catch-all prints the typed line and its diagnostic once.
            var nullArgs = Quiet(() => Invoke(null));
            var nullEnvelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", new NullReferenceException());
            Check(nullArgs.Thrown is null && nullArgs.Out == nullEnvelope.Line + nl && nullArgs.Out == envelope.Line + nl && nullArgs.Err == nullEnvelope.Diagnostic + nl && nullArgs.Exit == 1,
                "a run with null args (seams unset) prints the typed generate_prototypes_internal_error line, the NullReferenceException diagnostic once, exit 1, nothing thrown", Q(nullArgs));
            Check(probe.GetValue(null) is null && guardedProbe.GetValue(null) is null, "the seams are still null after the null-args run");

            // Seams unset: the in-process Run is the in-process pipeline and the real process, for the success and a sample of refusals.
            var inOut = Output();
            var inProcess = Quiet(() => Invoke(new[] { "generate-prototypes", request, inOut }));
            var processOut = Output();
            var process = cli(new[] { "generate-prototypes", request, processOut });
            var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
            var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
            Check(inProcess.Thrown is null && inProcess.Exit == 0 && inProcess.Err.Length == 0 && inBytes.Length > 0 && inBytes.SequenceEqual(expected) && inBytes.SequenceEqual(processBytes)
                && process.Exit == 0 && AsProcess(inProcess) == process,
                "after the reset the in-process Run writes the in-process pipeline's bytes and the real process's file and stdout", Q(inProcess) + " vs process exit " + process.Exit.ToString(CultureInfo.InvariantCulture) + " " + process.Out + " " + process.Err);

            // The refusal sample compares the in-process Run with a real process of the same Release binary (same code on
            // both sides); that these bytes are unchanged by P7-25 rests on the literal pins of the sections above, which run
            // against the real process.
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-prototypes", request }),
                ("output extension", output => new[] { "generate-prototypes", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-prototypes", request, existing }),
                ("missing request", output => new[] { "generate-prototypes", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-prototypes", Input("json.json", "{"), output }),
                ("unknown member", output => new[] { "generate-prototypes", Input("member.json", good[..^1] + ",\"bedrooms\":2}"), output }),
                ("unknown pack", output => new[] { "generate-prototypes", Input("pack.json", good[..^1] + ",\"pack\":\"xx.no-such.pack\"}"), output }),
                ("pipeline refusal", output => new[] { "generate-prototypes", Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal)), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var a = Output();
                var b = Output();
                var mine = Quiet(() => Invoke(argsOf(a)));
                var theirs = cli(argsOf(b));
                Check(mine.Thrown is null && mine.Exit == 1 && theirs.Exit == 1 && AsProcess(mine) == theirs && mine.Err.Length == 0 && !File.Exists(a) && !File.Exists(b) && !File.Exists(a + ".txt") && !File.Exists(b + ".txt")
                    && mine.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !mine.Out.Contains("internal_error", StringComparison.Ordinal),
                    "seams unset: the " + label + " refusal of the in-process Run equals the real process bytes", Q(mine) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(File.ReadAllText(existing) == "keep", "the existing output is left untouched");

            // Static: Run's body, comments stripped, layout-independent.
            var source = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateCommand.cs")), blankStrings: false);
            Check(System.Text.RegularExpressions.Regex.IsMatch(source,
                    @"public\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)\s*\{\s*try\s*\{\s*Execute\s*\(\s*args\s*\)\s*;\s*\}\s*catch\s*\(\s*Refusal\s+refusal\s*\)\s*\{\s*Fail\s*\(\s*refusal\s*\.\s*Code\s*,\s*refusal\s*\.\s*Detail\s*\)\s*;\s*\}\s*"
                    + @"catch\s*\(\s*Exception\s+error\s*\)\s*\{\s*IntentPlan\s*\.\s*Internal\s*\(\s*""generate-prototypes""\s*,\s*error\s*\)\s*\.\s*Print\s*\(\s*\)\s*;\s*\}\s*\}"),
                "GenerateCommand.Run is try Execute / catch (Refusal) Fail / catch (Exception) IntentPlan.Internal(\"generate-prototypes\", error).Print()");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // The documented rule: the first 16 bytes of SHA-256(UTF-8 "bim-agentic-os/generate-prototypes/v1\n" + canonical
    // request), read big-endian as an RFC 9562 UUID with version 8 and the RFC variant.
    static Guid DerivedId(string canonical)
    {
        var hash = SHA256.HashData(new UTF8Encoding(false).GetBytes("bim-agentic-os/generate-prototypes/v1\n" + canonical));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
