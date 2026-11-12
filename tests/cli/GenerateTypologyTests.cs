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
        var composedAgainOut = Output();
        var composedAgain = Cli("generate-typology", composedRequest, composedAgainOut);
        Check(composedAgain.Exit == 0 && Read(composedAgainOut).SequenceEqual(composedBytes) && composedAgain.Out == composed.Out, "a second composed run is byte-identical", D(composedAgain));

        void ExpectComposed(string label, string text, SkillManifest typology, TypologyBriefRequest request, IReadOnlyList<SkillManifest>? extras, IReadOnlyList<string>? precedence,
            string? refusalCode = null)
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
            }
        }
        ExpectComposed("skills naming the typology and code packs", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CodePack, TypologyPack)), pack, request3,
            new[] { caba, pack }, null);
        ExpectComposed("an extra style pack", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle)), pack, request3, new[] { compact }, null);
        ExpectComposed("an extra style pack on the family-4 lot", Request(TypologyPack, 4, 12, 12, 6, 0, ",\"skills\":" + Ids(CompactStyle)), pack, request4, new[] { compact }, null);
        ExpectComposed("an extra named twice", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"skills\":" + Ids(CompactStyle, CompactStyle)), pack, request3, new[] { compact, compact }, null);
        ExpectComposed("a precedence order alone", Request(TypologyPack, 3, 16, 16, 4, 0, ",\"precedence\":" + Ids(TypologyPack, CodePack)), pack, request3, null,
            new[] { TypologyPack, CodePack });
        ExpectComposed("skills and precedence with tuned weights", Request(TypologyPack, 3, 16, 16, 4, 2, ",\"weights\":{\"compliance\":0.2,\"area_fit\":0.5},\"precedence\":"
            + Ids(CompactStyle, TypologyPack) + ",\"skills\":" + Ids(CompactStyle)), pack,
            new TypologyBriefRequest(3, new PrototypeLot(16, 16), 4, 2, Weights: new ScoringWeights(0.5, 0.25, 0.15, 0.1, 0.1, 0.2)), new[] { compact }, new[] { CompactStyle, TypologyPack });
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
            ("an include member (not in v1)", good[..^1] + ",\"include\":[\"study\"]}", "request.include"),
            ("a code pack member (not in v1)", good[..^1] + ",\"code_pack\":\"" + CodePack + "\"}", "request.code_pack"),
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
    }

    static string RefusedEnvelope(TypologyPipelineResult library) =>
        JsonSerializer.Serialize(new { status = "failed", code = "generate_typology_refused", refusals = library.Refusals.Select(r => new { code = r.Code, subject = r.Subject, detail = r.Detail }) });
}
