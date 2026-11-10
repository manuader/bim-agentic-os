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

        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
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
    }
}
