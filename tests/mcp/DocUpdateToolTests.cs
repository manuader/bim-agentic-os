using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Mcp;

// P4-20 doc_update: the read-only MCP face of the P4-08 CLI verb `doc-update --before b --after a --realized r --bindings g
// --output plan.json [--dependents d]` (interfaces/cli/DocUpdateCommand.cs, linked into the server, no copied parser).
// Oracles:
//   1. On success the tool text is the canonical DocUpdatePlan JSON: its UTF-8 bytes equal the file the real Release CLI
//      process writes for the same input files and UTF-8(JsonSerializer.Serialize(DocUpdate.Plan(...))) computed in
//      process (door move, no edit, removed door, level insertion with dependents, empty dependents, reversed member order).
//   2. Every refusal is isError and byte-equal to the CLI stdout line for the same inputs (input_*, doc_update_*_invalid,
//      doc_update_input_bound_exceeded, the library codes with the RevitCommandException field as detail).
//   3. It writes nothing (the input directory is unchanged; it takes no output path, no pipe, no --accept, no --execute),
//      and leaves the exit code and Console.Out untouched; tools/list labels it permission level read with readOnlyHint
//      true only and the schema requires exactly before, after, realized, bindings (dependents optional).
//   4. MCP-only path guard: doc_update_path_invalid {detail the input}; missing / mistyped / extra arguments are -32602.
//   5. The real Release Bim.Mcp.dll answers the same bytes over stdio.
//   6. Static: the tool calls DocUpdateCommand.DryPlan and contains no parser, plan call, probe, transport or file access
//      of its own; the CLI verb and DryPlan read and parse through one ReadInputs; the MCP projects link the CLI source.
internal static class DocUpdateToolTests
{
    static readonly Provenance provenance = new("mcp-doc-update-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-20\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd20");

    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("mcp-doc-update:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static Guid RoomOf(int room) => Id("room" + N(room));
    static Guid Door(int room) => Id("door" + N(room));

    static string FakeUid(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    static Project Rooms(params double[] elevations)
    {
        var elements = new List<Element>();
        for (var room = 0; room < elevations.Length; room++)
        {
            foreach (var element in RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements)
                elements.Add(element.Kind == "level" ? element with { GeometryMetres = new[] { elevations[room] } } : element);
            var x = room * 10 + 0.1;
            elements.Add(new(Door(room), "door", new[] { x + 1, 0.1, x + 1.9, 0.1, 0, 2.1 }, new[] { RoomCompiler.Id(RoomOf(room), "south"), RoomCompiler.Id(RoomOf(room), "level") }, provenance));
        }
        return new(projectId, 0, elements.ToArray());
    }

    static RevitBindings BindingsOf(Project project) => new(document,
        project.Elements.Where(element => element.Kind == "level").ToDictionary(element => element.Id, element => "Level " + element.Id.ToString("N")[..8]),
        Array.Empty<RevitWallTypeBinding>(), null, null);

    static IEnumerable<string> DocKeys(DocumentationSet set) =>
        set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key));

    static Dictionary<string, string> RealizedOf(Project project)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in DocKeys(DocDefaults.From(project))) map[DocRegeneration.ItemSource(key)] = FakeUid("stale:" + key);
        foreach (var element in project.Elements) map[DocRegeneration.ElementSource(element.Id)] = FakeUid("element:" + element.Id.ToString("D"));
        return map;
    }

    // The P4-08 bindings file format, written by hand (not by a serializer of RevitBindings).
    static string BindingsJson(RevitBindings bindings, string extra = "") =>
        "{\"document\":{\"path\":" + JsonSerializer.Serialize(bindings.Document.Path) + ",\"unique_id\":" + JsonSerializer.Serialize(bindings.Document.UniqueId) + "},\"levels\":{"
        + string.Join(",", bindings.Levels.Select(pair => JsonSerializer.Serialize(pair.Key.ToString("D")) + ":" + JsonSerializer.Serialize(pair.Value))) + "}" + extra + "}";

    static string MapJson(IEnumerable<KeyValuePair<string, string>> map) =>
        "{" + string.Join(",", map.Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + JsonSerializer.Serialize(pair.Value))) + "}";

    public static void Run(string root, string work, string dotnet, string cliDll, Func<string, JsonObject> call, Func<string, string> mcpProcess, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "doc_update: " + name);
        var scratch = Path.Combine(work, "doc-update-mcp");
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
        string Listing() => string.Join("\n", Directory.GetFileSystemEntries(inputs, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(e => Path.GetRelativePath(inputs, e) + (File.Exists(e) ? ":" + Hex(File.ReadAllBytes(e)) + ":" + File.GetLastWriteTimeUtc(e).Ticks.ToString(CultureInfo.InvariantCulture) : "/")));
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
        static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        static string Short(string text) => text.Length > 400 ? text[..400] + "..." : text;
        static string Failed(string code, string? detail = null) =>
            detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });

        // The real CLI process with a fresh output path: (exit, stdout line, written bytes or null).
        (int Exit, string Out, byte[]? File) Cli(string before, string after, string realized, string bindings, string? dependents)
        {
            var output = Path.Combine(outputs, Next() + "-plan.json");
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in new[] { "doc-update", "--before", before, "--after", after, "--realized", realized, "--bindings", bindings, "--output", output }) start.ArgumentList.Add(argument);
            if (dependents is not null) { start.ArgumentList.Add("--dependents"); start.ArgumentList.Add(dependents); }
            using var process = Process.Start(start) ?? throw new Exception("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180000)) { process.Kill(true); throw new Exception("CLI timed out: doc-update " + before); }
            process.WaitForExit();
            _ = stderr.Result;
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, File.Exists(output) ? File.ReadAllBytes(output) : null);
        }
        JsonObject Arguments(string before, string after, string realized, string bindings, string? dependents)
        {
            var arguments = new JsonObject { ["before"] = before, ["after"] = after, ["realized"] = realized, ["bindings"] = bindings };
            if (dependents is not null) arguments["dependents"] = dependents;
            return arguments;
        }
        string ToolLine(JsonObject arguments) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 23, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "doc_update", ["arguments"] = arguments } }.ToJsonString();
        static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
        static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
        static int? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<int>();

        var exitBefore = Environment.ExitCode;
        // One MCP call: the response, with "nothing written, exit code and Console.Out untouched" checked around it.
        JsonObject Call(string label, JsonObject arguments)
        {
            var before = Listing();
            var stdout = Console.Out;
            var response = call(ToolLine(arguments));
            Check(Listing() == before && Environment.ExitCode == exitBefore && ReferenceEquals(Console.Out, stdout), label + ": writes nothing, exit code and Console.Out untouched");
            return response;
        }

        // ---- tools/list ----
        var listed = call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }.ToJsonString())["result"]!["tools"]!.AsArray();
        var tool = listed.SingleOrDefault(t => t!["name"]!.GetValue<string>() == "doc_update");
        Check(tool is not null && tool["description"]!.GetValue<string>().Contains("doc-update", StringComparison.Ordinal)
            && tool["description"]!.GetValue<string>().Contains("read-only", StringComparison.OrdinalIgnoreCase), "listed once, read-only, naming the CLI verb doc-update");
        Check(tool!["_meta"]?["bim-agentic-os/permission_level"]?.GetValue<string>() == "read" && tool["annotations"]?["readOnlyHint"]?.GetValue<bool>() == true
            && tool["annotations"]!.AsObject().Count == 1, "lists _meta permission_level read and annotations readOnlyHint true only");
        var properties = tool["inputSchema"]!["properties"]!.AsObject();
        Check(string.Join(",", tool["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>())) == "before,after,realized,bindings"
            && string.Join(",", properties.Select(p => p.Key)) == "before,after,realized,bindings,dependents"
            && properties.All(p => p.Value!["type"]!.GetValue<string>() == "string")
            && tool["inputSchema"]!["additionalProperties"]!.GetValue<bool>() == false, "schema: string paths before, after, realized, bindings required, dependents optional, nothing else");

        // ---- 1. plans: CLI output file == tool text == in-process DocUpdate.Plan ----
        var before = Rooms(0, 3);
        var realized = RealizedOf(before);
        var moved = before with { Elements = before.Elements.Select(e => e.Id == Door(1) ? e with { GeometryMetres = new[] { e.GeometryMetres[0] + 0.2, e.GeometryMetres[1], e.GeometryMetres[2] + 0.2, e.GeometryMetres[3], e.GeometryMetres[4], e.GeometryMetres[5] } } : e).ToArray() };
        var removed = before with { Elements = before.Elements.Where(e => e.Id != Door(1)).ToArray() };
        var inserted = Rooms(0, 3, -3);
        var insertedRealized = new Dictionary<string, string>(realized, StringComparer.Ordinal);
        foreach (var element in inserted.Elements) insertedRealized.TryAdd(DocRegeneration.ElementSource(element.Id), FakeUid("element:" + element.Id.ToString("D")));
        var plan0 = "plan-" + RoomCompiler.Id(RoomOf(0), "level").ToString("N");
        var plan1 = "plan-" + RoomCompiler.Id(RoomOf(1), "level").ToString("N");
        var tagDoor0 = "tag-door-" + Door(0).ToString("N");
        var tagDoor1 = "tag-door-" + Door(1).ToString("N");
        var dependents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [plan0] = new[] { realized[DocRegeneration.ItemSource(tagDoor0)], FakeUid("annotation:plan0") },
            [plan1] = new[] { realized[DocRegeneration.ItemSource(tagDoor1)] },
        };
        var movedPlan = DocUpdate.Plan(before, moved, BindingsOf(moved), realized);
        Check(movedPlan.Replacement.Commands.Steps.Count > 0 && movedPlan.Replacement.Replacements.Any(item => item.DeleteOperation is not null), "fixture: a door move plans a stale delete and a re-creation");
        Check(DocUpdate.Plan(before, removed, BindingsOf(removed), realized).Retired.Count == 1, "fixture: a removed door retires its tag");

        var okBefore = Input("ok.before.json", JsonSerializer.Serialize(before));
        var okAfter = Input("ok.after.json", JsonSerializer.Serialize(moved));
        var okRealized = Input("ok.realized.json", MapJson(realized));
        var okBindings = Input("ok.bindings.json", BindingsJson(BindingsOf(moved)));
        byte[]? movedBytes = null;

        void Plans(string name, Project b, Project a, RevitBindings g, IReadOnlyDictionary<string, string> r, IReadOnlyDictionary<string, IReadOnlyList<string>>? d = null,
            string? realizedText = null, string? bindingsText = null, string? dependentsText = null)
        {
            var expected = Utf8(JsonSerializer.Serialize(DocUpdate.Plan(b, a, g, r, d)));
            var bp = Input(name + ".before.json", JsonSerializer.Serialize(b));
            var ap = Input(name + ".after.json", JsonSerializer.Serialize(a));
            var rp = Input(name + ".realized.json", realizedText ?? MapJson(r));
            var gp = Input(name + ".bindings.json", bindingsText ?? BindingsJson(g));
            var dp = d is null && dependentsText is null ? null : Input(name + ".dependents.json", dependentsText ?? JsonSerializer.Serialize(d));
            var cli = Cli(bp, ap, rp, gp, dp);
            var response = Call(name, Arguments(bp, ap, rp, gp, dp));
            Check(cli.Exit == 0 && cli.File is not null && cli.File.AsSpan().SequenceEqual(expected), name + ": the CLI writes the library plan | exit " + cli.Exit + " " + Short(cli.Out));
            Check(!IsError(response) && Utf8(Text(response)).AsSpan().SequenceEqual(expected) && cli.File is not null && Utf8(Text(response)).AsSpan().SequenceEqual(cli.File),
                name + ": the tool text is byte-equal to the CLI output file and DocUpdate.Plan | " + Short(Text(response)));
            if (name == "door move") movedBytes = expected;
        }
        Plans("door move", before, moved, BindingsOf(moved), realized);
        Plans("door move reversed map", before, moved, BindingsOf(moved), realized, realizedText: MapJson(realized.Reverse()));
        Plans("no edit", before, before, BindingsOf(before), realized);
        Plans("door removed", before, removed, BindingsOf(removed), realized);
        Plans("level inserted with dependents", before, inserted, BindingsOf(inserted), insertedRealized, dependents);
        Plans("level inserted, dependents keys reversed", before, inserted, BindingsOf(inserted), insertedRealized, dependents,
            dependentsText: "{" + string.Join(",", dependents.OrderByDescending(pair => pair.Key, StringComparer.Ordinal).Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + JsonSerializer.Serialize(pair.Value))) + "}");
        Plans("door move with empty dependents", before, moved, BindingsOf(moved), realized, new Dictionary<string, IReadOnlyList<string>>());
        var fullBindings = BindingsOf(moved) with { WallTypes = new[] { new RevitWallTypeBinding(0.2, "Generic 200") }, DoorSymbol = "Door 0915", WindowSymbol = null };
        Plans("all bindings members", before, moved, fullBindings, realized,
            bindingsText: BindingsJson(fullBindings, ",\"wall_types\":[{\"thickness_metres\":0.2,\"type\":\"Generic 200\"}],\"door_symbol\":\"Door 0915\",\"window_symbol\":null"));

        // ---- 2. refusals: tool text == CLI stdout line, isError, no file ----
        void Refused(string name, string expected, string? b = null, string? a = null, string? r = null, string? g = null, string? d = null)
        {
            var bp = b ?? okBefore; var ap = a ?? okAfter; var rp = r ?? okRealized; var gp = g ?? okBindings;
            var cli = Cli(bp, ap, rp, gp, d);
            var response = Call(name, Arguments(bp, ap, rp, gp, d));
            Check(cli.Exit == 1 && cli.File is null && cli.Out == expected, name + ": the CLI refuses with " + expected + " | " + Short(cli.Out));
            Check(IsError(response) && Text(response) == cli.Out, name + ": the tool text is the CLI refusal line, isError | " + Short(Text(response)));
        }
        var missing = Path.Combine(inputs, "absent.json");
        Refused("a missing before", Failed("input_not_found", "before"), b: missing);
        Refused("a missing after", Failed("input_not_found", "after"), a: missing);
        Refused("a missing realized map", Failed("input_not_found", "realized"), r: missing);
        Refused("a missing bindings file", Failed("input_not_found", "bindings"), g: missing);
        Refused("a missing dependents file", Failed("input_not_found", "dependents"), d: missing);
        Refused("a missing before and after (first input wins)", Failed("input_not_found", "before"), b: missing, a: missing);
        Check(!File.Exists(missing), "creates no missing input");
        var directory = Path.Combine(inputs, Next() + "-a-directory.json");
        Directory.CreateDirectory(directory);
        Refused("a directory as realized map", Failed("input_not_regular_file", "realized"), r: directory);
        Refused("a before over 8 MiB", Failed("input_too_large", "before"), b: Input("huge.json", new string(' ', 8 * 1024 * 1024 + 1)));
        Refused("a before that is not JSON", Failed("doc_update_project_invalid", "before"), b: Input("bad.before.json", "{"));
        Refused("an after with null elements", Failed("doc_update_project_invalid", "after"), a: Input("null.after.json", "{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Revision\":0,\"Elements\":null}"));
        Refused("a realized map that is an array", Failed("doc_update_realized_invalid"), r: Input("array.realized.json", "[]"));
        Refused("a realized map with a duplicate key", Failed("doc_update_realized_invalid"), r: Input("dup.realized.json", "{\"doc:a\":\"x\",\"doc:a\":\"x\"}"));
        Refused("bindings without document", Failed("doc_update_bindings_invalid"), g: Input("nodoc.bindings.json", "{\"levels\":{}}"));
        var goodBindings = BindingsJson(BindingsOf(moved));
        Refused("bindings with an unknown member", Failed("doc_update_bindings_invalid"), g: Input("extra.bindings.json", goodBindings[..^1] + ",\"extra\":1}"));
        Refused("bindings with two wall types of one thickness", Failed("doc_update_bindings_invalid"),
            g: Input("twowalls.bindings.json", goodBindings[..^1] + ",\"wall_types\":[{\"thickness_metres\":0.2,\"type\":\"A\"},{\"thickness_metres\":0.2,\"type\":\"B\"}]}"));
        Refused("dependents that are a string list", Failed("doc_update_dependents_invalid"), d: Input("strings.dependents.json", "{\"k\":\"x\"}"));
        Refused("a not-JSON before ahead of a bad realized map (parse order)", Failed("doc_update_project_invalid", "before"), b: Input("bad2.before.json", "{"), r: Input("bad2.realized.json", "[]"));
        var bigMap = new StringBuilder("{");
        for (var i = 0; i <= 100_000; i++) bigMap.Append(i == 0 ? "" : ",").Append("\"doc:k").Append(N(i)).Append("\":\"x\"");
        Refused("a realized map over the entry bound", Failed("doc_update_input_bound_exceeded", "realized"), r: Input("big.realized.json", bigMap.Append('}').ToString()));

        // Library refusals: the code the library raises (a RevitCommandException's field as detail).
        static string LibraryLine(Project b, Project a, RevitBindings g, IReadOnlyDictionary<string, string> r, IReadOnlyDictionary<string, IReadOnlyList<string>>? d = null)
        {
            try { DocUpdate.Plan(b, a, g, r, d); return "succeeded"; }
            catch (RevitCommandException e) { return Failed(e.Code, e.Field); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return Failed(e.Message); }
        }
        void RefusedLike(string name, string expected, Project b, Project a, RevitBindings g, IReadOnlyDictionary<string, string> r, IReadOnlyDictionary<string, IReadOnlyList<string>>? d = null)
        {
            Check(LibraryLine(b, a, g, r, d) == expected, "fixture: the library refuses " + name + " as " + expected + " | " + LibraryLine(b, a, g, r, d));
            Refused(name, expected, Input(name.Replace(' ', '-') + ".before.json", JsonSerializer.Serialize(b)), Input(name.Replace(' ', '-') + ".after.json", JsonSerializer.Serialize(a)),
                Input(name.Replace(' ', '-') + ".realized.json", MapJson(r)), Input(name.Replace(' ', '-') + ".bindings.json", BindingsJson(g)),
                d is null ? null : Input(name.Replace(' ', '-') + ".dependents.json", JsonSerializer.Serialize(d)));
        }
        RefusedLike("a malformed realized source", Failed("realized_invalid"), before, moved, BindingsOf(moved), new Dictionary<string, string>(realized, StringComparer.Ordinal) { ["view:x"] = FakeUid("x") });
        RefusedLike("dependents of a key not regenerated", Failed("dependents_unknown_key"), before, moved, BindingsOf(moved), realized,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [plan0] = new[] { FakeUid("a") } });
        RefusedLike("a new element not realized yet", Failed("input_unresolved"), before, inserted, BindingsOf(inserted), realized);
        RefusedLike("a relative document path", Failed("document_path_invalid", "document.path"), before, moved, BindingsOf(moved) with { Document = new RevitDocumentRef("copy.rvt", document.UniqueId) }, realized);
        RefusedLike("an after project at another revision", Failed("revision_conflict"), before, moved with { Revision = 1 }, BindingsOf(moved), realized);

        // ---- 3. read-only: no output, pipe, accept or execute arguments ----
        foreach (var (label, extra, value) in new (string, string, JsonNode)[] { ("output", "output", Path.Combine(inputs, "never.json")), ("probe_dependents", "probe_dependents", "bim-agentic-os-2025-4242"),
            ("pipe_name", "pipe_name", "bim-agentic-os-2025-4242"), ("accept", "accept", new JsonArray(JsonValue.Create("x"))), ("execute", "execute", true), ("expected_document", "expected_document", new string('a', 64)) })
        {
            var arguments = Arguments(okBefore, okAfter, okRealized, okBindings, null);
            arguments[extra] = value;
            Check(ErrorCode(call(ToolLine(arguments))) == -32602, "takes no " + label + " argument (-32602)");
        }
        Check(!File.Exists(Path.Combine(inputs, "never.json")), "never writes an output file");

        // ---- 4. MCP-only path guard and argument errors ----
        Check(DocUpdateTool.PathInvalid == "doc_update_path_invalid" && DocUpdateTool.Name == "doc_update", "the MCP-only path refusal code is doc_update_path_invalid");
        foreach (var parameter in new[] { "before", "after", "realized", "bindings", "dependents" })
        {
            foreach (var (label, bad) in new[] { ("empty", ""), ("blank", "   "), ("with NUL", "a\0b"), ("of 5000 characters", new string('x', 5000)) })
            {
                var arguments = Arguments(okBefore, okAfter, okRealized, okBindings, null);
                arguments[parameter] = bad;
                var response = Call(parameter + " " + label, arguments);
                Check(IsError(response) && Text(response) == Failed("doc_update_path_invalid", parameter), "refuses a " + parameter + " path " + label + " as doc_update_path_invalid (" + parameter + ") | " + Short(Text(response)));
            }
        }
        {
            var arguments = Arguments("", okAfter, okRealized, "", "");
            var response = Call("several bad paths", arguments);
            Check(IsError(response) && Text(response) == Failed("doc_update_path_invalid", "before"), "several bad paths: the first in before, after, realized, bindings, dependents order is reported");
            var guarded = Call("bad dependents path before a missing before", Arguments(missing, okAfter, okRealized, okBindings, "\0"));
            Check(IsError(guarded) && Text(guarded) == Failed("doc_update_path_invalid", "dependents"), "the path guard runs before any input is read");
        }
        var surrogate = call("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"doc_update\",\"arguments\":{\"before\":\"\\ud800\",\"after\":" + JsonSerializer.Serialize(okAfter)
            + ",\"realized\":" + JsonSerializer.Serialize(okRealized) + ",\"bindings\":" + JsonSerializer.Serialize(okBindings) + "}}}");
        Check(IsError(surrogate) && Text(surrogate) == Failed("doc_update_path_invalid", "before") && Environment.ExitCode == exitBefore, "a lone-surrogate before is doc_update_path_invalid, never a crash | " + Short(Text(surrogate)));
        foreach (var parameter in new[] { "before", "after", "realized", "bindings" })
        {
            var arguments = Arguments(okBefore, okAfter, okRealized, okBindings, null);
            arguments.Remove(parameter);
            Check(ErrorCode(call(ToolLine(arguments))) == -32602, "without " + parameter + " returns -32602");
        }
        foreach (var parameter in new[] { "before", "after", "realized", "bindings", "dependents" })
        {
            var arguments = Arguments(okBefore, okAfter, okRealized, okBindings, null);
            arguments[parameter] = 1;
            Check(ErrorCode(call(ToolLine(arguments))) == -32602, "a non-string " + parameter + " returns -32602");
        }
        Check(ErrorCode(call(ToolLine(new JsonObject { ["before"] = JsonNode.Parse(JsonSerializer.Serialize(before)), ["after"] = okAfter, ["realized"] = okRealized, ["bindings"] = okBindings }))) == -32602,
            "takes no inline Project object (-32602)");

        // ---- 5. the real Release Bim.Mcp.dll over stdio ----
        var processOut = mcpProcess(ToolLine(Arguments(okBefore, okAfter, okRealized, okBindings, null)) + "\n");
        var processResponse = JsonNode.Parse(processOut.Split('\n')[0])!.AsObject();
        Check(!IsError(processResponse) && movedBytes is not null && Utf8(Text(processResponse)).AsSpan().SequenceEqual(movedBytes),
            "the real Bim.Mcp.dll process returns the door-move plan bytes | " + Short(processOut));

        // ---- 6. static: the shared reader and parser, linked, not copied ----
        // Comments stripped: /* */ blocks first, then // to end of line.
        static string CodeOf(string text) => string.Join("\n", Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline).Split('\n')
            .Select(line => { var at = line.IndexOf("//", StringComparison.Ordinal); return at < 0 ? line : line[..at]; }));
        var toolPath = Path.Combine(root, "interfaces", "mcp", "DocUpdateTool.cs");
        Check(File.Exists(toolPath), "interfaces/mcp/DocUpdateTool.cs exists");
        var toolCode = File.Exists(toolPath) ? CodeOf(File.ReadAllText(toolPath)) : "";
        Check(toolCode.Contains("DocUpdateCommand.DryPlan(", StringComparison.Ordinal), "the tool runs the CLI verb's shared DocUpdateCommand.DryPlan");
        var inlined = new[] { "JsonDocument", "Utf8JsonReader", "JsonSerializer.Deserialize", "\"levels\"", "\"wall_types\"", "DocUpdate.Plan(", "DocUpdate.Run(", "DocUpdateProbe", "DocUpdateExecute",
            "DocUpdateDependents", "DocUpdateCommand.Execute(", "DocUpdateRevit", "RevitCommandService", "Transport", "BoundedInput", "File.", "FileStream", "Directory.", "GltfExporter", "Console.SetOut", "Console.Out", "Environment.ExitCode" }
            .Where(api => toolCode.Contains(api, StringComparison.Ordinal)).ToArray();
        Check(inlined.Length == 0, "the tool inlines no parser, plan, probe, transport, file access or Console capture [" + string.Join(", ", inlined) + "]");
        var owners = Directory.GetFiles(Path.Combine(root, "interfaces", "mcp"), "*.cs")
            .Where(f => new[] { "DocUpdate.Plan(", "DocUpdateDependents.", "DocUpdateProbe.", "DocUpdateExecute.", "DocUpdateCommand.Execute(" }.Any(api => CodeOf(File.ReadAllText(f)).Contains(api, StringComparison.Ordinal)))
            .Select(f => Path.GetFileName(f)).ToArray();
        // The bindings member names are shared with the mirror_drift request (MirrorDriftTool.cs parses its own wall_types), so
        // the cross-file guard names the doc-update plan, probe and run entry points only.
        Check(owners.Length == 0, "no interfaces/mcp source plans, probes or runs a documentation update itself [" + string.Join(", ", owners) + "]");
        var commandCode = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "DocUpdateCommand.cs")));
        Check(Regex.Matches(commandCode, @"= ReadInputs\(").Count == 2 && Regex.Matches(commandCode, @"\bParseBindings\(bindingsText\)").Count == 1 && Regex.Matches(commandCode, @"\bRead\(beforePath, ""before""\)").Count == 1,
            "the CLI verb and DryPlan share one path: both call ReadInputs, which reads and parses the inputs in one place");
        foreach (var project in new[] { Path.Combine(root, "interfaces", "mcp", "Bim.Mcp.csproj"), Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj") })
        {
            var projectDir = Path.GetDirectoryName(project)!;
            var shared = Path.GetFullPath(Path.Combine(root, "interfaces", "cli", "DocUpdateCommand.cs"));
            var linked = System.Xml.Linq.XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "Compile")
                .Select(e => (string?)e.Attribute("Include")).Where(include => include is not null)
                .Any(include => string.Equals(Path.GetFullPath(Path.Combine(projectDir, include!.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))), shared,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Check(linked, Path.GetFileName(project) + " compiles interfaces/cli/DocUpdateCommand.cs (shared source, not a copy)");
        }
    }
}
