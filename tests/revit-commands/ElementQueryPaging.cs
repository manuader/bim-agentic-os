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
        Concatenation();
        VanishedRows();
        DeletedBetweenPages();
        SentEnvelopes();
        RefusedBeforeSending();
        PageCap();
        CursorLoop();
        MalformedReplies();
        TransportFailures();
        ServiceTransport();
        Json();
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
        var args = new JsonObject();
        if (category is not null) args["category"] = category;
        if (level is not null) args["level"] = level;
        if (parameters.Length > 0) args["parameters"] = new JsonArray(parameters.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
        if (limit is not null) args["limit"] = limit;
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

    static void Concatenation()
    {
        foreach (var (category, level, limit, parameters) in new (string?, string?, int?, string[])[]
        {
            ("wall", null, 4, Array.Empty<string>()), ("door", null, 1, Array.Empty<string>()), (null, L1, 3, Array.Empty<string>()),
            ("OST_Walls", L2, 2, new[] { "Mark" }), ("wall", null, 5000, Array.Empty<string>()), ("wall", null, null, Array.Empty<string>()),
        })
        {
            var args = Args(category, level, limit, parameters);
            var label = (category ?? "any") + "/" + (level is null ? "any" : "level") + " limit " + (limit?.ToString(CultureInfo.InvariantCulture) ?? "default");
            var fake = new FakeAddIn();
            var result = RevitElementQueryPaging.All(Envelope(args).ToJsonString(), fake);
            var (expected, matched) = Unlimited(args, fake.Items);
            var size = limit ?? RevitElementQueryRules.DefaultLimit;
            Check(result.IsSuccess && result.Code == RevitCommandCodes.Ok && result.Field is null, "client paging succeeds (" + label + ")", Describe(result));
            Check(expected.Length > 0 && Canonical(result.Elements).SequenceEqual(expected), "concatenated pages equal one unlimited query (" + label + ")", Describe(result));
            Check(result.Pages == Math.Max(1, (matched + size - 1) / size) && result.Pages == fake.Sent.Count, "pages is the ceiling of matched over limit and every page was sent once (" + label + ")", Describe(result));
            Check(result.Matched == matched, "the last page's matched is reported (" + label + ")", Describe(result));
        }
    }

    // Rows paged but no longer readable (the add-in skips them; a limit-cut page may even return nothing).
    static void VanishedRows()
    {
        var walls = Model.Where(f => f.BuiltIn == "OST_Walls").Select(f => f.Id).OrderBy(id => id).ToArray();
        // Limit 2: ids 3-4 of every 4 vanish, so every second page comes back empty but truncated.
        var gone = walls.Where((_, i) => i % 4 >= 2).ToHashSet();
        var args = Args("wall", null, 2);
        var fake = new FakeAddIn { Vanished = gone.Contains };
        var result = RevitElementQueryPaging.All(Envelope(args).ToJsonString(), fake);
        var (expected, matched) = Unlimited(args, fake.Items, gone.Contains);
        Check(gone.Count >= 4 && result.IsSuccess && Canonical(result.Elements).SequenceEqual(expected), "pages over vanished rows (including empty truncated pages) equal one unlimited query", Describe(result));
        Check(result.Pages == (matched + 1) / 2 && result.Matched == matched, "an empty truncated page is followed, not taken as the end", Describe(result));
        // The very last paged ids vanish: the last page is empty and untruncated.
        var tail = walls.TakeLast(3).ToHashSet();
        var last = new FakeAddIn { Vanished = tail.Contains };
        var lastResult = RevitElementQueryPaging.All(Envelope(Args("wall", null, 3)).ToJsonString(), last);
        Check(lastResult.IsSuccess && Canonical(lastResult.Elements).SequenceEqual(Unlimited(Args("wall", null, 3), last.Items, tail.Contains).Elements), "a last page whose rows all vanished ends the listing", Describe(lastResult));
        var none = new FakeAddIn { Vanished = _ => true };
        var noneResult = RevitElementQueryPaging.All(Envelope(Args("wall", null, 5)).ToJsonString(), none);
        Check(noneResult.IsSuccess && noneResult.Elements.Count == 0 && noneResult.Pages == (walls.Length + 4) / 5, "a listing whose rows all vanish succeeds empty after every page", Describe(noneResult));
    }

    // Rows deleted between pages (after an earlier page was read): the result equals the unlimited query on the final
    // model as long as no returned row was deleted later.
    static void DeletedBetweenPages()
    {
        var walls = Model.Where(f => f.BuiltIn == "OST_Walls").Select(f => f.Id).OrderBy(id => id).ToArray();
        var fake = new FakeAddIn();
        List<long> deleted = [];
        fake.BeforePage = page =>
        {
            if (page == 0) return;
            // Before each later page delete the next not yet listed wall plus the highest remaining one.
            var after = RevitCommandProtocol.Parse(fake.Sent[^1]).Args is ElementQueryArgs { Cursor: { } cursor } ? cursor.After : -1;
            foreach (var id in fake.Items.Where(f => f.BuiltIn == "OST_Walls" && f.Id > after).Select(f => f.Id).OrderBy(id => id).Take(1)
                .Concat(fake.Items.Where(f => f.BuiltIn == "OST_Walls" && f.Id > after).Select(f => f.Id).OrderByDescending(id => id).Take(1)).Distinct().ToArray())
            {
                deleted.Add(id);
                fake.Items.RemoveAll(f => f.Id == id);
            }
        };
        var args = Args("wall", null, 3);
        var result = RevitElementQueryPaging.All(Envelope(args).ToJsonString(), fake);
        var (expected, matched) = Unlimited(args, fake.Items);
        Check(deleted.Count >= 4 && result.IsSuccess && Canonical(result.Elements).SequenceEqual(expected), "rows deleted between pages: the concatenation equals one unlimited query on the final model", Describe(result) + " deleted " + string.Join(",", deleted));
        Check(result.Matched == matched && matched == walls.Length - deleted.Count, "matched is the last page's count (after the deletions)", Describe(result));
        var ids = result.Elements.Select(e => JsonNode.Parse(e)!["id"]!.GetValue<long>()).ToArray();
        Check(ids.Zip(ids.Skip(1)).All(pair => pair.First < pair.Second) && !ids.Any(deleted.Contains), "ids stay strictly ascending and deleted ids are absent", string.Join(",", ids));
    }

    static void SentEnvelopes()
    {
        var fake = new FakeAddIn();
        var args = Args("OST_Walls", L1, 2, "Mark", "Comments");
        var result = RevitElementQueryPaging.All(Envelope(args).ToJsonString(), fake);
        Check(result.IsSuccess && fake.Sent.Count >= 3, "a multi-page listing", Describe(result));
        var envelopes = fake.Sent.Select(RevitCommandProtocol.Parse).ToArray();
        Check(envelopes.All(e => e.Command == "query.elements" && !e.Spec.Writes), "only query.elements (read-only) is sent");
        Check(fake.Sent.SequenceEqual(envelopes.Select(e => e.CanonicalJson)), "every request is a canonical envelope");
        Check(envelopes.All(e => e.Document == new RevitDocumentRef(RealPath, Doc) && e.OperationId == "op-all"), "every page goes to the same document with the caller's operation id");
        var first = (ElementQueryArgs)envelopes[0].Args;
        Check(first.Cursor is null && first.Limit == 2 && first.BuiltInCategory == "OST_Walls" && first.Level == L1 && first.Parameters.SequenceEqual(new[] { "Mark", "Comments" }), "the first page is the caller's query without a cursor");
        Check(envelopes.Skip(1).All(e => e.Args is ElementQueryArgs a && a.Category == first.Category && a.BuiltInCategory == first.BuiltInCategory && a.Level == first.Level && a.Limit == first.Limit && a.Parameters.SequenceEqual(first.Parameters) && a.Cursor is not null),
            "every later page has the same filter and limit plus a cursor");
        var replies = fake.Sent.Select(sent => JsonNode.Parse(Collect(RevitCommandProtocol.Parse(sent), fake.Items, null))!).ToArray();
        var cursors = envelopes.Skip(1).Select(e => JsonNode.Parse(e.CanonicalJson)!["args"]!["cursor"]!.GetValue<string>()).ToArray();
        Check(cursors.SequenceEqual(replies.Take(replies.Length - 1).Select(r => r["next_cursor"]!.GetValue<string>())), "each cursor sent is the previous reply's next_cursor, verbatim");
        // A caller cursor resumes from it.
        var filter = RevitElementQueryCursor.FilterDigest("OST_Walls", null, Array.Empty<string>());
        var walls = Model.Where(f => f.BuiltIn == "OST_Walls").Select(f => f.Id).OrderBy(id => id).ToArray();
        var resumeArgs = Args("wall", null, 4);
        resumeArgs["cursor"] = RevitElementQueryCursor.Encode(Doc, filter, walls[5]);
        var resumed = RevitElementQueryPaging.All(Envelope(resumeArgs).ToJsonString(), new FakeAddIn());
        var resumedIds = resumed.Elements.Select(e => JsonNode.Parse(e)!["id"]!.GetValue<long>()).ToArray();
        Check(resumed.IsSuccess && resumedIds.SequenceEqual(walls.Skip(6)), "an initial cursor resumes the listing after its id", Describe(resumed));
    }

    static void RefusedBeforeSending()
    {
        foreach (var (label, json, code) in new[]
        {
            ("query.levels", Envelope(new JsonObject(), "query.levels").ToJsonString(), RevitElementQueryPagingCodes.CommandNotPageable),
            ("query.inspect", Envelope(new JsonObject { ["unique_id"] = Uid(2005) }, "query.inspect").ToJsonString(), RevitElementQueryPagingCodes.CommandNotPageable),
            ("element.delete (a write)", Envelope(new JsonObject { ["element_uids"] = new JsonArray(Uid(2005)) }, "element.delete").ToJsonString(), RevitElementQueryPagingCodes.CommandNotPageable),
            ("not JSON", "{not json", RevitCommandCodes.EnvelopeInvalid),
            ("an unknown command", Envelope(new JsonObject(), "query.nothing").ToJsonString(), RevitElementQueryPagingCodes.CommandNotPageable),
            ("a document that is not a UniqueId", Envelope(Args("wall", null, 3)).Also(e => e["document"]!["unique_id"] = "Tower").ToJsonString(), RevitCommandCodes.DocumentIdentityInvalid),
            ("no filter", Envelope(Args(null, null, 3)).ToJsonString(), RevitCommandCodes.ArgsInvalid),
            ("a cursor for another filter", Envelope(Args("door", null, 3).Also(a => a["cursor"] = RevitElementQueryCursor.Encode(Doc, RevitElementQueryCursor.FilterDigest("OST_Walls", null, Array.Empty<string>()), 5))).ToJsonString(), RevitCommandCodes.CursorFilterMismatch),
        })
        {
            var fake = new FakeAddIn();
            var result = RevitElementQueryPaging.All(json, fake);
            Check(!result.IsSuccess && result.Code == code && fake.Sent.Count == 0 && result.Pages == 0 && result.Elements.Count == 0, label + " is refused " + code + " before anything is sent", Describe(result));
        }
        Check(Throws<ArgumentOutOfRangeException>(() => RevitElementQueryPaging.All(Envelope(Args("wall", null, 3)).ToJsonString(), new FakeAddIn(), 0))
            && Throws<ArgumentOutOfRangeException>(() => RevitElementQueryPaging.All(Envelope(Args("wall", null, 3)).ToJsonString(), new FakeAddIn(), RevitElementQueryPaging.MaxPages + 1)),
            "the page cap is 1 to 1000");
        Check(RevitElementQueryPaging.MaxPages == 1000, "the default page cap is 1000");
    }

    static void PageCap()
    {
        var walls = Enumerable.Range(1, 30).Select(i => new Fixture(100 + i, "OST_Walls", L1)).ToArray();
        var small = new FakeAddIn(walls);
        var capped = RevitElementQueryPaging.All(Envelope(Args("wall", null, 1)).ToJsonString(), small, 10);
        Check(!capped.IsSuccess && capped.Code == RevitElementQueryPagingCodes.PageLimitExceeded && small.Sent.Count == 10 && capped.Pages == 10 && capped.Elements.Count == 0,
            "a listing needing more pages than the cap is refused page_limit_exceeded after the cap, without partial elements", Describe(capped));
        var exact = new FakeAddIn(walls);
        var fits = RevitElementQueryPaging.All(Envelope(Args("wall", null, 1)).ToJsonString(), exact, 30);
        Check(fits.IsSuccess && fits.Pages == 30 && fits.Elements.Count == 30, "a listing of exactly the cap succeeds", Describe(fits));
        var thousand = Enumerable.Range(1, 1000).Select(i => new Fixture(i, "OST_Walls", L1)).ToArray();
        var big = new FakeAddIn(thousand);
        var atDefault = RevitElementQueryPaging.All(Envelope(Args("wall", null, 1)).ToJsonString(), big);
        Check(atDefault.IsSuccess && atDefault.Pages == 1000 && atDefault.Elements.Count == 1000, "1000 pages fit the default cap", Describe(atDefault));
        var over = new FakeAddIn(thousand.Append(new Fixture(5000, "OST_Walls", L1)));
        var overDefault = RevitElementQueryPaging.All(Envelope(Args("wall", null, 1)).ToJsonString(), over);
        Check(!overDefault.IsSuccess && overDefault.Code == RevitElementQueryPagingCodes.PageLimitExceeded && over.Sent.Count == 1000, "a 1001st page is refused page_limit_exceeded with the default cap", Describe(overDefault));
    }

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

    static void TransportFailures()
    {
        void Fails(string label, Func<int, RevitCommandEnvelope, string, string?> line, string code)
        {
            var fake = new FakeAddIn { Line = line };
            var result = RevitElementQueryPaging.All(Envelope(Args("wall", null, 3)).ToJsonString(), fake);
            Check(!result.IsSuccess && result.Code == code && result.Elements.Count == 0 && result.Pages == 1, label + " is " + code, Describe(result));
        }
        Fails("no reply", (page, _, line) => page == 1 ? null : line, RevitCommandCodes.NoResponse);
        Fails("a transport timeout", (page, _, line) => page == 1 ? throw new TimeoutException() : line, RevitCommandCodes.PipeConnectTimeout);
        Fails("a transport I/O error", (page, _, line) => page == 1 ? throw new IOException("broken") : line, RevitCommandCodes.PipeIoError);
    }

    // The CLI adapter: each page goes through RevitCommandService.Run (pipe rule, envelope validation, guards, receipt).
    static void ServiceTransport()
    {
        var fake = new FakeAddIn();
        List<string> wire = [];
        Func<string, string, string?> forward = (pipe, line) =>
        {
            wire.Add(line);
            return pipe == Pipe ? fake.Send(JsonNode.Parse(line)!["Command"]!.ToJsonString()) : null;
        };
        var args = Args("wall", null, 4);
        var result = RevitElementQueryPaging.All(Envelope(args).ToJsonString(), new RevitServiceTransport(Pipe, RevitCommandPolicy.None, forward));
        Check(result.IsSuccess && Canonical(result.Elements).SequenceEqual(Unlimited(args, fake.Items).Elements) && wire.Count == result.Pages && wire.All(w => JsonNode.Parse(w)!["DocumentPath"]!.GetValue<string>() == RealPath),
            "through RevitCommandService each page is one wire line and the result equals one unlimited query", Describe(result));
        List<string> rejected = [];
        var badPipe = RevitElementQueryPaging.All(Envelope(args).ToJsonString(), new RevitServiceTransport("not-a-process-pipe", RevitCommandPolicy.None, (p, l) => { rejected.Add(l); return null; }));
        Check(!badPipe.IsSuccess && badPipe.Code == RevitCommandCodes.ExplicitProcessPipeRequired && rejected.Count == 0, "a non-process pipe is refused by the service before any wire line", Describe(badPipe));
        var noReply = RevitElementQueryPaging.All(Envelope(args).ToJsonString(), new RevitServiceTransport(Pipe, RevitCommandPolicy.None, (_, _) => null));
        Check(!noReply.IsSuccess && noReply.Code == RevitCommandCodes.NoResponse, "a closed pipe is no_response", Describe(noReply));
    }

    static void Json()
    {
        var result = RevitElementQueryPaging.All(Envelope(Args("door", null, 2)).ToJsonString(), new FakeAddIn());
        var root = JsonNode.Parse(result.ToJson())!.AsObject();
        Check(string.Join(",", root.Select(m => m.Key)) == "status,code,field,detail,pages,matched,returned,elements", "the result JSON members are status, code, field, detail, pages, matched, returned, elements", result.ToJson());
        Check(root["status"]!.GetValue<string>() == "succeeded" && root["code"]!.GetValue<string>() == "ok" && root["pages"]!.GetValue<int>() == result.Pages && root["matched"]!.GetValue<int>() == result.Matched
            && root["returned"]!.GetValue<int>() == result.Elements.Count && root["elements"]!.AsArray().Select(e => e!.ToJsonString()).SequenceEqual(Canonical(result.Elements)), "the result JSON carries the concatenated elements and counts");
        var failed = RevitElementQueryPaging.All(Envelope(new JsonObject(), "query.levels").ToJsonString(), new FakeAddIn());
        var failedRoot = JsonNode.Parse(failed.ToJson())!;
        Check(failedRoot["status"]!.GetValue<string>() == "failed" && failedRoot["code"]!.GetValue<string>() == RevitElementQueryPagingCodes.CommandNotPageable && failedRoot["matched"] is null && failedRoot["elements"]!.AsArray().Count == 0, "a refusal's JSON has status failed, its code, matched null and no elements", failed.ToJson());
    }

    static JsonObject Also(this JsonObject node, Action<JsonObject> change) { change(node); return node; }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
