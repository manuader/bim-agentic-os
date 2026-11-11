using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bim.Cad;
using Bim.Core;

// P6-10 `cad-plan-brief draft.json output.json [--min-confidence x] [--confirm id]... [--confirm-reason code]...`:
// a P6-07 cad-brief document (the P6-05 brief draft) becomes the P6-08 planner brief written to a new .json file.
// Oracles, all against the real Release CLI process:
//   1. The draft is made by the real `cad-brief` verb from tests/cad/fixtures/brief-plan.dxf; the written "brief" is
//      byte-identical to BriefSerializer.Serialize of BriefPlanner.ToPlannerBrief called in process on the library's
//      own draft of the same bytes with the same options, and "kept", "dropped", "unknown_confirmed_*" and "issues"
//      equal the library result serialized with the CLI's JSON options.
//   2. Reruns are byte-identical (and independent of option order).
//   3. Every refusal prints one {"status":"failed","code":...[,"detail"]} envelope, exits 1 and creates no output file;
//      an existing output is left byte-for-byte untouched.
internal static class CadPlanBriefTests
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public static void Run(string root, string work, string dotnet, string cliDll, byte[] overCap, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "cad-plan-brief");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "cad-plan-brief: " + name, detail);

        (int Exit, string Out, string Err) Cli(params string[] arguments) => CliWith(null, arguments);
        (int Exit, string Out, string Err) CliWith(IReadOnlyDictionary<string, string>? environment, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        static string R((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " " + (r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        static JsonElement? Parse(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        static JsonElement? Doc(string path)
        {
            try { return Parse(File.ReadAllText(path)); }
            catch (IOException) { return null; }
        }
        static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var counter = 0;
        string Out(string extension = ".json") => Path.Combine(scratch, "out-" + (++counter).ToString(CultureInfo.InvariantCulture) + extension);
        string Input(string name, string content)
        {
            var path = Path.Combine(scratch, (++counter).ToString(CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(path, content);
            return path;
        }

        // Succeeded envelope: exit 0, the output exists and the envelope's bytes/sha256 describe it.
        bool Ok((int Exit, string Out, string Err) r, string output) =>
            r.Exit == 0 && Parse(r.Out) is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("status", out var status) && status.GetString() == "succeeded"
            && e.GetProperty("kind").GetString() == "cad-plan-brief" && e.GetProperty("output").GetString() == output && File.Exists(output)
            && e.GetProperty("bytes").GetInt64() == new FileInfo(output).Length && e.GetProperty("sha256").GetString() == Sha(File.ReadAllBytes(output))
            && !e.GetProperty("persisted").GetBoolean();
        // Refusal: exit 1, one failed envelope with the code, and no output created (an existing one is left alone).
        void Expect(string name, string code, params string[] arguments)
        {
            var output = arguments.Length > 2 ? arguments[2] : null;
            var existed = output is not null && (File.Exists(output) || Directory.Exists(output));
            var before = existed && File.Exists(output!) ? File.ReadAllBytes(output!) : null;
            var r = Cli(arguments);
            Check(r.Exit == 1 && Parse(r.Out) is { ValueKind: JsonValueKind.Object } e && e.GetProperty("status").GetString() == "failed" && e.GetProperty("code").GetString() == code
                && (existed || output is null || !File.Exists(output)) && (before is null || File.ReadAllBytes(output!).SequenceEqual(before)), name, R(r));
        }

        // ---- the draft: real cad-brief on brief-plan.dxf ----
        var dxfPath = Path.Combine(root, "tests", "cad", "fixtures", "brief-plan.dxf");
        var dxfBytes = File.ReadAllBytes(dxfPath);
        var draftPath = Path.Combine(scratch, "draft.json");
        var made = Cli("cad-brief", dxfPath, draftPath);
        Check(made.Exit == 0 && File.Exists(draftPath), "cad-brief makes the draft input from brief-plan.dxf", R(made));
        if (!File.Exists(draftPath)) return;
        var draftBytes = File.ReadAllBytes(draftPath);
        var library = BriefExtract.FromDxf(DxfReader.Parse(dxfBytes, "brief-plan.dxf"));

        bool Matches(string output, BriefPlannerResult expected, out string why)
        {
            why = "";
            if (Doc(output) is not { ValueKind: JsonValueKind.Object } d) { why = "output is not a JSON object"; return false; }
            try { return Compare(d, expected, out why); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { why = "missing or mistyped member: " + e.Message; return false; }
        }
        static bool Compare(JsonElement d, BriefPlannerResult expected, out string why)
        {
            var checks = new (string Name, bool Ok)[]
            {
                ("header", d.GetProperty("kind").GetString() == "cad-plan-brief" && d.GetProperty("version").GetInt32() == 1 && !d.GetProperty("persisted").GetBoolean()),
                ("valid", d.GetProperty("valid").GetBoolean() == expected.IsValid),
                ("brief", d.GetProperty("brief").GetRawText() == BriefSerializer.Serialize(expected.Brief)),
                ("kept", d.GetProperty("kept").GetRawText() == JsonSerializer.Serialize(expected.Kept, Json)),
                ("dropped", d.GetProperty("dropped").GetRawText() == JsonSerializer.Serialize(expected.Dropped, Json)),
                ("unknown_confirmed_rooms", d.GetProperty("unknown_confirmed_rooms").GetRawText() == JsonSerializer.Serialize(expected.UnknownConfirmedRooms, Json)),
                ("unknown_confirmed_reasons", d.GetProperty("unknown_confirmed_reasons").GetRawText() == JsonSerializer.Serialize(expected.UnknownConfirmedReasons, Json)),
                ("issues", d.GetProperty("issues").GetRawText() == JsonSerializer.Serialize(expected.Issues, Json)),
            };
            why = string.Join(",", checks.Where(c => !c.Ok).Select(c => c.Name));
            return why.Length == 0;
        }
        string Ids(JsonElement rooms) => string.Join(",", rooms.EnumerateArray().Select(r => r.GetProperty("Id").GetString()));

        // ---- happy path, defaults ----
        {
            var expected = BriefPlanner.ToPlannerBrief(library);
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o);
            Check(Ok(r, o), "defaults write the planner brief and a succeeded envelope", R(r));
            Check(Matches(o, expected, out var why), "defaults equal BriefPlanner.ToPlannerBrief on the library draft", why);
            Check(Doc(o) is { } d && Ids(d.GetProperty("kept")) == "dormitorio-1,cocina,living-room,bano-2,lavadero,hall" && Ids(d.GetProperty("dropped")) == "bano" && d.GetProperty("valid").GetBoolean(),
                "defaults keep six rooms and drop baño", Doc(o)?.GetRawText() ?? "");
            Check(Parse(r.Out) is { } e && e.TryGetProperty("kept", out var k) && k.GetInt32() == 6 && e.GetProperty("dropped").GetInt32() == 1 && e.GetProperty("issues").GetInt32() == 0 && e.GetProperty("valid").GetBoolean(),
                "envelope carries kept/dropped/issues counts and valid", r.Out);
            Check(Doc(o) is { } s && s.GetProperty("source").GetProperty("file").GetString() == "draft.json" && s.GetProperty("source").GetProperty("bytes").GetInt64() == draftBytes.Length
                && s.GetProperty("source").GetProperty("sha256").GetString() == Sha(draftBytes) && s.GetProperty("source").GetProperty("drawing_sha256").GetString() == Sha(dxfBytes)
                && s.GetProperty("source").GetProperty("drawing_file").GetString() == "brief-plan.dxf",
                "source names the draft file (bytes, sha256) and the drawing it came from", Doc(o)?.GetProperty("source").GetRawText() ?? "");
            Check(Doc(o) is { } p && p.GetProperty("options").GetRawText() == "{\"min_confidence\":0.5,\"confirmed_rooms\":[],\"confirmed_reasons\":[]}",
                "options echo the default threshold and no confirmations", Doc(o)?.GetProperty("options").GetRawText() ?? "");
            Check(Doc(o) is { } b && BriefSerializer.Serialize(BriefParser.Parse(b.GetProperty("brief").GetRawText())) == b.GetProperty("brief").GetRawText(), "the brief is canonical P6-03 JSON (parse/serialize round trip)");
            var bytes = File.ReadAllBytes(o);
            Check(bytes.Length > 0 && bytes[0] == (byte)'{' && !bytes.Contains((byte)'\n'), "the output is compact UTF-8 without BOM");

            // Determinism: two more runs are byte-identical to the first.
            var o1 = Out(); var o2 = Out(); var r1 = Cli("cad-plan-brief", draftPath, o1); var r2 = Cli("cad-plan-brief", draftPath, o2);
            Check(Ok(r1, o1) && Ok(r2, o2) && File.ReadAllBytes(o1).SequenceEqual(bytes) && File.ReadAllBytes(o2).SequenceEqual(bytes), "reruns are byte-identical", R(r1) + " | " + R(r2));
        }

        // ---- --min-confidence ----
        foreach (var (value, threshold) in new[] { ("0.96", 0.96), ("0", 0.0), ("1", 1.0), ("5e-1", 0.5) })
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { MinConfidence = threshold });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--min-confidence", value);
            var ok = Ok(r, o);
            var same = Matches(o, expected, out var why);
            Check(ok && same && Doc(o) is { } d && d.GetProperty("options").GetProperty("min_confidence").GetDouble() == threshold,
                "--min-confidence " + value + " equals the library", R(r) + " " + why);
        }
        {
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--min-confidence", "0.96");
            Check(Ok(r, o) && Doc(o) is { } d && Ids(d.GetProperty("kept")) == "cocina", "--min-confidence 0.96 keeps only the certain kitchen", R(r));
        }

        // ---- --confirm / --confirm-reason ----
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { ConfirmedRooms = new[] { "bano", "bano-2" } });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--confirm", "bano-2", "--confirm", "bano");
            Check(Ok(r, o) && Matches(o, expected, out _) && Doc(o) is { } d && d.GetProperty("dropped").GetArrayLength() == 0 && d.GetProperty("kept").GetArrayLength() == 7,
                "--confirm bano --confirm bano-2 keeps all seven rooms (library equal)", R(r));
            Check(Doc(o) is { } p && p.GetProperty("options").GetProperty("confirmed_rooms").GetRawText() == "[\"bano\",\"bano-2\"]", "confirmed rooms are echoed distinct in ordinal order");
            var o2 = Out(); var r2 = Cli("cad-plan-brief", draftPath, o2, "--confirm", "bano", "--confirm", "bano-2", "--confirm", "bano");
            Check(Ok(r2, o2) && File.ReadAllBytes(o2).SequenceEqual(File.ReadAllBytes(o)), "option order and repeated ids do not change the output", R(r2));
        }
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { ConfirmedReasons = new[] { "low-confidence", "area-mismatch" } });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--confirm-reason", "low-confidence", "--confirm-reason", "area-mismatch");
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same && Doc(o) is { } d && Ids(d.GetProperty("dropped")) == "bano-2",
                "--confirm-reason for both of baño's reasons keeps it and drops baño 2 as duplicate-label", R(r) + " " + why);
            Check(Doc(o) is { } p && p.GetProperty("options").GetProperty("confirmed_reasons").GetRawText() == "[\"area-mismatch\",\"low-confidence\"]", "confirmed reasons are echoed in ordinal order");
        }
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { MinConfidence = 0.4, ConfirmedRooms = new[] { "bano" }, ConfirmedReasons = new[] { "duplicate-label" } });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--confirm-reason", "duplicate-label", "--min-confidence", "0.4", "--confirm", "bano");
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same, "threshold, room and reason confirmations combine as in the library", R(r) + " " + why);
        }
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { ConfirmedRooms = new[] { "zz", "cocina" } });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--confirm", "zz", "--confirm", "cocina");
            Check(Ok(r, o) && Matches(o, expected, out _) && Doc(o) is { } d && d.GetProperty("unknown_confirmed_rooms").GetRawText() == "[\"zz\"]",
                "a confirmation naming no draft room is reported in unknown_confirmed_rooms", R(r));
        }

        // ---- every room dropped: written, typed invalid, exit 0 ----
        {
            var node = JsonNode.Parse(File.ReadAllText(draftPath))!;
            foreach (var room in node["rooms"]!.AsArray()) room!["Confidence"] = 0.1;
            var low = Input("low.json", node.ToJsonString());
            var lowDraft = library with { Rooms = library.Rooms.Select(p => p with { Confidence = 0.1 }).ToArray() };
            var expected = BriefPlanner.ToPlannerBrief(lowDraft);
            var o = Out(); var r = Cli("cad-plan-brief", low, o);
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same && !expected.IsValid && Doc(o) is { } d && !d.GetProperty("valid").GetBoolean() && d.GetProperty("kept").GetArrayLength() == 0
                && d.GetProperty("issues").EnumerateArray().Any(i => i.GetProperty("Code").GetString() == BriefCodes.RoomsEmpty && i.GetProperty("Severity").GetString() == "Error"),
                "dropping every room writes an invalid brief flagged rooms_empty (proposal confidences come from the file)", R(r) + " " + why);
            Check(Parse(r.Out) is { } e && e.TryGetProperty("valid", out var v) && !v.GetBoolean(), "the envelope reports valid false", r.Out);
        }

        // ---- refusals ----
        Expect("--min-confidence above 1", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--min-confidence", "1.5");
        Expect("--min-confidence below 0", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--min-confidence", "-0.1");
        Expect("--min-confidence not a number", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--min-confidence", "abc");
        Expect("--min-confidence NaN", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--min-confidence", "NaN");
        Expect("--min-confidence twice", "cad_option_duplicate", "cad-plan-brief", draftPath, Out(), "--min-confidence", "0.5", "--min-confidence", "0.6");
        Expect("--confirm-reason unknown code", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--confirm-reason", "bogus");
        Expect("--confirm-reason is case-sensitive", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--confirm-reason", "Area-Mismatch");
        Expect("--confirm with an invalid room id", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--confirm", "Bad Id");
        Expect("unknown option", "cad_option_unknown", "cad-plan-brief", draftPath, Out(), "--page", "1");
        Expect("option without a value", "cad_option_value_missing", "cad-plan-brief", draftPath, Out(), "--confirm");
        Expect("missing input", "input_not_found", "cad-plan-brief", Path.Combine(scratch, "missing.json"), Out());
        Expect("directory input", "input_not_regular_file", "cad-plan-brief", Directory.CreateDirectory(Path.Combine(scratch, "dir.json")).FullName, Out());
        var big = Path.Combine(scratch, "over-cap.json"); File.WriteAllBytes(big, overCap);
        Expect("input over 8 MiB", "input_too_large", "cad-plan-brief", big, Out());
        File.Delete(big);
        Expect("malformed JSON", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("bad.json", "{\"kind\":"), Out());
        Expect("JSON that is not an object", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("array.json", "[]"), Out());
        Expect("a document that is not a cad-brief draft", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("kind.json", File.ReadAllText(draftPath).Replace("\"kind\":\"cad-brief\"", "\"kind\":\"cad-draft\"", StringComparison.Ordinal)), Out());
        Expect("an unsupported draft version", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("version.json", File.ReadAllText(draftPath).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal)), Out());
        {
            var node = JsonNode.Parse(File.ReadAllText(draftPath))!;
            node["rooms"]!.AsArray().Add(null);
            Expect("a null room proposal", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("null-room.json", node.ToJsonString()), Out());
            var node2 = JsonNode.Parse(File.ReadAllText(draftPath))!;
            node2["rooms"]![0]!["Confidence"] = "high";
            Expect("a mistyped proposal member", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("typed.json", node2.ToJsonString()), Out());
            var node3 = JsonNode.Parse(File.ReadAllText(draftPath))!;
            node3.AsObject().Remove("uncertainties");
            Expect("a draft without uncertainties", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("no-unc.json", node3.ToJsonString()), Out());
            var node4 = JsonNode.Parse(File.ReadAllText(draftPath))!;
            node4["brief"]!["surprise"] = 1;
            Expect("a brief with an unknown member is the P6-03 parser code", "brief_member_unknown", "cad-plan-brief", Input("brief-member.json", node4.ToJsonString()), Out());
            var r = Cli("cad-plan-brief", Input("bad2.json", "{"), Out());
            Check(Parse(r.Out) is { } e && e.TryGetProperty("detail", out var detail) && detail.GetString()!.Length > 0, "input refusals carry a detail", r.Out);
        }
        {
            var existing = Out(); File.WriteAllBytes(existing, new byte[] { 7, 7 });
            Expect("refuses an existing output", "output_exists", "cad-plan-brief", draftPath, existing);
            Check(File.ReadAllBytes(existing).SequenceEqual(new byte[] { 7, 7 }), "an existing output is left untouched");
            Expect("checks the output before reading the input", "output_exists", "cad-plan-brief", Path.Combine(scratch, "missing.json"), existing);
            Expect("refuses a non-.json output", "output_extension_unsupported", "cad-plan-brief", draftPath, Out(".txt"));
            Expect("refuses an output in a missing directory", "output_directory_not_found", "cad-plan-brief", draftPath, Path.Combine(scratch, "no-dir", "x.json"));
            Expect("an option refusal comes before any I/O", "cad_option_unknown", "cad-plan-brief", Path.Combine(scratch, "missing.json"), existing, "--nope", "1");
        }
        foreach (var args in new[] { new[] { "cad-plan-brief" }, new[] { "cad-plan-brief", draftPath } })
        {
            var r = Cli(args);
            Check(r.Exit == 1 && Parse(r.Out) is { } e && e.GetProperty("status").GetString() == "failed" && e.GetProperty("code").GetString()!.StartsWith("usage: cad-plan-brief draft.json output.json", StringComparison.Ordinal),
                "missing arguments print the usage (" + (args.Length - 1).ToString(CultureInfo.InvariantCulture) + " given)", R(r));
        }

        // ---- SF1 strict proposal/uncertainty members ----
        {
            JsonNode Fresh() => JsonNode.Parse(File.ReadAllText(draftPath))!;
            var n1 = Fresh(); n1["rooms"]![0]!.AsObject().Remove("Confidence");
            Expect("a proposal without Confidence is refused (not defaulted to 0)", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("no-confidence.json", n1.ToJsonString()), Out());
            var n2 = Fresh(); n2["rooms"]![0]!.AsObject().Remove("Confidence"); n2["rooms"]![0]!["confidence"] = 0.9;
            Expect("a lower-case confidence member is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("lower-confidence.json", n2.ToJsonString()), Out());
            var n3 = Fresh(); n3["rooms"]![0]!["Label"] = null;
            Expect("a null Label is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("null-label.json", n3.ToJsonString()), Out());
            var n4 = Fresh(); n4["rooms"]![0]!["Confidence"] = 5;
            Expect("a Confidence above 1 is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("confidence-5.json", n4.ToJsonString()), Out());
            var n5 = Fresh(); n5["rooms"]![0]!["Extra"] = 1;
            Expect("an unknown proposal member is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("extra.json", n5.ToJsonString()), Out());
            var n6 = Fresh(); n6["rooms"]![0]!["Label"]!.AsObject().Remove("Layer");
            Expect("a label source without Layer is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("no-layer.json", n6.ToJsonString()), Out());
            var n7 = Fresh(); n7["uncertainties"]!.AsArray().Add(JsonNode.Parse("{\"Code\":\"orphan-area\",\"Message\":\"m\",\"RoomId\":null,\"Sources\":[]}"));
            Expect("an uncertainty without ValueSquareMetres is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("no-value.json", n7.ToJsonString()), Out());
            var text = File.ReadAllText(draftPath);
            Check(text.StartsWith("{\"kind\":\"cad-brief\",", StringComparison.Ordinal), "the cad-brief draft starts with its kind member");
            Expect("a duplicate top-level kind is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("dup-kind.json", "{\"kind\":\"cad-brief\"," + text[1..]), Out());
            var dupRoom = text.Replace("\"rooms\":[{\"Id\":", "\"rooms\":[{\"Id\":\"x\",\"Id\":", StringComparison.Ordinal);
            Check(dupRoom != text, "duplicate proposal member fixture differs");
            Expect("a duplicate proposal member is refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("dup-id.json", dupRoom), Out());
        }

        // ---- SF3 work bound ----
        {
            const string Entry = "{\"Code\":\"duplicate-text\",\"Message\":\"m\",\"RoomId\":null,\"ValueSquareMetres\":null,\"Sources\":[]}";
            string WithUncertainties(IEnumerable<string> entries)
            {
                var node = JsonNode.Parse(File.ReadAllText(draftPath))!;
                node["uncertainties"] = JsonNode.Parse("[" + string.Join(",", entries) + "]");
                return node.ToJsonString();
            }
            var over = Out();
            Expect("1001 counted uncertainties are refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("unc-1001.json", WithUncertainties(Enumerable.Repeat(Entry, 1001))), over);
            Check(!File.Exists(over), "the refused 1001-uncertainty draft writes no file");
            var capped = Enumerable.Repeat(Entry, 1000).Append(Entry.Replace("duplicate-text", "text-limit", StringComparison.Ordinal)).Append(Entry.Replace("duplicate-text", "uncertainty-limit", StringComparison.Ordinal));
            var o = Out(); var r = Cli("cad-plan-brief", Input("unc-capped.json", WithUncertainties(capped)), o);
            Check(Ok(r, o), "a capped report (1000 counted, a limit entry and uncertainty-limit) is accepted", R(r));
            var twice = Enumerable.Repeat(Entry.Replace("duplicate-text", "uncertainty-limit", StringComparison.Ordinal), 2);
            Expect("two uncertainty-limit entries are refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("unc-limit2.json", WithUncertainties(twice)), Out());
            var rooms = JsonNode.Parse(File.ReadAllText(draftPath))!;
            rooms["rooms"] = JsonNode.Parse("[" + string.Join(",", Enumerable.Repeat("{}", 10001)) + "]");
            Expect("10001 room proposals are refused", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("rooms-10001.json", rooms.ToJsonString()), Out());
        }

        // ---- SF2 label re-binding on a hand-built draft ----
        {
            static BriefTextSource Source(string text, double x) => new("hand.dxf", "dxf", "1A", "ROOM-TEXT", "TEXT", 10, null, Array.Empty<string>(), new Bim.Cad.Point2(x, 0), text);
            static BriefRoomProposal Proposal(string id, string name, BriefTextSource label) => new(id, name, 12, null, 0.9, label, null, null, Array.Empty<string>());
            static BriefDraft Hand(BriefRoomProposal[] rooms, BriefExtractUncertainty[] uncertainties) =>
                new("hand.dxf", "dxf", 0.001, new ProjectBrief(1, "Hand", new BriefConstraints(), rooms.Select(p => new BriefRoom(p.Id, p.Name, TargetAreaSquareMetres: new BriefValue<double>(12))).ToArray(), Array.Empty<BriefAdjacency>()),
                    rooms, uncertainties, Array.Empty<BriefIssue>());
            static string DraftJson(BriefDraft d) =>
                "{\"kind\":\"cad-brief\",\"version\":1,\"persisted\":false,\"source\":{\"file\":\"hand.dxf\",\"format\":\"dxf\",\"sha256\":\"00\"},\"brief\":" + BriefSerializer.Serialize(d.Brief)
                + ",\"rooms\":" + JsonSerializer.Serialize(d.Rooms) + ",\"uncertainties\":" + JsonSerializer.Serialize(d.Uncertainties) + ",\"issues\":[]}";
            string Show(BriefPlannerResult x) => "kept " + string.Join(",", x.Kept.Select(k => k.Id)) + " dropped " + string.Join(",", x.Dropped.Select(k => k.Id + "[" + string.Join(",", k.Reasons) + "]"));

            // Different slugs; the duplicate-label uncertainty names living and quotes cocina's label as its first source.
            var cocina = Source("Cocina", 0); var living = Source("Living", 5000);
            var shared = Hand(new[] { Proposal("cocina", "Cocina", cocina), Proposal("living", "Living", living) },
                new[] { new BriefExtractUncertainty("duplicate-label", "dup", "living", null, new[] { cocina }) });
            var expected = BriefPlanner.ToPlannerBrief(shared);
            Check(expected.Dropped.Count == 1 && expected.Dropped[0].Id == "living" && expected.Dropped[0].Reasons.SequenceEqual(new[] { "duplicate-label" }),
                "oracle: with the shared label instance living is dropped as duplicate-label", Show(expected));
            var copied = shared with { Uncertainties = new[] { new BriefExtractUncertainty("duplicate-label", "dup", "living", null, new[] { cocina with { InsertPath = new List<string>() } }) } };
            Check(BriefPlanner.ToPlannerBrief(copied).Dropped.Count == 0, "oracle: without re-binding (a distinct label record) living would be kept, so this test discriminates", Show(BriefPlanner.ToPlannerBrief(copied)));
            var o = Out(); var r = Cli("cad-plan-brief", Input("hand-rebind.json", DraftJson(shared)), o);
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same, "the CLI re-binds an uncertainty source to the equal proposal label (equals the shared-instance library result)", R(r) + " " + why);

            // Two proposals with value-identical labels are read as one shared label record.
            var both = Hand(new[] { Proposal("cocina", "Cocina", cocina), Proposal("living", "Living", cocina) },
                new[] { new BriefExtractUncertainty("duplicate-label", "dup", "living", null, new[] { cocina }) });
            var expectedBoth = BriefPlanner.ToPlannerBrief(both);
            var o2 = Out(); var r2 = Cli("cad-plan-brief", Input("hand-identical.json", DraftJson(both)), o2);
            var same2 = Matches(o2, expectedBoth, out var why2);
            Check(Ok(r2, o2) && same2 && expectedBoth.Dropped.Count == 1 && expectedBoth.Dropped[0].Id == "living",
                "value-identical proposal labels are one shared record (equals the shared-instance library result)", R(r2) + " " + why2 + " " + Show(expectedBoth));
        }

        // ---- nits ----
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(scratch, "dangling.json");
            var target = Path.Combine(scratch, "nowhere.json");
            File.CreateSymbolicLink(link, target);
            // Not through Expect: File.Exists is true for a dangling link on Unix, and reading it would throw.
            var r = Cli("cad-plan-brief", draftPath, link);
            Check(r.Exit == 1 && Parse(r.Out) is { } e && e.GetProperty("status").GetString() == "failed" && e.GetProperty("code").GetString() == "output_link_rejected",
                "refuses a dangling symlink output", R(r));
            Check(!File.Exists(target) && new FileInfo(link).LinkTarget == target, "the symlink output target is not created and the link is left as it was");
            File.Delete(link);
        }
        {
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--min-confidence", "-0");
            Check(Ok(r, o) && Doc(o) is { } d && d.GetProperty("options").GetRawText().StartsWith("{\"min_confidence\":0,", StringComparison.Ordinal), "--min-confidence -0 is echoed as 0", R(r));
        }
        Expect("--min-confidence Infinity", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--min-confidence", "Infinity");
        Expect("--min-confidence 1e400 (overflows to infinity)", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--min-confidence", "1e400");
        {
            var node = JsonNode.Parse(File.ReadAllText(draftPath))!;
            node.AsObject().Remove("source");
            var o = Out(); var r = Cli("cad-plan-brief", Input("no-source.json", node.ToJsonString()), o);
            Check(Ok(r, o) && Matches(o, BriefPlanner.ToPlannerBrief(library), out _) && Doc(o) is { } d && d.GetProperty("source").GetProperty("drawing_file").ValueKind == JsonValueKind.Null
                && d.GetProperty("source").GetProperty("drawing_sha256").ValueKind == JsonValueKind.Null, "a draft without a source block plans the same and reports null drawing_file/drawing_sha256", R(r));
        }

        // ---- P6-12 --site-width w --site-depth d (metres, PrototypeLot bounds) and --name text ----
        static BriefConstraints Sited(BriefConstraints constraints, double width, double depth) =>
            constraints with { Site = new BriefSite(new BriefValue<double>(width), new BriefValue<double>(depth)) };
        static string Plan(Bim.Generative.SpacePlan plan) =>
            plan.Status + " rooms " + string.Join(",", plan.Rooms.Select(p => p.RoomId)) + " violations " + string.Join(";", plan.Violations.Select(v => v.Code + ":" + v.Subject + ":" + v.Detail));
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Constraints = Sited(library.Brief.Constraints, 10, 8), Name = "Casa Ader" });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--site-width", "10", "--site-depth", "8", "--name", "Casa Ader");
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same && expected.IsValid, "--site-width 10 --site-depth 8 --name equals the library with that site and name", R(r) + " " + why);
            Check(Doc(o) is { } p && p.GetProperty("options").GetRawText() == "{\"min_confidence\":0.5,\"confirmed_rooms\":[],\"confirmed_reasons\":[],\"site_width_m\":10,\"site_depth_m\":8,\"name\":\"Casa Ader\"}",
                "options echo the site and the name after the existing members", Doc(o)?.GetProperty("options").GetRawText() ?? "");
            ProjectBrief? parsed = null;
            try { if (Doc(o) is { } d) parsed = BriefParser.Parse(d.GetProperty("brief").GetRawText()); }
            catch (Exception e) when (e is BriefException or KeyNotFoundException or InvalidOperationException) { Check(false, "the sited brief parses back", e.Message); }
            Check(parsed is { Name: "Casa Ader", Constraints.Site: { } s } && s.WidthMetres.Value == 10 && s.DepthMetres.Value == 8 && BriefValidator.Validate(parsed).Length == 0,
                "the written brief parses back with the site and name and validates", Doc(o)?.GetProperty("brief").GetRawText() ?? "");
            if (parsed is not null)
            {
                var plan = Bim.Generative.SpacePlanner.Plan(parsed, 7);
                Check(plan.IsFeasible && plan.HardViolations.Count == 0 && plan.Rooms.Count > 0
                    && plan.Rooms.All(x => x.Rect.X >= 0 && x.Rect.Y >= 0 && x.Rect.Right <= 10 + 1e-9 && x.Rect.Top <= 8 + 1e-9),
                    "the space planner plans the written brief feasibly inside the 10 x 8 site", Plan(plan));
                var inProcess = Bim.Generative.SpacePlanner.Plan(expected.Brief, 7);
                Check(Bim.Generative.SpacePlanSerializer.Serialize(plan) == Bim.Generative.SpacePlanSerializer.Serialize(inProcess), "the plan of the written brief equals the in-process plan");
                var unsited = Bim.Generative.SpacePlanner.Plan(BriefPlanner.ToPlannerBrief(library).Brief, 7);
                Check(!unsited.IsFeasible && unsited.Violations.Any(v => v.Code == Bim.Generative.PlanCodes.FootprintMissing), "oracle: the default (unsited) brief is refused by the planner with footprint_missing", Plan(unsited));
            }
            var o2 = Out(); var r2 = Cli("cad-plan-brief", draftPath, o2, "--name", "Casa Ader", "--site-depth", "8", "--site-width", "10");
            Check(Ok(r2, o2) && File.Exists(o) && File.ReadAllBytes(o2).SequenceEqual(File.ReadAllBytes(o)), "site and name option order does not change the output", R(r2));
            var o3 = Out(); var r3 = Cli("cad-plan-brief", draftPath, o3, "--site-width", "10", "--site-depth", "8", "--name", "Casa Ader");
            Check(Ok(r3, o3) && File.Exists(o) && File.ReadAllBytes(o3).SequenceEqual(File.ReadAllBytes(o)), "sited reruns are byte-identical", R(r3));
        }
        {
            // Site only: the draft name is kept; name only: no site is added.
            var siteOnly = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Constraints = Sited(library.Brief.Constraints, 12.5, 9.25) });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--site-depth", "9.25", "--site-width", "1.25e1");
            var same = Matches(o, siteOnly, out var why);
            Check(Ok(r, o) && same && Doc(o) is { } d && d.GetProperty("brief").GetProperty("name").GetString() == library.Brief.Name
                && d.GetProperty("options").GetRawText().EndsWith(",\"site_width_m\":12.5,\"site_depth_m\":9.25}", StringComparison.Ordinal),
                "a site without --name keeps the draft name (library equal; exponent accepted)", R(r) + " " + why + " " + (Doc(o)?.GetProperty("options").GetRawText() ?? ""));
            var nameOnly = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Name = "Casa Áder 2" });
            var o2 = Out(); var r2 = Cli("cad-plan-brief", draftPath, o2, "--name", "Casa Áder 2");
            var same2 = Matches(o2, nameOnly, out var why2);
            Check(Ok(r2, o2) && same2 && Doc(o2) is { } d2 && d2.GetProperty("brief").GetProperty("name").GetString() == "Casa Áder 2" && d2.GetProperty("options").GetProperty("name").GetString() == "Casa Áder 2"
                && !d2.GetProperty("options").TryGetProperty("site_width_m", out _) && !d2.GetProperty("brief").GetProperty("constraints").TryGetProperty("site", out _),
                "--name alone renames the brief (non-ASCII kept) and adds no site", R(r2) + " " + why2);
            foreach (var (w, dp) in new[] { ("3", "100"), ("100", "3") })
            {
                var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Constraints = Sited(library.Brief.Constraints, double.Parse(w, CultureInfo.InvariantCulture), double.Parse(dp, CultureInfo.InvariantCulture)) });
                var ob = Out(); var rb = Cli("cad-plan-brief", draftPath, ob, "--site-width", w, "--site-depth", dp);
                var sameb = Matches(ob, expected, out var whyb);
                Check(Ok(rb, ob) && sameb, "site " + w + " x " + dp + " (PrototypeLot bounds, inclusive) is accepted", R(rb) + " " + whyb);
            }
            var longName = new string('n', 2048);
            var o4 = Out(); var r4 = Cli("cad-plan-brief", draftPath, o4, "--name", longName);
            Check(Ok(r4, o4) && Doc(o4) is { } d4 && d4.GetProperty("brief").GetProperty("name").GetString() == longName, "a 2048-character name is accepted", R(r4));
        }
        {
            // Other draft constraints are kept and a draft site is replaced.
            var node = JsonNode.Parse(File.ReadAllText(draftPath))!;
            node["brief"]!["constraints"] = JsonNode.Parse("{\"jurisdiction\":\"CABA\",\"site\":{\"width_m\":50,\"depth_m\":40},\"levels\":1,\"total_area_budget_m2\":90}");
            var constrained = Input("constrained.json", node.ToJsonString());
            var draftConstraints = new BriefConstraints(new BriefValue<string>("CABA"), new BriefSite(new BriefValue<double>(50), new BriefValue<double>(40)), new BriefValue<int>(1), new BriefValue<double>(90));
            var constrainedDraft = library with { Brief = library.Brief with { Constraints = draftConstraints } };
            var expected = BriefPlanner.ToPlannerBrief(constrainedDraft, new BriefPlannerOptions { Constraints = Sited(draftConstraints, 10, 8) });
            var o = Out(); var r = Cli("cad-plan-brief", constrained, o, "--site-width", "10", "--site-depth", "8");
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same && Doc(o) is { } d && d.GetProperty("brief").GetProperty("constraints").GetRawText() == "{\"jurisdiction\":\"CABA\",\"site\":{\"width_m\":10,\"depth_m\":8},\"levels\":1,\"total_area_budget_m2\":90}",
                "the site option replaces only the draft site; jurisdiction, levels and budget are kept", R(r) + " " + why + " " + (Doc(o)?.GetProperty("brief").GetProperty("constraints").GetRawText() ?? ""));
            var o2 = Out(); var r2 = Cli("cad-plan-brief", constrained, o2);
            Check(Ok(r2, o2) && Matches(o2, BriefPlanner.ToPlannerBrief(constrainedDraft), out _) && Doc(o2) is { } d2 && d2.GetProperty("brief").GetProperty("constraints").GetProperty("site").GetRawText() == "{\"width_m\":50,\"depth_m\":40}",
                "without site options the draft site is kept", R(r2));
        }
        foreach (var bad in new[] { "2.999", "100.001", "0", "-10", "NaN", "Infinity", "-Infinity", "1e400", "abc", "10,5", "", " 10", "0x10", "10 ", "1 0", "-0" })
        {
            Expect("--site-width '" + bad + "' is refused", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--site-width", bad, "--site-depth", "8");
            Expect("--site-depth '" + bad + "' is refused", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--site-width", "10", "--site-depth", bad);
        }
        Expect("--site-width without --site-depth", "cad_option_pair_missing", "cad-plan-brief", draftPath, Out(), "--site-width", "10");
        Expect("--site-depth without --site-width", "cad_option_pair_missing", "cad-plan-brief", draftPath, Out(), "--site-depth", "8", "--name", "Casa");
        Expect("--site-width twice", "cad_option_duplicate", "cad-plan-brief", draftPath, Out(), "--site-width", "10", "--site-depth", "8", "--site-width", "10");
        Expect("--site-depth twice", "cad_option_duplicate", "cad-plan-brief", draftPath, Out(), "--site-depth", "8", "--site-width", "10", "--site-depth", "9");
        Expect("--name twice", "cad_option_duplicate", "cad-plan-brief", draftPath, Out(), "--name", "a", "--name", "a");
        Expect("--name empty", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--name", "");
        Expect("--name blank", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--name", "   ");
        Expect("--name over 2048 characters", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--name", new string('n', 2049));
        Expect("--site-width without a value", "cad_option_value_missing", "cad-plan-brief", draftPath, Out(), "--site-depth", "8", "--site-width");
        Expect("--name without a value", "cad_option_value_missing", "cad-plan-brief", draftPath, Out(), "--name");
        Expect("a missing site pair is refused before any I/O", "cad_option_pair_missing", "cad-plan-brief", Path.Combine(scratch, "missing.json"), Out(), "--site-width", "10");
        {
            var r = Cli("cad-plan-brief", draftPath, Out(), "--site-width", "500", "--site-depth", "8");
            Check(Parse(r.Out) is { } e && e.TryGetProperty("detail", out var detail) && detail.GetString() is { } text && text.Contains("--site-width", StringComparison.Ordinal) && text.Contains("100", StringComparison.Ordinal),
                "an out-of-bounds site refusal names the option and the bounds", r.Out);
            var r2 = Cli("cad-plan-brief", draftPath, Out(), "--site-width", "10");
            Check(Parse(r2.Out) is { } e2 && e2.TryGetProperty("detail", out var detail2) && detail2.GetString() is { } text2 && text2.Contains("--site-depth", StringComparison.Ordinal),
                "a missing pair refusal names the missing option", r2.Out);
        }
        {
            var r = Cli("cad-plan-brief");
            Check(Parse(r.Out) is { } e && e.GetProperty("code").GetString() is { } usage && usage.Contains("[--site-width w --site-depth d]", StringComparison.Ordinal) && usage.Contains("[--name text]", StringComparison.Ordinal),
                "the usage names the site and name options", R(r));
        }

        // ---- review nits ----
        // Option refusals come before the output checks.
        Expect("a missing site pair is refused before the output extension", "cad_option_pair_missing", "cad-plan-brief", draftPath, Out(".txt"), "--site-width", "10");
        {
            var existing = Out(); File.WriteAllBytes(existing, new byte[] { 5, 6 });
            Expect("an out-of-bounds site is refused before output_exists", "cad_option_value_invalid", "cad-plan-brief", draftPath, existing, "--site-width", "500", "--site-depth", "8");
            Check(File.ReadAllBytes(existing).SequenceEqual(new byte[] { 5, 6 }), "the existing output is untouched by a site refusal");
        }
        // Grammar: explicit plus sign and a trailing decimal point are accepted.
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Constraints = Sited(library.Brief.Constraints, 10, 8) });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--site-width", "+10", "--site-depth", "8.");
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same, "--site-width +10 --site-depth 8. equals the library 10 x 8", R(r) + " " + why);
        }
        // Mixed order: the first bad option decides.
        Expect("--site-width abc alone is a value refusal (before the pair check)", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--site-width", "abc");
        Expect("--site-width 10 then an unknown option is cad_option_unknown", "cad_option_unknown", "cad-plan-brief", draftPath, Out(), "--site-width", "10", "--bogus", "x");
        // A name with a valid surrogate pair is accepted. (A lone surrogate cannot be passed through argv: the OS/runtime
        // re-encodes arguments as UTF-8 and replaces it, so BriefParser.IsText's lone-surrogate refusal is untestable here.)
        {
            var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Name = "Casa \U0001F3E0" });
            var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--name", "Casa \U0001F3E0");
            var same = Matches(o, expected, out var why);
            Check(Ok(r, o) && same && Doc(o) is { } dn && dn.GetProperty("brief").GetProperty("name").GetString() == "Casa \U0001F3E0" && dn.GetProperty("options").GetProperty("name").GetString() == "Casa \U0001F3E0",
                "a name with a surrogate pair is accepted and equals the library", R(r) + " " + why);
        }
        // Culture: the child process runs under a comma-decimal culture; the option grammar stays invariant.
        {
            var german = false;
            try { german = CultureInfo.GetCultureInfo("de-DE").NumberFormat.NumberDecimalSeparator == ","; }
            catch (CultureNotFoundException) { }
            if (!german) Console.WriteLine("SKIP cad-plan-brief: de-DE culture data unavailable (invariant globalization); culture check not run");
            else
            {
                var env = new Dictionary<string, string> { ["LANG"] = "de_DE.UTF-8", ["LC_ALL"] = "de_DE.UTF-8" };
                var bad = CliWith(env, "cad-plan-brief", draftPath, Out(), "--site-width", "10,5", "--site-depth", "8");
                Check(bad.Exit == 1 && Parse(bad.Out) is { } eb && eb.GetProperty("code").GetString() == "cad_option_value_invalid", "under de_DE, --site-width 10,5 is refused", R(bad));
                var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Constraints = Sited(library.Brief.Constraints, 12.5, 8) });
                var o = Out(); var r = CliWith(env, "cad-plan-brief", draftPath, o, "--site-width", "12.5", "--site-depth", "8");
                var same = Matches(o, expected, out var why);
                Check(Ok(r, o) && same && Doc(o) is { } dc && dc.GetProperty("options").GetProperty("site_width_m").GetRawText() == "12.5", "under de_DE, --site-width 12.5 is accepted and echoed invariantly", R(r) + " " + why);
            }
        }

        // ---- P6-14 --brief-only: the file is only the bare P6-03 planner brief ----
        // Definition: the written bytes are exactly the bytes of the full document's "brief" member (BriefSerializer
        // canonical text, UTF-8, no BOM, compact, no trailing newline), so the file feeds plan-layouts directly. Without
        // the flag the full document keeps its P6-12 bytes: the two digests below were taken from main's implementation
        // (red run 42983360377 of this test at fe1d63d, ubuntu/windows/macos agree, before the flag existed) and must not change.
        {
            const string PinnedDefaults = "1A092C0BC2ADC6B685D0D85490FAF803FAF0028C9F88614D9DA24002269ABA0F";
            const string PinnedSited = "69824E31E8B08AAF7B6D6D7E7809A3B165B344FFBB5092B23560705382645153";
            // The default run's stdout envelope with its machine-specific "output" string blanked (see below); from red run 42989848662 (ubuntu/windows/macos agree).
            const string PinnedDefaultsEnvelope = "B178D56BE662E41E86985523851ECFA755013FE4DC4395E00091B042E25889E1";
            static byte[] U(string s) => new UTF8Encoding(false).GetBytes(s);
            byte[] Member(string path) => Doc(path) is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("brief", out var b) ? U(b.GetRawText()) : Array.Empty<byte>();
            byte[] Bytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            bool Bare((int Exit, string Out, string Err) r, string output, bool valid) =>
                Ok(r, output) && Parse(r.Out) is { } e && e.TryGetProperty("brief_only", out var flag) && flag.ValueKind == JsonValueKind.True && e.GetProperty("valid").GetBoolean() == valid;
            bool NoFlag((int Exit, string Out, string Err) r) => Parse(r.Out) is { ValueKind: JsonValueKind.Object } e && !e.TryGetProperty("brief_only", out _);
            string[] SitedArgs = { "--site-width", "10", "--site-depth", "8", "--name", "Casa Ader" };

            // Defaults.
            var defaults = BriefPlanner.ToPlannerBrief(library);
            var full = Out(); var rf = Cli("cad-plan-brief", draftPath, full);
            Check(Ok(rf, full) && NoFlag(rf), "without --brief-only the envelope has no brief_only member", R(rf));
            // The "output" member is the machine-specific path: that one JSON string value, encoded as the CLI's
            // Utf8JsonWriter writes it (default encoder, like JsonSerializer), is blanked before hashing.
            var encodedOutput = "\"output\":" + JsonSerializer.Serialize(full) + ",";
            var normalized = rf.Out.Split(encodedOutput).Length == 2 ? rf.Out.Replace(encodedOutput, "\"output\":\"\",", StringComparison.Ordinal) : "";
            Check(Sha(U(normalized)) == PinnedDefaultsEnvelope, "without --brief-only the default stdout envelope keeps its pinned bytes (output path blanked)",
                "actual " + Sha(U(normalized)) + " pinned " + PinnedDefaultsEnvelope + " envelope " + normalized);
            Check(Sha(Bytes(full)) == PinnedDefaults, "without --brief-only the default full document keeps its pinned P6-12 bytes", "actual " + Sha(Bytes(full)) + " pinned " + PinnedDefaults);
            var bare = Out(); var rb = Cli("cad-plan-brief", draftPath, bare, "--brief-only");
            Check(Bare(rb, bare, true), "--brief-only writes a file and a succeeded envelope with brief_only true", R(rb));
            Check(Bytes(bare).Length > 0 && Bytes(bare).SequenceEqual(Member(full)), "--brief-only bytes equal the full document's brief member bytes (defaults)",
                Encoding.UTF8.GetString(Bytes(bare)) + " vs " + Encoding.UTF8.GetString(Member(full)));
            Check(Bytes(bare).SequenceEqual(U(BriefSerializer.Serialize(defaults.Brief))), "--brief-only bytes equal BriefSerializer.Serialize of the library planner brief (defaults)");

            // Sited and named: the brief plan-layouts accepts.
            var sited = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Constraints = Sited(library.Brief.Constraints, 10, 8), Name = "Casa Ader" });
            var fullSited = Out(); var rfs = Cli(new[] { "cad-plan-brief", draftPath, fullSited }.Concat(SitedArgs).ToArray());
            Check(Ok(rfs, fullSited) && NoFlag(rfs), "the sited full document still writes without brief_only in the envelope", R(rfs));
            Check(Sha(Bytes(fullSited)) == PinnedSited, "without --brief-only the sited full document keeps its pinned P6-12 bytes", "actual " + Sha(Bytes(fullSited)) + " pinned " + PinnedSited);
            var bareSited = Out(); var rbs = Cli(new[] { "cad-plan-brief", draftPath, bareSited }.Concat(SitedArgs).Append("--brief-only").ToArray());
            var bytes = Bytes(bareSited);
            Check(Bare(rbs, bareSited, true) && Parse(rbs.Out) is { } es && es.GetProperty("kept").GetInt32() == 6 && es.GetProperty("dropped").GetInt32() == 1 && es.GetProperty("issues").GetInt32() == 0,
                "sited --brief-only envelope keeps the kept/dropped/issues counts", R(rbs));
            Check(bytes.Length > 0 && bytes.SequenceEqual(Member(fullSited)), "sited --brief-only bytes equal the full document's brief member bytes");
            Check(bytes.SequenceEqual(U(BriefSerializer.Serialize(sited.Brief))), "sited --brief-only bytes equal BriefSerializer.Serialize of the library planner brief");
            Check(bytes.Length > 2 && Encoding.UTF8.GetString(bytes).StartsWith("{\"brief_version\":", StringComparison.Ordinal) && bytes[^1] == (byte)'}' && !bytes.Contains((byte)'\n') && !bytes.Contains((byte)'\r'),
                "the bare brief starts with brief_version, has no BOM, is compact and has no trailing newline", Encoding.UTF8.GetString(bytes));
            ProjectBrief? parsed = null;
            try { parsed = BriefParser.Parse(Encoding.UTF8.GetString(bytes)); }
            catch (BriefException e) { Check(false, "the bare brief parses with the strict P6-03 parser", e.Code); }
            Check(parsed is not null && U(BriefSerializer.Serialize(parsed)).SequenceEqual(bytes) && BriefValidator.Validate(parsed).Length == 0, "the bare brief is canonical (parse/serialize round trip) and validates");
            var layouts = Out(); var rl = Cli("plan-layouts", bareSited, layouts, "--alternatives", "2", "--seed", "7");
            Check(rl.Exit == 0 && File.Exists(layouts) && Parse(rl.Out) is { } el && el.GetProperty("status").GetString() == "succeeded", "plan-layouts reads the --brief-only file directly", R(rl));

            // Flag position and reruns.
            foreach (var order in new[] { new[] { "--brief-only", "--site-width", "10", "--site-depth", "8", "--name", "Casa Ader" }, new[] { "--site-width", "10", "--brief-only", "--name", "Casa Ader", "--site-depth", "8" } })
            {
                var o = Out(); var r = Cli(new[] { "cad-plan-brief", draftPath, o }.Concat(order).ToArray());
                Check(Bare(r, o, true) && Bytes(o).SequenceEqual(bytes), "--brief-only anywhere among the options writes the same bytes (" + string.Join(" ", order) + ")", R(r));
            }

            // An invalid planner brief is still written bare, valid:false, exit 0.
            var node = JsonNode.Parse(File.ReadAllText(draftPath))!;
            foreach (var room in node["rooms"]!.AsArray()) room!["Confidence"] = 0.1;
            var low = Input("low-bare.json", node.ToJsonString());
            var lowExpected = BriefPlanner.ToPlannerBrief(library with { Rooms = library.Rooms.Select(p => p with { Confidence = 0.1 }).ToArray() });
            var lowFull = Out(); var rlf = Cli("cad-plan-brief", low, lowFull);
            var lowBare = Out(); var rlb = Cli("cad-plan-brief", low, lowBare, "--brief-only");
            Check(!lowExpected.IsValid && Bare(rlb, lowBare, false) && Ok(rlf, lowFull) && Bytes(lowBare).SequenceEqual(Member(lowFull)) && Bytes(lowBare).SequenceEqual(U(BriefSerializer.Serialize(lowExpected.Brief))),
                "an invalid planner brief is written bare with valid false in the envelope and exit 0", R(rlb));

            // Refusals are unchanged with the flag.
            Expect("--brief-only twice", "cad_option_duplicate", "cad-plan-brief", draftPath, Out(), "--brief-only", "--brief-only");
            Expect("--brief-only takes no value (the next word is an option)", "cad_option_unknown", "cad-plan-brief", draftPath, Out(), "--brief-only", "true");
            Expect("--brief-only=true is not the flag", "cad_option_unknown", "cad-plan-brief", draftPath, Out(), "--brief-only=true");
            Expect("--min-confidence does not take --brief-only as its value", "cad_option_value_invalid", "cad-plan-brief", draftPath, Out(), "--min-confidence", "--brief-only");
            Expect("--brief-only with a missing site pair", "cad_option_pair_missing", "cad-plan-brief", draftPath, Out(), "--brief-only", "--site-width", "10");
            Expect("--brief-only with a missing input", "input_not_found", "cad-plan-brief", Path.Combine(scratch, "missing.json"), Out(), "--brief-only");
            Expect("--brief-only with a non-.json output", "output_extension_unsupported", "cad-plan-brief", draftPath, Out(".txt"), "--brief-only");
            Expect("--brief-only with a document that is not a cad-brief draft", "cad_plan_brief_input_invalid", "cad-plan-brief", Input("kind-bare.json", File.ReadAllText(draftPath).Replace("\"kind\":\"cad-brief\"", "\"kind\":\"cad-draft\"", StringComparison.Ordinal)), Out(), "--brief-only");
            var unknown = JsonNode.Parse(File.ReadAllText(draftPath))!;
            unknown["brief"]!["surprise"] = 1;
            Expect("--brief-only with a brief parser refusal", "brief_member_unknown", "cad-plan-brief", Input("brief-member-bare.json", unknown.ToJsonString()), Out(), "--brief-only");
            var existing = Out(); File.WriteAllBytes(existing, new byte[] { 3, 4 });
            Expect("--brief-only never overwrites", "output_exists", "cad-plan-brief", draftPath, existing, "--brief-only");
            Check(File.ReadAllBytes(existing).SequenceEqual(new byte[] { 3, 4 }), "--brief-only leaves an existing output untouched");
            Expect("--brief-only: option refusals come before any I/O", "cad_option_unknown", "cad-plan-brief", Path.Combine(scratch, "missing.json"), existing, "--brief-only", "--nope", "1");
            // Pinned current behaviour: a value option takes the next word positionally, so --name --brief-only names the
            // brief "--brief-only" and writes the full document (no flag).
            {
                var o = Out(); var r = Cli("cad-plan-brief", draftPath, o, "--name", "--brief-only");
                var expected = BriefPlanner.ToPlannerBrief(library, new BriefPlannerOptions { Name = "--brief-only" });
                var same = Matches(o, expected, out var why);
                Check(Ok(r, o) && NoFlag(r) && same && Doc(o) is { } dn && dn.GetProperty("kind").GetString() == "cad-plan-brief"
                    && dn.GetProperty("options").GetProperty("name").GetString() == "--brief-only" && dn.GetProperty("brief").GetProperty("name").GetString() == "--brief-only",
                    "--name --brief-only consumes the word as the name: full document, no brief_only, options.name \"--brief-only\"", R(r) + " " + why);
            }
            var ru = Cli("cad-plan-brief");
            Check(Parse(ru.Out) is { } eu && eu.GetProperty("code").GetString() is { } usage && usage.EndsWith(" [--brief-only]", StringComparison.Ordinal), "the usage names --brief-only", R(ru));
            var rd = Cli("no-such-verb");
            Check(rd.Out.Contains("[--site-width w --site-depth d] [--name text] [--brief-only] |", StringComparison.Ordinal), "the default usage names cad-plan-brief --brief-only", rd.Out.Length > 600 ? rd.Out[..600] : rd.Out);
        }
    }
}
