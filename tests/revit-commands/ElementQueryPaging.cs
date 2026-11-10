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
