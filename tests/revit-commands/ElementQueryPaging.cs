using Bim.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
// P1-15 query.elements client paging oracle (core/Bim.Core/RevitCommandsQueryPaging.cs): RevitElementQueryPaging.All
// follows next_cursor until null through an IRevitCommandTransport, sends only query.elements with the same filter and
// limit plus the cursor, validates every reply and concatenates the elements. Oracle: on a fake add-in that pages a fixed
// element list with RevitElementQueryRules.Page and real cursors (RevitElementQueryJson.Elements), including rows that
// vanish, the concatenation equals one unlimited (single page) query. Unit-tested only; no Revit.
static class ElementQueryPagingChecks
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
        CursorLoop();
        MalformedReplies();
    }

    sealed record Fixture(long Id, string BuiltIn, string Level);

    // 40 sparse, unordered ids over walls and doors on two levels.
    static readonly Fixture[] Model = Enumerable.Range(0, 40)
        .Select(i => new Fixture(2000 + ((i * 7919) % 211) * 5, i % 3 == 0 ? "OST_Doors" : "OST_Walls", i % 2 == 0 ? L1 : L2))
        .GroupBy(f => f.Id).Select(g => g.First()).Reverse().ToArray();

    static string Uid(long id) => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + id.ToString("x8", CultureInfo.InvariantCulture);

    static JsonObject Envelope(JsonObject args, string command = "query.elements", string operation = "op-all") =>
        new() { ["protocol_version"] = 1, ["operation_id"] = operation, ["document"] = new JsonObject { ["path"] = RealPath, ["unique_id"] = Doc }, ["command"] = command, ["args"] = args };

    static JsonObject Args(string? category, string? level, int? limit, params string[] parameters)
    {
        // Built from parsed text, not a JsonObject creation followed by conditional index assignments: that shape crashes
        // the IDE0028 analyzer (AD0001) on the CI SDK.
        var args = JsonNode.Parse("{}")!.AsObject();
        if (category is not null) args.Add("category", category);
        if (level is not null) args.Add("level", level);
        if (parameters.Length > 0) args.Add("parameters", new JsonArray(parameters.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()));
        if (limit is { } size) args.Add("limit", size);
        return args;
    }

    // The add-in's decisions without Revit: filter, page after the cursor id with the real paging rule, read the rows
    // still readable, write the reply in core, wrap it in a success answering this very request.
    sealed class FakeAddIn : IRevitCommandTransport
    {
        public List<Fixture> Items;
        public Func<long, bool>? Vanished;
        // Called before each reply with the 0-based page index (to edit the model between pages).
        public Action<int>? BeforePage;
        // Replaces the whole reply line of the 0-based page index.
        public Func<int, RevitCommandEnvelope, string, string?>? Line;
        public readonly List<string> Sent = [];

        public FakeAddIn(IEnumerable<Fixture>? items = null) => Items = (items ?? Model).ToList();

        public string? Send(string envelopeJson)
        {
            var page = Sent.Count;
            Sent.Add(envelopeJson);
            BeforePage?.Invoke(page);
            var envelope = RevitCommandProtocol.Parse(envelopeJson);
            var readback = Collect(envelope, Items, Vanished);
            var line = Reply(envelope, readback);
            return Line is null ? line : Line(page, envelope, line);
        }
    }

    static string Reply(RevitCommandEnvelope envelope, string? readback) =>
        RevitCommandResponse.Success(new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), readback, Array.Empty<string>()).ToJson();

    static string Collect(RevitCommandEnvelope envelope, IReadOnlyList<Fixture> model, Func<long, bool>? vanished)
    {
        var args = (ElementQueryArgs)envelope.Args;
        var matched = model.Where(f => (args.BuiltInCategory is null || f.BuiltIn == args.BuiltInCategory) && (args.Level is null || f.Level == args.Level)).Select(f => f.Id).ToList();
        var (selected, truncated) = RevitElementQueryRules.Page(matched, args.Limit, args.Cursor?.After);
        var rows = selected.Where(id => vanished?.Invoke(id) != true).Select(id =>
        {
            var f = model.First(m => m.Id == id);
            return new ObservedQueryElement(id, Uid(id), f.BuiltIn == "OST_Walls" ? "Walls" : "Doors", f.BuiltIn)
            {
                Level = new ObservedReference(f.Level == L1 ? "Level 1" : "Level 2", f.Level),
                FamilyName = "Basic",
                TypeName = "T" + (id % 7).ToString(CultureInfo.InvariantCulture),
                Parameters = new[] { new ObservedParameter("Mark", -1001203, "ALL_MODEL_MARK", RevitParameterSources.Instance, RevitParameterStorage.String, null, true, false, false) { Text = "M" + id.ToString(CultureInfo.InvariantCulture) } },
            };
        }).ToArray();
        return RevitElementQueryJson.Elements(envelope.Document.UniqueId, args, null, matched.Distinct().Count(), truncated, rows, selected);
    }

    // One unlimited query: the same filter with limit 5000 and no cursor on the given (final) model.
    static (string[] Elements, int Matched) Unlimited(JsonObject args, IReadOnlyList<Fixture> model, Func<long, bool>? vanished = null)
    {
        var whole = args.DeepClone().AsObject();
        whole["limit"] = RevitElementQueryRules.MaxLimit;
        var reply = JsonNode.Parse(Collect(RevitCommandProtocol.Parse(Envelope(whole).ToJsonString()), model, vanished))!;
        Check(!reply["truncated"]!.GetValue<bool>(), "the unlimited reference query is one untruncated page");
        return (reply["elements"]!.AsArray().Select(e => e!.ToJsonString()).ToArray(), reply["matched"]!.GetValue<int>());
    }

    static string[] Canonical(IEnumerable<string> elements) => elements.Select(e => JsonNode.Parse(e)!.ToJsonString()).ToArray();

    static string Describe(ElementQueryAllResult result) => result.Status + "/" + result.Code + " at " + result.Field + " (" + result.Detail + ") pages " + result.Pages.ToString(CultureInfo.InvariantCulture) + " elements " + result.Elements.Count.ToString(CultureInfo.InvariantCulture);


    // Rows paged but no longer readable (the add-in skips them; a limit-cut page may even return nothing).

    // Rows deleted between pages (after an earlier page was read): the result equals the unlimited query on the final
    // model as long as no returned row was deleted later.




    static string FilterOf(RevitCommandEnvelope envelope) => RevitElementQueryCursor.FilterDigest((ElementQueryArgs)envelope.Args);

    static void CursorLoop()
    {
        // The second page issues a cursor at its own after: the same request would be sent forever.
        var same = new FakeAddIn();
        same.Line = (page, envelope, line) => page == 1 ? Reply(envelope, ReadbackOf(line).Also(r => r["next_cursor"] = RevitElementQueryCursor.Encode(Doc, FilterOf(envelope), ((ElementQueryArgs)envelope.Args).Cursor!.After)).Also(r => r["elements"] = new JsonArray()).Also(r => r["returned"] = 0).ToJsonString()) : line;
        var repeated = RevitElementQueryPaging.All(Envelope(Args("wall", null, 2)).ToJsonString(), same);
        Check(!repeated.IsSuccess && repeated.Code == RevitElementQueryPagingCodes.CursorLoop && same.Sent.Count == 2 && repeated.Elements.Count == 0, "a next_cursor whose id does not move forward is refused cursor_loop", Describe(repeated));
        var back = new FakeAddIn();
        back.Line = (page, envelope, line) => page == 2 ? Reply(envelope, ReadbackOf(line).Also(r => r["next_cursor"] = RevitElementQueryCursor.Encode(Doc, FilterOf(envelope), 0)).Also(r => r["elements"] = new JsonArray()).Also(r => r["returned"] = 0).ToJsonString()) : line;
        var backwards = RevitElementQueryPaging.All(Envelope(Args("wall", null, 2)).ToJsonString(), back);
        Check(!backwards.IsSuccess && backwards.Code == RevitElementQueryPagingCodes.CursorLoop && back.Sent.Count == 3, "a next_cursor whose id goes back is refused cursor_loop", Describe(backwards));
    }

    static JsonObject ReadbackOf(string line) => JsonNode.Parse(line)!["readback"]!.AsObject();

    static void MalformedReplies()
    {
        void Refused(string label, Func<int, RevitCommandEnvelope, string, string?> line, string code, int pages = 2)
        {
            var fake = new FakeAddIn { Line = line };
            var result = RevitElementQueryPaging.All(Envelope(Args("wall", null, 3)).ToJsonString(), fake);
            Check(!result.IsSuccess && result.Code == code && result.Elements.Count == 0 && fake.Sent.Count == pages, label + " is refused " + code, Describe(result));
        }
        string OnSecond(int page, RevitCommandEnvelope envelope, string line, Action<JsonObject> change) => page == 1 ? Reply(envelope, ReadbackOf(line).Also(change).ToJsonString()) : line;
        var invalid = RevitCommandCodes.ResponseInvalid;
        Refused("a reply that is not JSON", (page, _, line) => page == 1 ? "{" : line, invalid);
        Refused("a reply missing members", (page, _, line) => page == 1 ? "{\"status\":\"succeeded\"}" : line, invalid);
        Refused("a failed reply with code ok", (page, _, line) => page == 1 ? RevitCommandResponse.Failure(RevitCommandCodes.Ok).ToJson() : line, invalid);
        Refused("a receipt for another operation", (page, envelope, line) => page == 1 ? RevitCommandResponse.Success(new RevitCommandReceipt("other-op", envelope.Command, Doc, envelope.RequestSha256, false), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), ReadbackOf(line).ToJsonString(), Array.Empty<string>()).ToJson() : line, invalid);
        Refused("a receipt for another request (the previous page's)", (page, envelope, line) => page == 1 ? RevitCommandResponse.Success(new RevitCommandReceipt(envelope.OperationId, envelope.Command, Doc, new string('0', 64), false), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), ReadbackOf(line).ToJsonString(), Array.Empty<string>()).ToJson() : line, invalid);
        Refused("a reply reporting changes", (page, envelope, line) => page == 1 ? RevitCommandResponse.Success(new RevitCommandReceipt(envelope.OperationId, envelope.Command, Doc, envelope.RequestSha256, false), Array.Empty<string>(), new[] { Uid(2005) }, Array.Empty<string>(), ReadbackOf(line).ToJsonString(), Array.Empty<string>()).ToJson() : line, invalid);
        Refused("a reply without readback", (page, envelope, line) => page == 1 ? Reply(envelope, null) : line, invalid);
        Refused("a readback that is not an object", (page, envelope, line) => page == 1 ? Reply(envelope, "[]") : line, invalid);
        Refused("truncated without next_cursor", (page, envelope, line) => OnSecond(page, envelope, line, r => r["next_cursor"] = null), invalid);
        Refused("a missing next_cursor member", (page, envelope, line) => OnSecond(page, envelope, line, r => r.Remove("next_cursor")), invalid);
        Refused("next_cursor on an untruncated page", (page, envelope, line) => OnSecond(page, envelope, line, r => { r["truncated"] = false; r["truncated_reason"] = null; }), invalid);
        Refused("an unknown truncated_reason", (page, envelope, line) => OnSecond(page, envelope, line, r => r["truncated_reason"] = "time"), invalid);
        Refused("a next_cursor that does not decode", (page, envelope, line) => OnSecond(page, envelope, line, r => r["next_cursor"] = "%%%"), invalid);
        Refused("a next_cursor for another filter", (page, envelope, line) => OnSecond(page, envelope, line, r => r["next_cursor"] = RevitElementQueryCursor.Encode(Doc, RevitElementQueryCursor.FilterDigest("OST_Doors", null, Array.Empty<string>()), 999999)), invalid);
        Refused("a next_cursor for another document", (page, envelope, line) => OnSecond(page, envelope, line, r => r["next_cursor"] = RevitElementQueryCursor.Encode(OtherDoc, FilterOf(envelope), 999999)), invalid);
        Refused("a next_cursor before the page's last element", (page, envelope, line) => OnSecond(page, envelope, line, r => r["next_cursor"] = RevitElementQueryCursor.Encode(Doc, FilterOf(envelope), r["elements"]!.AsArray()[^1]!["id"]!.GetValue<long>() - 1)), invalid);
        Refused("an after that is not the cursor's id", (page, envelope, line) => OnSecond(page, envelope, line, r => r["after"] = r["after"]!.GetValue<long>() + 1), invalid);
        Refused("an after on the first page", (page, envelope, line) => page == 0 ? Reply(envelope, ReadbackOf(line).Also(r => r["after"] = 1).ToJsonString()) : line, invalid, 1);
        Refused("another limit", (page, envelope, line) => OnSecond(page, envelope, line, r => r["limit"] = 4), invalid);
        Refused("a returned count that is not the element count", (page, envelope, line) => OnSecond(page, envelope, line, r => r["returned"] = 1), invalid);
        Refused("more elements than the limit", (page, envelope, line) => OnSecond(page, envelope, line, r => { r["elements"]!.AsArray().Add(r["elements"]!.AsArray()[0]!.DeepClone().AsObject().Also(e => e["id"] = 99999999)); r["returned"] = 4; }), invalid);
        Refused("a non-integer matched", (page, envelope, line) => OnSecond(page, envelope, line, r => r["matched"] = 1.5), invalid);
        Refused("matched below returned", (page, envelope, line) => OnSecond(page, envelope, line, r => r["matched"] = 1), invalid);
        Refused("elements out of order", (page, envelope, line) => OnSecond(page, envelope, line, r => { var e = r["elements"]!.AsArray(); var copy = e.Select(x => x!.DeepClone()).Reverse().ToArray(); e.Clear(); foreach (var x in copy) e.Add(x); }), invalid);
        Refused("an element at or before the cursor id", (page, envelope, line) => OnSecond(page, envelope, line, r => r["elements"]!.AsArray()[0]!["id"] = r["after"]!.GetValue<long>()), invalid);
        Refused("an element without an integer id", (page, envelope, line) => OnSecond(page, envelope, line, r => r["elements"]!.AsArray()[0]!["id"] = "7"), invalid);
        Refused("an element that is not an object", (page, envelope, line) => OnSecond(page, envelope, line, r => { r["elements"]!.AsArray()[0] = 7; }), invalid);
        Refused("a failure from the add-in", (page, _, line) => page == 1 ? RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.level", "gone").ToJson() : line, RevitCommandCodes.ReferenceNotFound);
        var failing = new FakeAddIn { Line = (page, _, line) => page == 1 ? RevitCommandResponse.Failure(RevitCommandCodes.DocumentConflict, "document.unique_id", "another document").ToJson() : line };
        var failed = RevitElementQueryPaging.All(Envelope(Args("wall", null, 3)).ToJsonString(), failing);
        Check(failed.Field == "document.unique_id" && failed.Detail == "another document" && failed.Pages == 1, "an add-in failure keeps its field and detail and counts only the accepted pages", Describe(failed));
    }


    // The CLI adapter: each page goes through RevitCommandService.Run (pipe rule, envelope validation, guards, receipt).


    static JsonObject Also(this JsonObject node, Action<JsonObject> change) { change(node); return node; }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
