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
//   2c. P1-31, the reply-at-deadline race of RevitDeadlineTransport (injected clock, fake page transports). The fakes
//      MODEL the pipe read's keep-at-timeout rule, mirroring RevitCommandService.Send (core/Bim.Core/RevitCommandsService.cs,
//      a line complete when the read timeout fires is still the reply); they do not test it; Send's own cancel-race
//      branch is pinned in 2d (P1-33). Given that model: a reply line that completes exactly when its read timeout fires
//      (the time left of a cut read, the configured timeout, or a cut connect's read) is kept: a final page there ends the listing succeeded, byte-equal to RevitServiceTransport
//      paging; a line due one tick after it is the read timeout, query_all_deadline_exceeded on a cut page (pinned
//      bytes) and pipe_read_timeout on an uncut one. A non-final page completing exactly at the deadline leaves no time
//      for the next page, which is refused query_all_deadline_exceeded without being sent.
//   2d. P1-33: RevitCommandService.Send's read seam (ReadReplyLine, gated reads, no timing race): a reply line that
//      completes after the timed wait gives up but before the read is cancelled is KEPT (the reply, verified by Run; an
//      --all cut page ends succeeded); none by the cancel is pipe_read_timeout / query_all_deadline_exceeded (pinned).
//   2f. P1-35: ReadReplyLine's late outcomes: a read faulting on or after the cancel raises no
//      TaskScheduler.UnobservedTaskException (the late fault is observed; a deliberately unobserved control fault is
//      seen by the same detector), and a line completed from inside a cancel registration callback is KEPT when it
//      completes before the read's own cancel callback (LIFO order pinned), otherwise pipe_read_timeout.
//   2e. P1-33: cutReads false never cuts: a last page replying after the deadline is kept (succeeded, byte-equal).
//   3d. P1-34: a page with at least 5 s left (connect not cut) on the default (real pipe) transports is read with
//      min(configured read timeout, time left after the connect): an add-in accepting 4 s into a 6 s deadline and never
//      replying ends query_all_deadline_exceeded within the deadline + 300 ms (seam clock and a real late listener).
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

    // P1-36: the former three page seams (the configured transport for an uncut page, cut(read) for a page whose read is
    // cut, connect(read, connectWait) for a page whose connect is cut) as one connectThenRead fake. Like those seams it
    // asks the read wait when the page's transport is made (these fakes move no clock in a connect): a connect wait under
    // the 5 s ConnectWait is the cut connect, a read under the configured timeout the cut read, else the configured one.
    static Func<TimeSpan, Func<TimeSpan>, Func<string, string, string?>> Legacy(TimeSpan readTimeout, Func<string, string, string?> transport,
        Func<TimeSpan, Func<string, string, string?>> cut, Func<TimeSpan, TimeSpan, Func<string, string, string?>>? connect = null) => (wait, readAfter) =>
    {
        var read = readAfter();
        if (wait < Bim.Cli.RevitDeadlineTransport.ConnectWait)
            return connect is null ? throw new InvalidOperationException("cut connect not modelled by this fake") : connect(read, wait);
        return read < readTimeout ? cut(read) : transport;
    };

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
        var slow = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(3), () => clock,
            connectThenRead: Legacy(TimeSpan.FromSeconds(30), Paging, Cut));
        var late = RevitElementQueryPaging.All(wallsText, slow);
        var lateJson = Node(late.ToJson());
        Check(!late.IsSuccess && Code(lateJson) == Exceeded && lateJson?["field"] is null && Pages(lateJson) == 3 && ElementCount(lateJson) == 0 && sent == 3 && cuts.Count == 0
            && lateJson?["detail"]?.GetValue<string>() == "the --all listing took longer than its 180000 ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor",
            "past its deadline the next page is refused query_all_deadline_exceeded without being sent (no partial elements, read timeout below the time left: not cut)", late.ToJson() + " sent " + N(sent));
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        Func<string, string, string?> uncutUsed = (_, _) => throw new InvalidOperationException("uncut transport used");
        var cutting = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, uncutUsed, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), () => clock,
            connectThenRead: Legacy(TimeSpan.FromMinutes(30), uncutUsed, Cut));
        var cutListing = RevitElementQueryPaging.All(wallsText, cutting);
        Check(!cutListing.IsSuccess && Code(Node(cutListing.ToJson())) == Exceeded && sent == 6
            && cuts.SequenceEqual(new[] { 330, 270, 210, 150, 90, 30 }.Select(s => TimeSpan.FromSeconds(s))),
            "each page under a read timeout above the time left is read with the time left (330 s, 270 s, ... 30 s)", string.Join(",", cuts.Select(c => N((long)c.TotalSeconds))));
        clock = TimeSpan.Zero; sent = 0;
        var stalledCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            connectThenRead: Legacy(TimeSpan.FromMinutes(30), Paging, left => (_, _) => throw new RevitPipeReadTimeoutException(left)));
        var stalledCutJson = Node(RevitElementQueryPaging.All(wallsText, stalledCut).ToJson());
        Check(Code(stalledCutJson) == Exceeded && Pages(stalledCutJson) == 0 && ElementCount(stalledCutJson) == 0,
            "a cut page's pipe_read_timeout is reported as query_all_deadline_exceeded", stalledCutJson?.ToJsonString() ?? "");
        // A cut page's failure other than its read timeout passes through unchanged (not reported as the deadline).
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        var closedCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            connectThenRead: Legacy(TimeSpan.FromMinutes(30), Paging, left => { cuts.Add(left); return (_, _) => null; }));
        var closedCutJson = Node(RevitElementQueryPaging.All(wallsText, closedCut).ToJson());
        Check(Code(closedCutJson) == RevitCommandCodes.NoResponse && Pages(closedCutJson) == 0 && ElementCount(closedCutJson) == 0 && cuts.Count == 1,
            "a cut page whose pipe closes without a reply stays no_response", closedCutJson?.ToJsonString() ?? "");
        var brokenCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            connectThenRead: Legacy(TimeSpan.FromMinutes(30), Paging, left => (_, _) => throw new IOException("broken pipe")));
        var brokenCutJson = Node(RevitElementQueryPaging.All(wallsText, brokenCut).ToJson());
        Check(Code(brokenCutJson) == RevitCommandCodes.PipeIoError && Pages(brokenCutJson) == 0 && ElementCount(brokenCutJson) == 0,
            "a cut page whose pipe fails with an IOException stays pipe_io_error", brokenCutJson?.ToJsonString() ?? "");
        // Time left equal to the read timeout (60 s / 60 s): no cut, the configured transport's pipe_read_timeout stays.
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        Func<string, string, string?> sixtyTimeout = (_, _) => throw new RevitPipeReadTimeoutException(TimeSpan.FromSeconds(60));
        var equalLeft = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, sixtyTimeout,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), () => clock, connectThenRead: Legacy(TimeSpan.FromSeconds(60), sixtyTimeout, Cut));
        var equalLeftJson = Node(RevitElementQueryPaging.All(wallsText, equalLeft).ToJson());
        Check(Code(equalLeftJson) == RevitCommandCodes.PipeReadTimeout && equalLeftJson?["detail"]?.GetValue<string>() is { } equalDetail && equalDetail.Contains("60000 ms", StringComparison.Ordinal)
            && Pages(equalLeftJson) == 0 && cuts.Count == 0,
            "time left equal to the read timeout is not cut: pipe_read_timeout, not query_all_deadline_exceeded", equalLeftJson?.ToJsonString() ?? "");
        clock = TimeSpan.Zero; sent = 0;
        var roomy = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), () => clock,
            connectThenRead: Legacy(TimeSpan.FromSeconds(30), Paging, Cut));
        var roomyListing = RevitElementQueryPaging.All(wallsText, roomy);
        var undeadlined = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(InProcessPipe, RevitCommandPolicy.None, Paging));
        Check(roomyListing.IsSuccess && roomyListing.Elements.Count == 23 && roomyListing.ToJson() == undeadlined.ToJson(),
            "within its deadline the listing is byte-equal to RevitServiceTransport paging");

        // ---- 2c. P1-31: the reply-at-deadline race ----
        ReplyAtDeadline(wallsText, model, check);

        // ---- 2d/2e. P1-33: the pipe read's cancel-race branch and cutReads false late replies ----
        CancelRace(root, wallsText, check);
        LateCancelOutcomes(wallsText, check);
        UncutLateReply(wallsText, model, check);

        // ---- 2g. P1-36: one page seam (connectThenRead), the legacy seams retired ----
        LegacySeamsRetired(root, check);

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

            // ---- 3c. P1-32: the read of a page whose connect was cut is measured after the connect ----
            ReadAfterConnect(wallsText, Check);

            // ---- 3d. P1-34: the read of a page whose connect was not cut is measured after the connect too ----
            UncutConnectRead(wallsText, model, Check);

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

    const string RaceExceeded = """{"status":"failed","code":"query_all_deadline_exceeded","field":null,"detail":"the --all listing took longer than its 120000 ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor","pages":0,"matched":null,"returned":0,"elements":[]}""";

    // P1-31 (section 2c): each page goes through a transport given the wait of its pipe read (the time left of a cut
    // read, the configured read timeout, or the read of a cut connect), whose reply line completes `duration` after the
    // send on the injected clock. The fake MODELS RevitCommandService.Send's keep-at-timeout rule (a line complete when
    // the read timeout fires is still the reply) rather than exercising it, so Send's cancel-race branch is not covered
    // here: a line due at or before the wait is returned with the clock at its completion; a line due after it is not:
    // the clock stops at the wait and the read times out (RevitPipeReadTimeoutException naming that wait).
    static string RaceExceededAt(long deadlineMs, int pages) => RaceExceeded.Replace("its 120000 ms deadline", "its " + N(deadlineMs) + " ms deadline", StringComparison.Ordinal)
        .Replace("\"pages\":0,", "\"pages\":" + N(pages) + ",", StringComparison.Ordinal);
    const string RaceReadDetail = "no reply within 30000 ms; read-only command, the document is unchanged and the request may be resent (the add-in serves one request at a time, so a resend while it is still busy may get pipe_connect_timeout: retry later)";

    static void ReplyAtDeadline(string wallsText, long[] model, Action<bool, string, string> check)
    {
        const string RacePipe = "bim-agentic-os-2025-3131";
        var single = new[] { 3100L };
        var tick = TimeSpan.FromTicks(1);
        var clock = TimeSpan.Zero;
        var sent = 0;
        var waits = new List<string>();
        string Label(TimeSpan t) => t.Ticks % TimeSpan.TicksPerMillisecond == 0 ? N((long)t.TotalMilliseconds) + " ms" : N(t.Ticks) + " ticks";
        Func<string, string, string?> Read(string kind, TimeSpan wait, TimeSpan duration, long[] rows)
        {
            waits.Add(kind + " " + Label(wait));
            return (_, line) =>
            {
                sent++;
                if (duration > wait) { clock += wait; throw new RevitPipeReadTimeoutException(wait); }
                clock += duration;
                return PageReply(line, rows);
            };
        }
        (string Json, JsonObject? Node) Listing(TimeSpan readTimeout, TimeSpan deadline, TimeSpan duration, long[] rows)
        {
            clock = TimeSpan.Zero; sent = 0; waits.Clear();
            Func<string, string, string?> configured = (pipe, line) => Read("transport", readTimeout, duration, rows)(pipe, line);
            var transport = new Bim.Cli.RevitDeadlineTransport(RacePipe, RevitCommandPolicy.None, configured, readTimeout, deadline, () => clock,
                connectThenRead: Legacy(readTimeout, configured, left => Read("cut", left, duration, rows),
                    (read, connect) => Read("connect " + Label(connect) + " read", read, duration, rows)));
            var json = RevitElementQueryPaging.All(wallsText, transport).ToJson();
            return (json, Node(json));
        }
        string Calls() => " sent " + N(sent) + " clock " + Label(clock) + " waits " + string.Join(",", waits);
        var singleExpected = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(RacePipe, RevitCommandPolicy.None, (_, line) => PageReply(line, single))).ToJson();
        var allExpected = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(RacePipe, RevitCommandPolicy.None, (_, line) => PageReply(line, model))).ToJson();
        var twoMinutes = TimeSpan.FromMinutes(2);
        var halfHour = TimeSpan.FromMinutes(30);

        // A cut read (30 min read timeout, 2 min deadline): the line completes exactly at the deadline, or one tick after.
        var atDeadline = Listing(halfHour, twoMinutes, twoMinutes, single);
        check(atDeadline.Json == singleExpected && atDeadline.Node?["status"]?.GetValue<string>() == "succeeded" && ElementCount(atDeadline.Node) == 1
            && sent == 1 && clock == twoMinutes && waits.SequenceEqual(new[] { "cut 120000 ms" }),
            "P1-31 race: a cut page whose reply line completes exactly when the deadline fires keeps the line (succeeded, byte-equal to RevitServiceTransport paging)",
            atDeadline.Json + Calls());
        var afterDeadline = Listing(halfHour, twoMinutes, twoMinutes + tick, single);
        check(afterDeadline.Json == RaceExceeded && sent == 1 && clock == twoMinutes && waits.SequenceEqual(new[] { "cut 120000 ms" }),
            "P1-31 race: a cut page whose reply line completes one tick after the deadline is query_all_deadline_exceeded (pinned bytes, no partial elements)",
            afterDeadline.Json + Calls());

        // An uncut read (30 s read timeout, 1 h deadline): the line completes exactly when the read timeout fires, or one tick after.
        var thirty = TimeSpan.FromSeconds(30);
        var atRead = Listing(thirty, TimeSpan.FromHours(1), thirty, single);
        check(atRead.Json == singleExpected && sent == 1 && clock == thirty && waits.SequenceEqual(new[] { "transport 30000 ms" }),
            "P1-31 race: an uncut page whose reply line completes exactly when the read timeout fires keeps the line (succeeded)", atRead.Json + Calls());
        var afterRead = Listing(thirty, TimeSpan.FromHours(1), thirty + tick, single);
        check(Code(afterRead.Node) == RevitCommandCodes.PipeReadTimeout && afterRead.Node?["detail"]?.GetValue<string>() == RaceReadDetail
            && Pages(afterRead.Node) == 0 && ElementCount(afterRead.Node) == 0 && sent == 1 && clock == thirty,
            "P1-31 race: an uncut page whose reply line completes one tick after the read timeout is pipe_read_timeout (not the deadline)", afterRead.Json + Calls());

        // A cut connect (3 s left, under the 5 s connect wait): its read of the time left, at and one tick after the deadline.
        var three = TimeSpan.FromSeconds(3);
        var atConnect = Listing(halfHour, three, three, single);
        check(atConnect.Json == singleExpected && sent == 1 && clock == three && waits.SequenceEqual(new[] { "connect 3000 ms read 3000 ms" }),
            "P1-31 race: a cut-connect page whose reply line completes exactly at the deadline keeps the line (succeeded)", atConnect.Json + Calls());
        var afterConnect = Listing(halfHour, three, three + tick, single);
        check(afterConnect.Json == RaceExceededAt(3000, 0) && sent == 1 && clock == three,
            "P1-31 race: a cut-connect page whose reply line completes one tick after the deadline is query_all_deadline_exceeded", afterConnect.Json + Calls());

        // 23 pages of 1 min each under a 30 min read timeout, every page cut to the time left.
        var minute = TimeSpan.FromMinutes(1);
        var cutsDown = Enumerable.Range(1, 23).Reverse().Select(m => "cut " + N(m * 60000L) + " ms");
        var lastAt = Listing(halfHour, TimeSpan.FromMinutes(23), minute, model);
        check(lastAt.Json == allExpected && ElementCount(lastAt.Node) == 23 && sent == 23 && clock == TimeSpan.FromMinutes(23) && waits.SequenceEqual(cutsDown),
            "P1-31 race: a 23-page listing whose last reply line completes exactly at the 23 min deadline is succeeded with all 23 elements (byte-equal)",
            (lastAt.Json.Length > 300 ? lastAt.Json[..300] : lastAt.Json) + Calls());
        var lastAfter = Listing(halfHour, TimeSpan.FromMinutes(23) - tick, minute, model);
        check(lastAfter.Json == RaceExceededAt(23 * 60000L - 1, 22) /* 23 min - 1 tick truncates to 1379999 ms */ && sent == 23 && clock == TimeSpan.FromMinutes(23) - tick
            && waits.Count == 23 && waits[22] == "cut " + Label(minute - tick),
            "P1-31 race: a last reply line completing one tick after the deadline is query_all_deadline_exceeded after 22 pages, no partial elements", lastAfter.Json + Calls());
        var earlyAt = Listing(halfHour, TimeSpan.FromMinutes(22), minute, model);
        check(Code(earlyAt.Node) == Exceeded && Pages(earlyAt.Node) == 22 && ElementCount(earlyAt.Node) == 0 && sent == 22 && clock == TimeSpan.FromMinutes(22)
            && waits.SequenceEqual(cutsDown.Skip(1)),
            "P1-31 race: a non-final reply line completing exactly at the deadline is kept, and the next page (no time left) is refused without being sent",
            earlyAt.Json + Calls());
    }

    // ---- 2d. P1-33: the pipe read's cancel-race branch (RevitCommandService.ReadReplyLine, the read seam of Send) ----
    // A gated read: its line completes only when the test says so; like a real pipe read it ends cancelled when its token
    // is cancelled first (or faults, or ignores the cancel). No timing race: the timed wait is 50 ms on a task that cannot
    // complete during it, and the "after the wait gave up, before the cancel" point is the seam's afterWaitGivesUp hook.
    sealed class GatedRead
    {
        public readonly TaskCompletionSource<string?> Line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly List<string> events = new();
        public bool HonoursCancel = true;
        public Exception? FaultOnCancel;
        public Task<string?> Start(CancellationToken token)
        {
            Event("read");
            token.Register(() =>
            {
                Event("cancel");
                if (FaultOnCancel is { } fault) Line.TrySetException(fault);
                else if (HonoursCancel) Line.TrySetCanceled(token);
            });
            return Line.Task;
        }
        public void Event(string name) { lock (events) events.Add(name); }
        public string Events { get { lock (events) return string.Join(",", events); } }
    }

    static void CancelRace(string root, string wallsText, Action<bool, string, string> check)
    {
        const string RacePipe = "bim-agentic-os-2025-3333";
        var wait = TimeSpan.FromMilliseconds(50);
        static Exception? Thrown(Action call) { try { call(); return null; } catch (Exception error) { return error; } }
        var single = new[] { 3100L };

        // Kept: a line that completes after the timed wait gave up but before the read is cancelled is this request's reply.
        var kept = new GatedRead();
        string? keptLine = null;
        var keptError = Thrown(() => keptLine = RevitCommandService.ReadReplyLine(kept.Start, wait, () => { kept.Event("gave up"); kept.Line.TrySetResult("late-line"); }));
        check(keptError is null && keptLine == "late-line" && kept.Events == "read,gave up,cancel",
            "P1-33 cancel race: a reply line completing after the timed wait gives up but before the read is cancelled is kept (returned as the reply)",
            (keptError?.GetType().Name ?? "no error") + " line " + (keptLine ?? "null") + " events " + kept.Events);

        // Not completed by the cancel: the read ends cancelled and the wait is the read timeout naming that wait.
        var cancelled = new GatedRead();
        var cancelledError = Thrown(() => RevitCommandService.ReadReplyLine(cancelled.Start, wait, () => cancelled.Event("gave up")));
        check(cancelledError is RevitPipeReadTimeoutException { Timeout: var t } && t == wait && cancelled.Line.Task.IsCanceled && cancelled.Events == "read,gave up,cancel",
            "P1-33 cancel race: with no line by the cancel the read is cancelled and RevitPipeReadTimeoutException names the 50 ms wait",
            (cancelledError?.GetType().Name ?? "no error") + " events " + cancelled.Events);

        // A line that completes only after the cancel (a read that ignores it) is dropped: still the read timeout.
        var ignoring = new GatedRead { HonoursCancel = false };
        var ignoringError = Thrown(() => RevitCommandService.ReadReplyLine(ignoring.Start, wait, () => ignoring.Event("gave up")));
        ignoring.Line.TrySetResult("too-late-line");
        check(ignoringError is RevitPipeReadTimeoutException && ignoring.Events == "read,gave up,cancel",
            "P1-33 cancel race: a line completing only after the cancel is dropped (RevitPipeReadTimeoutException)", (ignoringError?.GetType().Name ?? "no error") + " events " + ignoring.Events);

        // A read that ends cancelled within the wait (not by the seam's token) is taken as not done: hook, cancel, read timeout.
        var precancelled = new GatedRead();
        precancelled.Line.TrySetCanceled();
        var precancelledError = Thrown(() => RevitCommandService.ReadReplyLine(precancelled.Start, wait, () => precancelled.Event("gave up")));
        check(precancelledError is RevitPipeReadTimeoutException { Timeout: var pt } && pt == wait && precancelled.Events == "read,gave up,cancel",
            "P1-33 cancel race: a read already cancelled (not by the seam) is not done: RevitPipeReadTimeoutException names the 50 ms wait",
            (precancelledError?.GetType().Name ?? "no error") + " events " + precancelled.Events);

        // A read that faults on the cancel (a closed pipe) is still the read timeout; the late fault is dropped.
        var faulting = new GatedRead { FaultOnCancel = new IOException("late fault") };
        var faultingError = Thrown(() => RevitCommandService.ReadReplyLine(faulting.Start, wait, () => faulting.Event("gave up")));
        check(faultingError is RevitPipeReadTimeoutException && faulting.Line.Task.IsFaulted,
            "P1-33 cancel race: a read faulting on the cancel is still RevitPipeReadTimeoutException, not the late IOException", faultingError?.GetType().Name ?? "no error");

        // Within the wait: the line is returned and the hook never runs; a read fault within it passes through as is.
        var prompt = new GatedRead();
        prompt.Line.TrySetResult("prompt-line");
        var promptLine = RevitCommandService.ReadReplyLine(prompt.Start, wait, () => prompt.Event("gave up"));
        var closed = new GatedRead();
        closed.Line.TrySetResult(null);
        var closedLine = RevitCommandService.ReadReplyLine(closed.Start, wait, () => closed.Event("gave up"));
        var broken = new GatedRead();
        broken.Line.TrySetException(new IOException("broken"));
        var brokenError = Thrown(() => RevitCommandService.ReadReplyLine(broken.Start, wait, () => broken.Event("gave up")));
        check(promptLine == "prompt-line" && prompt.Events == "read" && closedLine is null && closed.Events == "read"
            && brokenError is IOException { Message: "broken" } and not RevitPipeReadTimeoutException && broken.Events == "read",
            "P1-33 seam: a line (or end of pipe, null) within the wait is returned without the hook; a read fault within it passes through",
            "prompt " + prompt.Events + " closed " + closed.Events + " broken " + (brokenError?.GetType().Name ?? "none") + " " + broken.Events);
        check(Thrown(() => RevitCommandService.ReadReplyLine(new GatedRead().Start, TimeSpan.Zero)) is ArgumentOutOfRangeException
            && Thrown(() => RevitCommandService.ReadReplyLine(new GatedRead().Start, RevitCommandService.MaxReadTimeout + TimeSpan.FromTicks(1))) is ArgumentOutOfRangeException
            && Thrown(() => RevitCommandService.ReadReplyLine(null!, wait)) is ArgumentNullException,
            "P1-33 seam: ReadReplyLine refuses a zero or over-24 h read timeout and a null read", "");

        // Through RevitCommandService.Run: the kept line is verified and returned byte-equal to a prompt reply; the
        // cancelled one is pipe_read_timeout with the read-only detail naming 50 ms.
        var expected = RevitCommandService.Run(RacePipe, wallsText, RevitCommandPolicy.None, (_, line) => PageReply(line, single)).Line;
        var runKept = RevitCommandService.Run(RacePipe, wallsText, RevitCommandPolicy.None, (_, line) =>
        {
            var gate = new GatedRead();
            return RevitCommandService.ReadReplyLine(gate.Start, wait, () => gate.Line.TrySetResult(PageReply(line, single)));
        });
        check(!runKept.IsError && runKept.Forwarded && runKept.Line == expected,
            "P1-33 cancel race through RevitCommandService.Run: the kept late line is the verified reply, byte-equal to a prompt one", runKept.Line);
        var runTimeout = RevitCommandService.Run(RacePipe, wallsText, RevitCommandPolicy.None, (_, _) => RevitCommandService.ReadReplyLine(new GatedRead().Start, wait));
        const string RunTimeout = """{"status":"failed","code":"pipe_read_timeout","field":"pipe_name","detail":"no reply within 50 ms; read-only command, the document is unchanged and the request may be resent (the add-in serves one request at a time, so a resend while it is still busy may get pipe_connect_timeout: retry later)","created":[],"modified":[],"deleted":[],"readback":null,"receipt":null,"warnings":[]}""";
        check(runTimeout.IsError && runTimeout.Forwarded && runTimeout.Line == RunTimeout,
            "P1-33 cancel race through RevitCommandService.Run: no line by the cancel is pipe_read_timeout (pinned bytes)", runTimeout.Line);

        // Through the --all deadline (50 ms deadline, 30 min read timeout, frozen clock: the page's connect and read are
        // cut to 50 ms): the kept late line ends the listing succeeded; without it the page is query_all_deadline_exceeded.
        var clock = TimeSpan.Zero;
        var singleExpected = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(RacePipe, RevitCommandPolicy.None, (_, line) => PageReply(line, single))).ToJson();
        string Deadlined(bool lineBeforeCancel)
        {
            var reads = new List<string>();
            var transport = new Bim.Cli.RevitDeadlineTransport(RacePipe, RevitCommandPolicy.None, (_, _) => throw new InvalidOperationException("uncut transport used"),
                TimeSpan.FromMinutes(30), wait, () => clock,
                connectThenRead: (left, readAfter) => (_, line) =>
                {
                    var read = readAfter();
                    reads.Add(((long)left.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "/" + ((long)read.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                    var gate = new GatedRead();
                    return RevitCommandService.ReadReplyLine(gate.Start, read, () => { if (lineBeforeCancel) gate.Line.TrySetResult(PageReply(line, single)); });
                });
            return RevitElementQueryPaging.All(wallsText, transport).ToJson() + "|" + string.Join(",", reads);
        }
        var deadlineKept = Deadlined(true);
        check(deadlineKept == singleExpected + "|50/50", "P1-33 cancel race under the --all deadline: the late line kept at the cut read ends the listing succeeded (byte-equal)", deadlineKept);
        var deadlineLost = Deadlined(false);
        check(deadlineLost == RaceExceededAt(50, 0) + "|50/50", "P1-33 cancel race under the --all deadline: no line by the cancel is query_all_deadline_exceeded (pinned bytes)", deadlineLost);

        // Static: the real pipe read (SendMeasured) goes through ReadReplyLine with no hook, its only timed wait.
        var source = File.ReadAllText(Path.Combine(root, "core", "Bim.Core", "RevitCommandsService.cs")).ReplaceLineEndings("\n");
        var sendStart = source.IndexOf("static string? SendMeasured(", StringComparison.Ordinal);
        var sendEnd = sendStart < 0 ? -1 : source.IndexOf("\n    }\n", sendStart, StringComparison.Ordinal);
        var sendBody = sendEnd < 0 ? "" : source[sendStart..sendEnd];
        check(sendBody.Contains("return ReadReplyLine(token => reader.ReadLineAsync(token).AsTask(), readTimeout);", StringComparison.Ordinal)
            && !sendBody.Contains(".Wait(", StringComparison.Ordinal) && !sendBody.Contains("Cancel()", StringComparison.Ordinal),
            "P1-33 static: RevitCommandService.SendMeasured reads the reply line only through ReadReplyLine, without the test hook", sendBody.Length > 400 ? sendBody[^400..] : sendBody);
        var coreUses = Directory.GetFiles(Path.Combine(root, "core"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file).ReplaceLineEndings("\n"), @"ReadReplyLine\(")
                .Select(hit => Path.GetRelativePath(root, file).Replace('\\', '/') + "@" + N(hit.Index))).ToArray();
        var definition = source.IndexOf("internal static string? ReadReplyLine(", StringComparison.Ordinal);
        var inSend = sendStart < 0 ? -1 : source.IndexOf("ReadReplyLine(", sendStart, StringComparison.Ordinal);
        check(coreUses.Length == 2 && definition >= 0 && inSend > sendStart && inSend < sendEnd
            && coreUses.All(use => use.StartsWith("core/Bim.Core/RevitCommandsService.cs@", StringComparison.Ordinal))
            && coreUses.Contains("core/Bim.Core/RevitCommandsService.cs@" + N(definition + "internal static string? ".Length))
            && coreUses.Contains("core/Bim.Core/RevitCommandsService.cs@" + N(inSend)),
            "P1-33 static: under core/ ReadReplyLine( appears only at its internal definition and in SendMeasured", string.Join(", ", coreUses));
    }

    // ---- 2f. P1-35: ReadReplyLine's late outcomes at and after the cancel ----
    // A read that faults on the cancel or after ReadReplyLine has already thrown its read timeout must not leave an
    // unobserved faulted task: once the task is collected (bounded GC loop, no sleep over 2 s) the process-global
    // TaskScheduler.UnobservedTaskException, filtered to this section's "P1-35 " messages, sees no such fault; a control
    // task faulted and never observed proves the detector sees one. A line completed from inside a cancel registration
    // callback on the read's token (it runs synchronously within the seam's Cancel()) is KEPT when it completes before
    // the read's own cancel callback (or the read ignores the cancel), consistent with ReadReplyLine's after-the-wait
    // rule (a read completed successfully by the end of Cancel() is the reply); otherwise it is pipe_read_timeout.
    const string LateFaultPrefix = "P1-35 ";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference AbandonUnobservedFault(string message)
    {
        var line = new TaskCompletionSource<string?>();
        line.SetException(new IOException(message));
        return new WeakReference(line.Task);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference AbandonFaultingRead(string message, bool faultOnCancel, TimeSpan wait, List<string> outcome)
    {
        var gate = new GatedRead { HonoursCancel = false, FaultOnCancel = faultOnCancel ? new IOException(message) : null };
        try { RevitCommandService.ReadReplyLine(gate.Start, wait, () => gate.Event("gave up")); outcome.Add("no error"); }
        catch (Exception error) { outcome.Add(error.GetType().Name); }
        // After the cancel and after ReadReplyLine returned: the read faults late (a pipe closed under it).
        if (!faultOnCancel) gate.Line.TrySetException(new IOException(message));
        outcome.Add(gate.Events + (gate.Line.Task.IsFaulted ? ",faulted" : ""));
        return new WeakReference(gate.Line.Task);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static bool Collected(WeakReference task)
    {
        var watch = Stopwatch.StartNew();
        while (task.IsAlive && watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (task.IsAlive) Thread.Sleep(10);
        }
        GC.WaitForPendingFinalizers();
        return !task.IsAlive;
    }

    static void LateCancelOutcomes(string wallsText, Action<bool, string, string> check)
    {
        const string RacePipe = "bim-agentic-os-2025-3333";
        var wait = TimeSpan.FromMilliseconds(50);
        var single = new[] { 3100L };
        static Exception? Thrown(Action call) { try { call(); return null; } catch (Exception error) { return error; } }

        var seen = new List<string>();
        EventHandler<UnobservedTaskExceptionEventArgs> detector = (_, e) =>
        {
            var mine = e.Exception.InnerExceptions.Select(inner => inner.Message).Where(message => message.StartsWith(LateFaultPrefix, StringComparison.Ordinal)).ToArray();
            if (mine.Length == 0) return;
            lock (seen) seen.AddRange(mine);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += detector;
        try
        {
            const string Control = LateFaultPrefix + "control fault never observed";
            const string OnCancel = LateFaultPrefix + "read faulting on the cancel";
            const string AfterCancel = LateFaultPrefix + "read faulting after the cancel";
            var controlCollected = Collected(AbandonUnobservedFault(Control));
            var onCancelOutcome = new List<string>();
            var onCancelCollected = Collected(AbandonFaultingRead(OnCancel, true, wait, onCancelOutcome));
            var afterCancelOutcome = new List<string>();
            var afterCancelCollected = Collected(AbandonFaultingRead(AfterCancel, false, wait, afterCancelOutcome));
            string[] faults;
            lock (seen) faults = seen.ToArray();
            var report = "seen [" + string.Join(" | ", faults) + "] collected " + controlCollected + "/" + onCancelCollected + "/" + afterCancelCollected
                + " on " + string.Join(" ", onCancelOutcome) + " after " + string.Join(" ", afterCancelOutcome);
            check(controlCollected && faults.Contains(Control),
                "P1-35 detector: a faulted task never observed is reported by TaskScheduler.UnobservedTaskException once collected", report);
            check(onCancelCollected && !faults.Contains(OnCancel)
                && onCancelOutcome.SequenceEqual(new[] { nameof(RevitPipeReadTimeoutException), "read,gave up,cancel,faulted" }),
                "P1-35 late fault: a read faulting on the cancel is RevitPipeReadTimeoutException and its fault is observed (no UnobservedTaskException)", report);
            check(afterCancelCollected && !faults.Contains(AfterCancel)
                && afterCancelOutcome.SequenceEqual(new[] { nameof(RevitPipeReadTimeoutException), "read,gave up,cancel,faulted" }),
                "P1-35 late fault: a read faulting after the cancel (ReadReplyLine already returned) is observed (no UnobservedTaskException)", report);
        }
        finally { TaskScheduler.UnobservedTaskException -= detector; }

        // A line completed from inside a cancel registration callback on the read's token is kept when it completes
        // before the read's own cancel: the token runs its callbacks in reverse registration order (LIFO, the .NET 8
        // CancellationTokenSource), so a line callback registered after the read's runs first (kept), one registered
        // before it runs after a read that honours the cancel has already ended cancelled (pipe_read_timeout). A read
        // that ignores the cancel keeps the line whatever the order. The callback never throws (the line is computed
        // before the read; a failure inside it is recorded as an event, so it fails the check instead of escaping Cancel()).
        Func<CancellationToken, Task<string?>> CallbackLine(GatedRead gate, string line, bool registerAfterRead) => token =>
        {
            void Complete()
            {
                try { gate.Event("callback line"); gate.Line.TrySetResult(line); }
                catch (Exception error) { gate.Event("callback error " + error.GetType().Name); }
            }
            if (!registerAfterRead) token.Register(Complete);
            var read = gate.Start(token);
            if (registerAfterRead) token.Register(Complete);
            return read;
        };
        (Exception? Error, string? Line, string Events, bool Cancelled) InCancel(bool honoursCancel, bool registerAfterRead)
        {
            var gate = new GatedRead { HonoursCancel = honoursCancel };
            string? line = null;
            var error = Thrown(() => line = RevitCommandService.ReadReplyLine(CallbackLine(gate, "cancel-callback-line", registerAfterRead), wait, () => gate.Event("gave up")));
            return (error, line, gate.Events, gate.Line.Task.IsCanceled);
        }
        static string Describe((Exception? Error, string? Line, string Events, bool Cancelled) outcome)
            => (outcome.Error?.GetType().Name ?? "no error") + " line " + (outcome.Line ?? "null") + " events " + outcome.Events + (outcome.Cancelled ? " cancelled" : "");

        var ignoring = InCancel(false, true);
        var ignoringEvents = ignoring.Events.Split(',');
        check(ignoring.Error is null && ignoring.Line == "cancel-callback-line" && ignoringEvents.Length == 4 && ignoringEvents[0] == "read" && ignoringEvents[1] == "gave up"
            && ignoringEvents.Skip(2).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(new[] { "callback line", "cancel" }),
            "P1-35 cancel callback: a reply line completed from inside a cancel registration callback on a read ignoring the cancel is kept (returned as the reply)",
            Describe(ignoring));
        var lineFirst = InCancel(true, true);
        check(lineFirst.Error is null && lineFirst.Line == "cancel-callback-line" && lineFirst.Events == "read,gave up,callback line,cancel" && !lineFirst.Cancelled,
            "P1-35 cancel callback: a line callback registered after the read's runs before its cancel (LIFO) and the line is kept", Describe(lineFirst));
        var cancelFirst = InCancel(true, false);
        check(cancelFirst.Error is RevitPipeReadTimeoutException { Timeout: var cancelFirstWait } && cancelFirstWait == wait && cancelFirst.Line is null
            && cancelFirst.Events == "read,gave up,cancel,callback line" && cancelFirst.Cancelled,
            "P1-35 cancel callback: a line callback registered before the read's runs after the read ended cancelled: RevitPipeReadTimeoutException (the line is dropped)",
            Describe(cancelFirst));

        var expected = RevitCommandService.Run(RacePipe, wallsText, RevitCommandPolicy.None, (_, line) => PageReply(line, single)).Line;
        var runInCancel = RevitCommandService.Run(RacePipe, wallsText, RevitCommandPolicy.None, (_, line) =>
        {
            var gate = new GatedRead { HonoursCancel = false };
            var reply = PageReply(line, single);
            return RevitCommandService.ReadReplyLine(CallbackLine(gate, reply, true), wait);
        });
        check(!runInCancel.IsError && runInCancel.Forwarded && runInCancel.Line == expected,
            "P1-35 cancel callback through RevitCommandService.Run: the line completed in the cancel callback is the verified reply, byte-equal to a prompt one", runInCancel.Line);
    }

    // ---- 2e. P1-33: cutReads false (the MCP server on an injected transport) never cuts the in-flight page ----
    // The deadline is checked only before a page is sent: a last page whose reply comes back after the deadline is kept
    // (the listing succeeds, byte-equal to RevitServiceTransport paging); a non-final late page's reply is kept and the
    // next page is refused query_all_deadline_exceeded unsent. Neither the read nor the connect is cut.
    static void UncutLateReply(string wallsText, long[] model, Action<bool, string, string> check)
    {
        const string RacePipe = "bim-agentic-os-2025-3334";
        var single = new[] { 3100L };
        var clock = TimeSpan.Zero;
        var sent = 0;
        var cuts = 0;
        string Listing(long[] rows)
        {
            clock = TimeSpan.Zero; sent = 0; cuts = 0;
            var transport = new Bim.Cli.RevitDeadlineTransport(RacePipe, RevitCommandPolicy.None,
                (_, line) => { sent++; clock += TimeSpan.FromSeconds(4); return PageReply(line, rows); },
                TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(3), () => clock,
                listing: Bim.Cli.RevitDeadlineTransport.McpListing, cutReads: false,
                connectThenRead: (_, _) => { cuts++; return (_, _) => throw new InvalidOperationException("cut connect"); });
            return RevitElementQueryPaging.All(wallsText, transport).ToJson();
        }
        var singleExpected = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(RacePipe, RevitCommandPolicy.None, (_, line) => PageReply(line, single))).ToJson();
        var last = Listing(single);
        check(last == singleExpected && sent == 1 && cuts == 0 && clock == TimeSpan.FromSeconds(4),
            "P1-33 cutReads false: a last page whose reply returns 1 s after the 3 s deadline is kept (succeeded, byte-equal, nothing cut)",
            last + " sent " + N(sent) + " cuts " + N(cuts));
        var early = Listing(model);
        var earlyExpected = RaceExceededAt(3000, 1).Replace("the --all listing", "the all:true listing", StringComparison.Ordinal);
        check(early == earlyExpected && sent == 1 && cuts == 0,
            "P1-33 cutReads false: a non-final page replying after the deadline is kept, the next page is query_all_deadline_exceeded unsent (pinned bytes)",
            early + " sent " + N(sent) + " cuts " + N(cuts));
    }

    // ---- 2g. P1-36: RevitDeadlineTransport has one page seam ----
    // Static, on the comment-stripped source of interfaces/cli/RevitDeadlineTransport.cs: no cutTransport or
    // connectTransport seam and no measured flag remain, and Send makes every real-pipe page through one
    // connectThenRead( call. McpServer's deadline comment says the read is measured after the connect on every
    // real-pipe page. Mutation self-test: the same checker flags a source with any legacy seam put back, and ignores a
    // seam named only in a comment.
    static string StripComments(string source)
    {
        var text = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/') { while (i < source.Length && source[i] != '\n') i++; continue; }
            if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                text.Append(' ');
                continue;
            }
            if (c == '"' || c == '\'')
            {
                var verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                text.Append(c);
                i++;
                while (i < source.Length)
                {
                    var d = source[i];
                    text.Append(d);
                    i++;
                    if (verbatim && d == '"' && i < source.Length && source[i] == '"') { text.Append('"'); i++; continue; }
                    if (!verbatim && d == '\\' && i < source.Length) { text.Append(source[i]); i++; continue; }
                    if (d == c || (!verbatim && d == '\n')) break;
                }
                continue;
            }
            text.Append(c);
            i++;
        }
        return text.ToString();
    }

    static string[] SeamProblems(string source)
    {
        var code = StripComments(source.ReplaceLineEndings("\n"));
        var problems = new List<string>();
        foreach (var seam in new[] { "cutTransport", "connectTransport", "measured", "PipeTransportWithReadTimeout" })
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\b" + seam + @"\b")) problems.Add("legacy seam " + seam);
        var sendStart = code.IndexOf("public string? Send(", StringComparison.Ordinal);
        var sendEnd = sendStart < 0 ? -1 : code.IndexOf("\n    }\n", sendStart, StringComparison.Ordinal);
        var send = sendEnd < 0 ? "" : code[sendStart..sendEnd];
        var calls = System.Text.RegularExpressions.Regex.Matches(send, @"\bconnectThenRead\(").Count;
        if (calls != 1) problems.Add("Send makes its page through connectThenRead( " + N(calls) + " times, not once");
        return problems.ToArray();
    }

    static void LegacySeamsRetired(string root, Action<bool, string, string> check)
    {
        var source = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "RevitDeadlineTransport.cs")).ReplaceLineEndings("\n");
        var problems = SeamProblems(source);
        check(problems.Length == 0, "P1-36 static: RevitDeadlineTransport has no cutTransport/connectTransport seam or measured flag, and Send makes every page through one connectThenRead(",
            string.Join("; ", problems));

        // Mutation self-test on a minimal transport: clean, comment-only mentions, and each legacy seam put back.
        const string Clean = "class T\n{\n    // cutTransport, connectTransport and measured are gone /* measured */\n    readonly string note = \"// not a comment\";\n"
            + "    public string? Send(string line)\n    {\n        /* connectTransport( */ var pipe = connectThenRead(wait, ReadAfter);\n        return pipe(line);\n    }\n}\n";
        check(SeamProblems(Clean).Length == 0, "P1-36 mutation self-test: one connectThenRead( call and seams named only in comments pass", string.Join("; ", SeamProblems(Clean)));
        var mutants = new (string Label, string Source)[]
        {
            ("cutTransport field", Clean.Replace("class T\n{\n", "class T\n{\n    readonly Func<TimeSpan, Func<string, string, string?>> cutTransport;\n", StringComparison.Ordinal)),
            ("connectTransport parameter", Clean.Replace("Send(string line)", "Send(string line, object? connectTransport = null)", StringComparison.Ordinal)),
            ("measured flag", Clean.Replace("var pipe = ", "var pipe = measured ? transport : ", StringComparison.Ordinal)),
            ("legacy cut pipe", Clean.Replace("return pipe(line);", "return RevitCommandService.PipeTransportWithReadTimeout(wait)(line);", StringComparison.Ordinal)),
            ("second page path", Clean.Replace("return pipe(line);", "return cut ? connectThenRead(left, ReadAfter)(line) : pipe(line);", StringComparison.Ordinal)),
            ("no page seam", Clean.Replace("connectThenRead(wait, ReadAfter)", "transport", StringComparison.Ordinal)),
        };
        foreach (var (label, mutant) in mutants)
            check(mutant != Clean && SeamProblems(mutant).Length > 0, "P1-36 mutation self-test: the seam check flags a " + label, string.Join("; ", SeamProblems(mutant)));

        // McpServer's deadline comment (before RevitCallAll's new RevitDeadlineTransport(), folded over comment lines.
        var mcp = File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")).ReplaceLineEndings("\n");
        var allStart = mcp.IndexOf("(string Text, bool IsError) RevitCallAll(", StringComparison.Ordinal);
        var built = allStart < 0 ? -1 : mcp.IndexOf("new RevitDeadlineTransport(", allStart, StringComparison.Ordinal);
        var comment = built < 0 ? "" : string.Join(" ", mcp[allStart..built].Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("//", StringComparison.Ordinal))
            .Select(line => line.TrimStart('/').Trim()));
        check(comment.Contains("the read is measured after the connect on every real-pipe page", StringComparison.Ordinal),
            "P1-36 static: McpServer.RevitCallAll's deadline comment states the read is measured after the connect on every real-pipe page", comment);
    }

    const string AbsentExceededCli ="""{"status":"failed","code":"query_all_deadline_exceeded","field":null,"detail":"the --all listing took longer than its 1000 ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor","pages":0,"matched":null,"returned":0,"elements":[]}""";
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
            Func<string, string, string?> configured = (pipe, line) => { calls.Add("transport"); throw error; };
            var seam = new Bim.Cli.RevitDeadlineTransport(SeamPipe, RevitCommandPolicy.None, configured, readTimeout, deadline, () => TimeSpan.Zero, cutReads: cutReads,
                connectThenRead: Legacy(readTimeout, configured, left => { calls.Add("cut " + Label(left)); return Throws(error); },
                    (read, connect) => { calls.Add("connect " + Label(read) + "/" + Label(connect)); return Throws(error); }));
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
            && !builtArgs.Contains("ConnectTimeout", StringComparison.Ordinal) && !builtArgs.Contains("cutTransport", StringComparison.Ordinal)
            && !builtArgs.Contains("connectThenRead", StringComparison.Ordinal),
            "P1-30/P1-32 static: McpServer.RevitCallAll builds RevitDeadlineTransport with cutReads: realPipe and the default (shared) connect bound and read after connect (no connectTransport, connectThenRead or cutTransport override)",
            builtArgs.ReplaceLineEndings(" "));

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

    // P1-32 (section 3c): a page whose connect was cut (time left below the 5 s connect wait) is read with the time left
    // after the connect completes, not the time left measured before it. Seam: an injected connectThenRead whose connect
    // advances the injected clock, then asks the read wait (as RevitCommandService.PipeTransportWithReadAfterConnect).
    // Real pipe: an add-in that starts listening at 800 ms of a 1 s deadline accepts the page and never replies; the run
    // ends query_all_deadline_exceeded (pinned bytes) within the deadline + 300 ms (before P1-32 the read waited the 1 s
    // measured before the connect: about 1.8 s). Without a deadline the read timeout still starts after the connect.
    static void ReadAfterConnect(string wallsText, Action<bool, string, string> check)
    {
        var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(wallsText));
        const string SeamPipe = "bim-agentic-os-2025-3232";
        var single = new[] { 3100L };
        var clock = TimeSpan.Zero;
        var sent = 0;
        var calls = new List<string>();
        string Label(TimeSpan t) => N((long)t.TotalMilliseconds);
        // The connect takes `connect` on the clock (over its wait it times out at the wait), then the read wait is asked;
        // a zero wait sends nothing; the reply line completes `reply` after the send (null: never), kept at the wait.
        Func<string, string, string?> Fake(TimeSpan wait, Func<TimeSpan> readAfter, TimeSpan connect, TimeSpan? reply) => (_, line) =>
        {
            if (connect > wait) { clock += wait; calls.Add("connect " + Label(wait) + " timed out"); throw new TimeoutException(); }
            clock += connect;
            var read = readAfter();
            calls.Add("connect " + Label(wait) + " took " + Label(connect) + " read " + Label(read));
            if (read <= TimeSpan.Zero) throw new RevitPipeReadTimeoutException(TimeSpan.Zero);
            sent++;
            if (reply is not { } due || due > read) { clock += read; throw new RevitPipeReadTimeoutException(read); }
            clock += due;
            return PageReply(line, single);
        };
        string Listing(TimeSpan readWait, TimeSpan limit, TimeSpan connect, TimeSpan? reply)
        {
            clock = TimeSpan.Zero; sent = 0; calls.Clear();
            var seam = new Bim.Cli.RevitDeadlineTransport(SeamPipe, RevitCommandPolicy.None, (_, _) => throw new InvalidOperationException("uncut transport used"), readWait, limit,
                () => clock, connectThenRead: (wait, readAfter) => Fake(wait, readAfter, connect, reply));
            return RevitElementQueryPaging.All(wallsText, seam).ToJson();
        }
        string Calls() => " sent " + N(sent) + " clock " + Label(clock) + " calls " + string.Join(",", calls);
        var singleExpected = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(SeamPipe, RevitCommandPolicy.None, (_, line) => PageReply(line, single))).ToJson();
        var second = TimeSpan.FromSeconds(1);
        var halfHour = TimeSpan.FromMinutes(30);
        static TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);

        var lateAccept = Listing(halfHour, second, Ms(800), null);
        check(lateAccept == RaceExceededAt(1000, 0) && sent == 1 && clock == second && calls.SequenceEqual(new[] { "connect 1000 took 800 read 200" }),
            "P1-32 seam: a connect taking 800 ms of a 1 s deadline leaves a 200 ms read; a page never answered ends query_all_deadline_exceeded at the deadline", lateAccept + Calls());
        var atDeadline = Listing(halfHour, second, second, null);
        check(atDeadline == RaceExceededAt(1000, 0) && sent == 0 && clock == second && calls.SequenceEqual(new[] { "connect 1000 took 1000 read 0" }),
            "P1-32 seam: a connect completing exactly at the deadline leaves no read: the page is not sent and is query_all_deadline_exceeded", atDeadline + Calls());
        // Under 1 ms left once connected (here 1 tick) counts as no time left: not sent, query_all_deadline_exceeded.
        var tick = TimeSpan.FromTicks(1);
        var subMillisecond = Listing(halfHour, second, second - tick, null);
        check(subMillisecond == RaceExceededAt(1000, 0) && sent == 0 && clock == second - tick && calls.SequenceEqual(new[] { "connect 1000 took 999 read 0" }),
            "P1-32 seam: a connect leaving under 1 ms (1 tick) of the deadline counts as no time left: the page is not sent and is query_all_deadline_exceeded",
            subMillisecond + Calls());
        var timedOut = Listing(halfHour, second, Ms(1200), null);
        check(timedOut == RaceExceededAt(1000, 0) && sent == 0 && clock == second && calls.SequenceEqual(new[] { "connect 1000 timed out" }),
            "P1-32 seam: a connect over the time left still times out at the deadline (query_all_deadline_exceeded)", timedOut + Calls());
        var replied = Listing(halfHour, second, Ms(800), Ms(200));
        check(replied == singleExpected && sent == 1 && clock == second,
            "P1-32 seam: after an 800 ms connect a reply completing exactly at the 1 s deadline is kept (succeeded, byte-equal)", replied + Calls());
        var repliedLate = Listing(halfHour, second, Ms(800), Ms(200) + tick);
        check(repliedLate == RaceExceededAt(1000, 0) && sent == 1 && clock == second,
            "P1-32 seam: after an 800 ms connect a reply one tick after the deadline is query_all_deadline_exceeded", repliedLate + Calls());
        // A 2 s read timeout under a 4 s deadline: uncut before the connect; cut by a 3 s connect, not by a 1 s one.
        var cutByConnect = Listing(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3), null);
        check(cutByConnect == RaceExceededAt(4000, 0) && sent == 1 && clock == TimeSpan.FromSeconds(4) && calls.SequenceEqual(new[] { "connect 4000 took 3000 read 1000" }),
            "P1-32 seam: a 3 s connect under a 4 s deadline cuts the 2 s read to the 1 s left; never answered, it is query_all_deadline_exceeded at the deadline (was pipe_read_timeout at 5 s)",
            cutByConnect + Calls());
        var keptRead = Listing(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), second, null);
        var keptReadNode = Node(keptRead);
        check(Code(keptReadNode) == RevitCommandCodes.PipeReadTimeout && keptReadNode?["detail"]?.GetValue<string>() == RaceReadDetail.Replace("30000 ms", "2000 ms", StringComparison.Ordinal)
            && Pages(keptReadNode) == 0 && sent == 1 && clock == TimeSpan.FromSeconds(3) && calls.SequenceEqual(new[] { "connect 4000 took 1000 read 2000" }),
            "P1-32 seam: a 1 s connect under a 4 s deadline keeps the 2 s read (3 s left), and its pipe_read_timeout passes through", keptRead + Calls());

        // Core: PipeTransportWithReadAfterConnect checks its arguments when made; a zero read wait after the connect
        // writes nothing (the listening add-in reads end of stream) and throws RevitPipeReadTimeoutException(0).
        static Exception? Thrown(Action call) { try { call(); return null; } catch (Exception error) { return error; } }
        check(Thrown(() => RevitCommandService.PipeTransportWithReadAfterConnect(TimeSpan.Zero, () => TimeSpan.FromSeconds(1))) is ArgumentOutOfRangeException
            && Thrown(() => RevitCommandService.PipeTransportWithReadAfterConnect(TimeSpan.FromMilliseconds(5001), () => TimeSpan.FromSeconds(1))) is ArgumentOutOfRangeException
            && Thrown(() => RevitCommandService.PipeTransportWithReadAfterConnect(TimeSpan.FromSeconds(5), null!)) is ArgumentNullException,
            "P1-32 PipeTransportWithReadAfterConnect refuses a zero or over-5 s connect timeout and a null read wait when made", "");
        var zeroPipe = AbsentPipe();
        using (var listening = new LateAddIn(zeroPipe, Stopwatch.StartNew(), TimeSpan.Zero))
        {
            var zero = Thrown(() => RevitCommandService.PipeTransportWithReadAfterConnect(TimeSpan.FromSeconds(5), () => TimeSpan.Zero)(zeroPipe, wire));
            _ = listening.WaitServed();
            check(zero is RevitPipeReadTimeoutException { Timeout.Ticks: 0 } && listening.ConnectedAt is not null && listening.Line is null,
                "P1-32 real pipe: a read wait of zero once connected sends nothing and throws RevitPipeReadTimeoutException(0)", (zero?.GetType().Name ?? "<none>") + "; " + listening.Describe());
        }
        // No deadline: the read timeout still starts once connected (a 300 ms connect, then the full 500 ms read).
        var plainPipe = AbsentPipe();
        var plainWatch = Stopwatch.StartNew();
        using (var slowListen = new LateAddIn(plainPipe, plainWatch, Ms(300)))
        {
            var plain = Thrown(() => RevitCommandService.PipeTransportWithReadTimeout(Ms(500))(plainPipe, wire));
            var took = plainWatch.Elapsed;
            var plainServed = slowListen.WaitServed();
            // The read timeout itself is the exception's (500 ms) and the accept is after the 300 ms listen; the wall-clock
            // floor is loose (750 ms) so a coarse Windows timer tick cannot fail it.
            check(plain is RevitPipeReadTimeoutException { Timeout.TotalMilliseconds: 500 } && slowListen.ConnectedAt is { } plainConnected && plainConnected >= Ms(300)
                && took >= Ms(750) && took < TimeSpan.FromSeconds(3) && plainServed && slowListen.Line == wire,
                "P1-32 no deadline: a connect accepted after 300 ms is followed by the full 500 ms read timeout (unchanged)", (plain?.GetType().Name ?? "<none>") + " in " + Label(took) + " ms; " + slowListen.Describe());
        }

        // Real pipe, 1 s deadline, add-in listening from 800 ms and never replying.
        var deadline = TimeSpan.FromSeconds(1);
        var listenAt = TimeSpan.FromMilliseconds(800);
        var pipe = AbsentPipe();
        var watch = Stopwatch.StartNew();
        using var addIn = new LateAddIn(pipe, watch, listenAt);
        var transport = new Bim.Cli.RevitDeadlineTransport(pipe, RevitCommandPolicy.None, RevitCommandService.PipeTransport, RevitCommandService.DefaultReadTimeout, deadline,
            () => watch.Elapsed);
        var listing = RevitElementQueryPaging.All(wallsText, transport).ToJson();
        var ended = watch.Elapsed;
        var served = addIn.WaitServed();
        var detail = listing + " in " + N((long)ended.TotalMilliseconds) + " ms; " + addIn.Describe();
        check(served && addIn.Line == wire && addIn.ConnectedAt is { } connectedAt && connectedAt >= listenAt && connectedAt < deadline,
            "P1-32 real pipe: an add-in listening from 800 ms of a 1 s deadline accepts the page before the deadline and receives its exact wire line", detail);
        check(listing == AbsentExceededCli && ended >= TimeSpan.FromMilliseconds(900) && ended < deadline + TimeSpan.FromMilliseconds(300),
            "P1-32 real pipe: that add-in never replies and the run ends query_all_deadline_exceeded within the deadline + 300 ms (read cut to the time left after the connect)", detail);
    }

    // P1-34 (section 3d): a page whose time left is at least the 5 s connect wait (connect not cut) on the default
    // transports goes through connectThenRead with the full 5 s connect wait, its read timeout asked once connected:
    // min(configured read timeout, time left then), cut (its pipe_read_timeout reported as the deadline) only when the
    // time left is the smaller. Seam: an injected connectThenRead whose connect advances the injected clock (P1-36: the
    // one page seam; the P1-21..P1-31 fakes above reach it through the Legacy adapter).
    // Real pipe: an add-in listening from 4 s of a 6 s deadline accepts the page and never replies; the run ends
    // query_all_deadline_exceeded (pinned bytes) within the deadline + 300 ms (before P1-34 the read waited the 6 s left
    // measured before the connect: about 10 s).
    static void UncutConnectRead(string wallsText, long[] model, Action<bool, string, string> check)
    {
        var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(wallsText));
        const string SeamPipe = "bim-agentic-os-2025-3434";
        var single = new[] { 3100L };
        var clock = TimeSpan.Zero;
        var sent = 0;
        var calls = new List<string>();
        string Label(TimeSpan t) => N((long)t.TotalMilliseconds);
        static TimeSpan Ms(long value) => TimeSpan.FromMilliseconds(value);
        // As ReadAfterConnect's fake: the connect takes `connect` (over its wait it times out at the wait), then the read
        // wait is asked; a zero wait sends nothing; the reply completes `reply` after the send (null: never), kept at the wait.
        Func<string, string, string?> Fake(TimeSpan wait, Func<TimeSpan> readAfter, TimeSpan connect, TimeSpan? reply, long[] rows) => (_, line) =>
        {
            if (connect > wait) { clock += wait; calls.Add("connect " + Label(wait) + " timed out"); throw new TimeoutException(); }
            clock += connect;
            var read = readAfter();
            calls.Add("connect " + Label(wait) + " took " + Label(connect) + " read " + Label(read));
            if (read <= TimeSpan.Zero) throw new RevitPipeReadTimeoutException(TimeSpan.Zero);
            sent++;
            if (reply is not { } due || due > read) { clock += read; throw new RevitPipeReadTimeoutException(read); }
            clock += due;
            return PageReply(line, rows);
        };
        string Listing(TimeSpan readWait, TimeSpan limit, TimeSpan connect, TimeSpan? reply, long[]? rows = null, bool cutReads = true)
        {
            clock = TimeSpan.Zero; sent = 0; calls.Clear();
            var seam = new Bim.Cli.RevitDeadlineTransport(SeamPipe, RevitCommandPolicy.None, (_, line) =>
                {
                    calls.Add("transport");
                    sent++;
                    clock += connect + (reply ?? TimeSpan.Zero);
                    return PageReply(line, rows ?? single);
                }, readWait, limit, () => clock, cutReads: cutReads,
                connectThenRead: (wait, readAfter) => Fake(wait, readAfter, connect, reply, rows ?? single));
            try { return RevitElementQueryPaging.All(wallsText, seam).ToJson(); }
            catch (Exception error) { return "<threw " + error.GetType().Name + ": " + error.Message + ">"; }
        }
        string Calls() => " sent " + N(sent) + " clock " + Label(clock) + " calls " + string.Join(",", calls);
        var singleExpected = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(SeamPipe, RevitCommandPolicy.None, (_, line) => PageReply(line, single))).ToJson();
        var allExpected = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(SeamPipe, RevitCommandPolicy.None, (_, line) => PageReply(line, model))).ToJson();
        var halfHour = TimeSpan.FromMinutes(30);
        var six = TimeSpan.FromSeconds(6);
        var tick = TimeSpan.FromTicks(1);

        var lateAccept = Listing(halfHour, six, Ms(4000), null);
        check(lateAccept == RaceExceededAt(6000, 0) && sent == 1 && clock == six && calls.SequenceEqual(new[] { "connect 5000 took 4000 read 2000" }),
            "P1-34 seam: a 4 s connect of a 6 s deadline (full 5 s connect wait) leaves a 2 s read; a page never answered ends query_all_deadline_exceeded at the deadline", lateAccept + Calls());
        var replied = Listing(halfHour, six, Ms(4000), Ms(2000));
        check(replied == singleExpected && sent == 1 && clock == six && calls.SequenceEqual(new[] { "connect 5000 took 4000 read 2000" }),
            "P1-34 seam: after a 4 s connect a reply completing exactly at the 6 s deadline is kept (succeeded, byte-equal)", replied + Calls());
        var repliedLate = Listing(halfHour, six, Ms(4000), Ms(2000) + tick);
        check(repliedLate == RaceExceededAt(6000, 0) && sent == 1 && clock == six,
            "P1-34 seam: after a 4 s connect a reply one tick after the 6 s deadline is query_all_deadline_exceeded", repliedLate + Calls());
        // A 3 s read timeout under a 6 s deadline: uncut before the connect, cut by a 4 s connect to the 2 s left.
        var cutByConnect = Listing(TimeSpan.FromSeconds(3), six, Ms(4000), null);
        check(cutByConnect == RaceExceededAt(6000, 0) && sent == 1 && clock == six && calls.SequenceEqual(new[] { "connect 5000 took 4000 read 2000" }),
            "P1-34 seam: a 4 s connect under a 6 s deadline cuts the 3 s read to the 2 s left; never answered, it is query_all_deadline_exceeded at the deadline", cutByConnect + Calls());
        // A 2 s read timeout under a 10 s deadline: a 4 s connect leaves 6 s, so the configured 2 s read is kept.
        var keptRead = Listing(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), Ms(4000), null);
        var keptReadNode = Node(keptRead);
        check(Code(keptReadNode) == RevitCommandCodes.PipeReadTimeout && keptReadNode?["detail"]?.GetValue<string>() == RaceReadDetail.Replace("30000 ms", "2000 ms", StringComparison.Ordinal)
            && Pages(keptReadNode) == 0 && sent == 1 && clock == TimeSpan.FromSeconds(6) && calls.SequenceEqual(new[] { "connect 5000 took 4000 read 2000" }),
            "P1-34 seam: a 4 s connect under a 10 s deadline keeps the configured 2 s read (min of 2 s and 6 s left), and its pipe_read_timeout passes through", keptRead + Calls());
        // Exactly 5 s left: the connect keeps its full 5 s wait; one taking all of it leaves no read (not sent).
        var five = TimeSpan.FromSeconds(5);
        var connectAll = Listing(halfHour, five, five, null);
        check(connectAll == RaceExceededAt(5000, 0) && sent == 0 && clock == five && calls.SequenceEqual(new[] { "connect 5000 took 5000 read 0" }),
            "P1-34 seam: with exactly 5 s left a connect taking the full 5 s leaves no read: not sent, query_all_deadline_exceeded", connectAll + Calls());
        // An uncut connect that times out (7 s deadline, add-in absent) is still pipe_connect_timeout (P1-30, unchanged).
        var connectTimeout = Listing(halfHour, TimeSpan.FromSeconds(7), Ms(6000), null);
        check(Code(Node(connectTimeout)) == RevitCommandCodes.PipeConnectTimeout && sent == 0 && clock == five && calls.SequenceEqual(new[] { "connect 5000 timed out" }),
            "P1-34 seam: an uncut 5 s connect that times out under a 7 s deadline stays pipe_connect_timeout (not the deadline)", connectTimeout + Calls());
        // A roomy deadline: every page connects with 5 s and reads the configured 30 s; byte-equal to RevitServiceTransport.
        var roomy = Listing(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), Ms(10), Ms(20), model);
        check(roomy == allExpected && sent == 23 && calls.Count == 23 && calls.All(call => call == "connect 5000 took 10 read 30000"),
            "P1-34 seam: within a 1 h deadline every page connects with the 5 s wait and reads the configured 30 s; the listing is byte-equal to RevitServiceTransport paging",
            (roomy.Length > 300 ? roomy[..300] : roomy) + Calls());
        // cutReads false (MCP on an injected transport) never measures: the configured transport serves every page.
        var uncut = Listing(halfHour, TimeSpan.FromHours(1), Ms(4000), Ms(10));
        var uncutFalse = Listing(halfHour, TimeSpan.FromHours(1), Ms(4000), Ms(10), cutReads: false);
        check(uncutFalse == singleExpected && sent == 1 && calls.SequenceEqual(new[] { "transport" }) && uncut == singleExpected,
            "P1-34 seam: cutReads false keeps the configured transport for a page with time left (nothing measured)", uncutFalse + Calls());
        // The cut decision of a measured page is made only once connected (readAfter): a connect that throws before asking
        // leaves the page uncut, so its failure passes through even though the 30 min read is above the 6 s left.
        string Throwing(Exception error)
        {
            calls.Clear();
            var seam = new Bim.Cli.RevitDeadlineTransport(SeamPipe, RevitCommandPolicy.None, (_, _) => { calls.Add("transport"); throw error; }, halfHour, six, () => TimeSpan.Zero,
                connectThenRead: (wait, _) => (_, _) => { calls.Add("connect " + Label(wait) + " threw"); throw error; });
            return RevitElementQueryPaging.All(wallsText, seam).ToJson();
        }
        var connectIo = Throwing(new IOException("broken pipe"));
        check(Code(Node(connectIo)) == RevitCommandCodes.PipeIoError && calls.SequenceEqual(new[] { "connect 5000 threw" }),
            "P1-34 seam: a measured page whose connect fails with an IOException stays pipe_io_error", connectIo + " calls " + string.Join(",", calls));
        var connectRead = Throwing(new RevitPipeReadTimeoutException(halfHour));
        var connectReadNode = Node(connectRead);
        check(Code(connectReadNode) == RevitCommandCodes.PipeReadTimeout && connectReadNode?["detail"]?.GetValue<string>() == RaceReadDetail.Replace("30000 ms", "1800000 ms", StringComparison.Ordinal)
            && calls.SequenceEqual(new[] { "connect 5000 threw" }),
            "P1-34 seam: a measured page that throws pipe_read_timeout before asking its read wait is not cut (the cut flag is reset until connected): it passes through, not the deadline",
            connectRead + " calls " + string.Join(",", calls));

        // Real pipe, 6 s deadline (time left at least 5 s: the connect is not cut), add-in listening from 4 s, never replying.
        var deadline = six;
        var listenAt = TimeSpan.FromSeconds(4);
        var pipe = AbsentPipe();
        var watch = Stopwatch.StartNew();
        using var addIn = new LateAddIn(pipe, watch, listenAt);
        var transport = new Bim.Cli.RevitDeadlineTransport(pipe, RevitCommandPolicy.None, RevitCommandService.PipeTransport, RevitCommandService.DefaultReadTimeout, deadline,
            () => watch.Elapsed);
        var listing = RevitElementQueryPaging.All(wallsText, transport).ToJson();
        var ended = watch.Elapsed;
        var served = addIn.WaitServed();
        var expected = AbsentExceededCli.Replace("its 1000 ms deadline", "its 6000 ms deadline", StringComparison.Ordinal);
        var detail = listing + " in " + N((long)ended.TotalMilliseconds) + " ms (ended - deadline " + N((long)(ended - deadline).TotalMilliseconds) + " ms, window [5900, 6300) ms); " + addIn.Describe();
        check(served && addIn.Line == wire && addIn.ConnectedAt is { } connectedAt && connectedAt >= listenAt && connectedAt < deadline,
            "P1-34 real pipe: an add-in listening from 4 s of a 6 s deadline accepts the page within the uncut 5 s connect and receives its exact wire line", detail);
        check(listing == expected && ended >= TimeSpan.FromMilliseconds(5900) && ended < deadline + TimeSpan.FromMilliseconds(300),
            "P1-34 real pipe: that add-in never replies and the run ends query_all_deadline_exceeded within the deadline + 300 ms (read cut to the time left after the connect)", detail);
    }

    // A same-user pipe server that starts listening only when `clock` reaches `listenAt`, on its own thread (no thread
    // pool latency), accepts one connection, reads one request line and never replies: the pipe stays open until disposed.
    sealed class LateAddIn : IDisposable
    {
        readonly ManualResetEventSlim served = new();
        readonly ManualResetEventSlim release = new();
        readonly Thread thread;

        public LateAddIn(string name, Stopwatch clock, TimeSpan listenAt)
        {
            thread = new Thread(() =>
            {
                try
                {
                    while (clock.Elapsed < listenAt) Thread.Sleep(1);
                    using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.None);
                    ListeningAt = clock.Elapsed;
                    server.WaitForConnection();
                    ConnectedAt = clock.Elapsed;
                    using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    Line = reader.ReadLine();
                    served.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
                catch (Exception error) { Error = error.GetType().Name + ": " + error.Message; }
                finally { served.Set(); }
            }) { IsBackground = true };
            thread.Start();
        }

        public TimeSpan? ListeningAt { get; private set; }
        public TimeSpan? ConnectedAt { get; private set; }
        public string? Line { get; private set; }
        public string? Error { get; private set; }

        public bool WaitServed() => served.Wait(TimeSpan.FromSeconds(10)) && Error is null;

        public string Describe() => "listening " + (ListeningAt is { } l ? N((long)l.TotalMilliseconds) + " ms" : "never")
            + ", connected " + (ConnectedAt is { } c ? N((long)c.TotalMilliseconds) + " ms" : "never")
            + ", line " + (Line ?? "<none>") + (Error is null ? "" : ", error " + Error);

        public void Dispose()
        {
            release.Set();
            if (!thread.Join(TimeSpan.FromSeconds(10))) return;
            served.Dispose();
            release.Dispose();
        }
    }
}
