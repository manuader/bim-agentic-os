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
                // plan-layouts --pack keeps its repository-only lookup (SkillPackLookup.Load without the package root).
                var briefPath = Input("package-brief.json", "{\"brief_version\":1,\"name\":\"Small studio\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
                    + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}],"
                    + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"bath\",\"kind\":\"required\"}]}");
                var planOut = Output();
                var plan = CliAt(packageDll, "plan-layouts", briefPath, planOut, "--alternatives", "1", "--pack", PackId);
                Check(plan.Exit == 1 && Str(Parse(plan.Out), "code") == "plan_layouts_pack_repository_not_found" && !File.Exists(planOut),
                    "plan-layouts --pack in the package layout is unchanged: plan_layouts_pack_repository_not_found, no file", D(plan));
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
        var fakeDll = CopyCli(Path.Combine(fakeRepo, "cli"));
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
