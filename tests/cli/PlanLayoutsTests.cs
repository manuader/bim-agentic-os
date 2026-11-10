using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-11 `plan-layouts brief.json output.json [--alternatives n] [--seed s]`: a P6-03 brief planned into ranked
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
internal static class PlanLayoutsTests
{
    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "plan-layouts");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "plan-layouts: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
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
        Check(usageDetail.Contains("plan-layouts brief.json output.json [--alternatives n] [--seed s]", StringComparison.Ordinal) && usageDetail.Contains("1..64", StringComparison.Ordinal),
            "the usage text documents the verb, its options and the alternatives bound", usageDetail);

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
    }
}
