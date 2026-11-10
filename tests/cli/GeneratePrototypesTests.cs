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
internal static class GeneratePrototypesTests
{
    const string PackId = "ar.caba.codigo-edificacion";

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "generate-prototypes");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "generate-prototypes: " + name, detail);

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
            && usageDetail.Contains("SHA-256", StringComparison.Ordinal) && usageDetail.Contains("UUIDv8", StringComparison.Ordinal),
            "the usage text documents the verb and the deterministic default project id rule", D(usage));

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
        const string canonical = "{\"typology\":1,\"lot\":{\"width_m\":7,\"depth_m\":12},\"alternatives\":2,\"seed\":0,\"door_width_m\":0.9,\"weights\":null,"
            + "\"fixed_rooms\":null,\"fixed_walls\":null,\"pack\":\"ar.caba.codigo-edificacion\",\"revision\":0,\"circulation_weight\":0.15,\"facade_weight\":0,\"snap\":false}";
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
