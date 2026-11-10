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


    static JsonObject ReadbackOf(string line) => JsonNode.Parse(line)!["readback"]!.AsObject();


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
