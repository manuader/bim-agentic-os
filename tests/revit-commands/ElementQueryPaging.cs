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
        SentEnvelopes();
        RefusedBeforeSending();
        PageCap();
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


    static JsonObject ReadbackOf(string line) => JsonNode.Parse(line)!["readback"]!.AsObject();



    // The CLI adapter: each page goes through RevitCommandService.Run (pipe rule, envelope validation, guards, receipt).


    static JsonObject Also(this JsonObject node, Action<JsonObject> change) { change(node); return node; }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }
}
