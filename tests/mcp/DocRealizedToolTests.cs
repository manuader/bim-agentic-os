using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bim.Cli;
using Bim.Core;

// P4-21 doc_realized: the read-only MCP face of the P4-15 CLI verb `doc-realized --record run.json --fixture ir.json
// --assignments a.json --document <UniqueId> --output realized.json` (interfaces/cli/DocRealizedCommand.cs, linked into
// the server, no copied reader or parser).
// Oracles:
//   1. On success the tool text is the canonical realized map: its UTF-8 bytes equal the file the real Release CLI process
//      writes for the same inputs and UTF-8(DocRealized.Canonical(DocRealized.FromRecord(...))) computed in process
//      (full assignment, reversed members, upper-case document, a subset, BOM-prefixed record, a 9 MiB record).
//   2. Every refusal is isError and byte-equal to the CLI stdout line for the same inputs (input_* with the input named,
//      realized_fixture_invalid, every library realized_* code with its detail, the CLI precedence).
//   3. A forced exception inside the guarded library work (DocRealizedCommand.GuardedWorkProbe) is the CLI's typed
//      doc_realized_internal_error envelope with the IntentPlan diagnostic on stderr once, never McpServer's generic failure.
//   4. It writes nothing (the input directory is unchanged; it takes no output path; a Console.Out redirected to a
//      StringWriter around every in-process call stays empty), leaves the exit code and Console.Out untouched;
//      tools/list labels it permission level read with readOnlyHint true only and the schema requires exactly record,
//      fixture, assignments, document (strings, nothing else).
//   5. MCP-only path guard: doc_realized_path_invalid {detail the input}; the document is judged by the library like the
//      CLI's; missing / mistyped / extra arguments are -32602.
//   6. The real Release Bim.Mcp.dll answers the same bytes over stdio.
//   7. Static (comments stripped by DocUpdateLinkTests.CodeOf, literals kept): the tool calls
//      DocRealizedCommand.DryRealized and contains no reader, parser, builder or file access of its own; the CLI verb and
//      DryRealized share one Build (whitespace-insensitive; read order record, fixture, assignments, then the parse);
//      the MCP projects link the shared CLI source but not the doc-realized write path (DocRealizedWrite.cs, found by
//      Include, wildcard or Link through DocUpdateLinkTests.Compiled), and no source they compile calls
//      DocRealizedCommand.Run / Execute / Fail.
internal static class DocRealizedToolTests
{
    const string ToolName = "doc_realized";
    const string PathInvalid = "doc_realized_path_invalid";
    static readonly Provenance provenance = new("mcp-doc-realized-tests", "unit");
    const string RecordDocument = "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9-0000abcd";
    sealed record S(string Key, bool Passed, bool Blocked, string? Status, string[] Created, string[]? Deleted = null);

    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("mcp-doc-realized:" + name)).Take(16).ToArray());

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

    public static void Run(string root, string work, string dotnet, string cliDll, Func<string, JsonObject> call, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "doc_realized: " + name);
        var scratch = Path.Combine(work, "doc-realized-mcp");
        var inputs = Path.Combine(scratch, "in");
        var outputs = Path.Combine(scratch, "cli-out");
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(outputs);
        var counter = 0;
        string Next() => (++counter).ToString(CultureInfo.InvariantCulture);
        string Input(string name, string content)
        {
            var path = Path.Combine(inputs, Next() + "-" + name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }
        string Bytes(string name, byte[] content)
        {
            var path = Path.Combine(inputs, Next() + "-" + name);
            File.WriteAllBytes(path, content);
            return path;
        }
        string Listing() => string.Join("\n", Directory.GetFileSystemEntries(inputs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(e => Path.GetRelativePath(inputs, e) + (File.Exists(e) ? ":" + Stamp(e) + ":" + File.GetLastWriteTimeUtc(e).Ticks.ToString(CultureInfo.InvariantCulture) : "/")));
        // The content hash of a small file; the length of a large one (the multi-MiB bound fixtures), with its mtime.
        static string Stamp(string path) => new FileInfo(path).Length > 1024 * 1024 ? "len" + new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) : Hex(File.ReadAllBytes(path));
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static string Short(string text) => text.Length > 400 ? text[..400] + "..." : text;
        static string Failed(string code, string? detail = null) =>
            detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });

        // The real CLI process with a fresh output path: (exit, stdout line, written bytes or null).
        (int Exit, string Out, byte[]? File) Cli(string record, string fixture, string assignments, string document)
        {
            var output = Path.Combine(outputs, Next() + "-realized.json");
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in new[] { "doc-realized", "--record", record, "--fixture", fixture, "--assignments", assignments, "--document", document, "--output", output }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new Exception("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("CLI timed out: doc-realized " + record); }
            process.WaitForExit();
            _ = stderr.Result;
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, File.Exists(output) ? File.ReadAllBytes(output) : null);
        }
        static JsonObject Arguments(string record, string fixture, string assignments, string document) =>
            new() { ["record"] = record, ["fixture"] = fixture, ["assignments"] = assignments, ["document"] = document };
        static string ToolLine(JsonObject arguments) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 24, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = ToolName, ["arguments"] = arguments } }.ToJsonString();
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        static int? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<int>();

        var exitBefore = Environment.ExitCode;
        // One MCP call: the response, with "nothing written, exit code untouched" checked around it. P4-23: the call
        // runs with Console.Out redirected to a StringWriter that must stay empty (success and refusal alike), so a
        // stray Console.Write in the shared path fails here, not only a swapped writer; the installed writer must still
        // be in place when the call returns, and the original writer is restored.
        JsonObject Call(string label, JsonObject arguments)
        {
            var before = Listing();
            var stdout = Console.Out;
            var captured = new StringWriter();
            JsonObject response;
            bool same;
            Console.SetOut(captured);
            // Console.SetOut installs a synchronized wrapper around the writer, so the call must leave that wrapper in place.
            var installed = Console.Out;
            try { response = call(ToolLine(arguments)); same = ReferenceEquals(Console.Out, installed); }
            finally { Console.SetOut(stdout); }
            Check(Listing() == before && Environment.ExitCode == exitBefore && same && captured.ToString().Length == 0,
                label + ": writes nothing (no file, nothing on a redirected Console.Out), exit code and Console.Out untouched | " + Short(captured.ToString()));
            return response;
        }

        // ---- 4. tools/list ----
        var listed = call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }.ToJsonString())["result"]!["tools"]!.AsArray();
        var tool = listed.SingleOrDefault(t => t!["name"]!.GetValue<string>() == ToolName);
        Check(tool is not null && tool["description"]!.GetValue<string>().Contains("doc-realized", StringComparison.Ordinal)
            && tool["description"]!.GetValue<string>().Contains("read-only", StringComparison.OrdinalIgnoreCase), "listed once, read-only, naming the CLI verb doc-realized");
        Check(tool!["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>() == "read" && tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true
            && tool["annotations"]!.AsObject().Count == 1, "lists _meta permission_level read and annotations readOnlyHint true only");
        var properties = tool["inputSchema"]!["properties"]!.AsObject();
        Check(string.Join(",", tool["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>())) == "record,fixture,assignments,document"
            && string.Join(",", properties.Select(p => p.Key)) == "record,fixture,assignments,document"
            && properties.All(p => p.Value!["type"]!.GetValue<string>() == "string")
            && tool["inputSchema"]!["additionalProperties"]!.GetValue<bool>() == false, "schema: string record, fixture, assignments, document all required, nothing else");

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
            "fixture: doc items and elements to realize");
        var expected = Utf8(DocRealized.Canonical(DocRealized.FromRecord(fixture, recordText, assignmentsText, RecordDocument)));

        // ---- 1. maps: CLI output file == tool text == in-process builder ----
        void Maps(string name, string record, string assignments, string document, byte[] library)
        {
            var cli = Cli(record, fixturePath, assignments, document);
            var response = Call(name, Arguments(record, fixturePath, assignments, document));
            Check(cli.Exit == 0 && cli.File is not null && cli.File.AsSpan().SequenceEqual(library), name + ": the CLI writes the library map | exit " + cli.Exit + " " + Short(cli.Out));
            Check(!IsError(response) && Utf8(Text(response)).AsSpan().SequenceEqual(library) && cli.File is not null && Utf8(Text(response)).AsSpan().SequenceEqual(cli.File),
                name + ": the tool text is byte-equal to the CLI output file and DocRealized.Canonical(FromRecord) | " + Short(Text(response)));
        }
        Maps("full assignment", recordPath, assignmentsPath, RecordDocument, expected);
        Maps("reversed assignment members", recordPath, Input("assignments-reversed.json", MapJson(Enumerable.Reverse(assigned))), RecordDocument, expected);
        Maps("upper-case document", recordPath, assignmentsPath, RecordDocument.ToUpperInvariant(), expected);
        var subset = assigned.Where((_, i) => i % 2 == 0).ToArray();
        Maps("a subset assignment", recordPath, Input("assignments-subset.json", MapJson(subset)), RecordDocument,
            Utf8(DocRealized.Canonical(DocRealized.FromRecord(fixture, recordText, MapJson(subset), RecordDocument))));
        Maps("a BOM-prefixed record", Bytes("record-bom.json", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(recordText)).ToArray()), assignmentsPath, RecordDocument, expected);
        {
            // A schema-valid record padded in an ignored member to 9 MiB: over the CLI's 8 MiB cap, read to MaxRecordBytes.
            const string member = "\"raw_response\":null";
            var at = recordText.IndexOf(member, StringComparison.Ordinal);
            var fill = 9 * 1024 * 1024 - Encoding.UTF8.GetByteCount(recordText) + member.Length - "\"raw_response\":\"\"".Length;
            Maps("a 9 MiB record", Input("record-9mib.json", recordText[..at] + "\"raw_response\":\"" + new string('x', fill) + "\"" + recordText[(at + member.Length)..]), assignmentsPath, RecordDocument, expected);
        }

        // ---- 2. refusals: tool text == CLI stdout line, isError, no file ----
        void Refused(string name, string expectedLine, string? r = null, string? f = null, string? a = null, string document = RecordDocument)
        {
            var rp = r ?? recordPath; var fp = f ?? fixturePath; var ap = a ?? assignmentsPath;
            var cli = Cli(rp, fp, ap, document);
            var response = Call(name, Arguments(rp, fp, ap, document));
            Check(cli.Exit == 1 && cli.File is null && cli.Out == expectedLine, name + ": the CLI refuses with " + expectedLine + " | " + Short(cli.Out));
            Check(IsError(response) && Text(response) == cli.Out, name + ": the tool text is the CLI refusal line, isError | " + Short(Text(response)));
        }
        var missing = Path.Combine(inputs, "absent.json");
        Refused("a missing record", Failed("input_not_found", "record"), r: missing);
        Refused("a missing fixture", Failed("input_not_found", "fixture"), f: missing);
        Refused("a missing assignments file", Failed("input_not_found", "assignments"), a: missing);
        Refused("a missing record and fixture (record first)", Failed("input_not_found", "record"), r: missing, f: missing);
        Refused("a missing fixture and assignments file (fixture first)", Failed("input_not_found", "fixture"), f: missing, a: missing);
        Refused("a fixture that is not JSON and a missing assignments file (every input read before the fixture parse)", Failed("input_not_found", "assignments"),
            f: Input("bad-ir-first.json", "{not json"), a: missing);
        Check(!File.Exists(missing), "creates no missing input");
        var directory = Path.Combine(inputs, Next() + "-a-directory.json");
        Directory.CreateDirectory(directory);
        Refused("a directory as record", Failed("input_not_regular_file", "record"), r: directory);
        Refused("a fixture over 8 MiB", Failed("input_too_large", "fixture"), f: Input("huge-ir.json", new string(' ', 8 * 1024 * 1024 + 1)));
        Refused("assignments over DocRealized.MaxRecordBytes", Failed("input_too_large", "assignments"), a: Input("huge-assignments.json", new string(' ', DocRealized.MaxRecordBytes + 1)));
        Refused("a fixture that is not JSON", Failed(DocRealizedCodes.FixtureInvalid, "json"), f: Input("bad-ir.json", "{not json"));
        Refused("a null fixture", Failed(DocRealizedCodes.FixtureInvalid, "json"), f: Input("null-ir.json", "null"));
        Refused("a fixture with a null element", Failed(DocRealizedCodes.FixtureInvalid, "fixture"), f: Input("null-element-ir.json", "{\"Id\":\"" + projectId.ToString("D") + "\",\"Revision\":0,\"Elements\":[null]}"));
        Refused("a bad record and a fixture that is not JSON (fixture parse first)", Failed(DocRealizedCodes.FixtureInvalid, "json"), r: Input("bad-record.json", "{oops"), f: Input("bad-ir2.json", "{not json"));

        // Library refusals: the code and detail the in-process DocRealizedException carries.
        static string LibraryLine(Project f, string record, string assignments, string document)
        {
            try { DocRealized.Canonical(DocRealized.FromRecord(f, record, assignments, document)); return "succeeded"; }
            catch (DocRealizedException error) { return Failed(error.Code, error.Detail); }
        }
        void Library(string name, string code, string record, string assignments, string document = RecordDocument, Project? f = null)
        {
            var line = LibraryLine(f ?? fixture, record, assignments, document);
            Check(line.StartsWith("{\"status\":\"failed\",\"code\":\"" + code + "\"", StringComparison.Ordinal), "fixture: the library refuses " + name + " as " + code + " | " + line);
            Refused(name, line, Input("record.json", record), f is null ? fixturePath : Input("fixture.json", JsonSerializer.Serialize(f)), Input("assignments.json", assignments), document);
        }
        Library("a record that is not JSON", DocRealizedCodes.RecordInvalid, "{oops", assignmentsText);
        Library("a record of schema_version 2", DocRealizedCodes.RecordInvalid, Record(steps, schema: 2), assignmentsText);
        Library("a record over MaxSteps", DocRealizedCodes.BoundExceeded,
            Record(Enumerable.Range(0, DocRealized.MaxSteps + 1).Select(i => new S("s" + i.ToString(CultureInfo.InvariantCulture), true, false, "succeeded", Array.Empty<string>()))), assignmentsText);
        Library("a duplicate step key", DocRealizedCodes.StepDuplicate, Record(steps.Concat(new[] { steps[1] })), assignmentsText);
        Library("another document", DocRealizedCodes.DocumentMismatch, recordText, assignmentsText, "11111111-2222-3333-4444-555555555555-00000001");
        Library("a document that is not a UniqueId", DocRealizedCodes.DocumentMismatch, recordText, assignmentsText, "not-a-unique-id");
        Library("assignments that are not JSON", DocRealizedCodes.AssignmentsInvalid, recordText, "[");
        Library("assignments that are an array", DocRealizedCodes.AssignmentsInvalid, recordText, "[]");
        Library("an unknown step", DocRealizedCodes.StepUnknown, recordText, MapJson(new[] { new KeyValuePair<string, string>(sources[0], "no-such-step") }));
        Library("a failed step", DocRealizedCodes.StepNotSucceeded, recordText, MapJson(new[] { new KeyValuePair<string, string>(sources[0], "doc-up-failed") }));
        Library("a step that created nothing", DocRealizedCodes.StepNoCreated, recordText, MapJson(new[] { new KeyValuePair<string, string>(sources[0], "q-levels") }));
        Library("an unknown source", DocRealizedCodes.SourceUnknown, recordText, MapJson(new[] { new KeyValuePair<string, string>("doc:no-such-item", "doc-up-0") }));
        Library("an invalid source", DocRealizedCodes.SourceInvalid, recordText, MapJson(new[] { new KeyValuePair<string, string>("wall:x", "doc-up-0") }));
        Library("two sources on one step", DocRealizedCodes.UniqueIdDuplicate, recordText,
            MapJson(new[] { new KeyValuePair<string, string>(sources[0], "doc-up-0"), new KeyValuePair<string, string>(sources[1], "doc-up-0") }));
        Library("a later step deletes an assigned UniqueId", DocRealizedCodes.UniqueIdDeleted,
            Record(steps.Concat(new[] { new S("doc-up-delete", true, false, "succeeded", Array.Empty<string>(), new[] { Uid("created:" + sources[0]) }) })), assignmentsText);
        var noLevel = new Project(projectId, 0, fixture.Elements.Where(e => e.Kind != "level").ToArray());
        Library("a fixture without a level", DocRealizedCodes.FixtureInvalid, recordText, assignmentsText, f: noLevel);
        Library("a bad record ahead of a fixture without a level (library order)", DocRealizedCodes.RecordInvalid, "{oops", assignmentsText, f: noLevel);

        // ---- 3. the guarded library work: a forced exception is the CLI's typed internal error envelope ----
        {
            var failure = new InvalidOperationException("boom doc_realized");
            var errors = new StringWriter();
            var stderr = Console.Error;
            JsonObject forced;
            Check(DocRealizedCommand.GuardedWorkProbe is null, "fixture: the guarded-work test seam is null unless a test sets it");
            DocRealizedCommand.GuardedWorkProbe = () => throw failure;
            Console.SetError(errors);
            try { forced = Call("a forced internal error", Arguments(recordPath, fixturePath, assignmentsPath, RecordDocument)); }
            finally { Console.SetError(stderr); DocRealizedCommand.GuardedWorkProbe = null; }
            Check(IsError(forced) && Text(forced) == Failed("doc_realized_internal_error") && Text(forced) == IntentPlan.Internal("doc-realized", failure).Line,
                "a forced exception in the guarded library work is the CLI's doc_realized_internal_error envelope, not a generic failure | " + Short(Text(forced)));
            Check(errors.ToString() == IntentPlan.InternalDiagnostic("doc-realized", failure) + Environment.NewLine,
                "the forced internal error writes the CLI's IntentPlan diagnostic to stderr once | " + Short(errors.ToString()));
            var after = Call("after the forced internal error", Arguments(recordPath, fixturePath, assignmentsPath, RecordDocument));
            Check(!IsError(after) && Utf8(Text(after)).AsSpan().SequenceEqual(expected), "the seam is reset: the next call answers the map");
        }

        // ---- 4. read-only: no output argument ----
        foreach (var extra in new[] { "output", "force" })
        {
            var arguments = Arguments(recordPath, fixturePath, assignmentsPath, RecordDocument);
            arguments[extra] = Path.Combine(inputs, "never.json");
            Check(ErrorCode(call(ToolLine(arguments))) == -32602, "takes no " + extra + " argument (-32602)");
        }
        Check(!File.Exists(Path.Combine(inputs, "never.json")), "never writes an output file");

        // ---- 5. MCP-only path guard, the document, and argument errors ----
        foreach (var parameter in new[] { "record", "fixture", "assignments" })
        {
            foreach (var (label, bad) in new[] { ("empty", ""), ("blank", "   "), ("with NUL", "a\0b"), ("of 5000 characters", new string('x', 5000)) })
            {
                var arguments = Arguments(recordPath, fixturePath, assignmentsPath, RecordDocument);
                arguments[parameter] = bad;
                var response = Call(parameter + " " + label, arguments);
                Check(IsError(response) && Text(response) == Failed(PathInvalid, parameter), "refuses a " + parameter + " path " + label + " as doc_realized_path_invalid (" + parameter + ") | " + Short(Text(response)));
            }
        }
        {
            var several = Call("several bad paths", Arguments("", fixturePath, "", RecordDocument));
            Check(IsError(several) && Text(several) == Failed(PathInvalid, "record"), "several bad paths: the first in record, fixture, assignments order is reported");
            var guarded = Call("bad assignments path before a missing record", Arguments(missing, fixturePath, "\0", RecordDocument));
            Check(IsError(guarded) && Text(guarded) == Failed(PathInvalid, "assignments"), "the path guard runs before any input is read");
            var surrogate = call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"doc_realized\",\"arguments\":{\"record\":\"\\ud800\",\"fixture\":" + JsonSerializer.Serialize(fixturePath)
                + ",\"assignments\":" + JsonSerializer.Serialize(assignmentsPath) + ",\"document\":" + JsonSerializer.Serialize(RecordDocument) + "}}}");
            Check(IsError(surrogate) && Text(surrogate) == Failed(PathInvalid, "record") && Environment.ExitCode == exitBefore, "a lone-surrogate record path is doc_realized_path_invalid, never a crash | " + Short(Text(surrogate)));
        }
        {
            // The document is not a path: the library judges it, as on the CLI (an empty value is the CLI's usage refusal,
            // which has no MCP form; the library refuses it as realized_document_mismatch "expected").
            var empty = Call("an empty document", Arguments(recordPath, fixturePath, assignmentsPath, ""));
            Check(IsError(empty) && Text(empty) == LibraryLine(fixture, recordText, assignmentsText, "") && Text(empty) == Failed(DocRealizedCodes.DocumentMismatch, "expected"),
                "an empty document is the library's realized_document_mismatch (expected) | " + Short(Text(empty)));
            var lone = call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"doc_realized\",\"arguments\":{\"record\":" + JsonSerializer.Serialize(recordPath)
                + ",\"fixture\":" + JsonSerializer.Serialize(fixturePath) + ",\"assignments\":" + JsonSerializer.Serialize(assignmentsPath) + ",\"document\":\"\\ud800\"}}}");
            Check(IsError(lone) && Text(lone) == Failed(DocRealizedCodes.DocumentMismatch, "expected"), "a lone-surrogate document is realized_document_mismatch (expected), never a crash | " + Short(Text(lone)));
        }
        foreach (var parameter in new[] { "record", "fixture", "assignments", "document" })
        {
            var arguments = Arguments(recordPath, fixturePath, assignmentsPath, RecordDocument);
            arguments.Remove(parameter);
            Check(ErrorCode(call(ToolLine(arguments))) == -32602, "without " + parameter + " returns -32602");
            var mistyped = Arguments(recordPath, fixturePath, assignmentsPath, RecordDocument);
            mistyped[parameter] = 1;
            Check(ErrorCode(call(ToolLine(mistyped))) == -32602, "a non-string " + parameter + " returns -32602");
        }
        Check(ErrorCode(call(ToolLine(new JsonObject { ["record"] = JsonNode.Parse(recordText), ["fixture"] = fixturePath, ["assignments"] = assignmentsPath, ["document"] = RecordDocument }))) == -32602,
            "takes no inline record object (-32602)");

        // ---- 6. the real Release Bim.Mcp.dll over stdio ----
        var processOut = mcpProcess(ToolLine(Arguments(recordPath, fixturePath, assignmentsPath, RecordDocument)) + "\n");
        var processResponse = JsonNode.Parse(processOut.Split('\n')[0])!.AsObject();
        Check(!IsError(processResponse) && Utf8(Text(processResponse)).AsSpan().SequenceEqual(expected), "the real Bim.Mcp.dll process returns the map bytes | " + Short(processOut));

        // ---- 7. static: the shared reader, parser and builder, linked, not copied ----
        // P4-23: the literal-aware comment stripper shared with the doc_update link check ("//" in a string hides nothing).
        static string CodeOf(string text) => DocUpdateLinkTests.CodeOf(text);
        var toolPath = Path.Combine(root, "interfaces", "mcp", "DocRealizedTool.cs");
        Check(File.Exists(toolPath), "interfaces/mcp/DocRealizedTool.cs exists");
        var toolCode = File.Exists(toolPath) ? CodeOf(File.ReadAllText(toolPath)) : "";
        Check(toolCode.Contains("DocRealizedCommand.DryRealized(", StringComparison.Ordinal), "the tool runs the CLI verb's shared DocRealizedCommand.DryRealized");
        var inlined = new[] { "JsonDocument", "Utf8JsonReader", "JsonSerializer.Deserialize", "DocRealized.", "BoundedInput", "File.", "FileStream", "Directory.", "GltfExporter", "StreamReader",
            "Console.SetOut", "Console.Out", "Environment.ExitCode", "DocRealizedCommand.Run(", "GuardedWorkProbe" }
            .Where(api => toolCode.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(inlined.Length == 0, "the tool inlines no reader, parser, builder, file access or Console capture [" + string.Join(", ", inlined) + "]");
        var owners = Directory.GetFiles(Path.Combine(root, "interfaces", "mcp"), "*.cs")
            .Where(f => new[] { "DocRealized.FromRecord(", "DocRealized.Build(", "DocRealized.Canonical(", "DocRealizedCommand.Run(" }.Any(api => CodeOf(File.ReadAllText(f)).Contains(api, StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f)).ToArray();
        Check(owners.Length == 0, "no interfaces/mcp source builds a realized map or runs the writing verb itself [" + string.Join(", ", owners) + "]");
        // P4-23: formatting-insensitive. Compact is the comment-free code with every whitespace run removed, so the
        // checks below hold however the calls are wrapped or spaced.
        static string Compact(string code) => Regex.Replace(code, @"\s+", "");
        static int Count(string compact, string fragment) => compact.Split(fragment).Length - 1;
        var commandPath = Path.Combine(root, "interfaces", "cli", "DocRealizedCommand.cs");
        var writePath = Path.Combine(root, "interfaces", "cli", "DocRealizedWrite.cs");
        Check(File.Exists(writePath), "interfaces/cli/DocRealizedWrite.cs holds the verb's write path (split from the shared DocRealizedCommand.cs)");
        var command = Compact(CodeOf(File.ReadAllText(commandPath)));
        var write = File.Exists(writePath) ? Compact(CodeOf(File.ReadAllText(writePath))) : "";
        Check(Count(command, ")Build(stringrecordPath,stringfixturePath,stringassignmentsPath,stringdocument)") == 1
            && Count(command + write, "Build(") == 3
            && Count(command, "Build(recordPath,fixturePath,assignmentsPath,document)") == 1
            && Count(write, "Build(values[\"--record\"],values[\"--fixture\"],values[\"--assignments\"],values[\"--document\"])") == 1
            && Count(command, "DocRealized.FromRecord(") == 1 && Count(command, "DocRealized.Canonical(") == 1
            && Count(write, "DocRealized.") == 0,
            "the CLI verb (DocRealizedWrite.cs) and DryRealized share one Build in DocRealizedCommand.cs, which reads, parses and builds the map in one place");
        // Precedence inside Build: record, fixture, assignments read, then the fixture parsed, then the guarded library work.
        var reads = new[] { "Read(recordPath,\"record\",", "Read(fixturePath,\"fixture\",", "Read(assignmentsPath,\"assignments\",", "ParseFixture(fixtureText)", "DocRealized.FromRecord(" }
            .Select(fragment => command.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Check(reads.All(at => at >= 0) && reads.Zip(reads.Skip(1)).All(pair => pair.First < pair.Second),
            "Build reads record, fixture, then assignments, then parses the fixture, then builds [" + string.Join(", ", reads) + "]");
        // The output path is judged before Build reads anything, and the write follows the build (the verb's part only).
        var order = new[] { "GltfExportCommand.OutputProblem(output,", "Build(values[", "GltfExporter.WriteNew(bytes,output)", "Console.WriteLine(line)" }
            .Select(fragment => write.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Check(order.All(at => at >= 0) && order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second),
            "DocRealizedWrite.cs: output path, then Build, then the exclusive write, then the success line [" + string.Join(", ", order) + "]");
        var writeApis = new[] { "GltfExporter", "GltfExportCommand", "WriteNew", "Console.WriteLine", "Console.Write(", "Console.Out", "Environment.ExitCode", "File.", "FileStream", "Directory." }
            .Where(api => command.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(writeApis.Length == 0, "the shared DocRealizedCommand.cs has no write path, stdout or exit code [" + string.Join(", ", writeApis) + "]");
        // The link check: every Compile item (each ';' entry, '\' or '/', wildcards expanded, Link) of the two MCP projects.
        var forbiddenUse = new Regex(@"\bDocRealizedCommand\s*\.\s*(?:Run|Execute|Fail)\b", RegexOptions.CultureInvariant);
        static bool IsWrite(string? path) => path is not null
            && string.Equals(path.Replace('\\', '/').Split('/')[^1], "DocRealizedWrite.cs", StringComparison.OrdinalIgnoreCase);
        string[] WriteLinks(string projectXml, string projectDir) => DocUpdateLinkTests.Compiled(projectXml, projectDir)
            .Where(c => IsWrite(c.Include) || IsWrite(c.Link) || c.Files.Any(IsWrite))
            .Select(c => c.Include + (c.Link is null ? "" : " (Link " + c.Link + ")")).ToArray();
        string[] Uses(string source) => forbiddenUse.Matches(CodeOf(source)).Select(m => Regex.Replace(m.Value, @"\s+", "")).ToArray();
        var mcpDir = Path.Combine(root, "interfaces", "mcp");
        string Synthetic(string items) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" + items + "\n  </ItemGroup>\n</Project>";
        foreach (var (label, items, flagged) in new (string, string, bool)[]
        {
            ("a multi-line item with '\\' separators", "    <Compile\n      Include=\"..\\cli\\DocRealizedWrite.cs\"\n      Link=\"DocRealizedWrite.cs\" />", true),
            ("an item in an XML comment", "    <!-- <Compile Include=\"../cli/DocRealizedWrite.cs\" /> -->\n    <Compile Include=\"../cli/DocRealizedCommand.cs\" />", false),
            ("the second of two ';' entries", "<Compile Include=\"../cli/BoundedInput.cs;  ../cli/DocRealizedWrite.cs\" />", true),
            ("another case", "<Compile Include=\"../CLI/docrealizedwrite.CS\" />", true),
            ("a Link only", "<Compile Include=\"../cli/Other.cs\" Link=\"Sub/DocRealizedWrite.cs\" />", true),
            ("a wildcard matching the CLI sources", "<Compile Include=\"../cli/DocRealized*.cs\" />", true),
            ("a recursive wildcard", "<Compile Include=\"../**/*.cs\" />", true),
            ("the shared source only", "<Compile Include=\"../cli/DocRealizedCommand.cs\" Link=\"DocRealizedCommand.cs\" />", false),
        })
        {
            var found = WriteLinks(Synthetic(items), mcpDir);
            Check(found.Length > 0 == flagged, "link checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }
        foreach (var (label, source, flagged) in new (string, string, bool)[]
        {
            ("a call", "DocRealizedCommand.Run(args);", true),
            ("a call split over lines", "DocRealizedCommand\n        .Execute(args);", true),
            ("a method group", "Action<string, string?> f = DocRealizedCommand . Fail;", true),
            ("a line comment", "// DocRealizedCommand.Run(args)\nvar x = 1;", false),
            ("a block comment", "/* DocRealizedCommand.Run(\n */ var x = 1;", false),
            ("code after '//' inside a string", "var url = \"http://host\"; DocRealizedCommand.Run(args);", true),
            ("a longer member", "DocRealizedCommand.RunTests(); DocRealizedCommand.Failed();", false),
            ("the dry entry", "return DocRealizedCommand.DryRealized(record, fixture, assignments, document);", false),
        })
        {
            var found = Uses(source);
            Check(found.Length > 0 == flagged, "use checker self-test: " + label + (flagged ? " is flagged" : " is not flagged") + " [" + string.Join(", ", found) + "]");
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var testsDir = Path.GetFullPath(Path.Combine(root, "tests")) + Path.DirectorySeparatorChar;
        foreach (var project in new[] { Path.Combine(mcpDir, "Bim.Mcp.csproj"), Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj") })
        {
            var projectDir = Path.GetDirectoryName(project)!;
            var projectXml = File.ReadAllText(project);
            var items = DocUpdateLinkTests.Compiled(projectXml, projectDir);
            Check(items.Any(c => c.Files.Any(path => string.Equals(path, Path.GetFullPath(commandPath), comparison))),
                Path.GetFileName(project) + " compiles interfaces/cli/DocRealizedCommand.cs (shared source, not a copy)");
            var links = WriteLinks(projectXml, projectDir);
            Check(links.Length == 0, Path.GetFileName(project) + " does not compile the doc-realized write path (interfaces/cli/DocRealizedWrite.cs) ["
                + string.Join(", ", links) + "]");
            // The sources the project compiles that are not tests: its linked items and the default glob of interfaces/mcp.
            var compiledSources = items.SelectMany(c => c.Files).Concat(Directory.GetFiles(mcpDir, "*.cs"))
                .Select(path => Path.GetFullPath(path)).Where(path => !path.StartsWith(testsDir, comparison) && File.Exists(path)).Distinct().ToArray();
            var users = compiledSources.Select(path => (Name: Path.GetFileName(path), Found: Uses(File.ReadAllText(path)))).Where(u => u.Found.Length > 0)
                .Select(u => u.Name + " " + string.Join(" ", u.Found)).ToArray();
            Check(users.Length == 0, Path.GetFileName(project) + ": no compiled source calls DocRealizedCommand.Run / Execute / Fail ["
                + string.Join(", ", users) + "]");
        }
    }
}
