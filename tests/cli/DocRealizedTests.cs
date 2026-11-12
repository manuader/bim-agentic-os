using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;

// P4-15 `doc-realized --record run.json --fixture ir.json --assignments a.json --document <UniqueId> --output realized.json`:
// the P4-13 realized map of an owner -Catalogue run record, written to a new file. Oracles, all against the real Release
// CLI process:
//   1. The output file is byte-identical to UTF-8(DocRealized.Canonical(DocRealized.FromRecord(fixture, record,
//      assignments, document))) computed in process; flag order, assignment member order and the document's case do not
//      change a byte; stdout is {"status":"succeeded","output","entries","bytes","sha256"}.
//   2. Never-overwrite: an existing output file or directory is refused (output_exists) and left untouched; a bad output
//      extension is refused before any input is read.
//   3. Every failure prints one {"status":"failed","code"[,"detail"]} envelope, exits 1 and leaves no output file:
//      doc_realized_usage, input_* (detail the input) for missing / oversized inputs, realized_fixture_invalid for a
//      fixture that is not a Project, and for every library refusal exactly the in-process DocRealizedException's code
//      and detail (bad record, bound exceeded, document mismatch, bad assignments, unknown step or source, ...).
//   4. The produced file is accepted by `doc-update --realized`: the plan it writes is byte-identical to
//      JsonSerializer.Serialize(DocUpdate.Plan(before, after, bindings, realized)) with the in-process realized map.
internal static class DocRealizedTests
{
    static readonly Provenance provenance = new("cli-doc-realized-tests", "unit");
    const string RecordDocument = "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9-0000abcd";
    sealed record S(string Key, bool Passed, bool Blocked, string? Status, string[] Created, string[]? Deleted = null);

    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("cli-doc-realized:" + name)).Take(16).ToArray());

    static string Uid(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    // A run record as the owner session writes it (members the builder does not read are present and ignored).
    static string Record(IEnumerable<S> steps, string document = RecordDocument, int schema = 1)
    {
        var list = steps.ToArray();
        var rows = list.Select((step, i) => new Dictionary<string, object?>
        {
            ["index"] = i + 1,
            ["group"] = "documentacion",
            ["key"] = step.Key,
            ["command"] = "doc.create",
            ["request"] = new { command = "doc.create", args = new { dry_run = false } },
            ["response"] = step.Status is null ? null : new Dictionary<string, object?>
            {
                ["status"] = step.Status, ["code"] = step.Status == "succeeded" ? "ok" : "refused",
                ["created"] = step.Created, ["modified"] = Array.Empty<string>(), ["deleted"] = step.Deleted ?? Array.Empty<string>(),
            },
            ["raw_response"] = null,
            ["passed"] = step.Passed,
            ["blocked"] = step.Blocked,
            ["reason"] = step.Passed ? null : "fallo",
        }).ToArray();
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema_version"] = schema, ["run"] = 1, ["document_unique_id"] = document, ["document_path"] = "C:/x/BIM-Agentic-Test.rvt",
            ["all_passed"] = list.All(step => step.Passed), ["steps"] = rows,
        });
    }

    static string MapJson(IEnumerable<KeyValuePair<string, string>> map) =>
        "{" + string.Join(",", map.Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + JsonSerializer.Serialize(pair.Value))) + "}";

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "doc-realized");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "doc-realized: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
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
        string Bytes(string name, byte[] content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllBytes(path, content);
            return path;
        }
        string Output(string name = "realized.json") => Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string Envelope(string code, string? detail) =>
            detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
        string[] Args(string record, string fixture, string assignments, string document, string output) =>
            new[] { "doc-realized", "--record", record, "--fixture", fixture, "--assignments", assignments, "--document", document, "--output", output };
        // Runs the verb expecting one exact failure envelope; the inputs keep their bytes and no output file appears.
        void ExpectFailure(string name, string code, string? detail, string output, params string[] arguments)
        {
            var inputs = arguments.Where(File.Exists).Where(path => path != output).Distinct().ToArray();
            var before = inputs.Select(File.ReadAllBytes).ToArray();
            var result = Cli(arguments);
            Check(result.Exit == 1 && result.Out == Envelope(code, detail), name + " -> " + Envelope(code, detail), D(result));
            Check(!File.Exists(output) && !Directory.Exists(output), name + ": no output file", D(result));
            Check(inputs.Select((path, i) => File.ReadAllBytes(path).AsSpan().SequenceEqual(before[i])).All(same => same), name + ": inputs unchanged", D(result));
        }

        // ---- fixture: one room with a door; every doc item and element assigned to its own catalogue step ----
        var projectId = Id("project"); var roomId = Id("room0"); var doorId = Id("door0");
        var elements = new List<Element>(RoomCompiler.Compile(projectId, 0, new RoomIntent(roomId, 4, 3, 2.8, 0.2, 0, 0, "m", provenance)).Elements);
        elements.Add(new(doorId, "door", new[] { 1.1, 0.1, 1.9, 0.1, 0, 2.1 }, new[] { RoomCompiler.Id(roomId, "south"), RoomCompiler.Id(roomId, "level") }, provenance));
        var fixture = new Project(projectId, 0, elements.ToArray());
        var set = DocDefaults.From(fixture);
        var docKeys = set.Views.Select(i => i.Key).Concat(set.Tags.Select(i => i.Key)).Concat(set.Dimensions.Select(i => i.Key))
            .Concat(set.Schedules.Select(i => i.Key)).Concat(set.Sheets.Select(i => i.Key)).Concat(set.Placements.Select(i => i.Key)).ToArray();
        var sources = docKeys.Select(DocRegeneration.ItemSource).Concat(fixture.Elements.Select(e => DocRegeneration.ElementSource(e.Id))).ToArray();
        var steps = new List<S> { new("q-levels", true, false, "succeeded", Array.Empty<string>()) };
        var assigned = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < sources.Length; i++)
        {
            var key = "doc-up-" + i.ToString(CultureInfo.InvariantCulture);
            steps.Add(new(key, true, false, "succeeded", new[] { Uid("created:" + sources[i]), Uid("extra:" + sources[i]) }));
            assigned.Add(new(sources[i], key));
        }
        steps.Add(new("doc-up-failed", false, false, "failed", Array.Empty<string>()));
        steps.Add(new("doc-up-blocked", false, true, null, Array.Empty<string>()));
        var recordText = Record(steps);
        var assignmentsText = MapJson(assigned);
        var fixtureText = JsonSerializer.Serialize(fixture);
        var recordPath = Input("run.json", recordText);
        var fixturePath = Input("ir.json", fixtureText);
        var assignmentsPath = Input("assignments.json", assignmentsText);
        Check(sources.Length > 4 && sources.Any(s => s.StartsWith("doc:", StringComparison.Ordinal)) && sources.Any(s => s.StartsWith("element:", StringComparison.Ordinal)),
            "fixture: doc items and elements to realize", sources.Length.ToString(CultureInfo.InvariantCulture));

        // In-process oracle: (code|detail) of a library refusal, or the canonical bytes.
        string? LibraryRefusal(Project f, string record, string assignments, string document)
        {
            try { DocRealized.Canonical(DocRealized.FromRecord(f, record, assignments, document)); return null; }
            catch (DocRealizedException error) { return error.Code + "|" + error.Detail; }
        }
        var realized = DocRealized.FromRecord(fixture, recordText, assignmentsText, RecordDocument);
        var expected = Utf8(DocRealized.Canonical(realized));
        Check(realized.Count == sources.Length, "fixture: the in-process map realizes every source", realized.Count.ToString(CultureInfo.InvariantCulture));

        // ---- 1. byte-equality with the in-process builder ----
        {
            var output = Output();
            var inputs = new[] { recordPath, fixturePath, assignmentsPath };
            var before = inputs.Select(File.ReadAllBytes).ToArray();
            var run = Cli(Args(recordPath, fixturePath, assignmentsPath, RecordDocument, output));
            var written = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
            Check(run.Exit == 0 && run.Err.Length == 0 && written.AsSpan().SequenceEqual(expected),
                "writes the map byte-identical to DocRealized.Canonical(DocRealized.FromRecord(...))", D(run));
            var summary = JsonSerializer.Serialize(new { status = "succeeded", output, entries = realized.Count, bytes = (long)expected.Length, sha256 = Convert.ToHexString(SHA256.HashData(expected)) });
            Check(run.Out == summary, "prints the success summary (output, entries, bytes, sha256)", D(run) + " expected " + summary);
            Check(inputs.Select((path, i) => File.ReadAllBytes(path).AsSpan().SequenceEqual(before[i])).All(same => same), "leaves its input files unchanged");

            // Any flag order, reversed assignment members, the document in upper case: the same bytes.
            var second = Output();
            var reversed = Input("assignments-reversed.json", MapJson(Enumerable.Reverse(assigned)));
            var again = Cli("doc-realized", "--output", second, "--document", RecordDocument.ToUpperInvariant(), "--assignments", reversed, "--fixture", fixturePath, "--record", recordPath);
            Check(again.Exit == 0 && File.Exists(second) && File.ReadAllBytes(second).AsSpan().SequenceEqual(expected),
                "flag order, assignment member order and document case do not change a byte", D(again));

            // A subset assignment: still the library's bytes.
            var subset = assigned.Where((_, i) => i % 2 == 0).ToArray();
            var subsetPath = Input("assignments-subset.json", MapJson(subset));
            var subsetExpected = Utf8(DocRealized.Canonical(DocRealized.FromRecord(fixture, recordText, MapJson(subset), RecordDocument)));
            var third = Output();
            var partial = Cli(Args(recordPath, fixturePath, subsetPath, RecordDocument, third));
            Check(partial.Exit == 0 && File.Exists(third) && File.ReadAllBytes(third).AsSpan().SequenceEqual(subsetExpected), "a partial assignment writes the library's bytes", D(partial));

            // ---- 4. doc-update --realized accepts the produced file ----
            var moved = fixture with { Elements = fixture.Elements.Select(e => e.Id == doorId ? e with { GeometryMetres = new[] { e.GeometryMetres[0] + 0.2, e.GeometryMetres[1], e.GeometryMetres[2] + 0.2, e.GeometryMetres[3], e.GeometryMetres[4], e.GeometryMetres[5] } } : e).ToArray() };
            var document = new RevitDocumentRef(@"C:\Repo\.bim\native-runs\p4-15\copy.rvt", RecordDocument);
            var bindings = new RevitBindings(document, moved.Elements.Where(e => e.Kind == "level").ToDictionary(e => e.Id, e => "Level " + e.Id.ToString("N")[..8]), Array.Empty<RevitWallTypeBinding>(), null, null);
            var bindingsJson = "{\"document\":{\"path\":" + JsonSerializer.Serialize(document.Path) + ",\"unique_id\":" + JsonSerializer.Serialize(document.UniqueId) + "},\"levels\":{"
                + string.Join(",", bindings.Levels.Select(pair => JsonSerializer.Serialize(pair.Key.ToString("D")) + ":" + JsonSerializer.Serialize(pair.Value))) + "}}";
            string planText;
            try { planText = JsonSerializer.Serialize(DocUpdate.Plan(fixture, moved, bindings, realized)); }
            catch (Exception error) { planText = ""; Check(false, "fixture: DocUpdate.Plan accepts the realized map in process", error.GetType().Name + ": " + error.Message); }
            var plan = Output("plan.json");
            var update = Cli("doc-update", "--before", fixturePath, "--after", Input("after.json", JsonSerializer.Serialize(moved)), "--realized", output,
                "--bindings", Input("bindings.json", bindingsJson), "--output", plan);
            Check(planText.Length > 0 && update.Exit == 0 && File.Exists(plan) && File.ReadAllBytes(plan).AsSpan().SequenceEqual(Utf8(planText)),
                "doc-update --realized <output> accepts the file and plans exactly DocUpdate.Plan(before, after, bindings, realized)", D(update));
            using (var planDocument = JsonDocument.Parse(planText.Length > 0 ? planText : "{}"))
                Check(planDocument.RootElement.TryGetProperty("Replacement", out var replacement) && replacement.GetProperty("Commands").GetProperty("Steps").GetArrayLength() > 0,
                    "fixture: the door move plans replacement steps against the realized map");

            // ---- 2. never-overwrite ----
            var existingBytes = Utf8("{\"sentinel\":true}");
            var existing = Bytes("existing.json", existingBytes);
            var stamp = File.GetLastWriteTimeUtc(existing);
            var refused = Cli(Args(recordPath, fixturePath, assignmentsPath, RecordDocument, existing));
            Check(refused.Exit == 1 && refused.Out == Envelope("output_exists", null) && File.ReadAllBytes(existing).AsSpan().SequenceEqual(existingBytes) && File.GetLastWriteTimeUtc(existing) == stamp,
                "an existing output file is refused (output_exists) and left byte- and mtime-identical", D(refused));
            var rerun = Cli(Args(recordPath, fixturePath, assignmentsPath, RecordDocument, output));
            Check(rerun.Exit == 1 && rerun.Out == Envelope("output_exists", null) && File.ReadAllBytes(output).AsSpan().SequenceEqual(expected),
                "a rerun onto its own output is refused and keeps the first bytes", D(rerun));
            var directory = Output("dir.json");
            Directory.CreateDirectory(directory);
            var dirRun = Cli(Args(recordPath, fixturePath, assignmentsPath, RecordDocument, directory));
            Check(dirRun.Exit == 1 && dirRun.Out == Envelope("output_exists", null) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any(),
                "an existing directory at the output path is refused (output_exists)", D(dirRun));
        }

        // ---- 3. typed envelopes ----
        var missing = Path.Combine(scratch, "does-not-exist.json");
        // Usage.
        { var o = Output(); ExpectFailure("no flags", "doc_realized_usage", null, o, "doc-realized"); }
        { var o = Output(); ExpectFailure("missing --document", "doc_realized_usage", null, o, "doc-realized", "--record", recordPath, "--fixture", fixturePath, "--assignments", assignmentsPath, "--output", o); }
        { var o = Output(); ExpectFailure("duplicate --record", "doc_realized_usage", null, o, "doc-realized", "--record", recordPath, "--record", recordPath, "--fixture", fixturePath, "--assignments", assignmentsPath, "--output", o); }
        { var o = Output(); ExpectFailure("unknown flag", "doc_realized_usage", null, o, "doc-realized", "--record", recordPath, "--fixture", fixturePath, "--assignments", assignmentsPath, "--document", RecordDocument, "--output", o, "--force", "yes"); }
        { var o = Output(); ExpectFailure("flag-shaped value", "doc_realized_usage", null, o, "doc-realized", "--record", "--fixture", fixturePath, "--assignments", assignmentsPath, "--document", RecordDocument, "--output", o, "--x"); }
        { var o = Output(); ExpectFailure("empty value", "doc_realized_usage", null, o, Args(recordPath, fixturePath, assignmentsPath, "", o)); }
        // Output path, checked before any input is read.
        { var o = Output("realized.txt"); ExpectFailure("output extension", "output_extension_unsupported", null, o, Args(missing, missing, missing, RecordDocument, o)); }
        { var o = Path.Combine(scratch, "no-such-dir", "realized.json"); ExpectFailure("output directory missing", "output_directory_not_found", null, o, Args(recordPath, fixturePath, assignmentsPath, RecordDocument, o)); }
        // Bounded inputs (8 MiB + 1 byte, missing), in the order record, fixture, assignments.
        var oversized = Bytes("over-cap.json", overCap);
        { var o = Output(); ExpectFailure("oversized record", "input_too_large", "record", o, Args(oversized, fixturePath, assignmentsPath, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("oversized fixture", "input_too_large", "fixture", o, Args(recordPath, oversized, assignmentsPath, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("oversized assignments", "input_too_large", "assignments", o, Args(recordPath, fixturePath, oversized, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("missing record", "input_not_found", "record", o, Args(missing, fixturePath, assignmentsPath, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("missing fixture", "input_not_found", "fixture", o, Args(recordPath, missing, assignmentsPath, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("missing assignments", "input_not_found", "assignments", o, Args(recordPath, fixturePath, missing, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("record is a directory", "input_not_regular_file", "record", o, Args(scratch, fixturePath, assignmentsPath, RecordDocument, o)); }
        // A fixture that is not a Project.
        { var o = Output(); ExpectFailure("fixture not JSON", "realized_fixture_invalid", "json", o, Args(recordPath, Input("bad-ir.json", "{not json"), assignmentsPath, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("fixture null", "realized_fixture_invalid", "json", o, Args(recordPath, Input("null-ir.json", "null"), assignmentsPath, RecordDocument, o)); }
        { var o = Output(); ExpectFailure("fixture null element", "realized_fixture_invalid", "fixture", o, Args(recordPath, Input("null-element-ir.json", "{\"Id\":\"" + projectId.ToString("D") + "\",\"Revision\":0,\"Elements\":[null]}"), assignmentsPath, RecordDocument, o)); }

        // Library refusals: the envelope carries exactly the in-process DocRealizedException's code and detail.
        void Library(string name, Project f, string fText, string record, string assignments, string document, string expectedCode)
        {
            var refusal = LibraryRefusal(f, record, assignments, document);
            Check(refusal is not null && refusal.StartsWith(expectedCode + "|", StringComparison.Ordinal), "fixture: in process, " + name + " is " + expectedCode, refusal ?? "accepted");
            if (refusal is null) return;
            var bar = refusal.IndexOf('|');
            var detail = refusal[(bar + 1)..];
            var o = Output();
            ExpectFailure(name, refusal[..bar], detail.Length == 0 ? null : detail, o,
                Args(Input("record.json", record), fText == fixtureText ? fixturePath : Input("fixture.json", fText), Input("assignments.json", assignments), document, o));
        }
        Library("record not JSON", fixture, fixtureText, "{oops", assignmentsText, RecordDocument, DocRealizedCodes.RecordInvalid);
        Library("record schema_version 2", fixture, fixtureText, Record(steps, schema: 2), assignmentsText, RecordDocument, DocRealizedCodes.RecordInvalid);
        Library("record over MaxSteps", fixture, fixtureText,
            Record(Enumerable.Range(0, DocRealized.MaxSteps + 1).Select(i => new S("s" + i.ToString(CultureInfo.InvariantCulture), true, false, "succeeded", Array.Empty<string>()))),
            assignmentsText, RecordDocument, DocRealizedCodes.BoundExceeded);
        Library("duplicate step key", fixture, fixtureText, Record(steps.Concat(new[] { steps[1] })), assignmentsText, RecordDocument, DocRealizedCodes.StepDuplicate);
        Library("other document", fixture, fixtureText, recordText, assignmentsText, "11111111-2222-3333-4444-555555555555-00000001", DocRealizedCodes.DocumentMismatch);
        Library("document not a UniqueId", fixture, fixtureText, recordText, assignmentsText, "not-a-unique-id", DocRealizedCodes.DocumentMismatch);
        Library("assignments not JSON", fixture, fixtureText, recordText, "[", RecordDocument, DocRealizedCodes.AssignmentsInvalid);
        Library("assignments not an object", fixture, fixtureText, recordText, "[]", RecordDocument, DocRealizedCodes.AssignmentsInvalid);
        Library("unknown step", fixture, fixtureText, recordText, MapJson(new[] { new KeyValuePair<string, string>(sources[0], "no-such-step") }), RecordDocument, DocRealizedCodes.StepUnknown);
        Library("failed step", fixture, fixtureText, recordText, MapJson(new[] { new KeyValuePair<string, string>(sources[0], "doc-up-failed") }), RecordDocument, DocRealizedCodes.StepNotSucceeded);
        Library("step created nothing", fixture, fixtureText, recordText, MapJson(new[] { new KeyValuePair<string, string>(sources[0], "q-levels") }), RecordDocument, DocRealizedCodes.StepNoCreated);
        Library("unknown source", fixture, fixtureText, recordText, MapJson(new[] { new KeyValuePair<string, string>("doc:no-such-item", "doc-up-0") }), RecordDocument, DocRealizedCodes.SourceUnknown);
        Library("invalid source", fixture, fixtureText, recordText, MapJson(new[] { new KeyValuePair<string, string>("wall:x", "doc-up-0") }), RecordDocument, DocRealizedCodes.SourceInvalid);
        Library("two sources on one step", fixture, fixtureText, recordText, MapJson(new[] { new KeyValuePair<string, string>(sources[0], "doc-up-0"), new KeyValuePair<string, string>(sources[1], "doc-up-0") }), RecordDocument, DocRealizedCodes.UniqueIdDuplicate);
        var noLevel = new Project(projectId, 0, fixture.Elements.Where(e => e.Kind != "level").ToArray());
        Library("fixture without a level", noLevel, JsonSerializer.Serialize(noLevel), recordText, assignmentsText, RecordDocument, DocRealizedCodes.FixtureInvalid);

        // ---- the verb's shape: bounded reads, the P4-13 builder, exclusive write, no transport ----
        var sourcePath = Path.Combine(root, "interfaces", "cli", "DocRealizedCommand.cs");
        Check(File.Exists(sourcePath), "interfaces/cli/DocRealizedCommand.cs exists");
        if (File.Exists(sourcePath))
        {
            var source = File.ReadAllText(sourcePath);
            Check(source.Contains("BoundedInput.ReadText(", StringComparison.Ordinal) && source.Contains("DocRealized.FromRecord(", StringComparison.Ordinal)
                && source.Contains("DocRealized.Canonical(", StringComparison.Ordinal) && source.Contains("GltfExporter.WriteNew(", StringComparison.Ordinal)
                && new[] { "Transport", "NamedPipe", "File.", "FileStream", "FileMode.", "Directory.", "ProjectStore" }.All(api => !source.Contains(api, StringComparison.Ordinal)),
                "doc-realized reads only through BoundedInput, builds with DocRealized.FromRecord/Canonical, writes only through GltfExporter.WriteNew and never runs a transport");
        }
        Check(Cli("no-such-verb").Out.Contains("doc-realized --record", StringComparison.Ordinal), "default usage lists doc-realized");
    }
}
