using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-11 `plan-layouts brief.json output.json [--alternatives n] [--seed s] [--pack id]`: a P6-03 brief planned into ranked
// alternatives, the canonical AlternativeSetSerializer JSON written to a new file. Oracles, all against the real Release
// CLI process:
//   1. The output file is byte-identical to AlternativeSetSerializer.Serialize(AlternativeRanker.Rank(brief, plans)) of
//      SpacePlanner.Plan(brief, s + i) computed in process (equal to AlternativeGenerator.Generate), UTF-8 without BOM,
//      and byte-identical across runs; option order does not matter; defaults are n = 4, s = 0; seeds wrap in 32 bits.
//   2. An infeasible brief is a written result (exit 0), not a failure.
//   3. Never-overwrite: an existing output is refused (output_exists) and left byte- and mtime-identical.
//   4. Every failure (usage, options, output path, oversize/missing input, malformed brief, missing site, invalid brief)
//      prints a typed {"status":"failed","code":...} envelope, exits 1 and leaves no output file, with the documented
//      precedence (options and output path before the input is read).
//   5. P7-13 --pack id: the pack's error-severity area_m2 / min_side_m minimum room rules become SpacePlanner rules
//      (grouped per room glob, traced in the Reference that the planner cites in its constraint details); the file is
//      byte-identical to AlternativeGenerator.Generate with exactly those PlannerRules in process, and the rules equal
//      both a literal expectation and the pack part (name-pattern rules) of TypologyRules.From for the same pack and
//      brief. Without --pack the bytes are today's. Pack refusals are typed plan_layouts_pack_* envelopes (exit 1, no
//      file); a scratch repository with its own skills/ covers malformed, mismatched, oversized and rule-less packs.
//   6. P7-15: --pack finds the pack like generate-prototypes (SkillPackLookup.Load(id, packageFirst: true)): from the
//      release package layout (outside any repository) the run is byte-equal to the repository run; a package inside a
//      repository is read before it; a stray skills/ beside a non-package binary folder is ignored.
internal static class PlanLayoutsTests
{
    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "plan-layouts");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "plan-layouts: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments) => CliAt(cliDll, null, arguments);
        (int Exit, string Out, string Err) CliAt(string dll, Dictionary<string, string>? environment, params string[] arguments)
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
            element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
        // A typed failure: exit 1, one JSON envelope with status "failed" and the code, and no output file.
        JsonElement? ExpectFailure(string name, string code, string output, params string[] arguments)
        {
            var result = Cli(arguments);
            var envelope = Parse(result.Out);
            Check(result.Exit == 1 && Str(envelope, "status") == "failed" && Str(envelope, "code") == code && !File.Exists(output) && !Directory.Exists(output),
                name + " -> " + code + ", exit 1, no output file", D(result));
            return envelope;
        }
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        // The documented pipeline, spelled out: SpacePlanner seeds s + i (wrapping) then AlternativeRanker, serialized.
        static byte[] Expected(ProjectBrief brief, int count, int seed)
        {
            var plans = new SpacePlan[count];
            for (var index = 0; index < count; index++) plans[index] = SpacePlanner.Plan(brief, unchecked(seed + index));
            return Utf8(AlternativeSetSerializer.Serialize(AlternativeRanker.Rank(brief, plans)));
        }
        byte[] Written(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        string Sizes(byte[] written, byte[] expected) => " written " + written.Length.ToString(CultureInfo.InvariantCulture) + " expected " + expected.Length.ToString(CultureInfo.InvariantCulture);

        // ---- registration and usage ----
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"plan-layouts\": PlanLayoutsCommand.Run(args); break;", StringComparison.Ordinal), "Program.cs registers plan-layouts");
        var usageOut = Path.Combine(scratch, "usage-never.json");
        var usageEnvelope = ExpectFailure("no arguments", "plan_layouts_usage", usageOut, "plan-layouts");
        ExpectFailure("one argument", "plan_layouts_usage", usageOut, "plan-layouts", Input("one.json", "{}"));
        ExpectFailure("an option in place of the output path", "plan_layouts_usage", usageOut, "plan-layouts", Input("two.json", "{}"), "--seed", "1");
        var usageDetail = Str(usageEnvelope, "detail") ?? "";
        Check(usageDetail.Contains("plan-layouts brief.json output.json [--alternatives n] [--seed s]", StringComparison.Ordinal) && usageDetail.Contains("1..64", StringComparison.Ordinal)
            && usageDetail.Contains("[--pack id]", StringComparison.Ordinal) && usageDetail.Contains("skills/<id>/skill.json", StringComparison.Ordinal),
            "the usage text documents the verb, its options (with --pack and the repository skills/<id>/skill.json lookup) and the alternatives bound", usageDetail);

        var verbList = Cli("no-such-verb");
        Check(verbList.Out.Contains("plan-layouts brief.json output.json [--alternatives n] [--seed s] [--pack id]", StringComparison.Ordinal), "the CLI's default usage lists plan-layouts with --pack", D(verbList));

        // ---- success: equals the in-process run, canonical bytes, deterministic ----
        var exampleText = File.ReadAllText(Path.Combine(root, "examples", "brief", "apartment-2br.json"));
        var example = BriefParser.Parse(exampleText);
        var exampleBrief = Input("apartment-2br.json", exampleText);
        var expected = Expected(example, 2, 1);
        Check(expected.SequenceEqual(Utf8(AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(example, new AlternativeOptions(2, 1))))),
            "the oracle (SpacePlanner seeds + AlternativeRanker) equals AlternativeGenerator.Generate in process");
        var first = Output();
        var run1 = Cli("plan-layouts", exampleBrief, first, "--alternatives", "2", "--seed", "1");
        var written1 = Written(first);
        Check(run1.Exit == 0 && written1.SequenceEqual(expected), "the output is AlternativeSetSerializer.Serialize of the ranked SpacePlanner alternatives byte for byte",
            D(run1) + Sizes(written1, expected));
        Check(written1.Length > 3 && !(written1[0] == 0xEF && written1[1] == 0xBB && written1[2] == 0xBF), "the output has no byte order mark");
        var success = Parse(run1.Out);
        var expectedSet = AlternativeGenerator.Generate(example, new AlternativeOptions(2, 1));
        Check(Str(success, "status") == "succeeded" && Str(success, "kind") == "plan-layouts" && Str(success, "scope") == "proposal_only" && Str(success, "sha256") == Hex(expected) && Int(success, "bytes") == expected.LongLength
            && Str(success, "set_status") == (expectedSet.Status == AlternativeSetStatus.Ranked ? "ranked" : "infeasible") && Int(success, "requested") == 2 && Int(success, "seed") == 1
            && Int(success, "ranked") == expectedSet.Ranked.Count && Int(success, "alternatives") == expectedSet.Alternatives.Count
            && success is { } s && s.TryGetProperty("persisted", out var persisted) && persisted.ValueKind == JsonValueKind.False,
            "stdout reports succeeded, scope proposal_only, the set status, requested count, seed, ranked count, byte count and SHA-256 of the file, persisted false", D(run1));
        Check(expectedSet.Status == AlternativeSetStatus.Ranked && expectedSet.Ranked.Count >= 1, "the example brief yields at least one ranked alternative", expectedSet.Status.ToString());
        var second = Output();
        var run2 = Cli("plan-layouts", exampleBrief, second, "--seed", "1", "--alternatives", "2");
        Check(run2.Exit == 0 && Written(second).SequenceEqual(written1) && run2.Out == run1.Out,
            "a second run with the options in the other order writes byte-identical output and the same stdout", D(run2));

        // A small brief keeps the remaining success runs fast.
        const string small = "{\"brief_version\":1,\"name\":\"Small studio\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
            + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}],"
            + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"bath\",\"kind\":\"required\"}]}";
        var smallBrief = BriefParser.Parse(small);
        var smallPath = Input("small.json", small);
        var defaultsOut = Output();
        var run3 = Cli("plan-layouts", smallPath, defaultsOut);
        var expectedDefaults = Expected(smallBrief, 4, 0);
        Check(run3.Exit == 0 && Written(defaultsOut).SequenceEqual(expectedDefaults) && Int(Parse(run3.Out), "requested") == 4 && Int(Parse(run3.Out), "seed") == 0,
            "without options the run is 4 alternatives from seed 0", D(run3) + Sizes(Written(defaultsOut), expectedDefaults));
        var wrapOut = Output();
        var run4 = Cli("plan-layouts", smallPath, wrapOut, "--seed", "2147483647", "--alternatives", "2");
        var expectedWrap = Expected(smallBrief, 2, int.MaxValue);
        Check(run4.Exit == 0 && Written(wrapOut).SequenceEqual(expectedWrap), "seed 2147483647 with 2 alternatives wraps to int.MinValue like the generator",
            D(run4) + Sizes(Written(wrapOut), expectedWrap));
        var negativeOut = Output();
        var run5 = Cli("plan-layouts", smallPath, negativeOut, "--alternatives", "1", "--seed", "-7");
        Check(run5.Exit == 0 && Written(negativeOut).SequenceEqual(Expected(smallBrief, 1, -7)), "a negative seed and a single alternative are accepted", D(run5));
        var bomOut = Output();
        var bomPath = Path.Combine(scratch, "bom-small.json");
        File.WriteAllBytes(bomPath, [0xEF, 0xBB, 0xBF, .. Utf8(small)]);
        var run6 = Cli("plan-layouts", bomPath, bomOut, "--alternatives", "1", "--seed", "-7");
        Check(run6.Exit == 0 && Written(bomOut).SequenceEqual(Written(negativeOut)), "a UTF-8 byte order mark on the brief is stripped", D(run6));

        // ---- an infeasible brief is a written result ----
        const string cramped = "{\"brief_version\":1,\"name\":\"Cramped\",\"constraints\":{\"site\":{\"width_m\":2,\"depth_m\":2}},"
            + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"min_area_m2\":15}]}";
        var crampedOut = Output();
        var run7 = Cli("plan-layouts", Input("cramped.json", cramped), crampedOut, "--alternatives", "2");
        var expectedCramped = Expected(BriefParser.Parse(cramped), 2, 0);
        Check(run7.Exit == 0 && Written(crampedOut).SequenceEqual(expectedCramped) && Str(Parse(run7.Out), "set_status") == "infeasible" && Int(Parse(run7.Out), "ranked") == 0,
            "an infeasible brief writes the infeasible set with the planner's reasons and exits 0", D(run7) + Sizes(Written(crampedOut), expectedCramped));

        // ---- options ----
        string Never() => Output("never.json");
        void Option(string label, string code, string detail, params string[] options)
        {
            var output = Never();
            var envelope = ExpectFailure(label, code, output, ["plan-layouts", smallPath, output, .. options]);
            Check(Str(envelope, "detail") == detail, label + " detail is exactly '" + detail + "'", Str(envelope, "detail") ?? "<no detail>");
        }
        Option("--alternatives 0", "plan_layouts_alternatives_invalid", "0", "--alternatives", "0");
        Option("--alternatives 65", "plan_layouts_alternatives_invalid", "65", "--alternatives", "65");
        Option("--alternatives -1", "plan_layouts_alternatives_invalid", "-1", "--alternatives", "-1");
        Option("--alternatives abc", "plan_layouts_alternatives_invalid", "abc", "--alternatives", "abc");
        Option("--alternatives 1.5", "plan_layouts_alternatives_invalid", "1.5", "--alternatives", "1.5");
        Option("--alternatives +2", "plan_layouts_alternatives_invalid", "+2", "--alternatives", "+2");
        Option("--alternatives ' 2'", "plan_layouts_alternatives_invalid", " 2", "--alternatives", " 2");
        Option("--alternatives 99999999999", "plan_layouts_alternatives_invalid", "99999999999", "--alternatives", "99999999999");
        Option("--seed 2147483648", "plan_layouts_seed_invalid", "2147483648", "--seed", "2147483648");
        Option("--seed 1e3", "plan_layouts_seed_invalid", "1e3", "--seed", "1e3");
        Option("--seed empty", "plan_layouts_seed_invalid", "", "--seed", "");
        Option("--alternatives without a value", "plan_layouts_option_value_missing", "--alternatives", "--alternatives");
        Option("--seed without a value", "plan_layouts_option_value_missing", "--seed", "--alternatives", "2", "--seed");
        Option("a repeated --seed", "plan_layouts_option_duplicate", "--seed", "--seed", "1", "--seed", "1");
        Option("a repeated --alternatives", "plan_layouts_option_duplicate", "--alternatives", "--alternatives", "2", "--alternatives", "3");
        Option("an unknown option", "plan_layouts_option_unknown", "--weights", "--weights", "x");
        Option("a stray argument", "plan_layouts_option_unknown", "extra", "extra");
        Option("--alternatives followed by another option", "plan_layouts_alternatives_invalid", "--seed", "--alternatives", "--seed", "3");
        Option("--seed -2147483649", "plan_layouts_seed_invalid", "-2147483649", "--seed", "-2147483649");
        var minSeedOut = Output();
        var minSeed = Cli("plan-layouts", smallPath, minSeedOut, "--seed", "-2147483648", "--alternatives", "1");
        Check(minSeed.Exit == 0 && Written(minSeedOut).SequenceEqual(Expected(smallBrief, 1, int.MinValue)) && Int(Parse(minSeed.Out), "seed") == int.MinValue,
            "seed -2147483648 (int.MinValue) is accepted", D(minSeed));
        var beforePaths = Never();
        ExpectFailure("an option before the paths", "plan_layouts_usage", beforePaths, "plan-layouts", "--seed", "1", smallPath, beforePaths);

        // ---- precedence: options, then the output path, before the input is read ----
        var missing = Path.Combine(scratch, "no-such-brief.json");
        var p1 = Never(); ExpectFailure("a bad option with a missing brief", "plan_layouts_alternatives_invalid", p1, "plan-layouts", missing, p1, "--alternatives", "0");
        var p2 = Path.Combine(scratch, "out.txt"); ExpectFailure("an output with another extension and a missing brief", "output_extension_unsupported", p2, "plan-layouts", missing, p2);

        // ---- never-overwrite and output path ----
        var existing = Output("existing.json");
        var sentinel = Utf8("{\"keep\":true}");
        File.WriteAllBytes(existing, sentinel);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(existing, stamp);
        var overwrite = Cli("plan-layouts", smallPath, existing, "--alternatives", "1");
        Check(overwrite.Exit == 1 && Str(Parse(overwrite.Out), "code") == "output_exists" && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
            "an existing output is refused (output_exists) and left byte- and mtime-identical", D(overwrite));
        var directoryOut = Output("dir.json");
        Directory.CreateDirectory(directoryOut);
        var dirResult = Cli("plan-layouts", smallPath, directoryOut, "--alternatives", "1");
        Check(dirResult.Exit == 1 && Str(Parse(dirResult.Out), "code") == "output_exists" && Directory.GetFileSystemEntries(directoryOut).Length == 0,
            "a directory at the output path is refused (output_exists)", D(dirResult));
        var sameBefore = File.ReadAllBytes(smallPath);
        var same = Cli("plan-layouts", smallPath, smallPath, "--alternatives", "1");
        Check(same.Exit == 1 && Str(Parse(same.Out), "status") == "failed" && Str(Parse(same.Out), "code") == "output_exists" && File.ReadAllBytes(smallPath).SequenceEqual(sameBefore),
            "the brief path given as the output is refused (output_exists) and the brief stays byte-identical", D(same));
        // GltfExporter.WriteNew itself (the verb's only writer): an existing file is output_exists and keeps its bytes.
        // The failing-stream cleanup path is covered through the shared private WriteNew in tests/gltf (WriteGlb seam).
        string? writeCode = null;
        try { Bim.Gltf.GltfExporter.WriteNew(Utf8("{\"replaced\":true}"), existing); }
        catch (Bim.Gltf.GltfExportException error) { writeCode = error.Code; }
        Check(writeCode == "output_exists" && File.ReadAllBytes(existing).SequenceEqual(sentinel) && File.GetLastWriteTimeUtc(existing) == stamp,
            "GltfExporter.WriteNew on an existing path refuses output_exists and leaves the file byte- and mtime-identical", writeCode ?? "<no refusal>");
        var noDir = Path.Combine(scratch, "missing-dir", "out.json");
        ExpectFailure("an output in a missing directory", "output_directory_not_found", noDir, "plan-layouts", smallPath, noDir);

        // ---- input and brief refusals ----
        var overCapPath = Path.Combine(scratch, "over-cap.json");
        File.WriteAllBytes(overCapPath, overCap);
        var o1 = Never(); ExpectFailure("an 8 MiB + 1 brief", "input_too_large", o1, "plan-layouts", overCapPath, o1);
        var o2 = Never(); ExpectFailure("a missing brief", "input_not_found", o2, "plan-layouts", missing, o2);
        var o3 = Never(); ExpectFailure("a directory as the brief", "input_not_regular_file", o3, "plan-layouts", scratch, o3);
        var bigBrief = Input("big.json", "{\"brief_version\":1,\"name\":\"" + new string('x', BriefParser.MaxInputChars) + "\"}");
        var o4 = Never(); var big = ExpectFailure("a brief over the parser's character cap", "plan_layouts_brief_invalid", o4, "plan-layouts", bigBrief, o4);
        Check(Str(big, "detail") == BriefCodes.InputTooLarge + " at brief", "the oversize brief detail is exactly 'input_too_large at brief'", Str(big, "detail") ?? "");
        void Brief(string label, string text, string code, string detail)
        {
            var output = Never();
            var envelope = ExpectFailure(label, code, output, "plan-layouts", Input("brief.json", text), output, "--alternatives", "1");
            Check(Str(envelope, "detail") == detail, label + " detail is exactly '" + detail + "'", Str(envelope, "detail") ?? "<no detail>");
        }
        Brief("malformed JSON", "{", "plan_layouts_brief_invalid", BriefCodes.JsonInvalid + " at brief");
        Brief("an unsupported brief version", small.Replace("\"brief_version\":1", "\"brief_version\":2", StringComparison.Ordinal), "plan_layouts_brief_invalid", BriefCodes.VersionUnsupported + " at brief_version");
        Brief("an unknown brief member", small.Replace("\"name\":\"Small studio\"", "\"name\":\"Small studio\",\"colour\":\"red\"", StringComparison.Ordinal), "plan_layouts_brief_invalid", BriefCodes.MemberUnknown + " at colour");
        Brief("a brief without a site", small.Replace("\"site\":{\"width_m\":6,\"depth_m\":8},", "", StringComparison.Ordinal), "plan_layouts_site_required", "constraints.site");
        Brief("a negative site width", small.Replace("\"width_m\":6", "\"width_m\":-6", StringComparison.Ordinal), "plan_layouts_brief_invalid", BriefCodes.SiteDimensionNonPositive + " at constraints.site.width_m");
        Brief("an adjacency naming an unknown room", small.Replace("\"b\":\"bath\"", "\"b\":\"kitchen\"", StringComparison.Ordinal), "plan_layouts_brief_invalid", BriefCodes.AdjacencyRoomUnknown + " at adjacencies[0].b");

        // ---- P7-13 --pack id: skill pack minimum rules as traced SpacePlanner rules ----
        const string Caba = "ar.caba.codigo-edificacion";
        static PlannerRule Rule(string pattern, double? area, double? side, string reference) => new(NamePattern: pattern, MinAreaSquareMetres: area, MinSideMetres: side, Reference: reference);
        static byte[] ExpectedWith(ProjectBrief brief, int count, int seed, IReadOnlyList<PlannerRule> rules) =>
            Utf8(AlternativeSetSerializer.Serialize(AlternativeGenerator.Generate(brief, new AlternativeOptions(count, seed, new PlannerOptions(Rules: new PlannerRules(rules))))));
        // The core mapping of composed pack rules (P5-13) for the same single pack and brief, without the typology
        // template's room-id rules (hall, laundry): the CLI plans arbitrary briefs, not typology programmes.
        static PlannerRule[] CoreRules(SkillManifest pack, ProjectBrief brief)
        {
            var resolution = SkillResolver.Resolve(new SkillCatalog(new[] { pack }), new[] { new SkillRequest(pack.Id) });
            var site = brief.Constraints.Site!;
            var set = TypologyRules.From(resolution, brief, new PrototypeLot(site.WidthMetres.Value, site.DepthMetres.Value));
            return set.Rules.Rules.Where(rule => rule.RoomId is null).ToArray();
        }
        string Rules(IEnumerable<PlannerRule> rules) => string.Join(" | ", rules.Select(rule => rule.ToString()));
        SkillManifest Manifest(string id) => SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", id, "skill.json")));
        var cabaPack = Manifest(Caba);

        // Success on the small brief (Living, Bath): the CABA living* and bath* minimums, in pack order.
        var smallCaba = new[]
        {
            Rule("living*", 9, 2.8, Caba + " living-area-min living-side-min"),
            Rule("bath*", 1.4, 0.9, Caba + " bathroom-area-min bathroom-side-min"),
        };
        Check(CoreRules(cabaPack, smallBrief).SequenceEqual(smallCaba), "the literal CABA rules of the small brief equal the pack part of TypologyRules.From", Rules(CoreRules(cabaPack, smallBrief)));
        var packOut = Output();
        var pack1 = Cli("plan-layouts", smallPath, packOut, "--pack", Caba, "--alternatives", "2", "--seed", "3");
        var expectedPack = ExpectedWith(smallBrief, 2, 3, smallCaba);
        var packText = Encoding.UTF8.GetString(Written(packOut));
        Check(pack1.Exit == 0 && Written(packOut).SequenceEqual(expectedPack), "--pack: the output equals AlternativeGenerator.Generate with the pack's PlannerRules in process, byte for byte",
            D(pack1) + Sizes(Written(packOut), expectedPack));
        Check(!expectedPack.SequenceEqual(Expected(smallBrief, 2, 3)), "--pack changes the plans of the small brief (the rules are applied)");
        Check(packText.Contains("rules[0] " + Caba + " living-area-min living-side-min", StringComparison.Ordinal),
            "--pack: the planner's constraint details trace the pack rule ids (rules[0] " + Caba + " living-area-min living-side-min)", packText.Length > 400 ? packText[..400] : packText);
        var packEnvelope = Parse(pack1.Out);
        Check(Str(packEnvelope, "status") == "succeeded" && Str(packEnvelope, "kind") == "plan-layouts" && Str(packEnvelope, "pack") == Caba && Int(packEnvelope, "pack_rules") == 2
            && Int(packEnvelope, "requested") == 2 && Int(packEnvelope, "seed") == 3 && Str(packEnvelope, "sha256") == Hex(expectedPack) && Int(packEnvelope, "bytes") == expectedPack.LongLength,
            "--pack: stdout adds the pack id and the planner rule count to the success envelope", D(pack1));
        var plainEnvelope = Parse(run3.Out);
        Check(plainEnvelope is { } plain && !plain.TryGetProperty("pack", out _) && !plain.TryGetProperty("pack_rules", out _),
            "without --pack the success envelope has no pack members (today's stdout)", D(run3));
        var pack2Out = Output();
        var pack2 = Cli("plan-layouts", smallPath, pack2Out, "--seed", "3", "--pack", Caba, "--alternatives", "2");
        Check(pack2.Exit == 0 && Written(pack2Out).SequenceEqual(expectedPack) && pack2.Out == pack1.Out, "--pack in another option order writes the same bytes and stdout", D(pack2));

        // The example brief: Living-dining, Kitchen and Bathroom match (Main/Second bedroom do not match bedroom*; the
        // brief's hall and laundry ids get no template rule).
        var exampleCaba = new[]
        {
            Rule("living*", 9, 2.8, Caba + " living-area-min living-side-min"),
            Rule("bath*", 1.4, 0.9, Caba + " bathroom-area-min bathroom-side-min"),
            Rule("kitchen*", 3, 1.5, Caba + " kitchen-area-min kitchen-side-min"),
        };
        Check(CoreRules(cabaPack, example).SequenceEqual(exampleCaba), "the literal CABA rules of the example brief equal the pack part of TypologyRules.From", Rules(CoreRules(cabaPack, example)));
        var exampleOut = Output();
        var pack3 = Cli("plan-layouts", exampleBrief, exampleOut, "--alternatives", "2", "--seed", "1", "--pack", Caba);
        var expectedExample = ExpectedWith(example, 2, 1, exampleCaba);
        Check(pack3.Exit == 0 && Written(exampleOut).SequenceEqual(expectedExample) && Int(Parse(pack3.Out), "pack_rules") == 3,
            "--pack on the example brief equals the in-process run with the three matching CABA rules", D(pack3) + Sizes(Written(exampleOut), expectedExample));

        // A parameter threshold is traced with its parameter (CABA corredor_lado_min_m) under a comma-decimal culture too.
        const string corridor = "{\"brief_version\":1,\"name\":\"Corridor\",\"constraints\":{\"site\":{\"width_m\":7,\"depth_m\":8},\"levels\":1},"
            + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":20,\"min_area_m2\":15},{\"id\":\"pasillo\",\"name\":\"Pasillo\",\"target_area_m2\":4,\"min_area_m2\":2}],"
            + "\"adjacencies\":[{\"a\":\"living\",\"b\":\"pasillo\",\"kind\":\"required\"}]}";
        var corridorBrief = BriefParser.Parse(corridor);
        var corridorPath = Input("corridor.json", corridor);
        var corridorRules = new[]
        {
            Rule("living*", 9, 2.8, Caba + " living-area-min living-side-min"),
            Rule("pasillo*", null, 1, Caba + " pasillo-side-min (corredor_lado_min_m 1)"),
        };
        Check(CoreRules(cabaPack, corridorBrief).SequenceEqual(corridorRules), "a parameter threshold is traced as '<rule> (<parameter> <value>)' like TypologyRules", Rules(CoreRules(cabaPack, corridorBrief)));
        var corridorOut = Output();
        var pack4 = Cli("plan-layouts", corridorPath, corridorOut, "--alternatives", "1", "--pack", Caba);
        var expectedCorridor = ExpectedWith(corridorBrief, 1, 0, corridorRules);
        Check(pack4.Exit == 0 && Written(corridorOut).SequenceEqual(expectedCorridor), "--pack with a parameter threshold equals the in-process run", D(pack4) + Sizes(Written(corridorOut), expectedCorridor));
        {
            var german = false;
            try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
            catch (CultureNotFoundException) { }
            if (!german) Console.WriteLine("SKIP plan-layouts: de-DE culture data unavailable (invariant globalization); --pack culture check not run");
            else
            {
                var env = new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" };
                var germanOut = Output();
                var pack5 = CliAt(cliDll, env, "plan-layouts", corridorPath, germanOut, "--alternatives", "1", "--pack", Caba);
                Check(pack5.Exit == 0 && Written(germanOut).SequenceEqual(expectedCorridor) && pack5.Out == pack4.Out, "under de_DE the --pack run writes the same invariant bytes and stdout", D(pack5));
            }
        }

        // Another real pack (the fictitious example code): only living* area 10 matches the small brief.
        var examplePack = Manifest("xx.example.codigo-ejemplo");
        var exampleRules = new[] { Rule("living*", 10, null, "xx.example.codigo-ejemplo living-area-min") };
        Check(CoreRules(examplePack, smallBrief).SequenceEqual(exampleRules), "the example pack's rule for the small brief equals TypologyRules.From", Rules(CoreRules(examplePack, smallBrief)));
        var otherOut = Output();
        var pack6 = Cli("plan-layouts", smallPath, otherOut, "--alternatives", "1", "--pack", "xx.example.codigo-ejemplo");
        Check(pack6.Exit == 0 && Written(otherOut).SequenceEqual(ExpectedWith(smallBrief, 1, 0, exampleRules)) && Int(Parse(pack6.Out), "pack_rules") == 1,
            "--pack xx.example.codigo-ejemplo plans with its living* area minimum", D(pack6));
        // A pack with planner rules none of which matches a brief room plans with no rules: today's bytes, pack_rules 0.
        const string unmatched = "{\"brief_version\":1,\"name\":\"Unmatched\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
            + "\"rooms\":[{\"id\":\"office\",\"name\":\"Office\",\"target_area_m2\":20,\"min_area_m2\":15}]}";
        var unmatchedOut = Output();
        var pack7 = Cli("plan-layouts", Input("unmatched.json", unmatched), unmatchedOut, "--alternatives", "1", "--pack", "xx.example.codigo-ejemplo");
        Check(pack7.Exit == 0 && Written(unmatchedOut).SequenceEqual(Expected(BriefParser.Parse(unmatched), 1, 0)) && Int(Parse(pack7.Out), "pack_rules") == 0 && Str(Parse(pack7.Out), "pack") == "xx.example.codigo-ejemplo",
            "a pack whose rules match no brief room plans with no rules (the no-pack bytes) and reports pack_rules 0", D(pack7));

        // ---- pack refusals ----
        Option("--pack without a value", "plan_layouts_option_value_missing", "--pack", "--pack");
        Option("a repeated --pack", "plan_layouts_option_duplicate", "--pack", "--pack", Caba, "--pack", Caba);
        Option("a malformed pack id", "plan_layouts_pack_id_invalid", "../skills", "--pack", "../skills");
        Option("an empty pack id", "plan_layouts_pack_id_invalid", "", "--pack", "");
        Option("a pack id with a path separator", "plan_layouts_pack_id_invalid", "ar.caba/x", "--pack", "ar.caba/x");
        Option("--pack followed by another option", "plan_layouts_pack_id_invalid", "--seed", "--pack", "--seed", "3");
        Option("an unknown pack", "plan_layouts_pack_unavailable", "input_not_found", "--pack", "xx.no-such.pack");
        Option("a pack with no planner minimum rules", "plan_layouts_pack_rules_none", "bim.typology.vivienda-unifamiliar", "--pack", "bim.typology.vivienda-unifamiliar");
        Option("a pack with only warning minimums", "plan_layouts_pack_rules_none", "bim.typology.dwelling", "--pack", "bim.typology.dwelling");
        // Precedence: the pack id syntax is an option (before the output path and the input); the pack is read after the brief.
        var q1 = Path.Combine(scratch, "pack-out.txt"); ExpectFailure("a malformed pack id with a bad output and a missing brief", "plan_layouts_pack_id_invalid", q1, "plan-layouts", missing, q1, "--pack", "../x");
        var q2 = Never(); ExpectFailure("an unknown pack with a missing brief", "input_not_found", q2, "plan-layouts", missing, q2, "--pack", "xx.no-such.pack");
        var q3 = Never(); ExpectFailure("an unknown pack with a brief without a site", "plan_layouts_site_required", q3, "plan-layouts",
            Input("nosite.json", small.Replace("\"site\":{\"width_m\":6,\"depth_m\":8},", "", StringComparison.Ordinal)), q3, "--pack", "xx.no-such.pack");

        // An existing output is refused before the (unknown) pack is looked up, and keeps its bytes.
        var keptOut = Output("kept.json");
        File.WriteAllBytes(keptOut, sentinel);
        var kept = Cli("plan-layouts", smallPath, keptOut, "--pack", "xx.no-such.pack");
        Check(kept.Exit == 1 && Str(Parse(kept.Out), "code") == "output_exists" && File.ReadAllBytes(keptOut).SequenceEqual(sentinel),
            "an existing output with an unknown --pack -> output_exists, the file byte-identical", D(kept));
        // SkillPackLookup.Load guards its id itself (callers check first): a non-id is Invalid "id", nothing read.
        foreach (var badId in new[] { "../x", "", "ar.caba/x", "..", "Ar.Caba" })
        {
            Bim.Cli.SkillPackException? lookupError = null;
            try { Bim.Cli.SkillPackLookup.Load(badId); }
            catch (Bim.Cli.SkillPackException error) { lookupError = error; }
            Check(lookupError is { Problem: Bim.Cli.SkillPackProblem.Invalid, Detail: "id" }, "SkillPackLookup.Load('" + badId + "') refuses Invalid with detail id",
                lookupError is null ? "<no refusal>" : lookupError.Problem + " " + lookupError.Detail);
        }

        // A scratch repository with its own skills/ (the CLI copied under it finds it as the repository).
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
        var fakeRepo = Path.Combine(scratch, "fake-repo");
        Directory.CreateDirectory(fakeRepo);
        File.WriteAllText(Path.Combine(fakeRepo, "global.json"), "{}");
        void FakePack(string folder, byte[] bytes) { Directory.CreateDirectory(Path.Combine(fakeRepo, "skills", folder)); File.WriteAllBytes(Path.Combine(fakeRepo, "skills", folder, "skill.json"), bytes); }
        static string Manifest1(string id, string rules) => "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"0.0.1\",\"kind\":\"code\",\"description\":\"test pack\",\"priority\":100,\"rules\":[" + rules + "]}";
        static string MinRule(string id, string severity, string room, string property, string threshold) =>
            "{\"id\":\"" + id + "\",\"severity\":\"" + severity + "\",\"target\":{\"room\":\"" + room + "\"},\"check\":{\"op\":\"min\",\"property\":\"" + property + "\"," + threshold + "},\"message\":\"m\"}";
        var strictText = Manifest1("zz.test.strict", MinRule("living-side", "error", "living*", "min_side_m", "\"value\":3.2") + "," + MinRule("living-area", "error", "living*", "area_m2", "\"value\":16")
            + "," + MinRule("living-area-2", "error", "living*", "area_m2", "\"value\":12") + "," + MinRule("bath-side", "error", "bath*", "min_side_m", "\"value\":1.25") + "," + MinRule("bed-area", "error", "bedroom*", "area_m2", "\"value\":8"));
        FakePack("zz.test.strict", Utf8(strictText));
        // The larger area minimum second (the group keeps the largest whatever the order) and a parameter threshold 1.25.
        var ascendingText = Manifest1("zz.test.ascending", MinRule("a1", "error", "living*", "area_m2", "\"value\":10") + "," + MinRule("a2", "error", "living*", "area_m2", "\"value\":14"));
        FakePack("zz.test.ascending", Utf8(ascendingText));
        var paramText = Manifest1("zz.test.param", MinRule("side", "error", "living*", "min_side_m", "\"parameter\":\"p\""))
            .Replace("\"rules\":[", "\"parameters\":[{\"name\":\"p\",\"type\":\"number\",\"default\":1.25}],\"rules\":[", StringComparison.Ordinal);
        FakePack("zz.test.param", Utf8(paramText));
        FakePack("zz.test.bad-json", Utf8("{"));
        FakePack("zz.test.mismatch", Utf8(Manifest1("zz.test.other", MinRule("r", "error", "living*", "area_m2", "\"value\":9"))));
        FakePack("zz.test.zero", Utf8(Manifest1("zz.test.zero", MinRule("ok", "error", "living*", "area_m2", "\"value\":9") + "," + MinRule("zero-side", "error", "office*", "min_side_m", "\"value\":0"))));
        FakePack("zz.test.over-parser", Utf8(Manifest1("zz.test.over-parser", MinRule("r", "error", "living*", "area_m2", "\"value\":9")).Replace("\"test pack\"", "\"" + new string('x', SkillParser.MaxInputChars) + "\"", StringComparison.Ordinal)));
        FakePack("zz.test.over-cap", overCap);
        Directory.CreateDirectory(Path.Combine(fakeRepo, "skills", "zz.test.empty-dir"));
        var fakeDll = CopyCli(Path.Combine(fakeRepo, "cli"));
        var strictPack = SkillParser.Parse(strictText);
        var strictRules = new[] { Rule("living*", 16, 3.2, "zz.test.strict living-side living-area living-area-2"), Rule("bath*", null, 1.25, "zz.test.strict bath-side") };
        Check(CoreRules(strictPack, smallBrief).SequenceEqual(strictRules), "a repository pack's rules (largest minimums per glob, rule ids in pack order) equal TypologyRules.From", Rules(CoreRules(strictPack, smallBrief)));
        var strictOut = Output();
        var pack8 = CliAt(fakeDll, null, "plan-layouts", smallPath, strictOut, "--alternatives", "2", "--pack", "zz.test.strict");
        var expectedStrict = ExpectedWith(smallBrief, 2, 0, strictRules);
        Check(pack8.Exit == 0 && Written(strictOut).SequenceEqual(expectedStrict) && Int(Parse(pack8.Out), "pack_rules") == 2,
            "a pack from the repository's skills/ (found from the CLI binary) plans with its rules, equal to the in-process run", D(pack8) + Sizes(Written(strictOut), expectedStrict));
        // Max of group with the larger value second.
        var ascendingRules = new[] { Rule("living*", 14, null, "zz.test.ascending a1 a2") };
        Check(CoreRules(SkillParser.Parse(ascendingText), smallBrief).SequenceEqual(ascendingRules), "a group keeps the largest area minimum when it comes second (TypologyRules.From)",
            Rules(CoreRules(SkillParser.Parse(ascendingText), smallBrief)));
        var ascendingOut = Output();
        var pack9 = CliAt(fakeDll, null, "plan-layouts", smallPath, ascendingOut, "--alternatives", "1", "--pack", "zz.test.ascending");
        var expectedAscending = ExpectedWith(smallBrief, 1, 0, ascendingRules);
        Check(pack9.Exit == 0 && Written(ascendingOut).SequenceEqual(expectedAscending) && Int(Parse(pack9.Out), "pack_rules") == 1,
            "--pack with the larger area minimum second plans with the largest (14 m2), equal to the in-process run", D(pack9) + Sizes(Written(ascendingOut), expectedAscending));
        // A parameter default 1.25 is traced as "(p 1.25)", invariantly under de_DE too.
        var paramRules = new[] { Rule("living*", null, 1.25, "zz.test.param side (p 1.25)") };
        Check(CoreRules(SkillParser.Parse(paramText), smallBrief).SequenceEqual(paramRules), "a parameter default 1.25 is traced as '(p 1.25)' like TypologyRules.From",
            Rules(CoreRules(SkillParser.Parse(paramText), smallBrief)));
        var expectedParam = ExpectedWith(smallBrief, 1, 0, paramRules);
        var paramOut = Output();
        var pack10 = CliAt(fakeDll, null, "plan-layouts", smallPath, paramOut, "--alternatives", "1", "--pack", "zz.test.param");
        Check(pack10.Exit == 0 && Written(paramOut).SequenceEqual(expectedParam) && Encoding.UTF8.GetString(Written(paramOut)).Contains("zz.test.param side (p 1.25)", StringComparison.Ordinal),
            "--pack with a parameter default 1.25 equals the in-process run and the file cites '(p 1.25)'", D(pack10) + Sizes(Written(paramOut), expectedParam));
        {
            var german = false;
            try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
            catch (CultureNotFoundException) { }
            if (!german) Console.WriteLine("SKIP plan-layouts: de-DE culture data unavailable (invariant globalization); --pack parameter culture check not run");
            else
            {
                var germanOut = Output();
                var pack11 = CliAt(fakeDll, new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" }, "plan-layouts", smallPath, germanOut, "--alternatives", "1", "--pack", "zz.test.param");
                Check(pack11.Exit == 0 && Written(germanOut).SequenceEqual(expectedParam) && pack11.Out == pack10.Out, "under de_DE the '(p 1.25)' trace and the bytes are invariant", D(pack11));
            }
        }
        // A pack minimum above a brief room's maximum is the planner's area_range_conflict: an infeasible set, written.
        const string capped = "{\"brief_version\":1,\"name\":\"Capped\",\"constraints\":{\"site\":{\"width_m\":6,\"depth_m\":8},\"levels\":1},"
            + "\"rooms\":[{\"id\":\"living\",\"name\":\"Living\",\"target_area_m2\":11,\"min_area_m2\":10,\"max_area_m2\":12},{\"id\":\"bath\",\"name\":\"Bath\",\"target_area_m2\":4,\"min_area_m2\":3}]}";
        var cappedBrief = BriefParser.Parse(capped);
        var cappedOut = Output();
        var pack12 = CliAt(fakeDll, null, "plan-layouts", Input("capped.json", capped), cappedOut, "--alternatives", "2", "--pack", "zz.test.strict");
        var expectedCapped = ExpectedWith(cappedBrief, 2, 0, strictRules);
        var cappedText = Encoding.UTF8.GetString(Written(cappedOut));
        Check(pack12.Exit == 0 && Str(Parse(pack12.Out), "set_status") == "infeasible" && Written(cappedOut).SequenceEqual(expectedCapped)
            && cappedText.Contains(PlanCodes.AreaRangeConflict, StringComparison.Ordinal) && cappedText.Contains("rules[0] zz.test.strict living-side living-area living-area-2", StringComparison.Ordinal),
            "a pack minimum (16 m2) above the brief maximum (12 m2) writes the infeasible set with area_range_conflict citing rules[0], equal to the in-process run",
            D(pack12) + Sizes(Written(cappedOut), expectedCapped) + " " + (cappedText.Length > 600 ? cappedText[..600] : cappedText));
        var plainFakeOut = Output();
        var plainFake = CliAt(fakeDll, null, "plan-layouts", smallPath, plainFakeOut, "--alternatives", "2");
        Check(plainFake.Exit == 0 && Written(plainFakeOut).SequenceEqual(Expected(smallBrief, 2, 0)), "without --pack the repository's packs are not read", D(plainFake));
        void FakeRefusal(string label, string pack, string code, string detail)
        {
            var output = Never();
            var result = CliAt(fakeDll, null, "plan-layouts", smallPath, output, "--alternatives", "1", "--pack", pack);
            var envelope = Parse(result.Out);
            Check(result.Exit == 1 && Str(envelope, "status") == "failed" && Str(envelope, "code") == code && Str(envelope, "detail") == detail && !File.Exists(output),
                label + " -> " + code + " (detail " + detail + "), exit 1, no output file", D(result));
        }
        FakeRefusal("a malformed pack manifest", "zz.test.bad-json", "plan_layouts_pack_invalid", SkillCodes.JsonInvalid);
        FakeRefusal("a manifest naming another pack id", "zz.test.mismatch", "plan_layouts_pack_invalid", "id");
        FakeRefusal("a manifest over the skill parser's character cap", "zz.test.over-parser", "plan_layouts_pack_invalid", SkillCodes.InputTooLarge);
        FakeRefusal("a manifest over the 8 MiB read cap", "zz.test.over-cap", "plan_layouts_pack_unavailable", "input_too_large");
        FakeRefusal("a pack folder without skill.json", "zz.test.empty-dir", "plan_layouts_pack_unavailable", "input_not_found");
        FakeRefusal("a pack with a zero side minimum (even on a room the brief lacks)", "zz.test.zero", "plan_layouts_pack_rule_invalid", TypologyRuleCodes.ThresholdInvalid + " at zz.test.zero/zero-side");

        // P7-15: --pack uses SkillPackLookup.Load(id, packageFirst: true) like generate-prototypes (P7-12).
        // A stray skills/ beside a binary folder that is not a package never takes over: <repo>/tests/out beside
        // <repo>/tests/skills, and <repo>/stray/cli beside <repo>/stray/skills without the VERSION and manifest.json markers
        // (its zz.test.strict is malformed), both read <repo>/skills.
        Directory.CreateDirectory(Path.Combine(fakeRepo, "tests", "skills"));
        var strayDll = CopyCli(Path.Combine(fakeRepo, "tests", "out"));
        var strayOut = Output();
        var stray = CliAt(strayDll, null, "plan-layouts", smallPath, strayOut, "--alternatives", "2", "--pack", "zz.test.strict");
        Check(stray.Exit == 0 && Written(strayOut).SequenceEqual(expectedStrict) && stray.Out == pack8.Out,
            "--pack from <repo>/tests/out beside an empty <repo>/tests/skills reads <repo>/skills (no stray package root): the repository run's bytes and stdout", D(stray));
        var unmarked = Path.Combine(fakeRepo, "stray");
        Directory.CreateDirectory(Path.Combine(unmarked, "skills", "zz.test.strict"));
        File.WriteAllText(Path.Combine(unmarked, "skills", "zz.test.strict", "skill.json"), "{");
        var unmarkedDll = CopyCli(Path.Combine(unmarked, "cli"));
        var unmarkedOut = Output();
        var unmarkedRun = CliAt(unmarkedDll, null, "plan-layouts", smallPath, unmarkedOut, "--alternatives", "2", "--pack", "zz.test.strict");
        Check(unmarkedRun.Exit == 0 && Written(unmarkedOut).SequenceEqual(expectedStrict) && unmarkedRun.Out == pack8.Out,
            "--pack from cli/ beside a skills/ without the package markers reads <repo>/skills: the repository run's bytes and stdout", D(unmarkedRun));
        // A package inside the repository (<repo>/dist: cli/, skills/, VERSION, manifest.json) is read before the repository:
        // its zz.test.mismatch is valid (the repository's names another id), its zz.test.package-only is absent from the
        // repository, and a pack only the repository has is not looked up there (no fallback once the package has skills/).
        var dist = Path.Combine(fakeRepo, "dist");
        var distDll = CopyCli(Path.Combine(dist, "cli"));
        File.WriteAllText(Path.Combine(dist, "VERSION"), "0.0.0-test\n");
        File.WriteAllText(Path.Combine(dist, "manifest.json"), "{}\n");
        foreach (var distId in new[] { "zz.test.mismatch", "zz.test.package-only" })
        {
            Directory.CreateDirectory(Path.Combine(dist, "skills", distId));
            File.WriteAllBytes(Path.Combine(dist, "skills", distId, "skill.json"), Utf8(strictText.Replace("\"id\":\"zz.test.strict\"", "\"id\":\"" + distId + "\"", StringComparison.Ordinal)));
            var distRules = new[] { Rule("living*", 16, 3.2, distId + " living-side living-area living-area-2"), Rule("bath*", null, 1.25, distId + " bath-side") };
            var distOut = Output();
            var distRun = CliAt(distDll, null, "plan-layouts", smallPath, distOut, "--alternatives", "2", "--pack", distId);
            var expectedDist = ExpectedWith(smallBrief, 2, 0, distRules);
            Check(distRun.Exit == 0 && Written(distOut).SequenceEqual(expectedDist) && Str(Parse(distRun.Out), "pack") == distId && Int(Parse(distRun.Out), "pack_rules") == 2,
                "--pack from a package inside a repository reads the package's skills/" + distId + " before the repository's (equal to the in-process run with its rules)",
                D(distRun) + Sizes(Written(distOut), expectedDist));
        }
        var distMissingOut = Never();
        var distMissing = CliAt(distDll, null, "plan-layouts", smallPath, distMissingOut, "--alternatives", "1", "--pack", "zz.test.strict");
        Check(distMissing.Exit == 1 && Str(Parse(distMissing.Out), "code") == "plan_layouts_pack_unavailable" && Str(Parse(distMissing.Out), "detail") == "input_not_found" && !File.Exists(distMissingOut),
            "--pack absent from the package's skills/ (present in the repository) -> plan_layouts_pack_unavailable (input_not_found), no file", D(distMissing));

        // A CLI copy outside any repository: --pack refuses, a run without it still plans.
        static bool UnderRepository(string directory)
        {
            for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "global.json"))) return true;
            return false;
        }
        var outsideBase = new[] { Environment.GetEnvironmentVariable("RUNNER_TEMP"), Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows() ? null : "/tmp" }
            .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !UnderRepository(candidate));
        if (outsideBase is null) Console.WriteLine("SKIP plan-layouts: no writable directory outside a repository for the copied CLI");
        else
        {
            var outsideRoot = Path.Combine(outsideBase, "bim-plan-layouts-outside-" + Guid.NewGuid().ToString("N"));
            try
            {
                var outsideDll = CopyCli(Path.Combine(outsideRoot, "cli"));
                var outsideOut = Never();
                var outside = CliAt(outsideDll, null, "plan-layouts", smallPath, outsideOut, "--alternatives", "1", "--pack", Caba);
                Check(outside.Exit == 1 && Str(Parse(outside.Out), "code") == "plan_layouts_pack_repository_not_found" && !File.Exists(outsideOut),
                    "--pack from a CLI copy outside any repository -> plan_layouts_pack_repository_not_found, exit 1, no file", D(outside) + " copy under " + outsideBase);
                var outsidePlainOut = Output();
                var outsidePlain = CliAt(outsideDll, null, "plan-layouts", smallPath, outsidePlainOut, "--alternatives", "1");
                Check(outsidePlain.Exit == 0 && Written(outsidePlainOut).SequenceEqual(Expected(smallBrief, 1, 0)), "without --pack a CLI copy outside any repository still plans", D(outsidePlain));
            }
            finally { try { Directory.Delete(outsideRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

            // P7-15: the release package layout (<package>/cli beside <package>/skills with VERSION and manifest.json, no
            // global.json anywhere above): --pack reads <package>/skills/<id>/skill.json and the run is byte-equal to the
            // repository run (bytes and stdout).
            var packageRoot = Path.Combine(outsideBase, "bim-plan-layouts-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                var packageDll = CopyCli(Path.Combine(packageRoot, "cli"));
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
                var packaged = CliAt(packageDll, null, "plan-layouts", smallPath, packagedOut, "--pack", Caba, "--alternatives", "2", "--seed", "3");
                Check(!UnderRepository(packageRoot) && packaged.Exit == 0 && Written(packagedOut).SequenceEqual(expectedPack) && packaged.Out == pack1.Out,
                    "--pack from a CLI in the package layout (cli/ beside skills/, outside any repository) writes the repository run's bytes and stdout",
                    D(packaged) + Sizes(Written(packagedOut), expectedPack) + " package under " + outsideBase);
                var packagedMissingOut = Never();
                var packagedMissing = CliAt(packageDll, null, "plan-layouts", smallPath, packagedMissingOut, "--alternatives", "1", "--pack", "xx.no-such.pack");
                Check(packagedMissing.Exit == 1 && Str(Parse(packagedMissing.Out), "code") == "plan_layouts_pack_unavailable" && Str(Parse(packagedMissing.Out), "detail") == "input_not_found"
                    && !File.Exists(packagedMissingOut), "--pack absent from the package's skills/ -> plan_layouts_pack_unavailable (input_not_found), no file", D(packagedMissing));
                Directory.Delete(Path.Combine(packageRoot, "skills"), true);
                var noSkillsOut = Never();
                var noSkills = CliAt(packageDll, null, "plan-layouts", smallPath, noSkillsOut, "--alternatives", "1", "--pack", Caba);
                Check(noSkills.Exit == 1 && Str(Parse(noSkills.Out), "code") == "plan_layouts_pack_repository_not_found" && !File.Exists(noSkillsOut),
                    "the package layout without its skills/ folder (outside any repository) -> plan_layouts_pack_repository_not_found, no file", D(noSkills));
            }
            finally { try { Directory.Delete(packageRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
