using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;

// P1-21 `revit-command envelope.json process-pipe-name --all [--read-timeout-seconds n] --deadline-seconds n` through
// the real Release CLI, the overall deadline of the MCP all:true listing (P1-16) on the CLI --all paging run.
// Oracles:
//   1. A value that is not whole seconds from 1 to 86400, a missing value, the flag without --all and a repeated flag
//      are refused typed in the RevitCommandResponse shape (field args[i], exit 1) before any file or pipe I/O: the
//      envelope file does not exist, the pipe name may be bad and the stalled add-in pipe receives nothing. In range
//      (1, 86400, 0030) with --all, in any order, the run goes on to the envelope read (listing-shape refusal).
//   2. In process (RevitDeadlineTransport, injected clock and cut transport): a page due after the deadline is answered
//      query_all_deadline_exceeded without being sent; a page whose time left is below the read timeout is read with
//      the time left, and its pipe_read_timeout is reported as the deadline; above it, the configured transport is used.
//   3. A stalled add-in pipe under --all --deadline-seconds 2 ends with query_all_deadline_exceeded in the listing shape
//      (pages 0, no elements) after about 2 s, also under --read-timeout-seconds 120, sent once; a read timeout below
//      the deadline stays pipe_read_timeout. A paging add-in that answers each page after 250 ms ends a 2 s deadline
//      run with query_all_deadline_exceeded before the listing is complete, without partial elements.
//   3a. P1-30: the pipe connect is bounded too: it waits min(RevitCommandService.ConnectTimeoutMs = 5 s, time left).
//      With no add-in listening a 1 s or 2 s deadline run ends query_all_deadline_exceeded (pinned bytes) within the
//      deadline + 1 s (the CLI bound adds the measured start-up of a refused run, as the clock starts before paging);
//      a 7 s deadline (time left at least 5 s) still ends pipe_connect_timeout after the full 5 s connect, byte-equal to
//      the run without a deadline, which keeps today's 5 s connect. In process (no process start-up) the real-pipe
//      RevitDeadlineTransport and MCP all:true (real pipe transport, 1 s QueryAllDeadline) end within 1 s + 1 s with
//      query_all_deadline_exceeded, having waited the deadline in the connect.
//   2b. A cut page's no_response / pipe_io_error pass through unchanged; time left equal to the read timeout is not cut.
//   4. Without the flag no deadline applies (the paging add-in lists all rows) and the stdout is byte-equal to a run
//      with a deadline long enough; the CLI still waits 3 s on a stalled pipe without the flag.
// Unit-tested only; no Revit.
internal static class RevitCommandDeadlineTests
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string DeadlineUsage = "usage: revit-command envelope.json process-pipe-name --all [--read-timeout-seconds n] --deadline-seconds n";
    const string Usage = "usage: revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n]";
    const string Flag = "--deadline-seconds";
    const string ReadFlag = "--read-timeout-seconds";
    const string Exceeded = "query_all_deadline_exceeded";

    static string Envelope(string command, JsonObject args) => new JsonObject
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-p1-21",
        ["document"] = new JsonObject { ["path"] = @"C:\Projects\P1-21\Model.rvt", ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args,
    }.ToJsonString();

    static JsonObject? Node(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (Exception) { return null; }
    }

    static string? Code(JsonObject? node) => node?["code"]?.GetValue<string>();
    static int? Pages(JsonObject? node) => node?["pages"]?.GetValue<int>();
    static int? ElementCount(JsonObject? node) => (node?["elements"] as JsonArray)?.Count;

    static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    static ProcessStartInfo Start(string dotnet, string cliDll, string[] arguments)
    {
        var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
        start.ArgumentList.Add(cliDll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    static (int Exit, string Out, long Ms) Cli(string dotnet, string cliDll, params string[] arguments)
    {
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(Start(dotnet, cliDll, arguments)) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", watch.ElapsedMilliseconds); }
        process.WaitForExit();
        watch.Stop();
        var text = stdout.Result.TrimEnd('\r', '\n');
        var error = stderr.Result.ReplaceLineEndings(" ").Trim();
        return (process.ExitCode, error.Length > 0 ? text + " stderr " + error : text, watch.ElapsedMilliseconds);
    }

    // True when the request reached the stalled add-in (its requests-th line) and the CLI is still running `wait` later.
    static bool StillWaiting(string dotnet, string cliDll, StalledAddIn addIn, int requests, TimeSpan wait, params string[] arguments)
    {
        using var process = Process.Start(Start(dotnet, cliDll, arguments)) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        var reached = false;
        var watch = Stopwatch.StartNew();
        while (!process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (addIn.Received.Count >= requests) { reached = true; break; }
            Thread.Sleep(10);
        }
        var exited = !reached || process.WaitForExit((int)wait.TotalMilliseconds);
        if (!process.HasExited) process.Kill(true);
        process.WaitForExit();
        return reached && !exited;
    }

    static string WallUid(long id) => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + id.ToString("x8", CultureInfo.InvariantCulture);

    // A fake add-in page reply: the model's walls paged with the real paging rule and cursors (as tests/mcp AllReply).
    static string PageReply(string wire, long[] model)
    {
        string command;
        using (var wireDocument = JsonDocument.Parse(wire)) command = wireDocument.RootElement.GetProperty("Command").GetRawText();
        var envelope = RevitCommandProtocol.Parse(command);
        var queryArgs = (ElementQueryArgs)envelope.Args;
        var (selected, truncated) = RevitElementQueryRules.Page(model, queryArgs.Limit, queryArgs.Cursor?.After);
        var rows = selected.Select(id => new ObservedQueryElement(id, WallUid(id), "Walls", "OST_Walls") { TypeName = "T" + N(id) }).ToArray();
        var readback = RevitElementQueryJson.Elements(envelope.Document.UniqueId, queryArgs, null, model.Length, truncated, rows, selected);
        return RevitCommandResponse.Success(new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), readback, Array.Empty<string>()).ToJson();
    }

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        _ = root;
        var dir = Path.Combine(work, "p1-21");
        Directory.CreateDirectory(dir);
        var walls = Path.Combine(dir, "walls.json");
        var wallsText = Envelope("query.elements", new JsonObject { ["category"] = "wall", ["limit"] = 1 });
        File.WriteAllText(walls, wallsText);
        var missing = Path.Combine(dir, "missing-envelope.json");
        string Refusal(string code, int index, string detail, string usage) => RevitCommandResponse.Failure(code, "args[" + N(index) + "]", detail + "; " + usage).ToJson();
        (int Exit, string Out, long Ms) Rc(params string[] arguments) => Cli(dotnet, cliDll, ["revit-command", .. arguments]);
        var model = Enumerable.Range(0, 23).Select(i => 3000L + i * 7).ToArray();

        // ---- 0. In process: the option and the code ----
        Check(Bim.Cli.RevitDeadlineTransport.Code == Exceeded && Bim.Cli.RevitDeadlineTransport.Code == Bim.Mcp.McpServer.QueryAllDeadlineExceeded,
            "the CLI deadline code is query_all_deadline_exceeded, the MCP all:true code");
        Check(Bim.Cli.RevitCommandOptions.DeadlineInvalid == "deadline_invalid" && !RevitCommandCodes.ExecutorReserved.Contains(Bim.Cli.RevitCommandOptions.DeadlineInvalid),
            "a bad " + Flag + " is the client-side code deadline_invalid");
        var plainOptions = Bim.Cli.RevitCommandOptions.Parse(["revit-command", missing, "bim-agentic-os-2025-1", "--all"], out _);
        Check(plainOptions is { All: true, Deadline: null }, "RevitCommandOptions without " + Flag + " has no deadline");
        var deadlineOptions = Bim.Cli.RevitCommandOptions.Parse(["revit-command", missing, "bim-agentic-os-2025-1", Flag, "7", "--all", ReadFlag, "9"], out var deadlineRefusal);
        Check(deadlineOptions is { All: true } && deadlineRefusal is null && deadlineOptions.Deadline == TimeSpan.FromSeconds(7) && deadlineOptions.ReadTimeout == TimeSpan.FromSeconds(9),
            "RevitCommandOptions with " + Flag + " 7 --all " + ReadFlag + " 9 has a 7 s deadline and a 9 s read timeout, any order");

        // ---- 2. In process: RevitDeadlineTransport on an injected clock ----
        const string InProcessPipe = "bim-agentic-os-2025-2121";
        var clock = TimeSpan.Zero;
        var sent = 0;
        var cuts = new List<TimeSpan>();
        string? Paging(string pipe, string line) { sent++; clock += TimeSpan.FromMinutes(1); return PageReply(line, model); }
        Func<string, string, string?> Cut(TimeSpan left) { cuts.Add(left); return Paging; }
        var slow = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), () => clock, Cut);
        var late = RevitElementQueryPaging.All(wallsText, slow);
        var lateJson = Node(late.ToJson());
        Check(!late.IsSuccess && Code(lateJson) == Exceeded && lateJson?["field"] is null && Pages(lateJson) == 3 && ElementCount(lateJson) == 0 && sent == 3 && cuts.Count == 0
            && lateJson?["detail"]?.GetValue<string>() == "the --all listing took longer than its 180000 ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor",
            "past its deadline the next page is refused query_all_deadline_exceeded without being sent (no partial elements, read timeout below the time left: not cut)", late.ToJson() + " sent " + N(sent));
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        var cutting = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, (_, _) => throw new InvalidOperationException("uncut transport used"), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), () => clock, Cut);
        var cutListing = RevitElementQueryPaging.All(wallsText, cutting);
        Check(!cutListing.IsSuccess && Code(Node(cutListing.ToJson())) == Exceeded && sent == 6
            && cuts.SequenceEqual(new[] { 330, 270, 210, 150, 90, 30 }.Select(s => TimeSpan.FromSeconds(s))),
            "each page under a read timeout above the time left is read with the time left (330 s, 270 s, ... 30 s)", string.Join(",", cuts.Select(c => N((long)c.TotalSeconds))));
        clock = TimeSpan.Zero; sent = 0;
        var stalledCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            left => (_, _) => throw new RevitPipeReadTimeoutException(left));
        var stalledCutJson = Node(RevitElementQueryPaging.All(wallsText, stalledCut).ToJson());
        Check(Code(stalledCutJson) == Exceeded && Pages(stalledCutJson) == 0 && ElementCount(stalledCutJson) == 0,
            "a cut page's pipe_read_timeout is reported as query_all_deadline_exceeded", stalledCutJson?.ToJsonString() ?? "");
        // A cut page's failure other than its read timeout passes through unchanged (not reported as the deadline).
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        var closedCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            left => { cuts.Add(left); return (_, _) => null; });
        var closedCutJson = Node(RevitElementQueryPaging.All(wallsText, closedCut).ToJson());
        Check(Code(closedCutJson) == RevitCommandCodes.NoResponse && Pages(closedCutJson) == 0 && ElementCount(closedCutJson) == 0 && cuts.Count == 1,
            "a cut page whose pipe closes without a reply stays no_response", closedCutJson?.ToJsonString() ?? "");
        var brokenCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            left => (_, _) => throw new IOException("broken pipe"));
        var brokenCutJson = Node(RevitElementQueryPaging.All(wallsText, brokenCut).ToJson());
        Check(Code(brokenCutJson) == RevitCommandCodes.PipeIoError && Pages(brokenCutJson) == 0 && ElementCount(brokenCutJson) == 0,
            "a cut page whose pipe fails with an IOException stays pipe_io_error", brokenCutJson?.ToJsonString() ?? "");
        // Time left equal to the read timeout (60 s / 60 s): no cut, the configured transport's pipe_read_timeout stays.
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        var equalLeft = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, (_, _) => throw new RevitPipeReadTimeoutException(TimeSpan.FromSeconds(60)),
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), () => clock, Cut);
        var equalLeftJson = Node(RevitElementQueryPaging.All(wallsText, equalLeft).ToJson());
        Check(Code(equalLeftJson) == RevitCommandCodes.PipeReadTimeout && equalLeftJson?["detail"]?.GetValue<string>() is { } equalDetail && equalDetail.Contains("60000 ms", StringComparison.Ordinal)
            && Pages(equalLeftJson) == 0 && cuts.Count == 0,
            "time left equal to the read timeout is not cut: pipe_read_timeout, not query_all_deadline_exceeded", equalLeftJson?.ToJsonString() ?? "");
        clock = TimeSpan.Zero; sent = 0;
        var roomy = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), () => clock, Cut);
        var roomyListing = RevitElementQueryPaging.All(wallsText, roomy);
        var undeadlined = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(InProcessPipe, RevitCommandPolicy.None, Paging));
        Check(roomyListing.IsSuccess && roomyListing.Elements.Count == 23 && roomyListing.ToJson() == undeadlined.ToJson(),
            "within its deadline the listing is byte-equal to RevitServiceTransport paging");

        // A .NET pipe on Unix is a Unix domain socket under Path.GetTempPath(), whose path must fit 108 characters;
        // verify.ps1 points TMPDIR at a long evidence directory, so this section (and the CLI it starts) uses /tmp/.
        var tmpDir = Environment.GetEnvironmentVariable("TMPDIR");
        if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR", "/tmp/");
        try
        {
            var pipe = "bim-agentic-os-2025-" + N(Random.Shared.Next(100000000, 999999999));
            using var addIn = new StalledAddIn(pipe);

            // ---- 1. Typed refusals before any I/O ----
            foreach (var (label, value) in new[] { ("zero", "0"), ("negative", "-1"), ("over 24 h", "86401"), ("fractional", "1.5"), ("signed", "+5"), ("padded", " 5"),
                ("empty", ""), ("not a number", "abc"), ("overflowing", "99999999999999999999") })
            {
                var bad = Rc(missing, pipe, "--all", Flag, value);
                Check(bad.Exit == 1 && bad.Out == Refusal(Bim.Cli.RevitCommandOptions.DeadlineInvalid, 5, Flag + " takes whole seconds from 1 to 86400, got " + value, DeadlineUsage),
                    "revit-command --all " + Flag + " " + label + " is deadline_invalid at args[5] before any I/O", "exit " + bad.Exit + " " + bad.Out);
            }
            var noValue = Rc(missing, pipe, "--all", Flag);
            Check(noValue.Exit == 1 && noValue.Out == Refusal(Bim.Cli.RevitCommandOptions.DeadlineInvalid, 4, Flag + " needs a value", DeadlineUsage), "revit-command --all " + Flag + " without a value is deadline_invalid at the flag", noValue.Out);
            var noAll = Rc(missing, pipe, Flag, "5");
            Check(noAll.Exit == 1 && noAll.Out == Refusal(Bim.Cli.RevitCommandOptions.DeadlineInvalid, 3, Flag + " needs --all", DeadlineUsage), "revit-command " + Flag + " without --all is deadline_invalid at the flag", noAll.Out);
            var noAllTimed = Rc(missing, pipe, ReadFlag, "5", Flag, "5");
            Check(noAllTimed.Exit == 1 && noAllTimed.Out == Refusal(Bim.Cli.RevitCommandOptions.DeadlineInvalid, 5, Flag + " needs --all", DeadlineUsage), "revit-command " + ReadFlag + " 5 " + Flag + " 5 without --all is deadline_invalid at the flag", noAllTimed.Out);
            var badPipe = Rc(missing, "not-a-process-pipe", "--all", Flag, "0");
            Check(badPipe.Exit == 1 && badPipe.Out == Refusal(Bim.Cli.RevitCommandOptions.DeadlineInvalid, 5, Flag + " takes whole seconds from 1 to 86400, got 0", DeadlineUsage), "the deadline is checked before the pipe name", badPipe.Out);
            var repeated = Rc(missing, pipe, "--all", Flag, "5", Flag, "6");
            Check(repeated.Exit == 1 && repeated.Out == Refusal(RevitCommandCodes.ExplicitProcessPipeRequired, 6, "repeated option " + Flag, Usage), "a repeated " + Flag + " is refused at the repeat", repeated.Out);
            var badRead = Rc(missing, pipe, "--all", Flag, "5", ReadFlag, "0");
            Check(badRead.Exit == 1 && badRead.Out == Refusal(RevitCommandCodes.ReadTimeoutInvalid, 7, ReadFlag + " takes whole seconds from 1 to 86400, got 0", Usage), "a bad " + ReadFlag + " next to " + Flag + " keeps its P1-20 refusal bytes", badRead.Out);
            foreach (var order in new[] { new[] { "--all", Flag, "1" }, new[] { Flag, "86400", "--all" }, new[] { ReadFlag, "3", Flag, "0030", "--all" } })
            {
                var accepted = Rc([missing, pipe, .. order]);
                var listing = Node(accepted.Out);
                Check(accepted.Exit == 1 && Code(listing) == RevitCommandCodes.EnvelopeInvalid && Pages(listing) == 0,
                    "revit-command " + string.Join(" ", order) + " is accepted (listing-shape envelope refusal)", accepted.Out);
            }
            Check(addIn.Received.Count == 0, "no refusal or envelope failure reached the add-in pipe", N(addIn.Received.Count));

            // ---- 3. A stalled add-in pipe ends at the overall deadline ----
            var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(wallsText));
            var stalled = Rc(walls, pipe, "--all", Flag, "2");
            var stalledJson = Node(stalled.Out);
            Check(stalled.Exit == 1 && stalledJson?["status"]?.GetValue<string>() == "failed" && Code(stalledJson) == Exceeded && Pages(stalledJson) == 0 && ElementCount(stalledJson) == 0
                && stalledJson?["detail"]?.GetValue<string>() is { } stalledDetail && stalledDetail.StartsWith("the --all listing took longer than its 2000 ms deadline", StringComparison.Ordinal),
                "revit-command --all " + Flag + " 2 on a stalled pipe is query_all_deadline_exceeded in the listing shape", "exit " + stalled.Exit + " " + stalled.Out);
            Check(stalled.Ms >= 2000 && stalled.Ms < 30000 && addIn.WaitForRequests(1) && addIn.Received.Count == 1 && addIn.Received[0] == wire,
                "the stalled page is sent once with the exact wire line and the run ends within the bound (about 2 s, not 1800 s)", N(stalled.Ms) + " ms");
            var stalledLong = Rc(walls, pipe, ReadFlag, "120", "--all", Flag, "2");
            Check(stalledLong.Exit == 1 && Code(Node(stalledLong.Out)) == Exceeded && stalledLong.Ms >= 2000 && stalledLong.Ms < 30000 && addIn.WaitForRequests(2) && addIn.Received.Count == 2,
                "a 2 s deadline under a 120 s read timeout cuts the stalled page to the time left", stalledLong.Out + " in " + N(stalledLong.Ms) + " ms");
            var readFirst = Rc(walls, pipe, "--all", ReadFlag, "1", Flag, "20");
            var readFirstJson = Node(readFirst.Out);
            Check(readFirst.Exit == 1 && Code(readFirstJson) == RevitCommandCodes.PipeReadTimeout && readFirstJson?["detail"]?.GetValue<string>() is { } readDetail && readDetail.Contains("1000 ms", StringComparison.Ordinal)
                && readFirst.Ms >= 1000 && readFirst.Ms < 20000 && addIn.WaitForRequests(3) && addIn.Received.Count == 3,
                "a 1 s read timeout below a 20 s deadline stays pipe_read_timeout", readFirst.Out + " in " + N(readFirst.Ms) + " ms");

            // ---- 3a. P1-30: the deadline also bounds the pipe connect (connect wait min(5 s, time left)) ----
            AbsentPipeConnect(root, walls, wallsText, Rc, Check);

            // ---- 3b. A slow paging add-in: the whole run is bounded, not each page ----
            addIn.Reply = line => PageReply(line, model);
            addIn.Delay = TimeSpan.FromMilliseconds(250);
            var before = addIn.Received.Count;
            var slowRun = Rc(walls, pipe, "--all", Flag, "2");
            var slowJson = Node(slowRun.Out);
            var slowSent = addIn.Received.Count - before;
            Check(slowRun.Exit == 1 && Code(slowJson) == Exceeded && ElementCount(slowJson) == 0 && Pages(slowJson) is { } slowPages && slowPages < 23
                && slowSent >= 1 && slowSent < 23 && slowRun.Ms < 30000,
                "a 250 ms-per-page add-in (23 pages, about 6 s) under a 2 s deadline ends query_all_deadline_exceeded before the 23-page listing completes, no partial elements", slowRun.Out + " sent " + N(slowSent) + " in " + N(slowRun.Ms) + " ms");

            // ---- 4. Absent flag: no deadline, today's bytes ----
            addIn.Delay = TimeSpan.FromMilliseconds(100);
            var plainAll = Rc(walls, pipe, "--all");
            var plainJson = Node(plainAll.Out);
            var roomyAll = Rc(walls, pipe, "--all", Flag, "600");
            Check(plainAll.Exit == 0 && plainJson?["status"]?.GetValue<string>() == "succeeded" && ElementCount(plainJson) == 23 && Pages(plainJson) == 23 && plainAll.Ms >= 2300,
                "without " + Flag + " a 23-page listing taking over 2 s completes (no deadline)", plainAll.Out.Length > 300 ? plainAll.Out[..300] : plainAll.Out);
            Check(roomyAll.Exit == 0 && roomyAll.Out == plainAll.Out, "within a 600 s deadline the --all stdout is byte-equal to the run without " + Flag);
            addIn.Reply = null;
            addIn.Delay = TimeSpan.Zero;
            var waitBefore = addIn.Received.Count;
            Check(StillWaiting(dotnet, cliDll, addIn, waitBefore + 1, TimeSpan.FromSeconds(3), "revit-command", walls, pipe, "--all"), "without " + Flag + " the --all CLI still waits 3 s on a stalled pipe (1800 s read timeout, no deadline)");
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR", tmpDir);
        }
    }

    const string AbsentExceededCli = """{"status":"failed","code":"query_all_deadline_exceeded","field":null,"detail":"the --all listing took longer than its 1000 ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor","pages":0,"matched":null,"returned":0,"elements":[]}""";
    const string AbsentExceededMcp = """{"status":"failed","code":"query_all_deadline_exceeded","field":null,"detail":"the all:true listing took longer than its 1000 ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor","pages":0,"matched":null,"returned":0,"elements":[]}""";

    static string AbsentPipe() => "bim-agentic-os-2025-" + N(Random.Shared.Next(100000000, 999999999));

    // P1-30 (section 3a): no add-in listening on the pipe (a name nothing serves), so every connect waits its full timeout.
    static void AbsentPipeConnect(string root, string walls, string wallsText, Func<string[], (int Exit, string Out, long Ms)> rc, Action<bool, string, string> check)
    {
        var connectMs = RevitCommandService.ConnectTimeoutMs;
        // In process, injected clock and connect transport: which pipe transport each page gets and what its codes become.
        const string SeamPipe = "bim-agentic-os-2025-3030";
        var calls = new List<string>();
        string Label(TimeSpan t) => N((long)t.TotalMilliseconds);
        Func<string, string, string?> Throws(Exception error) => (_, _) => throw error;
        string Listing(TimeSpan readTimeout, TimeSpan deadline, Exception error, bool cutReads = true)
        {
            calls.Clear();
            var seam = new Bim.Cli.RevitDeadlineTransport(SeamPipe, RevitCommandPolicy.None, (pipe, line) => { calls.Add("transport"); throw error; }, readTimeout, deadline, () => TimeSpan.Zero,
                left => { calls.Add("cut " + Label(left)); return Throws(error); }, cutReads: cutReads,
                connectTransport: (read, connect) => { calls.Add("connect " + Label(read) + "/" + Label(connect)); return Throws(error); });
            return RevitElementQueryPaging.All(wallsText, seam).ToJson();
        }
        var connectCut = Listing(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), new TimeoutException());
        check(Code(Node(connectCut)) == Exceeded && Pages(Node(connectCut)) == 0 && calls.SequenceEqual(new[] { "connect 3000/3000" }),
            "P1-30 seam: 3 s left under a 30 s read timeout is read and connected with the time left (3000/3000 ms); its pipe_connect_timeout is query_all_deadline_exceeded",
            connectCut + " calls " + string.Join(",", calls));
        var readKept = Listing(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), new RevitPipeReadTimeoutException(TimeSpan.FromSeconds(2)));
        check(Code(Node(readKept)) == RevitCommandCodes.PipeReadTimeout && calls.SequenceEqual(new[] { "connect 2000/4000" }),
            "P1-30 seam: 4 s left over a 2 s read timeout keeps the 2 s read, connects within 4 s, and its pipe_read_timeout passes through",
            readKept + " calls " + string.Join(",", calls));
        var readKeptConnect = Listing(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), new TimeoutException());
        check(Code(Node(readKeptConnect)) == Exceeded && calls.SequenceEqual(new[] { "connect 2000/4000" }),
            "P1-30 seam: a connect cut to 4 s left (read not cut) that times out is query_all_deadline_exceeded", readKeptConnect + " calls " + string.Join(",", calls));
        var fiveLeft = Listing(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), new TimeoutException());
        check(Code(Node(fiveLeft)) == RevitCommandCodes.PipeConnectTimeout && calls.SequenceEqual(new[] { "cut 5000" }),
            "P1-30 seam: exactly 5 s left keeps the full 5 s connect (read cut only) and its pipe_connect_timeout passes through", fiveLeft + " calls " + string.Join(",", calls));
        var tenLeft = Listing(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), new TimeoutException());
        check(Code(Node(tenLeft)) == RevitCommandCodes.PipeConnectTimeout && calls.SequenceEqual(new[] { "transport" }),
            "P1-30 seam: 10 s left over a 3 s read timeout uses the configured transport; its pipe_connect_timeout passes through", tenLeft + " calls " + string.Join(",", calls));
        var uncut = Listing(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), new TimeoutException(), cutReads: false);
        check(Code(Node(uncut)) == RevitCommandCodes.PipeConnectTimeout && calls.SequenceEqual(new[] { "transport" }),
            "P1-30 seam: cutReads false (MCP injected transport) never cuts the connect: pipe_connect_timeout passes through", uncut + " calls " + string.Join(",", calls));
        static bool Refused(Action make) { try { make(); return false; } catch (ArgumentOutOfRangeException) { return true; } }
        foreach (var (label, bad) in new[] { ("zero", TimeSpan.Zero), ("negative", TimeSpan.FromMilliseconds(-1)), ("over 5 s", TimeSpan.FromMilliseconds(5001)) })
            check(Refused(() => RevitCommandService.PipeTransportWithTimeouts(TimeSpan.FromSeconds(1), bad)), "P1-30 PipeTransportWithTimeouts refuses a " + label + " connect timeout when made", "");
        check(Refused(() => RevitCommandService.PipeTransportWithTimeouts(TimeSpan.Zero, TimeSpan.FromSeconds(1))) && !Refused(() => RevitCommandService.PipeTransportWithTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)))
            && Bim.Cli.RevitDeadlineTransport.ConnectWait == TimeSpan.FromMilliseconds(RevitCommandService.ConnectTimeoutMs) && RevitCommandService.ConnectTimeoutMs == 5000,
            "P1-30 PipeTransportWithTimeouts refuses a bad read timeout and accepts 5 s; the connect wait is the 5 s ConnectTimeoutMs", "");

        // Static: MCP all:true builds the shared RevitDeadlineTransport with the default connect transport (no override).
        var mcpSource = File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs"));
        var allStart = mcpSource.IndexOf("(string Text, bool IsError) RevitCallAll(", StringComparison.Ordinal);
        var built = allStart < 0 ? -1 : mcpSource.IndexOf("new RevitDeadlineTransport(", allStart, StringComparison.Ordinal);
        var builtArgs = built < 0 ? "" : mcpSource[built..mcpSource.IndexOf(';', built)];
        check(builtArgs.Contains("cutReads: realPipe", StringComparison.Ordinal) && !builtArgs.Contains("connectTransport", StringComparison.Ordinal)
            && !builtArgs.Contains("ConnectTimeout", StringComparison.Ordinal) && !builtArgs.Contains("cutTransport", StringComparison.Ordinal),
            "P1-30 static: McpServer.RevitCallAll builds RevitDeadlineTransport with cutReads: realPipe and the default (shared) connect bound", builtArgs.ReplaceLineEndings(" "));

        // Static: no source under interfaces/cli or interfaces/mcp still says the connect is not cut. Comment markers and
        // line breaks are folded into single spaces first, so a phrase wrapped over two comment lines is still found.
        string[] stale = ["connect is not cut", "connect of a page is not cut", "deadline + up to 5 s", "does not cut a page's 5 s pipe connect"];
        var staleHits = new List<string>();
        foreach (var dir in new[] { Path.Combine(root, "interfaces", "cli"), Path.Combine(root, "interfaces", "mcp") })
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj")))
            {
                var folded = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), @"\s*(?:\r?\n)\s*(?://+)?\s*", " ");
                folded = System.Text.RegularExpressions.Regex.Replace(folded, @"[ \t]+", " ");
                foreach (var phrase in stale)
                    if (folded.Contains(phrase, StringComparison.Ordinal)) staleHits.Add(Path.GetRelativePath(root, file).Replace('\\', '/') + ": " + phrase);
            }
        check(staleHits.Count == 0, "P1-30 static: no interfaces/cli or interfaces/mcp source says the deadline does not cut the pipe connect", string.Join("; ", staleHits));

        // The process start-up and envelope read of a refused --all run: the CLI clock starts just before paging.
        // The larger of two samples, so one fast start does not tighten the bounds below.
        var startupRuns = new[] { 0, 1 }.Select(_ => rc([Path.Combine(Path.GetDirectoryName(walls)!, "missing-envelope.json"), AbsentPipe(), "--all", Flag, "1"])).ToArray();
        check(startupRuns.All(run => run.Exit == 1 && Code(Node(run.Out)) == RevitCommandCodes.EnvelopeInvalid), "P1-30 start-up baseline: a missing envelope under --all is refused in the listing shape (2 samples)",
            string.Join(" ; ", startupRuns.Select(run => run.Out + " in " + N(run.Ms) + " ms")));
        var startupMs = startupRuns.Max(run => run.Ms);

        // In process, the real pipe transport: a 1 s deadline ends at the deadline, not after the 5 s connect.
        var inProcessWatch = Stopwatch.StartNew();
        var inProcess = RevitElementQueryPaging.All(wallsText, new Bim.Cli.RevitDeadlineTransport(AbsentPipe(), RevitCommandPolicy.None, RevitCommandService.PipeTransport,
            RevitCommandService.DefaultReadTimeout, TimeSpan.FromSeconds(1)));
        inProcessWatch.Stop();
        check(inProcess.ToJson() == AbsentExceededCli && inProcessWatch.ElapsedMilliseconds >= 900 && inProcessWatch.ElapsedMilliseconds < 2000,
            "P1-30 in process: RevitDeadlineTransport 1 s on an absent pipe is query_all_deadline_exceeded within 1 s + 1 s (connect cut to the time left)",
            inProcess.ToJson() + " in " + N(inProcessWatch.ElapsedMilliseconds) + " ms");

        // MCP all:true on the real pipe transport shares the bound.
        var server = new Bim.Mcp.McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, null, null) { QueryAllDeadline = TimeSpan.FromSeconds(1) };
        var arguments = new JsonObject
        {
            ["pipe_name"] = AbsentPipe(), ["operation_id"] = "op-p1-30", ["document"] = new JsonObject { ["path"] = @"C:\Projects\P1-21\Model.rvt", ["unique_id"] = Uid },
            ["args"] = new JsonObject { ["category"] = "wall", ["limit"] = 1 }, ["all"] = true,
        };
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 30, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "revit_query_elements", ["arguments"] = arguments } };
        var mcpWatch = Stopwatch.StartNew();
        var mcpResponse = Node(server.Handle(request.ToJsonString()) ?? "");
        mcpWatch.Stop();
        var mcpText = mcpResponse?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        check(mcpText == AbsentExceededMcp && mcpResponse?["result"]?["isError"]?.GetValue<bool>() == true && mcpWatch.ElapsedMilliseconds >= 900 && mcpWatch.ElapsedMilliseconds < 2000,
            "P1-30 MCP all:true (real pipe, 1 s QueryAllDeadline) on an absent pipe is query_all_deadline_exceeded within 1 s + 1 s",
            (mcpText ?? mcpResponse?.ToJsonString() ?? "<no response>") + " in " + N(mcpWatch.ElapsedMilliseconds) + " ms");

        // The real CLI: N = 1, 2 and 5 end query_all_deadline_exceeded within N + 1 s (plus the measured start-up). At N = 5
        // the first page has just under 5 s left, so its connect is cut too: pipe_connect_timeout needs time left >= 5 s
        // at the page (N >= 6 in practice).
        foreach (var seconds in new[] { 1, 2, 5 })
        {
            var absent = rc([walls, AbsentPipe(), "--all", Flag, N(seconds)]);
            var expected = AbsentExceededCli.Replace("its 1000 ms deadline", "its " + N(seconds * 1000L) + " ms deadline", StringComparison.Ordinal);
            check(absent.Exit == 1 && absent.Out == expected && absent.Ms >= seconds * 1000L && absent.Ms < seconds * 1000L + 1000 + startupMs,
                "P1-30 revit-command --all " + Flag + " " + N(seconds) + " with no add-in listening ends query_all_deadline_exceeded within the deadline + 1 s (start-up " + N(startupMs) + " ms), not after the 5 s connect",
                "exit " + absent.Exit + " " + absent.Out + " in " + N(absent.Ms) + " ms");
        }
        // N = 7 (time left at least 5 s): the full 5 s connect, pipe_connect_timeout, byte-equal to the run without a deadline.
        var roomy = rc([walls, AbsentPipe(), "--all", Flag, "7"]);
        var roomyJson = Node(roomy.Out);
        check(roomy.Exit == 1 && Code(roomyJson) == RevitCommandCodes.PipeConnectTimeout && Pages(roomyJson) == 0 && roomy.Ms >= connectMs && roomy.Ms < 7000 + 1000 + startupMs,
            "P1-30 revit-command --all " + Flag + " 7 with no add-in listening ends pipe_connect_timeout after the full 5 s connect, within 7 s + 1 s",
            "exit " + roomy.Exit + " " + roomy.Out + " in " + N(roomy.Ms) + " ms");
        var plain = rc([walls, AbsentPipe(), "--all"]);
        check(plain.Exit == 1 && plain.Out == roomy.Out && plain.Ms >= connectMs && plain.Ms < connectMs + 1000 + startupMs,
            "P1-30 revit-command --all without " + Flag + " on an absent pipe keeps the 5 s connect (5 s to 6 s + start-up) and pipe_connect_timeout bytes (equal to the 7 s deadline run)",
            "exit " + plain.Exit + " " + plain.Out + " in " + N(plain.Ms) + " ms");
    }
}
