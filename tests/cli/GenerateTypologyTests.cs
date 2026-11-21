using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-14 `generate-typology request.json output.json`: TypologyPipeline.Run (P5-12) on a strict JSON request, the
// canonical TypologyPipelineSerializer JSON written to a new file. Oracles, all against the real Release CLI process:
//   1. The output file is byte-identical to TypologyPipelineSerializer.Serialize(TypologyPipeline.Run(pack, request,
//      options)) computed in process with the same packs read from skills/, and its SHA-256 is the P5-12 pinned digest of
//      tests/generative/TypologyPipelineTests.cs for the ranked (family 3, 16 x 16 m) and the infeasible (family 4,
//      12 x 12 m) cases; reruns are byte-identical; weights {} / explicit defaults / absent give the same bytes.
//   2. Never-overwrite: an existing output is refused (output_exists) and left byte- and mtime-identical.
//   3. Every failure (usage, output path, oversize/missing/malformed request, unknown or malformed pack, pipeline
//      refusal) prints a typed {"status":"failed","code":...} envelope, exits 1 and leaves no output file; a pipeline
//      refusal carries the library's refusals exactly.
//   4. A child under LANG/LC_ALL=de_DE.UTF-8 writes the same bytes and prints the same stdout (required on Linux CI).
//   5. The release package layout (<package>/cli beside <package>/skills, outside any repository) gives the repository
//      run's bytes and stdout.
//   6. P7-16: optional include (optional rooms) and code_pack reach Run/RunComposed (TypologyBriefRequest.Include,
//      PrototypePipelineOptions.Pack) byte-equal to the in-process run; absent or default members keep the P7-14 pins;
//      unknown rooms are the pipeline's typed refusals; a code_pack that is not a code skill is code_pack_invalid (kind).
//   7. P7-18: "include":[] is normalised to the absent member: the P7-14 pins and stdout, and the composed pin a8c62da5...
//   8. P7-19: with a non-empty skills list (repository style/code/typology packs and a rule-changing package pack)
//      "include":[] gives the file bytes and stdout of the same run without include; duplicate include members stay
//      request_invalid (request.include).
internal static class GenerateTypologyTests
{
    const string TypologyPack = "bim.typology.vivienda-unifamiliar";
    const string CodePack = "ar.caba.codigo-edificacion";
    // TypologyPipelineTests.PinnedAcceptedSha256 / PinnedSha256 (P5-12), both with project id ...07a7 and default options.
    const string PinnedAcceptedSha256 = "eb8720c1a98a749048df6ac69f1794b965e833efc963eb9ddfe58db0ae886f4e";
    const string PinnedInfeasibleSha256 = "3a55177415f42f4d324cf3e4bd298223595c429006595a635aa12e9fb1a3d20b";
    const string Id = "00000000-0000-4000-8000-0000000007a7";
    // P5-16: TypologyPipelineManifestsTests.PinnedComposedSha256 (P5-14/P5-15): CABA + typology, ComposedRules, family 3, 16 x 16 m, 4 alternatives, seed 0.
    const string PinnedComposedSha256 = "a8c62da59e0eee2d25af4d27bc37fcf2f67ae366e7e48446f1edfe3c87f7c0dd";
    const string CompactStyle = "bim.style.compact-modern";
    const string MasonryStyle = "bim.style.warm-masonry";
    // TypologyPipelineManifestsTests.StricterPack: bedrooms at least 3.2 m on their short side (written into the package layout).
    const string StricterPack = """
    {"manifest_version":1,"id":"xx.test.stricter-bedroom","version":"1.0.0","kind":"code","description":"test pack with a stricter bedroom side","priority":100,
     "parameters":[{"name":"bedroom_side_min_m","type":"number","default":3.2}],
     "rules":[
      {"id":"bedroom-side-min","severity":"error","target":{"room":"bedroom*"},"check":{"op":"min","property":"min_side_m","parameter":"bedroom_side_min_m"},"message":"m"}
     ]}
    """;

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "generate-typology");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-typology: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments) => CliAt(cliDll, null, arguments);
        (int Exit, string Out, string Err) CliAt(string dll, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
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
        static long? Int(JsonElement? element, string name) =>
            element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
        byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        void ExpectFailure(string name, string code, string output, params string[] arguments)
        {
            var result = Cli(arguments);
            var envelope = Parse(result.Out);
            Check(result.Exit == 1 && Str(envelope, "status") == "failed" && Str(envelope, "code") == code && !File.Exists(output) && !Directory.Exists(output),
                name + " -> " + code + ", exit 1, no output file", D(result));
        }
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var pack = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", TypologyPack, "skill.json")));
        var caba = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", CodePack, "skill.json")));
        var projectId = Guid.Parse(Id);
        byte[] Library(SkillManifest typology, TypologyBriefRequest request, Guid id) =>
            Utf8(TypologyPipelineSerializer.Serialize(TypologyPipeline.Run(typology, request, new PrototypePipelineOptions(caba, id))));
        string Request(string packId, int family, double width, double depth, int alternatives, int seed, string extra = "") =>
            "{\"pack\":\"" + packId + "\",\"family_size\":" + family.ToString(CultureInfo.InvariantCulture) + ",\"lot\":{\"width_m\":" + width.ToString("R", CultureInfo.InvariantCulture)
            + ",\"depth_m\":" + depth.ToString("R", CultureInfo.InvariantCulture) + "},\"alternatives\":" + alternatives.ToString(CultureInfo.InvariantCulture)
            + ",\"seed\":" + seed.ToString(CultureInfo.InvariantCulture) + ",\"project_id\":\"" + Id + "\"" + extra + "}";

        // ---- registration and usage ----
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"generate-typology\": GenerateTypologyCommand.Run(args); break;", StringComparison.Ordinal), "Program.cs registers generate-typology");
        Check(program.Contains("| generate-typology request.json output.json |", StringComparison.Ordinal), "the default usage lists generate-typology request.json output.json");
        var usageOut = Path.Combine(scratch, "usage-never.json");
        ExpectFailure("no arguments", "generate_typology_usage", usageOut, "generate-typology");
        ExpectFailure("one argument", "generate_typology_usage", usageOut, "generate-typology", Input("one.json", "{}"));
        ExpectFailure("three arguments", "generate_typology_usage", usageOut, "generate-typology", Input("a.json", "{}"), usageOut, "extra");
        ExpectFailure("an empty output argument", "generate_typology_usage", usageOut, "generate-typology", Input("b.json", "{}"), "");
        var usage = Cli("generate-typology");
        var usageDetail = Str(Parse(usage.Out), "detail") ?? "";
        Check(usageDetail.Contains("generate-typology request.json output.json", StringComparison.Ordinal) && usageDetail.Contains("family_size", StringComparison.Ordinal)
            && usageDetail.Contains("project_id", StringComparison.Ordinal) && usageDetail.Contains(CodePack, StringComparison.Ordinal)
            && usageDetail.Contains("generate_typology_repository_not_found", StringComparison.Ordinal),
            "the usage text documents the verb, the request members, the fixed code pack and the pack lookup refusal", D(usage));
        Check(usageDetail.Contains("\"skills\"", StringComparison.Ordinal) && usageDetail.Contains("\"precedence\"", StringComparison.Ordinal),
            "the usage text documents the optional skills and precedence members (P5-16)", D(usage));

        // ---- success: the in-process pipeline, the P5-12 pinned digests, deterministic ----
        var acceptedRequest = Input("family-3.json", Request(TypologyPack, 3, 16, 16, 4, 0));
        var acceptedExpected = Library(pack, new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 0), projectId);
        var acceptedOut = Output();
        var accepted = Cli("generate-typology", acceptedRequest, acceptedOut);
        var acceptedBytes = Read(acceptedOut);
        Check(accepted.Exit == 0 && acceptedBytes.Length > 0 && acceptedBytes.SequenceEqual(acceptedExpected),
            "the ranked family-3 output is TypologyPipelineSerializer.Serialize(TypologyPipeline.Run(pack, request, options)) byte for byte",
            D(accepted) + " written " + acceptedBytes.Length.ToString(CultureInfo.InvariantCulture) + " expected " + acceptedExpected.Length.ToString(CultureInfo.InvariantCulture));
        Check(Hex(acceptedBytes) == PinnedAcceptedSha256, "the ranked family-3 output has the P5-12 pinned SHA-256 (TypologyPipelineTests.PinnedAcceptedSha256)", Hex(acceptedBytes));
        Check(acceptedBytes.Length > 3 && !(acceptedBytes[0] == 0xEF && acceptedBytes[1] == 0xBB && acceptedBytes[2] == 0xBF), "the output has no byte order mark");
        var success = Parse(accepted.Out);
        Check(Str(success, "status") == "succeeded" && Str(success, "scope") == "proposal_only" && Str(success, "pipeline_status") == "ranked"
            && Str(success, "pack") == TypologyPack && Str(success, "code_pack") == CodePack && Str(success, "project_id") == Id
            && Int(success, "alternatives") == 4 && Int(success, "accepted") is >= 1 && Int(success, "bytes") == acceptedExpected.LongLength
            && Str(success, "sha256") == Hex(acceptedExpected),
            "stdout reports succeeded, ranked, the packs, the project id, the counts, the byte count and the SHA-256 of the file", D(accepted));
        var againOut = Output();
        var again = Cli("generate-typology", acceptedRequest, againOut);
        Check(again.Exit == 0 && Read(againOut).SequenceEqual(acceptedBytes) && again.Out == accepted.Out, "a second run writes byte-identical output and the same stdout", D(again));

        var infeasibleOut = Output();
        var infeasible = Cli("generate-typology", Input("family-4.json", Request(TypologyPack, 4, 12, 12, 6, 0)), infeasibleOut);
        var infeasibleBytes = Read(infeasibleOut);
        Check(infeasible.Exit == 0 && infeasibleBytes.SequenceEqual(Library(pack, new TypologyBriefRequest(4, new PrototypeLot(12, 12), 6, 0), projectId))
            && Hex(infeasibleBytes) == PinnedInfeasibleSha256 && Str(Parse(infeasible.Out), "pipeline_status") == "infeasible" && Int(Parse(infeasible.Out), "accepted") == 0,
            "an infeasible result (every candidate rejected at a gate) is written: in-process bytes, the P5-12 pinned SHA-256, pipeline_status infeasible", D(infeasible) + " " + Hex(infeasibleBytes));

        // Weights: {} and explicit defaults are the absent weights; partial weights in any member order are the library's.
        var reordered = Output();
        var reorderedRun = Cli("generate-typology", Input("family-3-reordered.json", "{\n \"project_id\": \"" + Id + "\", \"seed\": -0, \"alternatives\": 4,\n \"lot\": {\"depth_m\": 16, \"width_m\": 16.0},"
            + " \"weights\": {}, \"family_size\": 3, \"pack\": \"" + TypologyPack + "\"\n}\n"), reordered);
        Check(reorderedRun.Exit == 0 && Read(reordered).SequenceEqual(acceptedBytes), "member order, whitespace, -0 and \"weights\":{} give the same bytes", D(reorderedRun));
        var explicitDefaults = Output();
        var explicitRun = Cli("generate-typology", Input("family-3-default-weights.json", Request(TypologyPack, 3, 16, 16, 4, 0,
            ",\"weights\":{\"area_fit\":0.3,\"adjacency\":0.25,\"proportions\":0.15,\"compactness\":0.1,\"perimeter\":0.1,\"compliance\":0.1}")), explicitDefaults);
        Check(explicitRun.Exit == 0 && Read(explicitDefaults).SequenceEqual(acceptedBytes), "explicit default weights give the absent weights' bytes", D(explicitRun));
        var tuned = Output();
        var tunedRun = Cli("generate-typology", Input("family-3-tuned.json", Request(TypologyPack, 3, 16, 16, 4, 2, ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5}")), tuned);
        var tunedExpected = Library(pack, new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 2, Weights: new ScoringWeights(0.5, 0.25, 0.15, 0.1, 0.1, 0.2)), projectId);
        Check(tunedRun.Exit == 0 && Read(tuned).SequenceEqual(tunedExpected) && !tunedExpected.SequenceEqual(acceptedBytes),
            "partial weights in any member order and another seed: output equal to the in-process run with those ScoringWeights", D(tunedRun));
        Check(!accepted.Out.Contains("\"rules\"", StringComparison.Ordinal) && !accepted.Out.Contains("\"skills\"", StringComparison.Ordinal),
            "without skills and precedence stdout is the P7-14 envelope (no rules or skills members)", D(accepted));

        // ---- P7-16: optional include (optional rooms) and code_pack (the facade gate's code pack) ----
        const string ExampleCodePack = "xx.example.codigo-ejemplo";
        var example = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", ExampleCodePack, "skill.json")));
        Check(example.Kind == SkillKind.Code && caba.Kind == SkillKind.Code && pack.Kind == SkillKind.Typology, "the P7-16 fixtures: two code packs and a typology pack");
        Check(usageDetail.Contains("\"include\"", StringComparison.Ordinal) && usageDetail.Contains("\"code_pack\"", StringComparison.Ordinal),
            "the usage text documents the optional include and code_pack members (P7-16)", D(usage));
        // One request through the CLI against the in-process run with the same packs: a refused library result must be the
        // CLI's generate_typology_refused envelope (and refusalCode among its refusals); a success must be the same bytes, with
        // stdout naming the code pack, and must state the reference it equals (same) or that it differs from the P7-14 bytes.
        void ExpectRun(string label, string text, TypologyPipelineResult library, string codePackId, string? refusalCode = null, byte[]? same = null, string? sameAs = null,
            bool differsFromP714 = false)
        {
            var output = Output();
            var result = Cli("generate-typology", Input("p7-16-case.json", text), output);
            if (library.Status == PrototypePipelineStatus.Refused)
            {
                var envelope = RefusedEnvelope(library);
                Check(result.Exit == 1 && result.Out == envelope && !File.Exists(output) && (refusalCode is null || library.Refusals.Any(refusal => refusal.Code == refusalCode))
                    && same is null && !differsFromP714,
                    label + ": refused as generate_typology_refused with the in-process refusals exactly" + (refusalCode is null ? "" : " (" + refusalCode + ")") + ", no output file",
                    D(result) + " expected " + envelope);
                return;
            }
            var expected = Utf8(TypologyPipelineSerializer.Serialize(library));
            var envelopeOut = Parse(result.Out);
            Check(refusalCode is null && result.Exit == 0 && Read(output).SequenceEqual(expected) && Str(envelopeOut, "sha256") == Hex(expected)
                && Str(envelopeOut, "code_pack") == codePackId && Int(envelopeOut, "bytes") == expected.LongLength,
                label + ": the output is the in-process bytes and stdout names code_pack " + codePackId, D(result) + " expected sha " + Hex(expected));
            if (same is not null)
                Check(expected.SequenceEqual(same) && Read(output).SequenceEqual(same), label + ": expected to equal " + sameAs + " byte for byte", Hex(expected) + " vs " + Hex(same));
            else if (differsFromP714)
                Check(!expected.SequenceEqual(acceptedBytes) && !expected.SequenceEqual(infeasibleBytes), label + ": differs from the P7-14 ranked and infeasible bytes", Hex(expected));
            else Check(false, label + ": a success case must name the reference run it equals or state that it differs", "");
        }
        TypologyBriefRequest R3(IReadOnlyCollection<string>? include = null) => new(3, new PrototypeLot(16, 16), 4, 0, include);
        TypologyPipelineResult Plain(TypologyBriefRequest request, SkillManifest code) => TypologyPipeline.Run(pack, request, new PrototypePipelineOptions(code, projectId));
        string Strs(params string[] values) => "[" + string.Join(",", values.Select(value => "\"" + value + "\"")) + "]";

        // Absent members keep the P7-14 bytes; an explicit default code pack is the absent one (same file, same stdout).
        var defaultCodeOut = Output();
        var defaultCode = Cli("generate-typology", Input("default-code-pack.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + CodePack + "\"")), defaultCodeOut);
        Check(defaultCode.Exit == 0 && Read(defaultCodeOut).SequenceEqual(acceptedBytes) && Hex(Read(defaultCodeOut)) == PinnedAcceptedSha256 && defaultCode.Out == accepted.Out,
            "\"code_pack\":\"" + CodePack + "\" (the default) gives the P7-14 ranked bytes (pin eb8720c1...) and the same stdout", D(defaultCode));
        var defaultCodeInfeasibleOut = Output();
        var defaultCodeInfeasible = Cli("generate-typology", Input("default-code-pack-4.json", Request(TypologyPack, 4, 12, 12, 6, 0, ",\"code_pack\":\"" + CodePack + "\"")), defaultCodeInfeasibleOut);
        Check(defaultCodeInfeasible.Exit == 0 && Read(defaultCodeInfeasibleOut).SequenceEqual(infeasibleBytes) && Hex(Read(defaultCodeInfeasibleOut)) == PinnedInfeasibleSha256
            && defaultCodeInfeasible.Out == infeasible.Out,
            "the default code pack named explicitly on the family-4 lot gives the P7-14 infeasible bytes (pin 3a551774...) and the same stdout", D(defaultCodeInfeasible));

        // include reaches TypologyPipeline.Run as TypologyBriefRequest.Include.
        var studyExpected = Utf8(TypologyPipelineSerializer.Serialize(Plain(R3(new[] { "study" }), caba)));
        ExpectRun("include [study]", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs("study")), Plain(R3(new[] { "study" }), caba), CodePack, differsFromP714: true);
        var studyGarage = Plain(R3(new[] { "study", "garage" }), caba);
        var studyGarageBytes = studyGarage.Status == PrototypePipelineStatus.Refused ? null : Utf8(TypologyPipelineSerializer.Serialize(studyGarage));
        ExpectRun("include [study, garage]", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs("study", "garage")), studyGarage, CodePack,
            differsFromP714: studyGarageBytes is not null);
        if (studyGarageBytes is not null)
            ExpectRun("include [garage, study, study] (another order, a repeat)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs("garage", "study", "study")),
                Plain(R3(new[] { "garage", "study", "study" }), caba), CodePack, same: studyGarageBytes, sameAs: "include [study, garage] (the pipeline sorts and de-duplicates)");
        // P7-18: "include":[] is the absent member (normalised before TypologyPipeline.Run): the P7-14 file and stdout.
        foreach (var (label, text, bytes, pin, stdout) in new[]
        {
            ("family 3", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":[]"), acceptedBytes, PinnedAcceptedSha256, accepted.Out),
            ("family 4 (infeasible)", Request(TypologyPack, 4, 12, 12, 6, 0, ",\"include\":[]"), infeasibleBytes, PinnedInfeasibleSha256, infeasible.Out),
            ("family 3 with the default code pack named", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":[],\"code_pack\":\"" + CodePack + "\""), acceptedBytes, PinnedAcceptedSha256, accepted.Out),
        })
        {
            var emptyOut = Output();
            var empty = Cli("generate-typology", Input("include-empty.json", text), emptyOut);
            Check(empty.Exit == 0 && Read(emptyOut).SequenceEqual(bytes) && Hex(Read(emptyOut)) == pin && empty.Out == stdout,
                "\"include\":[] on " + label + " is the absent member: the P7-14 bytes (pin " + pin[..8] + "...) and the same stdout (P7-18)", D(empty) + " " + Hex(Read(emptyOut)));
        }
        ExpectRun("include with tuned weights and seed 2", Request(TypologyPack, 3, 16, 16, 4, 2, ",\"include\":" + Strs("study") + ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5}"),
            Plain(new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 2, new[] { "study" }, new ScoringWeights(0.5, 0.25, 0.15, 0.1, 0.1, 0.2)), caba), CodePack, differsFromP714: true);
        ExpectRun("include 64 entries (the limit, one room repeated)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs(Enumerable.Repeat("study", 64).ToArray())),
            Plain(R3(Enumerable.Repeat("study", 64).ToArray()), caba), CodePack, same: studyExpected, sameAs: "include [study]");
        // Unknown rooms are the pipeline's typed refusals.
        ExpectRun("include an unknown room kind", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs("sauna")), Plain(R3(new[] { "sauna" }), caba), CodePack,
            SkillTypologyCodes.RoomKindUnknown);
        ExpectRun("include a room kind the programme does not offer", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs("storage")), Plain(R3(new[] { "storage" }), caba), CodePack,
            SkillTypologyCodes.RoomNotOffered);
        ExpectRun("include a 64-character room name (the length limit, unknown to the pipeline)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs(new string('a', 64))),
            Plain(R3(new[] { new string('a', 64) }), caba), CodePack, SkillTypologyCodes.RoomKindUnknown);
        ExpectRun("include an empty room name", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs("study", "")), Plain(R3(new[] { "study", "" }), caba), CodePack,
            SkillTypologyCodes.RoomKindUnknown);

        // code_pack reaches the pipeline as PrototypePipelineOptions.Pack.
        ExpectRun("code_pack " + ExampleCodePack, Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + ExampleCodePack + "\""), Plain(R3(), example), ExampleCodePack,
            differsFromP714: true);
        ExpectRun("code_pack " + ExampleCodePack + " with include [study]", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":" + Strs("study") + ",\"code_pack\":\"" + ExampleCodePack + "\""),
            Plain(R3(new[] { "study" }), example), ExampleCodePack, differsFromP714: true);
        var exampleOut = Output();
        var exampleRun = Cli("generate-typology", Input("example-stdout.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + ExampleCodePack + "\"")), exampleOut);
        var exampleExpected = Utf8(TypologyPipelineSerializer.Serialize(Plain(R3(), example)));
        Check(exampleRun.Exit == 0 && Read(exampleOut).SequenceEqual(exampleExpected) && Str(Parse(exampleRun.Out), "code_pack") == ExampleCodePack
            && Str(Parse(exampleRun.Out), "pack") == TypologyPack && Str(Parse(exampleRun.Out), "sha256") == Hex(exampleExpected)
            && !exampleRun.Out.Contains("\"rules\"", StringComparison.Ordinal) && !exampleRun.Out.Contains("\"skills\"", StringComparison.Ordinal),
            "a plain run with another code pack succeeds with the in-process bytes and prints that code_pack in the P7-14 envelope shape", D(exampleRun));
        // Composed mode: include and code_pack reach RunComposed too.
        ExpectRun("composed (skills []) with include [study]", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":[],\"include\":" + Strs("study")),
            TypologyPipeline.RunComposed(pack, R3(new[] { "study" }), new PrototypePipelineOptions(caba, projectId), Array.Empty<SkillManifest>(), null), CodePack, differsFromP714: true);
        ExpectRun("composed (skills []) with the default code pack named", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + CodePack + "\",\"skills\":[]"),
            TypologyPipeline.RunComposed(pack, R3(), new PrototypePipelineOptions(caba, projectId), Array.Empty<SkillManifest>(), null), CodePack,
            same: Utf8(TypologyPipelineSerializer.Serialize(TypologyPipeline.RunComposed(pack, R3(), new PrototypePipelineOptions(caba, projectId), null, null))), sameAs: "the composed CABA + typology run");
        // The example code pack's living minimum (10 m2) is above the programme minimum (9 m2): composed, it is the typed
        // typology_minimum_conflict refusal, which shows the code_pack reaches the composed resolution.
        ExpectRun("composed (skills []) with code_pack " + ExampleCodePack, Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + ExampleCodePack + "\",\"skills\":[]"),
            TypologyPipeline.RunComposed(pack, R3(), new PrototypePipelineOptions(example, projectId), Array.Empty<SkillManifest>(), null), ExampleCodePack, SkillTypologyCodes.MinimumConflict);
        ExpectRun("composed include of an unknown room", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":[],\"include\":" + Strs("sauna")),
            TypologyPipeline.RunComposed(pack, R3(new[] { "sauna" }), new PrototypePipelineOptions(caba, projectId), Array.Empty<SkillManifest>(), null), CodePack, SkillTypologyCodes.RoomKindUnknown);

        // A code_pack that is not a code skill, or that cannot be read, is refused before the pipeline runs.
        void ExpectCodePackRefusal(string label, string text, string code, string detail)
        {
            var output = Output();
            var result = Cli("generate-typology", Input("code-pack-refusal.json", text), output);
            var envelope = Parse(result.Out);
            Check(result.Exit == 1 && Str(envelope, "code") == code && Str(envelope, "detail") == detail && !File.Exists(output) && result.Out.Split('\n').Length == 1,
                label + " -> " + code + " (detail " + detail + "), exit 1, no output file", D(result));
        }
        ExpectCodePackRefusal("a style pack as code_pack", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + CompactStyle + "\""), "generate_typology_code_pack_invalid", "kind");
        ExpectCodePackRefusal("the typology pack as code_pack", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + TypologyPack + "\""), "generate_typology_code_pack_invalid", "kind");
        ExpectCodePackRefusal("a qa pack as code_pack in composed mode", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"bim.qa.room-basics\",\"skills\":[]"),
            "generate_typology_code_pack_invalid", "kind");
        ExpectCodePackRefusal("an unknown code_pack", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"xx.no-such.code\""), "generate_typology_code_pack_unavailable", "input_not_found");
        ExpectCodePackRefusal("a non-code code_pack with an unknown room (the code pack is checked first)",
            Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + CompactStyle + "\",\"include\":" + Strs("sauna")), "generate_typology_code_pack_invalid", "kind");
        var unknownBothOut = Output();
        var unknownBoth = Cli("generate-typology", Input("unknown-pack-and-code.json", Request("xx.no-such.pack", 3, 16, 16, 4, 0, ",\"code_pack\":\"xx.no-such.code\"")), unknownBothOut);
        Check(unknownBoth.Exit == 1 && Str(Parse(unknownBoth.Out), "code") == "generate_typology_pack_unavailable" && !File.Exists(unknownBothOut),
            "the typology pack is read before the code pack (generate_typology_pack_unavailable wins)", D(unknownBoth));
        var codeBeforeSkillOut = Output();
        var codeBeforeSkill = Cli("generate-typology", Input("code-before-skill.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"code_pack\":\"" + CompactStyle + "\",\"skills\":" + Strs("xx.no-such.skill"))), codeBeforeSkillOut);
        Check(codeBeforeSkill.Exit == 1 && Str(Parse(codeBeforeSkill.Out), "code") == "generate_typology_code_pack_invalid" && !File.Exists(codeBeforeSkillOut),
            "the code pack is checked before the extra skills (generate_typology_code_pack_invalid wins)", D(codeBeforeSkill));

        // ---- P5-16: optional skills / precedence run TypologyPipeline.RunComposed (composed pack rules) ----
        SkillManifest Skill(string id) => SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", id, "skill.json")));
        TypologyPipelineResult Composed(SkillManifest typology, TypologyBriefRequest request, IReadOnlyList<SkillManifest>? extras, IReadOnlyList<string>? precedence) =>
            TypologyPipeline.RunComposed(typology, request, new PrototypePipelineOptions(caba, projectId), extras, precedence);
        static string Ids(params string[] ids) => "[" + string.Join(",", ids.Select(id => "\"" + id + "\"")) + "]";
        var request3 = new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 0);
        var request4 = new TypologyBriefRequest(4, new PrototypeLot(12, 12), 6, 0);
        var compact = Skill(CompactStyle);
        var masonry = Skill(MasonryStyle);
        var composedExpected = Utf8(TypologyPipelineSerializer.Serialize(Composed(pack, request3, null, null)));
        var composedRequest = Input("composed.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":[]"));
        var composedOut = Output();
        var composed = Cli("generate-typology", composedRequest, composedOut);
        var composedBytes = Read(composedOut);
        Check(composed.Exit == 0 && composedBytes.Length > 0 && composedBytes.SequenceEqual(composedExpected),
            "\"skills\":[] runs RunComposed: the output is TypologyPipelineSerializer.Serialize(TypologyPipeline.RunComposed(pack, request, options)) byte for byte",
            D(composed) + " written " + composedBytes.Length.ToString(CultureInfo.InvariantCulture) + " expected " + composedExpected.Length.ToString(CultureInfo.InvariantCulture));
        Check(Hex(composedBytes) == PinnedComposedSha256 && !composedBytes.SequenceEqual(acceptedBytes),
            "CABA + typology through the CLI has the P5-14/P5-15 pinned SHA-256 a8c62da5... and differs from the P7-14 bytes", Hex(composedBytes));
        var composedEnvelope = Parse(composed.Out);
        Check(Str(composedEnvelope, "status") == "succeeded" && Str(composedEnvelope, "rules") == "composed" && Str(composedEnvelope, "pack") == TypologyPack
            && Str(composedEnvelope, "code_pack") == CodePack && composedEnvelope is { } ce && ce.TryGetProperty("skills", out var skillsOut) && skillsOut.ValueKind == JsonValueKind.Array
            && skillsOut.GetArrayLength() == 0 && Int(composedEnvelope, "bytes") == composedExpected.LongLength && Str(composedEnvelope, "sha256") == Hex(composedExpected),
            "a composed run's stdout reports rules composed, the extra skills, the byte count and the SHA-256", D(composed));
        // P7-18: "include":[] in composed mode is the absent member too (pin a8c62da5..., same stdout).
        var composedEmptyOut = Output();
        var composedEmpty = Cli("generate-typology", Input("composed-include-empty.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"include\":[],\"skills\":[]")), composedEmptyOut);
        Check(composedEmpty.Exit == 0 && Read(composedEmptyOut).SequenceEqual(composedBytes) && Hex(Read(composedEmptyOut)) == PinnedComposedSha256 && composedEmpty.Out == composed.Out,
            "\"include\":[] with \"skills\":[] is the composed CABA + typology run: pin a8c62da5... and the same stdout (P7-18)", D(composedEmpty) + " " + Hex(Read(composedEmptyOut)));
        var composedAgainOut = Output();
        var composedAgain = Cli("generate-typology", composedRequest, composedAgainOut);
        Check(composedAgain.Exit == 0 && Read(composedAgainOut).SequenceEqual(composedBytes) && composedAgain.Out == composed.Out, "a second composed run is byte-identical", D(composedAgain));

        void ExpectComposed(string label, string text, SkillManifest typology, TypologyBriefRequest request, IReadOnlyList<SkillManifest>? extras, IReadOnlyList<string>? precedence,
            string? refusalCode = null, byte[]? same = null, string? sameAs = null)
        {
            var library = Composed(typology, request, extras, precedence);
            var output = Output();
            var result = Cli("generate-typology", Input("composed-case.json", text), output);
            if (library.Status == PrototypePipelineStatus.Refused)
            {
                var envelope = RefusedEnvelope(library);
                Check((refusalCode is null || library.Refusals.Any(refusal => refusal.Code == refusalCode)) && result.Exit == 1 && result.Out == envelope && !File.Exists(output),
                    label + ": refused as generate_typology_refused with RunComposed's typed refusals exactly" + (refusalCode is null ? "" : " (" + refusalCode + ")") + ", no output file",
                    D(result) + " expected " + envelope);
            }
            else
            {
                var expected = Utf8(TypologyPipelineSerializer.Serialize(library));
                Check(refusalCode is null && result.Exit == 0 && Read(output).SequenceEqual(expected) && Str(Parse(result.Out), "sha256") == Hex(expected),
                    label + ": the output is the in-process RunComposed bytes", D(result) + " expected sha " + Hex(expected));
                // Every success case states how it relates to a reference run, so no case is silently redundant.
                if (same is not null)
                    Check(expected.SequenceEqual(same) && Read(output).SequenceEqual(same), label + ": expected to equal " + sameAs + " byte for byte", Hex(expected) + " vs " + Hex(same));
            }
            if (same is null && refusalCode is null && library.Status != PrototypePipelineStatus.Refused)
                Check(false, label + ": a success case must name the reference run it equals", "");
        }
        var compactOne = Utf8(TypologyPipelineSerializer.Serialize(Composed(pack, request3, new[] { compact }, null)));
        var tunedRequest = new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 2, Weights: new ScoringWeights(0.5, 0.25, 0.15, 0.1, 0.1, 0.2));
        var family4Composed = Utf8(TypologyPipelineSerializer.Serialize(Composed(pack, request4, null, null)));
        var tunedComposed = Utf8(TypologyPipelineSerializer.Serialize(Composed(pack, tunedRequest, null, null)));
        Check(!tunedComposed.SequenceEqual(composedBytes) && !family4Composed.SequenceEqual(composedBytes),
            "the family-4 and tuned-weights reference runs differ from the CABA + typology pin (so equality with them is not equality with the pin)");
        ExpectComposed("skills naming the typology and code packs", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CodePack, TypologyPack)), pack, request3,
            new[] { caba, pack }, null, same: composedBytes, sameAs: "the CABA + typology pin (the composed packs counted once)");
        // compact-modern has no minimum area/side planner rules, so it composes to the CABA + typology planner rules: the
        // same bytes as no extras (on 16 x 16 the pin itself). The stricter package pack below is the rule-changing case.
        ExpectComposed("an extra style pack without planner rules", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle)), pack, request3, new[] { compact }, null,
            same: composedBytes, sameAs: "the CABA + typology pin (compact-modern adds no planner rule)");
        ExpectComposed("an extra style pack on the family-4 lot", Request(TypologyPack, 4, 12, 12, 6, 0, ",\"skills\":" + Ids(CompactStyle)), pack, request4, new[] { compact }, null,
            same: family4Composed, sameAs: "the family-4 run without extras (compact-modern adds no planner rule)");
        ExpectComposed("an extra named twice", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle, CompactStyle)), pack, request3, new[] { compact, compact }, null,
            same: compactOne, sameAs: "the extra named once");
        ExpectComposed("64 skills (the limit, one pack repeated)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(Enumerable.Repeat(CompactStyle, 64).ToArray())), pack, request3,
            Enumerable.Repeat(compact, 64).ToArray(), null, same: compactOne, sameAs: "the extra named once");
        ExpectComposed("a precedence order alone (no equal-priority tie to rank)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":" + Ids(TypologyPack, CodePack)), pack, request3, null,
            new[] { TypologyPack, CodePack }, same: composedBytes, sameAs: "the CABA + typology pin (precedence only ranks ties, and these packs have none)");
        var sixtyFour = Enumerable.Range(0, 64).Select(i => "xx.test.p" + ((char)('a' + i / 26)).ToString() + ((char)('a' + i % 26)).ToString()).ToArray();
        ExpectComposed("64 precedence entries (the limit) are accepted and resolved", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":" + Ids(sixtyFour)), pack, request3, null,
            sixtyFour, SkillCodes.PrecedenceUnknown);
        ExpectComposed("a duplicate precedence entry", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":" + Ids(TypologyPack, CodePack, TypologyPack)), pack, request3, null,
            new[] { TypologyPack, CodePack, TypologyPack }, SkillCodes.PrecedenceDuplicate);
        ExpectComposed("skills and precedence with tuned weights", Request(TypologyPack, 3, 16, 16, 4, 2, ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"precedence\":"
            + Ids(CompactStyle, TypologyPack) + ",\"skills\":" + Ids(CompactStyle)), pack,
            tunedRequest, new[] { compact }, new[] { CompactStyle, TypologyPack }, same: tunedComposed, sameAs: "the tuned-weights run without extras (compact-modern adds no planner rule)");
        // P7-19: "include":[] with a NON-EMPTY skills list is the same composed run without include: the CLI output file and
        // stdout of both requests are byte-equal, and equal the in-process RunComposed with Include absent. Sensitivity: in
        // process, Include: [] is recorded in the document and differs from the absent member, so only the CLI normalisation
        // (P7-18) makes the two requests equal; without it every success case below fails.
        void ExpectEmptyIncludeWithSkills(string label, string dll, string absentText, string emptyText, TypologyPipelineResult library, TypologyPipelineResult emptyLibrary,
            string[] skillIds)
        {
            var absentOut = Output();
            var absent = CliAt(dll, null, "generate-typology", Input("p7-19-absent.json", absentText), absentOut);
            var emptyOut = Output();
            var empty = CliAt(dll, null, "generate-typology", Input("p7-19-empty.json", emptyText), emptyOut);
            Check(skillIds.Length > 0 && emptyText.Contains("\"include\":[]", StringComparison.Ordinal) && !absentText.Contains("\"include\"", StringComparison.Ordinal),
                label + ": the fixture pair is a non-empty skills list with and without \"include\":[] (P7-19)");
            // Every case must be a result (ranked or infeasible): a refused case would lose the sensitivity check below.
            Check(library.Status != PrototypePipelineStatus.Refused && emptyLibrary.Status != PrototypePipelineStatus.Refused,
                label + ": fixture: the in-process RunComposed runs (with and without Include: []) are results, not refusals (P7-19)", D(absent) + " / " + D(empty));
            if (library.Status == PrototypePipelineStatus.Refused) return;
            var expected = Utf8(TypologyPipelineSerializer.Serialize(library));
            var emptyRecorded = Utf8(TypologyPipelineSerializer.Serialize(emptyLibrary));
            Check(!emptyRecorded.SequenceEqual(expected), label + ": in process Include: [] differs from the absent member (the oracle is sensitive to the normalisation) (P7-19)",
                Hex(emptyRecorded) + " vs " + Hex(expected));
            var envelope = Parse(absent.Out);
            Check(absent.Exit == 0 && Read(absentOut).SequenceEqual(expected) && Str(envelope, "rules") == "composed" && envelope is { } ae && ae.TryGetProperty("skills", out var listed)
                && listed.ValueKind == JsonValueKind.Array && listed.EnumerateArray().Select(skill => skill.GetString()).SequenceEqual(skillIds),
                label + ": without include the output is the in-process RunComposed bytes and stdout lists the extra skills (P7-19)", D(absent) + " expected sha " + Hex(expected));
            Check(empty.Exit == 0 && Read(emptyOut).SequenceEqual(Read(absentOut)) && empty.Out == absent.Out,
                label + ": \"include\":[] gives the same file bytes and the same stdout as the run without include (P7-19)", D(empty) + " / " + D(absent));
        }
        TypologyBriefRequest WithEmptyInclude(TypologyBriefRequest request) => request with { Include = Array.Empty<string>() };
        foreach (var (label, absentExtra, emptyExtra, request, extras, precedence, skillIds) in new (string, string, string, TypologyBriefRequest, SkillManifest[], string[]?, string[])[]
        {
            ("skills [compact-modern] on family 3", ",\"skills\":" + Ids(CompactStyle), ",\"skills\":" + Ids(CompactStyle) + ",\"include\":[]",
                request3, new[] { compact }, null, new[] { CompactStyle }),
            ("include [] before skills [compact-modern] on the family-4 lot", ",\"skills\":" + Ids(CompactStyle), ",\"include\":[],\"skills\":" + Ids(CompactStyle),
                request4, new[] { compact }, null, new[] { CompactStyle }),
            ("skills naming the code and typology packs", ",\"skills\":" + Ids(CodePack, TypologyPack), ",\"skills\":" + Ids(CodePack, TypologyPack) + ",\"include\":[]",
                request3, new[] { caba, pack }, null, new[] { CodePack, TypologyPack }),
            ("skills [compact-modern] named twice with the default code pack named", ",\"code_pack\":\"" + CodePack + "\",\"skills\":" + Ids(CompactStyle, CompactStyle),
                ",\"code_pack\":\"" + CodePack + "\",\"skills\":" + Ids(CompactStyle, CompactStyle) + ",\"include\":[]",
                request3, new[] { compact, compact }, null, new[] { CompactStyle, CompactStyle }),
            ("skills [compact-modern], precedence and tuned weights", ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"precedence\":" + Ids(CompactStyle, TypologyPack) + ",\"skills\":" + Ids(CompactStyle),
                ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"precedence\":" + Ids(CompactStyle, TypologyPack) + ",\"include\":[],\"skills\":" + Ids(CompactStyle),
                tunedRequest, new[] { compact }, new[] { CompactStyle, TypologyPack }, new[] { CompactStyle }),
        })
        {
            var lot = request.Lot;
            ExpectEmptyIncludeWithSkills(label, cliDll,
                Request(TypologyPack, request.FamilySize, lot.WidthMetres, lot.DepthMetres, request.Alternatives, request.Seed, absentExtra),
                Request(TypologyPack, request.FamilySize, lot.WidthMetres, lot.DepthMetres, request.Alternatives, request.Seed, emptyExtra),
                Composed(pack, request, extras, precedence), Composed(pack, WithEmptyInclude(request), extras, precedence), skillIds);
        }
        // A non-empty include with skills is not normalised: it reaches RunComposed and differs from the run without include.
        var studyCompact = Composed(pack, R3(new[] { "study" }), new[] { compact }, null);
        var studyCompactBytes = Utf8(TypologyPipelineSerializer.Serialize(studyCompact));
        Check(studyCompact.Status != PrototypePipelineStatus.Refused && !studyCompactBytes.SequenceEqual(compactOne),
            "P7-19 fixture: with skills [compact-modern], include [study] is a result that differs from the run without include");
        ExpectComposed("skills [compact-modern] with include [study] (not normalised)", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle) + ",\"include\":" + Ids("study")),
            pack, R3(new[] { "study" }), new[] { compact }, null, same: studyCompactBytes, sameAs: "the in-process include [study] composed run with compact-modern");
        ExpectComposed("two conflicting style packs", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle, MasonryStyle)), pack, request3,
            new[] { compact, masonry }, null, SkillCodes.SkillConflict);
        ExpectComposed("a precedence entry that is not a composed skill", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":" + Ids("xx.no-such.skill")), pack, request3,
            null, new[] { "xx.no-such.skill" }, SkillCodes.PrecedenceUnknown);
        ExpectComposed("a conflict plus a request refusal", Request(TypologyPack, 0, 16, 16, 4, 0, ",\"skills\":" + Ids(MasonryStyle, CompactStyle)), pack,
            new TypologyBriefRequest(0, new PrototypeLot(16, 16), 4, 0), new[] { masonry, compact }, null, SkillCodes.SkillConflict);
        ExpectComposed("a code pack named as the typology pack in composed mode", Request(CodePack, 3, 16, 16, 4, 0, ",\"skills\":[]"), caba, request3, Array.Empty<SkillManifest>(), null);
        var unknownSkillOut = Output();
        var unknownSkill = Cli("generate-typology", Input("unknown-skill.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle, "xx.no-such.skill"))), unknownSkillOut);
        Check(unknownSkill.Exit == 1 && Str(Parse(unknownSkill.Out), "code") == "generate_typology_skill_unavailable" && Str(Parse(unknownSkill.Out), "detail") == "input_not_found"
            && Str(Parse(unknownSkill.Out), "skill") == "xx.no-such.skill" && !File.Exists(unknownSkillOut),
            "an unknown extra skill id -> generate_typology_skill_unavailable (input_not_found, skill id), exit 1, no file", D(unknownSkill));
        var bothUnknownOut = Output();
        var bothUnknown = Cli("generate-typology", Input("both-unknown.json", Request("xx.no-such.pack", 3, 16, 16, 4, 0, ",\"skills\":" + Ids("xx.no-such.skill"))), bothUnknownOut);
        Check(bothUnknown.Exit == 1 && Str(Parse(bothUnknown.Out), "code") == "generate_typology_pack_unavailable" && !File.Exists(bothUnknownOut),
            "the typology pack is read before the extra skills (generate_typology_pack_unavailable wins)", D(bothUnknown));

        // ---- culture: a de_DE child writes the same bytes and prints the same stdout ----
        {
            var german = false;
            try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
            catch (CultureNotFoundException) { }
            var linuxCi = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("CI") == "true";
            if (!german)
            {
                if (linuxCi) Check(false, "de-DE culture data with a comma decimal separator exists on Linux CI", "CultureInfo.GetCultureInfo(\"de-DE\") unavailable or not comma-decimal");
                else Console.WriteLine("SKIP generate-typology: de-DE culture data unavailable (invariant globalization); culture check not run");
            }
            else if (OperatingSystem.IsWindows()) Console.WriteLine("SKIP generate-typology: LANG/LC_ALL do not select the .NET culture on Windows");
            else
            {
                var env = new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" };
                var germanOut = Output();
                var germanRun = CliAt(cliDll, env, "generate-typology", acceptedRequest, germanOut);
                Check(germanRun.Exit == 0 && Read(germanOut).SequenceEqual(acceptedBytes) && germanRun.Out == accepted.Out,
                    "under de_DE the ranked run writes byte-identical output and the same stdout", D(germanRun));
                var germanTunedOut = Output();
                var germanTuned = CliAt(cliDll, env, "generate-typology", Input("family-3-tuned-de.json", Request(TypologyPack, 3, 16, 16, 4, 2, ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5}")), germanTunedOut);
                Check(germanTuned.Exit == 0 && Read(germanTunedOut).SequenceEqual(tunedExpected), "under de_DE fractional weights give byte-identical output", D(germanTuned));
                var germanComposedOut = Output();
                var germanComposed = CliAt(cliDll, env, "generate-typology", composedRequest, germanComposedOut);
                Check(germanComposed.Exit == 0 && Read(germanComposedOut).SequenceEqual(composedBytes) && Hex(Read(germanComposedOut)) == PinnedComposedSha256 && germanComposed.Out == composed.Out,
                    "under de_DE the composed run (skills) writes byte-identical output and the same stdout", D(germanComposed));
                var germanConflictText = Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle, MasonryStyle));
                var germanConflictOut = Output();
                var germanConflict = CliAt(cliDll, env, "generate-typology", Input("conflict-de.json", germanConflictText), germanConflictOut);
                Check(germanConflict.Exit == 1 && germanConflict.Out == RefusedEnvelope(Composed(pack, request3, new[] { compact, masonry }, null)) && !File.Exists(germanConflictOut),
                    "under de_DE a resolution refusal prints RunComposed's typed refusals byte for byte", D(germanConflict));
                var refusedText = Request(TypologyPack, 3, 2.5, 16, 4, 0);
                var refusedLibrary = TypologyPipeline.Run(pack, new TypologyBriefRequest(3, new PrototypeLot(2.5, 16), 4, 0), new PrototypePipelineOptions(caba, projectId));
                var germanRefusedOut = Output();
                var germanRefused = CliAt(cliDll, env, "generate-typology", Input("refused-de.json", refusedText), germanRefusedOut);
                Check(germanRefused.Exit == 1 && germanRefused.Out == RefusedEnvelope(refusedLibrary) && !File.Exists(germanRefusedOut),
                    "under de_DE a refusal prints the library's refusals byte for byte", D(germanRefused));
            }
        }

        // ---- never-overwrite and output paths ----
        var existing = Output();
        var sentinel = Utf8("{\"sentinel\":true}");
        File.WriteAllBytes(existing, sentinel);
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(existing, stamp);
        var overwrite = Cli("generate-typology", acceptedRequest, existing);
        Check(overwrite.Exit == 1 && Str(Parse(overwrite.Out), "code") == "output_exists" && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
            "an existing output is refused (output_exists) and left byte- and mtime-identical", D(overwrite));
        var directoryOut = Path.Combine(scratch, "dir-out.json");
        Directory.CreateDirectory(directoryOut);
        var dirResult = Cli("generate-typology", acceptedRequest, directoryOut);
        Check(dirResult.Exit == 1 && Str(Parse(dirResult.Out), "code") == "output_exists" && Directory.GetFileSystemEntries(directoryOut).Length == 0,
            "a directory at the output path is refused (output_exists)", D(dirResult));
        ExpectFailure("an output with another extension", "output_extension_unsupported", Path.Combine(scratch, "out.txt"), "generate-typology", acceptedRequest, Path.Combine(scratch, "out.txt"));
        var noDir = Path.Combine(scratch, "missing-directory", "out.json");
        ExpectFailure("an output in a missing directory", "output_directory_not_found", noDir, "generate-typology", acceptedRequest, noDir);
        // The output path is checked before the request is read: a missing request with a bad output is an output refusal.
        ExpectFailure("a bad output with a missing request", "output_extension_unsupported", Path.Combine(scratch, "first.txt"), "generate-typology",
            Path.Combine(scratch, "no-such-request.json"), Path.Combine(scratch, "first.txt"));

        // ---- bounded and malformed requests ----
        var overCapPath = Path.Combine(scratch, "over-cap.json");
        File.WriteAllBytes(overCapPath, overCap);
        var o1 = Output(); ExpectFailure("an 8 MiB + 1 request", "input_too_large", o1, "generate-typology", overCapPath, o1);
        var o2 = Output(); ExpectFailure("a missing request", "input_not_found", o2, "generate-typology", Path.Combine(scratch, "no-such-request.json"), o2);
        var o3 = Output(); ExpectFailure("a request directory", "input_not_regular_file", o3, "generate-typology", scratch, o3);
        var good = Request(TypologyPack, 3, 16, 16, 4, 0);
        string Without(string member) => member switch
        {
            "lot" => good.Replace(",\"lot\":{\"width_m\":16,\"depth_m\":16}", "", StringComparison.Ordinal),
            "pack" => good.Replace("\"pack\":\"" + TypologyPack + "\",", "", StringComparison.Ordinal),
            "project_id" => good.Replace(",\"project_id\":\"" + Id + "\"", "", StringComparison.Ordinal),
            "family_size" => good.Replace(",\"family_size\":3", "", StringComparison.Ordinal),
            "alternatives" => good.Replace(",\"alternatives\":4", "", StringComparison.Ordinal),
            _ => good.Replace(",\"seed\":0", "", StringComparison.Ordinal),
        };
        var malformed = new List<(string Label, string Text, string Detail)>
        {
            ("malformed JSON", "{\"pack\":", "json"),
            ("a non-object request", "[1,2]", "request"),
            ("an unknown member", good[..^1] + ",\"bedrooms\":2}", "request.bedrooms"),
            ("a duplicate member", good.Replace("\"family_size\":3", "\"family_size\":3,\"family_size\":4", StringComparison.Ordinal), "request.family_size"),
            ("a revision member (not in v1)", good[..^1] + ",\"revision\":0}", "request.revision"),
            ("a snap member (not in v1)", good[..^1] + ",\"snap\":{}}", "request.snap"),
            ("a string include", good[..^1] + ",\"include\":\"study\"}", "include"),
            ("null include", good[..^1] + ",\"include\":null}", "include"),
            ("an object include", good[..^1] + ",\"include\":{}}", "include"),
            ("a non-string include entry", good[..^1] + ",\"include\":[\"study\",7]}", "include[1]"),
            ("a null include entry", good[..^1] + ",\"include\":[null]}", "include[0]"),
            ("a lone surrogate include entry", good[..^1] + ",\"include\":[\"\\ud800\"]}", "include[0]"),
            ("more than 64 include entries", good[..^1] + ",\"include\":" + Ids(Enumerable.Repeat("study", 65).ToArray()) + "}", "include"),
            ("a 65-character include entry", good[..^1] + ",\"include\":[\"" + new string('a', 65) + "\"]}", "include[0]"),
            ("a control character in an include entry", good[..^1] + ",\"include\":[\"study\",\"gar\\u0001age\"]}", "include[1]"),
            ("a newline in an include entry", good[..^1] + ",\"include\":[\"study\\n\"]}", "include[0]"),
            ("a duplicate include member", good[..^1] + ",\"include\":[],\"include\":[]}", "request.include"),
            // P7-19: the duplicate stays request_invalid with a non-empty skills list, whichever member is empty.
            ("a duplicate empty include member with skills", good[..^1] + ",\"skills\":" + Ids(CompactStyle) + ",\"include\":[],\"include\":[]}", "request.include"),
            ("a duplicate include member ([study] then []) with skills", good[..^1] + ",\"skills\":" + Ids(CompactStyle) + ",\"include\":[\"study\"],\"include\":[]}", "request.include"),
            ("a duplicate include member ([] then [study]) before skills", good[..^1] + ",\"include\":[],\"include\":[\"study\"],\"skills\":" + Ids(CompactStyle) + "}", "request.include"),
            ("a non-string code_pack", good[..^1] + ",\"code_pack\":7}", "code_pack"),
            ("null code_pack", good[..^1] + ",\"code_pack\":null}", "code_pack"),
            ("an array code_pack", good[..^1] + ",\"code_pack\":[\"" + CodePack + "\"]}", "code_pack"),
            ("a malformed code_pack id", good[..^1] + ",\"code_pack\":\"../skills\"}", "code_pack"),
            ("an empty code_pack", good[..^1] + ",\"code_pack\":\"\"}", "code_pack"),
            ("a lone surrogate code_pack", good[..^1] + ",\"code_pack\":\"\\ud800\"}", "code_pack"),
            ("a duplicate code_pack member", good[..^1] + ",\"code_pack\":\"" + CodePack + "\",\"code_pack\":\"" + CodePack + "\"}", "request.code_pack"),
            ("a string family size", good.Replace("\"family_size\":3", "\"family_size\":\"3\"", StringComparison.Ordinal), "family_size"),
            ("a fractional family size", good.Replace("\"family_size\":3", "\"family_size\":3.5", StringComparison.Ordinal), "family_size"),
            ("a fractional alternative count", good.Replace("\"alternatives\":4", "\"alternatives\":2.5", StringComparison.Ordinal), "alternatives"),
            ("an out-of-range seed", good.Replace("\"seed\":0", "\"seed\":4294967296", StringComparison.Ordinal), "seed"),
            ("a non-finite lot width", good.Replace("\"width_m\":16", "\"width_m\":1e400", StringComparison.Ordinal), "lot.width_m"),
            ("a string lot depth", good.Replace("\"depth_m\":16", "\"depth_m\":\"16\"", StringComparison.Ordinal), "lot.depth_m"),
            ("an unknown lot member", good.Replace("\"depth_m\":16", "\"depth_m\":16,\"height_m\":3", StringComparison.Ordinal), "lot.height_m"),
            ("a lot without depth", good.Replace(",\"depth_m\":16", "", StringComparison.Ordinal), "lot"),
            ("a non-object lot", good.Replace("{\"width_m\":16,\"depth_m\":16}", "[16,16]", StringComparison.Ordinal), "lot"),
            ("a malformed project id", good.Replace(Id, "not-a-guid", StringComparison.Ordinal), "project_id"),
            ("a braced project id", good.Replace(Id, "{" + Id + "}", StringComparison.Ordinal), "project_id"),
            ("a malformed pack id", good.Replace(TypologyPack, "../skills", StringComparison.Ordinal), "pack"),
            ("a non-string pack", good.Replace("\"" + TypologyPack + "\"", "7", StringComparison.Ordinal), "pack"),
            ("an unknown weight", good[..^1] + ",\"weights\":{\"beauty\":1}}", "weights.beauty"),
            ("a string weight", good[..^1] + ",\"weights\":{\"area_fit\":\"1\"}}", "weights.area_fit"),
            ("a non-object weights", good[..^1] + ",\"weights\":[1]}", "weights"),
            ("a lone surrogate pack", good.Replace(TypologyPack, "\\ud800", StringComparison.Ordinal), "pack"),
            ("a lone surrogate project id", good.Replace(Id, "\\ud800", StringComparison.Ordinal), "project_id"),
            ("a lone surrogate member name", good[..^1] + ",\"\\ud800\":1}", "request"),
            ("an exponent alternative count", good.Replace("\"alternatives\":4", "\"alternatives\":4e0", StringComparison.Ordinal), "alternatives"),
            ("a 3.0 family size", good.Replace("\"family_size\":3", "\"family_size\":3.0", StringComparison.Ordinal), "family_size"),
            ("a duplicate written with an escaped name", good.Replace("\"family_size\":3", "\"family_size\":3,\"\\u0066amily_size\":3", StringComparison.Ordinal), "request.family_size"),
            ("a duplicate lot member", good.Replace("\"depth_m\":16", "\"depth_m\":16,\"width_m\":16", StringComparison.Ordinal), "lot.width_m"),
            ("a duplicate weight", good[..^1] + ",\"weights\":{\"area_fit\":0.3,\"area_fit\":0.3}}", "weights.area_fit"),
            ("null weights", good[..^1] + ",\"weights\":null}", "weights"),
            ("a null pack", good.Replace("\"" + TypologyPack + "\"", "null", StringComparison.Ordinal), "pack"),
            ("an int32-overflow family size", good.Replace("\"family_size\":3", "\"family_size\":2147483648", StringComparison.Ordinal), "family_size"),
            ("a string skills member", good[..^1] + ",\"skills\":\"" + CompactStyle + "\"}", "skills"),
            ("null skills", good[..^1] + ",\"skills\":null}", "skills"),
            ("a non-string skill", good[..^1] + ",\"skills\":[7]}", "skills[0]"),
            ("a malformed skill id", good[..^1] + ",\"skills\":[\"" + CompactStyle + "\",\"../skills\"]}", "skills[1]"),
            ("a lone surrogate skill", good[..^1] + ",\"skills\":[\"\\ud800\"]}", "skills[0]"),
            ("more than 64 skills", good[..^1] + ",\"skills\":" + Ids(Enumerable.Repeat(CompactStyle, 65).ToArray()) + "}", "skills"),
            ("a duplicate skills member", good[..^1] + ",\"skills\":[],\"skills\":[]}", "request.skills"),
            ("an object precedence", good[..^1] + ",\"precedence\":{}}", "precedence"),
            ("a non-string precedence entry", good[..^1] + ",\"precedence\":[\"" + TypologyPack + "\",1]}", "precedence[1]"),
            ("a non-id precedence entry", good[..^1] + ",\"precedence\":[\"" + TypologyPack + "\",\"Not An Id\"]}", "precedence[1]"),
            ("an oversize precedence entry", good[..^1] + ",\"precedence\":[\"xx." + new string('a', 200) + "\"]}", "precedence[0]"),
            ("a lone surrogate precedence entry", good[..^1] + ",\"precedence\":[\"\\ud800\"]}", "precedence[0]"),
            ("more than 64 precedence entries", good[..^1] + ",\"precedence\":" + Ids(Enumerable.Repeat(TypologyPack, 65).ToArray()) + "}", "precedence"),
            ("an int32-underflow alternative count", good.Replace("\"alternatives\":4", "\"alternatives\":-2147483649", StringComparison.Ordinal), "alternatives"),
        };
        foreach (var member in new[] { "pack", "family_size", "lot", "alternatives", "seed", "project_id" }) malformed.Add(("a missing " + member, Without(member), member));
        foreach (var (label, text, detail) in malformed)
        {
            var output = Output();
            var result = Cli("generate-typology", Input("bad.json", text), output);
            var envelope = Parse(result.Out);
            Check(result.Exit == 1 && Str(envelope, "code") == "generate_typology_request_invalid" && (detail.Length == 0 || Str(envelope, "detail") == detail) && !File.Exists(output),
                label + " -> generate_typology_request_invalid" + (detail.Length > 0 ? " (detail " + detail + ")" : "") + ", exit 1, no output file", D(result));
        }
        var unknownPackOut = Output();
        var unknownPack = Cli("generate-typology", Input("unknown-pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), unknownPackOut);
        Check(unknownPack.Exit == 1 && Str(Parse(unknownPack.Out), "code") == "generate_typology_pack_unavailable" && Str(Parse(unknownPack.Out), "detail") == "input_not_found" && !File.Exists(unknownPackOut),
            "an unknown pack id -> generate_typology_pack_unavailable (input_not_found), exit 1, no file", D(unknownPack));

        // ---- pipeline refusal: the library's refusals, typed, no file ----
        void ExpectRefusal(string label, string text, SkillManifest typology, TypologyBriefRequest request, Guid id)
        {
            var library = TypologyPipeline.Run(typology, request, new PrototypePipelineOptions(caba, id));
            var envelope = RefusedEnvelope(library);
            var output = Output();
            var result = Cli("generate-typology", Input("refused.json", text), output);
            Check(library.Status == PrototypePipelineStatus.Refused && library.Refusals.Count > 0 && result.Exit == 1 && result.Out == envelope && !File.Exists(output),
                label + ": refused with the library's refusals exactly, exit 1, no output file", D(result) + " expected " + envelope);
        }
        ExpectRefusal("a family size of 0", Request(TypologyPack, 0, 16, 16, 4, 0), pack, new TypologyBriefRequest(0, new PrototypeLot(16, 16), 4, 0), projectId);
        ExpectRefusal("a family size above the pack bound", Request(TypologyPack, 9, 16, 16, 4, 0), pack, new TypologyBriefRequest(9, new PrototypeLot(16, 16), 4, 0), projectId);
        ExpectRefusal("a negative lot width", Request(TypologyPack, 3, -1, 16, 4, 0), pack, new TypologyBriefRequest(3, new PrototypeLot(-1, 16), 4, 0), projectId);
        ExpectRefusal("zero alternatives", Request(TypologyPack, 3, 16, 16, 0, 0), pack, new TypologyBriefRequest(3, new PrototypeLot(16, 16), 0, 0), projectId);
        ExpectRefusal("a negative weight", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"weights\":{\"area_fit\":-1}"), pack,
            new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 0, Weights: new ScoringWeights(-1, 0.25, 0.15, 0.1, 0.1, 0.1)), projectId);
        ExpectRefusal("an all-zero project id", Request(TypologyPack, 3, 16, 16, 4, 0).Replace(Id, "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal), pack,
            new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 0), Guid.Empty);
        ExpectRefusal("a code pack named as the typology pack", Request(CodePack, 3, 16, 16, 4, 0), caba, new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 0), projectId);

        // ---- the release package layout, outside any repository ----
        static bool UnderRepository(string directory)
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return true;
            return false;
        }
        var outsideBase = new[] { Environment.GetEnvironmentVariable("RUNNER_TEMP"), Path.GetTempPath(), OperatingSystem.IsWindows() ? null : "/tmp" }
            .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !UnderRepository(candidate));
        if (outsideBase is null) Console.WriteLine("SKIP generate-typology: no writable directory outside a repository for the package layout");
        else
        {
            var packageRoot = Path.Combine(outsideBase, "bim-typology-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                var cliDirectory = Path.GetDirectoryName(cliDll)!;
                foreach (var file in Directory.GetFiles(cliDirectory, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(packageRoot, "cli", Path.GetRelativePath(cliDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
                var packageDll = Path.Combine(packageRoot, "cli", Path.GetFileName(cliDll));
                File.WriteAllText(Path.Combine(packageRoot, "VERSION"), "0.0.0-test\n");
                File.WriteAllText(Path.Combine(packageRoot, "manifest.json"), "{}\n");
                foreach (var id in new[] { TypologyPack, CodePack })
                {
                    Directory.CreateDirectory(Path.Combine(packageRoot, "skills", id));
                    File.Copy(Path.Combine(root, "skills", id, "skill.json"), Path.Combine(packageRoot, "skills", id, "skill.json"));
                }
                Directory.CreateDirectory(Path.Combine(packageRoot, "skills", "zz.test.bad-json"));
                File.WriteAllText(Path.Combine(packageRoot, "skills", "zz.test.bad-json", "skill.json"), "{");
                var packagedOut = Output();
                var packaged = CliAt(packageDll, null, "generate-typology", acceptedRequest, packagedOut);
                Check(!UnderRepository(packageRoot) && packaged.Exit == 0 && Read(packagedOut).SequenceEqual(acceptedBytes) && packaged.Out == accepted.Out,
                    "a CLI in the package layout (cli/ beside skills/, outside any repository) writes the repository run's bytes and stdout", D(packaged) + " under " + outsideBase);
                var packagedComposedOut = Output();
                var packagedComposed = CliAt(packageDll, null, "generate-typology", composedRequest, packagedComposedOut);
                Check(packagedComposed.Exit == 0 && Read(packagedComposedOut).SequenceEqual(composedBytes) && packagedComposed.Out == composed.Out,
                    "a packaged CLI composed run writes the repository composed bytes and stdout", D(packagedComposed));
                var stricter = SkillParser.Parse(StricterPack);
                Directory.CreateDirectory(Path.Combine(packageRoot, "skills", stricter.Id));
                File.WriteAllText(Path.Combine(packageRoot, "skills", stricter.Id, "skill.json"), StricterPack, new UTF8Encoding(false));
                var strictLibrary = Composed(pack, request4, new[] { stricter }, null);
                var strictNone = Composed(pack, request4, null, null);
                var strictOut = Output();
                var strict = CliAt(packageDll, null, "generate-typology", Input("stricter.json", Request(TypologyPack, 4, 12, 12, 6, 0, ",\"skills\":" + Ids(stricter.Id))), strictOut);
                var strictExpected = strictLibrary.Status == PrototypePipelineStatus.Refused ? Array.Empty<byte>() : Utf8(TypologyPipelineSerializer.Serialize(strictLibrary));
                Check(strictLibrary.Rules is { } strictRules && strictRules.Traces.Any(trace => trace.Rule?.ToString() == stricter.Id + ":rule:bedroom-side-min")
                    && TypologyPipelineSerializer.Serialize(strictLibrary) != TypologyPipelineSerializer.Serialize(strictNone)
                    && (strictLibrary.Status == PrototypePipelineStatus.Refused
                        ? strict.Exit == 1 && strict.Out == RefusedEnvelope(strictLibrary)
                        : strict.Exit == 0 && Read(strictOut).SequenceEqual(strictExpected) && Str(Parse(strict.Out), "skills") is null && Parse(strict.Out) is { } so
                            && so.TryGetProperty("skills", out var strictSkills) && strictSkills.GetArrayLength() == 1 && strictSkills[0].GetString() == stricter.Id),
                    "an extra stricter pack read from the package skills/ is RunComposed with that manifest (traced to its rule, unlike CABA + typology)", D(strict));
                // P7-19: a rule-changing extra pack read from the package skills/, with and without "include":[].
                foreach (var (strictLabel, strictRequest) in new[] { ("family-4 lot", request4), ("family-3 16 x 16 lot", request3) })
                {
                    var strictLot = strictRequest.Lot;
                    string StrictText(string extra) => Request(TypologyPack, strictRequest.FamilySize, strictLot.WidthMetres, strictLot.DepthMetres, strictRequest.Alternatives, strictRequest.Seed, extra);
                    ExpectEmptyIncludeWithSkills("the stricter package pack (a rule-changing extra) on the " + strictLabel, packageDll,
                        StrictText(",\"skills\":" + Ids(stricter.Id)), StrictText(",\"skills\":" + Ids(stricter.Id) + ",\"include\":[]"),
                        Composed(pack, strictRequest, new[] { stricter }, null), Composed(pack, WithEmptyInclude(strictRequest), new[] { stricter }, null), new[] { stricter.Id });
                }
                var badSkillOut = Output();
                var badSkill = CliAt(packageDll, null, "generate-typology", Input("bad-skill.json", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids("zz.test.bad-json"))), badSkillOut);
                Check(badSkill.Exit == 1 && Str(Parse(badSkill.Out), "code") == "generate_typology_skill_invalid" && Str(Parse(badSkill.Out), "detail") == "json_invalid"
                    && Str(Parse(badSkill.Out), "skill") == "zz.test.bad-json" && !File.Exists(badSkillOut),
                    "a malformed extra skill manifest -> generate_typology_skill_invalid (json_invalid, skill id), exit 1, no file", D(badSkill));
                var badPackOut = Output();
                var badPack = CliAt(packageDll, null, "generate-typology", Input("bad-pack.json", good.Replace(TypologyPack, "zz.test.bad-json", StringComparison.Ordinal)), badPackOut);
                Check(badPack.Exit == 1 && Str(Parse(badPack.Out), "code") == "generate_typology_pack_invalid" && Str(Parse(badPack.Out), "detail") == "json_invalid" && !File.Exists(badPackOut),
                    "a malformed pack manifest -> generate_typology_pack_invalid (json_invalid), exit 1, no file", D(badPack));
                Directory.CreateDirectory(Path.Combine(packageRoot, "skills", "zz.test.other-id"));
                File.Copy(Path.Combine(root, "skills", TypologyPack, "skill.json"), Path.Combine(packageRoot, "skills", "zz.test.other-id", "skill.json"));
                var otherIdOut = Output();
                var otherId = CliAt(packageDll, null, "generate-typology", Input("other-id-pack.json", good.Replace(TypologyPack, "zz.test.other-id", StringComparison.Ordinal)), otherIdOut);
                Check(otherId.Exit == 1 && Str(Parse(otherId.Out), "code") == "generate_typology_pack_invalid" && Str(Parse(otherId.Out), "detail") == "id" && !File.Exists(otherIdOut),
                    "a manifest naming another pack id -> generate_typology_pack_invalid (detail id), exit 1, no file", D(otherId));
                File.WriteAllText(Path.Combine(packageRoot, "skills", CodePack, "skill.json"), "{");
                var badCodeOut = Output();
                var badCode = CliAt(packageDll, null, "generate-typology", acceptedRequest, badCodeOut);
                Check(badCode.Exit == 1 && Str(Parse(badCode.Out), "code") == "generate_typology_code_pack_invalid" && Str(Parse(badCode.Out), "detail") == "json_invalid" && !File.Exists(badCodeOut),
                    "a malformed code pack manifest -> generate_typology_code_pack_invalid (json_invalid), exit 1, no file", D(badCode));
                var bothBadOut = Output();
                var bothBad = CliAt(packageDll, null, "generate-typology", Input("both-bad-pack.json", good.Replace(TypologyPack, "zz.test.bad-json", StringComparison.Ordinal)), bothBadOut);
                Check(bothBad.Exit == 1 && Str(Parse(bothBad.Out), "code") == "generate_typology_pack_invalid" && Str(Parse(bothBad.Out), "detail") == "json_invalid" && !File.Exists(bothBadOut),
                    "with both manifests malformed the typology pack refusal wins (generate_typology_pack_invalid), exit 1, no file", D(bothBad));
                File.Delete(Path.Combine(packageRoot, "skills", CodePack, "skill.json"));
                var noCodeOut = Output();
                var noCode = CliAt(packageDll, null, "generate-typology", acceptedRequest, noCodeOut);
                Check(noCode.Exit == 1 && Str(Parse(noCode.Out), "code") == "generate_typology_code_pack_unavailable" && Str(Parse(noCode.Out), "detail") == "input_not_found" && !File.Exists(noCodeOut),
                    "a package without the code pack -> generate_typology_code_pack_unavailable (input_not_found), exit 1, no file", D(noCode));
                Directory.Delete(Path.Combine(packageRoot, "skills"), true);
                var noSkillsOut = Output();
                var noSkills = CliAt(packageDll, null, "generate-typology", acceptedRequest, noSkillsOut);
                Check(noSkills.Exit == 1 && Str(Parse(noSkills.Out), "code") == "generate_typology_repository_not_found" && !File.Exists(noSkillsOut),
                    "the package layout without skills/ (outside any repository) -> generate_typology_repository_not_found, no file", D(noSkills));
            }
            finally { try { Directory.Delete(packageRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        UnguardedRun(root, scratch, cliDll, Check, arguments => Cli(arguments));
        PostWriteRun(root, scratch, cliDll, Check, arguments => Cli(arguments));
        PostWriteRemovalRun(root, scratch, cliDll, Check, arguments => Cli(arguments));
        DiagnosticWriteRun(scratch, cliDll, Check, arguments => Cli(arguments));
        OutputWriteRun(root, scratch, cliDll, Check, arguments => Cli(arguments));
        PartialWriteRun(root, scratch, cliDll, Check, arguments => Cli(arguments));
    }

    // ---- 9. P3-24: a non-refusal exception outside the guarded pipeline block on the CLI path is the typed internal error ----
    // GenerateTypologyCommand.Run of the real Release CLI assembly (loaded from cliDll, members by reflection, so a missing
    // seam is an assertion failure), in process, with valid inputs and the P3-23 test seam UnguardedWorkProbe (null in
    // production; Generate calls it before Parse and the pack loads) throwing: stdout is IntentPlan.Internal's
    // {"status":"failed","code":"generate_typology_internal_error"} line, stderr its diagnostic once, exit 1, nothing
    // escapes Run (so Program's generic catch, whose line would carry the exception message as the code, is not reached)
    // and no output file. The seams are null before and reset in a finally; afterwards the in-process Run gives the P5-12
    // pinned file and the same stdout as the real CLI process, and a sample of refusals (usage, output path, request,
    // pack, extra skill, pipeline) prints the real process's bytes. Static: Run is try Execute / catch (Refusal) Fail / one
    // catch-all through IntentPlan.Internal("generate-typology", error).Print().
    static void UnguardedRun(string root, string scratch, string cliDll, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "P3-24 " + name, detail);
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
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var inputs = Path.Combine(scratch, "p3-24-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            var good = "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}";
            var request = Input("request.json", good);

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateTypologyCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var unguarded = type?.GetProperty("UnguardedWorkProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p) => p is not null && p.PropertyType == typeof(Action) && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(unguarded) && Seam(guarded), "Run(string[]) and the settable static Action seams UnguardedWorkProbe / GuardedWorkProbe found in the Release CLI",
                (run is null ? "<missing Run> " : "") + (unguarded?.PropertyType.FullName ?? "<missing UnguardedWorkProbe>") + " " + (guarded?.PropertyType.FullName ?? "<missing GuardedWorkProbe>"));
            if (run is null || unguarded is null || guarded is null || !Seam(unguarded) || !Seam(guarded)) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo probe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[]? args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            Check(probe.GetValue(null) is null && guardedProbe.GetValue(null) is null, "the generate-typology seams are null unless a test sets them");

            // The typed bytes, and the line Program's generic catch would print for the escaped exception (the old behaviour).
            var failure = new InvalidOperationException("boom generate-typology P3-24 unguarded");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == "generate-typology internal error: System.InvalidOperationException: boom generate-typology P3-24 unguarded",
                "fixture: IntentPlan.Internal(\"generate-typology\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            var generic = JsonSerializer.Serialize(new { status = "failed", code = failure.Message });

            var forcedOut = Output();
            var forced = Quiet(() =>
            {
                probe.SetValue(null, (Action)(() => throw failure));
                try { return Invoke(new[] { "generate-typology", request, forcedOut }); }
                finally { probe.SetValue(null, null); }
            });
            Check(probe.GetValue(null) is null && guardedProbe.GetValue(null) is null, "UnguardedWorkProbe is reset to null after the forced run");
            Check(forced.Thrown is null, "a forced exception before the guarded block does not escape Run to Program's generic catch", Q(forced));
            Check(forced.Out == envelope.Line + nl && forced.Out != generic + nl && !forced.Out.Contains(failure.Message, StringComparison.Ordinal),
                "the forced run prints the typed generate_typology_internal_error line (not the exception message)", Q(forced));
            Check(forced.Err == envelope.Diagnostic + nl, "the forced run writes the IntentPlan diagnostic to stderr exactly once", Q(forced));
            Check(forced.Exit == 1 && !File.Exists(forcedOut) && !Directory.Exists(forcedOut), "the forced run exits 1 and writes no output file", Q(forced) + (File.Exists(forcedOut) ? " output written" : ""));

            // A forced run with null args: Execute itself throws (args.Length, a NullReferenceException) before the seam is
            // reached; Run's catch-all prints the typed line and its diagnostic once, and the seam is still reset.
            var nullArgs = Quiet(() =>
            {
                probe.SetValue(null, (Action)(() => throw failure));
                try { return Invoke(null); }
                finally { probe.SetValue(null, null); }
            });
            var nullEnvelope = Bim.Cli.IntentPlan.Internal("generate-typology", new NullReferenceException());
            Check(nullArgs.Thrown is null && nullArgs.Out == nullEnvelope.Line + nl && nullArgs.Out == envelope.Line + nl && nullArgs.Err == nullEnvelope.Diagnostic + nl && nullArgs.Exit == 1,
                "a forced run with null args prints the typed generate_typology_internal_error line, the NullReferenceException diagnostic once, exit 1, nothing thrown", Q(nullArgs));
            Check(probe.GetValue(null) is null, "UnguardedWorkProbe is null after a forced run with null args");

            // Seams unset: the in-process Run is the real process, for the success and a sample of refusals.
            var inOut = Output();
            var inProcess = Quiet(() => Invoke(new[] { "generate-typology", request, inOut }));
            var processOut = Output();
            var process = cli(new[] { "generate-typology", request, processOut });
            var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
            var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
            Check(inProcess.Thrown is null && inProcess.Exit == 0 && inProcess.Err.Length == 0 && Hex(inBytes) == PinnedAcceptedSha256 && inBytes.SequenceEqual(processBytes)
                && process.Exit == 0 && AsProcess(inProcess) == process,
                "after the reset the in-process Run writes the P5-12 pinned file and the real process's stdout", Q(inProcess) + " vs process exit " + process.Exit.ToString(CultureInfo.InvariantCulture) + " " + process.Out + " " + process.Err);

            // The refusal sample compares the in-process Run with a real process of the same Release binary (same code on
            // both sides), so it shows the in-process path is the process path; that these bytes are unchanged by P3-24
            // rests on the literal pins of sections 1-8 above, which run against the real process.
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-typology", request }),
                ("output extension", output => new[] { "generate-typology", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-typology", request, existing }),
                ("missing request", output => new[] { "generate-typology", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-typology", Input("json.json", "{"), output }),
                ("unknown member", output => new[] { "generate-typology", Input("member.json", good[..^1] + ",\"revision\":1}"), output }),
                ("unknown pack", output => new[] { "generate-typology", Input("pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), output }),
                ("unknown extra skill", output => new[] { "generate-typology", Input("skill.json", good[..^1] + ",\"skills\":[\"xx.no-such.skill\"]}"), output }),
                ("pipeline refusal", output => new[] { "generate-typology", Input("refused.json", good.Replace("\"family_size\":3", "\"family_size\":0", StringComparison.Ordinal)), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var a = Output();
                var b = Output();
                var mine = Quiet(() => Invoke(argsOf(a)));
                var theirs = cli(argsOf(b));
                Check(mine.Thrown is null && mine.Exit == 1 && theirs.Exit == 1 && AsProcess(mine) == theirs && mine.Err.Length == 0 && !File.Exists(a) && !File.Exists(b)
                    && mine.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !mine.Out.Contains("internal_error", StringComparison.Ordinal),
                    "seams unset: the " + label + " refusal of the in-process Run equals the real process bytes", Q(mine) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(File.ReadAllText(existing) == "keep", "the existing output is left untouched");

            // Static: Run's body, comments stripped, layout-independent (P4-26: Run lives in the CLI-only write part).
            var source = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateTypologyWrite.cs")), blankStrings: false);
            Check(System.Text.RegularExpressions.Regex.IsMatch(source,
                    // P3-30: catch (Refusal) sets the exit code, then runs Fail guarded (a throwing stdout drops only the typed line).
                    @"public\s+static\s+void\s+Run\s*\(\s*string\s*\[\s*\]\s*args\s*\)\s*\{\s*try\s*\{\s*Execute\s*\(\s*args\s*\)\s*;\s*\}\s*catch\s*\(\s*Refusal\s+refusal\s*\)\s*\{\s*Environment\s*\.\s*ExitCode\s*=\s*1\s*;\s*"
                    + @"try\s*\{\s*Fail\s*\(\s*refusal\s*\.\s*Code\s*,\s*refusal\s*\.\s*Detail\s*,\s*refusal\s*\.\s*Skill\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}\s*"
                    // P3-28: the catch-all writes the diagnostic guarded (a throwing stderr drops only it), then prints the typed line;
                    // P3-30: with the exit code set first, and the typed line printed guarded.
                    + @"catch\s*\(\s*Exception\s+error\s*\)\s*\{\s*var\s+internalError\s*=\s*IntentPlan\s*\.\s*Internal\s*\(\s*""generate-typology""\s*,\s*error\s*\)\s*;\s*"
                    + @"try\s*\{\s*Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*internalError\s*\.\s*Diagnostic\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*"
                    + @"Environment\s*\.\s*ExitCode\s*=\s*internalError\s*\.\s*Exit\s*;\s*"
                    + @"try\s*\{\s*\(\s*internalError\s+with\s*\{\s*Diagnostic\s*=\s*null\s*\}\s*\)\s*\.\s*Print\s*\(\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*\}\s*\}"),
                "GenerateTypologyCommand.Run is try Execute / catch (Refusal) { Environment.ExitCode = 1; try { Fail(...); } catch (Exception) { } } / catch (Exception) { var internalError = IntentPlan.Internal(\"generate-typology\", error); "
                + "try { Console.Error.WriteLine(internalError.Diagnostic); } catch (Exception) { } Environment.ExitCode = internalError.Exit; try { (internalError with { Diagnostic = null }).Print(); } catch (Exception) { } }");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---- 10. P3-25: a non-refusal exception after GltfExporter.WriteNew succeeded leaves no output file ----
    // GenerateTypologyCommand.Run of the real Release CLI assembly (reflection, so a missing seam is an assertion failure),
    // in process, with valid inputs and the P3-25 test seam PostWriteProbe (null in production; Execute calls it once,
    // right after GltfExporter.WriteNew returned) throwing: the probe sees the just-written file holding the P5-12 pinned
    // bytes (so the exception really is post-write), then stdout is IntentPlan.Internal's typed
    // {"status":"failed","code":"generate_typology_internal_error"} line, stderr its diagnostic once, exit 1, nothing
    // escapes Run, and the written file is gone. A pre-existing output with the seam set is still output_exists before any
    // write: the probe never runs and the file is byte- and mtime-identical. Every refusal sampled with the seam set never
    // reaches the probe and prints the real process's bytes; with the seam unset the in-process Run is the P5-12 pinned
    // file and the real process's stdout. Static: PostWriteProbe is declared once, invoked once inside Execute after
    // GltfExporter.WriteNew( and before the SHA-256, never assigned under interfaces/ (with self-tests).
    static void PostWriteRun(string root, string scratch, string cliDll, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "P3-25 " + name, detail);
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

        var inputs = Path.Combine(scratch, "p3-25-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            var good = "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}";
            var request = Input("request.json", good);
            var composedRequest = Input("composed.json", good[..^1] + ",\"skills\":[]}");

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateTypologyCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var postWrite = type?.GetProperty("PostWriteProbe", Any);
            var unguarded = type?.GetProperty("UnguardedWorkProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p) => p is not null && p.PropertyType == typeof(Action) && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite), "Run(string[]) and the settable static Action seam PostWriteProbe found in the Release CLI",
                (run is null ? "<missing Run> " : "") + (postWrite?.PropertyType.FullName ?? "<missing PostWriteProbe>"));
            if (run is null || postWrite is null || !Seam(postWrite)) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo probe = postWrite;
            Exception? Invoke(string[]? args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            Check(Seam(unguarded) && Seam(guarded), "the P3-20 / P3-23 seams GuardedWorkProbe / UnguardedWorkProbe found in the Release CLI (read by AllNull below)",
                (unguarded?.PropertyType.FullName ?? "<missing UnguardedWorkProbe>") + " " + (guarded?.PropertyType.FullName ?? "<missing GuardedWorkProbe>"));
            if (unguarded is null || guarded is null) return;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            bool AllNull() => probe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-typology seams (PostWriteProbe, UnguardedWorkProbe, GuardedWorkProbe) are null unless a test sets them");

            var failure = new InvalidOperationException("boom generate-typology P3-25 post-write");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == "generate-typology internal error: System.InvalidOperationException: boom generate-typology P3-25 post-write",
                "fixture: IntentPlan.Internal(\"generate-typology\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            var generic = JsonSerializer.Serialize(new { status = "failed", code = failure.Message });

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
                    try { return Invoke(new[] { "generate-typology", requestPath, output }); }
                    finally { probe.SetValue(null, null); }
                });
                Check(AllNull(), label + ": PostWriteProbe is reset to null after the forced run");
                Check(calls == 1 && seen is { Length: > 0 } && (pin is null || Hex(seen) == pin),
                    label + ": the probe ran once, after GltfExporter.WriteNew wrote the file" + (pin is null ? "" : " (P5-12 pinned bytes, " + pin[..8] + "...)"),
                    "calls " + calls.ToString(CultureInfo.InvariantCulture) + " seen " + (seen is null ? "<no file>" : Hex(seen)));
                Check(forced.Thrown is null, label + ": the post-write exception does not escape Run to Program's generic catch", Q(forced));
                Check(forced.Out == envelope.Line + nl && forced.Out != generic + nl && !forced.Out.Contains(failure.Message, StringComparison.Ordinal) && !forced.Out.Contains("succeeded", StringComparison.Ordinal),
                    label + ": stdout is only the typed generate_typology_internal_error line (no success line, not the exception message)", Q(forced));
                Check(forced.Err == envelope.Diagnostic + nl, label + ": stderr is the IntentPlan diagnostic exactly once", Q(forced));
                Check(forced.Exit == 1 && !File.Exists(output) && !Directory.Exists(output), label + ": exit 1 and the just-written output file is deleted",
                    Q(forced) + (File.Exists(output) ? " output left (" + new FileInfo(output).Length.ToString(CultureInfo.InvariantCulture) + " bytes)" : ""));
            }
            ExpectRemoved("plain run", request, PinnedAcceptedSha256);
            ExpectRemoved("composed run (skills [])", composedRequest, PinnedComposedSha256);

            // A pre-existing output is never touched: output_exists refuses before any write, so the probe never runs.
            var existing = Output();
            var sentinel = new UTF8Encoding(false).GetBytes("{\"sentinel\":\"p3-25\"}");
            File.WriteAllBytes(existing, sentinel);
            var stamp = new DateTime(2002, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(existing, stamp);
            var existingCalls = 0;
            var exists = Quiet(() =>
            {
                probe.SetValue(null, (Action)(() => { existingCalls++; throw failure; }));
                try { return Invoke(new[] { "generate-typology", request, existing }); }
                finally { probe.SetValue(null, null); }
            });
            Check(exists.Thrown is null && exists.Exit == 1 && exists.Out == "{\"status\":\"failed\",\"code\":\"output_exists\"}" + nl && exists.Err.Length == 0 && existingCalls == 0
                && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
                "with the seam set, an existing output is output_exists before any write (probe not run) and left byte- and mtime-identical", Q(exists) + " calls " + existingCalls.ToString(CultureInfo.InvariantCulture));
            Check(AllNull(), "PostWriteProbe is null after the existing-output run");

            // Refusals (every one before the write) never reach the probe and print the real process's bytes.
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-typology", request }),
                ("output extension", output => new[] { "generate-typology", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-typology", request, existing }),
                ("missing request", output => new[] { "generate-typology", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-typology", Input("json.json", "{"), output }),
                ("unknown member", output => new[] { "generate-typology", Input("member.json", good[..^1] + ",\"revision\":1}"), output }),
                ("unknown pack", output => new[] { "generate-typology", Input("pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), output }),
                ("non-code code_pack", output => new[] { "generate-typology", Input("code.json", good[..^1] + ",\"code_pack\":\"" + CompactStyle + "\"}"), output }),
                ("unknown extra skill", output => new[] { "generate-typology", Input("skill.json", good[..^1] + ",\"skills\":[\"xx.no-such.skill\"]}"), output }),
                ("pipeline refusal", output => new[] { "generate-typology", Input("refused.json", good.Replace("\"family_size\":3", "\"family_size\":0", StringComparison.Ordinal)), output }),
                ("composed resolution refusal", output => new[] { "generate-typology", Input("conflict.json", good[..^1] + ",\"skills\":[\"" + CompactStyle + "\",\"" + MasonryStyle + "\"]}"), output }),
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
                Check(mine.Thrown is null && refusalCalls == 0 && mine.Exit == 1 && theirs.Exit == 1 && AsProcess(mine) == theirs && mine.Err.Length == 0 && !File.Exists(a) && !File.Exists(b)
                    && mine.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !mine.Out.Contains("internal_error", StringComparison.Ordinal),
                    "with the seam set, the " + label + " refusal never reaches the probe and equals the real process bytes", Q(mine) + " calls " + refusalCalls.ToString(CultureInfo.InvariantCulture) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(AllNull() && File.ReadAllBytes(existing).SequenceEqual(sentinel), "the seams are null after the refusal sample and the existing output is untouched");

            // Seams unset: the in-process Run is the real process (P5-12 pinned file, same stdout, nothing on stderr).
            foreach (var (label, requestPath, pin) in new[] { ("plain", request, PinnedAcceptedSha256), ("composed", composedRequest, PinnedComposedSha256) })
            {
                var inOut = Output();
                var inProcess = Quiet(() => Invoke(new[] { "generate-typology", requestPath, inOut }));
                var processOut = Output();
                var process = cli(new[] { "generate-typology", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(inProcess.Thrown is null && inProcess.Exit == 0 && inProcess.Err.Length == 0 && Hex(inBytes) == pin && inBytes.SequenceEqual(processBytes)
                    && process.Exit == 0 && AsProcess(inProcess) == process && Bim.Cli.IntentPlan.Internal("generate-typology", failure).Line != process.Out,
                    "seams unset: the " + label + " in-process Run writes the pinned file (" + pin[..8] + "...) and the real process's stdout", Q(inProcess) + " vs process " + process.Out + " " + process.Err);
            }

            // Static: the seam over interfaces/**/*.cs, comments and literals stripped, plus self-tests of the checker.
            var interfaces = Path.Combine(root, "interfaces");
            var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file))).ToArray();
            var seam = PostWriteSeamProblems(sources);
            Check(seam.Count == 0, "interfaces/**/*.cs names PostWriteProbe only in its declaration and one ?.Invoke() inside Execute after GltfExporter.WriteNew( and a try {, before SHA256.HashData(, never assigned ["
                + string.Join("; ", seam) + "]");
            const string Path25 = "interfaces/cli/GenerateTypologyWrite.cs";
            const string Decl = "internal static Action? PostWriteProbe { get; set; }";
            string Command(string executeBody, string extra = "") => "internal static class GenerateTypologyCommand { static void Execute(string[] args) { " + executeBody + " } " + Decl + " " + extra + " }";
            const string GoodBody = "GltfExporter.WriteNew(bytes, output); try { PostWriteProbe?.Invoke(); var sha256 = SHA256.HashData(bytes); } catch { throw; }";
            Check(PostWriteSeamProblems(new[] { (Path25, Command(GoodBody)) }).Count == 0
                && PostWriteSeamProblems(new[] { (Path25, Command("GltfExporter . WriteNew ( bytes, output );\n try\n { PostWriteProbe ?. Invoke ( ) ; // PostWriteProbe = null;\n var s = \"PostWriteProbe = x\"; SHA256 . HashData ( bytes ); } catch { throw; }")) }).Count == 0,
                "self-test: the post-write seam checker accepts the real shape at any spacing, ignoring comments and string literals");
            var evasions = new (string Label, (string, string)[] Files)[]
            {
                ("an assignment in the command", new[] { (Path25, Command(GoodBody, "static void Set() { PostWriteProbe = () => throw null!; }")) }),
                ("an assignment in another interfaces file", new[] { (Path25, Command(GoodBody)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateTypologyCommand.PostWriteProbe = null; } }") }),
                ("a compound assignment", new[] { (Path25, Command(GoodBody)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateTypologyCommand.PostWriteProbe ??= null; } }") }),
                ("the invoke before WriteNew(", new[] { (Path25, Command("PostWriteProbe?.Invoke(); GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes);")) }),
                ("the invoke before the deleting try", new[] { (Path25, Command("GltfExporter.WriteNew(bytes, output); PostWriteProbe?.Invoke(); try { SHA256.HashData(bytes); } catch { throw; }")) }),
                ("the invoke after SHA256.HashData(", new[] { (Path25, Command("GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes); PostWriteProbe?.Invoke();")) }),
                ("the invoke outside Execute", new[] { (Path25, Command("GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes);", "static void Other() { PostWriteProbe?.Invoke(); }")) }),
                ("the invoke missing", new[] { (Path25, Command("GltfExporter.WriteNew(bytes, output); SHA256.HashData(bytes);")) }),
                ("a second invoke", new[] { (Path25, Command("GltfExporter.WriteNew(bytes, output); PostWriteProbe?.Invoke(); PostWriteProbe?.Invoke(); SHA256.HashData(bytes);")) }),
                ("the declaration missing", new[] { (Path25, "internal static class GenerateTypologyCommand { static void Execute(string[] args) { " + GoodBody + " } }") }),
                ("the command file missing", new[] { ("interfaces/cli/Other.cs", Command(GoodBody)) }),
            };
            foreach (var (label, files) in evasions)
                Check(PostWriteSeamProblems(files).Count > 0, "self-test: the post-write seam checker flags " + label);
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // P3-25: over (relative path, source) pairs under interfaces/, comments stripped and string literals blanked:
    // PostWriteProbe is named exactly twice in total, both in interfaces/cli/GenerateTypologyWrite.cs (P4-26: the CLI-only
    // write part of GenerateTypologyCommand, where Execute and the seam moved verbatim): its declaration
    // and one PostWriteProbe?.Invoke(); inside Execute after its GltfExporter.WriteNew( and before its SHA256.HashData(,
    // with a try { opening between WriteNew( and the invoke (the invoke is inside the deleting try); no assignment (=, +=, ??=, ...) anywhere.
    static List<string> PostWriteSeamProblems(IEnumerable<(string Name, string Source)> files)
    {
        var problems = new List<string>();
        var total = 0;
        var sawCommand = false;
        foreach (var (name, source) in files)
        {
            var code = ApplyIntentTests.CodeOf(source);
            total += System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteProbe\b").Count;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bPostWriteProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": PostWriteProbe assigned");
            if (name != "interfaces/cli/GenerateTypologyWrite.cs") continue;
            sawCommand = true;
            var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+static\s+Action\s*\?\s*PostWriteProbe\s*\{\s*get\s*;\s*set\s*;\s*\}").Count;
            if (declarations != 1) problems.Add(name + ": the declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;");
            var execute = System.Text.RegularExpressions.Regex.Match(code, @"\bstatic\s+void\s+Execute\s*\(");
            if (invokes.Count != 1 || !execute.Success) { problems.Add(name + ": " + invokes.Count.ToString(CultureInfo.InvariantCulture) + " PostWriteProbe?.Invoke() calls, Execute " + (execute.Success ? "found" : "missing")); continue; }
            var body = execute.Index + execute.Length;
            var write = System.Text.RegularExpressions.Regex.Match(code[body..], @"\bGltfExporter\s*\.\s*WriteNew\s*\(");
            var hash = System.Text.RegularExpressions.Regex.Match(code[body..], @"\bSHA256\s*\.\s*HashData\s*\(");
            var writeAt = write.Success ? body + write.Index : -1;
            var hashAt = hash.Success ? body + hash.Index : -1;
            var at = invokes[0].Index;
            if (writeAt < 0 || hashAt < 0 || at < writeAt || at > hashAt) problems.Add(name + ": PostWriteProbe?.Invoke() not inside Execute between GltfExporter.WriteNew( and SHA256.HashData(");
            // The invoke sits inside the deleting try: a try { opens after WriteNew( and before the invoke.
            else if (!System.Text.RegularExpressions.Regex.IsMatch(code[writeAt..at], @"\btry\s*\{")) problems.Add(name + ": no try { between GltfExporter.WriteNew( and PostWriteProbe?.Invoke()");
        }
        if (!sawCommand) problems.Add("interfaces/cli/GenerateTypologyWrite.cs not found");
        if (total != 2) problems.Add("PostWriteProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // ---- 11. P3-26: a failed post-write cleanup delete is reported on stderr; the original exception is still the one reported ----
    // GenerateTypologyCommand.Run of the real Release CLI assembly (reflection, so a missing seam is an assertion failure),
    // in process, with the P3-25 seam PostWriteProbe throwing the original exception and the P3-26 test seam
    // PostWriteRemovalProbe (Action<string>, null in production; the post-write catch calls it once with the output path
    // right before File.Delete(output)) throwing an IOException, an UnauthorizedAccessException or a non-IO exception: the
    // delete never runs, so the just-written file (the P5-12 pinned bytes, or the composed pin) is left on disk; stdout is
    // still only the typed {"status":"failed","code":"generate_typology_internal_error"} line, exit 1, nothing escapes Run,
    // and stderr is the documented line
    //   generate-typology: could not delete partial output: <output path as given>
    // (GenerateTypologyCommand.PartialOutputLeft + path) followed by the IntentPlan diagnostic of the ORIGINAL exception
    // exactly once (the delete exception's type and message appear nowhere). A stderr that throws on the cleanup line is
    // swallowed too (only the original diagnostic is recorded). A removal probe that does not throw keeps section 10's
    // bytes (file deleted, stderr only the diagnostic); with the removal probe set, success and the sampled refusals never
    // call it and equal the real process. Static: PostWriteRemovalProbe is declared once, never assigned under interfaces/,
    // invoked once inside Execute as the first statement of a try { PostWriteRemovalProbe?.Invoke(output); File.Delete(output); }
    // that is the first statement of the catch (Exception) following PostWriteProbe's try, directly followed by an
    // unfiltered catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }
    // without a throw, then throw; as the handler's last statement (brace-matched, with self-tests run first). Not named
    // PostWriteProbe (P3-25's pin) or PostWriteDeleteProbe (P7-27's pin counts that name over interfaces/).
    static void PostWriteRemovalRun(string root, string scratch, string cliDll, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "P3-26 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string Left = "generate-typology: could not delete partial output: ";
        var nl = Environment.NewLine;

        // Static first (so the checker's self-tests run even when the seam is missing): the removal seam over
        // interfaces/**/*.cs, comments and literals stripped.
        {
            var interfaces = Path.Combine(root, "interfaces");
            var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file))).ToArray();
            const string Path26 = "interfaces/cli/GenerateTypologyWrite.cs";
            const string Decls = "internal const string PartialOutputLeft = \"x\"; internal static Action<string>? PostWriteRemovalProbe { get; set; }";
            static string Command(string handler, string extra = "", string decls = Decls) =>
                "internal static class GenerateTypologyCommand { static void Execute(string[] args) { GltfExporter.WriteNew(bytes, output); try { PostWriteProbe?.Invoke(); Console.WriteLine(x); } catch (Exception) { "
                + handler + " } } " + decls + " " + extra + " }";
            const string Inner = "try { PostWriteRemovalProbe?.Invoke(output); File.Delete(output); }";
            const string Report = " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }";
            const string Good = Inner + Report + " throw;";
            var accepted = new[]
            {
                PostWriteRemovalSeamProblems(new[] { (Path26, Command(Good)) }),
                PostWriteRemovalSeamProblems(new[] { (Path26, Command("try\n { PostWriteRemovalProbe ?. Invoke ( output ) ; // PostWriteRemovalProbe = null; }\n File . Delete ( output ) ; }\n catch ( Exception )\n"
                    + " { try { /* throw; } */ Console . Error . WriteLine ( PartialOutputLeft + output ) ; }\n catch ( Exception ) { /* } throw; { */ } }\n throw ;", "static string S = \"} throw; { catch\";")) }),
            };
            Check(accepted.All(p => p.Count == 0),
                "self-test: the removal seam checker accepts the real shape at any spacing, ignoring comments and string literals (braces and throw in them too)",
                string.Join(" | ", accepted.Select(p => string.Join("; ", p))));
            var evasions = new (string Label, (string, string)[] Files)[]
            {
                ("an assignment in the command", new[] { (Path26, Command(Good, "static void Set() { PostWriteRemovalProbe = _ => { }; }")) }),
                ("an assignment in another interfaces file", new[] { (Path26, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateTypologyCommand.PostWriteRemovalProbe = null; } }") }),
                ("a compound assignment", new[] { (Path26, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateTypologyCommand.PostWriteRemovalProbe ??= null; } }") }),
                ("a third name elsewhere", new[] { (Path26, Command(Good)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateTypologyCommand.PostWriteRemovalProbe?.Invoke(\"\"); } }") }),
                ("the declaration missing", new[] { (Path26, Command(Good, decls: "internal const string PartialOutputLeft = \"x\";")) }),
                ("an Action declaration", new[] { (Path26, Command(Good, decls: "internal const string PartialOutputLeft = \"x\"; internal static Action? PostWriteRemovalProbe { get; set; }")) }),
                ("the constant missing", new[] { (Path26, Command(Good, decls: "internal static Action<string>? PostWriteRemovalProbe { get; set; }")) }),
                ("the command file missing", new[] { ("interfaces/cli/Other.cs", Command(Good)) }),
                ("the invoke missing", new[] { (Path26, Command("try { File.Delete(output); }" + Report + " throw;")) }),
                ("a second invoke", new[] { (Path26, Command("PostWriteRemovalProbe?.Invoke(output); " + Good)) }),
                ("the invoke after File.Delete", new[] { (Path26, Command("try { File.Delete(output); PostWriteRemovalProbe?.Invoke(output); }" + Report + " throw;")) }),
                ("the invoke with another argument", new[] { (Path26, Command("try { PostWriteRemovalProbe?.Invoke(args[2]); File.Delete(output); }" + Report + " throw;")) }),
                ("the invoke outside Execute", new[] { (Path26, Command("try { File.Delete(output); }" + Report + " throw;", "static void Other() { try { PostWriteRemovalProbe?.Invoke(output); File.Delete(output); }" + Report + " }")) }),
                ("the delete try not first in the handler", new[] { (Path26, Command("Console.Error.WriteLine(x); " + Good)) }),
                ("the delete try in a catch not after the post-write probe's try", new[] { (Path26,
                    "internal static class GenerateTypologyCommand { static void Execute(string[] args) { GltfExporter.WriteNew(bytes, output); try { Console.WriteLine(x); } catch (Exception) { " + Good + " } } " + Decls + " }") }),
                ("a filtered report catch", new[] { (Path26, Command(Inner + " catch (Exception removal) when (removal is IOException) { Console.Error.WriteLine(PartialOutputLeft + output); } throw;")) }),
                ("an IOException-only report catch", new[] { (Path26, Command(Inner + " catch (IOException) { Console.Error.WriteLine(PartialOutputLeft + output); } throw;")) }),
                ("the P3-25 IO-filtered silent catch", new[] { (Path26, Command(Inner + " catch (Exception removal) when (removal is IOException or UnauthorizedAccessException) { } throw;")) }),
                ("a report catch that rethrows", new[] { (Path26, Command(Inner + " catch (Exception) { Console.Error.WriteLine(PartialOutputLeft + output); throw; } throw;")) }),
                ("a report catch that throws the delete exception", new[] { (Path26, Command(Inner + " catch (Exception removal) { Console.Error.WriteLine(PartialOutputLeft + output); throw removal; } throw;")) }),
                ("a report catch without the stderr line", new[] { (Path26, Command(Inner + " catch (Exception) { } throw;")) }),
                ("an unwrapped report line", new[] { (Path26, Command(Inner + " catch (Exception) { Console.Error.WriteLine(PartialOutputLeft + output); } throw;")) }),
                ("a throw in the report line's inner catch", new[] { (Path26, Command(Inner + " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { throw; } } throw;")) }),
                ("a filtered inner catch around the report line", new[] { (Path26, Command(Inner + " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (IOException) { } } throw;")) }),
                ("a statement after the wrapped report line", new[] { (Path26, Command(Inner + " catch (Exception) { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } Console.Error.WriteLine(x); } throw;")) }),
                ("a report line to stdout", new[] { (Path26, Command(Inner + " catch (Exception) { try { Console.WriteLine(PartialOutputLeft + output); } catch (Exception) { } } throw;")) }),
                ("throw; missing after the report catch", new[] { (Path26, Command(Inner + Report)) }),
                ("a statement after throw;", new[] { (Path26, Command(Good + " return;")) }),
                ("throw new instead of throw;", new[] { (Path26, Command(Inner + Report + " throw new InvalidOperationException();")) }),
            };
            foreach (var (label, files) in evasions)
                Check(PostWriteRemovalSeamProblems(files).Count > 0, "self-test: the removal seam checker flags " + label);
            var seam = PostWriteRemovalSeamProblems(sources);
            Check(seam.Count == 0, "interfaces/**/*.cs names PostWriteRemovalProbe only in its declaration and one ?.Invoke(output) right before File.Delete(output) in the post-write catch (Exception), "
                + "whose unfiltered catch (Exception) writes Console.Error.WriteLine(PartialOutputLeft + output) without a throw, then throw; ends the handler; never assigned [" + string.Join("; ", seam) + "]");
        }

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

        var inputs = Path.Combine(scratch, "p3-26-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            var good = "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}";
            var request = Input("request.json", good);
            var composedRequest = Input("composed.json", good[..^1] + ",\"skills\":[]}");

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateTypologyCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var postWrite = type?.GetProperty("PostWriteProbe", Any);
            var removalSeam = type?.GetProperty("PostWriteRemovalProbe", Any);
            var unguarded = type?.GetProperty("UnguardedWorkProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            var leftField = type?.GetField("PartialOutputLeft", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite, typeof(Action)) && Seam(removalSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]), the settable static Action<string> seam PostWriteRemovalProbe and the Action seams PostWriteProbe / UnguardedWorkProbe / GuardedWorkProbe found in the Release CLI",
                (run is null ? "<missing Run> " : "") + (removalSeam?.PropertyType.FullName ?? "<missing PostWriteRemovalProbe>") + " " + (postWrite?.PropertyType.FullName ?? "<missing PostWriteProbe>"));
            if (run is null || postWrite is null || removalSeam is null || unguarded is null || guarded is null || !Seam(removalSeam, typeof(Action<string>)) || !Seam(postWrite, typeof(Action))) return;
            Check(leftField is { IsLiteral: true } && leftField.FieldType == typeof(string) && (string?)leftField.GetRawConstantValue() == Left,
                "GenerateTypologyCommand.PartialOutputLeft is the documented constant \"" + Left + "\"", leftField is { IsLiteral: true } ? "[" + leftField.GetRawConstantValue() + "]" : "<missing const>");
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo probe = postWrite;
            System.Reflection.PropertyInfo removalProbe = removalSeam;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[]? args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            bool AllNull() => probe.GetValue(null) is null && removalProbe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-typology seams (PostWriteRemovalProbe, PostWriteProbe, UnguardedWorkProbe, GuardedWorkProbe) are null unless a test sets them");
            (Exception? Thrown, string Out, string Err, int Exit) WithSeams(Action? stage, Action<string>? removal, string[] args) => Quiet(() =>
            {
                probe.SetValue(null, stage);
                removalProbe.SetValue(null, removal);
                try { return Invoke(args); }
                finally { probe.SetValue(null, null); removalProbe.SetValue(null, null); }
            });

            var failure = new InvalidOperationException("boom generate-typology P3-26 original post-write");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == "generate-typology internal error: System.InvalidOperationException: boom generate-typology P3-26 original post-write",
                "fixture: IntentPlan.Internal(\"generate-typology\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());

            // The cleanup delete forced to fail: the file stays, stderr names it once, the original exception is the one reported.
            void ExpectLeft(string label, string requestPath, string pin, Exception removal)
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
                }, new[] { "generate-typology", requestPath, output });
                Check(AllNull(), label + ": both seams are reset to null after the forced run");
                Check(stageCalls == 1 && deletes.Count == 1 && deletes[0] == output && seenAtDelete is { Length: > 0 } && Hex(seenAtDelete) == pin,
                    label + ": the removal probe ran once, with the output path as given, after the file was written (pinned bytes " + pin[..8] + "...)",
                    "stage " + stageCalls.ToString(CultureInfo.InvariantCulture) + " deletes [" + string.Join(", ", deletes) + "] seen " + (seenAtDelete is null ? "<no file>" : Hex(seenAtDelete)));
                Check(forced.Thrown is null, label + ": neither the original nor the delete exception escapes Run to Program's generic catch", Q(forced));
                Check(forced.Out == envelope.Line + nl, label + ": stdout is still only the typed generate_typology_internal_error line", Q(forced));
                Check(forced.Err == Left + output + nl + envelope.Diagnostic + nl,
                    label + ": stderr is the documented cleanup line naming the output path, then the ORIGINAL exception's diagnostic exactly once", Q(forced));
                Check(!forced.Err.Contains(removal.Message, StringComparison.Ordinal) && !forced.Err.Contains(removal.GetType().FullName!, StringComparison.Ordinal)
                    && !forced.Out.Contains(removal.Message, StringComparison.Ordinal),
                    label + ": the delete exception never replaces the original (its type and message appear nowhere)", Q(forced));
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(forced.Exit == 1 && left is { Length: > 0 } && Hex(left) == pin,
                    label + ": exit 1 and the output file the stderr line names is left behind with the pinned bytes",
                    Q(forced) + (left is null ? " <no file>" : " left " + Hex(left)));
                if (File.Exists(output)) File.Delete(output);
            }
            ExpectLeft("IOException on delete", request, PinnedAcceptedSha256, new IOException("p3-26 delete io failure"));
            ExpectLeft("UnauthorizedAccessException on delete", request, PinnedAcceptedSha256, new UnauthorizedAccessException("p3-26 delete access failure"));
            ExpectLeft("non-IO ArgumentException on delete", request, PinnedAcceptedSha256, new ArgumentException("p3-26 delete non-io failure"));
            ExpectLeft("non-IO NotSupportedException on delete, composed run (skills [])", composedRequest, PinnedComposedSha256, new NotSupportedException("p3-26 delete not supported"));

            // The cleanup line itself fails (stderr throws IOException on it): the original exception is still the one reported.
            {
                var output = Output();
                var deletes = 0;
                using var recorder = new RemovalReportRejectingWriter(Left);
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
                    removalProbe.SetValue(null, (Action<string>)(_ => { deletes++; throw new IOException("p3-26 delete io failure"); }));
                    try { thrown = Invoke(new[] { "generate-typology", request, output }); }
                    finally { probe.SetValue(null, null); removalProbe.SetValue(null, null); }
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

            // A removal probe that does not throw: section 10's bytes (file deleted, stderr only the diagnostic).
            foreach (var (label, requestPath) in new[] { ("plain", request), ("composed", composedRequest) })
            {
                var output = Output();
                var deletes = new List<string>();
                var quiet = WithSeams(() => throw failure, path => deletes.Add(path), new[] { "generate-typology", requestPath, output });
                Check(quiet.Thrown is null && deletes.Count == 1 && deletes[0] == output && quiet.Out == envelope.Line + nl && quiet.Err == envelope.Diagnostic + nl && quiet.Exit == 1
                    && !File.Exists(output) && !Directory.Exists(output),
                    "a " + label + " run with a removal probe that does not throw keeps section 10's bytes: typed line, diagnostic once, no cleanup line, exit 1, file deleted",
                    Q(quiet) + " deletes " + deletes.Count.ToString(CultureInfo.InvariantCulture) + (File.Exists(output) ? " <file left>" : ""));
            }
            Check(AllNull(), "the seams are null after the non-throwing removal probe runs");

            // Success with the removal probe set: it is never called; the file and stdout are the real process's.
            foreach (var (label, requestPath, pin) in new[] { ("plain", request, PinnedAcceptedSha256), ("composed", composedRequest, PinnedComposedSha256) })
            {
                var inOut = Output();
                var removalCalls = 0;
                var inProcess = WithSeams(null, _ => { removalCalls++; throw new IOException("p3-26 never"); }, new[] { "generate-typology", requestPath, inOut });
                var processOut = Output();
                var process = cli(new[] { "generate-typology", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(inProcess.Thrown is null && removalCalls == 0 && inProcess.Exit == 0 && inProcess.Err.Length == 0 && Hex(inBytes) == pin
                    && inBytes.SequenceEqual(processBytes) && process.Exit == 0 && AsProcess(inProcess) == process && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal),
                    "with the removal probe set, the " + label + " success never calls it and writes the pinned file (" + pin[..8] + "...) and the real process's stdout",
                    Q(inProcess) + " removals " + removalCalls.ToString(CultureInfo.InvariantCulture) + " vs process " + process.Out + " " + process.Err);
            }

            // Refusals (every one before the write) with both seams set call neither and print the real process's bytes.
            var existing = Output();
            var sentinel = new UTF8Encoding(false).GetBytes("{\"sentinel\":\"p3-26\"}");
            File.WriteAllBytes(existing, sentinel);
            var stamp = new DateTime(2003, 4, 5, 6, 7, 8, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(existing, stamp);
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-typology", request }),
                ("output extension", output => new[] { "generate-typology", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-typology", request, existing }),
                ("missing request", output => new[] { "generate-typology", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-typology", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-typology", Input("pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), output }),
                ("pipeline refusal", output => new[] { "generate-typology", Input("refused.json", good.Replace("\"family_size\":3", "\"family_size\":0", StringComparison.Ordinal)), output }),
                ("composed resolution refusal", output => new[] { "generate-typology", Input("conflict.json", good[..^1] + ",\"skills\":[\"" + CompactStyle + "\",\"" + MasonryStyle + "\"]}"), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var a = Output();
                var b = Output();
                var calls = 0;
                var mine = WithSeams(() => { calls++; throw failure; }, _ => { calls++; throw new IOException("p3-26 never"); }, argsOf(a));
                var theirs = cli(argsOf(b));
                Check(mine.Thrown is null && calls == 0 && mine.Exit == 1 && theirs.Exit == 1 && AsProcess(mine) == theirs && mine.Err.Length == 0
                    && !File.Exists(a) && !File.Exists(b) && !File.Exists(a + ".txt") && !File.Exists(b + ".txt")
                    && mine.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !mine.Out.Contains("internal_error", StringComparison.Ordinal),
                    "with both seams set, the " + label + " refusal calls neither and equals the real process bytes", Q(mine) + " calls " + calls.ToString(CultureInfo.InvariantCulture) + " vs process " + theirs.Out + " " + theirs.Err);
            }
            Check(AllNull() && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
                "the seams are null after the refusal sample and the existing output is byte- and mtime-identical");
        }
        finally
        {
            try { Directory.Delete(inputs, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // P3-26: a stderr that throws IOException on a line starting with the cleanup prefix (counted in Rejected) and records
    // every other line; characters written outside WriteLine(string) collect in Partial.
    sealed class RemovalReportRejectingWriter(string prefix) : TextWriter
    {
        readonly StringBuilder partial = new();
        public List<string> Lines { get; } = new();
        public int Rejected { get; private set; }
        public string Partial => partial.ToString();
        public override Encoding Encoding => new UTF8Encoding(false);
        public override void Write(char value) => partial.Append(value);
        public override void WriteLine(string? value)
        {
            if (value is not null && value.StartsWith(prefix, StringComparison.Ordinal)) { Rejected++; throw new IOException("p3-26 stderr rejects the cleanup line"); }
            Lines.Add(value ?? "");
        }
    }

    // P3-26: over (relative path, source) pairs under interfaces/, comments stripped and string literals blanked:
    // PostWriteRemovalProbe is named exactly twice in total, both in interfaces/cli/GenerateTypologyWrite.cs: its
    // Action<string>? declaration and one PostWriteRemovalProbe?.Invoke(output); no assignment anywhere; PartialOutputLeft
    // declared once there as an internal const string. Brace-matched inside Execute's body: the invoke's innermost try
    // block is exactly { PostWriteRemovalProbe?.Invoke(output); File.Delete(output); }, is the first statement of a
    // catch (Exception) { that directly follows a try block holding PostWriteProbe?.Invoke(), is directly followed by an
    // unfiltered catch (Exception) whose body is exactly { try { Console.Error.WriteLine(PartialOutputLeft + output); } catch (Exception) { } }
    // (so neither the delete nor a throwing stderr replaces the original), and that is directly followed by throw; and the
    // handler's closing brace.
    static List<string> PostWriteRemovalSeamProblems(IEnumerable<(string Name, string Source)> files)
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
            total += System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteRemovalProbe\b").Count;
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bPostWriteRemovalProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": PostWriteRemovalProbe assigned");
            if (name != "interfaces/cli/GenerateTypologyWrite.cs") continue;
            sawCommand = true;
            var declarations = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+static\s+Action\s*<\s*string\s*>\s*\?\s*PostWriteRemovalProbe\s*\{\s*get\s*;\s*set\s*;\s*\}").Count;
            if (declarations != 1) problems.Add(name + ": the Action<string>? declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var constants = System.Text.RegularExpressions.Regex.Matches(code, @"\binternal\s+const\s+string\s+PartialOutputLeft\s*=").Count;
            if (constants != 1) problems.Add(name + ": the PartialOutputLeft constant found " + constants.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = System.Text.RegularExpressions.Regex.Matches(code, @"\bPostWriteRemovalProbe\s*\?\s*\.\s*Invoke\s*\(\s*output\s*\)\s*;");
            var execute = System.Text.RegularExpressions.Regex.Match(code, @"\bstatic\s+void\s+Execute\s*\([^)]*\)\s*\{");
            if (invokes.Count != 1 || !execute.Success) { problems.Add(name + ": " + invokes.Count.ToString(CultureInfo.InvariantCulture) + " PostWriteRemovalProbe?.Invoke(output) calls, Execute " + (execute.Success ? "found" : "missing")); continue; }
            var open = execute.Index + execute.Length - 1;
            var close = Close(code, open);
            if (close < 0) { problems.Add(name + ": Execute's body is not closed"); continue; }
            var at = invokes[0].Index;
            if (at < open || at > close) { problems.Add(name + ": PostWriteRemovalProbe?.Invoke(output) not inside Execute's body"); continue; }
            // The innermost try { inside Execute whose block holds the invoke.
            int tryStart = -1, tryOpen = -1, tryClose = -1;
            foreach (System.Text.RegularExpressions.Match candidate in new System.Text.RegularExpressions.Regex(@"\btry\s*\{").Matches(code[..at], open))
            {
                var brace = candidate.Index + candidate.Length - 1;
                var end = Close(code, brace);
                if (end > at && end < close) { tryStart = candidate.Index; tryOpen = brace; tryClose = end; }
            }
            if (tryOpen < 0) { problems.Add(name + ": PostWriteRemovalProbe?.Invoke(output) not inside a try { in Execute"); continue; }
            if (!System.Text.RegularExpressions.Regex.IsMatch(code[tryOpen..(tryClose + 1)], @"^\{\s*PostWriteRemovalProbe\s*\?\s*\.\s*Invoke\s*\(\s*output\s*\)\s*;\s*File\s*\.\s*Delete\s*\(\s*output\s*\)\s*;\s*\}$"))
            { problems.Add(name + ": the invoke's try block is not exactly { PostWriteRemovalProbe?.Invoke(output); File.Delete(output); }"); continue; }
            // The enclosing handler: catch (Exception) { directly before the try, following a try block holding PostWriteProbe?.Invoke().
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
                if (Close(code, brace) == stageClose && System.Text.RegularExpressions.Regex.IsMatch(code[brace..stageClose], @"\bPostWriteProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)")) stageTry = true;
            }
            if (!stageTry) problems.Add(name + ": the delete try's catch (Exception) does not follow the try holding PostWriteProbe?.Invoke()");
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
        if (!sawCommand) problems.Add("interfaces/cli/GenerateTypologyWrite.cs not found");
        if (total != 2) problems.Add("PostWriteRemovalProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // ---- 12. P3-28: a stderr that throws while Run's catch-all writes the internal-error diagnostic is contained ----
    // GenerateTypologyCommand.Run of the real Release CLI assembly (reflection, so a missing seam is an assertion failure),
    // in process, with stdout and stderr replaced by recording writers that append each WriteLine to one shared event list
    // (so the stdout/stderr order is observable) and restored, with the exit code and every seam, in a finally (the Console
    // is checked to be the original writers again). A non-refusal exception reaches Run's P3-24 catch-all through the P3-23
    // seam UnguardedWorkProbe (before any write), the P3-25 seam PostWriteProbe (after the write, plain and composed) or
    // that seam plus the P3-26 removal seam PostWriteRemovalProbe (the cleanup delete fails). When stderr throws on the
    // IntentPlan diagnostic line (an IOException, a non-IO InvalidOperationException on every write, or a disposed
    // StringWriter's ObjectDisposedException): nothing escapes Run, stdout is still exactly the typed
    // {"status":"failed","code":"generate_typology_internal_error"} line, exit 1, the diagnostic was attempted exactly once
    // with the ORIGINAL exception's text, and the stderr exception's message appears nowhere; the P3-25 cleanup is unchanged
    // (the just-written file is deleted) and so is P3-26's (the PartialOutputLeft line is recorded before the rejected
    // diagnostic and the file is left behind with the pinned bytes). With a stderr that accepts everything the bytes and
    // order are sections 10/11's (cleanup line when the delete fails, the diagnostic, then the typed line). Success and
    // sampled refusals with a stderr that throws on every write never write stderr and equal the real process. Static: Run's
    // catch-all shape is the section-9 pin (P3-24, updated by P3-28); the delta over the P4-26 Run pin is in
    // tests/mcp/GenerateTypologyWriteLinkTests.cs. A stdout that throws in the catch-all is not covered here (follow-up).
    static void DiagnosticWriteRun(string scratch, string cliDll, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "P3-28 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string Left = "generate-typology: could not delete partial output: ";
        const string DiagnosticPrefix = "generate-typology internal error: ";
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var inputs = Path.Combine(scratch, "p3-28-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            var good = "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}";
            var request = Input("request.json", good);
            var composedRequest = Input("composed.json", good[..^1] + ",\"skills\":[]}");

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateTypologyCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var postWrite = type?.GetProperty("PostWriteProbe", Any);
            var removalSeam = type?.GetProperty("PostWriteRemovalProbe", Any);
            var unguarded = type?.GetProperty("UnguardedWorkProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && Seam(postWrite, typeof(Action)) && Seam(removalSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]) and the seams UnguardedWorkProbe / PostWriteProbe / PostWriteRemovalProbe / GuardedWorkProbe found in the Release CLI");
            if (run is null || postWrite is null || removalSeam is null || unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.PropertyInfo stageProbe = postWrite;
            System.Reflection.PropertyInfo removalProbe = removalSeam;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            Exception? Invoke(string[] args)
            {
                try { runMethod.Invoke(null, new object?[] { args }); return null; }
                catch (System.Reflection.TargetInvocationException error) { return error.InnerException; }
            }
            bool AllNull() => stageProbe.GetValue(null) is null && removalProbe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-typology seams are null unless a test sets them");

            // One in-process Run with stdout / stderr replaced and the seams set; Console, exit code and seams restored in finally.
            (Exception? Thrown, List<string> Events, string Out, int Exit, DiagnosticEventWriter? Err, bool Restored) Forced(Func<List<string>, TextWriter> stderrOf, Action? before, Action? stage, Action<string>? removal, string[] args)
            {
                var events = new List<string>();
                var stdout = new DiagnosticEventWriter("out", events);
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
                    removalProbe.SetValue(null, removal);
                    try { thrown = Invoke(args); }
                    finally { unguardedProbe.SetValue(null, null); stageProbe.SetValue(null, null); removalProbe.SetValue(null, null); }
                    exit = Environment.ExitCode;
                }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                    Environment.ExitCode = previousExit;
                }
                var restored = ReferenceEquals(Console.Out, previousOut) && ReferenceEquals(Console.Error, previousError) && Environment.ExitCode == previousExit;
                return (thrown, events, stdout.Text, exit, stderr as DiagnosticEventWriter, restored);
            }
            static string D((Exception? Thrown, List<string> Events, string Out, int Exit, DiagnosticEventWriter? Err, bool Restored) r) =>
                "events [" + string.Join(" | ", r.Events) + "] out [" + r.Out + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + (r.Err is { } e ? " rejected [" + string.Join(" | ", e.RejectedLines) + "] partial [" + e.Partial + "]" : "") + " restored " + r.Restored
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : "");

            var failure = new InvalidOperationException("boom generate-typology P3-28 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == DiagnosticPrefix + "System.InvalidOperationException: boom generate-typology P3-28 original",
                "fixture: IntentPlan.Internal(\"generate-typology\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            var typed = envelope.Line + Environment.NewLine;
            const string StderrMessage = "p3-28 stderr rejects the write";
            Func<List<string>, TextWriter> RejectDiagnostic = events => new DiagnosticEventWriter("err", events, DiagnosticPrefix, rejectAll: false, () => new IOException(StderrMessage));
            Func<List<string>, TextWriter> RejectAll = events => new DiagnosticEventWriter("err", events, null, rejectAll: true, () => new InvalidOperationException(StderrMessage));
            Func<List<string>, TextWriter> Disposed = _ => { var closed = new StringWriter(CultureInfo.InvariantCulture); closed.Dispose(); return closed; };
            bool Contained((Exception? Thrown, List<string> Events, string Out, int Exit, DiagnosticEventWriter? Err, bool Restored) r) =>
                r.Thrown is null && r.Restored && r.Exit == 1 && r.Out == typed && !r.Out.Contains(StderrMessage, StringComparison.Ordinal)
                && !string.Join("\n", r.Events).Contains(StderrMessage, StringComparison.Ordinal) && AllNull();

            // Controls: a stderr that accepts everything keeps sections 10/11's bytes and order.
            {
                var output = Output();
                var r = Forced(events => new DiagnosticEventWriter("err", events), null, () => throw failure, null, new[] { "generate-typology", request, output });
                Check(r.Thrown is null && r.Restored && r.Exit == 1 && r.Out == typed && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic, "out:" + envelope.Line })
                    && !File.Exists(output) && AllNull(),
                    "control: an accepting stderr records the diagnostic, then the typed line, exit 1, file deleted (section 10's bytes)", D(r));
            }
            {
                var output = Output();
                var r = Forced(events => new DiagnosticEventWriter("err", events), null, () => throw failure, _ => throw new IOException("p3-28 delete io failure"), new[] { "generate-typology", request, output });
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(r.Thrown is null && r.Restored && r.Exit == 1 && r.Out == typed
                    && r.Events.SequenceEqual(new[] { "err:" + Left + output, "err:" + envelope.Diagnostic, "out:" + envelope.Line })
                    && left is { Length: > 0 } && Hex(left) == PinnedAcceptedSha256 && AllNull(),
                    "control: an accepting stderr with a failing delete records the cleanup line, the diagnostic, then the typed line, exit 1, file left (section 11's bytes)",
                    D(r) + (left is null ? " <no file>" : " left " + Hex(left)));
                if (File.Exists(output)) File.Delete(output);
            }

            // Before any write (P3-24 path): the diagnostic is rejected, the typed line still printed, no file.
            {
                var output = Output();
                var r = Forced(RejectDiagnostic, () => throw failure, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 1 } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic })
                    && !File.Exists(output) && !Directory.Exists(output),
                    "UnguardedWorkProbe + stderr IOException on the diagnostic: nothing escapes Run, stdout is only the typed line, exit 1, the original diagnostic attempted once, no file", D(r));
            }

            // After the write (P3-25 path, plain and composed): the file is still deleted.
            foreach (var (label, requestPath, pin) in new[] { ("plain", request, PinnedAcceptedSha256), ("composed (skills [])", composedRequest, PinnedComposedSha256) })
            {
                var output = Output();
                byte[]? seen = null;
                var r = Forced(RejectDiagnostic, null, () => { seen = File.Exists(output) ? File.ReadAllBytes(output) : null; throw failure; }, null, new[] { "generate-typology", requestPath, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 1 } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic })
                    && seen is { Length: > 0 } && Hex(seen) == pin && !File.Exists(output),
                    "PostWriteProbe (" + label + ") + stderr IOException on the diagnostic: typed line, exit 1, nothing escapes, and the just-written file is still deleted (P3-25)",
                    D(r) + " seen " + (seen is null ? "<no file>" : Hex(seen)) + (File.Exists(output) ? " <file left>" : ""));
            }

            // Cleanup delete failing too (P3-26 path): the cleanup line is recorded, then the rejected diagnostic; the file is left.
            {
                var output = Output();
                var deletes = 0;
                var r = Forced(RejectDiagnostic, null, () => throw failure, _ => { deletes++; throw new IOException("p3-28 delete io failure"); }, new[] { "generate-typology", request, output });
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(Contained(r) && deletes == 1 && r.Events.SequenceEqual(new[] { "err:" + Left + output, "out:" + envelope.Line })
                    && r.Err is { Rejected: 1 } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && left is { Length: > 0 } && Hex(left) == PinnedAcceptedSha256,
                    "PostWriteProbe + failing delete + stderr IOException on the diagnostic: the P3-26 line, then the typed line, exit 1, file left with the pinned bytes", D(r) + (left is null ? " <no file>" : " left " + Hex(left)));
                if (File.Exists(output)) File.Delete(output);
            }

            // A stderr that throws a non-IO exception on every write (the P3-26 line and the diagnostic both rejected).
            {
                var output = Output();
                var r = Forced(RejectAll, null, () => throw failure, _ => throw new NotSupportedException("p3-28 delete not supported"), new[] { "generate-typology", request, output });
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 2 } e
                    && e.RejectedLines.SequenceEqual(new[] { Left + output, envelope.Diagnostic }) && e.Partial.Length == 0 && left is { Length: > 0 } && Hex(left) == PinnedAcceptedSha256,
                    "a stderr throwing InvalidOperationException on every write: the cleanup line and the diagnostic are both dropped, stdout is only the typed line, exit 1, file left", D(r));
                if (File.Exists(output)) File.Delete(output);
            }
            {
                var output = Output();
                var r = Forced(RejectAll, () => throw failure, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Err is { Rejected: 1 } && !File.Exists(output),
                    "UnguardedWorkProbe + a stderr throwing on every write: typed line, exit 1, nothing escapes, no file", D(r));
            }

            // A real closed stderr (a disposed StringWriter throws ObjectDisposedException).
            {
                var output = Output();
                var r = Forced(Disposed, null, () => throw failure, null, new[] { "generate-typology", request, output });
                Check(r.Thrown is null && r.Restored && r.Exit == 1 && r.Out == typed && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && !File.Exists(output) && AllNull(),
                    "PostWriteProbe + a disposed stderr (ObjectDisposedException): typed line, exit 1, nothing escapes, file deleted", D(r));
            }

            // Success with a stderr that throws on every write: stderr is never written; file and stdout are the real process's.
            foreach (var (label, requestPath, pin) in new[] { ("plain", request, PinnedAcceptedSha256), ("composed (skills [])", composedRequest, PinnedComposedSha256) })
            {
                var inOut = Output();
                var r = Forced(RejectAll, null, null, null, new[] { "generate-typology", requestPath, inOut });
                var processOut = Output();
                var process = cli(new[] { "generate-typology", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && r.Err is { Rejected: 0 } && r.Out == process.Out + Environment.NewLine && process.Exit == 0 && process.Err.Length == 0
                    && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && Hex(inBytes) == pin && inBytes.SequenceEqual(processBytes),
                    "with a rejecting stderr, the " + label + " success writes no stderr and equals the real process's file and stdout", D(r) + " vs process " + process.Out + " " + process.Err);
            }

            // Refusals (all typed, stdout only) with a stderr that throws on every write equal the real process bytes.
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-typology", request }),
                ("output extension", output => new[] { "generate-typology", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-typology", request, existing }),
                ("missing request", output => new[] { "generate-typology", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-typology", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-typology", Input("pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), output }),
                ("pipeline refusal", output => new[] { "generate-typology", Input("refused.json", good.Replace("\"family_size\":3", "\"family_size\":0", StringComparison.Ordinal)), output }),
                ("composed resolution refusal", output => new[] { "generate-typology", Input("conflict.json", good[..^1] + ",\"skills\":[\"" + CompactStyle + "\",\"" + MasonryStyle + "\"]}"), output }),
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

    // ---- 13. P3-30: a stdout that throws in Run's catch-all or catch (Refusal), and a stderr that throws in InternalRefusal, are contained ----
    // GenerateTypologyCommand.Run (and the shared Document path the MCP generate_typology tool runs) of the real Release CLI
    // assembly (reflection), in process, with stdout and stderr replaced by section 12's recording writers (either may
    // reject) and restored, with the exit code and every seam, in a finally. As P7-29 / P7-30 for generate-prototypes:
    //   a. Catch-all: a non-refusal exception through UnguardedWorkProbe (before any write), PostWriteProbe (after the write,
    //      plain and composed; P3-25 deletes the file) or that seam plus PostWriteRemovalProbe (P3-26: the file is left and
    //      named on stderr), with stdout throwing on the typed line (IOException, non-IO InvalidOperationException, a disposed
    //      StringWriter's ObjectDisposedException): nothing escapes Run, exit 1, the typed line attempted exactly once, stderr
    //      the section 10-12 bytes, the stdout exception's message nowhere. A stdout rejecting the success line is an internal
    //      error about the stdout exception, the just-written file deleted.
    //   b. catch (Refusal): every typed refusal Fail writes (usage, output extension, output exists, missing request,
    //      malformed JSON, unknown pack, unknown extra skill (with its skill member), the guarded block's Refusal(InternalError))
    //      to a stdout that throws: nothing escapes Run, exit 1, the real process's stdout line attempted once, stderr
    //      unchanged. A pipeline refusal (Execute's RefusedLine, not a Refusal) with a throwing stdout is reported one way,
    //      pinned: Run's catch-all, stderr the stdout exception's diagnostic once, the refused then the typed internal-error
    //      line attempted once each, exit 1.
    //   c. InternalRefusal: a GuardedWorkProbe failure with a stderr that throws on its diagnostic writes the diagnostic
    //      attempt once (the original exception's) and still returns the typed Refusal(InternalError): stdout the typed line
    //      once, exit 1, no second internal error (no diagnostic about the stderr exception); with stdout throwing too, exit
    //      1 and nothing escapes. Static: InternalRefusal is the guarded write then `return new Refusal(InternalError);`,
    //      called only from the guarded block's catch.
    //   d. Document (MCP path): the guarded block's failure with a stderr rejecting its diagnostic returns the typed
    //      (false, internal-error line), diagnostic attempted once, nothing escapes, stdout untouched. Document's own
    //      catch-all (UnguardedWorkProbe) with the same stderr: likewise contained since P3-31 (was a pinned gap; section 14).
    // Controls: accepting writers give section 10/12's order (diagnostic, then the typed line) and the real process's
    // success and refusal bytes. Static: Run's shape is the section-9 pin (updated by P3-30); the delta over the P4-26 Run
    // pin is in tests/mcp/GenerateTypologyWriteLinkTests.cs (RunDeltaP330).
    static void OutputWriteRun(string root, string scratch, string cliDll, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "P3-30 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string Left = "generate-typology: could not delete partial output: ";
        const string DiagnosticPrefix = "generate-typology internal error: ";
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var inputs = Path.Combine(scratch, "p3-30-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            var good = "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}";
            var request = Input("request.json", good);
            var composedRequest = Input("composed.json", good[..^1] + ",\"skills\":[]}");
            var refusedRequest = Input("refused.json", good.Replace("\"family_size\":3", "\"family_size\":0", StringComparison.Ordinal));

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateTypologyCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var document = type?.GetMethod("Document", Any, new[] { typeof(string) });
            var postWrite = type?.GetProperty("PostWriteProbe", Any);
            var removalSeam = type?.GetProperty("PostWriteRemovalProbe", Any);
            var unguarded = type?.GetProperty("UnguardedWorkProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            static bool Seam(System.Reflection.PropertyInfo? p, Type t) => p is not null && p.PropertyType == t && p.CanWrite && p.CanRead;
            Check(run is not null && document is not null && document.ReturnType == typeof((bool, string))
                && Seam(postWrite, typeof(Action)) && Seam(removalSeam, typeof(Action<string>)) && Seam(unguarded, typeof(Action)) && Seam(guarded, typeof(Action)),
                "Run(string[]), Document(string) and the seams UnguardedWorkProbe / GuardedWorkProbe / PostWriteProbe / PostWriteRemovalProbe found in the Release CLI",
                (run is null ? "<missing Run> " : "") + (document?.ReturnType.FullName ?? "<missing Document>"));
            if (run is null || document is null || document.ReturnType != typeof((bool, string)) || postWrite is null || removalSeam is null || unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.MethodInfo documentMethod = document;
            System.Reflection.PropertyInfo stageProbe = postWrite;
            System.Reflection.PropertyInfo removalProbe = removalSeam;
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            bool AllNull() => stageProbe.GetValue(null) is null && removalProbe.GetValue(null) is null && unguardedProbe.GetValue(null) is null && guardedProbe.GetValue(null) is null;
            Check(AllNull(), "the generate-typology seams are null unless a test sets them");

            // One in-process call (Run, or Document when documentPath is set) with stdout / stderr replaced and the seams set;
            // Console, exit code and seams restored in finally.
            (Exception? Thrown, List<string> Events, int Exit, DiagnosticEventWriter? Out, DiagnosticEventWriter? Err, bool Restored, (bool, string)? Document) Forced(
                Func<List<string>, TextWriter> stdoutOf, Func<List<string>, TextWriter> stderrOf, Action? before, Action? work, Action? stage, Action<string>? removal, string[]? args, string? documentPath = null)
            {
                var events = new List<string>();
                var stdout = stdoutOf(events);
                var stderr = stderrOf(events);
                var previousOut = Console.Out;
                var previousError = Console.Error;
                var previousExit = Environment.ExitCode;
                Exception? thrown = null;
                (bool, string)? returned = null;
                int exit;
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    Environment.ExitCode = 0;
                    unguardedProbe.SetValue(null, before);
                    guardedProbe.SetValue(null, work);
                    stageProbe.SetValue(null, stage);
                    removalProbe.SetValue(null, removal);
                    try
                    {
                        if (documentPath is not null) returned = ((bool, string))documentMethod.Invoke(null, new object?[] { documentPath })!;
                        else runMethod.Invoke(null, new object?[] { args });
                    }
                    catch (System.Reflection.TargetInvocationException error) { thrown = error.InnerException; }
                    finally { unguardedProbe.SetValue(null, null); guardedProbe.SetValue(null, null); stageProbe.SetValue(null, null); removalProbe.SetValue(null, null); }
                    exit = Environment.ExitCode;
                }
                finally
                {
                    Console.SetOut(previousOut);
                    Console.SetError(previousError);
                    Environment.ExitCode = previousExit;
                }
                var restored = ReferenceEquals(Console.Out, previousOut) && ReferenceEquals(Console.Error, previousError) && Environment.ExitCode == previousExit;
                return (thrown, events, exit, stdout as DiagnosticEventWriter, stderr as DiagnosticEventWriter, restored, returned);
            }
            static string D((Exception? Thrown, List<string> Events, int Exit, DiagnosticEventWriter? Out, DiagnosticEventWriter? Err, bool Restored, (bool, string)? Document) r) =>
                "events [" + string.Join(" | ", r.Events) + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + (r.Out is { } o ? " stdout text [" + o.Text + "] stdout rejected [" + string.Join(" | ", o.RejectedLines) + "]" : "")
                + (r.Err is { } e ? " stderr text [" + e.Text + "] stderr rejected [" + string.Join(" | ", e.RejectedLines) + "]" : "") + " restored " + r.Restored
                + (r.Document is { } d ? " document (" + d.Item1 + ", " + d.Item2 + ")" : "")
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : " threw nothing");

            var failure = new InvalidOperationException("boom generate-typology P3-30 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == DiagnosticPrefix + "System.InvalidOperationException: boom generate-typology P3-30 original",
                "fixture: IntentPlan.Internal(\"generate-typology\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            const string StdoutMessage = "p3-30 stdout rejects the write";
            const string StderrMessage = "p3-30 stderr rejects the write";
            Func<List<string>, TextWriter> AcceptOut = events => new DiagnosticEventWriter("out", events);
            Func<List<string>, TextWriter> AcceptErr = events => new DiagnosticEventWriter("err", events);
            Func<List<string>, TextWriter> RejectOut = events => new DiagnosticEventWriter("out", events, null, rejectAll: true, () => new IOException(StdoutMessage));
            Func<List<string>, TextWriter> RejectOutNonIo = events => new DiagnosticEventWriter("out", events, null, rejectAll: true, () => new InvalidOperationException(StdoutMessage));
            Func<List<string>, TextWriter> RejectErrAll = events => new DiagnosticEventWriter("err", events, null, rejectAll: true, () => new InvalidOperationException(StderrMessage));
            Func<List<string>, TextWriter> RejectErrDiagnostic = events => new DiagnosticEventWriter("err", events, DiagnosticPrefix, rejectAll: false, () => new IOException(StderrMessage));
            Func<List<string>, TextWriter> Disposed = _ => { var closed = new StringWriter(CultureInfo.InvariantCulture); closed.Dispose(); return closed; };
            bool NoMessage((Exception? Thrown, List<string> Events, int Exit, DiagnosticEventWriter? Out, DiagnosticEventWriter? Err, bool Restored, (bool, string)? Document) r, string message) =>
                !string.Join("\n", r.Events).Contains(message, StringComparison.Ordinal) && (r.Out is null || !r.Out.Text.Contains(message, StringComparison.Ordinal))
                && (r.Err is null || !string.Join("\n", r.Err.RejectedLines).Contains(message, StringComparison.Ordinal));
            bool Contained((Exception? Thrown, List<string> Events, int Exit, DiagnosticEventWriter? Out, DiagnosticEventWriter? Err, bool Restored, (bool, string)? Document) r) =>
                r.Thrown is null && r.Restored && r.Exit == 1 && AllNull();
            bool RejectedOnce((Exception? Thrown, List<string> Events, int Exit, DiagnosticEventWriter? Out, DiagnosticEventWriter? Err, bool Restored, (bool, string)? Document) r, string line) =>
                r.Out is { } o && o.RejectedLines.SequenceEqual(new[] { line }) && o.Text.Length == 0 && o.Partial.Length == 0;

            // ---- a. Run's catch-all with a throwing stdout ----
            {
                var output = Output();
                var r = Forced(AcceptOut, AcceptErr, null, null, () => throw failure, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic, "out:" + envelope.Line }) && r.Out is { } o && o.Text == envelope.Line + Environment.NewLine && !File.Exists(output),
                    "control: accepting writers record the diagnostic, then the typed line, exit 1, file deleted (section 10's bytes)", D(r));
            }
            foreach (var (label, stdoutOf) in new[] { ("IOException", RejectOut), ("InvalidOperationException", RejectOutNonIo) })
            {
                var output = Output();
                var r = Forced(stdoutOf, AcceptErr, () => throw failure, null, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && RejectedOnce(r, envelope.Line) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage) && !File.Exists(output) && !Directory.Exists(output),
                    "UnguardedWorkProbe + stdout " + label + " on the typed line: nothing escapes Run, exit 1, typed line attempted once, stderr the original diagnostic once, no file", D(r));
            }
            foreach (var (label, requestPath, pin) in new[] { ("plain", request, PinnedAcceptedSha256), ("composed (skills [])", composedRequest, PinnedComposedSha256) })
            {
                var output = Output();
                byte[]? seen = null;
                var r = Forced(RejectOut, AcceptErr, null, null, () => { seen = File.Exists(output) ? File.ReadAllBytes(output) : null; throw failure; }, null, new[] { "generate-typology", requestPath, output });
                Check(Contained(r) && RejectedOnce(r, envelope.Line) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage)
                    && seen is { Length: > 0 } && Hex(seen) == pin && !File.Exists(output),
                    "PostWriteProbe (" + label + ") + stdout IOException on the typed line: exit 1, nothing escapes, diagnostic once, the just-written file still deleted (P3-25)",
                    D(r) + " seen " + (seen is null ? "<no file>" : Hex(seen)) + (File.Exists(output) ? " <file left>" : ""));
            }
            {
                var output = Output();
                var deletes = 0;
                var r = Forced(RejectOut, AcceptErr, null, null, () => throw failure, _ => { deletes++; throw new IOException("p3-30 delete io failure"); }, new[] { "generate-typology", request, output });
                var left = File.Exists(output) ? File.ReadAllBytes(output) : null;
                Check(Contained(r) && RejectedOnce(r, envelope.Line) && deletes == 1 && r.Events.SequenceEqual(new[] { "err:" + Left + output, "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage)
                    && left is { Length: > 0 } && Hex(left) == PinnedAcceptedSha256,
                    "PostWriteProbe + failing delete + stdout IOException on the typed line: the P3-26 line, then the diagnostic, exit 1, nothing escapes, file left with the pinned bytes",
                    D(r) + (left is null ? " <no file>" : " left " + Hex(left)));
            }
            {
                var output = Output();
                var r = Forced(RejectOutNonIo, RejectErrAll, () => throw failure, null, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && RejectedOnce(r, envelope.Line) && r.Events.Count == 0 && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && !File.Exists(output),
                    "UnguardedWorkProbe + stdout and stderr throwing InvalidOperationException on every write: exit 1, nothing escapes, each line attempted once, no file", D(r));
            }
            {
                var output = Output();
                var r = Forced(Disposed, AcceptErr, null, null, () => throw failure, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                    "PostWriteProbe + a disposed stdout (ObjectDisposedException): exit 1, nothing escapes, diagnostic once, file deleted", D(r));
            }
            {
                var output = Output();
                var r = Forced(RejectOut, AcceptErr, null, null, null, null, new[] { "generate-typology", request, output });
                var stdoutDiagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-typology", new IOException(StdoutMessage));
                Check(Contained(r) && r.Out is { } o && o.RejectedLines.Count == 2 && o.RejectedLines[0].StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal)
                    && o.RejectedLines[1] == envelope.Line && o.Text.Length == 0 && r.Events.SequenceEqual(new[] { "err:" + stdoutDiagnostic }) && !File.Exists(output),
                    "a stdout rejecting the success line: exit 1, nothing escapes, success then typed line attempted once each, stderr the stdout exception's diagnostic, file deleted (P3-25)",
                    D(r) + (File.Exists(output) ? " <file left>" : ""));
            }

            // ---- b. catch (Refusal) with a throwing stdout ----
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-typology", request }),
                ("output extension", output => new[] { "generate-typology", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-typology", request, existing }),
                ("missing request", output => new[] { "generate-typology", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-typology", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-typology", Input("pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), output }),
                ("unknown extra skill", output => new[] { "generate-typology", Input("skill.json", good[..^1] + ",\"skills\":[\"xx.no-such.skill\"]}"), output }),
            };
            foreach (var (label, argsOf) in refusals)
            {
                var b = Output();
                var theirs = cli(argsOf(b));
                var expectedLine = theirs.Out;
                Check(theirs.Exit == 1 && theirs.Err.Length == 0 && expectedLine.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && !expectedLine.Contains("internal_error", StringComparison.Ordinal)
                    && (label != "unknown extra skill" || expectedLine.Contains("\"skill\":\"xx.no-such.skill\"", StringComparison.Ordinal)),
                    "fixture: the real process prints the typed " + label + " refusal line, exit 1, no stderr", theirs.Exit.ToString(CultureInfo.InvariantCulture) + " " + theirs.Out + " " + theirs.Err);
                {
                    var a = Output();
                    var control = Forced(AcceptOut, RejectErrAll, null, null, null, null, argsOf(a));
                    Check(Contained(control) && control.Err is { Rejected: 0 } && control.Out is { } o && o.Text == expectedLine + Environment.NewLine && control.Events.SequenceEqual(new[] { "out:" + expectedLine })
                        && !File.Exists(a) && !File.Exists(a + ".txt"),
                        "control: with an accepting stdout the " + label + " refusal equals the real process bytes", D(control) + " vs process " + theirs.Out);
                }
                foreach (var (kind, stdoutOf) in new[] { ("IOException", RejectOut), ("InvalidOperationException", RejectOutNonIo) })
                {
                    var a = Output();
                    var r = Forced(stdoutOf, RejectErrAll, null, null, null, null, argsOf(a));
                    Check(Contained(r) && RejectedOnce(r, expectedLine) && r.Err is { Rejected: 0 } && r.Events.Count == 0 && NoMessage(r, StdoutMessage) && !File.Exists(a) && !File.Exists(a + ".txt"),
                        "the " + label + " refusal with stdout throwing " + kind + ": nothing escapes Run, exit 1, the typed line attempted once, stderr unwritten, no file", D(r));
                }
                {
                    var a = Output();
                    var r = Forced(Disposed, RejectErrAll, null, null, null, null, argsOf(a));
                    Check(Contained(r) && r.Err is { Rejected: 0 } && r.Events.Count == 0 && !File.Exists(a) && !File.Exists(a + ".txt"),
                        "the " + label + " refusal with a disposed stdout (ObjectDisposedException): nothing escapes Run, exit 1, stderr unwritten, no file", D(r));
                }
            }
            Check(File.ReadAllText(existing) == "keep", "the existing output is left untouched by the refusal sample");
            // The guarded block's Refusal(InternalError) (accepting stderr) with a throwing stdout.
            {
                var output = Output();
                var r = Forced(RejectOut, AcceptErr, null, () => throw failure, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && RejectedOnce(r, envelope.Line) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && NoMessage(r, StdoutMessage) && !File.Exists(output),
                    "GuardedWorkProbe (Refusal(InternalError)) + stdout IOException: nothing escapes Run, exit 1, the typed line attempted once, the diagnostic once, no file", D(r));
            }
            // A pipeline refusal (RefusedLine) with a throwing stdout: reported through Run's catch-all (pinned).
            {
                var b = Output();
                var theirs = cli(new[] { "generate-typology", refusedRequest, b });
                var output = Output();
                var r = Forced(RejectOut, AcceptErr, null, null, null, null, new[] { "generate-typology", refusedRequest, output });
                var stdoutDiagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-typology", new IOException(StdoutMessage));
                Check(theirs.Exit == 1 && theirs.Out.StartsWith("{\"status\":\"failed\",\"code\":\"generate_typology_refused\"", StringComparison.Ordinal)
                    && Contained(r) && r.Out is { } o && o.RejectedLines.SequenceEqual(new[] { theirs.Out, envelope.Line }) && o.Text.Length == 0
                    && r.Events.SequenceEqual(new[] { "err:" + stdoutDiagnostic }) && !File.Exists(output),
                    "a pipeline refusal with stdout throwing: Run's catch-all, the refused then the typed internal-error line attempted once each, stderr the stdout exception's diagnostic once, exit 1, nothing escapes, no file (pinned)",
                    D(r) + " vs process " + theirs.Out);
            }

            // ---- c. InternalRefusal with a throwing stderr ----
            var stderrDiagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-typology", new IOException(StderrMessage));
            {
                var output = Output();
                var r = Forced(AcceptOut, AcceptErr, null, () => throw failure, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic, "out:" + envelope.Line }) && !File.Exists(output),
                    "control: GuardedWorkProbe with accepting writers is the diagnostic once, then the typed line, exit 1 (P3-20 bytes)", D(r));
            }
            {
                var output = Output();
                var r = Forced(AcceptOut, RejectErrDiagnostic, null, () => throw failure, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && r.Out is { } o && o.Text == envelope.Line + Environment.NewLine
                    && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && NoMessage(r, StderrMessage) && !File.Exists(output),
                    "GuardedWorkProbe + stderr rejecting the guarded block's diagnostic: the original diagnostic attempted once (no second internal error about the stderr exception), the typed line once, exit 1, nothing escapes, no file",
                    D(r) + " (a second diagnostic would be [" + stderrDiagnostic + "])");
            }
            {
                var output = Output();
                var r = Forced(RejectOut, RejectErrDiagnostic, null, () => throw failure, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && RejectedOnce(r, envelope.Line) && r.Events.Count == 0 && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && !File.Exists(output),
                    "GuardedWorkProbe + stderr rejecting the guarded diagnostic + stdout rejecting the typed line: each attempted once, exit 1, nothing escapes, no file", D(r));
            }
            {
                var output = Output();
                var r = Forced(AcceptOut, Disposed, null, () => throw failure, null, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && r.Events.SequenceEqual(new[] { "out:" + envelope.Line }) && !File.Exists(output),
                    "GuardedWorkProbe + a disposed stderr (ObjectDisposedException): the typed line once, exit 1, nothing escapes, no file", D(r));
            }
            // Static: InternalRefusal writes its diagnostic guarded, then returns Refusal(InternalError); called only from the guarded catch.
            var shared = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateTypologyCommand.cs")), blankStrings: false);
            var internalRefusal = System.Text.RegularExpressions.Regex.Matches(shared,
                @"static\s+Refusal\s+InternalRefusal\s*\(\s*Exception\s+error\s*\)\s*\{\s*try\s*\{\s*Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*IntentPlan\s*\.\s*InternalDiagnostic\s*\(\s*""generate-typology""\s*,\s*error\s*\)\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*return\s+new\s+Refusal\s*\(\s*InternalError\s*\)\s*;\s*\}").Count;
            var guardedCatch = System.Text.RegularExpressions.Regex.Matches(shared,
                @"try\s*\{\s*GuardedWorkProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;[^{}]*\}\s*catch\s*\(\s*Exception\s+error\s*\)\s*\{\s*throw\s+InternalRefusal\s*\(\s*error\s*\)\s*;\s*\}").Count;
            var calls = System.Text.RegularExpressions.Regex.Matches(shared, @"\bInternalRefusal\s*\(").Count;
            Check(internalRefusal == 1 && guardedCatch == 1 && calls == 2,
                "GenerateTypologyCommand.cs: InternalRefusal writes IntentPlan.InternalDiagnostic to Console.Error guarded (try / catch (Exception) { }) then returns Refusal(InternalError), and is called only from the guarded block's catch (Exception)",
                "InternalRefusal " + internalRefusal.ToString(CultureInfo.InvariantCulture) + ", guarded catch " + guardedCatch.ToString(CultureInfo.InvariantCulture) + ", name occurrences " + calls.ToString(CultureInfo.InvariantCulture));

            // ---- d. Document (the MCP generate_typology path) ----
            {
                var r = Forced(AcceptOut, AcceptErr, null, () => throw failure, null, null, null, request);
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && AllNull() && r.Document is (false, var returnedLine) && returnedLine == envelope.Line && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }),
                    "control: Document with GuardedWorkProbe and accepting writers returns (false, typed line), the diagnostic once, stdout and exit untouched", D(r));
            }
            {
                var r = Forced(AcceptOut, RejectErrDiagnostic, null, () => throw failure, null, null, null, request);
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && AllNull() && r.Document is (false, var returnedLine) && returnedLine == envelope.Line && r.Events.Count == 0
                    && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && NoMessage(r, StderrMessage),
                    "Document with GuardedWorkProbe + stderr rejecting the diagnostic: nothing escapes, (false, typed internal-error line), the original diagnostic attempted once, stdout and exit untouched", D(r));
            }
            {
                var r = Forced(AcceptOut, RejectErrDiagnostic, () => throw failure, null, null, null, null, request);
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && AllNull() && r.Document is (false, var returnedLine) && returnedLine == envelope.Line && r.Events.Count == 0
                    && r.Err is { } e && e.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && NoMessage(r, StderrMessage),
                    "P3-31 (was the pinned gap): Document's own catch-all (UnguardedWorkProbe) with a stderr rejecting its diagnostic: nothing escapes, (false, typed internal-error line), the diagnostic attempted once, stdout and exit untouched", D(r));
            }

            // Success with accepting stdout (stderr rejecting everything) equals the real process; stderr is never written.
            foreach (var (label, requestPath, pin) in new[] { ("plain", request, PinnedAcceptedSha256), ("composed (skills [])", composedRequest, PinnedComposedSha256) })
            {
                var inOut = Output();
                var r = Forced(AcceptOut, RejectErrAll, null, null, null, null, new[] { "generate-typology", requestPath, inOut });
                var processOut = Output();
                var process = cli(new[] { "generate-typology", requestPath, processOut });
                var inBytes = File.Exists(inOut) ? File.ReadAllBytes(inOut) : Array.Empty<byte>();
                var processBytes = File.Exists(processOut) ? File.ReadAllBytes(processOut) : Array.Empty<byte>();
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && r.Err is { Rejected: 0 } && r.Out is { } o && o.Text == process.Out + Environment.NewLine && process.Exit == 0 && process.Err.Length == 0
                    && process.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && Hex(inBytes) == pin && inBytes.SequenceEqual(processBytes),
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

    // ---- 14. P3-31: Document's own catch-all with a throwing stderr, and a stdout that accepts part of a line then throws ----
    // GenerateTypologyCommand.Run / Document of the real Release CLI assembly (reflection), in process, stdout replaced by a
    // PartialWriter (or a CountingWriter over a disposed StringWriter) and stderr by section 12's recording writer (or a
    // CountingWriter), all restored with the exit code and every seam in a finally. As P7-32 for generate-prototypes:
    //   a. Document (the MCP generate_typology path): its own catch-all (UnguardedWorkProbe) with a stderr that throws on
    //      the diagnostic (IOException, non-IO InvalidOperationException, a disposed StringWriter behind a CountingWriter)
    //      returns (false, the typed internal-error line), the diagnostic attempted exactly once, nothing escapes, stdout
    //      never written, exit untouched; the guarded block's failure with a disposed stderr likewise. Controls: accepting
    //      stderr gives the diagnostic once; a Document refusal equals the real process's line; Document's success text is the
    //      P5-12 pinned bytes; neither writes stderr. Static: Document's catch-all writes envelope.Diagnostic guarded
    //      (try / catch (Exception) { }) then returns (false, envelope.Line).
    //   b. The one documented handling of a partial stdout write: whatever prefix the writer accepted stays on stdout as a
    //      truncated line (never terminated by the newline, so never a complete record), nothing escapes Run, exit 1 (the
    //      authoritative signal), no typed line invented on another stream. catch (Refusal): each typed refusal (usage, output
    //      extension, output exists, missing request, malformed JSON, unknown pack, unknown extra skill; the real process's
    //      line L) and the guarded block's Refusal(InternalError), cut after 1, half and all of L: stdout exactly L[..k], L
    //      attempted once, stderr unchanged. Catch-all (UnguardedWorkProbe, PostWriteProbe): the typed line's first k chars,
    //      attempted once, the original diagnostic once, no file.
    //   c. A line Execute writes (the plain and composed success lines, the pipeline refusal) cut the same way: with one
    //      budget stdout is exactly line[..k] (the typed line then attempted with nothing accepted), with a per-line budget
    //      line[..k] then the typed line's first k chars; the line then the typed line attempted once each, stderr the stdout
    //      exception's diagnostic once, exit 1, the just-written file deleted (P3-25). k = all of the success line leaves the
    //      complete success JSON without its newline while the exit is 1 and no file exists (pinned: the exit code decides).
    //   d. A disposed stdout behind a CountingWriter: for b and c the attempts are exactly the lines above (one each),
    //      exit 1, nothing escapes, stderr as above (an ObjectDisposedException diagnostic for c).
    //   Controls: an unbounded budget gives the real process's success and refusal bytes, stderr unwritten.
    static void PartialWriteRun(string root, string scratch, string cliDll, Action<bool, string, string> check, Func<string[], (int Exit, string Out, string Err)> cli)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "P3-31 " + name, detail);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        const string DiagnosticPrefix = "generate-typology internal error: ";
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var nl = Environment.NewLine;
        void Check32(bool condition, string name, string detail = "") => check(condition, "P3-32 " + name, detail);
        static string Escape(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

        // P3-32: the writer helpers' WriteLine() and Write(char[], int, int) are one attempt each (self-test); without the
        // overrides TextWriter would split them into one Write(char) per character.
        {
            var inner = new StringWriter(CultureInfo.InvariantCulture);
            var counting = new CountingWriter(inner);
            counting.WriteLine();
            counting.Write("abcd".ToCharArray(), 1, 2);
            counting.Write(new[] { 'x', 'y' });
            Check32(counting.Attempts.SequenceEqual(new[] { "", "bc", "xy" }) && inner.ToString() == nl + "bcxy",
                "CountingWriter: WriteLine(), Write(char[], int, int) and Write(char[]) are one attempt each, forwarded byte-exact",
                "attempts [" + string.Join(" | ", counting.Attempts.Select(Escape)) + "] inner [" + Escape(inner.ToString()) + "]");
            foreach (var (label, act, expected) in new (string, Action<TextWriter>, string)[] { ("WriteLine()", w => w.WriteLine(), ""), ("Write(char[], int, int)", w => w.Write("zabc".ToCharArray(), 1, 3), "abc") })
            {
                var closed = new StringWriter(CultureInfo.InvariantCulture);
                closed.Dispose();
                var disposed = new CountingWriter(closed);
                Exception? thrown = null;
                try { act(disposed); }
                catch (Exception error) { thrown = error; }
                Check32(thrown is ObjectDisposedException && disposed.Attempts.SequenceEqual(new[] { expected }),
                    "CountingWriter over a disposed writer: " + label + " is recorded once, then ObjectDisposedException",
                    "attempts [" + string.Join(" | ", disposed.Attempts.Select(Escape)) + "] threw " + (thrown?.GetType().FullName ?? "nothing"));
            }
            var budgeted = new PartialWriter(2 + nl.Length, false, () => new IOException("p3-32 budget"));
            Exception? cut = null;
            budgeted.Write("zabc".ToCharArray(), 1, 2);
            budgeted.WriteLine();
            try { budgeted.Write(new[] { 'c', 'd' }); }
            catch (Exception error) { cut = error; }
            Check32(cut is IOException && budgeted.Attempts.SequenceEqual(new[] { "ab", "", "cd" }) && budgeted.Text == "ab" + nl && budgeted.Rejected == 1,
                "PartialWriter: Write(char[], int, int), WriteLine() and Write(char[]) are one attempt each against the budget; the write past it is rejected once with nothing accepted",
                "attempts [" + string.Join(" | ", budgeted.Attempts.Select(Escape)) + "] text [" + Escape(budgeted.Text) + "] rejected " + budgeted.Rejected.ToString(CultureInfo.InvariantCulture) + " threw " + (cut?.GetType().FullName ?? "nothing"));
            var empty = new PartialWriter(0, true, () => new InvalidOperationException("p3-32 per-line"));
            Exception? cutLine = null;
            try { empty.WriteLine(); }
            catch (Exception error) { cutLine = error; }
            Check32(cutLine is InvalidOperationException && empty.Attempts.SequenceEqual(new[] { "" }) && empty.Text.Length == 0 && empty.Rejected == 1,
                "PartialWriter with a zero per-line budget: WriteLine() is one attempt, rejected once, nothing accepted",
                "attempts [" + string.Join(" | ", empty.Attempts.Select(Escape)) + "] text [" + Escape(empty.Text) + "] rejected " + empty.Rejected.ToString(CultureInfo.InvariantCulture) + " threw " + (cutLine?.GetType().FullName ?? "nothing"));
        }

        var inputs = Path.Combine(scratch, "p3-31-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inputs);
        try
        {
            var counter = 0;
            string Input(string name, string content) { var path = Path.Combine(inputs, name); File.WriteAllText(path, content, new UTF8Encoding(false)); return path; }
            string Output() => Path.Combine(inputs, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + ".json");
            var good = "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}";
            var request = Input("request.json", good);
            var composedRequest = Input("composed.json", good[..^1] + ",\"skills\":[]}");
            var refusedRequest = Input("refused.json", good.Replace("\"family_size\":3", "\"family_size\":0", StringComparison.Ordinal));

            System.Reflection.Assembly? assembly = null;
            try { assembly = System.Reflection.Assembly.LoadFrom(cliDll); }
            catch (Exception error) when (error is IOException or BadImageFormatException) { Check(false, "the Release CLI assembly loads", error.Message); }
            var type = assembly?.GetType("Bim.Cli.GenerateTypologyCommand");
            var run = type?.GetMethod("Run", Any, new[] { typeof(string[]) });
            var document = type?.GetMethod("Document", Any, new[] { typeof(string) });
            var postWrite = type?.GetProperty("PostWriteProbe", Any);
            var removalSeam = type?.GetProperty("PostWriteRemovalProbe", Any);
            var unguarded = type?.GetProperty("UnguardedWorkProbe", Any);
            var guarded = type?.GetProperty("GuardedWorkProbe", Any);
            Check(run is not null && document is not null && document.ReturnType == typeof((bool, string)) && postWrite is not null && removalSeam is not null && unguarded is not null && guarded is not null,
                "Run(string[]), Document(string) and the seams UnguardedWorkProbe / GuardedWorkProbe / PostWriteProbe / PostWriteRemovalProbe found in the Release CLI");
            if (run is null || document is null || document.ReturnType != typeof((bool, string)) || postWrite is null || removalSeam is null || unguarded is null || guarded is null) return;
            System.Reflection.MethodInfo runMethod = run;
            System.Reflection.MethodInfo documentMethod = document;
            System.Reflection.PropertyInfo[] seams = { postWrite, removalSeam, unguarded, guarded };
            System.Reflection.PropertyInfo unguardedProbe = unguarded;
            System.Reflection.PropertyInfo guardedProbe = guarded;
            System.Reflection.PropertyInfo stageProbe = postWrite;
            bool AllNull() => seams.All(seam => seam.GetValue(null) is null);
            Check(AllNull(), "the generate-typology seams are null unless a test sets them");

            // One in-process Run (or Document when documentPath is set) with stdout / stderr replaced and the seams set;
            // Console, exit code and seams restored in finally.
            (Exception? Thrown, List<string> Events, int Exit, TextWriter Out, TextWriter Err, bool Restored, (bool, string)? Document) Forced(
                TextWriter stdout, TextWriter? stderrOverride, Action? before, Action? work, Action? stage, string[]? args, string? documentPath = null)
            {
                var events = new List<string>();
                var stderr = stderrOverride ?? new DiagnosticEventWriter("err", events);
                var previousOut = Console.Out;
                var previousError = Console.Error;
                var previousExit = Environment.ExitCode;
                Exception? thrown = null;
                (bool, string)? returned = null;
                int exit;
                try
                {
                    Console.SetOut(stdout);
                    Console.SetError(stderr);
                    Environment.ExitCode = 0;
                    unguardedProbe.SetValue(null, before);
                    guardedProbe.SetValue(null, work);
                    stageProbe.SetValue(null, stage);
                    try
                    {
                        if (documentPath is not null) returned = ((bool, string))documentMethod.Invoke(null, new object?[] { documentPath })!;
                        else runMethod.Invoke(null, new object?[] { args });
                    }
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
                return (thrown, events, exit, stdout, stderr, restored, returned);
            }
            static List<string> AttemptsOf(TextWriter writer) => writer switch { PartialWriter p => p.Attempts, CountingWriter c => c.Attempts, _ => new List<string>() };
            static string D((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, TextWriter Err, bool Restored, (bool, string)? Document) r) =>
                "stderr events [" + string.Join(" | ", r.Events) + "] exit " + r.Exit.ToString(CultureInfo.InvariantCulture)
                + (r.Out is PartialWriter p ? " stdout text [" + p.Text + "] (" + p.Text.Length.ToString(CultureInfo.InvariantCulture) + " chars) rejected " + p.Rejected.ToString(CultureInfo.InvariantCulture) : "")
                + " stdout attempts [" + string.Join(" | ", AttemptsOf(r.Out)) + "]"
                + (r.Err is CountingWriter ce ? " stderr attempts [" + string.Join(" | ", ce.Attempts) + "]" : "")
                + (r.Err is DiagnosticEventWriter de ? " stderr rejected [" + string.Join(" | ", de.RejectedLines) + "]" : "")
                + " restored " + r.Restored
                + (r.Document is { } d ? " document (" + d.Item1 + ", " + (d.Item2.Length > 300 ? d.Item2[..300] + "..." : d.Item2) + ")" : " no document")
                + (r.Thrown is { } t ? " threw " + t.GetType().FullName + ": " + t.Message : " threw nothing");
            bool Contained((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, TextWriter Err, bool Restored, (bool, string)? Document) r) => r.Thrown is null && r.Restored && r.Exit == 1 && AllNull();
            static bool Attempted((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, TextWriter Err, bool Restored, (bool, string)? Document) r, params string[] lines) => AttemptsOf(r.Out).SequenceEqual(lines);
            static string TextOf((Exception? Thrown, List<string> Events, int Exit, TextWriter Out, TextWriter Err, bool Restored, (bool, string)? Document) r) => r.Out is PartialWriter p ? p.Text : "";

            var failure = new InvalidOperationException("boom generate-typology P3-31 original");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}" && envelope.Exit == 1
                && envelope.Diagnostic == DiagnosticPrefix + "System.InvalidOperationException: boom generate-typology P3-31 original",
                "fixture: IntentPlan.Internal(\"generate-typology\", e) is the literal typed line, exit 1 and diagnostic", envelope.ToString());
            const string StdoutMessage = "p3-31 stdout broke part-way";
            const string StderrMessage = "p3-31 stderr rejects the diagnostic";
            var kinds = new (string Label, Func<Exception> Failure, Exception Sample)[]
            {
                ("IOException", () => new IOException(StdoutMessage), new IOException(StdoutMessage)),
                ("InvalidOperationException", () => new InvalidOperationException(StdoutMessage), new InvalidOperationException(StdoutMessage)),
            };
            static int[] Cuts(int n) => new[] { 1, n / 2, n };
            static TextWriter Disposed() { var closed = new StringWriter(CultureInfo.InvariantCulture); closed.Dispose(); return new CountingWriter(closed); }
            static TextWriter Open() => new CountingWriter(new StringWriter(CultureInfo.InvariantCulture));
            bool NoFile(string output) => !File.Exists(output) && !Directory.Exists(output) && !File.Exists(output + ".txt");

            // ---- a. Document (the MCP generate_typology path) ----
            {
                var r = Forced(Open(), null, () => throw failure, null, null, null, request);
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && AllNull() && r.Document is (false, var line) && line == envelope.Line
                    && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && Attempted(r),
                    "control: Document's own catch-all (UnguardedWorkProbe) with an accepting stderr returns (false, typed line), the diagnostic once, stdout never written, exit untouched", D(r));
            }
            foreach (var (kind, make) in new (string, Func<Exception>)[] { ("IOException", () => new IOException(StderrMessage)), ("InvalidOperationException", () => new InvalidOperationException(StderrMessage)) })
            {
                var events = new List<string>();
                var stderr = new DiagnosticEventWriter("err", events, DiagnosticPrefix, rejectAll: false, make);
                var r = Forced(Open(), stderr, () => throw failure, null, null, null, request);
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && AllNull() && r.Document is (false, var line) && line == envelope.Line
                    && events.Count == 0 && stderr.RejectedLines.SequenceEqual(new[] { envelope.Diagnostic }) && stderr.Text.Length == 0 && Attempted(r)
                    && (r.Document is not { } d || !d.Item2.Contains(StderrMessage, StringComparison.Ordinal)),
                    "Document's own catch-all (UnguardedWorkProbe) + stderr " + kind + " on the diagnostic: nothing escapes Document, (false, typed internal-error line), the diagnostic attempted once, stdout never written, exit untouched",
                    D(r) + " stderr rejected [" + string.Join(" | ", stderr.RejectedLines) + "]");
            }
            foreach (var (path, before, work) in new (string, Action?, Action?)[] { ("UnguardedWorkProbe (Document's catch-all)", () => throw failure, null), ("GuardedWorkProbe (InternalRefusal)", null, () => throw failure) })
            {
                var r = Forced(Open(), Disposed(), before, work, null, null, request);
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && AllNull() && r.Document is (false, var line) && line == envelope.Line
                    && r.Err is CountingWriter c && c.Attempts.SequenceEqual(new[] { envelope.Diagnostic }) && Attempted(r),
                    path + " + a disposed stderr behind a counting writer: Document returns (false, typed line), the diagnostic attempted once, nothing escapes, stdout never written", D(r));
            }
            {
                var processOut = Output();
                var theirs = cli(new[] { "generate-typology", Input("doc-pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), processOut });
                var r = Forced(Open(), Disposed(), null, null, null, null, Path.Combine(inputs, "doc-pack.json"));
                Check(theirs.Exit == 1 && theirs.Out.StartsWith("{\"status\":\"failed\",\"code\":\"", StringComparison.Ordinal) && r.Thrown is null && r.Restored && r.Exit == 0 && AllNull()
                    && r.Document is (false, var line) && line == theirs.Out && r.Err is CountingWriter { Attempts.Count: 0 } && Attempted(r),
                    "control: a Document refusal (unknown pack) is the real process's line, stderr and stdout never written", D(r) + " vs process " + theirs.Out);
            }
            {
                var r = Forced(Open(), Disposed(), null, null, null, null, request);
                Check(r.Thrown is null && r.Restored && r.Exit == 0 && AllNull() && r.Document is (true, var json) && Hex(new UTF8Encoding(false).GetBytes(json)) == PinnedAcceptedSha256
                    && r.Err is CountingWriter { Attempts.Count: 0 } && Attempted(r),
                    "control: Document's success text is the P5-12 pinned bytes, stderr and stdout never written", D(r));
            }
            var shared = ApplyIntentTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "GenerateTypologyCommand.cs")), blankStrings: false);
            var documentCatch = System.Text.RegularExpressions.Regex.Matches(shared,
                @"catch\s*\(\s*Exception\s+error\s*\)\s*\{\s*var\s+envelope\s*=\s*IntentPlan\s*\.\s*Internal\s*\(\s*""generate-typology""\s*,\s*error\s*\)\s*;\s*try\s*\{\s*Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*envelope\s*\.\s*Diagnostic\s*\)\s*;\s*\}\s*catch\s*\(\s*Exception\s*\)\s*\{\s*\}\s*return\s*\(\s*false\s*,\s*envelope\s*\.\s*Line\s*\)\s*;\s*\}").Count;
            // P3-32: the unguarded-stderr pin is StderrWriteProblems (layout-independent, mutation self-tested below) over both
            // generate-typology sources; it replaces the P3-31 look-behind regex, which rejected a reformatted guarded write
            // (System.Console...) and accepted a filtered or rethrowing catch.
            var stderrProblems = new List<string>();
            foreach (var file in new[] { "GenerateTypologyCommand.cs", "GenerateTypologyWrite.cs" })
                stderrProblems.AddRange(StderrWriteProblems(File.ReadAllText(Path.Combine(root, "interfaces", "cli", file))).Select(problem => file + ": " + problem));
            Check(documentCatch == 1 && stderrProblems.Count == 0,
                "GenerateTypologyCommand.cs: Document's catch-all writes envelope.Diagnostic guarded (try / catch (Exception) { }) then returns (false, envelope.Line), and no stderr write in GenerateTypologyCommand.cs or GenerateTypologyWrite.cs is unguarded",
                "guarded Document catch-all " + documentCatch.ToString(CultureInfo.InvariantCulture) + ", stderr problems [" + string.Join("; ", stderrProblems) + "]");
            {
                const string W = "Console.Error.WriteLine(envelope.Diagnostic);";
                static string Wrap(string body) => "static class C { static void M(System.Exception error) { " + body + " } }";
                var accepted = new (string Label, string Source)[]
                {
                    ("the real shape", Wrap("try { " + W + " } catch (Exception) { }")),
                    ("the shape spread over lines and tabs", Wrap("try\n\t{\n\t\tConsole\n\t\t\t.Error\n\t\t\t.WriteLine(\n\t\t\t\tenvelope.Diagnostic);\n\t}\n\tcatch (Exception)\n\t{\n\t}")),
                    ("the shape without spaces", Wrap("try{Console.Error.WriteLine(envelope.Diagnostic);}catch(Exception){}")),
                    ("a System.Console write", Wrap("try { System.Console.Error.WriteLine(envelope.Diagnostic); } catch (Exception) { }")),
                    ("a global::System.Console write and catch (System.Exception)", Wrap("try { global::System.Console . Error . WriteLine ( x ) ; } catch ( System.Exception ) { }")),
                    ("a named unused catch variable", Wrap("try { " + W + " } catch (Exception ignored) { }")),
                    ("nested parentheses and a literal holding ) ; }", Wrap("try { Console.Error.WriteLine(IntentPlan.InternalDiagnostic(\"generate-typology ); } x\", error)); } catch (Exception) { }")),
                    ("comments between the tokens", Wrap("try { /* note */ Console.Error.WriteLine(x); // trailing\n } catch (Exception) { /* swallowed */ }")),
                    ("a bare write only in a comment or a string", Wrap("// Console.Error.WriteLine(x);\n var s = \"Console.Error.WriteLine(x);\"; try { " + W + " } catch (Exception) { }")),
                    ("two guarded writes", Wrap("try { " + W + " } catch (Exception) { } try { Console.Error.WriteLine(y); } catch (Exception) { }")),
                };
                var rejected = new (string Label, string Source)[]
                {
                    ("a bare write", Wrap(W)),
                    ("a bare System.Console write", Wrap("System.Console.Error.WriteLine(x);")),
                    ("a bare write over several lines", Wrap("Console\n.Error\n.WriteLine(x);")),
                    ("an IOException-only catch", Wrap("try { " + W + " } catch (IOException) { }")),
                    ("a filtered catch", Wrap("try { " + W + " } catch (Exception e) when (e is IOException) { }")),
                    ("a catch that rethrows", Wrap("try { " + W + " } catch (Exception) { throw; }")),
                    ("a catch that writes stdout", Wrap("try { " + W + " } catch (Exception) { Console.WriteLine(x); }")),
                    ("a finally instead of a catch", Wrap("try { " + W + " } finally { }")),
                    ("a second write inside the same try", Wrap("try { " + W + " Console.Error.WriteLine(y); } catch (Exception) { }")),
                    ("Write instead of WriteLine", Wrap("try { Console.Error.Write(x); } catch (Exception) { }")),
                    ("WriteLineAsync", Wrap("try { Console.Error.WriteLineAsync(x); } catch (Exception) { }")),
                    ("an aliased Console.Error", Wrap("var e = Console.Error; try { e.WriteLine(x); } catch (Exception) { }")),
                    ("a Flush on stderr", Wrap("try { " + W + " } catch (Exception) { } Console.Error.Flush();")),
                    ("a guarded write plus a bare one", Wrap("try { " + W + " } catch (Exception) { } " + W)),
                    ("OpenStandardError", Wrap("using var s = Console.OpenStandardError();")),
                    ("Console.SetError", Wrap("Console.SetError(TextWriter.Null);")),
                    ("using static System.Console", "using static System.Console; " + Wrap("Error.WriteLine(x);")),
                };
                foreach (var (label, source) in accepted)
                {
                    var problems = StderrWriteProblems(source);
                    Check32(problems.Count == 0, "self-test: the stderr pin accepts " + label, "problems [" + string.Join("; ", problems) + "]");
                }
                foreach (var (label, source) in rejected)
                    Check32(StderrWriteProblems(source).Count > 0, "self-test: the stderr pin rejects " + label);
            }

            // ---- b. catch (Refusal): the typed refusals of the real process, cut part-way ----
            var existing = Output();
            File.WriteAllText(existing, "keep");
            var refusals = new (string Label, Func<string, string[]> Args)[]
            {
                ("usage", output => new[] { "generate-typology", request }),
                ("output extension", output => new[] { "generate-typology", request, output + ".txt" }),
                ("output exists", output => new[] { "generate-typology", request, existing }),
                ("missing request", output => new[] { "generate-typology", Path.Combine(inputs, "missing.json"), output }),
                ("malformed JSON", output => new[] { "generate-typology", Input("json.json", "{"), output }),
                ("unknown pack", output => new[] { "generate-typology", Input("pack.json", good.Replace(TypologyPack, "xx.no-such.pack", StringComparison.Ordinal)), output }),
                ("unknown extra skill", output => new[] { "generate-typology", Input("skill.json", good[..^1] + ",\"skills\":[\"xx.no-such.skill\"]}"), output }),
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
                    var control = Forced(new PartialWriter(int.MaxValue, false, () => new IOException(StdoutMessage)), null, null, null, null, argsOf(a));
                    Check(Contained(control) && Attempted(control, line) && TextOf(control) == line + nl && control.Events.Count == 0 && NoFile(a),
                        "control: the " + label + " refusal with an unbounded budget equals the real process bytes, stderr unwritten", D(control));
                }
                foreach (var (kind, make, _) in kinds)
                    foreach (var k in Cuts(line.Length))
                    {
                        var b = Output();
                        var r = Forced(new PartialWriter(k, false, make), null, null, null, null, argsOf(b));
                        Check(Contained(r) && Attempted(r, line) && TextOf(r) == line[..k] && r.Out is PartialWriter { Rejected: 1 } && r.Events.Count == 0 && NoFile(b),
                            "the " + label + " refusal + stdout " + kind + " after " + k.ToString(CultureInfo.InvariantCulture) + " of " + line.Length.ToString(CultureInfo.InvariantCulture)
                            + " chars: stdout the truncated line (no newline), attempted once, nothing escapes Run, exit 1, stderr unwritten, no file", D(r));
                    }
                {
                    var c = Output();
                    var r = Forced(Disposed(), null, null, null, null, argsOf(c));
                    Check(Contained(r) && Attempted(r, line) && r.Events.Count == 0 && NoFile(c),
                        "the " + label + " refusal + a disposed stdout behind a counting writer: the typed line attempted once, nothing escapes Run, exit 1, stderr unwritten, no file", D(r));
                }
            }
            Check(File.ReadAllText(existing) == "keep", "the existing output is left untouched");

            // b. The guarded block's Refusal(InternalError) (accepting stderr): the diagnostic once, Fail's line cut part-way.
            foreach (var (kind, make, _) in kinds)
                foreach (var k in Cuts(envelope.Line.Length))
                {
                    var output = Output();
                    var r = Forced(new PartialWriter(k, false, make), null, null, () => throw failure, null, new[] { "generate-typology", request, output });
                    Check(Contained(r) && Attempted(r, envelope.Line) && TextOf(r) == envelope.Line[..k] && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                        "GuardedWorkProbe Refusal(InternalError) + stdout " + kind + " after " + k.ToString(CultureInfo.InvariantCulture) + " chars: stdout the truncated typed line, attempted once, diagnostic once, exit 1, nothing escapes, no file", D(r));
                }
            {
                var output = Output();
                var r = Forced(Disposed(), null, null, () => throw failure, null, new[] { "generate-typology", request, output });
                Check(Contained(r) && Attempted(r, envelope.Line) && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                    "GuardedWorkProbe Refusal(InternalError) + a disposed stdout behind a counting writer: the typed line attempted once, diagnostic once, exit 1, nothing escapes, no file", D(r));
            }

            // b. Run's catch-all: before any write (UnguardedWorkProbe) and after it (PostWriteProbe; P3-25 deletes the file).
            foreach (var (path, before, stage) in new (string, Action?, Action?)[] { ("UnguardedWorkProbe", () => throw failure, null), ("PostWriteProbe", null, () => throw failure) })
            {
                foreach (var (kind, make, _) in kinds)
                    foreach (var k in Cuts(envelope.Line.Length))
                    {
                        var output = Output();
                        var r = Forced(new PartialWriter(k, false, make), null, before, null, stage, new[] { "generate-typology", request, output });
                        Check(Contained(r) && Attempted(r, envelope.Line) && TextOf(r) == envelope.Line[..k] && r.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(output),
                            path + " + stdout " + kind + " after " + k.ToString(CultureInfo.InvariantCulture) + " chars: stdout the truncated typed line, attempted once, diagnostic once, exit 1, nothing escapes, no file", D(r));
                    }
                var disposedOutput = Output();
                var d = Forced(Disposed(), null, before, null, stage, new[] { "generate-typology", request, disposedOutput });
                Check(Contained(d) && Attempted(d, envelope.Line) && d.Events.SequenceEqual(new[] { "err:" + envelope.Diagnostic }) && !File.Exists(disposedOutput),
                    path + " + a disposed stdout behind a counting writer: the typed line attempted once, diagnostic once, exit 1, nothing escapes, no file", D(d));
            }

            // ---- c. A line Execute writes (success plain / composed, the pipeline refusal) cut part-way: Run's catch-all follows ----
            var success = cli(new[] { "generate-typology", request, Output() });
            var composed = cli(new[] { "generate-typology", composedRequest, Output() });
            var refused = cli(new[] { "generate-typology", refusedRequest, Output() });
            Check(success.Exit == 0 && success.Err.Length == 0 && success.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal)
                && composed.Exit == 0 && composed.Err.Length == 0 && composed.Out.StartsWith("{\"status\":\"succeeded\"", StringComparison.Ordinal) && composed.Out.Contains("\"rules\":\"composed\"", StringComparison.Ordinal),
                "fixture: the plain and composed success lines of the real process", "plain exit " + success.Exit + " " + success.Out + " " + success.Err + " composed exit " + composed.Exit + " " + composed.Out + " " + composed.Err);
            Check(refused.Exit == 1 && refused.Err.Length == 0 && refused.Out.StartsWith("{\"status\":\"failed\",\"code\":\"generate_typology_refused\"", StringComparison.Ordinal),
                "fixture: the pipeline refusal line of the real process", "exit " + refused.Exit + " " + refused.Out + " " + refused.Err);
            foreach (var (label, requestPath, line, pin) in new (string, string, string, string?)[] { ("success", request, success.Out, PinnedAcceptedSha256), ("composed success", composedRequest, composed.Out, PinnedComposedSha256), ("pipeline refusal", refusedRequest, refused.Out, null) })
            {
                {
                    var output = Output();
                    var control = Forced(new PartialWriter(int.MaxValue, false, () => new IOException(StdoutMessage)), null, null, null, null, new[] { "generate-typology", requestPath, output });
                    var wrote = File.Exists(output) ? File.ReadAllBytes(output) : null;
                    Check(control.Thrown is null && control.Restored && control.Exit == (pin is not null ? 0 : 1) && AllNull() && Attempted(control, line) && TextOf(control) == line + nl && control.Events.Count == 0
                        && (pin is not null ? wrote is not null && Hex(wrote) == pin : wrote is null),
                        "control: the " + label + " with an unbounded budget equals the real process bytes (stdout and file), stderr unwritten", D(control) + " file " + (wrote is null ? "<none>" : Hex(wrote)));
                    if (File.Exists(output)) File.Delete(output);
                }
                foreach (var (kind, make, sample) in kinds)
                {
                    var diagnostic = Bim.Cli.IntentPlan.InternalDiagnostic("generate-typology", sample);
                    foreach (var perLine in new[] { false, true })
                        foreach (var k in perLine ? new[] { 1, line.Length / 2 } : Cuts(line.Length))
                        {
                            var output = Output();
                            byte[]? seen = null;
                            var r = Forced(new PartialWriter(k, perLine, make), null, null, null, pin is not null ? () => { seen = File.Exists(output) ? File.ReadAllBytes(output) : null; } : null,
                                new[] { "generate-typology", requestPath, output });
                            var typedPart = perLine ? (envelope.Line + nl)[..Math.Min(k, envelope.Line.Length + nl.Length)] : "";
                            var budget = (perLine ? "per-line " : "") + k.ToString(CultureInfo.InvariantCulture);
                            Check(Contained(r) && Attempted(r, line, envelope.Line) && TextOf(r) == line[..k] + typedPart && r.Events.SequenceEqual(new[] { "err:" + diagnostic })
                                && !File.Exists(output) && (pin is null || (seen is not null && Hex(seen) == pin)),
                                "the " + label + " line + stdout " + kind + " after a budget of " + budget + " of " + line.Length.ToString(CultureInfo.InvariantCulture)
                                + " chars: stdout the truncated line" + (perLine ? " then the typed line's first " + k.ToString(CultureInfo.InvariantCulture) + " chars" : "")
                                + ", the line then the typed line attempted once each, stderr the stdout exception's diagnostic once, exit 1, nothing escapes, no file" + (pin is not null ? " (the written file deleted)" : ""),
                                D(r) + " seen " + (seen is null ? "<none>" : Hex(seen)));
                        }
                }
                {
                    var output = Output();
                    var r = Forced(Disposed(), null, null, null, null, new[] { "generate-typology", requestPath, output });
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

    // P3-32: the unguarded-stderr pin over one source. Every Console.Error use ([global::][System.]Console.Error at any
    // spacing, over comment-stripped code with literals emptied) must be the guarded shape
    //   try { Console.Error.WriteLine(<one balanced argument list>); } catch ([System.]Exception [name]) { }
    // as the only statement of its try with an empty unfiltered catch; anything else (a bare write, a filtered,
    // narrowed or rethrowing catch, a finally, Write / WriteLineAsync / Flush, an alias, a second write in the try),
    // OpenStandardError, SetError or using static Console is a problem.
    static List<string> StderrWriteProblems(string source)
    {
        // P3-32 red probe: the P3-31 look-behind regex, kept until the layout-independent checker replaces it.
        var code = ApplyIntentTests.CodeOf(source, blankStrings: false);
        var unguarded = System.Text.RegularExpressions.Regex.Matches(code, @"(?<!try\s*\{\s*)Console\s*\.\s*Error\s*\.\s*WriteLine\s*\(").Count;
        return unguarded == 0 ? new List<string>() : new List<string> { unguarded.ToString(CultureInfo.InvariantCulture) + " unguarded Console.Error.WriteLine" };
    }

    // P3-31: a stdout that accepts a budget of characters and then throws (as P7-32's in GeneratePrototypesTests). Each
    // WriteLine(string) / Write call is recorded in Attempts (the full value), the accepted prefix of value + NewLine is
    // appended to Text, and when the budget runs out inside a write the given exception is thrown (counted in Rejected).
    // With perLine the budget restarts at every write; otherwise it is one budget for the whole run (a broken pipe stays
    // broken, so every later write throws with nothing accepted).
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
        public override void WriteLine(string? value) { Attempts.Add(value ?? ""); Take((value ?? "") + NewLine); }
    }

    // P3-31: a writer that records every write it is asked for (Attempts, the full value) and then hands it to inner (a
    // disposed StringWriter, which throws ObjectDisposedException, or an open one), so a real closed writer's failure is
    // observed together with the attempt.
    sealed class CountingWriter(TextWriter inner) : TextWriter
    {
        public List<string> Attempts { get; } = new();
        public override Encoding Encoding => new UTF8Encoding(false);
        public override void Write(char value) { Attempts.Add(value.ToString()); inner.Write(value); }
        public override void Write(string? value) { Attempts.Add(value ?? ""); inner.Write(value); }
        public override void WriteLine(string? value) { Attempts.Add(value ?? ""); inner.WriteLine(value); }
    }

    // P3-28: a recording writer: each WriteLine(string) is appended to the shared event list as "<stream>:<line>" and to
    // Text (with the newline). As stderr it can reject (throw the given exception, counted in Rejected, the line kept in
    // RejectedLines) a WriteLine whose line starts with rejectPrefix, or every write when rejectAll; other characters
    // collect in Partial.
    sealed class DiagnosticEventWriter(string stream, List<string> events, string? rejectPrefix = null, bool rejectAll = false, Func<Exception>? failure = null) : TextWriter
    {
        readonly StringBuilder text = new();
        readonly StringBuilder partial = new();
        public int Rejected => RejectedLines.Count;
        public List<string> RejectedLines { get; } = new();
        public string Text => text.ToString();
        public string Partial => partial.ToString();
        public override Encoding Encoding => new UTF8Encoding(false);
        Exception Reject(string? value) { RejectedLines.Add(value ?? ""); return failure?.Invoke() ?? new IOException("p3-28 rejected write"); }
        public override void Write(char value) { if (rejectAll) throw Reject(value.ToString()); partial.Append(value); text.Append(value); }
        public override void Write(string? value) { if (rejectAll) throw Reject(value); partial.Append(value); text.Append(value); }
        public override void WriteLine(string? value)
        {
            if (rejectAll || (rejectPrefix is not null && value is not null && value.StartsWith(rejectPrefix, StringComparison.Ordinal))) throw Reject(value);
            events.Add(stream + ":" + (value ?? ""));
            text.Append(value).Append(CoreNewLine);
        }
    }

    static string RefusedEnvelope(TypologyPipelineResult library) =>
        JsonSerializer.Serialize(new { status = "failed", code = "generate_typology_refused", refusals = library.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) });
}
