using Bim.Core;
using Bim.Revit;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
// P1-13 query.elements pagination cursor contract oracle (core/Bim.Core/RevitCommandsQuery.cs): an opaque next_cursor
// bound to the document identity, the filter digest and the last ascending element id; resuming after that id with the
// same filter; typed refusals of malformed, tampered, stale, foreign and filter-mismatched cursors before the pipe; and
// concatenated pages equal to one unlimited query on fixture data. No Revit is involved: unit-tested evidence only.
static class ElementQueryCursorChecks
{
    const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string OtherDoc = "11111111-2222-3333-4444-555555555555-0001fd0b";
    const string L1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string L2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000138";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string Pipe = "bim-agentic-os-2025-4242";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Codes();
        Schema();
        Encoding_();
        Paging();
        Reply();
        Refusals();
        BeforeThePipe();
        Concatenation();
        ByteBudgetPages();
        VanishedRows();
        ModelChangesBetweenPages();
        Vector();
    }

    // ---- fixture: 23 elements over walls and doors on two levels, ids deliberately unordered and sparse ----
    sealed record Fixture(long Id, string BuiltIn, string Level);
    static readonly Fixture[] Model = Enumerable.Range(0, 23)
        .Select(i => new Fixture(1000 + ((i * 7919) % 101) * 3, i % 3 == 0 ? "OST_Doors" : "OST_Walls", i % 2 == 0 ? L1 : L2))
        .GroupBy(f => f.Id).Select(g => g.First()).Reverse().ToArray();

    static string Uid(long id) => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + id.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    static JsonObject Envelope(JsonNode args, string document = Doc) => new() { ["protocol_version"] = 1, ["operation_id"] = "op-cursor", ["document"] = new JsonObject { ["path"] = RealPath, ["unique_id"] = document }, ["command"] = "query.elements", ["args"] = args };

    static JsonObject Args(string? category, string? level, int limit, string? cursor = null, params string[] parameters)
    {
        var args = new JsonObject();
        if (category is not null) args["category"] = category;
        if (level is not null) args["level"] = level;
        if (parameters.Length > 0) args["parameters"] = new JsonArray(parameters.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        args["limit"] = limit;
        if (cursor is not null) args["cursor"] = cursor;
        return args;
    }

    // The add-in's decisions without Revit: filter, page after the cursor id, read rows, write the reply in core.
    static string Collect(string envelopeJson, Func<long, ObservedQueryElement>? row = null, Fixture[]? model = null, Func<long, bool>? vanished = null)
    {
        model ??= Model;
        var envelope = RevitCommandProtocol.Parse(envelopeJson);
        var args = (ElementQueryArgs)envelope.Args;
        var matched = model.Where(f => (args.BuiltInCategory is null || f.BuiltIn == args.BuiltInCategory) && (args.Level is null || f.Level == args.Level)).Select(f => f.Id).ToList();
        var (selected, truncated) = RevitElementQueryRules.Page(matched, args.Limit, args.Cursor?.After);
        var rows = selected.Where(id => vanished?.Invoke(id) != true).Select(id => row?.Invoke(id) ?? new ObservedQueryElement(id, Uid(id), "Walls", model.First(f => f.Id == id).BuiltIn)).ToArray();
        return Invariant(RevitElementQueryJson.Elements(envelope.Document.UniqueId, args, null, matched.Distinct().Count(), truncated, rows, selected));
    }

    // Every query.elements reply: truncated exactly when next_cursor is present, and a present cursor decodes.
    static string Invariant(string reply)
    {
        Check(HoldsInvariant(reply), "query.elements reply: truncated == (next_cursor != null) and next_cursor decodes", reply.Length > 400 ? reply[..400] : reply);
        return reply;
    }

    public static bool HoldsInvariant(string reply)
    {
        var root = JsonNode.Parse(reply)!;
        var next = root["next_cursor"];
        var ok = root.AsObject().ContainsKey("next_cursor") && root["truncated"]!.GetValue<bool>() == (next is not null);
        if (ok && next is not null)
        {
            try { RevitElementQueryCursor.Decode(next.GetValue<string>(), "next_cursor"); }
            catch (RevitCommandException) { ok = false; }
        }
        return ok;
    }

    static RevitCommandException? Refusal(JsonNode args, string document = Doc)
    {
        try { RevitCommandProtocol.Parse(Envelope(args, document).ToJsonString()); return null; }
        catch (RevitCommandException error) { return error; }
    }

    static void Refuses(string name, JsonNode args, string code, string document = Doc)
    {
        var error = Refusal(args, document);
        Check(error is not null && error.Code == code && error.Field == "args.cursor", name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }

    static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] Unb64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    static void Codes()
    {
        var codes = new[] { (RevitCommandCodes.CursorMalformed, "cursor_malformed"), (RevitCommandCodes.CursorTampered, "cursor_tampered"), (RevitCommandCodes.CursorStale, "cursor_stale"), (RevitCommandCodes.CursorForeignDocument, "cursor_foreign_document"), (RevitCommandCodes.CursorFilterMismatch, "cursor_filter_mismatch") };
        Check(codes.All(c => c.Item1 == c.Item2 && RevitCommandCodes.ClientSide.Contains(c.Item1) && !RevitCommandCodes.ExecutorReserved.Contains(c.Item1)), "the five cursor refusal codes are named and client-side (decided before the pipe)");
    }

    static void Schema()
    {
        var spec = RevitCommandCatalogue.Find("query.elements")!;
        var cursor = JsonNode.Parse(spec.ArgsSchemaJson)!["properties"]!["cursor"];
        Check(cursor is not null && cursor["type"]!.GetValue<string>() == "string" && cursor["maxLength"]!.GetValue<int>() == RevitElementQueryCursor.MaxChars && cursor["description"]!.GetValue<string>().Contains("next_cursor", StringComparison.Ordinal), "query.elements args schema (and so the MCP inputSchema) has an optional string cursor naming next_cursor", cursor?.ToJsonString() ?? "none");
        Check(JsonNode.Parse(spec.ArgsSchemaJson)!["required"] is null && spec.Description.Contains("next_cursor", StringComparison.Ordinal), "cursor is optional and the description tells how to resume");
        using var schema = JsonDocument.Parse(spec.ArgsSchemaJson);
        string? unsupported = null;
        try { RevitCommandSchema.CheckSupported(schema.RootElement); } catch (InvalidOperationException error) { unsupported = error.Message; }
        Check(unsupported is null, "the cursor schema stays in the enforced schema subset", unsupported ?? "");
    }

    static void Encoding_()
    {
        var filter = RevitElementQueryCursor.FilterDigest("OST_Walls", "Level 1", Array.Empty<string>());
        var cursor = RevitElementQueryCursor.Encode(Doc, filter, 1234);
        Check(System.Text.RegularExpressions.Regex.IsMatch(cursor, @"\A[A-Za-z0-9_-]+\z") && cursor.Length <= RevitElementQueryCursor.MaxChars, "a cursor is base64url text within the maximum length", cursor);
        Check(!cursor.Contains("1234", StringComparison.Ordinal) && !cursor.Contains(Doc, StringComparison.Ordinal), "a cursor is opaque: no id or UniqueId is readable in it");
        Check(RevitElementQueryCursor.Encode(Doc, filter, 1234) == cursor, "encoding is deterministic");
        var decoded = RevitElementQueryCursor.Decode(cursor, "args.cursor");
        Check(decoded == new ElementQueryCursor(Doc, filter, 1234), "decode returns the document, filter digest and last id");
        var worst = RevitElementQueryCursor.Encode(Doc.ToUpperInvariant(), filter, long.MaxValue);
        Check(worst.Length <= RevitElementQueryCursor.MaxChars, "the longest cursor fits the schema maximum", worst.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Check(System.Text.RegularExpressions.Regex.IsMatch(filter, @"\A[0-9a-f]{64}\z"), "the filter digest is lowercase SHA-256 hex");
        Check(RevitElementQueryCursor.FilterDigest("OST_Walls", "Level 1", new[] { "Mark" }) != filter
            && RevitElementQueryCursor.FilterDigest("OST_Doors", "Level 1", Array.Empty<string>()) != filter
            && RevitElementQueryCursor.FilterDigest("OST_Walls", "Level 2", Array.Empty<string>()) != filter
            && RevitElementQueryCursor.FilterDigest("OST_Walls", null, Array.Empty<string>()) != RevitElementQueryCursor.FilterDigest(null, "OST_Walls", Array.Empty<string>())
            && RevitElementQueryCursor.FilterDigest(null, null, new[] { "a", "b" }) != RevitElementQueryCursor.FilterDigest(null, null, new[] { "b", "a" })
            && RevitElementQueryCursor.FilterDigest(null, null, new[] { "a,b" }) != RevitElementQueryCursor.FilterDigest(null, null, new[] { "a", "b" }),
            "the filter digest separates category, level and parameter names (and their order) unambiguously");
    }

    static void Paging()
    {
        var (selected, truncated) = RevitElementQueryRules.Page(new long[] { 50, 10, 40, 20, 30, 20 }, 2, 20);
        Check(selected.SequenceEqual(new long[] { 30, 40 }) && truncated, "paging after an id keeps the next ascending ids strictly after it");
        (selected, truncated) = RevitElementQueryRules.Page(new long[] { 50, 10, 40, 20, 30 }, 2, 40);
        Check(selected.SequenceEqual(new long[] { 50 }) && !truncated, "the last page is not truncated");
        (selected, truncated) = RevitElementQueryRules.Page(new long[] { 10, 20 }, 5, 99);
        Check(selected.Count == 0 && !truncated, "a cursor past every id gives an empty, untruncated page");
        (selected, truncated) = RevitElementQueryRules.Page(new long[] { 10, 20, 30 }, 2, 15);
        Check(selected.SequenceEqual(new long[] { 20, 30 }) && !truncated, "an id deleted since the cursor was issued does not break resumption (strictly after the id)");
        (selected, truncated) = RevitElementQueryRules.Page(new long[] { 10, 20, 30 }, 2, null);
        Check(selected.SequenceEqual(new long[] { 10, 20 }) && truncated, "no cursor pages from the lowest id as before");
    }

    static void Reply()
    {
        var first = JsonNode.Parse(Collect(Envelope(Args("wall", null, 3)).ToJsonString()))!.AsObject();
        Check(string.Join(",", first.Select(m => m.Key)) == "category,built_in_category,level,limit,matched,returned,truncated,truncated_reason,after,next_cursor,elements", "the reply adds after and next_cursor before elements", first.ToJsonString());
        Check(first["truncated"]!.GetValue<bool>() && first["after"] is null && first["next_cursor"] is JsonValue, "a truncated first page has after null and a next_cursor");
        var next = RevitElementQueryCursor.Decode(first["next_cursor"]!.GetValue<string>(), "args.cursor");
        var lastId = first["elements"]!.AsArray().Last()!["id"]!.GetValue<long>();
        Check(next.After == lastId && next.Document == Doc && next.Filter == RevitElementQueryCursor.FilterDigest("OST_Walls", null, Array.Empty<string>()), "next_cursor binds the document, the filter digest and the last returned id");
        var second = JsonNode.Parse(Collect(Envelope(Args("wall", null, 3, first["next_cursor"]!.GetValue<string>())).ToJsonString()))!;
        Check(second["after"]!.GetValue<long>() == lastId && second["elements"]!.AsArray().All(e => e!["id"]!.GetValue<long>() > lastId), "the next page reports after and holds only ids after it");
        Check(second["matched"]!.GetValue<int>() == first["matched"]!.GetValue<int>(), "matched counts the whole filter on every page");
        var all = JsonNode.Parse(Collect(Envelope(Args("wall", null, 5000)).ToJsonString()))!;
        Check(!all["truncated"]!.GetValue<bool>() && all["next_cursor"] is null && all.AsObject().ContainsKey("next_cursor"), "an untruncated reply has next_cursor null");
    }

    static void Refusals()
    {
        var filter = RevitElementQueryCursor.FilterDigest("OST_Walls", null, Array.Empty<string>());
        var good = RevitElementQueryCursor.Encode(Doc, filter, 1003);
        Check(Refusal(Args("wall", null, 3, good)) is null, "a cursor issued for this document and filter is accepted");
        var bound = RevitCommandProtocol.Parse(Envelope(Args("OST_Walls", null, 7, good)).ToJsonString()).Args as ElementQueryArgs;
        Check(bound is { Limit: 7, Cursor.After: 1003 }, "the same filter by its OST_ name and another page size resume the cursor");
        Check(Refusal(Args("wall", null, 3, good), Doc.ToUpperInvariant()) is null, "the document identity compares case-insensitively like the add-in admission");

        foreach (var (label, text) in new[] { ("empty", ""), ("non-base64url characters", "abc+/="), ("padding", "YWJj="), ("an impossible length", "a"), ("whitespace", "YW Jj"), ("non-canonical trailing bits", "YWJjZB") })
            Refuses("a cursor with " + label + " is refused cursor_malformed", Args("wall", null, 3, text), RevitCommandCodes.CursorMalformed);
        Refuses("a cursor of fullwidth letters is refused cursor_malformed", Args("wall", null, 3, "\uFF21\uFF22\uFF23\uFF24"), RevitCommandCodes.CursorMalformed);
        Refuses("a cursor with a non-ASCII letter is refused cursor_malformed", Args("wall", null, 3, "YWJj\u00e9"), RevitCommandCodes.CursorMalformed);
        Refuses("a JSON null cursor is refused by the schema", new JsonObject { ["category"] = "wall", ["cursor"] = null }, RevitCommandCodes.ArgsInvalid);
        Refuses("a numeric cursor is refused by the schema", new JsonObject { ["category"] = "wall", ["cursor"] = 7 }, RevitCommandCodes.ArgsInvalid);
        Refuses("a cursor over the maximum length is refused by the schema", Args("wall", null, 3, new string('A', RevitElementQueryCursor.MaxChars + 1)), RevitCommandCodes.ArgsInvalid);
        foreach (var (label, payload) in new[]
        {
            ("not JSON", "not json"), ("a JSON array", "[1]"), ("invalid UTF-8", "\uFFFD"), ("a missing member", """{"v":1,"document":"x"}"""),
            ("an extra member", "{\"v\":1,\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":1,\"check\":\"" + new string('0', 32) + "\",\"x\":1}"),
            ("a string id", "{\"v\":1,\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":\"1\",\"check\":\"" + new string('0', 32) + "\"}"),
            ("a fractional id", "{\"v\":1,\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":1.5,\"check\":\"" + new string('0', 32) + "\"}"),
            ("a document that is not a UniqueId", "{\"v\":1,\"document\":\"Tower\",\"filter\":\"" + filter + "\",\"after\":1,\"check\":\"" + new string('0', 32) + "\"}"),
            ("an id past the 64-bit range", "{\"v\":1,\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":9223372036854775808,\"check\":\"" + new string('0', 32) + "\"}"),
            ("an exponent id", "{\"v\":1,\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":1e3,\"check\":\"" + new string('0', 32) + "\"}"),
            ("a negative id", "{\"v\":1,\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":-5,\"check\":\"" + new string('0', 32) + "\"}"),
            ("a fractional version", "{\"v\":1.5,\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":1,\"check\":\"" + new string('0', 32) + "\"}"),
            ("no version", "{\"document\":\"" + Doc + "\",\"filter\":\"" + filter + "\",\"after\":1,\"check\":\"" + new string('0', 32) + "\"}"),
            ("an upper-case filter digest", "{\"v\":1,\"document\":\"" + Doc + "\",\"filter\":\"" + filter.ToUpperInvariant() + "\",\"after\":1,\"check\":\"" + new string('0', 32) + "\"}"),
        })
        {
            var bytes = label == "invalid UTF-8" ? new byte[] { 0xC3, 0x28, 0x7B } : Encoding.UTF8.GetBytes(payload);
            Refuses("a cursor carrying " + label + " is refused cursor_malformed", Args("wall", null, 3, B64(bytes)), RevitCommandCodes.CursorMalformed);
        }

        // Tampering: edit the decoded payload without recomputing its check.
        var json = JsonNode.Parse(Unb64(good))!.AsObject();
        Check(string.Join(",", json.Select(m => m.Key)) == "v,document,filter,after,check", "the cursor payload is v, document, filter, after and check");
        JsonObject Edit(Action<JsonObject> change) { var copy = JsonNode.Parse(json.ToJsonString())!.AsObject(); change(copy); return copy; }
        string Pack(JsonObject payload) => B64(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        Refuses("a cursor whose id was changed is refused cursor_tampered", Args("wall", null, 3, Pack(Edit(p => p["after"] = 1))), RevitCommandCodes.CursorTampered);
        Refuses("a cursor whose document was changed is refused cursor_tampered", Args("wall", null, 3, Pack(Edit(p => p["document"] = OtherDoc))), RevitCommandCodes.CursorTampered, OtherDoc);
        Refuses("a cursor whose filter digest was changed is refused cursor_tampered", Args("door", null, 3, Pack(Edit(p => p["filter"] = RevitElementQueryCursor.FilterDigest("OST_Doors", null, Array.Empty<string>())))), RevitCommandCodes.CursorTampered);
        Refuses("a cursor whose check was changed is refused cursor_tampered", Args("wall", null, 3, Pack(Edit(p => p["check"] = new string('0', 32)))), RevitCommandCodes.CursorTampered);
        Refuses("a cursor with reordered members is refused cursor_tampered", Args("wall", null, 3, B64(Encoding.UTF8.GetBytes("{\"document\":" + json["document"]!.ToJsonString() + ",\"v\":1,\"filter\":" + json["filter"]!.ToJsonString() + ",\"after\":1003,\"check\":" + json["check"]!.ToJsonString() + "}"))), RevitCommandCodes.CursorTampered);
        Refuses("a future-version payload with other members is refused cursor_stale (version read first)", Args("wall", null, 3, B64(Encoding.UTF8.GetBytes("{\"v\":2,\"page\":\"opaque\"}"))), RevitCommandCodes.CursorStale);
        Refuses("a cursor whose version was edited is refused cursor_stale", Args("wall", null, 3, Pack(Edit(p => p["v"] = 2))), RevitCommandCodes.CursorStale);
        Check(Throws<ArgumentOutOfRangeException>(() => RevitElementQueryCursor.Encode(Doc, filter, -1)), "no cursor is issued for a negative id");
        Refuses("a cursor with a version but a valid check is refused cursor_stale", Args("wall", null, 3, RevitElementQueryCursor.Encode(Doc, filter, 1003, version: 0)), RevitCommandCodes.CursorStale);

        // Well-formed, untampered cursors used out of context.
        Refuses("a cursor issued for another document is refused cursor_foreign_document", Args("wall", null, 3, RevitElementQueryCursor.Encode(OtherDoc, filter, 1003)), RevitCommandCodes.CursorForeignDocument);
        Refuses("a cursor reused with another category is refused cursor_filter_mismatch", Args("door", null, 3, good), RevitCommandCodes.CursorFilterMismatch);
        Refuses("a cursor reused with a level added is refused cursor_filter_mismatch", Args("wall", L1, 3, good), RevitCommandCodes.CursorFilterMismatch);
        Refuses("a cursor reused with other parameter names is refused cursor_filter_mismatch", Args("wall", null, 3, good, "Mark"), RevitCommandCodes.CursorFilterMismatch);
    }

    // Refused by the shared client path (CLI verb and MCP tools) before the pipe, and again by the add-in's re-parse.
    static void BeforeThePipe()
    {
        var filter = RevitElementQueryCursor.FilterDigest("OST_Walls", null, Array.Empty<string>());
        var cases = new[]
        {
            (RevitCommandCodes.CursorMalformed, "%%%"),
            (RevitCommandCodes.CursorTampered, B64(Encoding.UTF8.GetBytes(JsonNode.Parse(Unb64(RevitElementQueryCursor.Encode(Doc, filter, 5)))!.AsObject().Also(p => p["after"] = 6).ToJsonString()))),
            (RevitCommandCodes.CursorStale, RevitElementQueryCursor.Encode(Doc, filter, 5, version: 2)),
            (RevitCommandCodes.CursorForeignDocument, RevitElementQueryCursor.Encode(OtherDoc, filter, 5)),
            (RevitCommandCodes.CursorFilterMismatch, RevitElementQueryCursor.Encode(Doc, RevitElementQueryCursor.FilterDigest("OST_Doors", null, Array.Empty<string>()), 5)),
        };
        foreach (var (code, cursor) in cases)
        {
            var sent = 0;
            var line = Envelope(Args("wall", null, 3, cursor)).ToJsonString();
            var result = RevitCommandService.Run(Pipe, line, RevitCommandPolicy.None, (_, _) => { sent++; return null; });
            var failure = RevitCommandResponse.Parse(result.Line);
            Check(result.IsError && !result.Forwarded && sent == 0 && failure.Code == code && failure.Field == "args.cursor", code + " is refused by the client before the pipe", result.Line);
            RevitCommandException? addIn = null;
            try { CommandWire.Bind(RealPath, Doc, null, false, line); } catch (RevitCommandException error) { addIn = error; }
            Check(addIn?.Code == code, code + " is refused again by the add-in's own re-parse", addIn?.Code ?? "accepted");
        }
        var goodSent = 0;
        RevitCommandService.Run(Pipe, Envelope(Args("wall", null, 3, RevitElementQueryCursor.Encode(Doc, filter, 5))).ToJsonString(), RevitCommandPolicy.None, (_, _) => { goodSent++; return null; });
        Check(goodSent == 1, "a valid cursor is forwarded to the pipe");
    }

    static JsonObject Also(this JsonObject node, Action<JsonObject> change) { change(node); return node; }

    static List<JsonNode> Follow(JsonObject args, out int pages)
    {
        var elements = new List<JsonNode>();
        pages = 0;
        string? cursor = null;
        do
        {
            var page = JsonNode.Parse(Collect(Envelope(cursor is null ? args : args.DeepClone().AsObject().Also(a => a["cursor"] = cursor)).ToJsonString()))!;
            elements.AddRange(page["elements"]!.AsArray().Select(e => e!.DeepClone()));
            cursor = page["next_cursor"]?.GetValue<string>();
            Check(page["truncated"]!.GetValue<bool>() == (cursor is not null), "next_cursor is present exactly when the page is truncated");
            pages++;
        } while (cursor is not null && pages < 100);
        return elements;
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    // SF1: a limit cut issues the cursor from the last PAGED id, even when the rows read for the last (or every) paged id
    // vanished before they were read; a byte cut issues it from the last kept row.
    static void VanishedRows()
    {
        var filter = RevitElementQueryCursor.FilterDigest("OST_Walls", "Level 1", Array.Empty<string>());
        var args = new ElementQueryArgs("wall", "OST_Walls", "Level 1", Array.Empty<string>(), 2, new ElementQueryCursor(Doc, filter, 10));
        ObservedQueryElement Row(long id) => new(id, Uid(id), "Walls", "OST_Walls");
        var none = JsonNode.Parse(Invariant(RevitElementQueryJson.Elements(Doc, args, null, 5, true, Array.Empty<ObservedQueryElement>(), new long[] { 20, 30 })))!;
        Check(none["truncated"]!.GetValue<bool>() && none["returned"]!.GetValue<int>() == 0 && none["next_cursor"] is not null && RevitElementQueryCursor.Decode(none["next_cursor"]!.GetValue<string>(), "c").After == 30, "a limit-cut page whose paged ids all vanished still resumes after the last paged id");
        var lastGone = JsonNode.Parse(Invariant(RevitElementQueryJson.Elements(Doc, args, null, 5, true, new[] { Row(20) }, new long[] { 20, 30 })))!;
        Check(RevitElementQueryCursor.Decode(lastGone["next_cursor"]!.GetValue<string>(), "c").After == 30 && lastGone["returned"]!.GetValue<int>() == 1, "when the last paged id vanished the cursor does not fall back to an earlier id");
        var whole = JsonNode.Parse(Invariant(RevitElementQueryJson.Elements(Doc, args, null, 5, false, new[] { Row(20) }, new long[] { 20, 30 })))!;
        Check(!whole["truncated"]!.GetValue<bool>() && whole["next_cursor"] is null, "an uncut page whose last id vanished has no cursor");
        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Doc, args, null, 5, true, new[] { Row(40) }, new long[] { 20, 30 })), "a row that was not paged is a collector bug and throws");
        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Doc, args, null, 5, true, Array.Empty<ObservedQueryElement>(), new long[] { 5, 20 })), "a paged id at or before the cursor id throws");
        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Doc, args, null, 5, true, Array.Empty<ObservedQueryElement>(), Array.Empty<long>())), "a limit cut with no paged id throws (paging never produces it)");
        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Doc, args, null, 5, true, Array.Empty<ObservedQueryElement>(), new long[] { 20, 30, 40 })), "more paged ids than the limit throws");
        // Through the fake collector: the vanished ids are skipped, every surviving id is still reached once.
        var gone = new HashSet<long>(Model.Where(f => f.BuiltIn == "OST_Walls").Select(f => f.Id).OrderBy(id => id).Where((_, i) => i % 3 == 2));
        var seen = new List<long>();
        string? cursor = null;
        for (var guard = 0; guard < 100; guard++)
        {
            var page = JsonNode.Parse(Collect(Envelope(Args("wall", null, 3, cursor)).ToJsonString(), vanished: gone.Contains))!;
            seen.AddRange(page["elements"]!.AsArray().Select(e => e!["id"]!.GetValue<long>()));
            cursor = page["next_cursor"]?.GetValue<string>();
            if (cursor is null) break;
        }
        var expected = Model.Where(f => f.BuiltIn == "OST_Walls" && !gone.Contains(f.Id)).Select(f => f.Id).OrderBy(id => id);
        Check(gone.Count >= 3 && seen.SequenceEqual(expected), "paging over vanishing rows reaches every surviving id once, ascending", string.Join(",", seen));
    }

    // SF2: a cursor is not a snapshot. Between pages, elements may be added or deleted; the next page reads the document
    // as it is then, strictly after the cursor id: no duplicates, ids ascending, deleted ids absent, ids added at or
    // before the cursor id not seen, ids added after it seen.
    static void ModelChangesBetweenPages()
    {
        var before = Model.Where(f => f.BuiltIn == "OST_Walls").OrderBy(f => f.Id).ToArray();
        var first = JsonNode.Parse(Collect(Envelope(Args("wall", null, 4)).ToJsonString()))!;
        var firstIds = first["elements"]!.AsArray().Select(e => e!["id"]!.GetValue<long>()).ToArray();
        var after = firstIds[^1];
        var notYet = before.Where(f => f.Id > after).Select(f => f.Id).ToArray();
        var deleted = new HashSet<long> { firstIds[0], notYet[0], notYet[^1] };
        var added = new[] { new Fixture(firstIds[0] + 1, "OST_Walls", L1), new Fixture(after + 1, "OST_Walls", L2), new Fixture(notYet[^1] + 1000, "OST_Walls", L1) };
        var changed = before.Where(f => !deleted.Contains(f.Id)).Concat(added).Concat(Model.Where(f => f.BuiltIn != "OST_Walls")).Reverse().ToArray();
        var ids = firstIds.ToList();
        var cursor = first["next_cursor"]!.GetValue<string>();
        for (var guard = 0; guard < 100 && cursor is not null; guard++)
        {
            var page = JsonNode.Parse(Collect(Envelope(Args("wall", null, 4, cursor)).ToJsonString(), model: changed))!;
            ids.AddRange(page["elements"]!.AsArray().Select(e => e!["id"]!.GetValue<long>()));
            cursor = page["next_cursor"]?.GetValue<string>();
        }
        var resumed = changed.Where(f => f.BuiltIn == "OST_Walls" && f.Id > after).Select(f => f.Id).OrderBy(id => id);
        Check(ids.Distinct().Count() == ids.Count && ids.Zip(ids.Skip(1)).All(pair => pair.First < pair.Second), "pages across model changes have no duplicate and stay ascending", string.Join(",", ids));
        Check(ids.Skip(firstIds.Length).SequenceEqual(resumed), "after the change, the pages are exactly the surviving and added ids after the cursor id");
        Check(!ids.Contains(notYet[0]) && !ids.Contains(notYet[^1]) && !ids.Contains(firstIds[0] + 1) && ids.Contains(after + 1) && ids.Contains(notYet[^1] + 1000), "deleted ids are absent, an id added before the cursor is not seen, ids added after it are");
    }

    // Cross-language pin: the fake add-in's New-FakeCursor (tests/integration/fake-revit-command-executor.ps1) must issue
    // this same text; tests/integration/test-catalogue-session.ps1 asserts it.
    public const string VectorCursor = "eyJ2IjoxLCJkb2N1bWVudCI6IjYwZjkxZGFmLTNkZDctNDI4My1hODZkLTI0MTM3YjczZjNkYS0wMDAxZmQwYiIsImZpbHRlciI6IjBjYjIxNzZkNmNlODg4ZjA1OGMxNmY0MzNkNTNkNjIyMmM0M2JiNjU4OTA4NjljYTZlNWZiNzFiYTAwOWE0MzYiLCJhZnRlciI6NDA5NywiY2hlY2siOiI5MTcwMjNkMDlkYWZlYjEzMzBkMTRjMjM5OGMzYzg2OSJ9";
    static void Vector()
    {
        var filter = RevitElementQueryCursor.FilterDigest("OST_Walls", null, Array.Empty<string>());
        Check(filter == "0cb2176d6ce888f058c16f433d53d6222c43bb65890869ca6e5fb71ba009a436" && RevitElementQueryCursor.Encode(Doc, filter, 4097) == VectorCursor, "the cursor of a known document, filter and id is the pinned text (shared with the fake add-in)");
        var root = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        var fake = root is null ? null : Path.Combine(root, "tests", "integration", "test-catalogue-session.ps1");
        Check(fake is not null && File.Exists(fake) && File.ReadAllText(fake).Contains(VectorCursor, StringComparison.Ordinal), "the catalogue session test pins the fake add-in to the same cursor text");
    }

    static void Concatenation()
    {
        foreach (var (category, level, limit) in new (string?, string?, int)[] { ("wall", null, 4), ("door", null, 1), (null, L1, 3), ("OST_Walls", L2, 2), ("wall", null, 5000) })
        {
            var args = Args(category, level, limit);
            var paged = Follow(args, out var pages);
            var whole = JsonNode.Parse(Collect(Envelope(Args(category, level, 5000)).ToJsonString()))!;
            var expected = whole["elements"]!.AsArray().Select(e => e!.ToJsonString()).ToArray();
            var label = (category ?? "any") + "/" + (level is null ? "any" : "level") + " limit " + limit;
            Check(!whole["truncated"]!.GetValue<bool>() && expected.Length > 0, "the unlimited reference query for " + label + " is complete and non-empty");
            Check(paged.Select(e => e.ToJsonString()).SequenceEqual(expected), "concatenated pages equal one unlimited query (" + label + ")", paged.Count + " vs " + expected.Length);
            Check(pages == Math.Max(1, (expected.Length + limit - 1) / limit), "the page count is the ceiling of matched over limit (" + label + ")", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    // Pages cut by the 4 MiB byte budget also carry a next_cursor and resume without loss or repetition.
    static void ByteBudgetPages()
    {
        var heavy = new string('\u00e9', 1024);
        ObservedQueryElement Heavy(long id) => new(id, Uid(id), "Walls", "OST_Walls")
        {
            Parameters = Enumerable.Range(0, 16).Select(i => new ObservedParameter("P" + i, -i - 1, null, RevitParameterSources.Instance, RevitParameterStorage.String, null, true, false, false) { Text = heavy, Display = heavy }).ToArray(),
        };
        // 60 walls of about 196 KB each: about 21 rows fit one 4 MiB reply.
        var walls = Enumerable.Range(1, 60).Select(i => new Fixture(5000 + i * 11L, "OST_Walls", L1)).Reverse().ToArray();
        var ids = walls.Select(f => f.Id).OrderBy(id => id).ToArray();
        var seen = new List<long>();
        string? cursor = null;
        var cutByBytes = false;
        for (var guard = 0; guard < 50; guard++)
        {
            var reply = Collect(Envelope(Args("wall", null, 5000, cursor)).ToJsonString(), Heavy, walls);
            var page = JsonNode.Parse(reply)!;
            Check(Encoding.UTF8.GetByteCount(reply) <= RevitElementQueryRules.MaxReplyBytes, "a byte-bounded page stays within 4 MiB");
            cutByBytes |= page["truncated_reason"]?.GetValue<string>() == "bytes";
            seen.AddRange(page["elements"]!.AsArray().Select(e => e!["id"]!.GetValue<long>()));
            cursor = page["next_cursor"]?.GetValue<string>();
            if (cursor is null) break;
        }
        Check(cutByBytes, "the heavy fixture is cut by the byte budget");
        Check(seen.SequenceEqual(ids), "pages cut by bytes resume after the last returned id: every id once, ascending", string.Join(",", seen));
    }
}
