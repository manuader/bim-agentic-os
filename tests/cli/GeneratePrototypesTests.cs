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
//   8. P7-27: when that cleanup delete itself fails (forced in process through GenerateCommand.PostWriteDeleteProbe), the
//      typed line and exit 1 are unchanged, stderr adds one line naming the file left behind before the original
//      exception's diagnostic (once), and a delete exception never replaces the original (PostWriteDeleteStage).
//   9. P7-28: a stderr that throws while Run's catch-all writes the internal-error diagnostic (forced in process with a
//      rejecting TextWriter) still leaves the typed line on stdout, exit 1, nothing escaping Run, and the P7-26 / P7-27
//      cleanup unchanged (DiagnosticWriteStage).
//  10. P7-29: a stdout that throws while Run's catch-all writes the typed line (forced in process with a rejecting
//      TextWriter) leaves exit 1 with nothing escaping Run; the guarded block's own diagnostic write throwing is contained
//      (pinned bytes and a static pin of that write); success, refusal and section 7-9 bytes unchanged (OutputWriteStage).
//  11. P7-30: a stdout that throws while Fail writes a typed refusal (forced in process with a rejecting TextWriter) leaves
//      exit 1 with nothing escaping Run; a pipeline refusal whose stdout write throws is reported as the internal error
//      (pinned); success, refusal and section 7-10 bytes unchanged (RefusalWriteStage).
//  12. P7-32: a stdout that accepts part of a typed refusal, internal-error, success or pipeline-refusal line and then
//      throws leaves that truncated line (no newline) on stdout, exit 1, nothing escaping Run; a disposed stdout's attempts
//      are counted through a CountingWriter; success, refusal and section 7-11 bytes unchanged (PartialWriteStage).
//  13. P7-34: section 12's per-line budget also cut after the full line (a complete success JSON on stdout with exit 1 and
//      no file), CountingWriter's WriteLine() / Write(char[], int, int) self-tested, and a static pin (with mutation
//      self-tests) that Run never flushes or retries the typed line after a failed write (PartialWriteContractStage).
//  14. P7-36: PartialWriter's WriteLine() / Write(char[], int, int) self-tested, sections 12 and 13 on one ForcedRun helper,
//      and a static pin (with mutation self-tests) that Execute never retries or writes again the success or refusal line
//      (PartialWriterExecuteStage).
//  15. P7-38: mutation self-tests of section 14's one-copy ForcedRun pin (a second SetOut, a copied helper), the Execute
//      pin over every helper Execute reaches (a stdout write through a new private helper is rejected) and its tolerance of
//      a whitespace / trailing-comma reformat that still rejects section 14's mutants (WritePinReachStage).
//  16. P7-40: mutation self-tests of both section 14 pins through generic, qualified, method-group, delegate and
//      cross-class calls, under a block-scoped namespace, and of the success-only trailing-comma reformat (WritePinScopeStage).
//      P7-42: every section 15 / 16 mutant names the problem it is rejected for; delegate fields and properties armed
//      outside their declaration (static constructor, method, T.X =, +=) and invoked as X?.Invoke( or X( are followed;
//      uppercase property chains (Result.Items.Count()) are instances, not unresolved calls; nested generic and tuple type
//      arguments are calls; Execute's calls into OutputProblem.cs, BoundedInput.cs, SkillPackLookup.cs and IntentPlan.cs
//      are followed into those files. P7-44: those files (and GltfExporter.cs, so GltfExporter.WriteNew is followed) are
//      read from the root the stage receives; a Func / Action parameter call (BoundedInput.Stat's call, WriteNew's wrap)
//      is followed to the arguments its callers pass, any other parameter call is unresolved; a local or parameter that
//      shadows a field, and a tuple-typed local declaration, are not that field or its assignment. P7-46: a delegate
//      parameter's callers through this. / base. / instance receivers and a method group assigned to a local are followed
//      (other method-group uses rejected as gaps); Predicate / Comparison / Converter and declared delegate types are
//      followed, other called parameters named with their type; a local declared after the use does not shadow the field;
//      expression-bodied members' and lambdas' parameters are seen (a called lambda parameter is named unresolved).
//      P7-48: async / static lambda heads are lambdas and no lambda head is a declarator; a generic call's commas
//      (M<A, B>()) cut neither a lambda body nor an argument list; method-group alias chains (g = f) are followed and a
//      field alias used other than called is a gap; generic methods with where clauses expose their parameters; a lambda
//      or non-delegate parameter passed on where a delegate argument is followed is named unresolved.
//      P7-50: a where is a method head's clause only as a constraint (not a LINQ where, nor one inside an attribute or a
//      default value); a cast (List<A, B>)x and a pattern is Dictionary<A, B> d are one type-argument list, while a
//      comparison holding an operator or followed by a name is none (stated limit: a < b > (c) is taken as a generic
//      call, as C# itself parses it); a parameter passed on wrapped ((test), test!, a cast, a conditional or ??) is named;
//      a field alias's over-approximated name match is stated in its gap; a method group in a conditional or after ??
//      stays a named gap.
//      P7-52: a parenthesised operand followed by a binary operator ((test) ?? d, (test) + 1) is told from a cast by the
//      token after it, as C# does, so a parameter passed on that way is named, as is either side of a delegate
//      combination a + b (and the left of a - b); a nullable type's ? before > , ) ] (also past whitespace,
//      new List<int? >() ?? test) is no conditional; the case-pattern type-argument self-test uses a switch statement.
//      P7-54: a null-forgiving parenthesised operand ((test)! ?? d) is no cast, a ?[ whose ] is followed by : is a
//      conditional with a collection branch (c ?[x] : d; stated limit: only the span's first ?), an exponent literal's
//      sign (1e-5) is no subtraction; a < b, c > (d) is pinned as one argument and a Notifier parameter passed on as
//      (test) ?? d or (test)! ?? d is named.
//      P7-56: a ?[ collection branch inside a conditional's branch (c ? b ?[x] : y : d) is a nested conditional, an element
//      access there still is not (stated limit: a branch mixing both); (test)!(x) is a cast of !(x), as C# reads it; an
//      exponent's walk back crosses . and must give a whole decimal real prefix, so 1e5f - x, 0x_1e-5 and 1.E - x are
//      subtractions while 1e-5f and .5e-3 are one value.
//      P7-58: the mixed branch c ? a?[0] : b ?[x] : y : d is pinned as read (both ?[ nested, the reading that parses);
//      the nested count's ? is one named helper (ConditionalQuestion); an exponent prefix takes _ only between digits
//      (1_e-5 is a subtraction) and its sign only right after the e (1e - x, so 1e /*c*/ - x once comments are stripped,
//      is a subtraction); (test)!(x) as a null-forgiven call is a stated reach gap (C# reads a cast); the value and
//      exponent self-tests run through one table-driven helper.
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
        PostWriteDeleteStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
        DiagnosticWriteStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
        OutputWriteStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
        RefusalWriteStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
        PartialWriteStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
        PartialWriteContractStage(root, scratch, cliDll, expected, check, arguments => Cli(arguments));
        PartialWriterExecuteStage(root, check);
        WritePinReachStage(root, check);
        WritePinScopeStage(root, check);
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
            const string Path26 = "interfaces/cli/GenerateWrite.cs";
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
    // (P4-27: the path follows Execute into the CLI-only write part GenerateWrite.cs; the expectation is unchanged.)
    // PostWriteStageProbe is named exactly twice in total, both in interfaces/cli/GenerateWrite.cs: its declaration and
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
            if (name != "interfaces/cli/GenerateWrite.cs") continue;
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
        if (!sawCommand) problems.Add("interfaces/cli/GenerateWrite.cs not found");
        if (total != 2) problems.Add("PostWriteStageProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // ---- 8. P7-27: a failed post-write cleanup delete is reported on stderr; the original exception is still the one reported ----
    // GenerateCommand.Run of the real Release CLI assembly (reflection, so a missing seam is an assertion failure), in
    // process, with the P7-26 seam PostWriteStageProbe throwing the original exception and the P7-27 test seam
    // PostWriteDeleteProbe (Action<string>, null in production; the post-write catch calls it once with the output path
    // right before File.Delete(output)) throwing an IOException, an UnauthorizedAccessException or a non-IO exception: the
    // delete never runs, so the just-written file (the in-process pipeline's exact bytes for the explicit-id request) is
    // left on disk; stdout is still only the typed {"status":"failed","code":"generate_prototypes_internal_error"} line,
    // exit 1, nothing escapes Run, and stderr is the documented line
    //   generate-prototypes: could not delete partial output: <output path as given>
    // (GenerateCommand.PartialOutputLeft + path) followed by the IntentPlan diagnostic of the ORIGINAL exception exactly
    // once (the delete exception's type and message appear nowhere). A delete probe that does not throw leaves section 7's
    // bytes (file deleted, stderr only the diagnostic); with the delete probe set, success and the sampled refusals never
    // call it and equal the real process. Static: PostWriteDeleteProbe is declared once, never assigned under interfaces/,
    // invoked once inside Execute as the first statement of a try { PostWriteDeleteProbe?.Invoke(output); File.Delete(output); }
    // that is the first statement of the catch (Exception) following PostWriteStageProbe's try, directly followed by an
    // unfiltered catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }
    // without a throw, then throw; as the handler's last statement (brace-matched, with self-tests). Review fix: a stderr
    // that throws on the cleanup line (IOException) is swallowed too, so the original exception is still the one reported
    // and its diagnostic the only recorded stderr line. Not named PostWriteProbe (P3-25's pin).
    static void PostWriteDeleteStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-27 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string Left = "generate-prototypes: could not delete partial output: ";
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

        var inputs = Path.Combine(scratch, "p7-27-" + Guid.NewGuid().ToString("N"));
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
            var deleteSeam = type?.GetProperty("PostWriteDeleteProbe", Any);
            var unguarded = type?.GetProperty("UnguardedStageProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            var leftField = type?.GetField("PartialOutputLeft", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite, typeof(Action)) && Seam(deleteSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]), the settable static Action<string> seam PostWriteDeleteProbe and the Action seams PostWriteStageProbe / UnguardedStageProbe / GuardedWorkProbe found in the Release CLI",
                (run is null ? "<missing Run> " : "") + (deleteSeam?.PropertyType.FullName ?? "<missing PostWriteDeleteProbe>") + " " + (postWrite?.PropertyType.FullName ?? "<missing PostWriteStageProbe>"));
            if (run is null || postWrite is null || deleteSeam is null || unguarded is null || guarded is null || !Seam(deleteSeam, typeof(Action<string>)) || !Seam(postWrite, typeof(Action))) return;
            Check(leftField is { IsLiteral: true } && leftField.FieldType == typeof(string) && (string?)leftField.GetRawConstantValue() == Left,
                "GenerateCommand.PartialOutputLeft is the documented constant \"" + Left + "\"", leftField is { IsLiteral: true } ? "[" + leftField.GetRawConstantValue() + "]" : "<missing const>");
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo probe = postWrite;
            System.Reflection.PropertyInfo deleteProbe = deleteSeam;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[]? args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            bool AllNull() => probe.GetValue(null) is null && deleteProbe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-prototypes seams (PostWriteDeleteProbe, PostWriteStageProbe, UnguardedStageProbe, GuardedWorkProbe) are null unless a test sets them");
            (Exception? Thrown, string Out, string Err, int Exit) WithSeams(Action? stage, Action<string>? delete, string[] args) => Quiet(() =>
            {
                probe.SetValue(null, stage);
                deleteProbe.SetValue(null, delete);
                try { return Invoke(args); }
                finally { probe.SetValue(null, null); deleteProbe.SetValue(null, null); }
            });

            var failure = new InvalidOperationException("boom generate-prototypes P7-27 original post-write");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == "generate-prototypes internal error: System.InvalidOperationException: boom generate-prototypes P7-27 original post-write",
                "fixture: IntentPlan.Internal(\"generate-prototypes\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());

            // The cleanup delete forced to fail: the file stays, stderr names it once, the original exception is the one reported.
            void ExpectLeft(string label, string requestPath, string? pin, Exception removal)
            {
                var output = Output();
                var stageCalls = 0;
                var deletes = new List<string>();
                byte[]? seenAtDelete = null;
                var forced = WithSeams(() => { stageCalls++; throw failure; }, path =>
                {
                    deletes.Add(path);
                    seenAtDelete = File.Exists(output) ? File.ReadAllBytes(output) : null;
                    throw removal;
                }, new[] { "generate-prototypes", requestPath, output });
                Check(AllNull(), label + ": both seams are reset to null after the forced run");
                Check(stageCalls == 1 && deletes.Count == 1 && deletes[0] == output && seenAtDelete is { Length: > 0 } && (pin is null || Hex(seenAtDelete) == pin),
                    label + ": the delete probe ran once, with the output path as given, after the file was written" + (pin is null ? "" : " (the in-process pipeline's bytes)"),
                    "stage " + stageCalls.ToString(CultureInfo.InvariantCulture) + " deletes [" + string.Join(", ", deletes) + "] seen " + (seenAtDelete is null ? "<no file>" : Hex(seenAtDelete)));
                Check(forced.Thrown is null, label + ": neither the original nor the delete exception escapes Run to Program's generic catch", Q(forced));
                Check(forced.Out == envelope.Line + nl, label + ": stdout is still only the typed generate_prototypes_internal_error line", Q(forced));
                Check(forced.Err == Left + output + nl + envelope.Diagnostic + nl,
                    label + ": stderr is the documented cleanup line naming the output path, then the ORIGINAL exception's diagnostic exactly once", Q(forced));
                Check(!forced.Err.Contains(removal.Message, StringComparison.Ordinal) && !forced.Err.Contains(removal.GetType().FullName!, StringComparison.Ordinal)
                    && !forced.Out.Contains(removal.Message, StringComparison.Ordinal),
                    label + ": the delete exception never replaces the original (its type and message appear nowhere)", Q(forced));
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(forced.Exit == 1 && left is { Length: > 0 } && (pin is null || Hex(left) == pin),
                    label + ": exit 1 and the output file the stderr line names is left behind" + (pin is null ? "" : " with the in-process pipeline's bytes"),
                    Q(forced) + (left is null ? " <no file>" : " left " + Hex(left)));
                if (File.Exists(output)) File.Delete(output);
            }
            ExpectLeft("IOException on delete", request, pinned, new IOException("p7-27 delete io failure"));
            ExpectLeft("UnauthorizedAccessException on delete", request, pinned, new UnauthorizedAccessException("p7-27 delete access failure"));
            ExpectLeft("non-IO ArgumentException on delete", request, pinned, new ArgumentException("p7-27 delete non-io failure"));
            ExpectLeft("non-IO NotSupportedException on delete, derived project id", derivedRequest, null, new NotSupportedException("p7-27 delete not supported"));

            // The cleanup line itself fails (stderr throws IOException on it): the original exception is still the one reported.
            {
                var output = Output();
                var deletes = 0;
                using var recorder = new ReportRejectingWriter(Left);
                var previousOut = Console.Out;
                var previousError = Console.Error;
                var previousExit = Environment.ExitCode;
                using var stdout = new StringWriter(CultureInfo.InvariantCulture);
                Exception? thrown;
                int exit;
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(recorder);
                    Environment.ExitCode = 0;
                    probe.SetValue(null, (Action)(() => throw failure));
                    deleteProbe.SetValue(null, (Action<string>)(_ => { deletes++; throw new IOException("p7-27 delete io failure"); }));
                    try { thrown = Invoke(new[] { "generate-prototypes", request, output }); }
                    finally { probe.SetValue(null, null); deleteProbe.SetValue(null, null); }
                    exit = Environment.ExitCode;
                }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                    Environment.ExitCode = previousExit;
                }
                var detail = "out [" + stdout + "] recorded [" + string.Join(" | ", recorder.Lines) + "] partial [" + recorder.Partial + "] rejected " + recorder.Rejected.ToString(CultureInfo.InvariantCulture)
                    + " exit " + exit.ToString(CultureInfo.InvariantCulture) + (thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : "");
                Check(AllNull() && thrown is null, "a stderr that throws on the cleanup line: nothing escapes Run and the seams are reset", detail);
                Check(deletes == 1 && recorder.Rejected == 1 && stdout.ToString() == envelope.Line + nl && exit == 1,
                    "a stderr that throws on the cleanup line: the line was attempted once, stdout is still only the typed line, exit 1", detail);
                Check(recorder.Lines.Count == 1 && recorder.Lines[0] == envelope.Diagnostic && recorder.Partial.Length == 0,
                    "a stderr that throws on the cleanup line: the recorded stderr is exactly the ORIGINAL exception's diagnostic once", detail);
                if (File.Exists(output)) File.Delete(output);
            }

            // A delete probe that does not throw: section 7's bytes (file deleted, stderr only the diagnostic).
            {
                var output = Output();
                var deletes = new List<string>();
                var quiet = WithSeams(() => throw failure, path => deletes.Add(path), new[] { "generate-prototypes", request, output });
                Check(quiet.Thrown is null && deletes.Count == 1 && deletes[0] == output && quiet.Out == envelope.Line + nl && quiet.Err == envelope.Diagnostic + nl && quiet.Exit == 1
                    && !File.Exists(output) && !Directory.Exists(output),
                    "a delete probe that does not throw keeps section 7's bytes: typed line, diagnostic once, no cleanup line, exit 1, file deleted",
                    Q(quiet) + " deletes " + deletes.Count.ToString(CultureInfo.InvariantCulture) + (File.Exists(output) ? " <file left>" : ""));
            }
            Check(AllNull(), "the seams are null after the non-throwing delete probe run");

            // Success with the delete probe set: it is never called; the file and stdout are the real process's.
            foreach (var (label, requestPath, pin) in new (string Label, string Request, string? Pin)[] { ("explicit project id", request, pinned), ("derived project id", derivedRequest, null) })
            {
                var inOut = Output();
                var deleteCalls = 0;
                var inProcess = WithSeams(null, _ => { deleteCalls++; throw new IOException("p7-27 never"); }, new[] { "generate-prototypes", requestPath, inOut });
                var processOut = Output();
                var process = cli(new[] { "generate-prototypes", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(inProcess.Thrown is null && deleteCalls == 0 && inProcess.Exit == 0 && inProcess.Err.Length == 0 && inBytes.Length > 0 && (pin is null || Hex(inBytes) == pin)
                    && inBytes.SequenceEqual(processBytes) && process.Exit == 0 && AsProcess(inProcess) == process && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal),
                    "with the delete probe set, the " + label + " success never calls it and writes the real process's file and stdout",
                    Q(inProcess) + " deletes " + deleteCalls.ToString(CultureInfo.InvariantCulture) + " vs process " + process.Out + " " + process.Err);
            }

            // Refusals (every one before the write) with both seams set call neither and print the real process's bytes.
            var existing = Output();
            var sentinel = new UTF8Encoding(false).GetBytes("{\"sentinel\":\"p7-27\"}");
            File.WriteAllBytes(existing, sentinel);
            var stamp = new DateTime(2004, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(existing, stamp);
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-prototypes", request }),
                ("output extension", output => new[] { "generate-prototypes", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-prototypes", request, existing }),
                ("missing request", output => new[] { "generate-prototypes", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-prototypes", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-prototypes", Input("pack.json", good[..^1] + ",\"pack\":\"xx.no-such.pack\"}"), output }),
                ("pipeline refusal", output => new[] { "generate-prototypes", Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal)), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var a = Output();
                var b = Output();
                var calls = 0;
                var mine = WithSeams(() => { calls++; throw failure; }, _ => { calls++; throw new IOException("p7-27 never"); }, argsOf(a));
                var theirs = cli(argsOf(b));
                Check(mine.Thrown is null && calls == 0 && mine.Exit == 1 && theirs.Exit == 1 && AsProcess(mine) == theirs && mine.Err.Length == 0
                    && !File.Exists(a) && !File.Exists(b) && !File.Exists(a + ".txt") && !File.Exists(b + ".txt")
                    && mine.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !mine.Out.Contains("internal_error", StringComparison.Ordinal),
                    "with both seams set, the " + label + " refusal calls neither and equals the real process bytes", Q(mine) + " calls " + calls.ToString(CultureInfo.InvariantCulture) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(AllNull() && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
                "the seams are null after the refusal sample and the existing output is byte- and mtime-identical");

            // Static: the delete seam over interfaces/**/*.cs, comments and literals stripped, plus self-tests of the checker.
            var interfaces = Path.Combine(root, "interfaces");
            var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file))).ToArray();
            var seam = PostWriteDeleteSeamProblems(sources);
            Check(seam.Count == 0, "interfaces/**/*.cs names PostWriteDeleteProbe only in its declaration and one ?.Invoke(output) right before File.Delete(output) in the post-write catch (Exception), "
                + "whose unfiltered catch (Exception) writes Console.Error.WriteLine(PartialOutputLeft + output) without a throw, then throw; ends the handler; never assigned [" + string.Join("; ", seam) + "]");
            const string Path27 = "interfaces/cli/GenerateWrite.cs";
            const string Decls = "internal const string PartialOutputLeft = \"x\"; internal static Action<string>? PostWriteDeleteProbe { get; set; }";
            static string Command(string handler, string extra = "", string decls = Decls) =>
                "internal static class GenerateCommand { static void Execute(string[] args) { GltfExporter.WriteNew(bytes, output); try { PostWriteStageProbe?.Invoke(); Console.WriteLine(x); } catch (Exception) { "
                + handler + " } } " + decls + " " + extra + " }";
            const string Inner = "try { PostWriteDeleteProbe?.Invoke(output); File.Delete(output); }";
            const string Report = " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }";
            const string Good = Inner + Report + " throw;";
            Check(PostWriteDeleteSeamProblems(new[] { (Path27, Command(Good)) }).Count == 0
                && PostWriteDeleteSeamProblems(new[] { (Path27, Command("try\n { PostWriteDeleteProbe ?. Invoke ( output ) ; // PostWriteDeleteProbe = null; }\n File . Delete ( output ) ; }\n catch ( Exception )\n"
                    + " { try { /* throw; } */ Console . Error . WriteLine ( PartialOutputLeft + output ) ; }\n catch ( Exception ) { /* } throw; { */ } }\n throw ;", "static string S = \"} throw; { catch\";")) }).Count == 0,
                "self-test: the delete seam checker accepts the real shape at any spacing, ignoring comments and string literals (braces and throw in them too)");
            var evasions = new (string Label, (string, string)[] Files)[]
            {
                ("an assignment in the command", new[] { (Path27, Command(Good, "static void Set() { PostWriteDeleteProbe = _ => { }; }")) }),
                ("an assignment in another interfaces file", new[] { (Path27, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateCommand.PostWriteDeleteProbe = null; } }") }),
                ("a compound assignment", new[] { (Path27, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateCommand.PostWriteDeleteProbe ??= null; } }") }),
                ("the declaration missing", new[] { (Path27, Command(Good, decls: "internal const string PartialOutputLeft = \"x\";")) }),
                ("an Action declaration", new[] { (Path27, Command(Good, decls: "internal const string PartialOutputLeft = \"x\"; internal static Action? PostWriteDeleteProbe { get; set; }")) }),
                ("the constant missing", new[] { (Path27, Command(Good, decls: "internal static Action<string>? PostWriteDeleteProbe { get; set; }")) }),
                ("the command file missing", new[] { ("interfaces/cli/Other.cs", Command(Good)) }),
                ("the invoke missing", new[] { (Path27, Command("try { File.Delete(output); }" + Report + " throw;")) }),
                ("a second invoke", new[] { (Path27, Command("PostWriteDeleteProbe?.Invoke(output); " + Good)) }),
                ("the invoke after File.Delete", new[] { (Path27, Command("try { File.Delete(output); PostWriteDeleteProbe?.Invoke(output); }" + Report + " throw;")) }),
                ("the invoke with another argument", new[] { (Path27, Command("try { PostWriteDeleteProbe?.Invoke(args[2]); File.Delete(output); }" + Report + " throw;")) }),
                ("the invoke outside Execute", new[] { (Path27, Command("try { File.Delete(output); }" + Report + " throw;", "static void Other() { try { PostWriteDeleteProbe?.Invoke(output); File.Delete(output); }" + Report + " }")) }),
                ("the delete try not first in the handler", new[] { (Path27, Command("Console.Error.WriteLine(x); " + Good)) }),
                ("the delete try in a catch not after the stage probe's try", new[] { (Path27,
                    "internal static class GenerateCommand { static void Execute(string[] args) { GltfExporter.WriteNew(bytes, output); try { Console.WriteLine(x); } catch (Exception) { " + Good + " } } " + Decls + " }") }),
                ("a filtered report catch", new[] { (Path27, Command(Inner + " catch (Exception removal) when (removal is IOException) { Console.Error.WriteLine(PartialOutputLeft + output); } throw;")) }),
                ("an IOException-only report catch", new[] { (Path27, Command(Inner + " catch (IOException) { Console.Error.WriteLine(PartialOutputLeft + output); } throw;")) }),
                ("a report catch that rethrows", new[] { (Path27, Command(Inner + " catch (Exception) { Console.Error.WriteLine(PartialOutputLeft + output); throw; } throw;")) }),
                ("a report catch that throws the delete exception", new[] { (Path27, Command(Inner + " catch (Exception removal) { Console.Error.WriteLine(PartialOutputLeft + output); throw removal; } throw;")) }),
                ("a report catch without the stderr line", new[] { (Path27, Command(Inner + " catch (Exception) { } throw;")) }),
                ("an unwrapped report line", new[] { (Path27, Command(Inner + " catch (Exception) { Console.Error.WriteLine(PartialOutputLeft + output); } throw;")) }),
                ("a throw in the report line's inner catch", new[] { (Path27, Command(Inner + " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { throw; } } throw;")) }),
                ("a filtered inner catch around the report line", new[] { (Path27, Command(Inner + " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (IOException) { } } throw;")) }),
                ("a statement after the wrapped report line", new[] { (Path27, Command(Inner + " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } Console.Error.WriteLine(x); } throw;")) }),
                ("a report line to stdout", new[] { (Path27, Command(Inner + " catch (Exception) { Console.WriteLine(PartialOutputLeft + output); } throw;")) }),
                ("throw; missing after the report catch", new[] { (Path27, Command(Inner + Report)) }),
                ("a statement after throw;", new[] { (Path27, Command(Good + " return;")) }),
                ("throw new instead of throw;", new[] { (Path27, Command(Inner + Report + " throw new InvalidOperationException();")) }),
            };
            foreach (var (label, files) in evasions)
                Check(PostWriteDeleteSeamProblems(files).Count > 0, "self-test: the delete seam checker flags " + label);
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // P7-27 review: a stderr that throws IOException on a line starting with the cleanup prefix (counted in Rejected) and
    // records every other line; characters written outside WriteLine(string) collect in Partial.
    sealed class ReportRejectingWriter(string prefix) : TextWriter
    {
        readonly StringBuilder partial = new();
        public List<string> Lines { get; } = new();
        public int Rejected { get; private set; }
        public string Partial => partial.ToString();
        public override Encoding Encoding => new UTF8Encoding(false);
        public override void Write(char value) => partial.Append(value);
        public override void WriteLine(string? value)
        {
            if (value is not null && value.StartsWith(prefix, StringComparison.Ordinal)) { Rejected++; throw new IOException("p7-27 stderr rejects the cleanup line"); }
            Lines.Add(value ?? "");
        }
    }

    // P7-27: over (relative path, source) pairs under interfaces/, comments stripped and string literals blanked:
    // (P4-27: the path follows Execute into the CLI-only write part GenerateWrite.cs; the expectation is unchanged.)
    // PostWriteDeleteProbe is named exactly twice in total, both in interfaces/cli/GenerateWrite.cs: its Action<string>?
    // declaration and one PostWriteDeleteProbe?.Invoke(output); no assignment anywhere; PartialOutputLeft declared once as an
    // internal const string. Brace-matched inside Execute's body: the invoke's innermost try block is exactly
    // { PostWriteDeleteProbe?.Invoke(output); File.Delete(output); }, is the first statement of a catch (Exception) { that
    // directly follows a try block holding PostWriteStageProbe?.Invoke(), is directly followed by an unfiltered
    // catch (Exception) whose body is exactly { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }
    // (so neither the delete nor a throwing stderr replaces the original), and that is directly followed by throw; and the
    // handler's closing brace.
    static List<string> PostWriteDeleteSeamProblems(IEnumerable<(string Name, string Source)> files)
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
        static System.Text.RegularExpressions.Match At(string code, string pattern, int from) => new System.Text.RegularExpressions.Regex(@"\G" + pattern).Match(code, from);
        var problems = new List<string>();
        var total = 0;
        var sawCommand = false;
        foreach (var (name, source) in files)
        {
            var code = ApplyIntentTests.CodeOf(source);
            total += System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteDeleteProbe\b").Count;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bPostWriteDeleteProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": PostWriteDeleteProbe assigned");
            if (name != "interfaces/cli/GenerateWrite.cs") continue;
            sawCommand = true;
            var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+static\s+Action\s*<\s*string\s*>\s*\?\s*PostWriteDeleteProbe\s*\{\s*get\s*;\s*set\s*;\s*\}").Count;
            if (declarations != 1) problems.Add(name + ": the Action<string>? declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var constants = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+const\s+string\s+PartialOutputLeft\s*=").Count;
            if (constants != 1) problems.Add(name + ": the PartialOutputLeft constant found " + constants.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteDeleteProbe\s*\?\s*\.\s*Invoke\s*\(\s*output\s*\)\s*;");
            var execute = System.Text.RegularExpressions.Regex.Match(code, @"\bstatic\s+void\s+Execute\s*\([^)]*\)\s*\{");
            if (invokes.Count != 1 || !execute.Success) { problems.Add(name + ": " + invokes.Count.ToString(CultureInfo.InvariantCulture) + " PostWriteDeleteProbe?.Invoke(output) calls, Execute " + (execute.Success ? "found" : "missing")); continue; }
            var open = execute.Index + execute.Length - 1;
            var close = Close(code, open);
            if (close < 0) { problems.Add(name + ": Execute's body is not closed"); continue; }
            var at = invokes[0].Index;
            if (at < open || at > close) { problems.Add(name + ": PostWriteDeleteProbe?.Invoke(output) not inside Execute's body"); continue; }
            // The innermost try { inside Execute whose block holds the invoke.
            int tryStart = -1, tryOpen = -1, tryClose = -1;
            foreach (System.Text.RegularExpressions.Match candidate in new System.Text.RegularExpressions.Regex(@"\btry\s*\{").Matches(code[..at], open))
            {
                var brace = candidate.Index + candidate.Length - 1;
                var end = Close(code, brace);
                if (end > at && end < close) { tryStart = candidate.Index; tryOpen = brace; tryClose = end; }
            }
            if (tryOpen < 0) { problems.Add(name + ": PostWriteDeleteProbe?.Invoke(output) not inside a try { in Execute"); continue; }
            if (!System.Text.RegularExpressions.Regex.IsMatch(code[tryOpen..(tryClose + 1)], @"^\{\s*PostWriteDeleteProbe\s*\?\s*\.\s*Invoke\s*\(\s*output\s*\)\s*;\s*File\s*\.\s*Delete\s*\(\s*output\s*\)\s*;\s*\}$"))
            { problems.Add(name + ": the invoke's try block is not exactly { PostWriteDeleteProbe?.Invoke(output); File.Delete(output); }"); continue; }
            // The enclosing handler: catch (Exception) { directly before the try, following a try block holding PostWriteStageProbe?.Invoke().
            var before = System.Text.RegularExpressions.Regex.Match(code[..tryStart], @"\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*$");
            if (!before.Success) { problems.Add(name + ": the delete try is not the first statement of a catch (Exception) {"); continue; }
            var stageClose = before.Index;
            var handlerOpen = before.Index + before.Length - 1;
            while (code[handlerOpen] != '{') handlerOpen--;
            var handlerClose = Close(code, handlerOpen);
            var stageTry = false;
            foreach (System.Text.RegularExpressions.Match candidate in new System.Text.RegularExpressions.Regex(@"\btry\s*\{").Matches(code[..stageClose], open))
            {
                var brace = candidate.Index + candidate.Length - 1;
                if (Close(code, brace) == stageClose && System.Text.RegularExpressions.Regex.IsMatch(code[brace..stageClose], @"\bPostWriteStageProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)")) stageTry = true;
            }
            if (!stageTry) problems.Add(name + ": the delete try's catch (Exception) does not follow the try holding PostWriteStageProbe?.Invoke()");
            var report = At(code, @"\s*catch\s*\(\s*Exception\s*\)\s*\{", tryClose + 1);
            if (!report.Success) { problems.Add(name + ": the delete try is not directly followed by an unfiltered catch (Exception) {"); continue; }
            var reportOpen = report.Index + report.Length - 1;
            var reportClose = Close(code, reportOpen);
            if (reportClose < 0 || handlerClose < 0 || reportClose > handlerClose) { problems.Add(name + ": the report catch is not closed inside the handler"); continue; }
            var reportBody = code[reportOpen..(reportClose + 1)];
            if (!System.Text.RegularExpressions.Regex.IsMatch(reportBody, @"^\{\s*try\s*\{\s*Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*PartialOutputLeft\s*\+\s*output\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}$"))
                problems.Add(name + ": the report catch is not exactly { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }");
            if (System.Text.RegularExpressions.Regex.IsMatch(reportBody, @"\bthrow\b")) problems.Add(name + ": the report catch throws");
            var rethrow = At(code, @"\s*throw\s*;\s*\}", reportClose + 1);
            if (!rethrow.Success || rethrow.Index + rethrow.Length - 1 != handlerClose) problems.Add(name + ": the report catch is not directly followed by throw; ending the handler");
        }
        if (!sawCommand) problems.Add("interfaces/cli/GenerateWrite.cs not found");
        if (total != 2) problems.Add("PostWriteDeleteProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // ---- 9. P7-28: a stderr that throws while Run's catch-all writes the internal-error diagnostic is contained ----
    // GenerateCommand.Run of the real Release CLI assembly (reflection), in process, with stdout and stderr replaced by
    // recording writers that append each WriteLine to one shared event list (so the stdout/stderr order is observable) and
    // restored, with the exit code and every seam, in a finally (the Console is checked to be the original writers again).
    // A non-refusal exception reaches Run's P7-25 catch-all through the P7-23 seam UnguardedStageProbe (before any write),
    // the P7-26 seam PostWriteStageProbe (after the write) or that seam plus the P7-27 delete seam PostWriteDeleteProbe
    // (the cleanup delete fails). When stderr throws on the IntentPlan diagnostic line (an IOException, a non-IO
    // InvalidOperationException on every write, or a disposed StringWriter's ObjectDisposedException): nothing escapes Run,
    // stdout is still exactly the typed {"status":"failed","code":"generate_prototypes_internal_error"} line, exit 1, the
    // diagnostic was attempted exactly once with the ORIGINAL exception's text, and the stderr exception's message appears
    // nowhere; the P7-26 cleanup is unchanged (the just-written file is deleted) and so is P7-27's (the PartialOutputLeft
    // line is recorded before the rejected diagnostic and the file is left behind). With a stderr that accepts everything
    // the bytes and order are section 7's (diagnostic, then the typed line). Success and sampled refusals with a stderr
    // that throws on every write never write stderr and equal the real process. Static: Run's catch-all shape is the
    // section-6 pin (P7-25, updated by P7-28), the delta over the P4-27 Run pin is in tests/mcp/GenerateWriteLinkTests.cs.
    static void DiagnosticWriteStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-28 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string Left = "generate-prototypes: could not delete partial output: ";
        const string DiagnosticPrefix = "generate-prototypes internal error: ";
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var inputs = Path.Combine(scratch, "p7-28-" + Guid.NewGuid().ToString("N"));
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
            var deleteSeam = type?.GetProperty("PostWriteDeleteProbe", Any);
            var unguarded = type?.GetProperty("UnguardedStageProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite, typeof(Action)) && Seam(deleteSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]) and the seams UnguardedStageProbe / PostWriteStageProbe / PostWriteDeleteProbe / GuardedWorkProbe found in the Release CLI");
            if (run is null || postWrite is null || deleteSeam is null || unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo stageProbe = postWrite;
            System.Reflection.PropertyInfo deleteProbe = deleteSeam;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[] args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            bool AllNull() => stageProbe.GetValue(null) is null && deleteProbe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-prototypes seams are null unless a test sets them");

            // One in-process Run with stdout / stderr replaced and the seams set; Console, exit code and seams restored in finally.
            (Exception? Thrown, List<string> Events, string Out, int Exit, EventWriter? Err, bool Restored) Forced(Func<List<string>, TextWriter> stderrOf, Action? before, Action? stage, Action<string>? delete, string[] args)
            {
                var events = new List<string>();
                var stdout = new EventWriter("out", events);
                var stderr = stderrOf(events);
                var previousOut = Console.Out;
                var previousError = Console.Error;
                var previousExit = Environment.ExitCode;
                Exception? thrown;
                int exit;
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    Environment.ExitCode = 0;
                    unguardedProbe.SetValue(null, before);
                    stageProbe.SetValue(null, stage);
                    deleteProbe.SetValue(null, delete);
                    try { thrown = Invoke(args); }
                    finally { unguardedProbe.SetValue(null, null); stageProbe.SetValue(null, null); deleteProbe.SetValue(null, null); }
                    exit = Environment.ExitCode;
                }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                    Environment.ExitCode = previousExit;
                }
                var restored = ReferenceEquals(Console.Out, previousOut) && ReferenceEquals(Console.Error, previousError) && Environment.ExitCode == previousExit;
                return (thrown, events, stdout.Text, exit, stderr as EventWriter, restored);
            }
            static string D((Exception? Thrown, List<string> Events, string Out, int Exit, EventWriter? Err, bool Restored) r) =>
                "events [" + string.Join(" | ", r.Events) + "] out [" + r.Out + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + (r.Err is { } e ? " rejected [" + string.Join(" | ", e.RejectedLines) + "] partial [" + e.Partial + "]" : "") + " restored " + r.Restored
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : "");

            var failure = new InvalidOperationException("boom generate-prototypes P7-28 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == DiagnosticPrefix + "System.InvalidOperationException: boom generate-prototypes P7-28 original",
                "fixture: IntentPlan.Internal(\"generate-prototypes\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            var typed = envelope.Line + Environment.NewLine;
            const string StderrMessage = "p7-28 stderr rejects the write";
            Func<List<string>, TextWriter> RejectDiagnostic = events => new EventWriter("err", events, DiagnosticPrefix, rejectAll: false, () => new IOException(StderrMessage));
            Func<List<string>, TextWriter> RejectAll = events => new EventWriter("err", events, null, rejectAll: true, () => new InvalidOperationException(StderrMessage));
            Func<List<string>, TextWriter> Disposed = _ => { var closed = new StringWriter(CultureInfo.InvariantCulture); closed.Dispose(); return closed; };
            bool Contained((Exception? Thrown, List<string> Events, string Out, int Exit, EventWriter? Err, bool Restored) r) =>
                r.Thrown is null && r.Restored && r.Exit == 1 && r.Out == typed && !r.Out.Contains(StderrMessage, StringComparison.Ordinal)
                && !string.Join("\n", r.Events).Contains(StderrMessage, StringComparison.Ordinal) && AllNull();

            // Control: a stderr that accepts everything keeps section 7's bytes and order (diagnostic, then the typed line).
            {
                var output = Output();
                var r = Forced(events => new EventWriter("err", events), null, () => throw failure, null, new[] { "generate-prototypes", request, output });
                Check(r.Thrown is null && r.Restored && r.Exit == 1 && r.Out == typed && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic, "out:" + envelope.Line })
                    && !File.Exists(output) && AllNull(),
                    "control: an accepting stderr records the diagnostic, then the typed line, exit 1, file deleted (section 7's bytes)", D(r));
            }

            // Before any write (P7-25 path): the diagnostic is rejected, the typed line still printed, no file.
            {
                var output = Output();
                var r = Forced(RejectDiagnostic, () => throw failure, null, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 1 } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic })
                    && !File.Exists(output) && !Directory.Exists(output),
                    "UnguardedStageProbe + stderr IOException on the diagnostic: nothing escapes Run, stdout is only the typed line, exit 1, the original diagnostic attempted once, no file", D(r));
            }

            // After the write (P7-26 path): the file is still deleted.
            foreach (var (label, requestPath, pin) in new (string, string, string?)[] { ("explicit project id", request, pinned), ("derived project id", derivedRequest, null) })
            {
                var output = Output();
                byte[]? seen = null;
                var r = Forced(RejectDiagnostic, null, () => { seen = File.Exists(output) ? File.ReadAllBytes(output) : null; throw failure; }, null, new[] { "generate-prototypes", requestPath, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 1 } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic })
                    && seen is { Length: > 0 } && (pin is null || Hex(seen) == pin) && !File.Exists(output),
                    "PostWriteStageProbe (" + label + ") + stderr IOException on the diagnostic: typed line, exit 1, nothing escapes, and the just-written file is still deleted (P7-26)",
                    D(r) + " seen " + (seen is null ? "<no file>" : Hex(seen)) + (File.Exists(output) ? " <file left>" : ""));
            }

            // Cleanup delete failing too (P7-27 path): the cleanup line is recorded, then the rejected diagnostic; the file is left.
            {
                var output = Output();
                var deletes = 0;
                var r = Forced(RejectDiagnostic, null, () => throw failure, _ => { deletes++; throw new IOException("p7-28 delete io failure"); }, new[] { "generate-prototypes", request, output });
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(Contained(r) && deletes == 1 && r.Events.SequenceEqual(new[] { "err:" + Left + output, "out:" + envelope.Line })
                    && r.Err is { Rejected: 1 } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && left is { Length: > 0 } && Hex(left) == pinned,
                    "PostWriteStageProbe + failing delete + stderr IOException on the diagnostic: the P7-27 line, then the typed line, exit 1, file left with the pipeline's bytes", D(r) + (left is null ? " <no file>" : " left " + Hex(left)));
                if (File.Exists(output)) File.Delete(output);
            }

            // A stderr that throws a non-IO exception on every write (the P7-27 line and the diagnostic both rejected).
            {
                var output = Output();
                var r = Forced(RejectAll, null, () => throw failure, _ => throw new NotSupportedException("p7-28 delete not supported"), new[] { "generate-prototypes", request, output });
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 2 } e
                    && e.RejectedLines.SequenceEqual(new[] { Left + output, envelope.Diagnostic }) && e.Partial.Length == 0 && left is { Length: > 0 } && Hex(left) == pinned,
                    "a stderr throwing InvalidOperationException on every write: the cleanup line and the diagnostic are both dropped, stdout is only the typed line, exit 1, file left", D(r));
                if (File.Exists(output)) File.Delete(output);
            }
            {
                var output = Output();
                var r = Forced(RejectAll, () => throw failure, null, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 1 } && !File.Exists(output),
                    "UnguardedStageProbe + a stderr throwing on every write: typed line, exit 1, nothing escapes, no file", D(r));
            }

            // A real closed stderr (a disposed StringWriter throws ObjectDisposedException).
            {
                var output = Output();
                var r = Forced(Disposed, null, () => throw failure, null, new[] { "generate-prototypes", request, output });
                Check(r.Thrown is null && r.Restored && r.Exit == 1 && r.Out == typed && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && !File.Exists(output) && AllNull(),
                    "PostWriteStageProbe + a disposed stderr (ObjectDisposedException): typed line, exit 1, nothing escapes, file deleted", D(r));
            }

            // Success with a stderr that throws on every write: stderr is never written; file and stdout are the real process's.
            foreach (var (label, requestPath, pin) in new (string, string, string?)[] { ("explicit project id", request, pinned), ("derived project id", derivedRequest, null) })
            {
                var inOut = Output();
                var r = Forced(RejectAll, null, null, null, new[] { "generate-prototypes", requestPath, inOut });
                var processOut = Output();
                var process = cli(new[] { "generate-prototypes", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && r.Err is { Rejected: 0 } && r.Out == process.Out + Environment.NewLine && process.Exit == 0 && process.Err.Length == 0
                    && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && inBytes.Length > 0 && (pin is null || Hex(inBytes) == pin) && inBytes.SequenceEqual(processBytes),
                    "with a rejecting stderr, the " + label + " success writes no stderr and equals the real process's file and stdout", D(r) + " vs process " + process.Out + " " + process.Err);
            }

            // Refusals (all typed, stdout only) with a stderr that throws on every write equal the real process bytes.
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-prototypes", request }),
                ("output extension", output => new[] { "generate-prototypes", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-prototypes", request, existing }),
                ("missing request", output => new[] { "generate-prototypes", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-prototypes", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-prototypes", Input("pack.json", good[..^1] + ",\"pack\":\"xx.no-such.pack\"}"), output }),
                ("pipeline refusal", output => new[] { "generate-prototypes", Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal)), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var a = Output();
                var b = Output();
                var mine = Forced(RejectAll, null, null, null, argsOf(a));
                var theirs = cli(argsOf(b));
                Check(mine.Thrown is null && mine.Restored && mine.Exit == 1 && theirs.Exit == 1 && mine.Err is { Rejected: 0 } && theirs.Err.Length == 0 && mine.Out == theirs.Out + Environment.NewLine
                    && mine.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !mine.Out.Contains("internal_error", StringComparison.Ordinal)
                    && !File.Exists(a) && !File.Exists(b) && !File.Exists(a + ".txt") && !File.Exists(b + ".txt"),
                    "with a rejecting stderr, the " + label + " refusal equals the real process bytes", D(mine) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(File.ReadAllText(existing) == "keep" && AllNull(), "the existing output is left untouched and the seams are null after the sample");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // P7-28: a recording writer: each WriteLine(string) is appended to the shared event list as "<stream>:<line>" and to
    // Text (with the newline). As stderr it can reject (throw the given exception, counted in Rejected, the line kept in
    // RejectedLines) a WriteLine whose line starts with rejectPrefix, or every write when rejectAll; other characters
    // collect in Partial.
    sealed class EventWriter(string stream, List<string> events, string? rejectPrefix = null, bool rejectAll = false, Func<Exception>? failure = null) : TextWriter
    {
        readonly StringBuilder text = new();
        readonly StringBuilder partial = new();
        public int Rejected => RejectedLines.Count;
        public List<string> RejectedLines { get; } = new();
        public string Text => text.ToString();
        public string Partial => partial.ToString();
        public override Encoding Encoding => new UTF8Encoding(false);
        Exception Reject(string? value) { RejectedLines.Add(value ?? ""); return failure?.Invoke() ?? new IOException("p7-28 rejected write"); }
        public override void Write(char value) { if (rejectAll) throw Reject(value.ToString()); partial.Append(value); text.Append(value); }
        public override void Write(string? value) { if (rejectAll) throw Reject(value); partial.Append(value); text.Append(value); }
        public override void WriteLine(string? value)
        {
            if (rejectAll || (rejectPrefix is not null && value is not null && value.StartsWith(rejectPrefix, StringComparison.Ordinal))) throw Reject(value);
            events.Add(stream + ":" + (value ?? ""));
            text.Append(value).Append(CoreNewLine);
        }
    }

    // ---- 10. P7-29: a stdout that throws while Run's catch-all writes the typed line is contained ----
    // GenerateCommand.Run of the real Release CLI assembly (reflection), in process, with stdout and stderr replaced by the
    // section-9 recording writers (EventWriter; either may reject) and restored, with the exit code and every seam, in a
    // finally. A non-refusal exception reaches Run's P7-25 catch-all through UnguardedStageProbe (before any write),
    // PostWriteStageProbe (after the write; P7-26 deletes the file) or that seam plus PostWriteDeleteProbe (P7-27: the file
    // is left and named on stderr). When stdout throws on the typed line (IOException, non-IO InvalidOperationException, a
    // disposed StringWriter's ObjectDisposedException): nothing escapes Run, the exit code is 1, the typed line was
    // attempted exactly once, stderr is the section 7-9 bytes (the diagnostic once, after the P7-27 line when the delete
    // fails) and the stdout exception's message appears nowhere. A stdout that rejects the success line makes the run an
    // internal error: the just-written file is deleted (P7-26) and stderr is the stdout exception's diagnostic. The guarded
    // pipeline block's own diagnostic write (InternalRefusal in GenerateCommand.cs, forced through GuardedWorkProbe with a
    // stderr rejecting it) throws out of Generate; Run's catch-all contains it: the pinned bytes are the typed line on
    // stdout, exit 1, and two rejected diagnostics (the original exception's, then the stderr exception's); with stdout
    // rejecting too, still exit 1 and nothing escapes. Statically, that write is pinned unguarded in InternalRefusal and
    // reached only from the guarded block's catch. Controls: with accepting writers the catch-all is section 7's order
    // (diagnostic, then the typed line), and success and sampled refusals equal the real process.
    static void OutputWriteStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-29 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string Left = "generate-prototypes: could not delete partial output: ";
        const string DiagnosticPrefix = "generate-prototypes internal error: ";
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var inputs = Path.Combine(scratch, "p7-29-" + Guid.NewGuid().ToString("N"));
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
            var deleteSeam = type?.GetProperty("PostWriteDeleteProbe", Any);
            var unguarded = type?.GetProperty("UnguardedStageProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite, typeof(Action)) && Seam(deleteSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]) and the seams UnguardedStageProbe / PostWriteStageProbe / PostWriteDeleteProbe / GuardedWorkProbe found in the Release CLI");
            if (run is null || postWrite is null || deleteSeam is null || unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo stageProbe = postWrite;
            System.Reflection.PropertyInfo deleteProbe = deleteSeam;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[] args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            bool AllNull() => stageProbe.GetValue(null) is null && deleteProbe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-prototypes seams are null unless a test sets them");

            // One in-process Run with stdout / stderr replaced and the seams set; Console, exit code and seams restored in finally.
            (Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) Forced(
                Func<List<string>, TextWriter> stdoutOf, Func<List<string>, TextWriter> stderrOf, Action? before, Action? work, Action? stage, Action<string>? delete, string[] args)
            {
                var events = new List<string>();
                var stdout = stdoutOf(events);
                var stderr = stderrOf(events);
                var previousOut = Console.Out;
                var previousError = Console.Error;
                var previousExit = Environment.ExitCode;
                Exception? thrown;
                int exit;
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    Environment.ExitCode = 0;
                    unguardedProbe.SetValue(null, before);
                    guardedProbe.SetValue(null, work);
                    stageProbe.SetValue(null, stage);
                    deleteProbe.SetValue(null, delete);
                    try { thrown = Invoke(args); }
                    finally { unguardedProbe.SetValue(null, null); guardedProbe.SetValue(null, null); stageProbe.SetValue(null, null); deleteProbe.SetValue(null, null); }
                    exit = Environment.ExitCode;
                }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                    Environment.ExitCode = previousExit;
                }
                var restored = ReferenceEquals(Console.Out, previousOut) && ReferenceEquals(Console.Error, previousError) && Environment.ExitCode == previousExit;
                return (thrown, events, exit, stdout as EventWriter, stderr as EventWriter, restored);
            }
            static string D((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r) =>
                "events [" + string.Join(" | ", r.Events) + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + (r.Out is { } o ? " stdout text [" + o.Text + "] stdout rejected [" + string.Join(" | ", o.RejectedLines) + "]" : "")
                + (r.Err is { } e ? " stderr rejected [" + string.Join(" | ", e.RejectedLines) + "]" : "") + " restored " + r.Restored
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : " threw nothing");

            var failure = new InvalidOperationException("boom generate-prototypes P7-29 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == DiagnosticPrefix + "System.InvalidOperationException: boom generate-prototypes P7-29 original",
                "fixture: IntentPlan.Internal(\"generate-prototypes\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            const string StdoutMessage = "p7-29 stdout rejects the write";
            const string StderrMessage = "p7-29 stderr rejects the write";
            Func<List<string>, TextWriter> AcceptOut = events => new EventWriter("out", events);
            Func<List<string>, TextWriter> AcceptErr = events => new EventWriter("err", events);
            Func<List<string>, TextWriter> RejectOut = events => new EventWriter("out", events, null, rejectAll: true, () => new IOException(StdoutMessage));
            Func<List<string>, TextWriter> RejectOutNonIo = events => new EventWriter("out", events, null, rejectAll: true, () => new InvalidOperationException(StdoutMessage));
            Func<List<string>, TextWriter> RejectErrAll = events => new EventWriter("err", events, null, rejectAll: true, () => new InvalidOperationException(StderrMessage));
            Func<List<string>, TextWriter> RejectErrDiagnostic = events => new EventWriter("err", events, DiagnosticPrefix, rejectAll: false, () => new IOException(StderrMessage));
            Func<List<string>, TextWriter> DisposedOut = _ => { var closed = new StringWriter(CultureInfo.InvariantCulture); closed.Dispose(); return closed; };
            bool NoMessage((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r, string message) =>
                !string.Join("\n", r.Events).Contains(message, StringComparison.Ordinal) && (r.Out is null || !r.Out.Text.Contains(message, StringComparison.Ordinal));
            bool Contained((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r) =>
                r.Thrown is null && r.Restored && r.Exit == 1 && AllNull();
            bool TypedRejectedOnce((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r) =>
                r.Out is { } o && o.RejectedLines.SequenceEqual(new[] { envelope.Line }) && o.Text.Length == 0 && o.Partial.Length == 0;

            // Control: accepting writers keep section 7's bytes and order (diagnostic, then the typed line).
            {
                var output = Output();
                var r = Forced(AcceptOut, AcceptErr, null, null, () => throw failure, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic, "out:" + envelope.Line }) && r.Out is { } o && o.Text == envelope.Line + Environment.NewLine && !File.Exists(output),
                    "control: accepting writers record the diagnostic, then the typed line, exit 1, file deleted (section 7's bytes)", D(r));
            }

            // Before any write (P7-25 path): stdout rejects the typed line.
            {
                var output = Output();
                var r = Forced(RejectOut, AcceptErr, () => throw failure, null, null, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && TypedRejectedOnce(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage) && !File.Exists(output) && !Directory.Exists(output),
                    "UnguardedStageProbe + stdout IOException on the typed line: nothing escapes Run, exit 1, typed line attempted once, stderr the original diagnostic once, no file", D(r));
            }

            // After the write (P7-26 path): the file is still deleted.
            foreach (var (label, requestPath, pin) in new (string, string, string?)[] { ("explicit project id", request, pinned), ("derived project id", derivedRequest, null) })
            {
                var output = Output();
                byte[]? seen = null;
                var r = Forced(RejectOut, AcceptErr, null, null, () => { seen = File.Exists(output) ? File.ReadAllBytes(output) : null; throw failure; }, null, new[] { "generate-prototypes", requestPath, output });
                Check(Contained(r) && TypedRejectedOnce(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage)
                    && seen is { Length: > 0 } && (pin is null || Hex(seen) == pin) && !File.Exists(output),
                    "PostWriteStageProbe (" + label + ") + stdout IOException on the typed line: exit 1, nothing escapes, diagnostic once, the just-written file still deleted (P7-26)",
                    D(r) + " seen " + (seen is null ? "<no file>" : Hex(seen)) + (File.Exists(output) ? " <file left>" : ""));
            }

            // Cleanup delete failing too (P7-27 path): the cleanup line, then the diagnostic; the file is left.
            {
                var output = Output();
                var deletes = 0;
                var r = Forced(RejectOut, AcceptErr, null, null, () => throw failure, _ => { deletes++; throw new IOException("p7-29 delete io failure"); }, new[] { "generate-prototypes", request, output });
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(Contained(r) && TypedRejectedOnce(r) && deletes == 1 && r.Events.SequenceEqual(new[] { "err:" + Left + output, "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage)
                    && left is { Length: > 0 } && Hex(left) == pinned,
                    "PostWriteStageProbe + failing delete + stdout IOException on the typed line: the P7-27 line, then the diagnostic, exit 1, nothing escapes, file left with the pipeline's bytes",
                    D(r) + (left is null ? " <no file>" : " left " + Hex(left)));
                if (File.Exists(output)) File.Delete(output);
            }

            // Both streams throwing a non-IO exception on every write.
            {
                var output = Output();
                var r = Forced(RejectOutNonIo, RejectErrAll, () => throw failure, null, null, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && TypedRejectedOnce(r) && r.Events.Count == 0 && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && !File.Exists(output),
                    "UnguardedStageProbe + stdout and stderr throwing InvalidOperationException on every write: exit 1, nothing escapes, each line attempted once, no file", D(r));
            }

            // A real closed stdout (a disposed StringWriter throws ObjectDisposedException).
            {
                var output = Output();
                var r = Forced(DisposedOut, AcceptErr, null, null, () => throw failure, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                    "PostWriteStageProbe + a disposed stdout (ObjectDisposedException): exit 1, nothing escapes, diagnostic once, file deleted", D(r));
            }

            // A stdout rejecting the success line: an internal error about the stdout exception; the written file is deleted.
            {
                var output = Output();
                var r = Forced(RejectOut, AcceptErr, null, null, null, null, new[] { "generate-prototypes", request, output });
                var stdoutDiagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-prototypes", new IOException(StdoutMessage));
                Check(Contained(r) && r.Out is { } o && o.RejectedLines.Count == 2 && o.RejectedLines[0].StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal)
                    && o.RejectedLines[1] == envelope.Line && o.Text.Length == 0 && r.Events.SequenceEqual(new[] { "err:" + stdoutDiagnostic }) && !File.Exists(output),
                    "a stdout rejecting the success line: exit 1, nothing escapes, success then typed line attempted once each, stderr the stdout exception's diagnostic, file deleted (P7-26)",
                    D(r) + (File.Exists(output) ? " <file left>" : ""));
            }

            // The guarded block's own diagnostic write (InternalRefusal) throwing: pinned bytes, contained by Run's catch-all.
            var stderrDiagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-prototypes", new IOException(StderrMessage));
            {
                var output = Output();
                var r = Forced(AcceptOut, RejectErrDiagnostic, null, () => throw failure, null, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic, stderrDiagnostic })
                    && r.Out is { } o && o.Text == envelope.Line + Environment.NewLine && !File.Exists(output),
                    "GuardedWorkProbe + stderr rejecting the guarded block's diagnostic: the typed line, exit 1, nothing escapes, the original then the stderr exception's diagnostic attempted, no file (pinned)", D(r));
            }
            {
                var output = Output();
                var r = Forced(RejectOut, RejectErrDiagnostic, null, () => throw failure, null, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && TypedRejectedOnce(r) && r.Events.Count == 0 && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic, stderrDiagnostic }) && !File.Exists(output),
                    "GuardedWorkProbe + stderr rejecting the guarded diagnostic + stdout rejecting the typed line: exit 1, nothing escapes, no file", D(r));
            }
            {
                var output = Output();
                var r = Forced(AcceptOut, AcceptErr, null, () => throw failure, null, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic, "out:" + envelope.Line }) && !File.Exists(output),
                    "control: GuardedWorkProbe with accepting writers is the diagnostic once, then the typed line, exit 1 (P3-20 bytes)", D(r));
            }

            // Static: the guarded block's diagnostic write is the unguarded line in InternalRefusal, reached only from that catch.
            var shared = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateCommand.cs")), blankStrings: false);
            var internalRefusal = System.Text.RegularExpressions.Regex.Matches(shared,
                @"static\s+Refusal\s+InternalRefusal\s*\(\s*Exception\s+error\s*\)\s*\{\s*Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*IntentPlan\s*\.\s*InternalDiagnostic\s*\(\s*""generate-prototypes""\s*,\s*error\s*\)\s*\)\s*;\s*return\s+new\s+Refusal\s*\(\s*InternalError\s*\)\s*;\s*\}").Count;
            var guardedCatch = System.Text.RegularExpressions.Regex.Matches(shared,
                @"try\s*\{\s*GuardedWorkProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;[^{}]*\}\s*catch\s*\(\s*Exception\s+error\s*\)\s*\{\s*throw\s+InternalRefusal\s*\(\s*error\s*\)\s*;\s*\}").Count;
            var calls = System.Text.RegularExpressions.Regex.Matches(shared, @"\bInternalRefusal\s*\(").Count;
            Check(internalRefusal == 1 && guardedCatch == 1 && calls == 2,
                "GenerateCommand.cs: InternalRefusal writes IntentPlan.InternalDiagnostic to Console.Error unguarded then returns Refusal(InternalError), and is called only from the guarded block's catch (Exception) (pinned; containment rests on Run's catch-all)",
                "InternalRefusal " + internalRefusal.ToString(CultureInfo.InvariantCulture) + ", guarded catch " + guardedCatch.ToString(CultureInfo.InvariantCulture) + ", name occurrences " + calls.ToString(CultureInfo.InvariantCulture));

            // Success with accepting writers equals the real process (file and stdout); stderr is never written.
            foreach (var (label, requestPath, pin) in new (string, string, string?)[] { ("explicit project id", request, pinned), ("derived project id", derivedRequest, null) })
            {
                var inOut = Output();
                var r = Forced(AcceptOut, RejectErrAll, null, null, null, null, new[] { "generate-prototypes", requestPath, inOut });
                var processOut = Output();
                var process = cli(new[] { "generate-prototypes", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && r.Err is { Rejected: 0 } && r.Out is { } o && o.Text == process.Out + Environment.NewLine && process.Exit == 0 && process.Err.Length == 0
                    && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && inBytes.Length > 0 && (pin is null || Hex(inBytes) == pin) && inBytes.SequenceEqual(processBytes),
                    "the " + label + " success writes no stderr and equals the real process's file and stdout", D(r) + " vs process " + process.Out + " " + process.Err);
            }

            // Refusals with accepting writers equal the real process bytes.
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-prototypes", request }),
                ("output extension", output => new[] { "generate-prototypes", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-prototypes", request, existing }),
                ("missing request", output => new[] { "generate-prototypes", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-prototypes", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-prototypes", Input("pack.json", good[..^1] + ",\"pack\":\"xx.no-such.pack\"}"), output }),
                ("pipeline refusal", output => new[] { "generate-prototypes", Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal)), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var a = Output();
                var b = Output();
                var mine = Forced(AcceptOut, RejectErrAll, null, null, null, null, argsOf(a));
                var theirs = cli(argsOf(b));
                Check(mine.Thrown is null && mine.Restored && mine.Exit == 1 && theirs.Exit == 1 && mine.Err is { Rejected: 0 } && theirs.Err.Length == 0 && mine.Out is { } o && o.Text == theirs.Out + Environment.NewLine
                    && o.Text.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !o.Text.Contains("internal_error", StringComparison.Ordinal)
                    && !File.Exists(a) && !File.Exists(b) && !File.Exists(a + ".txt") && !File.Exists(b + ".txt"),
                    "the " + label + " refusal equals the real process bytes", D(mine) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(File.ReadAllText(existing) == "keep" && AllNull(), "the existing output is left untouched and the seams are null after the sample");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- 11. P7-30: a stdout that throws while Fail writes a typed refusal is contained ----
    // GenerateCommand.Run of the real Release CLI assembly (reflection), in process, with stdout and stderr replaced by the
    // section-9 recording writers (EventWriter) and restored, with the exit code and every seam, in a finally. Every typed
    // refusal Run's catch (Refusal) hands to Fail (usage, output extension, output exists, missing request, malformed JSON,
    // unknown pack, and the guarded pipeline block's Refusal(InternalError) forced through GuardedWorkProbe with an
    // accepting stderr) is written to a stdout that throws (IOException, non-IO InvalidOperationException, a disposed
    // StringWriter's ObjectDisposedException): nothing escapes Run, the exit code is 1, the typed line (the real process's
    // stdout bytes) was attempted exactly once, stderr is unchanged (nothing for a typed refusal; the guarded block's
    // diagnostic once) and the stdout exception's message appears nowhere. A pipeline refusal (Execute's RefusedLine, not a
    // Refusal) whose stdout write throws is reported one documented way, pinned: the exception reaches Run's catch-all, so
    // stderr is the stdout exception's internal-error diagnostic once, the refused line then the typed internal-error line
    // were attempted once each, exit 1, nothing escapes, no file (statically: that write stays the unguarded
    // Console.WriteLine(refused) in Execute). Controls: accepting writers give the real process's refusal and success bytes
    // with stderr never written.
    static void RefusalWriteStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-30 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string DiagnosticPrefix = "generate-prototypes internal error: ";
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var inputs = Path.Combine(scratch, "p7-30-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            const string good = "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}";
            var request = Input("request.json", good);
            var derivedRequest = Input("derived.json", "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2}");
            var refusedRequest = Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal));
            var pinned = Hex(expected);

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var postWrite = type?.GetProperty("PostWriteStageProbe", Any);
            var deleteSeam = type?.GetProperty("PostWriteDeleteProbe", Any);
            var unguarded = type?.GetProperty("UnguardedStageProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite, typeof(Action)) && Seam(deleteSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]) and the seams UnguardedStageProbe / PostWriteStageProbe / PostWriteDeleteProbe / GuardedWorkProbe found in the Release CLI");
            if (run is null || postWrite is null || deleteSeam is null || unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo[] seams = { postWrite, deleteSeam, unguarded, guarded };
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[] args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            bool AllNull() => seams.All(seam => seam.GetValue(null) is null);
            Check(AllNull(), "the generate-prototypes seams are null unless a test sets them");

            // One in-process Run with stdout / stderr replaced (and GuardedWorkProbe set when given); all restored in finally.
            (Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) Forced(
                Func<List<string>, TextWriter> stdoutOf, Func<List<string>, TextWriter> stderrOf, Action? work, string[] args)
            {
                var events = new List<string>();
                var stdout = stdoutOf(events);
                var stderr = stderrOf(events);
                var previousOut = Console.Out;
                var previousError = Console.Error;
                var previousExit = Environment.ExitCode;
                Exception? thrown;
                int exit;
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    Environment.ExitCode = 0;
                    guardedProbe.SetValue(null, work);
                    try { thrown = Invoke(args); }
                    finally { foreach (var seam in seams) seam.SetValue(null, null); }
                    exit = Environment.ExitCode;
                }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                    Environment.ExitCode = previousExit;
                }
                var restored = ReferenceEquals(Console.Out, previousOut) && ReferenceEquals(Console.Error, previousError) && Environment.ExitCode == previousExit;
                return (thrown, events, exit, stdout as EventWriter, stderr as EventWriter, restored);
            }
            static string D((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r) =>
                "events [" + string.Join(" | ", r.Events) + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + (r.Out is { } o ? " stdout text [" + o.Text + "] stdout rejected [" + string.Join(" | ", o.RejectedLines) + "]" : "")
                + (r.Err is { } e ? " stderr rejected [" + string.Join(" | ", e.RejectedLines) + "]" : "") + " restored " + r.Restored
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : " threw nothing");

            var failure = new InvalidOperationException("boom generate-prototypes P7-30 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == DiagnosticPrefix + "System.InvalidOperationException: boom generate-prototypes P7-30 original",
                "fixture: IntentPlan.Internal(\"generate-prototypes\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            const string StdoutMessage = "p7-30 stdout rejects the write";
            Func<List<string>, TextWriter> AcceptOut = events => new EventWriter("out", events);
            Func<List<string>, TextWriter> AcceptErr = events => new EventWriter("err", events);
            Func<List<string>, TextWriter> RejectErrAll = events => new EventWriter("err", events, null, rejectAll: true, () => new InvalidOperationException("p7-30 stderr rejects the write"));
            var stdouts = new (string Label, Func<List<string>, TextWriter> Writer)[]
            {
                ("IOException", events => new EventWriter("out", events, null, rejectAll: true, () => new IOException(StdoutMessage))),
                ("InvalidOperationException", events => new EventWriter("out", events, null, rejectAll: true, () => new InvalidOperationException(StdoutMessage))),
                ("disposed StringWriter", _ => { var closed = new StringWriter(CultureInfo.InvariantCulture); closed.Dispose(); return closed; }),
            };
            bool NoMessage((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r, string message) =>
                !string.Join("\n", r.Events).Contains(message, StringComparison.Ordinal) && (r.Out is null || !r.Out.Text.Contains(message, StringComparison.Ordinal));
            bool Contained((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r) =>
                r.Thrown is null && r.Restored && r.Exit == 1 && AllNull();
            // The stdout lines attempted: every rejected line for an EventWriter; unknown (null) for a disposed StringWriter.
            bool Attempted((Exception? Thrown, List<string> Events, int Exit, EventWriter? Out, EventWriter? Err, bool Restored) r, params string[] lines) =>
                r.Out is null || (r.Out.RejectedLines.SequenceEqual(lines) && r.Out.Text.Length == 0 && r.Out.Partial.Length == 0);

            // Typed refusals: the real process bytes first (accepting writers in process must equal them), then a throwing stdout.
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-prototypes", request }),
                ("output extension", output => new[] { "generate-prototypes", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-prototypes", request, existing }),
                ("missing request", output => new[] { "generate-prototypes", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-prototypes", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-prototypes", Input("pack.json", good[..^1] + ",\"pack\":\"xx.no-such.pack\"}"), output }),
            };
            bool NoFile(string output) => !File.Exists(output) && !Directory.Exists(output) && !File.Exists(output + ".txt");
            foreach (var (label, argsOf) in refusals)
            {
                var processOut = Output();
                var theirs = cli(argsOf(processOut));
                var a = Output();
                var control = Forced(AcceptOut, RejectErrAll, null, argsOf(a));
                Check(theirs.Exit == 1 && theirs.Err.Length == 0 && theirs.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !theirs.Out.Contains("internal_error", StringComparison.Ordinal)
                    && Contained(control) && control.Err is { Rejected: 0 } && control.Out is { } co && co.Text == theirs.Out + Environment.NewLine && NoFile(processOut) && NoFile(a),
                    "control: the " + label + " refusal with accepting stdout equals the real process bytes, stderr never written", D(control) + " vs process exit " + theirs.Exit + " " + theirs.Out + " " + theirs.Err);
                foreach (var (writer, stdout) in stdouts)
                {
                    var b = Output();
                    var r = Forced(stdout, AcceptErr, null, argsOf(b));
                    Check(Contained(r) && Attempted(r, theirs.Out) && r.Events.Count == 0 && NoMessage(r, StdoutMessage) && NoFile(b),
                        "the " + label + " refusal + stdout " + writer + " on the typed line: nothing escapes Run, exit 1, typed line attempted once, stderr unwritten, no file", D(r));
                }
            }
            Check(File.ReadAllText(existing) == "keep", "the existing output is left untouched");

            // The guarded block's Refusal(InternalError) (GuardedWorkProbe, accepting stderr): the diagnostic once, then Fail's line.
            {
                var output = Output();
                var control = Forced(AcceptOut, AcceptErr, () => throw failure, new[] { "generate-prototypes", request, output });
                Check(Contained(control) && control.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic, "out:" + envelope.Line }) && !File.Exists(output),
                    "control: GuardedWorkProbe with accepting writers is the diagnostic once, then the typed line, exit 1 (P3-20 bytes)", D(control));
            }
            foreach (var (writer, stdout) in stdouts)
            {
                var output = Output();
                var r = Forced(stdout, AcceptErr, () => throw failure, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && Attempted(r, envelope.Line) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage) && !File.Exists(output),
                    "GuardedWorkProbe Refusal(InternalError) + accepting stderr + stdout " + writer + " on the typed line: nothing escapes Run, exit 1, diagnostic once, typed line attempted once, no file", D(r));
            }

            // A pipeline refusal (Execute's RefusedLine) whose stdout write throws: reported as the internal error (pinned).
            {
                var processOut = Output();
                var theirs = cli(new[] { "generate-prototypes", refusedRequest, processOut });
                Check(theirs.Exit == 1 && theirs.Err.Length == 0 && theirs.Out.StartsWith("{\"status\":\"failed\",\"code\":\"generate_prototypes_refused\",\"refusals\":[", StringComparison.Ordinal) && !File.Exists(processOut),
                    "fixture: the pipeline refusal of the real process is the generate_prototypes_refused line, exit 1, no stderr, no file", "exit " + theirs.Exit + " " + theirs.Out + " " + theirs.Err);
                var a = Output();
                var control = Forced(AcceptOut, RejectErrAll, null, new[] { "generate-prototypes", refusedRequest, a });
                Check(Contained(control) && control.Err is { Rejected: 0 } && control.Out is { } co && co.Text == theirs.Out + Environment.NewLine && !File.Exists(a),
                    "control: the pipeline refusal with accepting stdout equals the real process bytes, stderr never written", D(control));
                foreach (var (writer, stdout, error) in new (string, Func<List<string>, TextWriter>, Exception)[]
                {
                    ("IOException", stdouts[0].Writer, new IOException(StdoutMessage)),
                    ("InvalidOperationException", stdouts[1].Writer, new InvalidOperationException(StdoutMessage)),
                })
                {
                    var output = Output();
                    var r = Forced(stdout, AcceptErr, null, new[] { "generate-prototypes", refusedRequest, output });
                    var diagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-prototypes", error);
                    Check(Contained(r) && Attempted(r, theirs.Out, envelope.Line) && r.Events.SequenceEqual(new[] { "err:" + diagnostic }) && !File.Exists(output),
                        "the pipeline refusal + stdout " + writer + ": reported as the internal error (pinned): the refused line then the typed internal-error line attempted once each, stderr the stdout exception's diagnostic once, exit 1, nothing escapes, no file", D(r));
                }
                {
                    var output = Output();
                    var r = Forced(stdouts[2].Writer, AcceptErr, null, new[] { "generate-prototypes", refusedRequest, output });
                    Check(Contained(r) && r.Events.Count == 1 && r.Events[0].StartsWith("err:" + DiagnosticPrefix + "System.ObjectDisposedException: ", StringComparison.Ordinal) && !File.Exists(output),
                        "the pipeline refusal + a disposed stdout: reported as the internal error, stderr the ObjectDisposedException diagnostic once, exit 1, nothing escapes, no file", D(r));
                }
            }

            // Static: the pipeline refusal's write is the unguarded Console.WriteLine(refused) in Execute (so a throwing stdout
            // there reaches Run's catch-all, as pinned above).
            var write = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateWrite.cs")), blankStrings: false);
            var refusedWrite = System.Text.RegularExpressions.Regex.Matches(write,
                @"if\s*\(\s*generated\s*\.\s*RefusedLine\s+is\s*\{\s*\}\s*refused\s*\)\s*\{\s*Console\s*\.\s*WriteLine\s*\(\s*refused\s*\)\s*;\s*Environment\s*\.\s*ExitCode\s*=\s*1\s*;\s*return\s*;\s*\}").Count;
            Check(refusedWrite == 1, "GenerateWrite.cs: Execute writes the pipeline refusal unguarded (Console.WriteLine(refused); Environment.ExitCode = 1; return;) once (pinned)",
                "matches " + refusedWrite.ToString(CultureInfo.InvariantCulture));

            // Success with accepting writers equals the real process (file and stdout); stderr is never written.
            foreach (var (label, requestPath, pin) in new (string, string, string?)[] { ("explicit project id", request, pinned), ("derived project id", derivedRequest, null) })
            {
                var inOut = Output();
                var r = Forced(AcceptOut, RejectErrAll, null, new[] { "generate-prototypes", requestPath, inOut });
                var processOut = Output();
                var process = cli(new[] { "generate-prototypes", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && r.Err is { Rejected: 0 } && r.Out is { } o && o.Text == process.Out + Environment.NewLine && process.Exit == 0 && process.Err.Length == 0
                    && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && inBytes.Length > 0 && (pin is null || Hex(inBytes) == pin) && inBytes.SequenceEqual(processBytes),
                    "the " + label + " success writes no stderr and equals the real process's file and stdout", D(r) + " vs process " + process.Out + " " + process.Err);
            }
            Check(AllNull(), "the seams are null after the section");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // P7-32: a stdout that accepts a budget of characters and then throws. Each WriteLine(string) / Write call is recorded
    // in Attempts (the full value), the accepted prefix of value + NewLine is appended to Text, and when the budget runs
    // out inside a write the given exception is thrown (counted in Rejected). With perLine the budget restarts at every
    // write (each line gets the first budget characters); otherwise it is one budget for the whole run (a pipe that broke
    // stays broken, so every later write throws with nothing accepted).
    sealed class PartialWriter(int budget, bool perLine, Func<Exception> failure) : TextWriter
    {
        readonly StringBuilder text = new();
        readonly int each = budget;
        readonly bool restart = perLine;
        int left = budget;
        public List<string> Attempts { get; } = new();
        public int Rejected { get; private set; }
        public string Text => text.ToString();
        public override Encoding Encoding => new UTF8Encoding(false);
        void Take(string value)
        {
            if (restart) left = each;
            var accepted = Math.Min(left, value.Length);
            text.Append(value, 0, accepted);
            left -= accepted;
            if (accepted < value.Length) { Rejected++; throw failure(); }
        }
        public override void Write(char value) { Attempts.Add(value.ToString()); Take(value.ToString()); }
        public override void Write(string? value) { Attempts.Add(value ?? ""); Take(value ?? ""); }
        // P7-36: one attempt each (WriteLine() recorded as "", Write(char[], int, int) as the slice; Write(char[]) reaches it
        // through TextWriter), taken from the budget as one value instead of TextWriter's per-char fallback (section 14).
        public override void Write(char[] buffer, int index, int count) { var slice = new string(buffer, index, count); Attempts.Add(slice); Take(slice); }
        public override void WriteLine() { Attempts.Add(""); Take(NewLine); }
        public override void WriteLine(string? value) { Attempts.Add(value ?? ""); Take((value ?? "") + NewLine); }
    }

    // P7-32: a stdout that records every write it is asked for (Attempts, the full value) and then hands it to inner (here a
    // disposed StringWriter, which throws ObjectDisposedException), so a real closed writer's failure is observed together
    // with the attempt instead of a null Out. P7-34: WriteLine() (recorded as "", the line's content like WriteLine("")) and
    // Write(char[], int, int) (recorded as the slice; Write(char[]) reaches it through TextWriter) are one attempt each and
    // forwarded as the same overload, instead of TextWriter's per-char fallback (section 13 self-tests them).
    sealed class CountingWriter(TextWriter inner) : TextWriter
    {
        public List<string> Attempts { get; } = new();
        public override Encoding Encoding => new UTF8Encoding(false);
        public override void Write(char value) { Attempts.Add(value.ToString()); inner.Write(value); }
        public override void Write(string? value) { Attempts.Add(value ?? ""); inner.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Attempts.Add(new string(buffer, index, count)); inner.Write(buffer, index, count); }
        public override void WriteLine() { Attempts.Add(""); inner.WriteLine(); }
        public override void WriteLine(string? value) { Attempts.Add(value ?? ""); inner.WriteLine(value); }
    }

    // P7-32 / P7-36: the one in-process forced GenerateCommand.Run of sections 12 and 13 (run by reflection from the Release
    // CLI; seams in the order PostWriteStageProbe, PostWriteDeleteProbe, UnguardedStageProbe, GuardedWorkProbe): stdout
    // replaced by the given writer and stderr by an accepting EventWriter, UnguardedStageProbe = before, GuardedWorkProbe =
    // work, PostWriteStageProbe = stage; the seams reset, Console and the exit code restored in finally. Returns what Run
    // let escape (the TargetInvocationException's inner exception), the stderr events, the exit code, the stdout writer
    // and whether Console and the exit code were restored.
    static (Exception? Thrown, List<string> Events, int Exit, TextWriter Out, bool Restored) ForcedRun(System.Reflection.MethodInfo run, System.Reflection.PropertyInfo[] seams,
        TextWriter stdout, Action? before, Action? work, Action? stage, string[] args)
    {
        var events = new List<string>();
        var stderr = new EventWriter("err", events);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousExit = Environment.ExitCode;
        Exception? thrown = null;
        int exit;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Environment.ExitCode = 0;
            seams[2].SetValue(null, before);
            seams[3].SetValue(null, work);
            seams[0].SetValue(null, stage);
            try { run.Invoke(null, new object?[] { args }); }
            catch (System.Reflection.TargetInvocationException error) { thrown = error.InnerException; }
            finally { foreach (var seam in seams) seam.SetValue(null, null); }
            exit = Environment.ExitCode;
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.ExitCode = previousExit;
        }
        var restored = ReferenceEquals(Console.Out, previousOut) && ReferenceEquals(Console.Error, previousError) && Environment.ExitCode == previousExit;
        return (thrown, events, exit, stdout, restored);
    }

    // ---- 12. P7-32: a stdout that accepts part of a typed line and then throws: truncated line, exit 1 ----
    // GenerateCommand.Run of the real Release CLI assembly (reflection), in process, stdout replaced by a PartialWriter
    // (or a CountingWriter over a disposed StringWriter) and stderr by the section-9 EventWriter, all restored with the exit
    // code and every seam in a finally. The one documented handling of a partial stdout write: whatever prefix the writer
    // accepted stays on stdout as a truncated line (never terminated by the newline, so never a complete record), nothing
    // escapes Run and the exit code is 1, which is the authoritative signal; no typed line is invented on another stream.
    //   a. catch (Refusal): each typed refusal Fail writes (usage, output extension, output exists, missing request,
    //      malformed JSON, unknown pack; the real process's line L) and the guarded block's Refusal(InternalError)
    //      (GuardedWorkProbe, accepting stderr): with a budget k of 1, half of L and all of L (only the newline lost),
    //      IOException or non-IO InvalidOperationException: stdout is exactly L[..k], L was attempted once and nothing
    //      else, stderr unchanged (nothing; the guarded diagnostic once), exit 1, no file.
    //   b. Catch-all: UnguardedStageProbe (before any write) and PostWriteStageProbe (after the write; P7-26 deletes the
    //      file): stdout exactly the typed internal-error line's first k characters, attempted once, stderr the original
    //      diagnostic once, exit 1, no file.
    //   c. The success line S cut after k characters (Execute's write, then Run's catch-all): with one budget, stdout is
    //      exactly S[..k] (the typed line attempted with nothing accepted), with a per-line budget S[..k] directly followed
    //      by the typed line's first k characters (newline included when it fits); S then the typed line attempted once
    //      each, stderr the stdout exception's diagnostic once, exit 1, the just-written file deleted. k = all of S leaves
    //      the complete success JSON without its newline on stdout while the exit is 1 and no file exists (pinned: the exit
    //      code, not a parse of stdout, decides).
    //   d. The pipeline refusal R (Execute's RefusedLine, unguarded) cut the same way: as c, with R for S (no file).
    //   e. A disposed StringWriter behind a CountingWriter: for a, b, c and d the attempts are exactly the lines above (one
    //      each), nothing reaches stdout, exit 1, nothing escapes, stderr as above (an ObjectDisposedException diagnostic
    //      for c and d).
    //   Controls: a budget large enough for everything gives the real process's success and refusal bytes, stderr unwritten.
    static void PartialWriteStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-32 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string DiagnosticPrefix = "generate-prototypes internal error: ";
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var nl = Environment.NewLine;

        var inputs = Path.Combine(scratch, "p7-32-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            const string good = "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}";
            var request = Input("request.json", good);
            var refusedRequest = Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal));
            var pinned = Hex(expected);

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var postWrite = type?.GetProperty("PostWriteStageProbe", Any);
            var deleteSeam = type?.GetProperty("PostWriteDeleteProbe", Any);
            var unguarded = type?.GetProperty("UnguardedStageProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite, typeof(Action)) && Seam(deleteSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]) and the seams UnguardedStageProbe / PostWriteStageProbe / PostWriteDeleteProbe / GuardedWorkProbe found in the Release CLI");
            if (run is null || postWrite is null || deleteSeam is null || unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo[] seams = { postWrite, deleteSeam, unguarded, guarded };
            bool AllNull() => seams.All(seam => seam.GetValue(null) is null);
            Check(AllNull(), "the generate-prototypes seams are null unless a test sets them");

            // One in-process Run with stdout / stderr replaced and the seams set (P7-36: the shared ForcedRun below).
            (Exception? Thrown, List<string> Events, int Exit, TextWriter Out, bool Restored) Forced(TextWriter stdout, Action? before, Action? work, Action? stage, string[] args) =>
                ForcedRun(runMethod, seams, stdout, before, work, stage, args);
            static List<string> AttemptsOf(TextWriter writer) => writer switch { PartialWriter p => p.Attempts, CountingWriter c => c.Attempts, _ => new List<string>() };
            static string D((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, bool Restored) r) =>
                "stderr [" + string.Join(" | ", r.Events) + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + (r.Out is PartialWriter p ? " stdout text [" + p.Text + "] (" + p.Text.Length.ToString(CultureInfo.InvariantCulture) + " chars) rejected " + p.Rejected.ToString(CultureInfo.InvariantCulture) : "")
                + " attempts [" + string.Join(" | ", AttemptsOf(r.Out)) + "] restored " + r.Restored
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : " threw nothing");
            bool Contained((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, bool Restored) r) => r.Thrown is null && r.Restored && r.Exit == 1 && AllNull();
            static bool Attempted((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, bool Restored) r, params string[] lines) => AttemptsOf(r.Out).SequenceEqual(lines);
            static string TextOf((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, bool Restored) r) => r.Out is PartialWriter p ? p.Text : "";

            var failure = new InvalidOperationException("boom generate-prototypes P7-32 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-prototypes", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == DiagnosticPrefix + "System.InvalidOperationException: boom generate-prototypes P7-32 original",
                "fixture: IntentPlan.Internal(\"generate-prototypes\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            const string StdoutMessage = "p7-32 stdout broke part-way";
            var kinds = new (string Label, Func<Exception> Failure, Exception Sample)[]
            {
                ("IOException", () => new IOException(StdoutMessage), new IOException(StdoutMessage)),
                ("InvalidOperationException", () => new InvalidOperationException(StdoutMessage), new InvalidOperationException(StdoutMessage)),
            };
            // The budgets cut a line of n characters: after 1, after half of it, and after all of it (only the newline lost).
            static int[] Cuts(int n) => new[] { 1, n / 2, n };
            static TextWriter Disposed() { var closed = new StringWriter(CultureInfo.InvariantCulture); closed.Dispose(); return new CountingWriter(closed); }
            bool NoFile(string output) => !File.Exists(output) && !Directory.Exists(output) && !File.Exists(output + ".txt");

            // a. catch (Refusal): the typed refusals of the real process, cut part-way.
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-prototypes", request }),
                ("output extension", output => new[] { "generate-prototypes", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-prototypes", request, existing }),
                ("missing request", output => new[] { "generate-prototypes", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-prototypes", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-prototypes", Input("pack.json", good[..^1] + ",\"pack\":\"xx.no-such.pack\"}"), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var processOut = Output();
                var theirs = cli(argsOf(processOut));
                var line = theirs.Out;
                Check(theirs.Exit == 1 && theirs.Err.Length == 0 && line.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !line.Contains("internal_error", StringComparison.Ordinal) && NoFile(processOut),
                    "fixture: the " + label + " refusal of the real process is a typed line, exit 1, no stderr, no file", "exit " + theirs.Exit + " " + theirs.Out + " " + theirs.Err);
                {
                    var a = Output();
                    var control = Forced(new PartialWriter(int.MaxValue, false, () => new IOException(StdoutMessage)), null, null, null, argsOf(a));
                    Check(Contained(control) && Attempted(control, line) && TextOf(control) == line + nl && control.Events.Count == 0 && NoFile(a),
                        "control: the " + label + " refusal with an unbounded budget equals the real process bytes, stderr unwritten", D(control));
                }
                foreach (var (kind, make, _) in kinds)
                    foreach (var k in Cuts(line.Length))
                    {
                        var b = Output();
                        var r = Forced(new PartialWriter(k, false, make), null, null, null, argsOf(b));
                        Check(Contained(r) && Attempted(r, line) && TextOf(r) == line[..k] && r.Out is PartialWriter { Rejected: 1 } && r.Events.Count == 0 && NoFile(b),
                            "the " + label + " refusal + stdout " + kind + " after " + k.ToString(CultureInfo.InvariantCulture) + " of " + line.Length.ToString(CultureInfo.InvariantCulture)
                            + " chars: stdout the truncated line (no newline), attempted once, nothing escapes Run, exit 1, stderr unwritten, no file", D(r));
                    }
                {
                    var c = Output();
                    var r = Forced(Disposed(), null, null, null, argsOf(c));
                    Check(Contained(r) && Attempted(r, line) && r.Events.Count == 0 && NoFile(c),
                        "the " + label + " refusal + a disposed stdout behind a counting writer: the typed line attempted once, nothing escapes Run, exit 1, stderr unwritten, no file", D(r));
                }
            }
            Check(File.ReadAllText(existing) == "keep", "the existing output is left untouched");

            // a. The guarded block's Refusal(InternalError) (accepting stderr): the diagnostic once, Fail's line cut part-way.
            foreach (var (kind, make, _) in kinds)
                foreach (var k in Cuts(envelope.Line.Length))
                {
                    var output = Output();
                    var r = Forced(new PartialWriter(k, false, make), null, () => throw failure, null, new[] { "generate-prototypes", request, output });
                    Check(Contained(r) && Attempted(r, envelope.Line) && TextOf(r) == envelope.Line[..k] && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                        "GuardedWorkProbe Refusal(InternalError) + stdout " + kind + " after " + k.ToString(CultureInfo.InvariantCulture) + " chars: stdout the truncated typed line, attempted once, diagnostic once, exit 1, nothing escapes, no file", D(r));
                }
            {
                var output = Output();
                var r = Forced(Disposed(), null, () => throw failure, null, new[] { "generate-prototypes", request, output });
                Check(Contained(r) && Attempted(r, envelope.Line) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                    "GuardedWorkProbe Refusal(InternalError) + a disposed stdout behind a counting writer: the typed line attempted once, diagnostic once, exit 1, nothing escapes, no file", D(r));
            }

            // b. Run's catch-all: before any write (UnguardedStageProbe) and after it (PostWriteStageProbe; the file is deleted).
            foreach (var (path, before, stage) in new (string, Action?, Action?)[] { ("UnguardedStageProbe", () => throw failure, null), ("PostWriteStageProbe", null, () => throw failure) })
            {
                foreach (var (kind, make, _) in kinds)
                    foreach (var k in Cuts(envelope.Line.Length))
                    {
                        var output = Output();
                        var r = Forced(new PartialWriter(k, false, make), before, null, stage, new[] { "generate-prototypes", request, output });
                        Check(Contained(r) && Attempted(r, envelope.Line) && TextOf(r) == envelope.Line[..k] && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                            path + " + stdout " + kind + " after " + k.ToString(CultureInfo.InvariantCulture) + " chars: stdout the truncated typed line, attempted once, diagnostic once, exit 1, nothing escapes, no file", D(r));
                    }
                var disposedOutput = Output();
                var d = Forced(Disposed(), before, null, stage, new[] { "generate-prototypes", request, disposedOutput });
                Check(Contained(d) && Attempted(d, envelope.Line) && d.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(disposedOutput),
                    path + " + a disposed stdout behind a counting writer: the typed line attempted once, diagnostic once, exit 1, nothing escapes, no file", D(d));
            }

            // c / d. A line Execute writes (the success line, the pipeline refusal) cut part-way: Run's catch-all follows.
            var success = cli(new[] { "generate-prototypes", request, Output() });
            var refused = cli(new[] { "generate-prototypes", refusedRequest, Output() });
            Check(success.Exit == 0 && success.Err.Length == 0 && success.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal),
                "fixture: the success line of the real process", "exit " + success.Exit + " " + success.Out + " " + success.Err);
            Check(refused.Exit == 1 && refused.Err.Length == 0 && refused.Out.StartsWith("{\"status\":\"failed\",\"code\":\"generate_prototypes_refused\",\"refusals\":[", StringComparison.Ordinal),
                "fixture: the pipeline refusal line of the real process", "exit " + refused.Exit + " " + refused.Out + " " + refused.Err);
            foreach (var (label, requestPath, line) in new (string, string, string)[] { ("success", request, success.Out), ("pipeline refusal", refusedRequest, refused.Out) })
            {
                {
                    var output = Output();
                    var control = Forced(new PartialWriter(int.MaxValue, false, () => new IOException(StdoutMessage)), null, null, null, new[] { "generate-prototypes", requestPath, output });
                    var wrote = File.Exists(output) ? File.ReadAllBytes(output) : null;
                    Check(control.Thrown is null && control.Restored && control.Exit == (label == "success" ? 0 : 1) && Attempted(control, line) && TextOf(control) == line + nl && control.Events.Count == 0
                        && (label == "success" ? wrote is not null && Hex(wrote) == pinned : wrote is null),
                        "control: the " + label + " with an unbounded budget equals the real process bytes (stdout and file), stderr unwritten", D(control));
                    if (File.Exists(output)) File.Delete(output);
                }
                foreach (var (kind, make, sample) in kinds)
                {
                    var diagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-prototypes", sample);
                    foreach (var perLine in new[] { false, true })
                        foreach (var k in perLine ? new[] { 1, line.Length / 2 } : Cuts(line.Length))
                        {
                            var output = Output();
                            byte[]? seen = null;
                            var r = Forced(new PartialWriter(k, perLine, make), null, null, label == "success" ? () => { seen = File.Exists(output) ? File.ReadAllBytes(output) : null; } : null,
                                new[] { "generate-prototypes", requestPath, output });
                            var typedPart = perLine ? (envelope.Line + nl)[..Math.Min(k, envelope.Line.Length + nl.Length)] : "";
                            var budget = (perLine ? "per-line " : "") + k.ToString(CultureInfo.InvariantCulture);
                            Check(Contained(r) && Attempted(r, line, envelope.Line) && TextOf(r) == line[..k] + typedPart && r.Events.SequenceEqual(new[] { "err:" + diagnostic })
                                && !File.Exists(output) && (label != "success" || (seen is not null && Hex(seen) == pinned)),
                                "the " + label + " line + stdout " + kind + " after a budget of " + budget + " of " + line.Length.ToString(CultureInfo.InvariantCulture)
                                + " chars: stdout the truncated line" + (perLine ? " then the typed line's first " + k.ToString(CultureInfo.InvariantCulture) + " chars" : "")
                                + ", the line then the typed line attempted once each, stderr the stdout exception's diagnostic once, exit 1, nothing escapes, no file" + (label == "success" ? " (the written file deleted)" : ""),
                                D(r) + " seen " + (seen is null ? "<none>" : Hex(seen)));
                        }
                }
                {
                    var output = Output();
                    var r = Forced(Disposed(), null, null, null, new[] { "generate-prototypes", requestPath, output });
                    Check(Contained(r) && Attempted(r, line, envelope.Line) && r.Events.Count == 1 && r.Events[0].StartsWith("err:" + DiagnosticPrefix + "System.ObjectDisposedException: ", StringComparison.Ordinal) && !File.Exists(output),
                        "the " + label + " line + a disposed stdout behind a counting writer: the line then the typed line attempted once each, stderr the ObjectDisposedException diagnostic once, exit 1, nothing escapes, no file", D(r));
                }
            }
            Check(AllNull(), "the seams are null after the section");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- 13. P7-34: partial-write contract, no flush/retry of the typed line, full-line per-line cut ----
    //   a. CountingWriter self-test: WriteLine() and Write(char[], int, int) (and Write(char[]) through it) are one attempt
    //      each, forwarded byte-exact to an accepting inner writer, and still recorded before a disposed inner throws.
    //   b. Section 12's per-line budget case also cut after the full line: the success line S (and the pipeline refusal R)
    //      with a per-line budget of all of S: stdout is S without its newline directly followed by the typed internal-error
    //      line with its newline (the writer recovered), S then the typed line attempted once each, stderr the stdout
    //      exception's diagnostic once, exit 1, no file (the success file was written with the pinned bytes, then deleted).
    //      So a complete success JSON appears on stdout while exit is 1: only the exit code is authoritative.
    //   c. Static (comments stripped, strings blanked): no flush and no retry of the typed line after a failed write.
    //      GenerateWrite.cs names no Flush at all; Run calls Fail( once and .Print( once, each as the only statement of a
    //      try whose catch (Exception) { } is empty and which is the last statement of its catch block; Run has no loop,
    //      goto, finally or other Console member than Console.Error; Fail and IntentEnvelope.Print write their line with one
    //      Console.WriteLine( and contain no Flush, loop or try. Accept self-test on the real sources plus mutation
    //      self-tests (flush, retry in the empty catch, a second call, a loop, goto, finally, a filter, a direct write).
    //   d. The contract (docs/contracts/module-contracts.md) states that the exit code, not a parse of stdout, decides.
    static void PartialWriteContractStage(string root, string scratch, string cliDll, byte[] expected, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-34 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var nl = Environment.NewLine;

        // a. CountingWriter overloads.
        {
            var inner = new StringWriter(CultureInfo.InvariantCulture);
            var counting = new CountingWriter(inner);
            counting.WriteLine();
            counting.Write("xabcx".ToCharArray(), 1, 3);
            counting.Write("de".ToCharArray());
            counting.WriteLine("f");
            Check(counting.Attempts.SequenceEqual(new[] { "", "abc", "de", "f" }) && inner.ToString() == inner.NewLine + "abcde" + "f" + inner.NewLine,
                "CountingWriter: WriteLine(), Write(char[], int, int) and Write(char[]) are one attempt each, forwarded byte-exact",
                "attempts [" + string.Join(" | ", counting.Attempts) + "] inner [" + inner + "]");
            var closed = new StringWriter(CultureInfo.InvariantCulture);
            closed.Dispose();
            var disposed = new CountingWriter(closed);
            var thrown = new List<string>();
            foreach (var (label, act) in new (string, Action)[] { ("WriteLine()", () => disposed.WriteLine()), ("Write(char[], int, int)", () => disposed.Write("abc".ToCharArray(), 0, 2)) })
            {
                try { act(); thrown.Add(label + " no exception"); }
                catch (ObjectDisposedException) { thrown.Add(label + " ObjectDisposedException"); }
            }
            Check(disposed.Attempts.SequenceEqual(new[] { "", "ab" }) && thrown.SequenceEqual(new[] { "WriteLine() ObjectDisposedException", "Write(char[], int, int) ObjectDisposedException" }),
                "CountingWriter over a disposed writer: WriteLine() and Write(char[], int, int) are recorded once each, then ObjectDisposedException",
                "attempts [" + string.Join(" | ", disposed.Attempts) + "] " + string.Join(", ", thrown));
        }

        // b. The per-line budget cut after the full line.
        var inputs = Path.Combine(scratch, "p7-34-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            const string good = "{\"typology\":2,\"lot\":{\"width_m\":8.66,\"depth_m\":20},\"alternatives\":2,\"seed\":0,\"project_id\":\"00000000-0000-4000-8000-0000000007a9\"}";
            var request = Input("request.json", good);
            var refusedRequest = Input("refused.json", good.Replace("\"typology\":2", "\"typology\":4", StringComparison.Ordinal));
            var pinned = Hex(expected);

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var seamProperties = new[] { "PostWriteStageProbe", "PostWriteDeleteProbe", "UnguardedStageProbe", "GuardedWorkProbe" }.Select(name => type?.GetProperty(name, Any)).ToArray();
            Check(run is not null && seamProperties.All(p => p is not null && p.CanRead && p.CanWrite), "Run(string[]) and the four seams found in the Release CLI");
            if (run is null || seamProperties.Any(p => p is null)) return;
            System.Reflection.MethodInfo runMethod = run;
            var seams = seamProperties.Select(p => p!).ToArray();
            bool AllNull() => seams.All(seam => seam.GetValue(null) is null);
            Check(AllNull(), "the generate-prototypes seams are null unless a test sets them");

            // P7-36: section 12's ForcedRun (only PostWriteStageProbe set).
            (Exception? Thrown, List<string> Events, int Exit, PartialWriter Out, bool Restored) Forced(PartialWriter stdout, Action? stage, string[] args)
            {
                var r = ForcedRun(runMethod, seams, stdout, null, null, stage, args);
                return (r.Thrown, r.Events, r.Exit, stdout, r.Restored);
            }
            static string D((Exception? Thrown, List<string> Events, int Exit, PartialWriter Out, bool Restored) r) =>
                "stderr [" + string.Join(" | ", r.Events) + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + " stdout text [" + r.Out.Text + "] (" + r.Out.Text.Length.ToString(CultureInfo.InvariantCulture) + " chars) rejected " + r.Out.Rejected.ToString(CultureInfo.InvariantCulture)
                + " attempts [" + string.Join(" | ", r.Out.Attempts) + "] restored " + r.Restored
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : " threw nothing");

            var typed = Bim.Cli.IntentPlan.Internal("generate-prototypes", new InvalidOperationException("p7-34 fixture")).Line;
            Check(typed == "{\"status\":\"failed\",\"code\":\"generate_prototypes_internal_error\"}", "fixture: the typed internal-error line", typed);
            var success = cli(new[] { "generate-prototypes", request, Output() });
            var refused = cli(new[] { "generate-prototypes", refusedRequest, Output() });
            Check(success.Exit == 0 && success.Err.Length == 0 && success.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && success.Out.EndsWith("}", StringComparison.Ordinal),
                "fixture: the success line of the real process is one complete JSON object", "exit " + success.Exit + " " + success.Out + " " + success.Err);
            Check(refused.Exit == 1 && refused.Err.Length == 0 && refused.Out.StartsWith("{\"status\":\"failed\",\"code\":\"generate_prototypes_refused\",\"refusals\":[", StringComparison.Ordinal),
                "fixture: the pipeline refusal line of the real process", "exit " + refused.Exit + " " + refused.Out + " " + refused.Err);
            const string StdoutMessage = "p7-34 stdout lost the newline";
            foreach (var (label, requestPath, line) in new (string, string, string)[] { ("success", request, success.Out), ("pipeline refusal", refusedRequest, refused.Out) })
                foreach (var (kind, make, sample) in new (string, Func<Exception>, Exception)[]
                {
                    ("IOException", () => new IOException(StdoutMessage), new IOException(StdoutMessage)),
                    ("InvalidOperationException", () => new InvalidOperationException(StdoutMessage), new InvalidOperationException(StdoutMessage)),
                })
                {
                    var output = Output();
                    byte[]? seen = null;
                    var r = Forced(new PartialWriter(line.Length, true, make), label == "success" ? () => { seen = File.Exists(output) ? File.ReadAllBytes(output) : null; } : null,
                        new[] { "generate-prototypes", requestPath, output });
                    var typedPart = (typed + nl)[..Math.Min(line.Length, typed.Length + nl.Length)];
                    var diagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-prototypes", sample);
                    Check(r.Thrown is null && r.Restored && r.Exit == 1 && AllNull() && r.Out.Attempts.SequenceEqual(new[] { line, typed }) && r.Out.Rejected == 1
                        && r.Out.Text == line + typedPart && r.Events.SequenceEqual(new[] { "err:" + diagnostic })
                        && !File.Exists(output) && (label != "success" || (seen is not null && Hex(seen) == pinned)),
                        "the " + label + " line + stdout " + kind + " after a per-line budget of all " + line.Length.ToString(CultureInfo.InvariantCulture)
                        + " chars: stdout the whole line without its newline then the typed line's first " + typedPart.Length.ToString(CultureInfo.InvariantCulture)
                        + " chars, the line then the typed line attempted once each, stderr the stdout exception's diagnostic once, exit 1, nothing escapes, no file"
                        + (label == "success" ? " (the written file deleted)" : ""), D(r) + " seen " + (seen is null ? "<none>" : Hex(seen)));
                    if (label == "success")
                        Check(r.Out.Text.StartsWith(success.Out, StringComparison.Ordinal) && typedPart == typed + nl,
                            "pinned: a complete success JSON object is on stdout (the typed line follows on the same line) while exit is 1 and no file exists: the exit code decides", D(r));
                }
            Check(AllNull(), "the seams are null after the section");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }

        // c. Static no flush / no retry pin with accept and mutation self-tests.
        var write = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateWrite.cs")));
        var plan = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "IntentPlan.cs")));
        var real = NoRetryProblems(write, plan);
        Check(real.Count == 0, "static: GenerateWrite.cs Run / Fail and IntentEnvelope.Print never flush or retry the typed line after a failed write", string.Join("; ", real));
        const string FailTry = @"try\s*\{\s*Fail\s*\(\s*refusal\s*\.\s*Code\s*,\s*refusal\s*\.\s*Detail\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}";
        const string PrintTry = @"try\s*\{\s*\(\s*internalError\s+with\s*\{\s*Diagnostic\s*=\s*null\s*\}\s*\)\s*\.\s*Print\s*\(\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}";
        const string FailWrite = @"Console\s*\.\s*WriteLine\s*\(\s*FailureLine\s*\(\s*code\s*,\s*detail\s*\)\s*\)\s*;";
        const string PrintWrite = @"Console\s*\.\s*WriteLine\s*\(\s*Line\s*\)\s*;";
        const string FailCall = "try { Fail(refusal.Code, refusal.Detail); }";
        const string PrintCall = "try { (internalError with { Diagnostic = null }).Print(); }";
        var mutations = new (string Label, bool Plan, string Pattern, string Replacement)[]
        {
            ("a flush in the empty catch after Fail", false, FailTry, FailCall + " catch (Exception) { Console.Out.Flush(); }"),
            ("a retry of Fail in its catch", false, FailTry, FailCall + " catch (Exception) { Fail(refusal.Code, refusal.Detail); }"),
            ("a second Fail call after the guarded one", false, FailTry, FailCall + " catch (Exception) { } Fail(refusal.Code, refusal.Detail);"),
            ("a flush statement after the guarded Fail", false, FailTry, FailCall + " catch (Exception) { } Console.Out.Flush();"),
            ("a for retry loop around the guarded Fail", false, FailTry, "for (var attempt = 0; attempt < 2; attempt++) { " + FailCall + " catch (Exception) { } }"),
            ("a goto retry of the guarded Fail", false, FailTry, "var tries = 0; again: " + FailCall + " catch (Exception) { if (++tries < 2) goto again; }"),
            ("a filtered catch after Fail", false, FailTry, FailCall + " catch (Exception) when (Retry()) { }"),
            ("a newline written in the catch after Fail", false, FailTry, FailCall + " catch (Exception) { Console.Out.WriteLine(); }"),
            ("a retry of Print in its catch", false, PrintTry, PrintCall + " catch (Exception) { (internalError with { Diagnostic = null }).Print(); }"),
            ("a direct write of the typed line in the catch after Print", false, PrintTry, PrintCall + " catch (Exception) { Console.WriteLine(internalError.Line); }"),
            ("a flush in the empty catch after Print", false, PrintTry, PrintCall + " catch (Exception) { Console.Out.FlushAsync(); }"),
            ("a flush statement after the guarded Print", false, PrintTry, PrintCall + " catch (Exception) { } Console.Out.Flush();"),
            ("a while retry loop around the guarded Print", false, PrintTry, "var left = 2; while (left-- > 0) { " + PrintCall + " catch (Exception) { } }"),
            ("a finally flushing stdout", false, PrintTry, PrintCall + " catch (Exception) { } finally { Console.Out.Flush(); }"),
            ("a re-run of Execute in the catch after Print", false, PrintTry, PrintCall + " catch (Exception) { Execute(args); }"),
            ("a second Print call after the guarded one", false, PrintTry, PrintCall + " catch (Exception) { } internalError.Print();"),
            ("a flush in Fail", false, FailWrite, "Console.WriteLine(FailureLine(code, detail)); Console.Out.Flush();"),
            ("a guarded retry in Fail", false, FailWrite, "try { Console.WriteLine(FailureLine(code, detail)); } catch (IOException) { Console.WriteLine(FailureLine(code, detail)); }"),
            ("a flush anywhere in GenerateWrite.cs (Execute's success line)", false, @"PostWriteStageProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;", "PostWriteStageProbe?.Invoke(); Console.Out.Flush();"),
            ("AutoFlush set in GenerateWrite.cs", false, @"var\s+output\s*=\s*args\s*\[\s*2\s*\]\s*;", "var output = args[2]; ((StreamWriter)Console.Out).AutoFlush = true;"),
            ("a flush in IntentEnvelope.Print", true, PrintWrite, "Console.WriteLine(Line); Console.Out.Flush();"),
            ("a retry loop in IntentEnvelope.Print", true, PrintWrite, "for (var i = 0; i < 2; i++) { try { Console.WriteLine(Line); break; } catch (IOException) { } }"),
            ("a second write in IntentEnvelope.Print", true, PrintWrite, "Console.WriteLine(Line); Console.WriteLine(Line);"),
        };
        foreach (var (label, onPlan, pattern, replacement) in mutations)
        {
            var target = onPlan ? plan : write;
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            var found = regex.Matches(target).Count;
            var mutated = regex.Replace(target, replacement.Replace("$", "$$", StringComparison.Ordinal), 1);
            var problems = onPlan ? NoRetryProblems(write, mutated) : NoRetryProblems(mutated, plan);
            Check(found == 1 && mutated != target && problems.Count > 0, "static self-test: " + label + " is rejected",
                "anchor matches " + found.ToString(CultureInfo.InvariantCulture) + "; problems [" + string.Join("; ", problems) + "]");
        }

        // d. The contract wording.
        var contracts = File.ReadAllText(Path.Combine(root, "docs", "contracts", "module-contracts.md"));
        var heading = contracts.IndexOf("## generate-prototypes partial stdout write (P7-32)", StringComparison.Ordinal);
        var next = heading < 0 ? -1 : contracts.IndexOf("\n## ", heading + 1, StringComparison.Ordinal);
        var sectionText = heading < 0 ? "" : next < 0 ? contracts[heading..] : contracts[heading..next];
        Check(sectionText.Contains("the exit code, not a parse of stdout, is authoritative", StringComparison.Ordinal)
            && sectionText.Contains("never flushed, completed or retried", StringComparison.Ordinal),
            "docs/contracts/module-contracts.md: the generate-prototypes partial-write section states that the exit code, not a parse of stdout, is authoritative and that the typed line is never flushed, completed or retried",
            sectionText.Length == 0 ? "section not found" : sectionText);
    }

    // P7-34: over the comment-stripped, string-blanked GenerateWrite.cs (write) and IntentPlan.cs (plan): the problems that
    // would let generate-prototypes flush, complete or retry its typed stdout line after a failed write (empty = pinned).
    static List<string> NoRetryProblems(string write, string plan)
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
        static string? Body(string code, string header)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(code, header);
            if (matches.Count != 1) return null;
            var open = matches[0].Index + matches[0].Length - 1;
            var close = Close(code, open);
            return close < 0 ? null : code[open..(close + 1)];
        }
        static int Count(string code, string pattern) => System.Text.RegularExpressions.Regex.Matches(code, pattern).Count;
        const string Loop = @"\b(?:for|foreach|while|do|goto)\b";
        var problems = new List<string>();
        if (Count(write, @"\b(?:Auto)?Flush(?:Async)?\b") != 0) problems.Add("GenerateWrite.cs names Flush");
        var run = Body(write, @"\bpublic\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)\s*\{");
        if (run is null) problems.Add("Run(string[] args) not found once");
        else
        {
            if (Count(run, @"\bFail\s*\(") != 1) problems.Add("Run calls Fail( other than once");
            if (Count(run, @"\.\s*Print\s*\(") != 1) problems.Add("Run calls .Print( other than once");
            if (Count(run, Loop) != 0) problems.Add("Run has a loop or goto");
            if (Count(run, @"\bfinally\b") != 0) problems.Add("Run has a finally");
            if (Count(run, @"\bwhen\b") != 0) problems.Add("Run has an exception filter");
            if (Count(run, @"\bConsole\s*\.(?!\s*Error\b)") != 0) problems.Add("Run uses a Console member other than Console.Error");
            if (Count(run, @"\bExecute\s*\(") != 1) problems.Add("Run calls Execute( other than once");
            // Each guarded write is the only statement of its try, its catch (Exception) { } is empty, and it is the last
            // statement of the enclosing catch block (the next token closes that block).
            if (Count(run, @"try\s*\{\s*Fail\s*\(\s*refusal\s*\.\s*Code\s*,\s*refusal\s*\.\s*Detail\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}") != 1)
                problems.Add("Fail( is not the last, guarded statement of catch (Refusal) with an empty catch (Exception) { }");
            if (Count(run, @"try\s*\{\s*\(\s*internalError\s+with\s*\{\s*Diagnostic\s*=\s*null\s*\}\s*\)\s*\.\s*Print\s*\(\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}\s*\}$") != 1)
                problems.Add(".Print( is not the last, guarded statement of the catch-all (and of Run) with an empty catch (Exception) { }");
        }
        var fail = Body(write, @"\bstatic\s+void\s+Fail\s*\(\s*string\s+code\s*,\s*string\s*\?\s*detail\s*\)\s*\{");
        if (fail is null) problems.Add("Fail(string code, string? detail) not found once");
        else
        {
            if (Count(fail, @"\bConsole\s*\.") != 1 || Count(fail, @"\bConsole\s*\.\s*WriteLine\s*\(\s*FailureLine\s*\(") != 1) problems.Add("Fail does not write its line with exactly one Console.WriteLine(FailureLine(");
            if (Count(fail, Loop) != 0 || Count(fail, @"\b(?:try|catch|finally)\b") != 0) problems.Add("Fail has a loop, goto or try");
        }
        var print = Body(plan, @"\bpublic\s+void\s+Print\s*\(\s*\)\s*\{");
        if (print is null) problems.Add("IntentEnvelope.Print() not found once");
        else
        {
            if (Count(print, @"\bConsole\s*\.(?!\s*Error\b)") != 1 || Count(print, @"\bConsole\s*\.\s*WriteLine\s*\(\s*Line\s*\)") != 1) problems.Add("Print does not write stdout with exactly one Console.WriteLine(Line)");
            if (Count(print, @"\b(?:Auto)?Flush(?:Async)?\b") != 0) problems.Add("Print names Flush");
            if (Count(print, Loop) != 0 || Count(print, @"\b(?:try|catch|finally)\b") != 0) problems.Add("Print has a loop, goto or try");
        }
        return problems;
    }

    // ---- 14. P7-36: PartialWriter overloads, one Forced helper, no retry of Execute's lines ----
    //   a. PartialWriter self-test: WriteLine() (recorded as "", the newline taken from the budget) and Write(char[], int,
    //      int) (recorded as the slice; Write(char[]) reaches it through TextWriter) are one attempt each, taken from the
    //      budget as one value (one rejection when cut, the accepted prefix kept), with one budget and with a per-line one.
    //   b. Sections 12 and 13 run GenerateCommand.Run through the one shared ForcedRun helper (no Console.SetOut of their own).
    //   c. Static (comments stripped, strings blanked): Execute writes the pipeline refusal line once (unguarded, then exit
    //      1 and return) and the success line once (the last statement of its try, whose catch (Exception) deletes the file,
    //      reports a failed delete on stderr and rethrows, and is the end of Execute); Execute has no other stdout member,
    //      loop, goto, finally, extra filter or lambda, never calls itself, and runs Generate and WriteNew once. Accept
    //      self-test on the real source plus mutation self-tests (second write, guarded retry, completing newline, a write on
    //      stderr, loop, lambda, goto, rewrite in the cleanup catch, re-run of Execute, filter, finally, second pipeline run).
    static void PartialWriterExecuteStage(string root, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-36 " + name, detail);
        var nl = Environment.NewLine;
        static string W(PartialWriter p) => "attempts [" + string.Join(" | ", p.Attempts) + "] text [" + p.Text + "] rejected " + p.Rejected.ToString(CultureInfo.InvariantCulture);

        // a. PartialWriter overloads.
        {
            var open = new PartialWriter(int.MaxValue, false, () => new IOException("p7-36 never"));
            open.WriteLine();
            open.Write("xabcx".ToCharArray(), 1, 3);
            open.Write("de".ToCharArray());
            open.WriteLine("f");
            Check(open.Attempts.SequenceEqual(new[] { "", "abc", "de", "f" }) && open.Text == nl + "abcdef" + nl && open.Rejected == 0,
                "PartialWriter unbounded: WriteLine(), Write(char[], int, int) and Write(char[]) are one attempt each, accepted byte-exact", W(open));

            var cut = new PartialWriter(nl.Length + 2, false, () => new IOException("p7-36 cut"));
            var outcomes = new List<string>();
            foreach (var (label, act) in new (string, Action)[]
            {
                ("WriteLine()", () => cut.WriteLine()),
                ("Write(char[], int, int)", () => cut.Write("zabcdz".ToCharArray(), 1, 4)),
                ("WriteLine() after", () => cut.WriteLine()),
            })
            {
                try { act(); outcomes.Add(label + " accepted"); }
                catch (IOException error) when (error.Message == "p7-36 cut") { outcomes.Add(label + " IOException"); }
            }
            Check(cut.Attempts.SequenceEqual(new[] { "", "abcd", "" }) && cut.Text == nl + "ab" && cut.Rejected == 2
                && outcomes.SequenceEqual(new[] { "WriteLine() accepted", "Write(char[], int, int) IOException", "WriteLine() after IOException" }),
                "PartialWriter one budget: WriteLine() takes the newline from the budget, Write(char[], int, int) keeps its accepted prefix and throws once, a later WriteLine() throws with nothing accepted",
                W(cut) + " " + string.Join(", ", outcomes));

            var perLine = new PartialWriter(2, true, () => new InvalidOperationException("p7-36 per line"));
            var perLineOutcomes = new List<string>();
            foreach (var (label, act) in new (string, Action)[]
            {
                ("WriteLine()", () => perLine.WriteLine()),
                ("Write(char[], int, int)", () => perLine.Write("xyz".ToCharArray(), 0, 3)),
                ("Write(char[])", () => perLine.Write("pq".ToCharArray())),
            })
            {
                try { act(); perLineOutcomes.Add(label + " accepted"); }
                catch (InvalidOperationException error) when (error.Message == "p7-36 per line") { perLineOutcomes.Add(label + " InvalidOperationException"); }
            }
            Check(perLine.Attempts.SequenceEqual(new[] { "", "xyz", "pq" }) && perLine.Text == nl + "xy" + "pq" && perLine.Rejected == 1
                && perLineOutcomes.SequenceEqual(new[] { "WriteLine() accepted", "Write(char[], int, int) InvalidOperationException", "Write(char[]) accepted" }),
                "PartialWriter per-line budget: each WriteLine() / Write(char[], int, int) / Write(char[]) restarts the budget once, a cut slice throws once",
                W(perLine) + " " + string.Join(", ", perLineOutcomes));
        }

        // b. One Forced helper for sections 12 and 13 (P7-38: ForcedRunCopyProblems, mutation self-tests in section 15).
        {
            var tests = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "tests", "cli", "GeneratePrototypesTests.cs")));
            var oneCopy = ForcedRunCopyProblems(tests);
            Check(oneCopy.Count == 0, "sections 12 and 13 share the one ForcedRun helper (defined once, called once by each, no Console.SetOut of their own)", string.Join("; ", oneCopy));
        }

        // c. Static: Execute never retries or writes again the success line or the pipeline refusal line.
        var write = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateWrite.cs")));
        var command = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateCommand.cs")));
        var followed = ExecuteFollowedCode(root);
        var real = ExecuteWriteProblems(write, command, followed);
        Check(real.Count == 0, "static: GenerateWrite.cs Execute writes the success line and the pipeline refusal line once each, never retried, completed or written again", string.Join("; ", real));
        foreach (var (label, pattern, replacement) in ExecuteMutants())
        {
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            var found = regex.Matches(write).Count;
            var mutated = regex.Replace(write, replacement.Replace("$", "$$", StringComparison.Ordinal), 1);
            var problems = ExecuteWriteProblems(mutated, command, followed);
            Check(found == 1 && mutated != write && problems.Count > 0, "static self-test: " + label + " is rejected",
                "anchor matches " + found.ToString(CultureInfo.InvariantCulture) + "; problems [" + string.Join("; ", problems) + "]");
        }
    }

    // P7-36 (moved out of section 14 by P7-38, unchanged but for the trailing comma of the success anchor, now optional):
    // the 16 mutations of GenerateWrite.cs that ExecuteWriteProblems must reject, each by one regex anchor matching once.
    static (string Label, string Pattern, string Replacement)[] ExecuteMutants()
    {
        const string RefusedWrite = @"Console\s*\.\s*WriteLine\s*\(\s*refused\s*\)\s*;";
        const string CleanupCatch = @"catch\s*\(\s*Exception\s*\)\s*\{\s*try\s*\{\s*PostWriteDeleteProbe";
        const string SuccessEnd = @"sha256\s*,?\s*\}\s*\)\s*\)\s*;";
        return new (string Label, string Pattern, string Replacement)[]
        {
            ("a second write of the refusal line", RefusedWrite, "Console.WriteLine(refused); Console.WriteLine(refused);"),
            ("a guarded retry of the refusal line", RefusedWrite, "try { Console.WriteLine(refused); } catch (IOException) { Console.WriteLine(refused); }"),
            ("a newline completing the refusal line", RefusedWrite, "Console.WriteLine(refused); Console.Out.WriteLine();"),
            ("the refusal line written on stderr too", RefusedWrite, "Console.WriteLine(refused); Console.Error.WriteLine(refused);"),
            ("a loop writing the refusal line", RefusedWrite, "for (var i = 0; i < 2; i++) Console.WriteLine(refused);"),
            ("a lambda writing the refusal line twice", RefusedWrite, "Action again = () => Console.WriteLine(refused); again(); again();"),
            ("a goto retry of the refusal line", RefusedWrite, "var tries = 0; again: Console.WriteLine(refused); if (++tries < 2) goto again;"),
            ("the success line written again in the cleanup catch", CleanupCatch, "catch (Exception) { Console.WriteLine(JsonSerializer.Serialize(new { status = 0 })); try { PostWriteDeleteProbe"),
            ("a re-run of Execute in the cleanup catch", CleanupCatch, "catch (Exception) { Execute(args); try { PostWriteDeleteProbe"),
            ("a filtered retry catch on the success write", CleanupCatch, "catch (Exception) when (Retry()) { try { PostWriteDeleteProbe"),
            ("a goto retry of the success write", CleanupCatch, "catch (Exception) { if (Retry()) goto again; try { PostWriteDeleteProbe"),
            ("a newline completing the success line", SuccessEnd, "sha256, })); Console.Out.Write(Environment.NewLine);"),
            ("a second success write after the first", SuccessEnd, "sha256, })); Console.WriteLine(JsonSerializer.Serialize(new { sha256 }));"),
            ("a loop around the success try", @"try\s*\{\s*PostWriteStageProbe", "for (var attempt = 0; attempt < 2; attempt++) try { PostWriteStageProbe"),
            ("a finally completing the success line", @"throw\s*;", "throw; } finally { Console.Out.WriteLine();"),
            ("a second pipeline run", @"var\s+generated\s*=\s*Generate\s*\(\s*ReadRequest\s*\(\s*args\s*\[\s*1\s*\]\s*\)\s*\)\s*;",
                "var generated = Generate(ReadRequest(args[1])); if (generated.RefusedLine is null) generated = Generate(ReadRequest(args[1]));"),
        };
    }

    // P7-42: the problem (a substring of one of ExecuteWriteProblems' problems) each ExecuteMutants mutant must be
    // rejected for (section 15 c); a label without one is a failure.
    static string ExecuteMutantReason(string label) => label switch
    {
        "a second write of the refusal line" or "a guarded retry of the refusal line" => "Execute does not write the refusal line with exactly one Console.WriteLine(refused)",
        "a newline completing the refusal line" => "Execute uses a stdout Console member other than twice",
        "the refusal line written on stderr too" => "Execute writes stderr other than the one PartialOutputLeft line",
        "a loop writing the refusal line" or "a goto retry of the refusal line" or "a goto retry of the success write" or "a loop around the success try" => "Execute has a loop or goto",
        "a lambda writing the refusal line twice" => "Execute has a lambda other than the accepted count",
        "the success line written again in the cleanup catch" or "a second success write after the first" => "Execute does not write the success line with exactly one Console.WriteLine(JsonSerializer.Serialize(new {",
        "a re-run of Execute in the cleanup catch" => "Execute calls itself",
        "a filtered retry catch on the success write" => "Execute has an exception filter other than the WriteNew IO one",
        "a newline completing the success line" => "the success line is not the last statement of its try",
        "a finally completing the success line" => "Execute has a finally",
        "a second pipeline run" => "Execute runs Generate( other than once",
        _ => "<no reason listed for " + label + ">",
    };

    // P7-36 / P7-38: over the comment-stripped, string-blanked GeneratePrototypesTests.cs: the problems that would let
    // section 12 or 13 replace stdout other than through the one shared ForcedRun helper (empty = pinned).
    static List<string> ForcedRunCopyProblems(string tests)
    {
        static int Count(string code, string pattern) => System.Text.RegularExpressions.Regex.Matches(code, pattern).Count;
        static string? Body(string code, string header)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(code, header);
            if (matches.Count != 1) return null;
            var open = code.IndexOf('{', matches[0].Index + matches[0].Length);
            var depth = 0;
            for (var i = open; open >= 0 && i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
            }
            return null;
        }
        const string Reflected = @"\.\s*Invoke\s*\(\s*null\b";
        var problems = new List<string>();
        var twelve = Body(tests, @"\bstatic\s+void\s+PartialWriteStage\s*\(");
        var thirteen = Body(tests, @"\bstatic\s+void\s+PartialWriteContractStage\s*\(");
        if (twelve is null || thirteen is null) { problems.Add("section 12 or 13 (PartialWriteStage / PartialWriteContractStage) not found once"); return problems; }
        var calls = Count(tests, @"\bForcedRun\s*\(");
        if (calls != 3) problems.Add("ForcedRun( appears " + calls.ToString(CultureInfo.InvariantCulture) + " times, not 3 (its declaration and one call from each section)");
        foreach (var (label, section) in new[] { ("section 12", twelve), ("section 13", thirteen) })
        {
            var setOut = Count(section, @"\bConsole\s*\.\s*SetOut\b");
            if (setOut != 0) problems.Add(label + " calls Console.SetOut " + setOut.ToString(CultureInfo.InvariantCulture) + " times itself");
            var forced = Count(section, @"\bForcedRun\s*\(");
            if (forced != 1) problems.Add(label + " calls ForcedRun " + forced.ToString(CultureInfo.InvariantCulture) + " times, not once");
            var invoke = Count(section, Reflected);
            if (invoke != 0) problems.Add(label + " invokes a method by reflection " + invoke.ToString(CultureInfo.InvariantCulture) + " times itself");
        }
        // P7-38: ForcedRun itself sets stdout once and restores it once around its one reflected Run, and no other helper
        // that section 12 or 13 reaches (transitively) sets stdout or invokes by reflection (a second copy).
        // P7-40: members of GeneratePrototypesTests by type membership (not brace depth 1); the reach follows generic,
        // qualified, method-group, delegate-field and cross-class calls, and starts from the argument lists of every call of
        // sections 12 and 13 too (a lambda passed as cli or check runs inside them), which must not set stdout or reflect.
        var map = new CodeMap(tests);
        var forcedRun = map.MembersOf("GeneratePrototypesTests", "ForcedRun", false);
        if (forcedRun.Count != 1) { problems.Add("ForcedRun declared " + forcedRun.Count.ToString(CultureInfo.InvariantCulture) + " times as a member of GeneratePrototypesTests, not once"); return problems; }
        var helper = tests[forcedRun[0].Start..forcedRun[0].End];
        if (Count(helper, @"\bConsole\s*\.\s*SetOut\b") != 2 || Count(helper, @"\bConsole\s*\.\s*SetOut\s*\(\s*stdout\s*\)") != 1 || Count(helper, @"\bConsole\s*\.\s*SetOut\s*\(\s*previousOut\s*\)") != 1)
            problems.Add("ForcedRun does not call Console.SetOut exactly twice (SetOut(stdout) once, SetOut(previousOut) once): " + Count(helper, @"\bConsole\s*\.\s*SetOut\b").ToString(CultureInfo.InvariantCulture));
        if (Count(helper, Reflected) != 1) problems.Add("ForcedRun invokes by reflection other than once");
        var sections = new[] { "PartialWriteStage", "PartialWriteContractStage" };
        var roots = sections.SelectMany(name => map.MembersOf("GeneratePrototypesTests", name, false)).ToList();
        if (roots.Count != 2) problems.Add("PartialWriteStage / PartialWriteContractStage declared " + roots.Count.ToString(CultureInfo.InvariantCulture) + " times as members of GeneratePrototypesTests, not once each");
        var arguments = new List<(int Start, int End)>();
        foreach (var name in sections)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(tests, @"\b" + name + @"\s*\("))
            {
                if (roots.Any(r => r.Start == m.Index)) continue;
                var close = Closing(tests, m.Index + m.Length - 1, '(', ')');
                if (close < 0) continue;
                arguments.Add((m.Index + m.Length, close));
                var passed = tests[(m.Index + m.Length)..close];
                if (Count(passed, @"\bConsole\s*\.\s*SetOut\b") != 0 || Count(passed, Reflected) != 0)
                    problems.Add("a call of " + name + " passes an argument that sets stdout or invokes by reflection (a copy of ForcedRun in a lambda)");
            }
        var (reached, _) = Reach(map, roots.Concat(arguments).ToList(), null);
        if (!reached.Any(r => r.Start == forcedRun[0].Start)) problems.Add("sections 12 and 13 do not reach ForcedRun");
        foreach (var (name, start, end) in reached)
        {
            if (start == forcedRun[0].Start) continue;
            var body = tests[start..end];
            if (Count(body, @"\bConsole\s*\.\s*SetOut\b") != 0 || Count(body, Reflected) != 0)
                problems.Add("section 12 or 13 reaches " + name + ", which sets stdout or invokes by reflection (a copy of ForcedRun)");
        }
        return problems;
    }

    // P7-36: over the comment-stripped, string-blanked GenerateWrite.cs: the problems that would let Execute retry, complete
    // or write a second time the success line or the pipeline refusal line (empty = pinned).
    // P7-38: every helper of GenerateCommand (GenerateWrite.cs + GenerateCommand.cs) that Execute reaches through
    // unqualified calls is followed too: none names a stdout Console member or Print(, and every callee resolves to a
    // declared method or local function (a delegate field is not followed, so it is a problem). The success object's
    // trailing comma is optional (a reformat without it is accepted).
    // P7-40: Execute is found as a member of GenerateCommand (not at brace depth 1), and the reach also follows generic,
    // qualified (GenerateCommand.X(), method-group, delegate and cross-class calls (see Reach); a call into a class outside
    // the two files is allowed only when listed in ExecuteExternalCalls, else unresolved (a problem).
    // P7-42: the code also holds followed (default: ExecuteFollowedCode(), the real OutputProblem.cs, BoundedInput.cs,
    // SkillPackLookup.cs and IntentPlan.cs), so Execute's calls into those repository CLI helpers are followed into their
    // files like any other declared type's members, with the same stdout and resolution checks.
    // P7-44: followed is required (callers pass ExecuteFollowedCode(root) for the root their stage receives, or a mutant of
    // it), and it also holds adapters/gltf/Bim.Gltf/GltfExporter.cs, so GltfExporter.WriteNew is followed too.
    static List<string> ExecuteWriteProblems(string write, string command, string followed)
    {
        static int Count(string code, string pattern) => System.Text.RegularExpressions.Regex.Matches(code, pattern).Count;
        var problems = new List<string>();
        var header = System.Text.RegularExpressions.Regex.Matches(write, @"\bstatic\s+void\s+Execute\s*\(\s*string\s*\[\s*\]\s*args\s*\)\s*\{");
        if (header.Count != 1) { problems.Add("Execute(string[] args) not found once"); return problems; }
        var open = header[0].Index + header[0].Length - 1;
        var close = -1;
        var depth = 0;
        for (var i = open; i < write.Length && close < 0; i++)
        {
            if (write[i] == '{') depth++;
            else if (write[i] == '}' && --depth == 0) close = i;
        }
        if (close < 0) { problems.Add("Execute's body is not closed"); return problems; }
        var body = write[open..(close + 1)];
        if (Count(body, @"\bConsole\s*\.(?!\s*Error\b)") != 2) problems.Add("Execute uses a stdout Console member other than twice");
        if (Count(body, @"\bConsole\s*\.\s*WriteLine\s*\(\s*refused\s*\)\s*;") != 1) problems.Add("Execute does not write the refusal line with exactly one Console.WriteLine(refused)");
        if (Count(body, @"\bConsole\s*\.\s*WriteLine\s*\(\s*JsonSerializer\s*\.\s*Serialize\s*\(\s*new\s*\{") != 1) problems.Add("Execute does not write the success line with exactly one Console.WriteLine(JsonSerializer.Serialize(new {");
        if (Count(body, @"\brefused\b") != 2) problems.Add("Execute names refused other than in its pattern and its one write");
        if (Count(body, @"\bJsonSerializer\b") != 1) problems.Add("Execute names JsonSerializer other than once");
        if (Count(body, @"\b(?:Write|WriteLine|WriteAsync|WriteLineAsync)\s*\(") != 3) problems.Add("Execute has other than three writes (refusal line, success line, PartialOutputLeft)");
        if (Count(body, @"\bConsole\s*\.\s*Error\b") != 1 || Count(body, @"\bConsole\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*PartialOutputLeft\s*\+\s*output\s*\)\s*;") != 1)
            problems.Add("Execute writes stderr other than the one PartialOutputLeft line");
        if (Count(body, @"\b(?:for|foreach|while|do|goto)\b") != 0) problems.Add("Execute has a loop or goto");
        if (Count(body, @"\bfinally\b") != 0) problems.Add("Execute has a finally");
        if (Count(body, @"\bwhen\b") != 1) problems.Add("Execute has an exception filter other than the WriteNew IO one");
        if (Count(body, @"=>") != 1) problems.Add("Execute has a lambda other than the accepted count");
        if (Count(body, @"\bExecute\s*\(") != 0) problems.Add("Execute calls itself");
        if (Count(body, @"\bGenerate\s*\(") != 1) problems.Add("Execute runs Generate( other than once");
        if (Count(body, @"\bGltfExporter\s*\.\s*WriteNew\s*\(") != 1) problems.Add("Execute calls GltfExporter.WriteNew( other than once");
        // The refusal line: written once, unguarded, then exit 1 and return.
        if (Count(body, @"if\s*\(\s*generated\s*\.\s*RefusedLine\s+is\s*\{\s*\}\s*refused\s*\)\s*\{\s*Console\s*\.\s*WriteLine\s*\(\s*refused\s*\)\s*;\s*Environment\s*\.\s*ExitCode\s*=\s*1\s*;\s*return\s*;\s*\}") != 1)
            problems.Add("the refusal line is not if (generated.RefusedLine is { } refused) { Console.WriteLine(refused); Environment.ExitCode = 1; return; }");
        // The success line: the last statement of a try that starts with PostWriteStageProbe; its catch (Exception) deletes,
        // reports a failed delete on stderr and rethrows, and closes Execute.
        if (Count(body, @"try\s*\{\s*PostWriteStageProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;\s*var\s+sha256\s*=") != 1)
            problems.Add("the success try does not start with PostWriteStageProbe?.Invoke(); var sha256 =");
        if (Count(body, @"sha256\s*,?\s*\}\s*\)\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*try\s*\{\s*PostWriteDeleteProbe\s*\?\s*\.\s*Invoke\s*\(\s*output\s*\)\s*;\s*File\s*\.\s*Delete\s*\(\s*output\s*\)\s*;\s*\}\s*"
            + @"catch\s*\(\s*Exception\s*\)\s*\{\s*try\s*\{\s*Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*PartialOutputLeft\s*\+\s*output\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}\s*throw\s*;\s*\}\s*\}$") != 1)
            problems.Add("the success line is not the last statement of its try with the delete / report / rethrow catch (Exception) closing Execute");
        if (Count(body, @"\bPrint\s*\(") != 0) problems.Add("Execute calls Print(");
        var code = write + "\n" + command + "\n" + followed;
        var map = new CodeMap(code);
        var executes = map.MembersOf("GenerateCommand", "Execute", false);
        if (executes.Count != 1) { problems.Add("Execute declared " + executes.Count.ToString(CultureInfo.InvariantCulture) + " times as a member of GenerateCommand, not once"); return problems; }
        var (reached, unresolved) = Reach(map, executes, ExecuteExternalCalls);
        foreach (var name in unresolved)
            problems.Add("Execute reaches " + (name.Contains('(') ? name : name + "(") + ", declared as no method, local function or delegate in GenerateWrite.cs, GenerateCommand.cs or the followed files and not a listed external call (a parameter that is no followed delegate type, of a lambda or that no caller passes, or a class the pin cannot read)");
        foreach (var (name, start, end) in reached)
        {
            var helper = code[start..end];
            if (Count(helper, @"\bConsole\s*\.(?!\s*Error\b)") != 0) problems.Add("Execute reaches a stdout Console member through the helper " + name);
            if (Count(helper, @"\bPrint\s*\(") != 0) problems.Add("Execute reaches Print( through the helper " + name);
        }
        return problems;
    }

    // P7-40: the calls into classes outside GenerateWrite.cs and GenerateCommand.cs that Execute may reach, as written
    // (Type.Member): BCL and library code. Console's members are listed so the stdout checks, not this list, judge them.
    // Any other external call Execute reaches is unresolved.
    // P7-42: OutputProblem, BoundedInput, SkillPackLookup and IntentPlan are no longer listed: their files are followed
    // (ExecuteFollowedCode), so the BCL calls made there are listed instead, and an unqualified call with no body in the read
    // code is allowed as Type.Member of its enclosing type: BoundedInput's extern P/Invoke declarations GetFileType,
    // NativeLStat and NativeFStat (lstat / fstat / GetFileType, no writes).
    // P7-44: BoundedInput.call is no longer listed: Stat's Func parameter call( is followed to the lambdas LStat and FStat
    // pass (Reach). GltfExporter.WriteNew is no longer listed: GltfExporter.cs is followed (its File / Directory calls are
    // BCL, listed). The repository library calls still listed, each for its reason:
    //   - PrototypePipeline.Run (core/Bim.Generative): the pure prototype pipeline over in-memory values, about 700 lines
    //     with its model and serializer, no Console / stdout member (it returns a result; the CLI prints it);
    //   - PrototypePipelineSerializer.Serialize (core/Bim.Generative): returns the canonical JSON string, writes nothing;
    //   - SkillParser.IsId / SkillParser.Parse (core): a pack id check and a skill.json parse over a string, write nothing;
    //   - RevitCommandPolicy.RepositoryRoot (core): a directory walk for the repository root, reads only.
    // A stdout write added there is not caught by this pin (gap: those library files are not followed).
    static readonly HashSet<string> ExecuteExternalCalls = new(StringComparer.Ordinal)
    {
        "Console.WriteLine", "Console.Error.WriteLine", "Convert.ToHexString", "SHA256.HashData", "JsonSerializer.Serialize",
        "File.Delete", "PrototypePipeline.Run", "PrototypePipelineSerializer.Serialize", "JsonDocument.Parse", "Array.Empty", "Array.IndexOf",
        "SkillParser.IsId", "Guid.TryParseExact", "Encoding.UTF8.GetString", "double.IsFinite",
        "string.IsNullOrEmpty", "string.Equals", "char.IsAsciiLetter", "Path.GetFullPath", "Path.GetExtension", "Path.GetDirectoryName", "Path.Combine",
        "Path.TrimEndingDirectorySeparator", "Path.GetFileName", "Directory.Exists", "File.Exists", "File.GetAttributes", "OperatingSystem.IsWindows",
        "RevitCommandPolicy.RepositoryRoot", "SkillParser.Parse", "ArgumentException.ThrowIfNullOrEmpty", "ArgumentOutOfRangeException.ThrowIfNegativeOrZero",
        "Encoding.UTF8.GetBytes", "BitConverter.ToInt32", "BoundedInput.GetFileType", "BoundedInput.NativeLStat", "BoundedInput.NativeFStat",
    };

    // P7-42: the CLI files Execute's calls into GltfExportCommand.OutputProblem, BoundedInput.ReadText, SkillPackLookup.Load and
    // IntentPlan.InternalDiagnostic lead to, comment-stripped and string-blanked, joined by newlines.
    // P7-44: plus GltfExporter.cs (GltfExporter.WriteNew), as repository-relative paths read from the root the calling stage
    // receives (the root every other check of this file uses), each time it is called: no global.json search of its own and
    // no static cache.
    static readonly string[] ExecuteFollowedFiles =
    {
        "interfaces/cli/OutputProblem.cs", "interfaces/cli/BoundedInput.cs", "interfaces/cli/SkillPackLookup.cs", "interfaces/cli/IntentPlan.cs",
        "adapters/gltf/Bim.Gltf/GltfExporter.cs",
    };
    static string ExecuteFollowedCode(string root) =>
        string.Join("\n", ExecuteFollowedFiles.Select(file => ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(new[] { root }.Concat(file.Split('/')).ToArray())))));

    // P7-38 / P7-40: static reachability over comment-stripped, string-blanked C# (sections 14 to 16). Not a parser.
    // A declaration is name( or name<...>( preceded by whitespace and a type-ending token (not return / throw / new / ...),
    // whose parameter list (and where clauses) is followed by a block or =>. Membership, not brace depth: a declaration is
    // a member of the class / struct / interface / record whose body is its innermost enclosing brace, else local to the
    // member whose body (the outermost brace inside that type body) encloses it. From each reached text (P7-40):
    //   - an unqualified name( or name<...>( resolves to the local functions and delegate declarators of the same member,
    //     then to the methods and delegate declarators of the enclosing types (all overloads); declared nowhere it is
    //     unresolved (a delegate parameter, say);
    //   - T.name( or A.B.T.name( (generic or not), with T a type declared in the code, resolves to T's members of that name
    //     (declared nowhere: unresolved); with T not declared in the code the call is allowed only when the caller lists it
    //     as external (Type.Member exactly), else unresolved;
    //   - any other receiver (an instance, ?., a call result): the methods of that name of every type declared in the code
    //     (an instance method of an external type is not followed);
    //   - new T( or new T { with T declared in the code: T's whole body (constructors, initializers);
    //   - a name not followed by ( (bare or T.-qualified) that names a method in scope (a method group passed or assigned),
    //     or a delegate field or property: that method or declarator;
    //   - a delegate declarator (name = / += / ??= / => / { get; ... } = ...) is followed through its right-hand side.
    // P7-42:
    //   - a name (called or not) that is a field or property of an enclosing type (CodeMap.IsField) is also followed through
    //     every assignment of it outside its declaration: name = / += / ??= inside a member of that type (not a local
    //     declaration), this.name = inside it, or T.name = / A.B.T.name = anywhere (a delegate armed in a static constructor
    //     or a method and invoked as name?.Invoke( or name(); the same for T.name);
    //   - a receiver chain A.B.name( is an instance (followed as any other receiver) when its last declared type is not its
    //     last segment (GenerateCommand.Result.Items.Count() ) or, with no declared type, when A is a field or property of an
    //     enclosing type (Result.Items.Count() ); otherwise as above;
    //   - type-argument lists nest to any depth and hold tuples (Run<Dictionary<string, List<int>>>(, Run<(int, string)>();
    //   - an unqualified call with no body in the read code is allowed when the caller lists Type.Member for its innermost
    //     enclosing type (an extern declaration), else unresolved.
    // P7-44:
    //   - a name that is a parameter of an enclosing block-bodied method, constructor or local function (CodeMap.Parameter)
    //     is that parameter: a Func / Action parameter, called or passed on, is followed to the arguments every caller
    //     passes for it (CodeMap.Arguments: named or at its position; a local function's callers in its member, a type
    //     member's unqualified callers in its type and Type.method( callers anywhere); called with no such argument, or a
    //     parameter of another type called, it is unresolved;
    //   - a field is not followed from where a parameter or a local of an enclosing block shadows it, and an assignment of
    //     such a local is not a field assignment (CodeMap.Shadowed); a tuple type (A a, B b) Name ends a type (AfterType).
    // P7-46:
    //   - Parameter also sees the parameters of expression-bodied methods and local functions (followed like block-bodied
    //     ones) and of lambdas (x => / (x, y) => / typed); a lambda's parameter called is unresolved naming that limit (whoever
    //     invokes the lambda passes its arguments, not followed);
    //   - followed delegate parameter types are Func, Action, Predicate, Comparison, Converter and delegate types declared
    //     in the read code; any other parameter called is unresolved naming its type and method;
    //   - Arguments also takes a type member's calls through this. / base. / any instance receiver (every instance call of
    //     that name, over-approximated) and the calls of a local or field the method group is assigned to (f = M; f( /
    //     f?.Invoke(); any other method-group use (passed on, a method-group local used other than called) is a gap,
    //     unresolved whenever the parameter is referenced;
    //   - a local declared after the use does not shadow the field there (Shadowed: fail closed, the field is followed).
    //   An unresolved label is name( [why] when the reach knows why; ExecuteWriteProblems prints it as is.
    // P7-48:
    //   - async / static before a lambda head (async x =>, static (x) =>, static async (T x) =>) is a modifier: the head
    //     is a lambda's, not an expression-bodied property's or a method called async; and a lambda head x => is no
    //     declarator of x (Declarators keeps name => only after a type: an expression-bodied property or method);
    //   - a type-argument list followed by ( . or { (M<A, B>(), new Dictionary<A, B> { }) is one bracket: its commas cut
    //     neither a lambda's expression body (Arrows) nor a caller's argument list (Arguments' Split);
    //   - a method-group alias assigned on as a whole (var g = f; G = F;) is followed through its own calls (a chain, local
    //     or field); a field alias used other than called or assigned on (passed on) is a gap like a local's;
    //   - the parameters of a generic method or local function with where clauses (M<T>(Func<T> f) where T : new() { or
    //     => ) are seen (HeadClose);
    //   - a lambda's parameter or a parameter of a type that is no followed delegate, reached as the whole argument a caller
    //     passes for a followed delegate parameter (passed on, not called), is unresolved naming that limit.
    // P7-50:
    //   - HeadClose takes a where only as a constraint (where Name : at bracket depth 0 walking back from the body, and
    //     Declarations likewise): a where inside an attribute or a default value, or a LINQ where, opens no method head;
    //   - TypeArgumentsEnd takes a list followed by C#'s disambiguating tokens (a cast (List<A, B>)x) or by a designation
    //     after is / case / out (a pattern is Dictionary<A, B> d); a comparison with an operator or followed by a name is
    //     none. Stated limit: a < b > (c) and a < b, c > (d) are a generic call, as C# itself parses them;
    //   - a parameter passed on wrapped as (test), test!, a cast (T)test, a conditional's branch or a side of ?? (Values,
    //     inside an argument Arguments returned: IsArgument; not inside a lambda's body) is named like the bare one;
    //   - a field alias's uses are matched by name over the whole read code (over-approximated), which its gap states; a
    //     method group in a conditional or after ?? is not followed: it stays a named gap.
    // P7-52:
    //   - Values takes (T) as a cast only when the token after it can start an operand (CastFollows: a name or keyword
    //     other than as / is / switch / with / and / or, a literal, ( ~ or ! but not !=; after a keyword type also + -),
    //     so (test) ?? d and (test) + 1 are parenthesised operands; both sides of a binary + and the left of a binary -
    //     are values (a delegate combination invokes both, a removal leaves the left's);
    //   - a nullable type's ? (NullableMark: the next non-whitespace character is > , ) or ]) opens no conditional, so
    //     new List<int?>() ?? test and new List<int? >() ?? test keep test as a value.
    // P7-54:
    //   - CastFollows takes (T)! as a cast only when an operand follows the ! (a logical not): (test)! ?? d and (test)!.M
    //     are a null-forgiving parenthesised operand;
    //   - the first ? of a span followed by [ opens a conditional when the matching ] is followed by : (CollectionBranch:
    //     c ?[x] : d, a collection-expression branch); else it is an element access a?[0]. Stated limit: inside a
    //     conditional's branch every ?[ is still an element access (c ? b ?[x] : y : d is not split at its inner ?);
    //   - the sign after the e / E of a decimal numeric literal (ExponentSign: 1e-5, 2.5E+3) is no binary operator; a hex
    //     0x1e - 5 still is.
    // P7-58:
    //   - a ? that opens a conditional, the span's first or one counted as nested in a branch, is one predicate
    //     (ConditionalQuestion); the mixed branch c ? a?[0] : b ?[x] : y : d reads both ?[ as nested conditionals;
    //   - an exponent's sign follows its e directly (1e - x is a subtraction, as is 1e /*c*/ - x once comments are
    //     stripped) and its prefix takes _ only between digits (1_e-5 and 1.5_e-3 are subtractions);
    //   - stated reach gap: (test)!(x) is a cast of !(x), as C# reads it, so a delegate test written so is never seen called;
    //     C# does not compile that text as a call of a lambda's or method's parameter (test names no type).
    static readonly HashSet<string> CallKeywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "do", "switch", "catch", "when", "using", "lock", "return", "throw", "nameof", "typeof", "sizeof",
        "default", "checked", "unchecked", "fixed", "new", "base", "this", "await", "is", "as", "in", "out", "ref", "var", "stackalloc",
        "else", "yield", "case", "not", "and", "or", "with", "goto", "get", "set", "async", "static",
    };
    // P7-48: the modifiers a lambda head may start with.
    static readonly HashSet<string> LambdaModifiers = new(StringComparer.Ordinal) { "async", "static" };
    static readonly HashSet<string> DeclarationBlockers = new(StringComparer.Ordinal) { "return", "throw", "new", "await", "else", "in", "is", "as", "case", "yield", "not", "and", "or", "when", "goto" };
    static readonly HashSet<string> KeywordTypes = new(StringComparer.Ordinal) { "string", "int", "long", "double", "float", "decimal", "bool", "char", "byte", "short", "uint", "ulong", "object" };
    // An optional type-argument list: Foo<T>(, Foo<List<int>, string>( and (P7-42) any nesting and tuples,
    // Foo<Dictionary<string, List<(int, string)>>>( (balanced < > and ( ) over names, whitespace , . ? [ ]).
    const string TypeArguments = @"(?:\s*<(?>[\w\s,.?\[\]]+|(?<ta><)|(?<-ta>>)|(?<tp>\()|(?<-tp>\)))*(?(ta)(?!))(?(tp)(?!))>)?";

    // P7-42: whether the token before index ends a type, so the name at index is declared there (a local, field, property or
    // parameter): whitespace before the name, a letter / digit / _ / > / ] / ? (not the > of =>), not a statement keyword.
    // P7-44: not the ? of ??; and a tuple type (Action Say, int Count) Name ends a type: a ) whose ( holds two or more
    // items at depth 0 and follows no name, call or member access (if (on) Name = ... is an assignment), or a modifier.
    static bool AfterType(string code, int index)
    {
        var (last, word, spaced) = Before(code, index);
        var j = index - 1;
        while (j >= 0 && char.IsWhiteSpace(code[j])) j--;
        if (spaced && last == ')')
        {
            var open = Opening(code, j, '(', ')');
            if (open < 0 || Split(code, open + 1, j, true).Count < 2) return false;
            var (before, beforeWord, _) = Before(code, open);
            return !(char.IsLetterOrDigit(before) || before is '_' or ')' or ']' or '>' or '.' or '?') || TupleModifiers.Contains(beforeWord);
        }
        if (!spaced || !(char.IsLetterOrDigit(last) || last is '_' or '>' or ']' or '?') || DeclarationBlockers.Contains(word)) return false;
        if (last == '?') return !(j > 0 && code[j - 1] == '?');
        if (last != '>') return true;
        return !(j > 0 && code[j - 1] == '=');
    }
    static readonly HashSet<string> TupleModifiers = new(StringComparer.Ordinal)
    {
        "static", "readonly", "public", "private", "protected", "internal", "required", "volatile", "unsafe", "scoped", "ref", "out", "in", "params", "this",
    };

    // P7-44: the index of the bracket opening the one closing at close, or -1.
    static int Opening(string code, int close, char opening, char closing)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (code[i] == closing) depth++;
            else if (code[i] == opening && --depth == 0) return i;
        }
        return -1;
    }

    // P7-44: the comma-separated items of code[start, end) at bracket depth 0 (( ), [ ], { } and, when angles, < >; a > of
    // => is not a bracket), trimmed, empty items skipped.
    static List<(int Start, int End)> Split(string code, int start, int end, bool angles)
    {
        var items = new List<(int Start, int End)>();
        var depth = 0;
        var from = start;
        for (var i = start; i <= end; i++)
        {
            if (i == end || (code[i] == ',' && depth == 0))
            {
                var s = from;
                var e = i;
                while (s < e && char.IsWhiteSpace(code[s])) s++;
                while (e > s && char.IsWhiteSpace(code[e - 1])) e--;
                if (e > s) items.Add((s, e));
                from = i + 1;
            }
            // P7-48: without angles, a type-argument list of a generic call (M<A, B>() is skipped whole.
            else if (!angles && code[i] == '<' && TypeArgumentsEnd(code, i) is var list && list > i && list < end) i = list;
            else if (code[i] is '(' or '[' or '{' || (angles && code[i] == '<')) depth++;
            else if (code[i] is ')' or ']' or '}' || (angles && code[i] == '>' && !(i > 0 && code[i - 1] == '='))) depth--;
        }
        return items;
    }

    // P7-48: the index of the > closing the type-argument list whose < is at index, when that < follows a name and the
    // list is followed by ( . or { (M<A, B>(), List<A>.Empty, new Dictionary<A, B> { }), else -1 (a comparison).
    // P7-50: followed by any of C#'s disambiguating tokens ( ) ] } : ; , . ? == != | ^ & [ (or {), so a cast (List<A, B>)x is
    // one list too; or by a designation when the type follows is / case / out / not / and / or (a pattern
    // x is Dictionary<A, B> d, a declaration out List<A, B> l). A comparison holding an operator (a < b + 1, c > d) or
    // followed by a name is no list. Stated limit: a < b > (c) and a < b, c > (d) are taken as a generic call, as C# itself
    // parses them (the spec's grammar ambiguity rule).
    static readonly System.Text.RegularExpressions.Regex TypeArgumentList = new(@"\G" + TypeArguments + @"(?=\s*(?:[(.{)\]}:;,?\[|^&]|==|!=))");
    static readonly System.Text.RegularExpressions.Regex PatternTypeArgumentList = new(@"\G" + TypeArguments + @"(?=\s*[A-Za-z_])");
    static readonly HashSet<string> PatternWords = new(StringComparer.Ordinal) { "is", "case", "out", "not", "and", "or" };
    static int TypeArgumentsEnd(string code, int index)
    {
        if (code[index] != '<') return -1;
        var j = index - 1;
        while (j >= 0 && char.IsWhiteSpace(code[j])) j--;
        if (j < 0 || !(char.IsLetterOrDigit(code[j]) || code[j] == '_')) return -1;
        var list = TypeArgumentList.Match(code, index);
        if (!(list.Success && list.Length > 0))
        {
            var k = j;
            while (k >= 0 && (char.IsLetterOrDigit(code[k]) || code[k] is '_' or '.')) k--;
            if (!PatternWords.Contains(Before(code, k + 1).Word)) return -1;
            list = PatternTypeArgumentList.Match(code, index);
        }
        return list.Success && list.Length > 0 ? index + list.Length - 1 : -1;
    }

    // P7-50: a where constraint clause starting at the index (where T :, not where T :: nor a LINQ where cond).
    static readonly System.Text.RegularExpressions.Regex Constraint = new(@"\Gwhere\s+[A-Za-z_]\w*\s*:(?!:)");

    // P7-50: the spans whose value is the value of the expression [start, end), trimmed: the expression itself and,
    // recursively, x of a parenthesized (x), of a null-forgiving x! and of a cast (T)x, both branches of a conditional
    // c ? a : b (not c) and both sides of a ?? b; P7-52: both sides of a + b and the left of a - b (P7-54: not at an
    // exponent's sign).
    static readonly System.Text.RegularExpressions.Regex CastType = new(@"^\s*(?:global::)?[A-Za-z_][\w.]*\s*(?:<[\w\s,.?<>\[\]()]*>)?\s*\??\s*(?:\[[\s,]*\]\s*)*$");
    static List<(int Start, int End)> Values(string code, int start, int end)
    {
        var values = new List<(int Start, int End)>();
        void Add(int s, int e)
        {
            while (s < e && char.IsWhiteSpace(code[s])) s++;
            while (e > s && char.IsWhiteSpace(code[e - 1])) e--;
            if (e <= s) return;
            values.Add((s, e));
            // A lambda's value is the lambda: its body's branches are not passed on.
            var level = 0;
            for (var i = s; i + 1 < e; i++)
            {
                if (code[i] is '(' or '[' or '{') level++;
                else if (code[i] is ')' or ']' or '}') level--;
                else if (level == 0 && code[i] == '=' && code[i + 1] == '>' && !(i > s && code[i - 1] is '=' or '!' or '<' or '>')) return;
            }
            if (code[e - 1] == '!') { Add(s, e - 1); return; }
            if (code[s] == '(')
            {
                var close = Closing(code, s, '(', ')');
                if (close == e - 1) { Add(s + 1, e - 1); return; }
                var after = close + 1;
                while (after < e && char.IsWhiteSpace(code[after])) after++;
                // A cast, not a lambda's parameter list (x) => nor a parenthesised operand of a binary operator.
                if (close > s && after < e && CastFollows(code, s + 1, close, after, e) && CastType.IsMatch(code[(s + 1)..close])) { Add(close + 1, e); return; }
            }
            // The first ? of a conditional (not ?? ?. ?[ nor a nullable type's ? before > , ) ], P7-52: also past whitespace)
            // and the first ?? at depth 0.
            var depth = 0;
            var question = -1;
            var coalesce = -1;
            for (var i = s; i < e; i++)
            {
                var c = code[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (depth == 0 && c == '?')
                {
                    var next = i + 1 < e ? code[i + 1] : '\0';
                    if (next == '?') { if (coalesce < 0) coalesce = i; i++; }
                    else if (question < 0 && ConditionalQuestion(code, i, e, true)) question = i;
                }
            }
            if (question >= 0)
            {
                // P7-56: a ?[ inside the branch whose ] is followed by : (CollectionBranch) first counts as a nested
                // conditional (c ? b ?[x] : y : d); when no : is then left for this conditional, every ?[ there is an
                // element access (c ? a?[0] : d). Stated limit: a branch is read with all such ?[ nested or none (P7-58:
                // c ? a?[0] : b ?[x] : y : d nests both, the reading that parses).
                foreach (var collections in new[] { true, false })
                {
                    var nested = 0;
                    depth = 0;
                    for (var i = question + 1; i < e; i++)
                    {
                        var c = code[i];
                        if (c is '(' or '[' or '{') depth++;
                        else if (c is ')' or ']' or '}') depth--;
                        else if (depth > 0) continue;
                        else if (c == '?' && i + 1 < e && code[i + 1] == '?') i++;
                        else if (c == '?' && ConditionalQuestion(code, i, e, collections)) nested++;
                        else if (c == ':' && !(i + 1 < e && code[i + 1] == ':') && !(i > 0 && code[i - 1] == ':') && nested-- == 0)
                        {
                            Add(question + 1, i);
                            Add(i + 1, e);
                            return;
                        }
                    }
                }
                return;
            }
            if (coalesce >= 0) { Add(s, coalesce); Add(coalesce + 2, e); return; }
            // P7-52: the last binary + or - at depth 0 (a delegate combination a + b invokes both sides; a removal a - b
            // leaves a's): a binary operator follows an operand's end (a name, a literal, ) or ]), not another operator.
            depth = 0;
            // From the last character, so a closing bracket at the end is counted (a right operand (x) of a + (x)).
            for (var i = e - 1; i > s; i--)
            {
                var c = code[i];
                if (c is ')' or ']' or '}') depth++;
                else if (c is '(' or '[' or '{') depth--;
                else if (depth == 0 && i + 1 < e && c is '+' or '-' && code[i + 1] != c && code[i + 1] != '=' && code[i - 1] != c && code[i + 1] != '>')
                {
                    var p = i - 1;
                    while (p > s && char.IsWhiteSpace(code[p])) p--;
                    if (!(char.IsLetterOrDigit(code[p]) || code[p] is '_' or ')' or ']' or '"' or '\'')) continue;
                    // P7-58: an exponent's sign follows its e directly (no whitespace, so no stripped comment, between).
                    if (p == i - 1 && ExponentSign(code, s, p)) continue;
                    Add(s, i);
                    if (c == '+') Add(i + 1, e);
                    return;
                }
            }
        }
        Add(start, end);
        return values;
    }

    // P7-52: whether (T) at [open, close) followed by the token at after is a cast, as C# tells it from a parenthesised
    // operand: the next token is a name or keyword (but as, is, switch, with, and, or), a literal, ( ~ or ! (not !=);
    // after a keyword type ((int), (string?)) also + or -. So (x) =>, (test) ?? d, (test) + 1 and (test).M are no casts.
    // P7-56: (test)!(x) is a cast of !(x) (! followed by an operand), as C# reads it, not an invocation of test!.
    static readonly HashSet<string> OperatorWords = new(StringComparer.Ordinal) { "as", "is", "switch", "with", "and", "or" };
    static bool CastFollows(string code, int open, int close, int after, int end)
    {
        var c = code[after];
        if (char.IsLetter(c) || c is '_' or '@')
        {
            var w = after;
            while (w < end && (char.IsLetterOrDigit(code[w]) || code[w] is '_' or '@')) w++;
            return !OperatorWords.Contains(code[after..w]);
        }
        if (char.IsDigit(c) || c is '(' or '~' or '"' or '\'' or '$') return true;
        if (c == '!')
        {
            // P7-54: ! is a logical not (a cast's operand) only when an operand follows it; else it is the null-forgiving
            // postfix of the parenthesised operand ((test)! ?? d, (test)!.M), and != is an operator.
            var n = after + 1;
            if (n < end && code[n] == '=') return false;
            while (n < end && char.IsWhiteSpace(code[n])) n++;
            return n < end && (char.IsLetterOrDigit(code[n]) || code[n] is '_' or '@' or '(' or '!' or '~' or '"' or '\'' or '$');
        }
        return c is '+' or '-' && KeywordTypes.Contains(code[open..close].Trim().TrimEnd('?').TrimEnd());
    }

    // P7-58: whether the ? at index (not the first of ??, which callers skip) opens a conditional in [.., end): not ?. nor
    // ?[ (but ?[ starting a collection branch when collections), and not a nullable type's ? (NullableMark). One predicate
    // for the span's first ? and for the ? a branch counts as nested.
    static bool ConditionalQuestion(string code, int index, int end, bool collections)
    {
        var next = index + 1 < end ? code[index + 1] : '\0';
        return (next is not ('.' or '[') || (collections && next == '[' && CollectionBranch(code, index + 1, end))) && !NullableMark(code, index, end);
    }

    // P7-54: whether the ?[ whose [ is at open starts a collection-expression branch of a conditional (c ?[x] : d): the
    // matching ] is followed by a : (not ::). Else it is a null-conditional element access (a?[0]). P7-56: also told inside
    // a conditional's branch (c ? b ?[x] : y : d), falling back to element accesses when the conditional's : is not left.
    static bool CollectionBranch(string code, int open, int end)
    {
        var close = Closing(code, open, '[', ']');
        if (close < 0 || close >= end) return false;
        var n = close + 1;
        while (n < end && char.IsWhiteSpace(code[n])) n++;
        return n < end && code[n] == ':' && !(n + 1 < end && code[n + 1] == ':');
    }

    // P7-54: whether the + or - after the character at last is an exponent's sign: last is e / E ending a decimal numeric
    // literal (1e-5, 2.5E+3, 1_0e-5; not a name nor a hex 0x1e, whose - is a subtraction).
    // P7-56: the walk back crosses letters, digits, _ and . (so 2.5e and .5e are whole), and the walked token must then be a
    // whole decimal real prefix (digits, an optional . and digits, or . and digits, then e): a suffix ends no exponent
    // (1e5f - x), a hex stays one (0x_1e-5), and 1.E (a member access on 1) or 2.5.X1e (a member) is no exponent. Stated
    // limit: the token is read from the text before last only; a literal cut by whitespace or a comment is not seen.
    // P7-58: a digit separator stands only between digits (1_0e, 1__0e; not 1_e nor 1.5_e), and Values asks only when the
    // sign follows last directly, so a literal cut by whitespace or a stripped comment (1e - x) is a subtraction.
    static readonly System.Text.RegularExpressions.Regex ExponentPrefix = new(@"^(?:[0-9](?:_*[0-9])*(?:\.[0-9](?:_*[0-9])*)?|\.[0-9](?:_*[0-9])*)[eE]$");
    static bool ExponentSign(string code, int start, int last)
    {
        if (code[last] is not ('e' or 'E')) return false;
        var q = last;
        while (q > start && (char.IsLetterOrDigit(code[q - 1]) || code[q - 1] is '_' or '.')) q--;
        return ExponentPrefix.IsMatch(code[q..(last + 1)]);
    }

    // P7-52: whether the ? at index marks a nullable type: the next non-whitespace character closes a type (> , ) ]).
    static bool NullableMark(string code, int index, int end)
    {
        var n = index + 1;
        while (n < end && char.IsWhiteSpace(code[n])) n++;
        return n < end && code[n] is '>' or ',' or ')' or ']';
    }

    // The index of the bracket closing the one at open, or -1.
    static int Closing(string code, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == opening) depth++;
            else if (code[i] == closing && --depth == 0) return i;
        }
        return -1;
    }

    // The last word before index (skipping whitespace) and the character it ends at ('\0' at the start).
    static (char Last, string Word, bool Spaced) Before(string code, int index)
    {
        var j = index - 1;
        while (j >= 0 && char.IsWhiteSpace(code[j])) j--;
        if (j < 0) return ('\0', "", index > 0);
        var end = j + 1;
        while (j >= 0 && (char.IsLetterOrDigit(code[j]) || code[j] == '_')) j--;
        return (code[end - 1], code[(j + 1)..end], end < index);
    }

    // The declarations of name (generic or not) as [start, end) spans from the name to the body's closing brace or semicolon.
    static List<(int Start, int End)> Declarations(string code, string name)
    {
        var spans = new List<(int Start, int End)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"\b" + name + TypeArguments + @"\s*\("))
        {
            var (last, word, spaced) = Before(code, m.Index);
            if (!spaced || !(char.IsLetterOrDigit(last) || last is '_' or '>' or ']' or ')' or '?') || DeclarationBlockers.Contains(word)) continue;
            var paren = Closing(code, m.Index + m.Length - 1, '(', ')');
            if (paren < 0) continue;
            var at = paren + 1;
            while (at < code.Length && char.IsWhiteSpace(code[at])) at++;
            // Generic constraints: where T : ... up to the body.
            // P7-50: only a constraint (where Name :), not a LINQ where after a call.
            if (Constraint.IsMatch(code, at))
                while (at < code.Length && code[at] != '{' && code[at] != ';' && !(code[at] == '=' && at + 1 < code.Length && code[at + 1] == '>')) at++;
            var end = -1;
            if (at < code.Length && code[at] == '{') end = Closing(code, at, '{', '}');
            else if (at + 1 < code.Length && code[at] == '=' && code[at + 1] == '>') end = StatementEnd(code, at + 2);
            if (end >= 0) spans.Add((m.Index, end + 1));
        }
        return spans;
    }

    // The index of the ; ending the expression starting at start (at bracket depth 0), or of the bracket closing an
    // enclosing one (an initializer inside { } or an argument inside ( )), or -1.
    static int StatementEnd(string code, int start)
    {
        var depth = 0;
        for (var i = start; i < code.Length; i++)
        {
            if (code[i] is '(' or '{' or '[') depth++;
            else if (code[i] is ')' or '}' or ']') { if (--depth < 0) return i - 1; }
            else if (code[i] == ';' && depth == 0) return i;
        }
        return -1;
    }

    // P7-40: the brace structure and the type bodies of a comment-stripped C# text.
    sealed class CodeMap
    {
        readonly int[] enclosing;
        readonly int[] parens;
        readonly Dictionary<int, int> parent = new();
        readonly Dictionary<(string, string), bool> fields = new();
        readonly Dictionary<(string, string), List<(int Start, int End)>> assignments = new();
        readonly Dictionary<string, List<(int Start, int End)>> methods = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<(int Start, int End)>> declarators = new(StringComparer.Ordinal);
        readonly HashSet<(int Start, int End)> argumentSpans = new();

        // P7-50: whether [start, end) is an argument Arguments returned (passed for a followed delegate parameter).
        public bool IsArgument(int start, int end) => argumentSpans.Contains((start, end));

        public CodeMap(string code)
        {
            Code = code;
            enclosing = new int[code.Length + 1];
            // P7-42: the parenthesis depth before each index.
            parens = new int[code.Length + 1];
            for (var i = 0; i < code.Length; i++) parens[i + 1] = parens[i] + (code[i] == '(' ? 1 : code[i] == ')' ? -1 : 0);
            var stack = new Stack<int>();
            for (var i = 0; i <= code.Length; i++)
            {
                enclosing[i] = stack.Count == 0 ? -1 : stack.Peek();
                if (i == code.Length) break;
                if (code[i] == '{') { parent[i] = enclosing[i]; stack.Push(i); }
                else if (code[i] == '}' && stack.Count > 0) stack.Pop();
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"\b(?:class|struct|interface|record)\s+([A-Za-z_]\w*)"))
            {
                var name = m.Groups[1].Value;
                if (name is "class" or "struct" || CallKeywords.Contains(name)) continue;
                var depth = 0;
                for (var i = m.Index + m.Length; i < code.Length; i++)
                {
                    if (code[i] is '(' or '[') depth++;
                    else if (code[i] is ')' or ']') depth--;
                    else if (depth == 0 && code[i] == ';') break;
                    else if (depth == 0 && code[i] == '{')
                    {
                        var close = Closing(code, i, '{', '}');
                        if (close > 0) Types[i] = (name, close);
                        break;
                    }
                }
            }
        }

        public string Code { get; }

        // The type bodies by opening brace: the type's name and closing brace.
        public Dictionary<int, (string Name, int Close)> Types { get; } = new();

        // The names of the types enclosing index (innermost first) and its member brace: the outermost brace inside the
        // innermost enclosing type body (-1 when index is directly in a type body).
        public (List<string> Types, int Member) Scope(int index)
        {
            var types = new List<string>();
            var member = -1;
            var candidate = -1;
            for (var b = enclosing[index]; b >= 0; b = parent[b])
            {
                if (Types.TryGetValue(b, out var type))
                {
                    if (types.Count == 0) member = candidate;
                    types.Add(type.Name);
                }
                else candidate = b;
            }
            if (types.Count == 0) member = candidate;
            return (types, member);
        }

        // The type directly enclosing index when index is a type member (null for a local or a top-level declaration).
        public string? MemberOf(int index)
        {
            var (types, member) = Scope(index);
            return member < 0 && types.Count > 0 ? types[0] : null;
        }

        public bool IsType(string name) => Types.Values.Any(t => t.Name == name);

        public List<(int Start, int End)> Methods(string name)
        {
            if (!methods.TryGetValue(name, out var found)) methods[name] = found = Declarations(Code, name);
            return found;
        }

        // The delegate declarators of name: name = / += / ??= / => / { get; ... } = ..., as [name, end of the right-hand side).
        public List<(int Start, int End)> Declarators(string name)
        {
            if (declarators.TryGetValue(name, out var cached)) return cached;
            var found = new List<(int Start, int End)>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Code,
                @"\b" + name + @"\s*(?:(?:\+|\?\?)?=(?![=>])|=>|\{\s*get\s*;[^{}]*\}\s*=(?![=>]))"))
            {
                if (Before(Code, m.Index).Last == '.') continue;
                // P7-48: name => is a declarator only after a type (an expression-bodied property); a lambda head (x =>,
                // async x =>, static x =>) is not.
                if (Code[m.Index + m.Length - 1] == '>' && (!AfterType(Code, m.Index) || LambdaModifiers.Contains(Before(Code, m.Index).Word))) continue;
                var end = StatementEnd(Code, m.Index + m.Length);
                if (end >= 0) found.Add((m.Index, end + 1));
            }
            return declarators[name] = found;
        }

        // The spans of name visible from index: locals of the same member, then members of the enclosing types.
        public List<(int Start, int End)> Visible(string name, int index, bool withDeclarators, bool localDeclarators)
        {
            var (types, member) = Scope(index);
            var result = new List<(int Start, int End)>();
            void Add(List<(int Start, int End)> spans, bool locals)
            {
                foreach (var d in spans)
                {
                    var (dTypes, dMember) = Scope(d.Start);
                    if (dMember < 0 ? dTypes.Count > 0 && types.Contains(dTypes[0]) : locals && dMember == member) result.Add(d);
                }
            }
            Add(Methods(name), true);
            if (withDeclarators) Add(Declarators(name), localDeclarators);
            return result;
        }

        // The member spans of name in the types called type.
        public List<(int Start, int End)> MembersOf(string type, string name, bool withDeclarators) =>
            Methods(name).Concat(withDeclarators ? Declarators(name) : Enumerable.Empty<(int Start, int End)>()).Where(d => MemberOf(d.Start) == type).ToList();

        // P7-42: whether name is declared as a field or property of a type called type: an occurrence directly in that type's
        // body, outside parentheses (not a parameter), after a type-ending token (AfterType), followed by ; , = { or =>.
        public bool IsField(string type, string name)
        {
            if (fields.TryGetValue((type, name), out var known)) return known;
            var found = false;
            var declarator = new System.Text.RegularExpressions.Regex(@"\G\s*(?:;|,|=(?![=>])|\{|=>)");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Code, @"\b" + name + @"\b"))
            {
                if (MemberOf(m.Index) != type || parens[m.Index] > parens[enclosing[m.Index] + 1] || !AfterType(Code, m.Index)) continue;
                if (declarator.Match(Code, m.Index + m.Length).Success) { found = true; break; }
            }
            return fields[(type, name)] = found;
        }

        // P7-42: the assignments of the field or property name of type outside its declaration, as [name, end of the
        // right-hand side): name = / += / ??= inside a member of type that is not a local declaration, this.name = inside
        // type, or T.name = / A.B.T.name = anywhere.
        public List<(int Start, int End)> FieldAssignments(string type, string name)
        {
            if (assignments.TryGetValue((type, name), out var cached)) return cached;
            var found = new List<(int Start, int End)>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Code, @"\b" + name + @"\s*(?:\+|\?\?)?=(?![=>])"))
            {
                if (Before(Code, m.Index).Last == '.')
                {
                    var dot = m.Index - 1;
                    while (Code[dot] != '.') dot--;
                    var receiver = Receiver(Code, dot);
                    if (receiver is null || (receiver == "this" ? !Scope(m.Index).Types.Contains(type) : receiver.Split('.')[^1] != type)) continue;
                }
                else
                {
                    if (AfterType(Code, m.Index)) continue;
                    var (types, member) = Scope(m.Index);
                    // P7-44: an assignment of a local or parameter that shadows the field is not a field assignment.
                    if (member < 0 || !types.Contains(type) || Shadowed(name, m.Index)) continue;
                }
                var end = StatementEnd(Code, m.Index + m.Length);
                if (end >= 0) found.Add((m.Index, end + 1));
            }
            return assignments[(type, name)] = found;
        }

        // P7-42: the enclosing types of index (innermost first, once each) that declare name as a field or property.
        // P7-44: none when a local or parameter shadows name at index.
        public List<string> FieldTypes(string name, int index)
        {
            var types = Scope(index).Types.Distinct().Where(type => IsField(type, name)).ToList();
            return types.Count > 0 && Shadowed(name, index) ? new List<string>() : types;
        }

        // P7-44: the parameter name of the innermost method, constructor or local function whose block body encloses index
        // (walking out through the enclosing blocks of a member, not into the type body): that declaration's name, its type
        // (innermost enclosing type, or null), the member brace of a local function (-1 for a type member), the parameter's
        // position and whether its type is a Func<...> or Action delegate. Null when name is no such parameter (an
        // expression-bodied member's or a lambda's parameter is not seen; a generic method's neither: its call stays
        // unresolved).
        // P7-46: also the parameters of an expression-bodied method or local function (name(...) => body, generic or not) and
        // of a lambda (x => body, (x, y) => body, typed or not, block or expression bodied) whose body encloses index, the
        // innermost declaring one winning. A lambda's parameter is reported as such (Lambda: its arguments, passed by
        // whoever invokes the lambda, are not followed, so References names that limit when it is called). Delegate is true
        // for Func<...>, Action, Predicate<...>, Comparison<...>, Converter<...> and a delegate type declared in the code
        // (IsDelegateType); Kind is the parameter's declared type ("" for an untyped lambda parameter).
        // P7-48: a generic method's or local function's head past its where clauses is seen too (HeadClose), and an async /
        // static lambda's parameters are a lambda's (Arrows).
        public (string Method, string? Type, int Member, int Position, bool Delegate, bool Lambda, string Kind)? Parameter(string name, int index)
        {
            (string Method, string? Type, int Member, int Position, bool Delegate, bool Lambda, string Kind)? best = null;
            var bestStart = -1;
            for (var b = enclosing[index]; b >= 0 && !Types.ContainsKey(b); b = parent[b])
            {
                // P7-48: also past where clauses (HeadClose).
                var j = HeadClose(b);
                if (j < 0) continue;
                if (Head(j) is not { Method: { } method } head) continue;
                var p = head.Parameters.FindIndex(x => x.Name == name && x.Type.Length > 0);
                if (p < 0) continue;
                var (types, member) = Scope(head.NameStart);
                best = (method, types.Count > 0 ? types[0] : null, member, p, IsDelegateType(head.Parameters[p].Type), false, head.Parameters[p].Type);
                bestStart = b;
                break;
            }
            foreach (var arrow in Arrows())
            {
                if (arrow.BodyStart <= bestStart || index < arrow.BodyStart || index >= arrow.BodyEnd) continue;
                var p = arrow.Parameters.FindIndex(x => x.Name == name && (arrow.Method is null || x.Type.Length > 0));
                if (p < 0) continue;
                var kind = arrow.Parameters[p].Type;
                if (arrow.Method is null) best = ("", null, -1, p, false, true, kind);
                else
                {
                    var (types, member) = Scope(arrow.NameStart);
                    best = (arrow.Method, types.Count > 0 ? types[0] : null, member, p, IsDelegateType(kind), false, kind);
                }
                bestStart = arrow.BodyStart;
            }
            return best;
        }

        // P7-48: the ) closing the parameter list of the head that a body opening at at ({ or =>) follows: the ) right before
        // it, or the ) before the first of the where clauses between them (M<T>(Func<T> f) where T : class, new() {), when
        // those clauses hold no ; { } or =>; else -1.
        // P7-50: a where is a clause only as a constraint (where Name : at bracket depth 0 walking back from at, so not inside
        // an attribute's [ ] or a parameter list's ( ), and not a LINQ where cond); the walk stops at a ; { } or => at that
        // depth or at an unmatched ( or [ (at is inside it, so no head is before).
        readonly Dictionary<int, int> headCloses = new();
        int HeadClose(int at)
        {
            if (headCloses.TryGetValue(at, out var known)) return known;
            var first = -1;
            var depth = 0;
            for (var i = at - 1; i >= 0; i--)
            {
                var c = Code[i];
                if (c is ')' or ']') { depth++; continue; }
                if (c is '(' or '[') { if (--depth < 0) break; continue; }
                if (depth > 0) continue;
                if (c is ';' or '{' or '}' || (c == '>' && i > 0 && Code[i - 1] == '=')) break;
                if (c == 'w' && (i == 0 || !(char.IsLetterOrDigit(Code[i - 1]) || Code[i - 1] == '_')) && Constraint.IsMatch(Code, i)) first = i;
            }
            var j = (first >= 0 ? first : at) - 1;
            while (j >= 0 && char.IsWhiteSpace(Code[j])) j--;
            return headCloses[at] = j >= 0 && Code[j] == ')' ? j : -1;
        }

        // P7-46: the head of a parameter list whose ) is at close: the declared method / local function name (null for a
        // lambda's list or anything else that is no declaration), the index of that name and the (type, name) of each
        // parameter (type "" when untyped; a name that is no identifier is kept as "").
        readonly Dictionary<int, (string? Method, int NameStart, List<(string Type, string Name)> Parameters)?> heads = new();
        (string? Method, int NameStart, List<(string Type, string Name)> Parameters)? Head(int close)
        {
            if (!heads.TryGetValue(close, out var head)) heads[close] = head = ReadHead(close);
            return head;
        }
        (string? Method, int NameStart, List<(string Type, string Name)> Parameters)? ReadHead(int close)
        {
            var open = Opening(Code, close, '(', ')');
            if (open < 0) return null;
            var parameters = new List<(string Type, string Name)>();
            foreach (var item in Split(Code, open + 1, close, true))
            {
                var parameter = System.Text.RegularExpressions.Regex.Match(Code[item.Start..item.End],
                    @"^(?:\[[^\]]*\]\s*)*(?:(?:this|params|ref|out|in|scoped)\s+)*(?<type>.*?)\s*\b(?<name>[A-Za-z_]\w*)\s*(?:=.*)?$",
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                parameters.Add(parameter.Success ? (parameter.Groups["type"].Value, parameter.Groups["name"].Value) : ("", ""));
            }
            var k = open - 1;
            while (k >= 0 && char.IsWhiteSpace(Code[k])) k--;
            // A generic declaration: name<T, U>(.
            if (k >= 0 && Code[k] == '>' && !(k > 0 && Code[k - 1] == '='))
            {
                var angle = Opening(Code, k, '<', '>');
                if (angle > 0) { k = angle - 1; while (k >= 0 && char.IsWhiteSpace(Code[k])) k--; }
            }
            var end = k + 1;
            while (k >= 0 && (char.IsLetterOrDigit(Code[k]) || Code[k] == '_')) k--;
            var method = Code[(k + 1)..end];
            // P7-48: async and static (CallKeywords) are lambda modifiers (static async (T x) =>), not a method's name.
            if (method.Length == 0 || char.IsDigit(method[0]) || CallKeywords.Contains(method)) return (null, open, parameters);
            var (last, word, spaced) = Before(Code, k + 1);
            if (!spaced || !(char.IsLetterOrDigit(last) || last is '_' or '>' or ']' or ')' or '?') || DeclarationBlockers.Contains(word)) return (null, open, parameters);
            return (method, k + 1, parameters);
        }

        // P7-46: every => of the code with its head and body [BodyStart, BodyEnd): an expression-bodied method or local
        // function (Method set, body up to its ;), or a lambda (Method null; x => or (...) =>, body a block or an expression
        // up to a , ; or closing bracket at its depth). An identifier after a type (an expression-bodied property, a
        // declaration pattern) or after a dot (an enum switch arm) is no lambda.
        List<(string? Method, int NameStart, List<(string Type, string Name)> Parameters, int BodyStart, int BodyEnd)>? arrows;
        List<(string? Method, int NameStart, List<(string Type, string Name)> Parameters, int BodyStart, int BodyEnd)> Arrows()
        {
            if (arrows is not null) return arrows;
            arrows = new();
            foreach (System.Text.RegularExpressions.Match a in System.Text.RegularExpressions.Regex.Matches(Code, @"(?<![=!<>])=>"))
            {
                var j = a.Index - 1;
                while (j >= 0 && char.IsWhiteSpace(Code[j])) j--;
                if (j < 0) continue;
                (string? Method, int NameStart, List<(string Type, string Name)> Parameters)? head = null;
                // P7-48: a method's head past its where clauses (HeadClose), not a lambda x => after the constraint.
                var close = HeadClose(a.Index);
                if (close >= 0 && close != j)
                {
                    head = Head(close);
                    if (head is not { Method: not null }) continue;
                }
                else if (Code[j] == ')') head = Head(j);
                else if (char.IsLetterOrDigit(Code[j]) || Code[j] == '_')
                {
                    var k = j;
                    while (k >= 0 && (char.IsLetterOrDigit(Code[k]) || Code[k] == '_')) k--;
                    var name = Code[(k + 1)..(j + 1)];
                    // P7-48: after async / static (a lambda modifier) the name is a lambda's parameter, not a property's.
                    var before = Before(Code, k + 1);
                    if (char.IsDigit(name[0]) || CallKeywords.Contains(name) || name is "class" or "struct" or "notnull" or "unmanaged" or "init" or "null" or "true" or "false"
                        || before.Last == '.' || (!LambdaModifiers.Contains(before.Word) && AfterType(Code, k + 1))) continue;
                    head = (null, k + 1, new List<(string Type, string Name)> { ("", name) });
                }
                if (head is not { } h) continue;
                var start = a.Index + 2;
                while (start < Code.Length && char.IsWhiteSpace(Code[start])) start++;
                int end;
                if (start < Code.Length && Code[start] == '{') end = Closing(Code, start, '{', '}');
                else if (h.Method is not null) end = StatementEnd(Code, start);
                else
                {
                    end = -1;
                    var depth = 0;
                    for (var i = start; i < Code.Length && end < 0; i++)
                    {
                        // P7-48: a generic call's type-argument list (M<A, B>() is skipped whole, so its commas do not cut.
                        if (Code[i] == '<' && TypeArgumentsEnd(Code, i) is var list && list > i) i = list;
                        else if (Code[i] is '(' or '{' or '[') depth++;
                        else if (Code[i] is ')' or '}' or ']') { if (--depth < 0) end = i - 1; }
                        else if (Code[i] is ';' or ',' && depth == 0) end = i - 1;
                    }
                }
                if (end >= start) arrows.Add((h.Method, h.NameStart, h.Parameters, start, end + 1));
            }
            return arrows;
        }

        // P7-46: whether a parameter type is a delegate the reach follows: Func<...>, Action, Predicate<...>,
        // Comparison<...>, Converter<...> (System. or not, nullable or not) or a type declared in the code as
        // delegate R Name( / Name<.
        readonly Dictionary<string, bool> delegateTypes = new(StringComparer.Ordinal);
        public bool IsDelegateType(string type)
        {
            var t = System.Text.RegularExpressions.Regex.Replace(type, @"\s+", "");
            if (delegateTypes.TryGetValue(t, out var known)) return known;
            var bare = System.Text.RegularExpressions.Regex.Replace(t, @"^(?:global::)?System\.", "");
            var found = System.Text.RegularExpressions.Regex.IsMatch(bare, @"^(?:(?:Func|Predicate|Comparison|Converter)<|Action(?:<|\??$))");
            var head = System.Text.RegularExpressions.Regex.Match(bare, @"^(?:[A-Za-z_]\w*\.)*(?<name>[A-Za-z_]\w*)");
            if (!found && head.Success)
                found = System.Text.RegularExpressions.Regex.IsMatch(Code, @"\bdelegate\s+[^;{}()=]*?\b" + head.Groups["name"].Value + @"\s*[<(]");
            return delegateTypes[t] = found;
        }

        // P7-44: the arguments passed for the parameter name (named name: or at position) by every call of method outside its
        // declarations: a local function's calls in its own member, a type member's unqualified calls inside its type and its
        // Type.method( / A.B.Type.method( calls anywhere.
        // P7-46: also a type member's calls through this. / base. and any other instance receiver (IsInstance: every such
        // call of that name, whatever its type, so over-approximated), and the calls of a local or field a method group of
        // method is assigned to (f = method; / var f = Type.method;, then f(, f.Invoke( or f?.Invoke(; a local's in its
        // member after the assignment, a field's anywhere). Every other use of method as a method group (passed on, in a
        // conditional, a local alias used other than called) is a gap, returned so the caller rejects it loudly.
        public (List<(int Start, int End)> Found, List<string> Gaps) Arguments(string method, string? type, int member, string name, int position)
        {
            var found = new List<(int Start, int End)>();
            var gaps = new List<string>();
            var declared = Methods(method).Select(d => d.Start).ToHashSet();
            var callOpen = new System.Text.RegularExpressions.Regex(@"\G" + TypeArguments + @"\s*\(");
            var aliases = new Queue<(string Name, int At)>();
            var queued = new HashSet<int>();
            void Take(int open)
            {
                var close = Closing(Code, open, '(', ')');
                if (close < 0) return;
                var arguments = Split(Code, open + 1, close, false);
                var named = new System.Text.RegularExpressions.Regex(@"^" + name + @"\s*:(?!:)\s*");
                var at = arguments.FindIndex(a => named.IsMatch(Code[a.Start..a.End]));
                (int Start, int End)? argument = null;
                if (at >= 0) argument = (arguments[at].Start + named.Match(Code[arguments[at].Start..arguments[at].End]).Length, arguments[at].End);
                else if (position < arguments.Count && !arguments.Take(position + 1).Any(a => System.Text.RegularExpressions.Regex.IsMatch(Code[a.Start..a.End], @"^[A-Za-z_]\w*\s*:(?!:)")))
                    argument = arguments[position];
                // P7-50: remembered as an argument (IsArgument), so References names a parameter passed on inside it.
                if (argument is { } taken) { found.Add(taken); argumentSpans.Add(taken); }
            }
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Code, @"\b" + method + @"\b"))
            {
                if (declared.Contains(m.Index)) continue;
                var (callTypes, callMember) = Scope(m.Index);
                var from = m.Index;
                if (Before(Code, m.Index).Last == '.')
                {
                    var dot = m.Index - 1;
                    while (Code[dot] != '.') dot--;
                    var receiver = Receiver(Code, dot);
                    if (member >= 0 || type is null) continue;
                    if (!(receiver is "this" or "base" ? callTypes.Contains(type) : IsInstance(this, receiver, m.Index) || receiver!.Split('.')[^1] == type)) continue;
                    from = ExpressionStart(m.Index);
                }
                else if (member >= 0 ? callMember != member : type is null || !callTypes.Contains(type)) continue;
                var call = callOpen.Match(Code, m.Index + m.Length);
                if (call.Success) { Take(call.Index + call.Length - 1); continue; }
                // A method group: nameof(method) names it only; alias = method; is followed through the alias's calls.
                var (last, _, _) = Before(Code, from);
                if (last == '(' && Before(Code, Code.LastIndexOf('(', from - 1)).Word == "nameof") continue;
                var alias = Alias(from, m.Index + m.Length);
                if (alias is null)
                {
                    // P7-50: a method group in a conditional or after ?? (f = on ? M : null; g ?? M) stays such a named gap.
                    gaps.Add(method + " used as a method group (passed on, in a conditional ?: or ??, or assigned other than alias = " + method + ";), so the arguments for its parameter "
                        + name + " are not all seen");
                    continue;
                }
                if (queued.Add(alias.Value.At)) aliases.Enqueue(alias.Value);
            }
            // P7-48: each alias's uses in turn: called (taken), assigned on as a whole to another alias (var g = f; G = F;
            // queued, so chains are followed), declared, assigned to or named by nameof (nothing passed on); any other use
            // of a local or field alias (passed on, returned) is a gap.
            var assignedTo = new System.Text.RegularExpressions.Regex(@"\G\s*(?:\+|-|\?\?)?=(?![=>])");
            while (aliases.Count > 0)
            {
                var (aliasName, aliasAt) = aliases.Dequeue();
                var (_, aliasMember) = Scope(aliasAt);
                var local = aliasMember >= 0 && (AfterType(Code, aliasAt) || Shadowed(aliasName, aliasAt));
                foreach (System.Text.RegularExpressions.Match use in System.Text.RegularExpressions.Regex.Matches(Code, @"\b" + aliasName + @"\b"))
                {
                    if (use.Index == aliasAt) continue;
                    if (local && (use.Index < aliasAt || Scope(use.Index).Member != aliasMember || Before(Code, use.Index).Last == '.')) continue;
                    var after = use.Index + use.Length;
                    var invoke = System.Text.RegularExpressions.Regex.Match(Code[after..Math.Min(Code.Length, after + 40)], @"^\s*(?:\(|\??\s*\.\s*Invoke\s*\()");
                    if (invoke.Success) { Take(after + invoke.Length - 1); continue; }
                    var useFrom = local ? use.Index : ExpressionStart(use.Index);
                    if (AfterType(Code, use.Index) || assignedTo.Match(Code, after).Success
                        || (Before(Code, useFrom).Last == '(' && Before(Code, Code.LastIndexOf('(', useFrom - 1)).Word == "nameof")) continue;
                    if (Alias(useFrom, after) is { } next)
                    {
                        if (queued.Add(next.At)) aliases.Enqueue(next);
                        continue;
                    }
                    // P7-50: a field alias's uses are matched by name over the whole read code (over-approximated), stated in its gap.
                    gaps.Add(aliasName + " (a " + (local ? "local" : "field") + " holding the method group " + method + ") used other than called"
                        + (local ? "" : " (a field alias is matched by name over the whole read code, so a same-named local, parameter or member elsewhere counts as its use: over-approximated)")
                        + ", so the arguments for " + name + " are not all seen");
                }
            }
            return (found, gaps);
        }

        // P7-46: the start of the receiver chain ending at the member name at index (A.B.name: the index of A).
        int ExpressionStart(int index)
        {
            var s = index;
            while (true)
            {
                var j = s - 1;
                while (j >= 0 && char.IsWhiteSpace(Code[j])) j--;
                if (j < 1 || Code[j] != '.' || Code[j - 1] == '?') return s;
                j--;
                while (j >= 0 && char.IsWhiteSpace(Code[j])) j--;
                var e = j + 1;
                while (j >= 0 && (char.IsLetterOrDigit(Code[j]) || Code[j] == '_')) j--;
                if (e == j + 1) return s;
                s = j + 1;
            }
        }

        // P7-46: the alias a method group [from, to) is assigned to as a whole statement (alias = / += / ??= group ; or ,), as
        // (name, index of the name), or null.
        (string Name, int At)? Alias(int from, int to)
        {
            var after = to;
            while (after < Code.Length && char.IsWhiteSpace(Code[after])) after++;
            if (after >= Code.Length || Code[after] is not (';' or ',')) return null;
            var j = from - 1;
            while (j >= 0 && char.IsWhiteSpace(Code[j])) j--;
            if (j < 1 || Code[j] != '=' || Code[j - 1] is '=' or '!' or '<' or '>') return null;
            j--;
            if (Code[j] == '+') j--;
            else if (j >= 1 && Code[j] == '?' && Code[j - 1] == '?') j -= 2;
            while (j >= 0 && char.IsWhiteSpace(Code[j])) j--;
            var end = j + 1;
            while (j >= 0 && (char.IsLetterOrDigit(Code[j]) || Code[j] == '_')) j--;
            var alias = Code[(j + 1)..end];
            if (alias.Length == 0 || char.IsDigit(alias[0]) || CallKeywords.Contains(alias)) return null;
            return (alias, j + 1);
        }

        // P7-44: whether a parameter or local named name is in scope at index (so name there is not a field): the parameter
        // declared at index itself, a parameter of an enclosing method or local function (Parameter), or a declaration after
        // a type (AfterType; not after out / ref) followed by = ; , or in, directly in a block enclosing index inside the same
        // member. P7-46: only a local declared at or before index (one declared later in the block leaves the field
        // followed), and Parameter also sees expression-bodied members' and lambdas' parameters.
        public bool Shadowed(string name, int index)
        {
            // A parameter's own declaration (inside parentheses, after a type, followed by , ) or =) is not the field.
            if (parens[index] > parens[enclosing[index] + 1] && AfterType(Code, index)
                && new System.Text.RegularExpressions.Regex(@"\G\s*(?:,|\)|=(?![=>]))").Match(Code, index + name.Length).Success) return true;
            var blocks = new HashSet<int>();
            for (var b = enclosing[index]; b >= 0 && !Types.ContainsKey(b); b = parent[b]) blocks.Add(b);
            if (Parameter(name, index) is not null) return true;
            if (blocks.Count == 0) return false;
            var local = new System.Text.RegularExpressions.Regex(@"\G\s*(?:=(?![=>])|;|,|in\b)");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(Code, @"\b" + name + @"\b"))
            {
                // P7-46: a local declared after index does not shadow the field at index (fail closed: the field is followed).
                if (m.Index > index) break;
                if (!blocks.Contains(enclosing[m.Index]) || !AfterType(Code, m.Index) || Before(Code, m.Index).Word is "out" or "ref") continue;
                if (local.Match(Code, m.Index + m.Length).Success) return true;
            }
            return false;
        }
    }

    // The receiver chain of a member access whose dot is at dot: "A.B.C" when it is a chain of names (null for an instance:
    // ?., a call result, an indexer, a literal).
    static string? Receiver(string code, int dot)
    {
        var segments = new List<string>();
        var j = dot;
        while (true)
        {
            var k = j - 1;
            while (k >= 0 && char.IsWhiteSpace(code[k])) k--;
            var end = k + 1;
            while (k >= 0 && (char.IsLetterOrDigit(code[k]) || code[k] == '_')) k--;
            if (end == k + 1) return null;
            segments.Insert(0, code[(k + 1)..end]);
            while (k >= 0 && char.IsWhiteSpace(code[k])) k--;
            if (k >= 1 && code[k] == '.' && code[k - 1] != '?') { j = k; continue; }
            if (k >= 0 && code[k] == '.') return null;
            return string.Join('.', segments);
        }
    }

    // P7-42 / P7-46: whether the receiver chain of a member access at index is an instance (see Reach): null (?., a call
    // result), a lowercase first segment that is no keyword type (this, a local, a parameter), a chain whose last declared
    // type is not its last segment, or, with no declared type, a first segment that is a field or property of an
    // enclosing type.
    static bool IsInstance(CodeMap map, string? receiver, int index)
    {
        if (receiver is null) return true;
        var segments = receiver.Split('.');
        var typed = Array.FindLastIndex(segments, map.IsType);
        return (char.IsLower(receiver[0]) && !KeywordTypes.Contains(segments[0]))
            || (typed >= 0 && typed < segments.Length - 1) || (typed < 0 && map.FieldTypes(segments[0], index).Count > 0);
    }

    // The references of the text [start, end): (label, the spans it reaches, an unresolved label or null).
    // P7-46: an unresolved label is name( or name( [why], the reason naming the parameter's kind and the limit hit.
    static List<(string Name, List<(int Start, int End)> Targets, string? Unresolved)> References(CodeMap map, int start, int end, IReadOnlySet<string>? external)
    {
        var code = map.Code;
        var found = new List<(string Name, List<(int Start, int End)> Targets, string? Unresolved)>();
        var none = new List<(int Start, int End)>();
        // P7-50: the value spans of the text when it is an argument passed for a followed delegate parameter.
        var values = map.IsArgument(start, end) ? Values(code, start, end) : new List<(int Start, int End)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code[start..end], @"\b([A-Za-z_]\w*)" + TypeArguments + @"(\s*[({])?"))
        {
            var name = m.Groups[1].Value;
            if (CallKeywords.Contains(name) || char.IsDigit(name[0])) continue;
            var at = start + m.Index;
            var opener = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            var call = opener == "(";
            var (last, word, _) = Before(code, at);
            if (word == "new" && char.IsLetter(last))
            {
                if (opener.Length > 0 && map.IsType(name))
                    found.Add(("new " + name, map.Types.Where(t => t.Value.Name == name).Select(t => (Start: t.Key, End: t.Value.Close + 1)).ToList(), null));
                continue;
            }
            if (last == '.')
            {
                var dot = at - 1;
                while (code[dot] != '.') dot--;
                var receiver = Receiver(code, dot);
                // P7-42: the last declared type of the chain, and whether the chain is an instance (see Reach).
                var segments = receiver?.Split('.') ?? Array.Empty<string>();
                var typed = Array.FindLastIndex(segments, map.IsType);
                if (IsInstance(map, receiver, at))
                {
                    if (call) found.Add((name, map.Types.Values.Select(t => t.Name).Distinct().SelectMany(t => map.MembersOf(t, name, false)).ToList(), null));
                }
                else if (typed >= 0)
                {
                    var type = segments[^1];
                    var targets = map.MembersOf(type, name, true);
                    if (map.IsField(type, name)) targets.AddRange(map.FieldAssignments(type, name));
                    if (call && targets.Count == 0) found.Add((type + "." + name, none, type + "." + name));
                    else found.Add((type + "." + name, targets, null));
                }
                else if (call && external is not null && !external.Contains(receiver + "." + name)) found.Add((receiver + "." + name, none, receiver + "." + name));
                continue;
            }
            // P7-44: a parameter of an enclosing method or local function (CodeMap.Parameter): a Func / Action parameter,
            // called or passed on, is followed to the arguments its callers pass for it; called with no such argument, or
            // any other parameter called, it is unresolved.
            // P7-46: a lambda's parameter (its arguments are not followed) or a parameter of a type that is no followed
            // delegate (IsDelegateType), called, is unresolved naming why; a method-group use of the parameter's method
            // that hides callers (Arguments' gaps) is unresolved whenever the parameter is referenced.
            if (map.Parameter(name, at) is { } parameter)
            {
                // P7-48: the whole text is this name (the argument a caller passes for a followed delegate parameter, as
                // Arguments returns it): a lambda's or a non-delegate parameter passed on there is named, not dropped.
                // P7-50: or the name is a value of that argument (Values: (test), test!, (T)test, c ? test : d, test ?? d).
                var passedOn = !call && values.Contains((at, at + m.Groups[1].Length));
                const string PassedOn = ", passed on (not called) for a followed delegate parameter";
                if (parameter.Lambda)
                {
                    var typed = parameter.Kind.Length > 0 ? " typed " + parameter.Kind : "";
                    found.Add((name, none, call ? name + "( [a parameter of a lambda" + typed + ": whoever invokes the lambda passes its arguments, which the reach does not follow]"
                        : passedOn ? name + " [a parameter of a lambda" + typed + PassedOn + ": whoever invokes the lambda passes its arguments, which the reach does not follow]" : null));
                    continue;
                }
                if (!parameter.Delegate)
                {
                    const string NotFollowed = "not a Func, Action, Predicate, Comparison, Converter or a delegate type declared in the read code, so not followed]";
                    found.Add((name, none, call ? name + "( [a " + parameter.Kind + " parameter of " + parameter.Method + ": " + NotFollowed
                        : passedOn ? name + " [a " + parameter.Kind + " parameter of " + parameter.Method + PassedOn + ": " + NotFollowed : null));
                    continue;
                }
                var (passed, gaps) = map.Arguments(parameter.Method, parameter.Type, parameter.Member, name, parameter.Position);
                found.Add((name, passed, call && passed.Count == 0 ? name + "( [the " + parameter.Kind + " parameter of " + parameter.Method + ": no caller passes it]" : null));
                foreach (var gap in gaps) found.Add((name, none, name + "( [the " + parameter.Kind + " parameter of " + parameter.Method + ": " + gap + "]"));
                continue;
            }
            // P7-42: a field or property of an enclosing type is followed through its assignments too.
            var assigned = map.FieldTypes(name, at).SelectMany(type => map.FieldAssignments(type, name)).ToList();
            if (call)
            {
                var targets = map.Visible(name, at, true, true).Concat(assigned).ToList();
                var enclosing = map.Scope(at).Types;
                var allowed = external is not null && enclosing.Count > 0 && external.Contains(enclosing[0] + "." + name);
                found.Add((name, targets, targets.Count == 0 && !allowed ? name : null));
            }
            else found.Add((name, map.Visible(name, at, true, false).Concat(assigned).ToList(), null));
        }
        return found;
    }

    // The declarations reached from the roots, transitively (see above), and the unresolved callees. Declarations inside a
    // root are not listed (the root's own text covers them).
    static (List<(string Name, int Start, int End)> Reached, List<string> Unresolved) Reach(CodeMap map, List<(int Start, int End)> roots, IReadOnlySet<string>? external)
    {
        var reached = new List<(string Name, int Start, int End)>();
        var unresolved = new List<string>();
        var seen = new HashSet<(int, int)>(roots);
        var texts = new Queue<(int Start, int End)>(roots);
        while (texts.Count > 0)
        {
            var (start, end) = texts.Dequeue();
            foreach (var (name, targets, problem) in References(map, start, end, external))
            {
                if (problem is not null) { if (!unresolved.Contains(problem)) unresolved.Add(problem); continue; }
                foreach (var target in targets)
                {
                    if (roots.Any(r => r.Start <= target.Start && target.End <= r.End) || !seen.Add(target)) continue;
                    reached.Add((name, target.Start, target.End));
                    texts.Enqueue(target);
                }
            }
        }
        return (reached, unresolved);
    }

    // ---- 15. P7-38: the section 14 write pins under mutation: one ForcedRun copy, helper-reachable writes, reformat ----
    //   a. ForcedRunCopyProblems (section 14 b) accepts the real tests file and rejects a second Console.SetOut (in
    //      ForcedRun or of section 12's own) and a copy of the helper (called by section 13 besides ForcedRun, reached from
    //      section 12 through a second helper, replacing section 13's call, inlined as section 13's own).
    //   b. ExecuteWriteProblems (section 14 c) rejects a stdout write reached from Execute through a new private helper of
    //      GenerateCommand (block or expression bodied, through a second helper, through Fail, through an IntentPlan
    //      Print, through a delegate field, and through a helper of Generate in GenerateCommand.cs).
    //   c. ExecuteWriteProblems accepts GenerateWrite.cs (and GenerateCommand.cs) reformatted (all on one line, every
    //      bracket / brace / semicolon / comma on its own line, the success object's trailing comma removed) and still
    //      rejects each of section 14's 16 mutants applied to the reformatted source.
    static void WritePinReachStage(string root, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-38 " + name, detail);
        // Each edit's pattern must match exactly once; null when one does not.
        static string? Mutate(string code, params (string Pattern, string Replacement)[] edits)
        {
            foreach (var (pattern, replacement) in edits)
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern);
                if (regex.Matches(code).Count != 1) return null;
                code = regex.Replace(code, replacement.Replace("$", "$$", StringComparison.Ordinal), 1);
            }
            return code;
        }
        static string Problems(List<string> problems) => "problems [" + string.Join("; ", problems) + "]";

        // a. The one-copy ForcedRun pin.
        var tests = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "tests", "cli", "GeneratePrototypesTests.cs")));
        var realCopy = ForcedRunCopyProblems(tests);
        Check(realCopy.Count == 0, "one-copy pin accepts the real tests file", Problems(realCopy));
        const string ForcedSetOut = @"Console\s*\.\s*SetOut\s*\(\s*stdout\s*\)\s*;\s*Console\s*\.\s*SetError\s*\(\s*stderr\s*\)\s*;\s*Environment\s*\.\s*ExitCode\s*=\s*0\s*;\s*seams\s*\[";
        const string TwelveCall = @"ForcedRun\s*\(\s*runMethod\s*,\s*seams\s*,\s*stdout\s*,\s*before\s*,\s*work\s*,\s*stage\s*,\s*args\s*\)";
        const string ThirteenCall = @"var\s+r\s*=\s*ForcedRun\s*\(\s*runMethod\s*,\s*seams\s*,\s*stdout\s*,\s*null\s*,\s*null\s*,\s*stage\s*,\s*args\s*\)\s*;";
        const string ClassMember = @"\bstatic\s+void\s+PartialWriterExecuteStage\s*\(";
        const string Copy = "static void ForcedRunCopy(System.Reflection.MethodInfo run, TextWriter stdout, string[] args) { var previous = Console.Out; "
            + "try { Console.SetOut(stdout); run.Invoke(null, new object?[] { args }); } finally { Console.SetOut(previous); } } static void PartialWriterExecuteStage(";
        // P7-42: each mutant of this section names the problem it must be rejected for (a substring of one problem).
        static bool Names(List<string> problems, string reason) => problems.Any(p => p.Contains(reason, StringComparison.Ordinal));
        const string CopyReached = "section 12 or 13 reaches ForcedRunCopy, which sets stdout or invokes by reflection";
        var copyMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("a second Console.SetOut in ForcedRun", new[] { (ForcedSetOut, "Console.SetOut(stdout); Console.SetOut(stdout); Console.SetError(stderr); Environment.ExitCode = 0; seams[") },
                "ForcedRun does not call Console.SetOut exactly twice"),
            ("a Console.SetOut of section 12's own", new[] { (TwelveCall, "Console.SetOut(stdout); ForcedRun(runMethod, seams, stdout, before, work, stage, args)") }, "section 12 calls Console.SetOut 1 times itself"),
            ("a copied helper called by section 13 besides ForcedRun", new[] { (ThirteenCall, "ForcedRunCopy(runMethod, stdout, args); var r = ForcedRun(runMethod, seams, stdout, null, null, stage, args);"), (ClassMember, Copy) }, CopyReached),
            ("a copied helper reached from section 12 through a second helper", new[] { (TwelveCall, "Prepare(runMethod, stdout, args); ForcedRun(runMethod, seams, stdout, before, work, stage, args)"),
                (ClassMember, "static void Prepare(System.Reflection.MethodInfo run, TextWriter stdout, string[] args) => ForcedRunCopy(run, stdout, args); " + Copy) }, CopyReached),
            ("a copied helper replacing section 13's call", new[] { (ThirteenCall, "ForcedRunCopy(runMethod, stdout, args); var r = (Thrown: (Exception?)null, Events: new List<string>(), Exit: 0, Out: stdout, Restored: true);"), (ClassMember, Copy) }, CopyReached),
            ("a copy inlined in section 13", new[] { (ThirteenCall, "var previous = Console.Out; Console.SetOut(stdout); runMethod.Invoke(null, new object?[] { args }); Console.SetOut(previous); var r = ForcedRun(runMethod, seams, stdout, null, null, stage, args);") },
                "section 13 calls Console.SetOut 2 times itself"),
        };
        foreach (var (label, edits, reason) in copyMutants)
        {
            var mutated = Mutate(tests, edits);
            var problems = mutated is null ? new List<string>() : ForcedRunCopyProblems(mutated);
            Check(mutated is not null && Names(problems, reason), "one-copy self-test: " + label + " is rejected for " + reason, (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // b. Execute's pin over the helpers it reaches.
        var write = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateWrite.cs")));
        var command = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateCommand.cs")));
        var followed = ExecuteFollowedCode(root);
        var real = ExecuteWriteProblems(write, command, followed);
        Check(real.Count == 0, "Execute pin accepts the real GenerateWrite.cs and GenerateCommand.cs", Problems(real));
        const string AfterOutput = @"var\s+output\s*=\s*args\s*\[\s*2\s*\]\s*;";
        const string AfterParsed = @"var\s+parsed\s*=\s*generated\s*\.\s*Parsed\s*;";
        const string AfterResult = @"var\s+result\s*=\s*generated\s*\.\s*Result\s*;";
        const string BeforeFail = @"\bstatic\s+void\s+Fail\s*\(";
        const string Through = "Execute reaches a stdout Console member through the helper ";
        var helperMutants = new (string Label, (string Pattern, string Replacement)[] Write, (string Pattern, string Replacement)[] Command, string Reason)[]
        {
            ("a private helper writing stdout, called before the pipeline",
                new[] { (AfterOutput, "var output = args[2]; Announce();"), (BeforeFail, "static void Announce() { Console.Out.Write(string.Empty); } static void Fail(") }, Array.Empty<(string, string)>(), Through + "Announce"),
            ("an expression-bodied private helper writing the document",
                new[] { (AfterParsed, "var parsed = generated.Parsed; Echo(generated.Json);"), (BeforeFail, "static void Echo(string line) => Console.WriteLine(line); static void Fail(") }, Array.Empty<(string, string)>(), Through + "Echo"),
            ("a private helper writing stdout through a second helper",
                new[] { (AfterResult, "var result = generated.Result; Prepare(result);"), (BeforeFail, "static void Prepare(PrototypePipelineResult result) { Emit(result.Alternatives.Count); } static void Emit(int count) { Console.Write(count); } static void Fail(") }, Array.Empty<(string, string)>(), Through + "Emit"),
            ("a private helper calling Fail",
                new[] { (AfterOutput, "var output = args[2]; Report();"), (BeforeFail, "static void Report() => Fail(UsageCode, null); static void Fail(") }, Array.Empty<(string, string)>(), Through + "Fail"),
            ("a private helper printing an IntentPlan envelope",
                new[] { (AfterOutput, "var output = args[2]; Warn(args);"), (BeforeFail, "static void Warn(string[] args) { IntentPlan.Internal(args[0], new InvalidOperationException()).Print(); } static void Fail(") }, Array.Empty<(string, string)>(),
                "Execute reaches Print( through the helper Warn"),
            ("a delegate field writing stdout",
                new[] { (AfterParsed, "var parsed = generated.Parsed; Echo(generated.Json);"), (BeforeFail, "static readonly Action<string> Echo = Console.WriteLine; static void Fail(") }, Array.Empty<(string, string)>(), Through + "Echo"),
            ("a helper of Generate in GenerateCommand.cs writing stdout", Array.Empty<(string, string)>(),
                new[] { (@"var\s+parsed\s*=\s*Parse\s*\(\s*text\s*\)\s*;", "var parsed = Parse(text); Log(parsed);"), (@"\bstatic\s+Parsed\s+Parse\s*\(", "static void Log(Parsed parsed) { Console.WriteLine(parsed.Pack); } static Parsed Parse(") }, Through + "Log"),
        };
        foreach (var (label, writeEdits, commandEdits, reason) in helperMutants)
        {
            var mutatedWrite = Mutate(write, writeEdits);
            var mutatedCommand = Mutate(command, commandEdits);
            var problems = mutatedWrite is null || mutatedCommand is null ? new List<string>() : ExecuteWriteProblems(mutatedWrite, mutatedCommand, followed);
            Check(mutatedWrite is not null && mutatedCommand is not null && Names(problems, reason), "Execute self-test: " + label + " is rejected for " + reason,
                (mutatedWrite is null || mutatedCommand is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // c. Reformat tolerance, with section 14's mutants still rejected on the reformatted source.
        static string OneLine(string code) => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");
        static string Spread(string code) => System.Text.RegularExpressions.Regex.Replace(code, @"\s*([(){};,])\s*", "\n    $1 ");
        static string NoTrailingComma(string code) => SuccessTrailingCommaRemoved(code) ?? code;
        var reformats = new (string Label, string Write, string Command)[]
        {
            ("all on one line", OneLine(write), OneLine(command)),
            ("every bracket, brace, semicolon and comma on its own line", Spread(write), Spread(command)),
            ("the success object's trailing comma removed", NoTrailingComma(write), command),
            ("spread and without the trailing comma", Spread(NoTrailingComma(write)), Spread(command)),
        };
        foreach (var (label, reformatted, reformattedCommand) in reformats)
        {
            var problems = ExecuteWriteProblems(reformatted, reformattedCommand, followed);
            Check(reformatted != write && problems.Count == 0, "reformat self-test: Execute pin accepts GenerateWrite.cs " + label, Problems(problems));
        }
        var spreadWrite = Spread(NoTrailingComma(write));
        var spreadCommand = Spread(command);
        foreach (var (label, pattern, replacement) in ExecuteMutants())
        {
            var mutated = Mutate(spreadWrite, (pattern, replacement));
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(mutated, spreadCommand, followed);
            var reason = ExecuteMutantReason(label);
            Check(mutated is not null && Names(problems, reason), "reformat self-test: " + label + " is still rejected on the spread source without the trailing comma, for " + reason,
                (mutated is null ? "the anchor does not match once; " : "") + Problems(problems));
        }
    }

    // P7-40: section 15's trailing-comma reformat: GenerateWrite.cs with the success object's trailing comma (after sha256)
    // removed and no other change; null unless that comma is found exactly once.
    static string? SuccessTrailingCommaRemoved(string code)
    {
        const string SuccessComma = @"\bsha256\s*,(\s*\}\s*\)\s*\)\s*;)";
        return System.Text.RegularExpressions.Regex.Matches(code, SuccessComma).Count == 1
            ? System.Text.RegularExpressions.Regex.Replace(code, SuccessComma, "sha256$1") : null;
    }

    // ---- 16. P7-40: the section 14 write pins through generic, qualified, method-group, delegate and cross-class calls ----
    //   a. ExecuteWriteProblems (section 14 c) rejects a stdout write hidden behind an explicit generic call Echo<string>(,
    //      an inferred call of a generic helper with a where clause, a qualified GenerateCommand.Echo( and
    //      Bim.Cli.GenerateCommand.Echo( call, a method group (bare or qualified) passed to List.ForEach or assigned to a
    //      local delegate, a static method of a class nested in GenerateCommand or declared beside it, an instance method
    //      and a constructor of a class declared beside it, and a call into a class whose source the pin does not read
    //      (GltfExportCommand.Run, rejected as unresolved).
    //   b. ForcedRunCopyProblems (section 14 b) rejects a copy of ForcedRun that section 13 reaches only through a delegate
    //      field, through the lambda its caller passes as cli, through an explicit generic call, a qualified
    //      GeneratePrototypesTests. call or a method group passed to Array.ForEach.
    //   c. Type membership, not brace depth 1: both pins accept their sources inside a block-scoped namespace (members at
    //      brace depth 2) and still reject a mutant there.
    //   d. SuccessTrailingCommaRemoved (section 15's reformat) removes the success object's trailing comma only: another
    //      trailing comma added to GenerateWrite.cs is kept.
    //   P7-42: every mutant of sections 15 and 16 is rejected for its own reason (a substring of one problem), not only
    //   rejected; and
    //   e. a delegate field or property assigned outside its declaration (in a static constructor, a method, another class's
    //      expression-bodied method as GenerateCommand.Echo =, or added to with +=) and invoked as Echo?.Invoke( or Echo( (the
    //      PostWriteStageProbe shape) is followed to the assigned delegate, in Execute's pin and in the one-copy pin;
    //   f. an uppercase property chain (Result.Items.Count(), GenerateCommand.Result.Items.Count()) is an instance receiver,
    //      not an unresolved external call, and its method is followed (Result.Say() writing stdout is rejected);
    //   g. nested generic and tuple type arguments (Run<Dictionary<string, List<int>>>(, Run<(int Count, string Name)>(,
    //      Emit<(int, int)>() are calls: unlisted or undeclared, they are unresolved;
    //   h. Execute's calls into OutputProblem.cs, BoundedInput.cs, SkillPackLookup.cs and IntentPlan.cs are followed into
    //      those files: a stdout write hidden in each is rejected.
    //   P7-44:
    //   i. BoundedInput.Stat's Func parameter call( is followed to the lambdas its callers pass (no BoundedInput.call
    //      allowance): an unrelated call( and a call( of a non-delegate parameter are unresolved, and a stdout write in a
    //      lambda a new caller passes to Stat is rejected through call;
    //   j. a local or parameter shadowing a delegate field armed with a stdout write is not followed to the field, a
    //      shadowing local's assignment and a tuple-typed local declaration are not field assignments; a field assignment
    //      after if (on) and a local of the same name in another method still leave the field followed;
    //   k. GltfExporter.WriteNew is followed into GltfExporter.cs (not listed in ExecuteExternalCalls): a stdout write in it
    //      and in the wrap lambda WriteGltf passes to it are rejected.
    //   P7-46:
    //   l. Stat's Func argument is followed through this.Stat(, an instance receiver input.Stat( and a local the method group
    //      Stat is assigned to (var f = Stat; f(...), f = Stat; f?.Invoke(...)): a stdout lambda passed so is rejected; Stat
    //      passed on as a method group, or a method-group local passed on, is unresolved naming the gap;
    //   m. a Predicate<int> parameter and a parameter of a delegate type declared in the code are followed to their callers'
    //      lambdas (stdout rejected); a called parameter of a type the code does not declare is unresolved naming its type;
    //   n. a field used before a local of the same name is declared in the same block stays followed (stdout rejected);
    //   o. the Func parameter of an expression-bodied method or local function is followed (stdout rejected); a lambda's
    //      parameter (untyped or typed), called, is unresolved naming that limit.
    //   P7-48:
    //   p. an async, static or static async (typed) lambda's parameter, called, is unresolved as a lambda's parameter;
    //   q. a lambda head is no declarator: the wrap lambda WriteGltf passes, its parameter named stream, writing stdout is
    //      rejected through wrap (not reached first as a declarator of stream); Declarators on a small text keeps an
    //      expression-bodied property x => and drops the lambda heads x =>, async x =>, static x =>;
    //   r. Pick<int, int>(...) cuts neither a lambda's expression body (its called parameter is still the lambda's) nor a
    //      caller's argument list (the stdout lambda after it is followed at its position);
    //   s. Stat's method group through a local chain (var f = Stat; var g = f; g(...)) and a field chain (StatAlias = Stat;
    //      StatChain = StatAlias; StatChain(...)) is followed (stdout rejected); a field alias passed on is a named gap;
    //   t. the Func parameter of a block- or expression-bodied generic method with where clauses (new() included) is
    //      followed (stdout rejected);
    //   u. a lambda's parameter or a Notifier parameter passed on as the argument of a followed Func parameter is
    //      unresolved naming that it was passed on, not called.
    //   P7-50:
    //   v. a where inside an attribute or a default value, or a LINQ where after a call (in a foreach head, before a lambda),
    //      opens no method head: test stays M's delegate parameter; a where T : constraint (block- or expression-bodied)
    //      still exposes it;
    //   w. TypeArgumentsEnd and Split: a cast (List<A, B>)x, a pattern is / case Dictionary<A, B> d and a call N<A, B>(x)
    //      are one list (not cut); a < b + 1, c > (d) and a < b, c > d are comparisons; a < b > (c) is a generic call (the
    //      stated limit, as C# parses it);
    //   x. a lambda's parameter passed on as (test), test!, a conditional's branch or a side of ??, and a Notifier
    //      parameter passed on through a cast (Func<int>)test, are unresolved naming that they were passed on;
    //   y. a field alias's gap states its name match is over-approximated (a same-named local elsewhere passed on);
    //   z. Stat as a method group in a conditional or after ?? is a gap naming that use.
    //   P7-52 (letters continue after z):
    //   aa. Values: a parenthesised operand before ?? + or - ((test) ?? d, (test) + 1, (test) - d, other + (test)) keeps
    //       test as a value, casts ((Func<int>)test, (Func<int>)(test), (Func<int>)test!) still do, (int)-test's value is
    //       -test; a lambda's parameter passed on as (test) ?? (() => 0) or (test) + 1 is unresolved naming that;
    //   ab. a nullable generic's ? before ?? (new List<int?>() ?? test, spaced List<int? >, (List<int?>)test ?? d) or in a
    //       conditional's branch is no conditional: test stays a value, and passed on that way it is named.
    //   The w case-pattern entry is a switch statement inside a lambda's block body (a valid switch form).
    //   P7-54: w also pins a < b, c > (d) as one argument (a comparison would be two);
    //   ac. Values: a null-forgiving parenthesised operand before ?? ((test)! ?? d) keeps test as a value, (test)!.M and
    //       (bool)!test do not; a conditional with a collection branch (c ? [x] : test, c ?[x] : test) keeps test, as do an
    //       element access in a branch or before ?? (c ? a?[0] : test, a?[0] ?? test); a lambda's parameter and a Notifier
    //       parameter passed on as (test)! ?? d, (test) ?? d or after a collection branch are unresolved naming that;
    //   ad. an exponent literal's sign (1e-5, 2.5E+3, 1_0e-5) splits no value; a hex 0x1e-5 is a subtraction.
    //   P7-56:
    //   ae. Values: a ?[ collection branch inside a conditional's branch (c ? b ?[x] : test : d) is a nested conditional,
    //       while an element access there (c ? a?[0] : test) is not; (test)!(x) is a cast of !(x), as C# reads it (test is
    //       no value); a suffixed exponent 1e5f - test and 1e-5f, a separated hex 0x_1e-5, a leading-dot .5e-3 and a member
    //       access on an integer 1.E - test have self-tests; a lambda's parameter passed on after a nested collection branch
    //       is unresolved naming that.
    //   P7-58:
    //   af. the aa / ac / ad / ae value and exponent self-tests run through one table-driven helper (ValueSelfTests, the
    //       cases and check names unchanged); the mixed branch c ? a?[0] : b ?[x] : test : d is pinned as read (both ?[
    //       nested); a separator next to e (1_e-5, 1.5_e-3) is a subtraction, 1__0e-5 one value; 2.5.X1e - test, a spaced
    //       sign 1e - test and 1e /*c*/ - test as comment-stripped are subtractions, while on text with the comment kept it
    //       is one value (stated limit: Values reads comment-stripped code); a lambda's parameter written (test)!(0) is a
    //       stated reach gap naming no problem of test (C# reads a cast of !(0)), while test(0) is named.
    static void WritePinScopeStage(string root, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: P7-40 " + name, detail);
        // Each edit's pattern must match exactly once; null when one does not.
        static string? Mutate(string code, params (string Pattern, string Replacement)[] edits)
        {
            foreach (var (pattern, replacement) in edits)
            {
                var regex = new System.Text.RegularExpressions.Regex(pattern);
                if (regex.Matches(code).Count != 1) return null;
                code = regex.Replace(code, replacement.Replace("$", "$$", StringComparison.Ordinal), 1);
            }
            return code;
        }
        static int Count(string code, string pattern) => System.Text.RegularExpressions.Regex.Matches(code, pattern).Count;
        static string Problems(List<string> problems) => "problems [" + string.Join("; ", problems) + "]";
        var write = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateWrite.cs")));
        var command = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateCommand.cs")));
        var tests = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "tests", "cli", "GeneratePrototypesTests.cs")));
        // P7-44: the followed files are read from the root this stage receives (no second global.json search, no cache).
        var followed = ExecuteFollowedCode(root);

        // a. Execute's pin through generic, qualified, method-group, delegate and cross-class calls.
        const string AfterOutput = @"var\s+output\s*=\s*args\s*\[\s*2\s*\]\s*;";
        const string AfterParsed = @"var\s+parsed\s*=\s*generated\s*\.\s*Parsed\s*;";
        const string AfterResult = @"var\s+result\s*=\s*generated\s*\.\s*Result\s*;";
        const string BeforeFail = @"\bstatic\s+void\s+Fail\s*\(";
        const string ClassHeader = @"\binternal\s+static\s+partial\s+class\s+GenerateCommand\b";
        const string Echo = "static void Echo(string line) => Console.WriteLine(line); static void Fail(";
        const string Parsed = "var parsed = generated.Parsed; ";
        // P7-42: each mutant names the problem it must be rejected for (a substring of one of ExecuteWriteProblems' problems).
        const string Through = "Execute reaches a stdout Console member through the helper ";
        const string Holder = "static Holder Result { get; } = new(); sealed class Holder { internal int[] Items { get; } = new int[0]; internal void Say() => Console.WriteLine(); } static void Fail(";
        var executeMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("an explicit generic call Echo<string>(", new[] { (AfterParsed, Parsed + "Echo<string>(generated.Json);"), (BeforeFail, "static void Echo<T>(T line) => Console.WriteLine(line); static void Fail(") }, Through + "Echo"),
            ("an inferred call of a generic helper with a where clause", new[] { (AfterParsed, Parsed + "Echo(generated.Json);"),
                (BeforeFail, "static void Echo<T>(T line) where T : class { Console.Out.Write(line); } static void Fail(") }, Through + "Echo"),
            ("a qualified GenerateCommand.Echo( call", new[] { (AfterParsed, Parsed + "GenerateCommand.Echo(generated.Json);"), (BeforeFail, Echo) }, Through + "GenerateCommand.Echo"),
            ("a namespace-qualified Bim.Cli.GenerateCommand.Echo( call", new[] { (AfterParsed, Parsed + "Bim.Cli.GenerateCommand.Echo(generated.Json);"), (BeforeFail, Echo) }, Through + "GenerateCommand.Echo"),
            ("a method group passed to List.ForEach", new[] { (AfterParsed, Parsed + "new List<string> { generated.Json }.ForEach(Echo);"), (BeforeFail, Echo) }, Through + "Echo"),
            ("a qualified method group passed to List.ForEach", new[] { (AfterParsed, Parsed + "new List<string> { generated.Json }.ForEach(GenerateCommand.Echo);"), (BeforeFail, Echo) }, Through + "GenerateCommand.Echo"),
            ("a method group assigned to a local delegate", new[] { (AfterParsed, Parsed + "Action<string> say = Echo; say(generated.Json);"), (BeforeFail, Echo) }, Through + "Echo"),
            ("a static method of a class nested in GenerateCommand", new[] { (AfterParsed, Parsed + "Echoes.Line(generated.Json);"),
                (BeforeFail, "static class Echoes { internal static void Line(string line) => Console.WriteLine(line); } static void Fail(") }, Through + "Echoes.Line"),
            ("a static method of a class declared beside GenerateCommand", new[] { (AfterParsed, Parsed + "Echoes.Line(generated.Json);"),
                (ClassHeader, "static class Echoes { internal static void Line(string line) => Console.WriteLine(line); } internal static partial class GenerateCommand") }, Through + "Echoes.Line"),
            ("an instance method of a class declared beside GenerateCommand", new[] { (AfterParsed, Parsed + "new Echoer().Say(generated.Json);"),
                (ClassHeader, "sealed class Echoer { internal void Say(string line) => Console.Out.WriteLine(line); } internal static partial class GenerateCommand") }, Through + "Say"),
            ("a constructor of a class declared beside GenerateCommand", new[] { (AfterParsed, Parsed + "_ = new Echoer(generated.Json);"),
                (ClassHeader, "sealed class Echoer { internal Echoer(string line) { Console.Write(line); } } internal static partial class GenerateCommand") }, Through + "new Echoer"),
            ("a call into a class the pin does not read (GltfExportCommand.Run)", new[] { (AfterOutput, "var output = args[2]; GltfExportCommand.Run(args);") }, "Execute reaches GltfExportCommand.Run("),
            // P7-42 (e): a delegate field or property assigned outside its declaration (the PostWriteStageProbe shape).
            ("a delegate field assigned in a static constructor, invoked as Echo?.Invoke(", new[] { (AfterParsed, Parsed + "Echo?.Invoke(generated.Json);"),
                (BeforeFail, "static Action<string>? Echo; static GenerateCommand() { Echo = line => Console.WriteLine(line); } static void Fail(") }, Through + "Echo"),
            ("a delegate property assigned in a method, invoked as Echo?.Invoke()", new[] { (AfterParsed, Parsed + "Echo?.Invoke();"),
                (BeforeFail, "internal static Action? Echo { get; set; } static void Arm() { Echo = () => Console.Out.Write(string.Empty); } static void Fail(") }, Through + "Echo"),
            ("a delegate property assigned as GenerateCommand.Echo by another class's expression-bodied method", new[] { (AfterParsed, Parsed + "Echo?.Invoke();"),
                (BeforeFail, "internal static Action? Echo { get; set; } static void Fail("),
                (ClassHeader, "static class Arming { internal static void Arm() => GenerateCommand.Echo = () => Console.WriteLine(); } internal static partial class GenerateCommand") }, Through + "Echo"),
            ("a delegate field initialized to null, added to in a static constructor, called Echo(", new[] { (AfterParsed, Parsed + "Echo(generated.Json);"),
                (BeforeFail, "static Action<string>? Echo = null; static GenerateCommand() { Echo += Console.WriteLine; } static void Fail(") }, Through + "Echo"),
            // P7-42 (f): an uppercase property receiver is an instance: its method is followed.
            ("an instance method reached through an uppercase property Result.Say()", new[] { (AfterResult, "var result = generated.Result; Result.Say();"), (BeforeFail, Holder) }, Through + "Say"),
            // P7-42 (g): nested generic and tuple type arguments are calls.
            ("an unlisted external call with a nested generic type argument", new[] { (AfterOutput, "var output = args[2]; GltfExportCommand.Run<Dictionary<string, List<int>>>(args);") }, "Execute reaches GltfExportCommand.Run("),
            ("an unlisted external call with a named tuple type argument", new[] { (AfterOutput, "var output = args[2]; GltfExportCommand.Run<(int Count, string Name)>(args);") }, "Execute reaches GltfExportCommand.Run("),
            ("an undeclared callee with a tuple type argument", new[] { (AfterOutput, "var output = args[2]; Emit<(int, int)>((1, 2));") }, "Execute reaches Emit("),
            ("a helper called with a nested generic tuple type argument", new[] { (AfterParsed, Parsed + "Echo<Dictionary<string, List<(int, string)>>>(null);"),
                (BeforeFail, "static void Echo<T>(T value) => Console.WriteLine(value); static void Fail(") }, Through + "Echo"),
        };
        foreach (var (label, edits, reason) in executeMutants)
        {
            var mutated = Mutate(write, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(mutated, command, followed);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "Execute self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // P7-42 (f): uppercase property chains whose methods are not declared in the code are not unresolved external calls.
        var uppercase = new (string Label, (string Pattern, string Replacement)[] Edits)[]
        {
            ("Result.Items.Count()", new[] { (AfterResult, "var result = generated.Result; var count = Result.Items.Count();"), (BeforeFail, Holder) }),
            ("GenerateCommand.Result.Items.Count()", new[] { (AfterResult, "var result = generated.Result; var count = GenerateCommand.Result.Items.Count();"), (BeforeFail, Holder) }),
        };
        foreach (var (label, edits) in uppercase)
        {
            var mutated = Mutate(write, edits);
            var problems = mutated is null ? new List<string> { "an anchor does not match once" } : ExecuteWriteProblems(mutated, command, followed);
            Check(problems.Count == 0, "receiver self-test: Execute pin accepts an uppercase property chain " + label, Problems(problems));
        }

        // P7-42 (h): the calls into the repository's CLI helpers are followed into their files: a stdout write hidden in each
        // is rejected.
        var followedMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("BoundedInput.ReadText", new[] { (@"var\s+bytes\s*=\s*ReadBytes\s*\(\s*path\s*,\s*MaxBytes\s*,\s*share\s*\)\s*;", "Console.WriteLine(path); var bytes = ReadBytes(path, MaxBytes, share);") },
                Through + "BoundedInput.ReadText"),
            ("GltfExportCommand.OutputProblem", new[] { (@"\bformat\s*=\s*""""\s*;", "format = \"\"; Console.Out.Write(path);") }, Through + "GltfExportCommand.OutputProblem"),
            ("SkillPackLookup.Load", new[] { (@"if\s*\(\s*pack\s*\.\s*Id\s*!=\s*packId\s*\)", "Console.WriteLine(packId); if (pack.Id != packId)") }, Through + "SkillPackLookup.Load"),
            ("IntentPlan.InternalDiagnostic", new[] { (@"InternalDiagnostic\s*\(\s*string\s+verb\s*,\s*Exception\s+error\s*\)\s*=>\s*verb\b", "InternalDiagnostic(string verb, Exception error) => Shout(verb)"),
                (@"\bstatic\s+IntentStep\s*<\s*T\s*>\s+Refuse\s*<", "static string Shout(string text) { Console.WriteLine(text); return text; } static IntentStep<T> Refuse<") }, Through + "Shout"),
        };
        foreach (var (label, edits, reason) in followedMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "followed self-test: a stdout write in " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // P7-44 (i): BoundedInput.Stat's Func parameter call( is followed to the arguments Stat's callers pass (no
        // BoundedInput.call allowance), and (k) GltfExporter.WriteNew is followed into GltfExporter.cs, its wrap parameter to
        // the lambdas its callers pass. Each mutant edits the followed code and names the problem it must be rejected for.
        const string ReadBytesCall = @"var\s+bytes\s*=\s*ReadBytes\s*\(\s*path\s*,\s*MaxBytes\s*,\s*share\s*\)\s*;";
        const string RequireRegular = @"\bstatic\s+void\s+RequireUnixRegular\s*\(";
        var parameterMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("an unrelated call( in a BoundedInput helper ReadText reaches", new[] { (ReadBytesCall, "Poke(path); var bytes = ReadBytes(path, MaxBytes, share);"),
                (RequireRegular, "static void Poke(string path) { call(path); } static void RequireUnixRegular(") }, "Execute reaches call("),
            ("a call( of a BoundedInput parameter named call that is not a delegate", new[] { (ReadBytesCall, "Poke(path); var bytes = ReadBytes(path, MaxBytes, share);"),
                (RequireRegular, "static void Poke(string call) { call(); } static void RequireUnixRegular(") }, "Execute reaches call("),
            ("a stdout write in a lambda another BoundedInput caller passes to Stat", new[] {
                (RequireRegular, "static bool Noisy(out int mode) => Stat(status => { Console.WriteLine(); return 0; }, out mode); static void RequireUnixRegular(") }, Through + "call"),
            ("a stdout write in GltfExporter.WriteNew", new[] { (@"try\s*\{\s*file\s*=\s*new\s+FileStream\s*\(", "Console.WriteLine(path); try { file = new FileStream(") },
                Through + "GltfExporter.WriteNew"),
            // The lambda's parameter is not named stream: a lambda stream => ... directly in a type body is also a declarator
            // of the name stream, which WriteNew references, and would be reached (labelled stream) before wrap.
            ("a stdout write in the wrap lambda GltfExporter.WriteGltf passes to WriteNew", new[] {
                (@"ExportGltf\s*\(\s*project\s*\)\s*\)\s*,\s*path\s*,\s*stream\s*=>\s*stream\s*\)", "ExportGltf(project)), path, written => { Console.WriteLine(); return written; })") }, Through + "wrap"),
        };
        foreach (var (label, edits, reason) in parameterMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-44 parameter self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }
        Check(!ExecuteExternalCalls.Contains("BoundedInput.call") && !ExecuteExternalCalls.Contains("GltfExporter.WriteNew"),
            "P7-44 ExecuteExternalCalls lists neither BoundedInput.call (Stat's Func argument is followed) nor GltfExporter.WriteNew (followed into GltfExporter.cs)");

        // P7-44 (j): a local or parameter named like a field is not that field (its assignments are not followed from the
        // local's scope, and the local's own assignments are not field assignments), and a tuple-typed local declaration is
        // not an assignment; a field assignment after if (...) and a local of another method still leave the field followed.
        const string Field = "static Action? Echo; ";
        var shadowMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string? Reason)[]
        {
            ("a local Echo shadowing a delegate field armed with a stdout write", new[] { (AfterParsed, Parsed + "Quiet();"),
                (BeforeFail, Field + "static void Arm() { Echo = () => Console.WriteLine(); } static void Quiet() { Action Echo = () => { }; Echo = () => { }; Echo(); } static void Fail(") }, null),
            ("a parameter Echo shadowing a delegate field armed with a stdout write", new[] { (AfterParsed, Parsed + "Relay();"),
                (BeforeFail, Field + "static void Arm() { Echo = () => Console.WriteLine(); } static void Relay() { Quiet(() => { }); } static void Quiet(Action Echo) { Echo(); } static void Fail(") }, null),
            ("a stdout write assigned to a shadowing local in a method Execute does not reach", new[] { (AfterParsed, Parsed + "Echo?.Invoke();"),
                (BeforeFail, Field + "static void Arm() { Echo = () => { }; } static void Other() { Action Echo = () => { }; Echo = () => Console.WriteLine(); Echo(); } static void Fail(") }, null),
            ("a tuple-typed local declaration named like the field", new[] { (AfterParsed, Parsed + "Echo?.Invoke();"),
                (BeforeFail, Field + "static void Other() { (Action Say, int Count) Echo = (() => Console.WriteLine(), 1); Echo.Say(); } static void Fail(") }, null),
            ("a field assigned after if (on)", new[] { (AfterParsed, Parsed + "Echo?.Invoke();"),
                (BeforeFail, Field + "static void Arm(bool on) { if (on) Echo = () => Console.WriteLine(); } static void Fail(") }, Through + "Echo"),
            ("a field whose name another method uses for a local", new[] { (AfterParsed, Parsed + "Fire();"),
                (BeforeFail, Field + "static void Arm() { Echo = () => Console.WriteLine(); } static void Other() { Action Echo = () => { }; Echo(); } static void Fire() { Echo?.Invoke(); } static void Fail(") },
                Through + "Echo"),
        };
        foreach (var (label, edits, reason) in shadowMutants)
        {
            var mutated = Mutate(write, edits);
            var problems = mutated is null ? new List<string> { "an anchor does not match once" } : ExecuteWriteProblems(mutated, command, followed);
            if (reason is null) Check(problems.Count == 0, "P7-44 shadow self-test: Execute pin accepts " + label, Problems(problems));
            else Check(problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-44 shadow self-test: " + label + " is still rejected for " + reason, Problems(problems));
        }

        // P7-46 (l): Stat's Func argument is followed through a this. or instance receiver and through a local the method
        // group Stat is assigned to (var f = Stat; f(...) / f?.Invoke(...)); Stat passed on as a method group is rejected
        // loudly (unresolved, naming the gap). (m) A Predicate<T> parameter and one of a delegate type declared in the code
        // are followed; a parameter of a type the code does not declare, called, is unresolved naming its type. (n) A local
        // declared after the use does not shadow the field there. (o) Expression-bodied methods' and local functions'
        // parameters are followed like block-bodied ones; a lambda's parameter, called, is unresolved naming that limit.
        // Each mutant edits the followed code (BoundedInput.cs) or GenerateWrite.cs and names its expected problem. The stdout
        // lambda sits in a caller Execute does not reach (Noisy, Loud), so only the followed argument reaches it; a local
        // function's callers share its member, so (o)'s local-function stdout lambda is in Poke and the reason names test.
        // The lambdas' parameter is sink, a name the followed code does not use: a lambda x => ... directly in a type body is
        // also a declarator of x, which Stat's local status would reach (labelled status) besides call.
        const string StdoutLambda = "sink => { Console.WriteLine(); return 0; }";
        const string Poke = "Poke(path); var bytes = ReadBytes(path, MaxBytes, share);";
        const string StatParameter = "Execute reaches call( [the Func<byte[], int> parameter of Stat: ";
        var reachMutants = new (string Label, bool Followed, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("(l) a stdout lambda passed via this.Stat(", true, new[] { (RequireRegular, "static bool Noisy(out int mode) => this.Stat(" + StdoutLambda + ", out mode); static void RequireUnixRegular(") },
                Through + "call"),
            ("(l) a stdout lambda passed via an instance receiver input.Stat(", true, new[] {
                (RequireRegular, "static bool Noisy(Reader input, out int mode) => input.Stat(" + StdoutLambda + ", out mode); static void RequireUnixRegular(") }, Through + "call"),
            ("(l) a stdout lambda passed via var f = Stat; f(...)", true, new[] {
                (RequireRegular, "static bool Noisy(out int mode) { var f = Stat; return f(" + StdoutLambda + ", out mode); } static void RequireUnixRegular(") }, Through + "call"),
            ("(l) a stdout lambda passed via f = Stat; f?.Invoke(...) of a declared local", true, new[] {
                (RequireRegular, "static bool Noisy(out int mode) { StatCall f; f = Stat; return f?.Invoke(" + StdoutLambda + ", out mode) ?? false; } static void RequireUnixRegular(") }, Through + "call"),
            ("(l) Stat passed on as a method group", true, new[] { (RequireRegular, "static bool Noisy(out int mode) => Relay(Stat, out mode); static void RequireUnixRegular(") },
                StatParameter + "Stat used as a method group"),
            ("(l) a method-group local passed on instead of called", true, new[] {
                (RequireRegular, "static bool Noisy(out int mode) { var f = Stat; return Relay(f, out mode); } static void RequireUnixRegular(") }, StatParameter + "f (a local holding the method group Stat) used other than called"),
            ("(m) a stdout lambda passed for a Predicate<int> parameter", true, new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Probe(sink => true); } static void Loud() { Probe(sink => { Console.WriteLine(); return true; }); } static void Probe(Predicate<int> test) { test(1); } static void RequireUnixRegular(") },
                Through + "test"),
            ("(m) a stdout lambda passed for a parameter of a delegate type declared in the code", true, new[] { (ReadBytesCall, Poke),
                (RequireRegular, "delegate bool Probing(int value); static void Poke(string path) { Probe(sink => true); } static void Loud() { Probe(sink => { Console.WriteLine(); return true; }); } static void Probe(Probing test) { test(1); } static void RequireUnixRegular(") },
                Through + "test"),
            ("(m) a called parameter of a delegate type the code does not declare", true, new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Probe(sink => true); } static void Probe(Notifier test) { test(1); } static void RequireUnixRegular(") },
                "Execute reaches test( [a Notifier parameter of Probe: not a Func, Action, Predicate"),
            ("(n) a field used before a local of the same name is declared in the same block", false, new[] { (AfterParsed, Parsed + "Fire();"),
                (BeforeFail, Field + "static void Arm() { Echo = () => Console.WriteLine(); } static void Fire() { Echo?.Invoke(); Action Echo = () => { }; Echo(); } static void Fail(") }, Through + "Echo"),
            ("(o) a stdout lambda passed for a Func parameter of an expression-bodied method", true, new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) => Probe(() => 0); static void Loud() { Probe(() => { Console.WriteLine(); return 0; }); } static int Probe(Func<int> test) => test(); static void RequireUnixRegular(") },
                Through + "test"),
            ("(o) a stdout lambda passed for a Func parameter of an expression-bodied local function", true, new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { int Probe(Func<int> test) => test(); Probe(() => { Console.WriteLine(path); return 0; }); } static void RequireUnixRegular(") },
                Through + "test"),
            ("(o) a lambda's parameter called", true, new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => test(); run(() => 0); } static void RequireUnixRegular(") },
                "Execute reaches test( [a parameter of a lambda: whoever invokes the lambda passes its arguments"),
            ("(o) a typed lambda's parameter called", true, new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = (Func<int> test) => test(); run(() => 0); } static void RequireUnixRegular(") },
                "Execute reaches test( [a parameter of a lambda typed Func<int>: "),
        };
        foreach (var (label, onFollowed, edits, reason) in reachMutants)
        {
            var mutated = Mutate(onFollowed ? followed : write, edits);
            var problems = mutated is null ? new List<string>() : onFollowed ? ExecuteWriteProblems(write, command, mutated) : ExecuteWriteProblems(mutated, command, followed);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-46 reach self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // P7-48 (p) async / static lambda heads are lambdas; (q) a lambda head is no declarator (the wrap lambda may be
        // called stream again); (r) a generic call's commas cut neither a lambda body nor an argument list; (s) method-group
        // alias chains are followed and a field alias passed on is a gap; (t) generic methods with where clauses expose
        // their parameters; (u) a lambda or non-delegate parameter passed on for a followed delegate parameter is named.
        // Each mutant edits the followed code (BoundedInput.cs, GltfExporter.cs) and names its expected problem.
        const string LambdaCalled = "Execute reaches test( [a parameter of a lambda: whoever invokes the lambda passes its arguments";
        const string TypedLambdaCalled = "Execute reaches test( [a parameter of a lambda typed Func<int>: ";
        var tidyMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("(p) an async lambda's parameter called", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, Task<int>> run = async test => test(); run(() => 0); } static void RequireUnixRegular(") }, LambdaCalled),
            ("(p) a static lambda's parameter called", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = static test => test(); run(() => 0); } static void RequireUnixRegular(") }, LambdaCalled),
            ("(p) a static async typed lambda's parameter called", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, Task<int>> run = static async (Func<int> test) => test(); run(() => 0); } static void RequireUnixRegular(") },
                TypedLambdaCalled),
            ("(q) a stdout write in the wrap lambda GltfExporter.WriteGltf passes to WriteNew, its parameter named stream", new[] {
                (@"ExportGltf\s*\(\s*project\s*\)\s*\)\s*,\s*path\s*,\s*stream\s*=>\s*stream\s*\)", "ExportGltf(project)), path, stream => { Console.WriteLine(); return stream; })") }, Through + "wrap"),
            ("(r) a typed lambda's parameter called inside a generic call with two type arguments", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = (Func<int> test) => Pick<int, int>(test()); run(() => 0); } static void RequireUnixRegular(") },
                TypedLambdaCalled),
            ("(r) a stdout lambda passed after a generic argument with two type arguments", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Probe(0, () => 0); } static void Loud() { Probe(Pick<int, int>(0), () => { Console.WriteLine(); return 0; }); } "
                    + "static void Probe(int seed, Func<int> test) { test(); } static void RequireUnixRegular(") }, Through + "test"),
            ("(s) a stdout lambda passed via a local alias chain var f = Stat; var g = f; g(...)", new[] {
                (RequireRegular, "static bool Noisy(out int mode) { var f = Stat; var g = f; return g(" + StdoutLambda + ", out mode); } static void RequireUnixRegular(") }, Through + "call"),
            ("(s) a stdout lambda passed via a field alias chain StatAlias = Stat; StatChain = StatAlias; StatChain(...)", new[] {
                (RequireRegular, "static StatCall StatAlias = Stat; static StatCall StatChain = StatAlias; static bool Noisy(out int mode) => StatChain(" + StdoutLambda + ", out mode); static void RequireUnixRegular(") },
                Through + "call"),
            ("(s) a field alias of Stat passed on instead of called", new[] {
                (RequireRegular, "static StatCall StatAlias = Stat; static bool Noisy(out int mode) => Relay(StatAlias, out mode); static void RequireUnixRegular(") },
                StatParameter + "StatAlias (a field holding the method group Stat) used other than called"),
            ("(t) a stdout lambda passed for a Func parameter of a block-bodied generic method with where clauses", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Probe(() => 0); } static void Loud() { Probe(() => { Console.WriteLine(); return 0; }); } "
                    + "static void Probe<T>(Func<T> test) where T : IComparable<T>, new() { test(); } static void RequireUnixRegular(") }, Through + "test"),
            ("(t) a stdout lambda passed for a Func parameter of an expression-bodied generic method with where clauses", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Probe(() => 0); } static void Loud() { Probe(() => { Console.WriteLine(); return 0; }); } "
                    + "static void Probe<T>(Func<T> test) where T : IComparable<T>, new() => test(); static void RequireUnixRegular(") }, Through + "test"),
            ("(u) a lambda's parameter passed on for a followed Func parameter", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe(test); run(() => 0); } static int Probe(Func<int> act) => act(); static void RequireUnixRegular(") },
                "Execute reaches test [a parameter of a lambda, passed on (not called)"),
            ("(u) a non-delegate parameter passed on for a followed Func parameter", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) => Relay(null); static void Relay(Notifier test) => Probe(test); static int Probe(Func<int> act) => act(); static void RequireUnixRegular(") },
                "Execute reaches test [a Notifier parameter of Relay, passed on (not called)"),
        };
        foreach (var (label, edits, reason) in tidyMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-48 reach self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }
        // (q) Declarators: an expression-bodied property x => is a declarator of x; the lambda heads x =>, async x => and
        // static x => are not.
        const string LambdaHeads = "class C { int x => 1; void M() { Run(x => x); Run(async x => x); Run(static x => x); } }";
        var lambdaDeclarators = new CodeMap(LambdaHeads).Declarators("x");
        Check(lambdaDeclarators.Count == 1 && lambdaDeclarators[0].Start == LambdaHeads.IndexOf("x => 1", StringComparison.Ordinal),
            "P7-48 declarator self-test: Declarators counts the expression-bodied property x => and no lambda head x =>",
            "declarators at [" + string.Join(", ", lambdaDeclarators.Select(d => d.Start.ToString(CultureInfo.InvariantCulture))) + "]");

        // P7-50 (v): HeadClose takes a where only as a constraint (where T : at the head's bracket depth): a where inside an
        // attribute or a default value, or a LINQ where after a call, opens no method head, so test is still M's parameter.
        var headTexts = new (string Label, string Code)[]
        {
            ("a where inside an attribute", "class C { [Tag(where: 1)] static void M(Func<int> test) { test(); } }"),
            ("a where as a default value", "class C { static void M(Func<int> test, int n = where) { test(); } }"),
            ("a LINQ where after a call in a foreach head", "class C { static void M(Func<int> test) { foreach (var z in from z in Zs orderby Key(z.test) where z.Ok select z) { test(); } } }"),
            ("a LINQ where after a call before a lambda", "class C { static void M(Func<int> test) { Run(from z in Zs orderby Key(z.test) where z.Ok select z, () => test()); } }"),
            ("a where T : class, new() constraint", "class C { static void M<T>(Func<T> test) where T : class, new() { test(); } }"),
            ("an expression-bodied method's where T : IComparable<T> constraint", "class C { static int M<T>(Func<T> test) where T : IComparable<T> => test(); }"),
        };
        foreach (var (label, text) in headTexts)
        {
            var parameter = new CodeMap(text).Parameter("test", text.LastIndexOf("test()", StringComparison.Ordinal));
            Check(parameter is { Method: "M", Delegate: true, Lambda: false }, "P7-50 head self-test: " + label + " leaves test the delegate parameter of M",
                parameter is { } found ? found.Method + " " + found.Kind : "<no parameter>");
        }

        // P7-50 (w): TypeArgumentsEnd takes a type-argument list followed by C#'s disambiguating tokens ( ) ] } : ; , . ? ==
        // != | ^ & [ or {, or by a designation after is / case / out (patterns): a cast and a pattern are one list (Split does
        // not cut them); a comparison with an operator, or followed by a name, is no list. Stated limit: a < b > (c) and
        // a < b, c > (d) are a generic call, as C# itself parses them.
        var angleTexts = new (string Label, string Code, bool List, int Items)[]
        {
            ("a cast (List<A, B>)x", "M((List<A, B>)x, y)", true, 2),
            ("a declaration pattern is Dictionary<A, B> d", "M(x is Dictionary<A, B> d, y)", true, 2),
            // P7-52: a valid switch statement (in a lambda's block body), not a bare case inside an argument list.
            ("a case pattern case Dictionary<A, B> d in a switch statement", "M(() => { switch (x) { case Dictionary<A, B> d: break; } }, y)", true, 2),
            ("a generic call N<A, B>(x)", "M(N<A, B>(x), y)", true, 2),
            ("a comparison with an operator a < b + 1, c > (d)", "M(a < b + 1, c > (d))", false, 2),
            ("a comparison a < b, c > d", "M(a < b, c > d)", false, 2),
            ("a < b > (c), a generic call as C# parses it (the stated limit)", "M(a < b > (c))", true, 1),
            // P7-54: a comma inside the angles (outside any brace): a comparison would be two arguments, the call is one.
            ("a < b, c > (d), a generic call as C# parses it (the stated limit)", "M(a < b, c > (d))", true, 1),
        };
        foreach (var (label, text, list, items) in angleTexts)
        {
            var open = text.IndexOf('<');
            var end = TypeArgumentsEnd(text, open);
            var split = Split(text, text.IndexOf('(') + 1, text.LastIndexOf(')'), false).Count;
            Check((list ? end == text.IndexOf('>', open) : end == -1) && split == items,
                "P7-50 type-argument self-test: " + label + (list ? " is one type-argument list" : " is a comparison") + " and " + items.ToString(CultureInfo.InvariantCulture) + " argument(s)",
                "end " + end.ToString(CultureInfo.InvariantCulture) + ", items " + split.ToString(CultureInfo.InvariantCulture));
        }

        // P7-50 (x): a lambda's or non-delegate parameter passed on wrapped ((test), test!, a cast, a conditional's branch, a
        // side of ??) for a followed Func parameter is named like the bare one; (y) a field alias's name match is stated
        // as over-approximated in its gap; (z) Stat as a method group in a conditional or after ?? stays a named gap.
        const string PassedOnLambda = "Execute reaches test [a parameter of a lambda, passed on (not called)";
        const string ProbeTail = " static int Probe(Func<int> act) => act(); static void RequireUnixRegular(";
        var wrappedMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("(x) a lambda's parameter passed on parenthesized (test)", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe((test)); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(x) a lambda's parameter passed on null-forgiving test!", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe(test!); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(x) a lambda's parameter passed on as a conditional's branch", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe(path.Length > 0 ? test : null); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(x) a lambda's parameter passed on as a side of ??", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe(test ?? (() => 0)); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(x) a Notifier parameter passed on through a cast (Func<int>)test", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) => Relay(null); static void Relay(Notifier test) => Probe((Func<int>)test);" + ProbeTail) },
                "Execute reaches test [a Notifier parameter of Relay, passed on (not called)"),
            ("(y) a field alias of Stat whose name a local elsewhere passes on", new[] {
                (RequireRegular, "static StatCall StatAlias = Stat; static bool Noisy(out int mode) => StatAlias(sink => 0, out mode); static void Other() { int StatAlias = 0; Use(StatAlias); } static void RequireUnixRegular(") },
                StatParameter + "StatAlias (a field holding the method group Stat) used other than called (a field alias is matched by name"),
            ("(z) Stat as a method group in a conditional", new[] {
                (RequireRegular, "static bool Noisy(bool on, out int mode) { StatCall f = on ? Stat : null; return f(sink => 0, out mode); } static void RequireUnixRegular(") },
                StatParameter + "Stat used as a method group (passed on, in a conditional ?: or ??"),
            ("(z) Stat as a method group after ??", new[] {
                (RequireRegular, "static bool Noisy(StatCall custom, out int mode) { var f = custom ?? Stat; return f(sink => 0, out mode); } static void RequireUnixRegular(") },
                StatParameter + "Stat used as a method group (passed on, in a conditional ?: or ??"),
        };
        foreach (var (label, edits, reason) in wrappedMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-50 reach self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // P7-58 (af): one table-driven helper for the Values self-tests: a row with Expected pins Values' whole list in
        // order, a row with Named whether test itself is one of them; kind starts each check's name.
        void ValueSelfTests(string kind, IEnumerable<(string Label, string Code, bool? Named, string[]? Expected)> rows)
        {
            foreach (var (label, text, named, expected) in rows)
            {
                var values = Values(text, 0, text.Length).Select(v => text[v.Start..v.End]).ToArray();
                if (expected is not null)
                    Check(values.SequenceEqual(expected), kind + ": " + label,
                        "values [" + string.Join(", ", values) + "], expected [" + string.Join(", ", expected) + "]");
                else
                    Check(values.Contains("test") == named, kind + ": " + label + (named == true ? " has test as a value" : " does not have test itself as a value"),
                        "values [" + string.Join(", ", values) + "]");
            }
        }

        // P7-52 (aa): Values tells a parenthesised operand followed by a binary operator ((test) ?? d, (test) + d) from a
        // cast ((T)test, (T)(test), (int)-test): test is a value of the first and of the casts' operands; (ab) a nullable
        // generic's ? before ?? (new List<int?>() ?? test, also spaced List<int? >) is no conditional.
        var valueTexts = new (string Label, string Code, bool Named)[]
        {
            ("a parenthesised operand before ?? (test) ?? d", "(test) ?? d", true),
            ("a parenthesised operand before + (test) + 1", "(test) + 1", true),
            ("a parenthesised operand before - (test) - d", "(test) - d", true),
            ("a parenthesised operand before a delegate combination (test) + other", "(test) + other", true),
            ("a parenthesised right operand of + other + (test)", "other + (test)", true),
            ("a cast (Func<int>)test", "(Func<int>)test", true),
            ("a cast of a parenthesised operand (Func<int>)(test)", "(Func<int>)(test)", true),
            ("a cast of a null-forgiving operand (Func<int>)test!", "(Func<int>)test!", true),
            ("a keyword-type cast before unary minus (int)-test", "(int)-test", false),
            ("a nullable generic before ?? new List<int?>() ?? test", "new List<int?>() ?? test", true),
            ("a spaced nullable generic before ?? new List<int? >() ?? test", "new List<int? >() ?? test", true),
            ("a nullable generic cast before ?? (List<int?>)test ?? d", "(List<int?>)test ?? d", true),
            ("a conditional with a nullable generic branch c ? new List<int?>() : test", "c ? new List<int?>() : test", true),
        };
        ValueSelfTests("P7-52 value self-test", valueTexts.Select(r => (r.Label, r.Code, (bool?)r.Named, (string[]?)null)));
        var operandMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("(aa) a lambda's parameter passed on parenthesised before ??", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe((test) ?? (() => 0)); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(aa) a lambda's parameter passed on parenthesised before +", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe((test) + 1); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(ab) a lambda's parameter passed on after a nullable generic and ??", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe(new List<int?>() ?? test); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
        };
        foreach (var (label, edits, reason) in operandMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-52 reach self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // P7-54 (ac): Values keeps test as a value of a null-forgiving parenthesised operand before ?? ((test)! ?? d: the
        // ! is postfix, no cast) and of a conditional whose first branch is a collection expression (c ?[x] : test, the ]
        // followed by :); (ad) an exponent literal's sign (1e-5, 2.5E+3) is no binary operator, a hex 0x1e - 5 is.
        var tidyValueTexts = new (string Label, string Code, bool Named)[]
        {
            ("a null-forgiving parenthesised operand before ?? (test)! ?? d", "(test)! ?? d", true),
            ("a null-forgiving parenthesised operand before a member (test)!.M", "(test)!.M", false),
            ("a cast of a logical not (bool)!test", "(bool)!test", false),
            ("a conditional with a spaced collection branch c ? [x] : test", "c ? [x] : test", true),
            ("a conditional with an unspaced collection branch c ?[x] : test", "c ?[x] : test", true),
            ("a conditional whose branch is an element access c ? a?[0] : test", "c ? a?[0] : test", true),
            ("an element access before ?? a?[0] ?? test", "a?[0] ?? test", true),
        };
        ValueSelfTests("P7-54 value self-test", tidyValueTexts.Select(r => (r.Label, r.Code, (bool?)r.Named, (string[]?)null)));
        var exponentTexts = new (string Label, string Code, string[] Expected)[]
        {
            ("an exponent literal 1e-5 is one value", "1e-5", new[] { "1e-5" }),
            ("an exponent literal 2.5E+3 in a sum is not split at its sign", "2.5E+3 + test", new[] { "2.5E+3 + test", "2.5E+3", "test" }),
            ("an exponent literal 1_0e-5 after a subtraction keeps its sign", "test - 1_0e-5", new[] { "test - 1_0e-5", "test" }),
            ("a hex literal 0x1e - 5 is a subtraction", "0x1e-5", new[] { "0x1e-5", "0x1e" }),
        };
        ValueSelfTests("P7-54 exponent self-test", exponentTexts.Select(r => (r.Label, r.Code, (bool?)null, (string[]?)r.Expected)));
        var operandTidyMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("(ac) a lambda's parameter passed on null-forgiving parenthesised before ??", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe((test)! ?? (() => 0)); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(ac) a lambda's parameter passed on after a collection branch", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe(path.Length > 0 ?[null] : test); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
            ("(ac) a Notifier parameter passed on parenthesised before ??", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) => Relay(null); static void Relay(Notifier test) => Probe((test) ?? (() => 0));" + ProbeTail) },
                "Execute reaches test [a Notifier parameter of Relay, passed on (not called)"),
            ("(ac) a Notifier parameter passed on null-forgiving parenthesised before ??", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) => Relay(null); static void Relay(Notifier test) => Probe((test)! ?? (() => 0));" + ProbeTail) },
                "Execute reaches test [a Notifier parameter of Relay, passed on (not called)"),
        };
        foreach (var (label, edits, reason) in operandTidyMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-54 reach self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // P7-56 (ae): Values reads a ?[ whose ] is followed by : inside a conditional's branch as a nested conditional with a
        // collection branch (c ? b ?[x] : test : d), and an element access there as before (c ? a?[0] : test); (test)!(x) is
        // a cast of !(x) (the token after ) is !, followed by an operand), as C# reads it, so test is no value.
        var nestedValueTexts = new (string Label, string Code, string[] Expected)[]
        {
            ("a nested collection branch c ? b ?[x] : test : d", "c ? b ?[x] : test : d", new[] { "c ? b ?[x] : test : d", "b ?[x] : test", "[x]", "test", "d" }),
            ("a nested spaced collection branch c ? b ? [x] : test : d", "c ? b ? [x] : test : d", new[] { "c ? b ? [x] : test : d", "b ? [x] : test", "[x]", "test", "d" }),
            ("an element access in a branch before a nested conditional c ? a?[0] : b ?[x] : test", "c ? a?[0] : b ?[x] : test", new[] { "c ? a?[0] : b ?[x] : test", "a?[0]", "b ?[x] : test", "[x]", "test" }),
            ("an element access in a branch c ? a?[0] : test", "c ? a?[0] : test", new[] { "c ? a?[0] : test", "a?[0]", "test" }),
            ("a cast of a parenthesised logical not (test)!(x), as C# reads it", "(test)!(x)", new[] { "(test)!(x)", "!(x)" }),
        };
        ValueSelfTests("P7-56 value self-test", nestedValueTexts.Select(r => (r.Label, r.Code, (bool?)null, (string[]?)r.Expected)));
        // A literal's suffix ends no exponent (1e5f - test is a subtraction, 1e-5f is one value); a hex with a separator
        // (0x_1e-5) is a subtraction; a leading-dot real .5e-3 is one value; 1.E is a member access on 1, not an exponent.
        var suffixExponentTexts = new (string Label, string Code, string[] Expected)[]
        {
            ("a suffixed exponent literal 1e5f - test is a subtraction", "1e5f - test", new[] { "1e5f - test", "1e5f" }),
            ("a suffixed signed exponent literal 1e-5f is one value", "1e-5f", new[] { "1e-5f" }),
            ("a separated hex literal 0x_1e-5 is a subtraction", "0x_1e-5", new[] { "0x_1e-5", "0x_1e" }),
            ("a leading-dot exponent literal .5e-3 is one value", ".5e-3", new[] { ".5e-3" }),
            ("a member access on an integer 1.E - test is a subtraction", "1.E - test", new[] { "1.E - test", "1.E" }),
        };
        ValueSelfTests("P7-56 exponent self-test", suffixExponentTexts.Select(r => (r.Label, r.Code, (bool?)null, (string[]?)r.Expected)));
        var nestedMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("(ae) a lambda's parameter passed on after a nested collection branch", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => Probe(path.Length > 0 ? path.Length > 1 ?[null] : test : null); run(() => 0); }" + ProbeTail) }, PassedOnLambda),
        };
        foreach (var (label, edits, reason) in nestedMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "P7-56 reach self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // P7-58 (af): the mixed branch is pinned as Values reads it: with collection branches counted first both ?[ are
        // nested conditionals (c ? (a ? [0] : (b ? [x] : test)) : d), the only reading under which the text parses.
        var mixedValueTexts = new (string Label, string Code, string[] Expected)[]
        {
            ("a mixed branch c ? a?[0] : b ?[x] : test : d reads both ?[ as nested conditionals", "c ? a?[0] : b ?[x] : test : d",
                new[] { "c ? a?[0] : b ?[x] : test : d", "a?[0] : b ?[x] : test", "[0]", "b ?[x] : test", "[x]", "test", "d" }),
        };
        ValueSelfTests("P7-58 value self-test", mixedValueTexts.Select(r => (r.Label, r.Code, (bool?)null, (string[]?)r.Expected)));
        // A digit separator stands only between digits, so 1_e and 1.5_e end no exponent prefix; an exponent's sign follows
        // its e directly, so a spaced 1e - test (and 1e /*c*/ - test, a block comment being one space once stripped) is a
        // subtraction. Stated limit: Values reads comment-stripped code; on text with the comment kept the operand ending
        // in */ is not one, so 1e /*c*/ - test is one value there.
        var commentCut = ApplyIntentTests.CodeOf("1e /*c*/ - test");
        var separatorExponentTexts = new (string Label, string Code, string[] Expected)[]
        {
            ("a separator next to e 1_e-5 is a subtraction", "1_e-5", new[] { "1_e-5", "1_e" }),
            ("a separator next to e after a fraction 1.5_e-3 is a subtraction", "1.5_e-3", new[] { "1.5_e-3", "1.5_e" }),
            ("a doubled separator between digits 1__0e-5 is one value", "1__0e-5", new[] { "1__0e-5" }),
            ("a member X1e of a real 2.5.X1e - test is a subtraction", "2.5.X1e - test", new[] { "2.5.X1e - test", "2.5.X1e" }),
            ("a spaced sign 1e - test is a subtraction", "1e - test", new[] { "1e - test", "1e" }),
            ("a literal cut by a comment 1e /*c*/ - test, comment-stripped, is a subtraction", commentCut, new[] { commentCut, "1e" }),
            ("stated limit: 1e /*c*/ - test with the comment kept is one value", "1e /*c*/ - test", new[] { "1e /*c*/ - test" }),
        };
        ValueSelfTests("P7-58 exponent self-test", separatorExponentTexts.Select(r => (r.Label, r.Code, (bool?)null, (string[]?)r.Expected)));
        // Stated reach gap: C# reads (test)!(0) as a cast of !(0) to a type test (which it does not compile for a lambda's
        // parameter), never as a null-forgiven call of test, and so does the reach: no problem names test. A call test(0)
        // is named, as before.
        var castCallMutants = new (string Label, (string Pattern, string Replacement)[] Edits, bool Named)[]
        {
            ("(af) stated gap: a lambda's parameter written (test)!(0), a cast as C# reads it", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => (test)!(0); run(() => 0); } static void RequireUnixRegular(") }, false),
            ("(af) a lambda's parameter called test(0)", new[] { (ReadBytesCall, Poke),
                (RequireRegular, "static void Poke(string path) { Func<Func<int>, int> run = test => test(0); run(() => 0); } static void RequireUnixRegular(") }, true),
        };
        foreach (var (label, edits, named) in castCallMutants)
        {
            var mutated = Mutate(followed, edits);
            var problems = mutated is null ? new List<string>() : ExecuteWriteProblems(write, command, mutated);
            Check(mutated is not null && (named ? problems.Any(p => p.Contains(LambdaCalled, StringComparison.Ordinal)) : !problems.Any(p => p.Contains("reaches test", StringComparison.Ordinal))),
                "P7-58 reach self-test: " + label + (named ? " is rejected for " + LambdaCalled : " names no problem of test"),
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // b. The one-copy ForcedRun pin through delegates, the caller's lambda, generic, qualified and method-group calls.
        const string ThirteenCall = @"var\s+r\s*=\s*ForcedRun\s*\(\s*runMethod\s*,\s*seams\s*,\s*stdout\s*,\s*null\s*,\s*null\s*,\s*stage\s*,\s*args\s*\)\s*;";
        const string Thirteen = "var r = ForcedRun(runMethod, seams, stdout, null, null, stage, args);";
        const string ClassMember = @"\bstatic\s+void\s+PartialWriterExecuteStage\s*\(";
        const string ContractCall = @"\bPartialWriteContractStage\s*\(\s*root\s*,\s*scratch\s*,\s*cliDll\s*,\s*expected\s*,\s*check\s*,\s*arguments\s*=>\s*Cli\s*\(\s*arguments\s*\)\s*\)\s*;";
        const string CopyBody = "{ var previous = Console.Out; try { Console.SetOut(stdout); run.Invoke(null, new object?[] { args }); } finally { Console.SetOut(previous); } }";
        const string Copy = "static void ForcedRunCopy(System.Reflection.MethodInfo run, TextWriter stdout, string[] args) " + CopyBody + " static void PartialWriterExecuteStage(";
        const string Copied = " which sets stdout or invokes by reflection";
        var copyMutants = new (string Label, (string Pattern, string Replacement)[] Edits, string Reason)[]
        {
            ("a copy in a delegate field section 13 calls", new[] { (ThirteenCall, "Runner(runMethod, stdout, args); " + Thirteen),
                (ClassMember, "static readonly Action<System.Reflection.MethodInfo, TextWriter, string[]> Runner = (run, stdout, args) => " + CopyBody + "; static void PartialWriterExecuteStage(") }, "reaches Runner," + Copied),
            ("a copy in the lambda section 13's caller passes as cli", new[] { (ContractCall,
                "PartialWriteContractStage(root, scratch, cliDll, expected, check, arguments => { var previous = Console.Out; try { Console.SetOut(TextWriter.Null); return Cli(arguments); } finally { Console.SetOut(previous); } });") },
                "a call of PartialWriteContractStage passes an argument that sets stdout"),
            ("a copy behind an explicit generic call", new[] { (ThirteenCall, "ForcedRunCopy<TextWriter>(runMethod, stdout, args); " + Thirteen),
                (ClassMember, "static void ForcedRunCopy<T>(System.Reflection.MethodInfo run, T stdout, string[] args) where T : TextWriter " + CopyBody + " static void PartialWriterExecuteStage(") }, "reaches ForcedRunCopy," + Copied),
            ("a copy behind a qualified GeneratePrototypesTests. call", new[] { (ThirteenCall, "GeneratePrototypesTests.ForcedRunCopy(runMethod, stdout, args); " + Thirteen), (ClassMember, Copy) },
                "reaches GeneratePrototypesTests.ForcedRunCopy," + Copied),
            ("a copy behind a method group passed to Array.ForEach", new[] { (ThirteenCall, "Array.ForEach(new[] { args }, ForcedRunEach); " + Thirteen),
                (ClassMember, "static void ForcedRunEach(string[] args) { var previous = Console.Out; try { Console.SetOut(TextWriter.Null); } finally { Console.SetOut(previous); } } static void PartialWriterExecuteStage(") },
                "reaches ForcedRunEach," + Copied),
            // P7-42 (e): a delegate field assigned in a static constructor and invoked as Runner?.Invoke(.
            ("a copy in a delegate field assigned in a static constructor, invoked as Runner?.Invoke(", new[] { (ThirteenCall, "Runner?.Invoke(runMethod, stdout, args); " + Thirteen),
                (ClassMember, "static Action<System.Reflection.MethodInfo, TextWriter, string[]>? Runner; static GeneratePrototypesTests() { Runner = (run, stdout, args) => " + CopyBody + "; } static void PartialWriterExecuteStage(") },
                "reaches Runner," + Copied),
        };
        foreach (var (label, edits, reason) in copyMutants)
        {
            var mutated = Mutate(tests, edits);
            var problems = mutated is null ? new List<string>() : ForcedRunCopyProblems(mutated);
            Check(mutated is not null && problems.Any(p => p.Contains(reason, StringComparison.Ordinal)), "one-copy self-test: " + label + " is rejected for " + reason,
                (mutated is null ? "an anchor does not match once; " : "") + Problems(problems));
        }

        // c. Type membership, not brace depth 1: a block-scoped namespace around the sources.
        static string? Blocked(string code) => Count(code, @"\bnamespace\s+Bim\.Cli\s*;") == 1
            ? System.Text.RegularExpressions.Regex.Replace(code, @"\bnamespace\s+Bim\.Cli\s*;", "namespace Bim.Cli {") + "\n}" : null;
        var blockedWrite = Blocked(write);
        var blockedCommand = Blocked(command);
        var blockedReal = blockedWrite is null || blockedCommand is null ? new List<string> { "namespace Bim.Cli; not found once" } : ExecuteWriteProblems(blockedWrite, blockedCommand, followed);
        Check(blockedReal.Count == 0, "membership self-test: Execute pin accepts GenerateWrite.cs and GenerateCommand.cs in a block-scoped namespace", Problems(blockedReal));
        var blockedMutant = blockedWrite is null ? null : Mutate(blockedWrite, executeMutants[0].Edits);
        var blockedMutantProblems = blockedMutant is null || blockedCommand is null ? new List<string>() : ExecuteWriteProblems(blockedMutant, blockedCommand, followed);
        Check(blockedMutant is not null && blockedMutantProblems.Any(p => p.Contains(executeMutants[0].Reason, StringComparison.Ordinal)),
            "membership self-test: " + executeMutants[0].Label + " is still rejected in a block-scoped namespace for " + executeMutants[0].Reason, Problems(blockedMutantProblems));
        var blockedTests = "namespace Bim.Cli.Tests.Blocked {\n" + tests + "\n}";
        var blockedCopy = ForcedRunCopyProblems(blockedTests);
        Check(blockedCopy.Count == 0, "membership self-test: one-copy pin accepts the tests file in a block-scoped namespace", Problems(blockedCopy));
        var blockedCopyMutant = Mutate(blockedTests, copyMutants[0].Edits);
        var blockedCopyProblems = blockedCopyMutant is null ? new List<string>() : ForcedRunCopyProblems(blockedCopyMutant);
        Check(blockedCopyMutant is not null && blockedCopyProblems.Any(p => p.Contains(copyMutants[0].Reason, StringComparison.Ordinal)),
            "membership self-test: " + copyMutants[0].Label + " is still rejected in a block-scoped namespace for " + copyMutants[0].Reason, Problems(blockedCopyProblems));

        // d. The trailing-comma reformat touches the success object only.
        var removed = SuccessTrailingCommaRemoved(write);
        Check(removed is not null && removed != write && Count(removed, @"\bsha256\s*,\s*\}") == 0 && Count(removed, @"\bsha256\s*\}\s*\)\s*\)\s*;") == 1
            && removed.Length == write.Length - 1, "reformat self-test: SuccessTrailingCommaRemoved removes exactly the success object's trailing comma");
        var spare = Mutate(write, (AfterResult, "var result = generated.Result; var spare = new[] { 1, 2, };"));
        var spareRemoved = spare is null ? null : SuccessTrailingCommaRemoved(spare);
        Check(spareRemoved is not null && Count(spareRemoved, @"\b2\s*,\s*\}") == 1 && Count(spareRemoved, @"\bsha256\s*,\s*\}") == 0,
            "reformat self-test: SuccessTrailingCommaRemoved keeps another trailing comma (new[] { 1, 2, })", spareRemoved ?? "<null>");
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
            // P4-27: Run lives in the CLI-only write part GenerateWrite.cs.
            var source = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateWrite.cs")), blankStrings: false);
            Check(System.Text.RegularExpressions.Regex.IsMatch(source,
                    @"public\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)\s*\{\s*try\s*\{\s*Execute\s*\(\s*args\s*\)\s*;\s*\}\s*catch\s*\(\s*Refusal\s+refusal\s*\)\s*\{\s*"
                    // P7-30: the catch (Refusal) sets the exit code, then runs Fail guarded (a throwing stdout drops only the typed line).
                    + @"Environment\s*\.\s*ExitCode\s*=\s*1\s*;\s*try\s*\{\s*Fail\s*\(\s*refusal\s*\.\s*Code\s*,\s*refusal\s*\.\s*Detail\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}\s*"
                    // P7-28: the catch-all writes the diagnostic guarded (a throwing stderr drops only it), then prints the typed line.
                    + @"catch\s*\(\s*Exception\s+error\s*\)\s*\{\s*var\s+internalError\s*=\s*IntentPlan\s*\.\s*Internal\s*\(\s*""generate-prototypes""\s*,\s*error\s*\)\s*;\s*"
                    + @"try\s*\{\s*Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*internalError\s*\.\s*Diagnostic\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*"
                    // P7-29: the exit code is set first, then the typed line is printed guarded (a throwing stdout drops only it).
                    + @"Environment\s*\.\s*ExitCode\s*=\s*internalError\s*\.\s*Exit\s*;\s*"
                    + @"try\s*\{\s*\(\s*internalError\s+with\s*\{\s*Diagnostic\s*=\s*null\s*\}\s*\)\s*\.\s*Print\s*\(\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}\s*\}"),
                "GenerateCommand.Run is try Execute / catch (Refusal) { Environment.ExitCode = 1; try { Fail(refusal.Code, refusal.Detail); } catch (Exception) { } } / catch (Exception) { var internalError = IntentPlan.Internal(\"generate-prototypes\", error); "
                + "try { Console.Error.WriteLine(internalError.Diagnostic); } catch (Exception) { } Environment.ExitCode = internalError.Exit; "
                + "try { (internalError with { Diagnostic = null }).Print(); } catch (Exception) { } } (P7-28, P7-29, P7-30)");
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
