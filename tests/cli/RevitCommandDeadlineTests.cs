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
//   2. In process (RevitDeadlineTransport, injected clock and its one page seam connectThenRead; the P1-21..P1-31 fakes
//      reach it through the Legacy adapter): a page due after the deadline is answered query_all_deadline_exceeded
//      without being sent; a page whose time left is below the read timeout is read with the time left, and its
//      pipe_read_timeout is reported as the deadline; above it, the configured read timeout is used.
//   2g. P1-36: one page seam (static pin, no legacy seams). 2h. P1-37: ReadReplyLine's OnlyOnFaulted continuation
//      (static pin). 2i. P1-38: PipeTransportWithReadTimeout kept for its production callers (static scan), the CLI
//      passes no never-called transport, the Legacy adapter fails a named Check on an unmodelled cut connect, and
//      StripComments reads @$ verbatim and raw string literals (mutation self-test). P1-40: the caller scan reads the
//      tracked .cs files only (an untracked copy is ignored, control), the CLI pin parses the argument list (formatting
//      and named arguments, self-test), and MCP all:true passes no configured transport when cutReads is true.
//   3e. P1-37: ReadReplyLine's abandoned read on a real stalled pipe raises no unobserved fault. P1-39: a pipe-dispose
//      control (D2) proves the detector's StreamReader filter; a completed read records its result; E claims no collection.
//   P1-39: one strip helper (StripCode: comments, literals, interpolation holes as code) and brace-matched member bodies.
//   P1-43: Run ends with one summary line listing every FAIL name (::warning::, beyond the 10 error annotations a step
//      shows); the Windows D2/B measured runtime is declared once and a different one is a named annotation; MemberBody
//      reads its head at bracket depth 0 (a parameter list or attribute may hold ';', '=>', '{'); {x::} pinned as measured.
//   P1-44: StripCode, MemberBody and the RevitDeadlineTransport argument parser live once in DeadlineSourceScan.cs
//      (linked by tests/mcp); 2g's McpServer comment and 3a's construction are read inside RevitCallAll's own body; the
//      2i control location names a missing git apart from 'not gitignored' and resolves symbolic links.
//   P1-45: the whole cli-inputs step gets one FAIL summary line too (tests/cli/Program.cs, CliInputsSummary, pinned
//      statically here); the Windows runtime annotation is a ::warning:: written from VisibilitySelfTest, the first
//      thing RunSteps does, so the notice budget of the other gates cannot hide it; MemberBody(raw: true) counts
//      brackets on CodeMask (comments and literals blanked in place) and no stray ')' takes that depth below 0.
//   P1-47: the step summary print sits inside the last finally block; the cap's '; +N more' count is pinned; FailSummary
//      is CliInputsSummary.Line under its own title (shared escape and cap); MemberBody(raw: true) skips a signature with
//      any character in a comment or literal, and agrees with the stripped path on interpolated literals.
//   P1-49: MemberBody(raw: true) reads a signature's masked characters from the mask marks (a comment's or literal's
//      spaces and newlines are not code); Retitled throws a named problem for a line without the 'cli-inputs step'
//      prefix, VisibilitySelfTest reds a changed CliInputsSummary prefix by name first, and FailSummary never throws.
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
    // P1-38: a cut connect the fake does not model (no connect given) fails the named Check LegacyUnmodelled and the page
    // answers null (no_response) instead of throwing out of Run.
    static Func<TimeSpan, Func<TimeSpan>, Func<string, string, string?>> Legacy(Action<bool, string, string> check, TimeSpan readTimeout, Func<string, string, string?> transport,
        Func<TimeSpan, Func<string, string, string?>> cut, Func<TimeSpan, TimeSpan, Func<string, string, string?>>? connect = null) => (wait, readAfter) =>
    {
        var read = readAfter();
        if (wait < Bim.Cli.RevitDeadlineTransport.ConnectWait)
        {
            if (connect is not null) return connect(read, wait);
            check(false, LegacyUnmodelled, "connect wait " + N((long)wait.TotalMilliseconds) + " ms, read " + N((long)read.TotalMilliseconds) + " ms");
            return (_, _) => null;
        }
        return read < readTimeout ? cut(read) : transport;
    };

    // P1-43: the step's FAIL names are collected and listed on one summary line at the end, even when a step throws.
    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        var failed = new List<string>();
        try { RunSteps(root, work, dotnet, cliDll, Collecting(check, failed)); }
        finally
        {
            string[] names;
            lock (failed) names = failed.ToArray();
            Console.WriteLine(FailSummary(names));
        }
    }

    static void RunSteps(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        _ = root;
        VisibilitySelfTest(root, check);
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
            connectThenRead: Legacy(check, TimeSpan.FromSeconds(30), Paging, Cut));
        var late = RevitElementQueryPaging.All(wallsText, slow);
        var lateJson = Node(late.ToJson());
        Check(!late.IsSuccess && Code(lateJson) == Exceeded && lateJson?["field"] is null && Pages(lateJson) == 3 && ElementCount(lateJson) == 0 && sent == 3 && cuts.Count == 0
            && lateJson?["detail"]?.GetValue<string>() == "the --all listing took longer than its 180000 ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor",
            "past its deadline the next page is refused query_all_deadline_exceeded without being sent (no partial elements, read timeout below the time left: not cut)", late.ToJson() + " sent " + N(sent));
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        Func<string, string, string?> uncutUsed = (_, _) => throw new InvalidOperationException("uncut transport used");
        var cutting = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, uncutUsed, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), () => clock,
            connectThenRead: Legacy(check, TimeSpan.FromMinutes(30), uncutUsed, Cut));
        var cutListing = RevitElementQueryPaging.All(wallsText, cutting);
        Check(!cutListing.IsSuccess && Code(Node(cutListing.ToJson())) == Exceeded && sent == 6
            && cuts.SequenceEqual(new[] { 330, 270, 210, 150, 90, 30 }.Select(s => TimeSpan.FromSeconds(s))),
            "each page under a read timeout above the time left is read with the time left (330 s, 270 s, ... 30 s)", string.Join(",", cuts.Select(c => N((long)c.TotalSeconds))));
        clock = TimeSpan.Zero; sent = 0;
        var stalledCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            connectThenRead: Legacy(check, TimeSpan.FromMinutes(30), Paging, left => (_, _) => throw new RevitPipeReadTimeoutException(left)));
        var stalledCutJson = Node(RevitElementQueryPaging.All(wallsText, stalledCut).ToJson());
        Check(Code(stalledCutJson) == Exceeded && Pages(stalledCutJson) == 0 && ElementCount(stalledCutJson) == 0,
            "a cut page's pipe_read_timeout is reported as query_all_deadline_exceeded", stalledCutJson?.ToJsonString() ?? "");
        // A cut page's failure other than its read timeout passes through unchanged (not reported as the deadline).
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        var closedCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            connectThenRead: Legacy(check, TimeSpan.FromMinutes(30), Paging, left => { cuts.Add(left); return (_, _) => null; }));
        var closedCutJson = Node(RevitElementQueryPaging.All(wallsText, closedCut).ToJson());
        Check(Code(closedCutJson) == RevitCommandCodes.NoResponse && Pages(closedCutJson) == 0 && ElementCount(closedCutJson) == 0 && cuts.Count == 1,
            "a cut page whose pipe closes without a reply stays no_response", closedCutJson?.ToJsonString() ?? "");
        var brokenCut = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), () => clock,
            connectThenRead: Legacy(check, TimeSpan.FromMinutes(30), Paging, left => (_, _) => throw new IOException("broken pipe")));
        var brokenCutJson = Node(RevitElementQueryPaging.All(wallsText, brokenCut).ToJson());
        Check(Code(brokenCutJson) == RevitCommandCodes.PipeIoError && Pages(brokenCutJson) == 0 && ElementCount(brokenCutJson) == 0,
            "a cut page whose pipe fails with an IOException stays pipe_io_error", brokenCutJson?.ToJsonString() ?? "");
        // Time left equal to the read timeout (60 s / 60 s): no cut, the configured transport's pipe_read_timeout stays.
        clock = TimeSpan.Zero; sent = 0; cuts.Clear();
        Func<string, string, string?> sixtyTimeout = (_, _) => throw new RevitPipeReadTimeoutException(TimeSpan.FromSeconds(60));
        var equalLeft = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, sixtyTimeout,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), () => clock, connectThenRead: Legacy(check, TimeSpan.FromSeconds(60), sixtyTimeout, Cut));
        var equalLeftJson = Node(RevitElementQueryPaging.All(wallsText, equalLeft).ToJson());
        Check(Code(equalLeftJson) == RevitCommandCodes.PipeReadTimeout && equalLeftJson?["detail"]?.GetValue<string>() is { } equalDetail && equalDetail.Contains("60000 ms", StringComparison.Ordinal)
            && Pages(equalLeftJson) == 0 && cuts.Count == 0,
            "time left equal to the read timeout is not cut: pipe_read_timeout, not query_all_deadline_exceeded", equalLeftJson?.ToJsonString() ?? "");
        clock = TimeSpan.Zero; sent = 0;
        var roomy = new Bim.Cli.RevitDeadlineTransport(InProcessPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), () => clock,
            connectThenRead: Legacy(check, TimeSpan.FromSeconds(30), Paging, Cut));
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

        // ---- 2h. P1-37: ReadReplyLine's OnlyOnFaulted continuation pinned statically ----
        ContinuationPinned(root, check);

        // ---- 2i. P1-38: no never-called CLI transport, PipeTransportWithReadTimeout callers, Legacy and StripComments ----
        ConfiguredTransportTidy(root, wallsText, model, check);

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

            // ---- 3e. P1-37: ReadReplyLine's abandoned read on a real pipe raises no unobserved fault ----
            LateCancelRealPipe(wallsText, check);

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
                connectThenRead: Legacy(check, readTimeout, configured, left => Read("cut", left, duration, rows),
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
        var span = SendMeasuredSpan(source);
        var sendBody = span?.Body ?? "";
        var sendProblems = SendMeasuredProblems(source);
        check(sendProblems.Length == 0,
            "P1-33 static: RevitCommandService.SendMeasured reads the reply line only through ReadReplyLine, without the test hook",
            string.Join("; ", sendProblems) + " | " + (sendBody.Length > 400 ? sendBody[^400..] : sendBody));
        var coreUses = Directory.GetFiles(Path.Combine(root, "core"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file).ReplaceLineEndings("\n"), @"ReadReplyLine\(")
                .Select(hit => Path.GetRelativePath(root, file).Replace('\\', '/') + "@" + N(hit.Index))).ToArray();
        var definition = source.IndexOf("internal static string? ReadReplyLine(", StringComparison.Ordinal);
        var inSend = sendStart < 0 ? -1 : source.IndexOf("ReadReplyLine(", sendStart, StringComparison.Ordinal);
        check(coreUses.Length == 2 && definition >= 0 && inSend > sendStart && span is { } sendSpan && LineOf(source, inSend) <= sendSpan.LastLine
            && coreUses.All(use => use.StartsWith("core/Bim.Core/RevitCommandsService.cs@", StringComparison.Ordinal))
            && coreUses.Contains("core/Bim.Core/RevitCommandsService.cs@" + N(definition + "internal static string? ".Length))
            && coreUses.Contains("core/Bim.Core/RevitCommandsService.cs@" + N(inSend)),
            "P1-33 static: under core/ ReadReplyLine( appears only at its internal definition and in SendMeasured", string.Join(", ", coreUses));
        SendMeasuredSelfTest(check);
    }

    static int LineOf(string text, int index)
    {
        var lines = 0;
        for (var k = 0; k < index && k < text.Length; k++) if (text[k] == '\n') lines++;
        return lines;
    }

    // P1-41: SendMeasured's member body and its line span (0-based, first and last line), found by MemberBody on the
    // StripCode output (newlines kept, so its lines are the source's lines); null if absent, unclosed or expression-bodied.
    static (string Body, int FirstLine, int LastLine)? SendMeasuredSpan(string source)
    {
        const string Signature = "static string? SendMeasured(";
        var code = StripCode(source.ReplaceLineEndings("\n"));
        var body = MemberBody(code, Signature);
        if (body is null) return null;
        var first = LineOf(code, code.IndexOf(Signature, StringComparison.Ordinal));
        return (body, first, first + LineOf(body, body.Length));
    }

    // P1-33/P1-41: SendMeasured returns ReadReplyLine(token => reader.ReadLineAsync(token).AsTask(), readTimeout) (no
    // hook), has no own .Wait( or Cancel(), and the one ReadReplyLine( outside its definition lies in SendMeasured's lines.
    static string[] SendMeasuredProblems(string source)
    {
        var span = SendMeasuredSpan(source);
        if (span is not { } send) return new[] { "no SendMeasured block body (absent, unclosed or expression-bodied)" };
        var problems = new List<string>();
        if (!send.Body.Contains("return ReadReplyLine(token => reader.ReadLineAsync(token).AsTask(), readTimeout);", StringComparison.Ordinal))
            problems.Add("SendMeasured does not return ReadReplyLine(token => reader.ReadLineAsync(token).AsTask(), readTimeout)");
        if (send.Body.Contains(".Wait(", StringComparison.Ordinal)) problems.Add("SendMeasured has its own .Wait(");
        if (send.Body.Contains("Cancel()", StringComparison.Ordinal)) problems.Add("SendMeasured has its own Cancel()");
        var definition = source.IndexOf("internal static string? ReadReplyLine(", StringComparison.Ordinal);
        if (definition < 0) problems.Add("no internal static string? ReadReplyLine( definition");
        var defined = definition < 0 ? -1 : definition + "internal static string? ".Length;
        var uses = System.Text.RegularExpressions.Regex.Matches(source, @"ReadReplyLine\(").Select(hit => hit.Index).Where(at => at != defined).ToArray();
        if (uses.Length != 1) problems.Add("ReadReplyLine( appears " + N(uses.Length) + " times outside its definition, not once");
        else if (LineOf(source, uses[0]) < send.FirstLine || LineOf(source, uses[0]) > send.LastLine)
            problems.Add("the ReadReplyLine( call (line " + N(LineOf(source, uses[0]) + 1) + ") is outside SendMeasured (lines " + N(send.FirstLine + 1) + "-" + N(send.LastLine + 1) + ")");
        return problems.ToArray();
    }

    // P1-41: section 2d's method end does not depend on indentation (tab and two-space controls) and an expression-bodied
    // SendMeasured has no body. Mutation self-test on SendMeasuredProblems.
    static void SendMeasuredSelfTest(Action<bool, string, string> check)
    {
        const string Read = "return ReadReplyLine(token => reader.ReadLineAsync(token).AsTask(), readTimeout);";
        const string Clean = "static class S\n{\n    internal static string? ReadReplyLine(Func<CancellationToken, Task<string?>> readLine, TimeSpan readTimeout, Action? afterWaitGivesUp = null)\n    {\n"
            + "        if (readTimeout > TimeSpan.Zero)\n        {\n            return null;\n        }\n        return null;\n    }\n\n"
            + "    static string? SendMeasured(string pipeName, string line, Func<TimeSpan> readTimeoutAfterConnect, int connectMs)\n    {\n"
            + "        using var pipe = new NamedPipeClientStream(\".\", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);\n        if (connectMs > 0)\n        {\n            pipe.Connect(connectMs);\n        }\n"
            + "        var readTimeout = readTimeoutAfterConnect();\n        using var reader = new StreamReader(pipe);\n        " + Read + "\n    }\n}\n";
        string Tabs(string source) => source.Replace("    ", "\t", StringComparison.Ordinal);
        string TwoSpaces(string source) => source.Replace("    ", "  ", StringComparison.Ordinal);
        var cleans = new (string Label, string Source)[]
        {
            ("four-space indentation", Clean),
            ("tab indentation", Tabs(Clean)),
            ("two-space indentation (a nested block closing at four spaces)", TwoSpaces(Clean)),
        };
        foreach (var (label, source) in cleans)
            check(SendMeasuredProblems(source).Length == 0, "P1-41 2d self-test: the SendMeasured check passes with " + label, string.Join("; ", SendMeasuredProblems(source)));
        var moved = Clean.Replace("        " + Read + "\n    }\n}\n", "        return null;\n    }\n\n    static string? After(StreamReader reader, TimeSpan readTimeout)\n    {\n        " + Read + "\n    }\n}\n", StringComparison.Ordinal);
        var arrow = Clean.Replace("int connectMs)\n    {\n", "int connectMs) => After(pipeName);\n\n    static string? After(string pipeName)\n    {\n", StringComparison.Ordinal);
        var mutants = new (string Label, string Source)[]
        {
            ("read moved to a following method, tab indentation", Tabs(moved)),
            ("read moved to a following method, two-space indentation", TwoSpaces(moved)),
            ("expression-bodied SendMeasured whose read is in the next method", arrow),
            ("read only in a comment, two-space indentation", TwoSpaces(Clean.Replace(Read, "// " + Read + "\n        return null;", StringComparison.Ordinal))),
            ("own .Wait( after a nested block, tab indentation", Tabs(Clean.Replace("        using var reader", "        pipe.FlushAsync().Wait(1);\n        using var reader", StringComparison.Ordinal))),
        };
        foreach (var (label, source) in mutants)
            check(source != Clean && SendMeasuredProblems(source).Length > 0, "P1-41 2d self-test: the SendMeasured check flags a " + label, "no problem flagged");
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

    // ---- P1-43: one FAIL summary line per test step; the Windows measured runtime declared once ----
    // GitHub Actions shows at most 10 error annotations per step, so ::error:: FAIL lines after the tenth are hidden. Run
    // ends with one summary line listing every FAIL name of this test step, as a ::warning:: annotation (a budget of its
    // own, which cli-inputs only uses for failing P10-30/P10-32 probes); with no failure a plain line says 0 FAIL.
    const string SummaryTitle = "cli-inputs P1-21..P1-43 deadline tests";

    // P1-47: CliInputsSummary.Line (tests/cli/Program.cs) under this title: one escape (% CR LF) and one cap, '; +N more'.
    // P1-49: a line Retitled cannot read (no 'cli-inputs step' prefix) gives a ::warning:: naming the problem with the FAIL
    // count instead of throwing, so Run's finally block still prints a summary; the problem itself reds by name in
    // VisibilitySelfTest.
    static string FailSummary(IReadOnlyList<string> failed)
    {
        var line = CliInputsSummary.Line(failed);
        return RetitleProblem(line) is { } problem ? "::warning::" + SummaryTitle + ": " + N(failed.Count) + " FAIL, not listed: " + SummaryEscape(problem) : Retitled(line, SummaryTitle);
    }

    // The check the steps call: records each FAIL name (in order) and passes every call on unchanged.
    static Action<bool, string, string> Collecting(Action<bool, string, string> check, List<string> failed) => (condition, name, detail) =>
    {
        if (!condition) lock (failed) failed.Add(name);
        check(condition, name, detail);
    };

    // The Windows D2/B pins of section 3e were measured on this runtime, OS and CI run (P1-39), declared here only.
    const string WindowsMeasuredRuntime = ".NET 8.0.34";
    const string WindowsMeasuredOs = "Windows 10.0.26100";
    const string WindowsMeasuredRun = "44873211457";
    static string WindowsMeasured => "Windows pins measured on " + WindowsMeasuredRuntime + ", " + WindowsMeasuredOs + " (CI run " + WindowsMeasuredRun + ")";

    // null when the running runtime and OS are the measured ones; otherwise a named annotation line (the pins still
    // apply). P1-45: a ::warning::, not a ::notice::, and written from VisibilitySelfTest: the step that runs cli-inputs
    // also runs the gates that spend the ten-notice budget (see VisibilitySelfTest (b)).
    static string? RuntimeNotice(string framework, string os) =>
        framework == WindowsMeasuredRuntime && os.Contains(WindowsMeasuredOs, StringComparison.Ordinal) ? null
            : "::warning::cli-inputs P1-43 Windows runtime differs from the measured one: " + WindowsMeasured + "; this run " + framework + ", " + os
                + " (the D2/B pins still apply; if they fail, re-measure and update the declaration)";

    // P1-47: a CliInputsSummary.Line under another title (its prefix only; the names after it are left as they are).
    // P1-49: the prefix is the literal 'cli-inputs step' (not CliInputsSummary.Title, so a changed title is caught), and
    // a line without it throws a named problem instead of being passed on unchanged.
    const string RetitledPrefix = "cli-inputs step";
    static string? RetitleProblem(string line) =>
        line.StartsWith("::warning::" + RetitledPrefix + ": ", StringComparison.Ordinal) || line.StartsWith(RetitledPrefix + ": ", StringComparison.Ordinal) ? null
            : "Retitled: the line does not start with the 'cli-inputs step' prefix ('::warning::cli-inputs step: ' or 'cli-inputs step: '): "
                + (line.Length > 80 ? line[..80] + "..." : line);

    static string Retitled(string line, string title)
    {
        if (RetitleProblem(line) is { } problem) throw new InvalidOperationException(problem);
        var warning = "::warning::" + RetitledPrefix + ": ";
        return line.StartsWith(warning, StringComparison.Ordinal) ? "::warning::" + title + ": " + line[warning.Length..] : title + ": " + line[(RetitledPrefix.Length + 2)..];
    }

    // P1-49: what Retitled does with a line under SummaryTitle: "returned <line>" or "threw <message>", for the self-tests.
    static string RetitleOutcome(string line)
    {
        try { return "returned " + Retitled(line, SummaryTitle); }
        catch (InvalidOperationException error) { return "threw " + error.Message; }
    }

    // ---- P1-45: the step-wide FAIL summary line and the runtime warning's level and place ----
    // (a) Static, on the comment- and literal-stripped tests/cli/Program.cs: its Check records every FAIL name of the
    // step in failedNames, and the step-wide summary line is printed exactly once, from the finally block, so a
    // harness error (or a test that throws) still prints it. A print named only in a comment is no print.
    static string[] StepSummaryProblems(string source)
    {
        var code = StripCode(source.ReplaceLineEndings("\n"));
        var problems = new List<string>();
        var checkBody = MemberBody(code, "void Check(");
        if (checkBody is null) problems.Add("the Check function was not found");
        else if (!System.Text.RegularExpressions.Regex.IsMatch(checkBody, @"\bfailedNames\s*\.\s*Add\s*\(\s*name\s*\)")) problems.Add("Check does not record the FAIL name in failedNames");
        var prints = System.Text.RegularExpressions.Regex.Matches(code, @"Console\s*\.\s*WriteLine\s*\(\s*CliInputsSummary\s*\.\s*Line\s*\(\s*failedNames\s*\)\s*\)");
        if (prints.Count != 1) problems.Add("the step-wide summary line is printed " + N(prints.Count) + " times, not once");
        else
        {
            // P1-47: the LAST '} finally {' is the step's own (an earlier one is a nested cleanup), and the print must sit
            // inside that final block, between its '{' and the matching '}'.
            var finallies = System.Text.RegularExpressions.Regex.Matches(code, @"\}\s*finally\s*\{");
            if (finallies.Count == 0) problems.Add("no finally block was found");
            else
            {
                var last = finallies[^1];
                var open = last.Index + last.Length - 1;
                var close = -1;
                for (int k = open, depth = 0; k < code.Length; k++)
                {
                    if (code[k] == '{') depth++;
                    else if (code[k] == '}' && --depth == 0) { close = k; break; }
                }
                if (prints[0].Index < open) problems.Add("the step-wide summary line is printed before the final finally block");
                else if (close < 0 || prints[0].Index > close) problems.Add("the step-wide summary line is printed after the final finally block closes");
            }
        }
        return problems.ToArray();
    }

    // (b) Static, on this file: the Windows runtime annotation is written exactly once, from VisibilitySelfTest, which
    // RunSteps calls before it checks or prints anything else; and no ::notice:: line is left anywhere (that budget is
    // spent by the other gates of the same step).
    static string[] RuntimeWarningProblems(string source)
    {
        var code = StripCode(source.ReplaceLineEndings("\n"));
        var problems = new List<string>();
        const string Written = @"Console\s*\.\s*WriteLine\s*\(\s*notice\s*\)";
        var writes = System.Text.RegularExpressions.Regex.Matches(code, Written).Count;
        if (writes != 1) problems.Add("the runtime warning is written " + N(writes) + " times, not once");
        var visibility = MemberBody(code, "static void VisibilitySelfTest(");
        if (visibility is null) problems.Add("VisibilitySelfTest was not found");
        else if (!System.Text.RegularExpressions.Regex.IsMatch(visibility, Written)) problems.Add("VisibilitySelfTest does not write the runtime warning");
        var steps = MemberBody(code, "static void RunSteps(");
        if (steps is null) problems.Add("RunSteps was not found");
        else
        {
            var call = steps.IndexOf("VisibilitySelfTest(", StringComparison.Ordinal);
            if (call < 0) problems.Add("RunSteps does not call VisibilitySelfTest");
            else if (System.Text.RegularExpressions.Regex.IsMatch(steps[..call], @"Console\s*\.\s*Write|SelfTest\(|Problems\(")) problems.Add("RunSteps checks or prints something before VisibilitySelfTest");
        }
        if (DeadlineSourceScan.CommentsBlanked(source).Contains("::" + "notice::", StringComparison.Ordinal)) problems.Add("a " + "::" + "notice:: line is left in the file");
        return problems.ToArray();
    }

    // P1-43: echoes the format an interpolation hole passes (the compiler's reading of a hole like {x::}).
    sealed class EchoFormat : IFormattable
    {
        public string ToString(string? format, IFormatProvider? formatProvider) => "[" + (format ?? "no format") + "]";
        public override string ToString() => "[no format]";
    }

    static void VisibilitySelfTest(string root, Action<bool, string, string> check)
    {
        // P1-49: first, so a changed CliInputsSummary prefix reds by this name before any summary self-test reads it.
        var liveList = RetitleOutcome(CliInputsSummary.Line(new[] { "a" }));
        var liveNone = RetitleOutcome(CliInputsSummary.Line(Array.Empty<string>()));
        check(liveList == "returned ::warning::" + SummaryTitle + ": 1 FAIL: #1 a" && liveNone == "returned " + SummaryTitle + ": 0 FAIL",
            "P1-49 Retitled: CliInputsSummary.Line starts with the 'cli-inputs step' prefix Retitled replaces (a FAIL list and 0 FAIL)",
            liveList + " | " + liveNone);
        var names = Enumerable.Range(1, 12).Select(k => "P1-43 synthetic failure " + N(k)).ToList();
        names[3] = "P1-43 synthetic failure 4 with a ';' and a '|'";
        names[7] = "P1-43 synthetic failure 8\nsecond line 100%";
        var summary = FailSummary(names);
        var escaped = names.Select(name => name.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal)).ToArray();
        var positions = escaped.Select((name, k) => summary.IndexOf("#" + N(k + 1) + " " + name, StringComparison.Ordinal)).ToArray();
        check(summary.StartsWith("::warning::" + SummaryTitle + ": 12 FAIL: ", StringComparison.Ordinal) && !summary.Contains('\n') && !summary.Contains('\r')
            && positions.All(at => at > 0) && positions.Zip(positions.Skip(1)).All(pair => pair.First < pair.Second),
            "P1-43 summary self-test: 12 FAIL names (more than the 10 error annotations a step shows) are listed in order on one ::warning:: line, % CR LF escaped",
            summary.Replace("\n", "<LF>", StringComparison.Ordinal).Replace("\r", "<CR>", StringComparison.Ordinal));
        check(FailSummary(Array.Empty<string>()) == SummaryTitle + ": 0 FAIL", "P1-43 summary self-test: no failure is one plain line (no annotation)", FailSummary(Array.Empty<string>()));
        var calls = new List<string>();
        var failed = new List<string>();
        var collecting = Collecting((condition, name, detail) => calls.Add((condition ? "PASS " : "FAIL ") + name + " | " + detail), failed);
        collecting(true, "a", "1");
        collecting(false, "b", "2");
        collecting(false, "c", "");
        check(calls.SequenceEqual(new[] { "PASS a | 1", "FAIL b | 2", "FAIL c | " }) && failed.SequenceEqual(new[] { "b", "c" }),
            "P1-43 summary self-test: the collecting check passes every call on unchanged and records the FAIL names in order",
            string.Join(", ", calls) + " ; failed " + string.Join(", ", failed));

        var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        // P1-45: written here, the first thing RunSteps does, and as a ::warning:: (reason under (b) below).
        if (OperatingSystem.IsWindows() && RuntimeNotice(framework, os) is { } notice) Console.WriteLine(notice);
        var same = RuntimeNotice(WindowsMeasuredRuntime, "Microsoft " + WindowsMeasuredOs);
        var newer = RuntimeNotice(".NET 8.0.99", "Microsoft " + WindowsMeasuredOs);
        var otherOs = RuntimeNotice(WindowsMeasuredRuntime, "Microsoft Windows 10.0.20348");
        check(same is null && newer is { } n && n.StartsWith("::warning::", StringComparison.Ordinal) && n.Contains(WindowsMeasured, StringComparison.Ordinal) && n.Contains(".NET 8.0.99", StringComparison.Ordinal)
            && otherOs is { } o && o.StartsWith("::warning::", StringComparison.Ordinal) && o.Contains("10.0.20348", StringComparison.Ordinal),
            "P1-43 runtime self-test: the measured runtime and OS give no annotation; a different runtime or OS is a named ::warning:: naming both (P1-45 level)",
            (same ?? "null") + " ; " + (newer ?? "null") + " ; " + (otherOs ?? "null"));
        var selfPath = Path.Combine(root, "tests", "cli", "RevitCommandDeadlineTests.cs");
        var self = File.Exists(selfPath) ? File.ReadAllText(selfPath) : "";
        int Occurrences(string text) { var count = 0; for (var at = self.IndexOf(text, StringComparison.Ordinal); at >= 0; at = self.IndexOf(text, at + 1, StringComparison.Ordinal)) count++; return count; }
        check(self.Length > 0 && Occurrences(WindowsMeasuredRuntime) == 1 && Occurrences(WindowsMeasuredOs) == 1 && Occurrences(WindowsMeasuredRun) == 1,
            "P1-43 runtime self-test: the Windows measured runtime, OS and run each appear once in this file (their declaration)",
            selfPath + ": " + N(Occurrences(WindowsMeasuredRuntime)) + "/" + N(Occurrences(WindowsMeasuredOs)) + "/" + N(Occurrences(WindowsMeasuredRun)) + "; this run " + framework + ", " + os);

        // ---- P1-45 (a): one step-wide FAIL summary line ----
        // The summary above lists the FAIL names of these deadline tests only; the cli-inputs step runs many more test
        // files, whose ::error:: FAIL lines are hidden after the tenth just the same. tests/cli/Program.cs collects
        // every FAIL name its Check reports and prints one step-wide summary line through CliInputsSummary.Line from
        // its finally block, so a harness error still prints it.
        var programPath = Path.Combine(root, "tests", "cli", "Program.cs");
        var program = File.Exists(programPath) ? File.ReadAllText(programPath) : "";
        var stepProblems = program.Length == 0 ? new[] { "not read" } : StepSummaryProblems(program);
        check(stepProblems.Length == 0,
            "P1-45 static: tests/cli/Program.cs records every FAIL name of the step and prints one step-wide summary line from its finally block",
            programPath + ": " + string.Join("; ", stepProblems));
        const string CleanProgram = "int passed=0, failed=0;\nvar failedNames=new List<string>();\n"
            + "void Check(bool condition,string name,string detail=\"\") {\n    if(condition) { Console.WriteLine(\"PASS \"+name); passed++; return; }\n"
            + "    Console.WriteLine(\"::error::cli-inputs FAIL \"+name+(detail.Length>0?\" | \"+detail:\"\"));\n    failedNames.Add(name);\n    failed++;\n}\n"
            + "try {\n    Check(true,\"a\");\n    // Console.WriteLine(CliInputsSummary.Line(failedNames));\n"
            + "} catch(Exception exception) {\n    Console.WriteLine(\"::error::cli-inputs harness error: \"+exception.Message);\n"
            + "} finally {\n    Console.WriteLine(CliInputsSummary.Line(failedNames));\n}\n";
        const string StepPrint = "Console.WriteLine(CliInputsSummary.Line(failedNames));";
        check(StepSummaryProblems(CleanProgram).Length == 0,
            "P1-45 step summary mutation self-test: a Check recording every FAIL name and one summary line printed from the finally block pass (a print named only in a comment is no print)",
            string.Join("; ", StepSummaryProblems(CleanProgram)));
        var stepMutants = new (string Label, string Source)[]
        {
            ("a Check that does not record the FAIL name", CleanProgram.Replace("    failedNames.Add(name);\n", "", StringComparison.Ordinal)),
            ("the summary line printed twice", CleanProgram.Replace("} finally {\n    " + StepPrint, "} finally {\n    " + StepPrint + "\n    " + StepPrint, StringComparison.Ordinal)),
            ("the summary line printed before the finally block", CleanProgram.Replace("} finally {\n    " + StepPrint + "\n}", "    " + StepPrint + "\n} finally {\n}", StringComparison.Ordinal)),
            ("the summary line named only in a comment", CleanProgram.Replace("} finally {\n    " + StepPrint + "\n}", "} finally {\n}", StringComparison.Ordinal)),
            ("no finally block at all", CleanProgram.Replace("} finally {\n    " + StepPrint + "\n}", "}\n" + StepPrint, StringComparison.Ordinal)),
            // P1-47: the pin reads the LAST '} finally {' and wants the print inside that final block.
            ("two finally blocks, the summary line printed from the first only", CleanProgram + "try {\n    Check(true,\"b\");\n} finally {\n    Cleanup();\n}\n"),
            ("the summary line printed after the final block closes", CleanProgram.Replace("} finally {\n    " + StepPrint + "\n}", "} finally {\n}\n" + StepPrint, StringComparison.Ordinal)),
        };
        foreach (var (label, source) in stepMutants)
            check(source != CleanProgram && StepSummaryProblems(source).Length > 0, "P1-45 step summary mutation self-test: " + label + " is flagged", "no problem flagged");
        // P1-47: an earlier try/finally (a nested helper's cleanup) before the step's own one is fine when the print sits
        // in the last, final block.
        var earlierFinally = "try {\n    Prepare();\n} finally {\n    Release();\n}\n" + CleanProgram;
        check(StepSummaryProblems(earlierFinally).Length == 0,
            "P1-47 step summary mutation self-test: an earlier finally block before the final one that prints the summary line passes",
            string.Join("; ", StepSummaryProblems(earlierFinally)));

        var stepNames = Enumerable.Range(1, 12).Select(k => "P1-45 synthetic step failure " + N(k)).ToList();
        stepNames[5] = "P1-45 synthetic step failure 6 with a '%' and a\nsecond line";
        var stepLine = CliInputsSummary.Line(stepNames);
        check(stepLine.StartsWith("::warning::" + CliInputsSummary.Title + ": 12 FAIL: #1 ", StringComparison.Ordinal) && !stepLine.Contains('\n') && !stepLine.Contains('\r')
            && stepLine.Contains("#6 P1-45 synthetic step failure 6 with a '%25' and a%0Asecond line", StringComparison.Ordinal)
            && stepLine.IndexOf("#1 ", StringComparison.Ordinal) < stepLine.IndexOf("#12 ", StringComparison.Ordinal),
            "P1-45 step summary: every FAIL name of the step is listed in order on one ::warning:: line, % CR LF escaped",
            stepLine.Replace("\n", "<LF>", StringComparison.Ordinal).Replace("\r", "<CR>", StringComparison.Ordinal));
        check(CliInputsSummary.Line(Array.Empty<string>()) == CliInputsSummary.Title + ": 0 FAIL",
            "P1-45 step summary: no failure is one plain line (no annotation)", CliInputsSummary.Line(Array.Empty<string>()));
        var longNames = Enumerable.Range(1, 400).Select(k => "P1-45 synthetic long step failure " + N(k) + new string('x', 40)).ToList();
        var longLine = CliInputsSummary.Line(longNames);
        check(longLine.Length <= CliInputsSummary.Cap + 200 && !longLine.Contains('\n')
            && longLine.StartsWith("::warning::" + CliInputsSummary.Title + ": 400 FAIL: #1 ", StringComparison.Ordinal) && longLine.EndsWith(" more", StringComparison.Ordinal),
            "P1-45 step summary: a list past one annotation's length keeps the count and the first names and ends with the number left out",
            N(longLine.Length) + " chars; tail " + longLine[Math.Max(0, longLine.Length - 60)..]);
        // P1-47: the exact count left out. Each of these items is "; #k P1-45 synthetic long step failure k" + 40 'x'
        // (#1 without "; "); #1..#36 fill 2932 of the 3000-char cap and #37 would pass it, so 364 are left out.
        var shownTail = "#36 P1-45 synthetic long step failure 36" + new string('x', 40);
        check(longLine.EndsWith(shownTail + "; +364 more", StringComparison.Ordinal) && !longLine.Contains("#37 ", StringComparison.Ordinal)
            && longLine.Length == ("::warning::" + CliInputsSummary.Title + ": 400 FAIL: ").Length + 2932 + "; +364 more".Length,
            "P1-47 step summary: the cap self-test pins the exact '; +364 more' count (#1..#36 listed, 2932 of the 3000-char cap)",
            N(longLine.Length) + " chars; tail " + longLine[Math.Max(0, longLine.Length - 80)..]);

        // P1-47: P1-43's FailSummary shares CliInputsSummary.Line's escape and cap (its own title in place of the step's),
        // so a long deadline FAIL list is cut the same way.
        var deadlineLong = FailSummary(longNames);
        check(deadlineLong == Retitled(longLine, SummaryTitle) && deadlineLong.StartsWith("::warning::" + SummaryTitle + ": 400 FAIL: #1 ", StringComparison.Ordinal)
            && deadlineLong.EndsWith("; +364 more", StringComparison.Ordinal) && FailSummary(stepNames) == Retitled(stepLine, SummaryTitle)
            && FailSummary(Array.Empty<string>()) == SummaryTitle + ": 0 FAIL",
            "P1-47 summary self-test: P1-43 FailSummary is CliInputsSummary.Line under its own title (same % CR LF escape, same cap and '; +N more')",
            N(deadlineLong.Length) + " chars; tail " + deadlineLong[Math.Max(0, deadlineLong.Length - 60)..]);

        // P1-49: Retitled throws a named problem for a line without the 'cli-inputs step' prefix (a changed title, another
        // annotation level, another case) instead of passing it on unchanged under the wrong title.
        var retitleMutants = new[] { "::warning::cli-inputs steps: 1 FAIL: #1 a", "cli-inputs stage: 0 FAIL", "::error::cli-inputs step: 1 FAIL: #1 a", "::warning::CLI-inputs step: 1 FAIL: #1 a", "" };
        var retitleOutcomes = retitleMutants.Select(RetitleOutcome).ToArray();
        check(retitleOutcomes.All(outcome => outcome.StartsWith("threw ", StringComparison.Ordinal) && outcome.Contains("'cli-inputs step' prefix", StringComparison.Ordinal)),
            "P1-49 Retitled self-test: a line without the 'cli-inputs step' prefix (changed title, other level, other case, empty) throws a named problem, not passed on unchanged",
            string.Join(" | ", retitleOutcomes));

        // ---- P1-45 (b): the runtime annotation's level and place ----
        // The step that runs cli-inputs (scripts/verify.ps1) also runs the gates that print ::notice:: lines (tests/mcp,
        // tests/generative, tests/mirror-journal, tests/packaging) and GitHub Actions shows at most ten annotations of
        // each level per step, so a notice printed from here can be dropped. P1-43's Windows runtime annotation is a
        // ::warning:: instead (this step's warnings are otherwise only the two FAIL summary lines and the failing
        // P10-30/P10-32 probes) and it is printed from this self-test, the first thing RunSteps does.
        var warningProblems = self.Length == 0 ? new[] { "not read" } : RuntimeWarningProblems(self);
        check(warningProblems.Length == 0,
            "P1-45 static: the Windows runtime annotation is written once, from VisibilitySelfTest before anything else these tests print, and no notice line is left",
            selfPath + ": " + string.Join("; ", warningProblems));
        const string CleanFile = "class F\n{\n    static void RunSteps(string root, Action<bool, string, string> check)\n    {\n"
            + "        void Check(bool condition, string name) => check(condition, name, \"\");\n        VisibilitySelfTest(root, check);\n        Check(true, \"a\");\n    }\n\n"
            + "    static void VisibilitySelfTest(string root, Action<bool, string, string> check)\n    {\n"
            + "        if (OperatingSystem.IsWindows() && RuntimeNotice(\"x\", \"y\") is { } notice) Console.WriteLine(notice);\n        check(true, \"b\", root);\n    }\n}\n";
        const string Write = "Console.WriteLine(notice);";
        check(RuntimeWarningProblems(CleanFile).Length == 0,
            "P1-45 runtime warning mutation self-test: one write, from a VisibilitySelfTest that RunSteps calls first, passes", string.Join("; ", RuntimeWarningProblems(CleanFile)));
        var warningMutants = new (string Label, string Source)[]
        {
            ("the write moved out of VisibilitySelfTest", CleanFile.Replace("if (OperatingSystem.IsWindows() && RuntimeNotice(\"x\", \"y\") is { } notice) " + Write, "check(true, \"c\", root);", StringComparison.Ordinal)
                .Replace("        Check(true, \"a\");", "        var notice = \"x\";\n        " + Write, StringComparison.Ordinal)),
            ("the warning written twice", CleanFile.Replace(Write, Write + " " + Write, StringComparison.Ordinal)),
            ("something printed before the VisibilitySelfTest call", CleanFile.Replace("        VisibilitySelfTest(root, check);", "        Console.WriteLine(\"first\");\n        VisibilitySelfTest(root, check);", StringComparison.Ordinal)),
            ("a " + "::" + "notice:: line left in the file", CleanFile.Replace("check(true, \"b\", root);", "Console.WriteLine(\"::" + "notice::b \" + root);", StringComparison.Ordinal)),
        };
        foreach (var (label, source) in warningMutants)
            check(source != CleanFile && RuntimeWarningProblems(source).Length > 0, "P1-45 runtime warning mutation self-test: " + label + " is flagged", "no problem flagged");
    }

    // ---- 2g. P1-36: RevitDeadlineTransport has one page seam ----
    // Static, on the comment-stripped source of interfaces/cli/RevitDeadlineTransport.cs: no cutTransport or
    // connectTransport seam and no measured flag remain, and Send makes every real-pipe page through one
    // connectThenRead( call. McpServer's deadline comment says the read is measured after the connect on every
    // real-pipe page. Mutation self-test: the same checker flags a source with any legacy seam put back, and ignores a
    // seam named only in a comment.
    // P1-44: StripCode and MemberBody live once in tests/cli/DeadlineSourceScan.cs (shared with tests/mcp).
    static string StripCode(string source) => DeadlineSourceScan.StripCode(source);
    static string? MemberBody(string code, string signature) => DeadlineSourceScan.MemberBody(code, signature);
    // P1-45: the same helpers reading the source as written (comments and literals masked out), not StripCode output.
    static string CodeMask(string source) => DeadlineSourceScan.CodeMask(source);
    static string? MemberBody(string code, string signature, bool raw) => DeadlineSourceScan.MemberBody(code, signature, raw);

    static string[] SeamProblems(string source)
    {
        var code = StripCode(source.ReplaceLineEndings("\n"));
        var problems = new List<string>();
        foreach (var seam in new[] { "cutTransport", "connectTransport", "measured", "PipeTransportWithReadTimeout" })
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\b" + seam + @"\b")) problems.Add("legacy seam " + seam);
        var send = MemberBody(code, "public string? Send(") ?? "";
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
        var comment = McpDeadlineComment(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")));
        check(comment.Contains(MeasuredPhrase, StringComparison.Ordinal),
            "P1-36 static: McpServer.RevitCallAll's deadline comment states the read is measured after the connect on every real-pipe page", comment);

        // P1-44 self-test: the comment is read inside RevitCallAll's own body (signature and construction found on the
        // stripped code), and a '//' line counts only when it is a comment, not text inside a literal.
        static string CallAll(string inside) => "    (string Text, bool IsError) RevitCallAll(JsonElement arguments)\n    {\n" + inside + "        return (\"\", false);\n    }\n";
        const string Built = "        var transport = new RevitDeadlineTransport(pipeName, RevitCommandPolicy.None, null, readTimeout, QueryAllDeadline, cutReads: realPipe);\n";
        const string Said = "        // P1-36: the read is measured after the connect on every\n        // real-pipe page.\n";
        const string Head = "sealed class S\n{\n", Tail = "}\n";
        var commentCleans = new (string Label, string Source)[]
        {
            ("today's layout", Head + CallAll(Said + Built) + Tail),
            ("CRLF line endings", (Head + CallAll(Said + Built) + Tail).Replace("\n", "\r\n", StringComparison.Ordinal)),
            ("a construction named in a comment before the phrase", Head + CallAll("        // replaces P1-23's new RevitDeadlineTransport( call\n" + Said + Built) + Tail),
            ("a '}' in a string before the phrase", Head + CallAll("        var close = \"}\";\n" + Said + Built) + Tail),
        };
        foreach (var (label, text) in commentCleans)
            check(McpDeadlineComment(text).Contains(MeasuredPhrase, StringComparison.Ordinal), "P1-44 deadline comment self-test: " + label + " states the phrase", McpDeadlineComment(text));
        var commentMutants = new (string Label, string Source)[]
        {
            ("phrase only above another member that names the signature in a comment",
                Head + "    // see (string Text, bool IsError) RevitCallAll(\n    // " + MeasuredPhrase + "\n    void Other() { }\n" + CallAll(Built) + Tail),
            ("phrase only in a raw string literal line", Head + CallAll("        var note = \"\"\"\n        // " + MeasuredPhrase + "\n        \"\"\";\n" + Built) + Tail),
            ("phrase only in a verbatim string literal line", Head + CallAll("        var note = @\"\n        // " + MeasuredPhrase + "\n        \";\n" + Built) + Tail),
            ("phrase only after the construction", Head + CallAll(Built + Said) + Tail),
            ("no construction inside RevitCallAll (one in the next member)",
                Head + CallAll(Said + "        var transport = new RevitServiceTransport(pipeName, RevitCommandPolicy.None, revitTransport);\n") + "    void Other()\n    {\n" + Built + "    }\n" + Tail),
        };
        foreach (var (label, text) in commentMutants)
            check(!McpDeadlineComment(text).Contains(MeasuredPhrase, StringComparison.Ordinal), "P1-44 deadline comment self-test: " + label + " is flagged", McpDeadlineComment(text));
    }

    const string MeasuredPhrase = "the read is measured after the connect on every real-pipe page";

    // P1-44: McpServer.RevitCallAll's deadline comment: the whole-line // comments from its signature to its one
    // RevitDeadlineTransport construction, folded into one line ("" without that body or construction). The body and
    // the construction are found on the StripCode output (MemberBody and the parsed construction pattern, so a signature
    // or construction named in a comment or literal is not them); a line counts when it starts with '//' and
    // CommentsBlanked blanks it whole (a comment, not '//' text inside a raw or verbatim literal).
    static string McpDeadlineComment(string source)
    {
        const string Signature = "(string Text, bool IsError) RevitCallAll(";
        var raw = source.ReplaceLineEndings("\n");
        var code = StripCode(raw);
        var body = MemberBody(code, Signature);
        if (body is null) return "";
        var built = System.Text.RegularExpressions.Regex.Matches(body, DeadlineSourceScan.ConstructionPattern);
        if (built.Count != 1) return "";
        var first = LineOf(code, code.IndexOf(Signature, StringComparison.Ordinal));
        var last = first + LineOf(body, built[0].Index);
        var lines = raw.Split('\n');
        var blanked = DeadlineSourceScan.CommentsBlanked(raw).Split('\n');
        var said = new List<string>();
        for (var line = first; line < last && line < lines.Length; line++)
        {
            var text = lines[line].Trim();
            if (text.StartsWith("//", StringComparison.Ordinal) && blanked[line].Trim().Length == 0) said.Add(text.TrimStart('/').Trim());
        }
        return string.Join(" ", said);
    }

    // ---- 2h. P1-37: ReadReplyLine observes its abandoned read (static pin) ----
    // On the comment- and literal-stripped source of core/Bim.Core/RevitCommandsService.cs, ReadReplyLine starts its read
    // as `var read = readLine(cancel.Token)` and, after cancel.Cancel() and before it throws RevitPipeReadTimeoutException,
    // attaches exactly one continuation read.ContinueWith(task => task.Exception, CancellationToken.None,
    // TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default): reading Exception marks a late fault observed.
    // Mutation self-test: the checker flags the continuation removed, hidden in a comment or a (regular, verbatim or raw)
    // string, on another task, not reading Exception, for cancels instead of faults, duplicated, or out of place.
    static string[] ContinuationProblems(string source)
    {
        var body = MemberBody(StripCode(source.ReplaceLineEndings("\n")), "internal static string? ReadReplyLine(");
        if (body is null) return new[] { "no ReadReplyLine definition" };
        var problems = new List<string>();
        if (!System.Text.RegularExpressions.Regex.IsMatch(body, @"\bvar\s+read\s*=\s*readLine\s*\(\s*cancel\s*\.\s*Token\s*\)\s*;"))
            problems.Add("the read is not started as var read = readLine(cancel.Token)");
        var continuations = System.Text.RegularExpressions.Regex.Matches(body, @"\.\s*ContinueWith\s*\(").Count;
        if (continuations != 1) problems.Add("ContinueWith( appears " + N(continuations) + " times, not once");
        var pinned = System.Text.RegularExpressions.Regex.Matches(body,
            @"\bread\s*\.\s*ContinueWith\s*\(\s*(?:static\s+)?(?<t>\w+)\s*=>\s*\k<t>\s*\.\s*Exception\s*,\s*CancellationToken\s*\.\s*None\s*,\s*TaskContinuationOptions\s*\.\s*OnlyOnFaulted\s*,\s*TaskScheduler\s*\.\s*Default\s*\)\s*;");
        if (pinned.Count != 1) { problems.Add("no read.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default)"); return problems.ToArray(); }
        var at = pinned[0].Index;
        var cancelled = body.IndexOf("cancel.Cancel();", StringComparison.Ordinal);
        var thrown = body.IndexOf("throw new RevitPipeReadTimeoutException(", StringComparison.Ordinal);
        if (cancelled < 0 || at < cancelled) problems.Add("the continuation is not attached after cancel.Cancel()");
        if (thrown < 0 || at > thrown) problems.Add("the continuation is not attached before the read timeout is thrown");
        return problems.ToArray();
    }

    static void ContinuationPinned(string root, Action<bool, string, string> check)
    {
        var source = File.ReadAllText(Path.Combine(root, "core", "Bim.Core", "RevitCommandsService.cs")).ReplaceLineEndings("\n");
        var problems = ContinuationProblems(source);
        check(problems.Length == 0, "P1-37 static: ReadReplyLine attaches one OnlyOnFaulted continuation reading the abandoned read's Exception, after the cancel and before the read timeout throw",
            string.Join("; ", problems));

        const string Attach = "_ = read.ContinueWith(static task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);";
        const string Clean = "static class S\n{\n    internal static string? ReadReplyLine(Func<CancellationToken, Task<string?>> readLine, TimeSpan readTimeout)\n    {\n"
            + "        using var cancel = new CancellationTokenSource();\n        var read = readLine(cancel.Token);\n        // read.ContinueWith( named in a comment\n"
            + "        if (read.Wait(readTimeout)) return read.Result;\n        cancel.Cancel();\n        var note = \"read.ContinueWith(\" + @\"read.ContinueWith(\"\"\";\n"
            + "        if (read.IsCompletedSuccessfully) return read.Result;\n        " + Attach + "\n        throw new RevitPipeReadTimeoutException(readTimeout);\n    }\n}\n";
        check(ContinuationProblems(Clean).Length == 0, "P1-37 mutation self-test: one pinned continuation (others named only in comments and strings) passes",
            string.Join("; ", ContinuationProblems(Clean)));
        var mutants = new (string Label, string Source)[]
        {
            ("removed continuation", Clean.Replace(Attach, "", StringComparison.Ordinal)),
            ("line-commented continuation", Clean.Replace(Attach, "// " + Attach, StringComparison.Ordinal)),
            ("block-commented continuation", Clean.Replace(Attach, "/* " + Attach + " */", StringComparison.Ordinal)),
            ("continuation in a string", Clean.Replace(Attach, "var hidden = \"" + Attach + "\";", StringComparison.Ordinal)),
            ("continuation in a verbatim string", Clean.Replace(Attach, "var hidden = @\"" + Attach + "\";", StringComparison.Ordinal)),
            ("continuation in a raw string", Clean.Replace(Attach, "var hidden = \"\"\"\n" + Attach + "\n\"\"\";", StringComparison.Ordinal)),
            ("OnlyOnCanceled continuation", Clean.Replace("OnlyOnFaulted", "OnlyOnCanceled", StringComparison.Ordinal)),
            ("continuation not reading Exception", Clean.Replace("task => task.Exception", "task => task.Status", StringComparison.Ordinal)),
            ("continuation on another task", Clean.Replace("_ = read.ContinueWith(", "_ = Task.CompletedTask.ContinueWith(", StringComparison.Ordinal)),
            ("second continuation", Clean.Replace(Attach, Attach + "\n        " + Attach, StringComparison.Ordinal)),
            ("continuation after the throw", Clean.Replace("        " + Attach + "\n        throw new RevitPipeReadTimeoutException(readTimeout);", "        throw new RevitPipeReadTimeoutException(readTimeout);\n        " + Attach, StringComparison.Ordinal)),
            ("continuation before the cancel", Clean.Replace("        " + Attach + "\n", "", StringComparison.Ordinal).Replace("        cancel.Cancel();\n", "        " + Attach + "\n        cancel.Cancel();\n", StringComparison.Ordinal)),
            ("read not started with the cancel token", Clean.Replace("var read = readLine(cancel.Token);", "var read = readLine(CancellationToken.None);", StringComparison.Ordinal)),
        };
        foreach (var (label, mutant) in mutants)
            check(mutant != Clean && ContinuationProblems(mutant).Length > 0, "P1-37 mutation self-test: the continuation check flags a " + label, string.Join("; ", ContinuationProblems(mutant)));
        StripHelperSelfTest(Clean, Attach, check);
    }

    // P1-39: the one strip helper (StripCode) reads interpolation holes as code, nested quotes included, and the method
    // end is found by brace matching, not by indentation. Mutation self-test on ContinuationProblems and SeamProblems.
    static void StripHelperSelfTest(string clean, string attach, Action<bool, string, string> check)
    {
        const string Hide = "_ = read.ContinueWith(t => t, TaskScheduler.Default);";
        var cleans = new (string Label, string Source)[]
        {
            ("continuation code after a hole holding a \"//\" string on the same line", clean.Replace(attach, "var shown = $\"{(read.IsFaulted ? \"//\" : \"\")}\"; " + attach, StringComparison.Ordinal)),
            ("continuation text after a hole holding an escaped quote", clean.Replace(attach, "var note = $\"{\"\\\"\"}" + Hide + "\";\n        " + attach, StringComparison.Ordinal)),
            ("continuation text after a verbatim hole holding a char quote", clean.Replace(attach, "var note = $@\"{F('\"')}" + Hide + "\";\n        " + attach, StringComparison.Ordinal)),
            ("continuation code after a raw hole holding a \"//\" string", clean.Replace(attach, "var shown = $$\"\"\"{{F(\"//\")}}\"\"\"; " + attach, StringComparison.Ordinal)),
            ("tab indentation", clean.Replace("    ", "\t", StringComparison.Ordinal)),
            ("two-space indentation", clean.Replace("    ", "  ", StringComparison.Ordinal)),
            ("a namespace block (eight-space members)", "namespace N\n{\n" + string.Join("\n", clean.TrimEnd('\n').Split('\n').Select(line => line.Length == 0 ? line : "    " + line)) + "\n}\n"),
        };
        foreach (var (label, source) in cleans)
            check(ContinuationProblems(source).Length == 0, "P1-39 strip self-test: the continuation check passes with " + label, string.Join("; ", ContinuationProblems(source)));
        var mutants = new (string Label, string Source)[]
        {
            ("second continuation after a hole holding a \"//\" string", clean.Replace(attach, attach + "\n        var shown = $\"{(read.IsFaulted ? \"//\" : \"\")}\"; " + Hide, StringComparison.Ordinal)),
            ("second continuation inside a hole", clean.Replace(attach, attach + "\n        var shown = $\"{(read.IsFaulted ? \"x\" : \"\")} {read.ContinueWith(t => t, TaskScheduler.Default)}\";", StringComparison.Ordinal)),
            ("removed continuation, tab indentation", clean.Replace(attach, "", StringComparison.Ordinal).Replace("    ", "\t", StringComparison.Ordinal)),
            ("continuation in a following method, tab indentation", clean.Replace("        " + attach + "\n", "", StringComparison.Ordinal).Replace("    }\n}\n", "    }\n    static void After(Task<string?> read)\n    {\n        " + attach + "\n    }\n}\n", StringComparison.Ordinal).Replace("    ", "\t", StringComparison.Ordinal)),
        };
        foreach (var (label, source) in mutants)
            check(source != clean && ContinuationProblems(source).Length > 0, "P1-39 strip self-test: the continuation check flags a " + label, "no problem flagged");

        const string Send = "class T\n{\n    public string? Send(string line)\n    {\n        var pipe = connectThenRead(wait, ReadAfter);\n        return pipe(line);\n    }\n}\n";
        check(SeamProblems(Send.Replace("    ", "\t", StringComparison.Ordinal)).Length == 0, "P1-39 strip self-test: the seam check passes a tab-indented Send",
            string.Join("; ", SeamProblems(Send.Replace("    ", "\t", StringComparison.Ordinal))));
        var hidden = Send.Replace("        return pipe(line);", "        var s = $\"{(line is null ? \"//\" : \"\")}\"; var c = cutTransport;\n        return pipe(line);", StringComparison.Ordinal);
        check(SeamProblems(hidden).Length > 0, "P1-39 strip self-test: the seam check flags a seam in code after a hole holding a \"//\" string", "no problem flagged");

        // P1-41: inside a hole `global::` is the alias qualifier, not a format; a format ':' is the first single ':' at
        // hole depth 0 (literal text, blanked).
        var holes = new (string Source, string Expected)[]
        {
            ("var s = $\"{global::X.Y()}\";", "var s = \"{global::X.Y()}\";"),
            ("var s = $\"{global::X.Y():N2}\";", "var s = \"{global::X.Y()}\";"),
            ("var s = $\"{x:N2} {global::X.Y(a, b)}\";", "var s = \"{x}{global::X.Y(a, b)}\";"),
            ("var s = $@\"{global::X.Y()}\";", "var s = \"{global::X.Y()}\";"),
            ("var s = $$\"\"\"{{global::X.Y()}}\"\"\";", "var s = \"{global::X.Y()}\";"),
            ("var s = $\"{(a ? global::X.Y() : b):D}\";", "var s = \"{(a ? global::X.Y() : b)}\";"),
        };
        foreach (var (source, expected) in holes)
            check(StripCode(source) == expected, "P1-41 strip self-test: " + source + " strips to " + expected, StripCode(source));
        check(ContinuationProblems(clean.Replace(attach, "var shown = $\"{global::System.String.Concat(\"}\", \"//\")}\"; " + attach, StringComparison.Ordinal)).Length == 0,
            "P1-41 strip self-test: the continuation check passes with continuation code after a global:: hole holding \"}\" and \"//\" strings",
            string.Join("; ", ContinuationProblems(clean.Replace(attach, "var shown = $\"{global::System.String.Concat(\"}\", \"//\")}\"; " + attach, StringComparison.Ordinal))));
        var globalHole = clean.Replace(attach, attach + "\n        var shown = $\"{global::System.Threading.Tasks.Task.FromResult(read.ContinueWith(t => t, TaskScheduler.Default))}\";", StringComparison.Ordinal);
        check(globalHole != clean && ContinuationProblems(globalHole).Length > 0, "P1-41 strip self-test: the continuation check flags a second continuation inside a global:: hole", "no problem flagged");

        // P1-41: MemberBody has no body for a member whose ';' or '=>' comes before its first '{' (abstract,
        // expression-bodied), so a later member's block is never taken for it.
        const string Members = "abstract class T\n{\n    public abstract string? Send(string line);\n    public string? Peek(string line) => connectThenRead(wait, ReadAfter)(line);\n"
            + "    public string? Body(string line)\n    {\n        return line;\n    }\n}\n";
        check(MemberBody(Members, "public abstract string? Send(") is null, "P1-41 MemberBody self-test: an abstract member (';' before the first '{') has no body",
            MemberBody(Members, "public abstract string? Send(") ?? "null");
        check(MemberBody(Members, "public string? Peek(") is null, "P1-41 MemberBody self-test: an expression-bodied member ('=>' before the first '{') has no body",
            MemberBody(Members, "public string? Peek(") ?? "null");
        check(MemberBody(Members, "public string? Body(") == "public string? Body(string line)\n    {\n        return line;\n    }",
            "P1-41 MemberBody self-test: a block member's body is its signature through its closing brace", MemberBody(Members, "public string? Body(") ?? "null");
        const string ArrowSend = "class T\n{\n    public string? Send(string line) => pipe(line);\n    string? Other(string line)\n    {\n        var pipe = connectThenRead(wait, ReadAfter);\n        return pipe(line);\n    }\n}\n";
        check(SeamProblems(ArrowSend).Length > 0, "P1-41 MemberBody self-test: the seam check flags an expression-bodied Send whose page seam is in the next method", "no problem flagged");
        var arrowContinuation = clean.Replace("TimeSpan readTimeout)\n    {\n", "TimeSpan readTimeout) => Body(readLine, readTimeout);\n    static string? Body(Func<CancellationToken, Task<string?>> readLine, TimeSpan readTimeout)\n    {\n", StringComparison.Ordinal);
        check(arrowContinuation != clean && ContinuationProblems(arrowContinuation).Length > 0,
            "P1-41 MemberBody self-test: the continuation check flags an expression-bodied ReadReplyLine whose body is the next method", "no problem flagged");

        // P1-43: a ';', '=>' or '{' inside the parameter list or an attribute (bracket depth > 0) is not the member's own:
        // a block member keeps its body there, on raw and on stripped text; abstract and expression-bodied members with
        // such parameters still have none (mutants: the P1-41 head check returned null or the attribute's braces).
        const string Tail = "\n    {\n        return line;\n    }";
        const string Next = "\n    string? After(string line)\n    {\n        return line;\n    }\n}\n";
        var blockMembers = new (string Label, string Signature, string Member)[]
        {
            ("a parameter attribute holding '=>' and ';' text", "public string? Send(", "public string? Send([Note(\"a => b; c\")] string line)" + Tail),
            ("a parameter attribute holding an array initializer's braces", "public string? Send(", "public string? Send([Values(new[] { 1, 2 })] string line, Func<int, int>? map = null)" + Tail),
            ("a member attribute holding '=>' and ';' text", "[Note(\"x => y;\")] public string? Send(", "[Note(\"x => y;\")] public string? Send(string line)" + Tail),
            ("a generic constraint after a parameter attribute", "public string? Send<T>(", "public string? Send<T>([Note(\"(;)\")] T line) where T : class, new()" + Tail),
        };
        foreach (var (label, signature, member) in blockMembers)
        {
            var source = "class T\n{\n    " + member + Next;
            var stripped = StripCode(source);
            var strippedMember = StripCode(member);
            check(MemberBody(source, signature) == member && MemberBody(stripped, StripCode(signature)) == strippedMember,
                "P1-43 MemberBody self-test: a block member with " + label + " keeps its body (raw and stripped)",
                (MemberBody(source, signature) ?? "null") + " ; " + (MemberBody(stripped, StripCode(signature)) ?? "null"));
        }
        var bodiless = new (string Label, string Signature, string Member)[]
        {
            ("an abstract member with an array initializer in a parameter attribute", "public abstract string? Send(", "public abstract string? Send([Values(new[] { 1 })] string line);"),
            ("an expression-bodied member with a '{' in a parameter attribute", "public string? Peek(", "public string? Peek([Note(\"{\")] string line) => line;"),
            ("an expression-bodied member with braces in its parameters and its expression", "public string? Make(", "public string? Make([Values(new[] { 1 })] string line) => new Holder { Line = line }.Line;"),
        };
        foreach (var (label, signature, member) in bodiless)
        {
            var source = "abstract class T\n{\n    " + member + Next;
            check(MemberBody(source, signature) is null && MemberBody(StripCode(source), StripCode(signature)) is null,
                "P1-43 MemberBody self-test: " + label + " has no body (raw and stripped)",
                (MemberBody(source, signature) ?? "null") + " ; " + (MemberBody(StripCode(source), StripCode(signature)) ?? "null"));
        }
        var attributedArrow = "class T\n{\n    public string? Send([Values(new[] { 1 })] string line) => pipe(line);\n    string? Other(string line)\n    {\n        var pipe = connectThenRead(wait, ReadAfter);\n        return pipe(line);\n    }\n}\n";
        check(SeamProblems(attributedArrow).Length > 0, "P1-43 MemberBody self-test: the seam check flags an expression-bodied Send with braces in a parameter attribute whose page seam is in the next method", "no problem flagged");

        // P1-43: a hole ending in '::' like {x::}, as measured. The C# compiler reads the first ':' at hole depth 0 as the
        // format start (an IFormattable receives the format ":"); StripCode, following P1-41's rule that a ':' next to a
        // ':' is code, keeps x:: as code. They differ only on such a hole, which no pinned source holds.
        var echo = new EchoFormat();
        var compiled = $"{echo::}";
        check(compiled == "[:]", "P1-43 strip self-test: the compiler reads {x::} as x with the format \":\" (measured)", compiled);
        check(StripCode("var s = $\"{x::}\";") == "var s = \"{x::}\";" && StripCode("var s = $@\"{x::}\";") == "var s = \"{x::}\";",
            "P1-43 strip self-test: StripCode reads {x::} as the code x:: (P1-41 '::' rule, pinned as measured)", StripCode("var s = $\"{x::}\";") + " ; " + StripCode("var s = $@\"{x::}\";"));

        // P1-45: CodeMask is the source as written with every comment and every literal blanked at its own position
        // (same length, newlines kept), the code of an interpolation hole kept and its braces and surrounding literal
        // text blanked. MemberBody(raw: true) counts brackets on it, so a bracket inside a string or char literal is
        // not the code's (the stripped path is unchanged: there a literal is "" or an interpolated literal's
        // "{hole code}", whose brackets are that code's).
        const string MaskSource = "var s = \"a(b\"; // c)\nvar c = '(';\nvar t = $\"{F(x)}:N2\";\nvar r = \"\"\"a)b\"\"\";\n";
        var mask = CodeMask(MaskSource);
        check(mask.Length == MaskSource.Length && mask.Count(ch => ch == '\n') == MaskSource.Count(ch => ch == '\n')
            && mask.StartsWith("var s = ", StringComparison.Ordinal) && mask.Contains("var t = ", StringComparison.Ordinal) && mask.Contains("F(x)", StringComparison.Ordinal)
            && !mask.Contains("a(b", StringComparison.Ordinal) && !mask.Contains("c)", StringComparison.Ordinal) && !mask.Contains(":N2", StringComparison.Ordinal)
            && !mask.Contains("a)b", StringComparison.Ordinal) && !mask.Contains("{F", StringComparison.Ordinal)
            && mask.Count(ch => ch == '(') == 1 && mask.Count(ch => ch == ')') == 1,
            "P1-45 CodeMask self-test: comments and literals are blanked in place (length, lines and code kept; an interpolation hole's code stays, its braces and text go)",
            mask.Replace("\n", "<LF>", StringComparison.Ordinal));

        var rawMembers = new (string Label, string Signature, string Member)[]
        {
            ("a parameter attribute holding ')'", "public string? Send(", "public string? Send([Note(\")\")] string line)" + Tail),
            ("a parameter attribute holding an unbalanced '('", "public string? Send(", "public string? Send([Note(\"(\")] string line)" + Tail),
            ("a parameter attribute holding an unbalanced '[' as a char literal", "public string? Send(", "public string? Send([Note('[')] string line)" + Tail),
            ("a default value holding brackets", "public string? Send(", "public string? Send(string line, string tag = \"([{\")" + Tail),
            ("a verbatim default value holding brackets", "public string? Send(", "public string? Send(string line, string tag = @\"a)b(\")" + Tail),
        };
        foreach (var (label, signature, member) in rawMembers)
        {
            var source = "class T\n{\n    " + member + Next;
            check(MemberBody(source, signature, true) == member, "P1-45 MemberBody self-test: raw text with " + label + " keeps the member's body",
                MemberBody(source, signature, true) ?? "null");
        }
        var rawBodiless = new (string Label, string Signature, string Member)[]
        {
            ("an expression-bodied member whose parameter attribute holds '('", "public string? Peek(", "public string? Peek([Note(\"(\")] string line) => line;"),
            ("an abstract member whose parameter attribute holds '('", "public abstract string? Send(", "public abstract string? Send([Note(\"(\")] string line);"),
        };
        foreach (var (label, signature, member) in rawBodiless)
        {
            var source = "abstract class T\n{\n    " + member + Next;
            check(MemberBody(source, signature, true) is null, "P1-45 MemberBody self-test: raw text with " + label + " has no body", MemberBody(source, signature, true) ?? "null");
        }
        var commented = "class T\n{\n    // public string? Send(string line) { return line; }\n    public string? Send(string line)" + Tail + Next;
        check(MemberBody(commented, "public string? Send(", true) == "public string? Send(string line)" + Tail,
            "P1-45 MemberBody self-test: raw text whose signature is named first in a comment reads the member, not the comment",
            MemberBody(commented, "public string? Send(", true) ?? "null");

        // P1-45: a stray ')' (an unbalanced source, or a signature found mid-expression) never takes the bracket depth
        // below 0, where a later parameter list's '=>' or ';' would read as the member's own.
        const string Stray = "public string? Send(string line)) Guard(Func<int> make = () => 0)";
        var strayMember = Stray + Tail;
        var straySource = "class T\n{\n    " + strayMember + Next;
        check(MemberBody(straySource, "public string? Send(", true) == strayMember && MemberBody(StripCode(straySource), "public string? Send(") == StripCode(strayMember),
            "P1-45 MemberBody self-test: a stray ')' never takes the bracket depth below 0, so a '=>' in a later parameter list is not the member's (raw and stripped)",
            (MemberBody(straySource, "public string? Send(", true) ?? "null") + " ; " + (MemberBody(StripCode(straySource), "public string? Send(") ?? "null"));

        // P1-47: raw text skips a signature occurrence any character of which lies in a comment or a literal, not only its
        // first: in `Pick(@"x")` the '@' opens a verbatim literal, while `Pick(@string s)` is a verbatim identifier (code).
        // The body runs from the signature on, so the modifiers before it are not part of it.
        const string PickMember = "static string Pick(@string s) { return s + \"}\"; }";
        var pickSource = "class T\n{\n    static string A() { return Pick(@\"x\"); }\n    " + PickMember + "\n    static string B() { return \"\"; }\n}\n";
        check(MemberBody(pickSource, "Pick(@", true) == PickMember["static string ".Length..],
            "P1-47 MemberBody self-test: raw text skips a signature whose later character lies in a literal ('@' of @\"x\") and reads the member",
            MemberBody(pickSource, "Pick(@", true) ?? "null");
        var tailInComment = "class T\n{\n    static void G() /* note */ { }\n}\n";
        check(MemberBody(tailInComment, "G() /*", true) is null && MemberBody(tailInComment, "G() ", true) == "G() /* note */ { }",
            "P1-47 MemberBody self-test: raw text never reads a signature whose tail lies in a comment (the code part alone still reads)",
            (MemberBody(tailInComment, "G() /*", true) ?? "null") + " ; " + (MemberBody(tailInComment, "G() ", true) ?? "null"));

        // P1-49: CodeMask blanks a comment's or literal's spaces to spaces and keeps its newlines, so a signature whose
        // masked characters are only spaces or newlines read the same in the mask and in the code. The raw path compares
        // the signature against the mask marks instead: the trailing spaces of a line comment, or the spaces inside a string
        // literal, are not code (CodeMask blanks any other masked character to a space, so only spaces and newlines
        // matched). Control: the newline that ends a line comment is code, so a signature starting there reads.
        const string HMember = "static void H() { }";
        var afterLineComment = "class T\n{\n    int x; // note  \n    " + HMember + "\n}\n";
        var afterLineCommentPlain = "class T\n{\n    int x; // note\n    " + HMember + "\n}\n";
        var spacesInLiteral = "class T\n{\n[Note(\"  \")] static void P() { }\n}\n";
        check(MemberBody(afterLineComment, "  \n    static void H(", true) is null && MemberBody(afterLineComment, " \n    static void H(", true) is null
            && MemberBody(afterLineCommentPlain, "\n    static void H(", true) == "\n    " + HMember && MemberBody(afterLineComment, "static void H(", true) == HMember,
            "P1-49 MemberBody self-test: raw text skips a signature whose characters in a line comment are only spaces (compared against the mask marks), and a signature from the comment's ending newline on still reads",
            (MemberBody(afterLineComment, "  \n    static void H(", true) ?? "null") + " ; " + (MemberBody(afterLineCommentPlain, "\n    static void H(", true) ?? "null"));
        var newlineInBlock = "class T\n{\n/* a \n b */ static void Q() { }\n}\n";
        check(MemberBody(spacesInLiteral, "  ", true) is null && MemberBody(newlineInBlock, " \n ", true) is null,
            "P1-49 MemberBody self-test: raw text skips a signature of spaces inside a string literal, or of spaces and a newline inside a block comment (masked, though blanked to themselves)",
            (MemberBody(spacesInLiteral, "  ", true) ?? "null") + " ; " + (MemberBody(newlineInBlock, " \n ", true) ?? "null"));

        // P1-47: on members with interpolated literals the raw path and the stripped path read the same member:
        // StripCode(MemberBody(raw)) == MemberBody(StripCode(source)). (The paths stay two: CodeMask cannot run on
        // StripCode output, whose interpolated literals have lost their '$'.)
        var interpolated = new (string Label, string Member)[]
        {
            ("a hole holding a call and a format", "static string I1(int x) { return $\"{F(x):N2} ({x})\"; }"),
            ("escaped braces and a hole holding a brace literal", "static string I2(bool b) { return $\"{{a}} {(b ? \"}\" : \"{\")} }}\"; }"),
            ("a verbatim interpolated literal over lines", "static string I3(int x) { return $@\"a {x}\n)}}{{ {Wrap(x)}\"; }"),
            ("a raw interpolated literal with two-dollar holes", "static string I4(int y) { return $$\"\"\"{ {{y}} }( {{G(() => { return y; })}}\"\"\"; }"),
            ("a hole holding a lambda block", "static string I5(int z) { var s = $\"{H(() => { if (z > 0) { return \"}\"; } return \"{\"; })}\"; return s; }"),
        };
        foreach (var (label, member) in interpolated)
        {
            var source = "class T\n{\n    " + member + "\n    static string After() { return \"\"; }\n}\n";
            var signature = member[..(member.IndexOf('(') + 1)];
            var raw = MemberBody(source, signature, true);
            var stripped = MemberBody(StripCode(source), signature);
            check(raw == member && stripped is not null && StripCode(raw) == stripped,
                "P1-47 MemberBody self-test: raw and stripped paths read the same member with " + label,
                (raw ?? "null") + " ; " + (stripped ?? "null"));
        }
    }

    // ---- 2i. P1-38: the deadline transport's configured transport, the core read-timeout helper, the test helpers ----
    // (a) A static caller scan (comment- and literal-stripped, every .cs file outside bin, obj and dot directories) lists
    // every file naming RevitCommandService.PipeTransportWithReadTimeout. It is KEPT: production code calls it (the CLI
    // --read-timeout-seconds transport, interfaces/cli/RevitCommandOptions.cs, and the MCP server read timeout,
    // interfaces/mcp/McpServer.cs), and that exact production caller set is the pinned reason; RevitDeadlineTransport.cs
    // is not among them. (b) The CLI builds its RevitDeadlineTransport (always cutReads true, every page through
    // connectThenRead) with a null configured transport instead of one it never calls; the constructor refuses a null
    // transport only with cutReads false (ArgumentNullException naming transport), and a null-transport listing within
    // its deadline is byte-equal to RevitServiceTransport paging. (c) The Legacy adapter given a cut connect it does not
    // model fails a named Check and answers no_response (null) instead of throwing out of Run. (d) StripComments reads
    // @$"..." as verbatim and a raw string literal ("""...""", any $ prefix, any quote run) as one literal, so a // inside
    // it is no comment and code after it is not hidden (mutation self-test).
    // P1-40: (a) scans the tracked .cs files (git ls-files), so an untracked copy in an untracked non-dot folder adds no
    // caller (control: the directory walk finds the copy, the scan does not), and pins no test reference (the helper is
    // kept for its production callers alone). (b) is read from the parsed argument list (positional by position, named by
    // name, whitespace ignored; self-test on formatting and named-argument variants and mutants). (e) RevitDeadlineTransport
    // uses its configured transport only on the cutReads false return (static, and a counting cutReads true spy is never
    // called), so McpServer.RevitCallAll passes realPipe ? null : revitTransport with cutReads: realPipe.
    // P1-42: the untracked-copy control runs in a scratch git repository under the temp folder (outside the root or
    // under a gitignored folder, pinned); git on PATH is required and named in the failure text; the argument map is
    // C#'s (positional after in-position named arguments, CS8323 and empty arguments refused by name) over the
    // constructor's own parameters (reflection); section 3a reads McpServer's construction through it too.
    static readonly string[] ReadTimeoutHelperProductionCallers = { "interfaces/cli/RevitCommandOptions.cs", "interfaces/mcp/McpServer.cs" };
    const string ReadTimeoutHelperDefinition = "core/Bim.Core/RevitCommandsService.cs";
    const string LegacyUnmodelled = "P1-38 Legacy adapter: every cut connect a fake meets is modelled by that fake";

    // P1-40: the .cs files the 2i caller scan reads: the tracked ones (`git -C root ls-files -z -- *.cs`, present on
    // disk), relative with '/' separators, sorted; null with the reason when git cannot list them (the pin then fails).
    // P1-42: the scan needs git on PATH (docs/agent/ENVIRONMENT.md); the reason says so.
    const string GitRequired = "the 2i caller scan needs git on PATH (docs/agent/ENVIRONMENT.md): ";
    static List<string>? ScannedSourceFiles(string root, out string reason)
    {
        reason = "";
        var git = GitRun(root, "ls-files", "-z", "--", "*.cs");
        if (git.Exit != 0) { reason = GitRequired + "git ls-files exit " + N(git.Exit) + ": " + git.Error; return null; }
        var files = git.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(file => file.Replace('\\', '/'))
            .Where(file => File.Exists(Path.Combine(root, file))).ToList();
        files.Sort(StringComparer.Ordinal);
        if (files.Count == 0) reason = GitRequired + "git ls-files listed no .cs file";
        return files.Count == 0 ? null : files;
    }

    // Every .cs file under root on disk (tracked or not), relative with '/' separators: the P1-38 walk, kept as the
    // control that proves an untracked copy exists on disk.
    static IEnumerable<string> WalkedSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'));

    // P1-42: runs git in dir (the 2i scan and its control need git on PATH, docs/agent/ENVIRONMENT.md); exit -1 with
    // the reason when git cannot start.
    static (int Exit, string Out, string Error) GitRun(string dir, params string[] arguments) => GitRunWith("git", dir, arguments);

    // P1-44: GitRun with the git executable named (the 'git missing' self-test names one that does not exist).
    static (int Exit, string Out, string Error) GitRunWith(string gitExecutable, string dir, string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(gitExecutable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(dir);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var git = Process.Start(start);
            if (git is null) return (-1, "", "git did not start");
            var stderr = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return (git.ExitCode, output, stderr.Result.Trim());
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "", "git not started: " + error.Message);
        }
    }

    // P1-42: why path is no safe place for the 2i scan control (empty: outside the repository root, or inside it only
    // under a gitignored folder, so a crash cannot leave a visible untracked file in the root).
    // P1-44: root and path are compared with their symbolic links resolved (RealPath: a temp folder reached through a
    // link to the root, or a root reached through a link, is still inside it); a git that cannot start is named
    // 'git missing', apart from a path git reports as not gitignored (exit 1) and any other check-ignore failure.
    // P1-46: every message names git's exit code (the not-gitignored one too).
    static string ControlLocationProblem(string root, string path, string gitExecutable = "git")
    {
        var realRoot = RealPath(root);
        var relative = Path.GetRelativePath(realRoot, RealPath(path)).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return "";
        var ignored = GitRunWith(gitExecutable, realRoot, new[] { "check-ignore", "-q", "--", relative });
        var error = ignored.Error.Length > 0 ? ", " + ignored.Error : "";
        return ignored.Exit switch
        {
            0 => "",
            1 => "inside the repository root and not gitignored (git check-ignore exit 1): " + relative,
            -1 => "git missing (" + ignored.Error + "): cannot tell whether " + relative + " inside the repository root is gitignored; " + GitRequired.TrimEnd(' ', ':'),
            _ => "git check-ignore failed (exit " + N(ignored.Exit) + error + "): cannot tell whether " + relative + " inside the repository root is gitignored",
        };
    }

    // P1-44: path made absolute with every symbolic link among its existing ancestors (and itself) resolved, the part
    // that does not exist yet appended unchanged (at most 40 link hops).
    static string RealPath(string path, int hops = 0)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full) ?? "";
        var parts = full[current.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (var k = 0; k < parts.Length; k++)
        {
            var next = Path.Combine(current, parts[k]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            string? link = null;
            try { link = info.LinkTarget; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (!info.Exists && link is null) return Path.Combine(new[] { next }.Concat(parts[(k + 1)..]).ToArray());
            FileSystemInfo? target = null;
            try { target = link is null || hops >= 40 ? null : info.ResolveLinkTarget(returnFinalTarget: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            current = target is null ? next : RealPath(target.FullName, hops + 1);
        }
        return current;
    }

    // P1-46: the outcome of the symbolic-link self-test, recorded either way. A created link passes only when the
    // resolution checks hold. A link that could not be created fails off Windows; on Windows (which may refuse a link
    // without the privilege) it passes with a ::warning:: line naming the skip reason, so the skip is never silent. A
    // warning, not a notice: this step's notice budget is spent by other gates (P1-45, VisibilitySelfTest (b)).
    static (bool Pass, string? Warning, string Record) LinkOutcome(bool created, bool resolved, bool windows, string reason)
    {
        if (created) return (resolved, null, "symbolic link created; resolution " + (resolved ? "exercised" : "FAILED"));
        var record = "symbolic link not created (" + reason.ReplaceLineEndings(" ") + ")";
        // P1-48: the warning line escapes the reason as written (% CR LF, CliInputsSummary.Line's helper); the record
        // (check names and details) keeps it on one line.
        return windows ? (true, "::warning::cli-inputs P1-46 " + SummaryEscape("symbolic link not created (" + reason + ")") + "; the symlink resolution of ControlLocationProblem is not exercised on this Windows runner", record) : (false, null, record);
    }

    // P1-48: the % CR LF escape of CliInputsSummary.Line (tests/cli/Program.cs, its private Escape), read by reflection so
    // this file keeps no copy; null when the method is gone (the P1-48 self-test then fails by name).
    static readonly Func<string, string>? SummaryEscapeMethod =
        typeof(CliInputsSummary).GetMethod("Escape", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, null, new[] { typeof(string) }, null) is { } escape
        && escape.ReturnType == typeof(string) ? escape.CreateDelegate<Func<string, string>>() : null;
    static string SummaryEscape(string text) => SummaryEscapeMethod is { } escape ? escape(text)
        : throw new InvalidOperationException("P1-48: CliInputsSummary.Escape(string) is not found in tests/cli/Program.cs");

    // P1-48: GitHub Actions shows at most this many ::warning:: annotations per step (later ones stay in the log only).
    const int WarningsPerStep = 10;
    const string LinkPassCheck = "check(link.Pass, \"P1-44 control location self-test: a symbolic link between the temp folder and the root is resolved (a path inside the root through a link is refused, one beside it accepted)\", link.Record + \"; \" + detail);";

    // P1-48: the link pass is asserted once: the P1-44 check keeps its name and carries the P1-46 record in its detail
    // (StripCode output: a check(link.Pass, call in a literal is not counted). "" when it holds.
    static string LinkPassProblems(string source)
    {
        var text = source.ReplaceLineEndings("\n");
        var calls = System.Text.RegularExpressions.Regex.Matches(StripCode(text), @"\bcheck\s*\(\s*link\s*\.\s*Pass\s*,").Count;
        var named = CountOf(DeadlineSourceScan.CommentsBlanked(text), LinkPassCheck);
        return (calls == 1 ? "" : N(calls) + " check(link.Pass, calls; ") + (named == 1 ? "" : N(named) + " P1-44 link checks with the record in the detail");
    }

    static int CountOf(string text, string token)
    {
        var count = 0;
        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // P1-48: LinkOutcome escapes its warning text with SummaryEscape (CliInputsSummary's helper), no replace of its own.
    const string LinkOutcomeSignature = "static (bool Pass, string? Warning, string Record) LinkOutcome(";
    static string LinkEscapeProblems(string source)
    {
        var body = MemberBody(StripCode(source.ReplaceLineEndings("\n")), LinkOutcomeSignature);
        if (body is null) return "no LinkOutcome block";
        return (body.Contains("SummaryEscape(", StringComparison.Ordinal) ? "" : "no SummaryEscape( call; ")
            + (body.Contains(".Replace(", StringComparison.Ordinal) ? "an own .Replace( escape" : "");
    }

    // P1-48: the warning budget of the step that runs cli-inputs (scripts/verify.ps1; the macOS job runs tests/cli alone).
    // GitHub Actions keeps the first WarningsPerStep warning annotations of a step. Only tests/cli and tests/mirror-journal
    // print ::warning:: lines, and verify.ps1 runs mirror-journal after cli-inputs, so no other gate's warning comes first.
    // In cli-inputs this file writes three lines (VisibilitySelfTest's Windows runtime warning, the first thing RunSteps
    // does; ControlLocationSelfTest's P1-46 link-skip warning, Windows only; Run's FailSummary at its end) and runs
    // before DeletedCwdPathTests (P10-30/P10-32 probes, one per failing verb or mode) and before Program.cs's step-wide
    // CliInputsSummary line (its last line, P1-45/P1-47). So: a green run prints at most 2 warnings from here plus
    // mirror-journal's (its sites), within the budget; in a failing run the deadline summary is at most the 3rd warning
    // of the step, and the step summary comes after the failing probes (stated, not counted: one per failing verb or
    // mode), every FAIL line staying in the log. warningCounts maps a repository path ('/') to its count of ::warning::
    // in code and literals (comments blanked); "" when the budget holds.
    static string WarningBudgetProblems(IReadOnlyDictionary<string, int> warningCounts, string verify, string cliProgram, string self)
    {
        var problems = new List<string>();
        foreach (var (path, _) in warningCounts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            if (path is not ("tests/cli/Program.cs" or "tests/cli/DeletedCwdPathTests.cs" or "tests/cli/RevitCommandDeadlineTests.cs") && !path.StartsWith("tests/mirror-journal/", StringComparison.Ordinal))
                problems.Add("a ::warning:: source outside cli-inputs and mirror-journal: " + path);
        var cliInputs = verify.IndexOf("Invoke-EvidenceCommand cli-inputs ", StringComparison.Ordinal);
        var mirror = verify.IndexOf("Invoke-EvidenceCommand mirror-journal-tests ", StringComparison.Ordinal);
        if (cliInputs < 0 || mirror < 0 || mirror < cliInputs) problems.Add("verify.ps1 does not run mirror-journal-tests after cli-inputs");
        var program = StripCode(cliProgram.ReplaceLineEndings("\n"));
        var deadline = program.IndexOf("RevitCommandDeadlineTests.Run(", StringComparison.Ordinal);
        var deleted = program.IndexOf("DeletedCwdPathTests.Run(", StringComparison.Ordinal);
        if (deadline < 0 || deleted < 0 || deleted < deadline) problems.Add("tests/cli/Program.cs does not run RevitCommandDeadlineTests before DeletedCwdPathTests");
        var code = StripCode(self.ReplaceLineEndings("\n"));
        var writes = System.Text.RegularExpressions.Regex.Matches(code, @"\bConsole\s*\.\s*(?:(?:Out|Error)\s*\.\s*)?Write(?:Line)?\s*\(").Count;
        var flat = string.Concat(code.Where(c => !char.IsWhiteSpace(c)));
        var known = new[] { "Console.WriteLine(notice);", "Console.WriteLine(skipWarning);", "Console.WriteLine(FailSummary(names));" };
        if (writes != known.Length || known.Any(write => CountOf(flat, write) != 1))
            problems.Add(N(writes) + " console writes in RevitCommandDeadlineTests.cs, not the runtime warning, the link-skip warning and FailSummary once each");
        var mirrorSites = warningCounts.Where(entry => entry.Key.StartsWith("tests/mirror-journal/", StringComparison.Ordinal)).Sum(entry => entry.Value);
        var ownBeforeSummary = known.Length - 1; // the runtime and link-skip warnings, both before FailSummary
        if (ownBeforeSummary + mirrorSites > WarningsPerStep) problems.Add("a green run can print " + N(ownBeforeSummary + mirrorSites) + " warnings, over " + N(WarningsPerStep));
        if (ownBeforeSummary + 1 > WarningsPerStep) problems.Add("the deadline summary is past warning " + N(WarningsPerStep));
        return string.Join("; ", problems);
    }

    // P1-48: the counts WarningBudgetProblems reads: every .cs/.ps1/.psm1 file of the repository (no dot, bin or obj
    // folder) with ::warning:: outside its comments (C# comments blanked; a PowerShell line starting with '#' skipped).
    static Dictionary<string, int> WarningCounts(string root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var full = Path.GetFullPath(root);
        foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var relative = Path.GetRelativePath(full, file).Replace('\\', '/');
            if (relative.Split('/').SkipLast(1).Any(part => part.StartsWith('.') || part is "bin" or "obj")) continue;
            var extension = Path.GetExtension(file);
            string text;
            if (extension == ".cs") text = DeadlineSourceScan.CommentsBlanked(File.ReadAllText(file).ReplaceLineEndings("\n"));
            else if (extension is ".ps1" or ".psm1") text = string.Join("\n", File.ReadAllText(file).ReplaceLineEndings("\n").Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
            else continue;
            var count = CountOf(text, "::warning::");
            if (count > 0) counts[relative] = count;
        }
        return counts;
    }

    // P1-48: the link warning escape, the single link-pass assertion and the step's warning budget (static, self-tests).
    static void LinkWarningPinned(string root, Action<bool, string, string> check)
    {
        const string Reason = "IOException: 50% done\r\nsecond\rthird\nfourth";
        var skip = LinkOutcome(false, false, true, Reason).Warning ?? "";
        check(SummaryEscapeMethod is not null && skip == "::warning::cli-inputs P1-46 symbolic link not created (IOException: 50%25 done%0D%0Asecond%0Dthird%0Afourth); the symlink resolution of ControlLocationProblem is not exercised on this Windows runner",
            "P1-48 link outcome self-test: the ::warning:: line escapes % CR LF (the link-skip reason kept, not flattened)", skip);
        var summary = CliInputsSummary.Line(new[] { Reason });
        check(SummaryEscapeMethod is not null && summary == "::warning::" + CliInputsSummary.Title + ": 1 FAIL: #1 " + SummaryEscape(Reason) && skip.Contains(SummaryEscape("(" + Reason + ")"), StringComparison.Ordinal),
            "P1-48 link outcome self-test: the escape is CliInputsSummary.Line's own helper (its Escape, read by reflection)", summary + " | " + skip);

        var selfPath = Path.Combine(root, "tests", "cli", "RevitCommandDeadlineTests.cs");
        var self = File.Exists(selfPath) ? File.ReadAllText(selfPath) : "";
        var escapeProblems = self.Length == 0 ? "not read" : LinkEscapeProblems(self);
        check(escapeProblems.Length == 0, "P1-48 static: LinkOutcome escapes its warning with SummaryEscape and no replace of its own", escapeProblems);
        const string CleanOutcome = "static class F\n{\n    static (bool Pass, string? Warning, string Record) LinkOutcome(bool created, string reason)\n    {\n"
            + "        var record = \"not created (\" + reason.ReplaceLineEndings(\" \") + \")\";\n        return (true, \"::warning::x \" + SummaryEscape(\"(\" + reason + \")\"), record);\n    }\n}\n";
        check(LinkEscapeProblems(CleanOutcome).Length == 0, "P1-48 link escape mutation self-test: SummaryEscape in LinkOutcome passes", LinkEscapeProblems(CleanOutcome));
        foreach (var (label, source) in new[]
        {
            ("an own .Replace( escape", CleanOutcome.Replace("SummaryEscape(\"(\" + reason + \")\")", "(\"(\" + reason + \")\").Replace(\"%\", \"%25\")", StringComparison.Ordinal)),
            ("SummaryEscape only in a comment", CleanOutcome.Replace("SummaryEscape(\"(\" + reason + \")\")", "record /* SummaryEscape( */", StringComparison.Ordinal)),
        })
            check(source != CleanOutcome && LinkEscapeProblems(source).Length > 0, "P1-48 link escape mutation self-test: " + label + " is flagged", "no problem flagged");

        var passProblems = self.Length == 0 ? "not read" : LinkPassProblems(self);
        check(passProblems.Length == 0, "P1-48 static: the link pass is asserted once (the P1-44 name kept, the P1-46 record in its detail)", passProblems);
        const string CleanPass = "static class F\n{\n    static void T(Action<bool, string, string> check)\n    {\n        " + LinkPassCheck + "\n    }\n}\n";
        check(LinkPassProblems(CleanPass).Length == 0, "P1-48 link pass mutation self-test: one P1-44 check passes", LinkPassProblems(CleanPass));
        foreach (var (label, source) in new[]
        {
            ("a second check(link.Pass, assertion", CleanPass.Replace(LinkPassCheck, LinkPassCheck + "\n        check(link.Pass, \"P1-46 control location self-test: \" + link.Record, detail);", StringComparison.Ordinal)),
            ("the P1-44 name changed", CleanPass.Replace("P1-44 control location", "P1-48 control location", StringComparison.Ordinal)),
            ("the record dropped from the detail", CleanPass.Replace("link.Record + \"; \" + detail", "detail", StringComparison.Ordinal)),
        })
            check(source != CleanPass && LinkPassProblems(source).Length > 0, "P1-48 link pass mutation self-test: " + label + " is flagged", "no problem flagged");

        var verifyPath = Path.Combine(root, "scripts", "verify.ps1");
        var programPath = Path.Combine(root, "tests", "cli", "Program.cs");
        var verify = File.Exists(verifyPath) ? File.ReadAllText(verifyPath).ReplaceLineEndings("\n") : "";
        var cliProgram = File.Exists(programPath) ? File.ReadAllText(programPath) : "";
        var counts = WarningCounts(root);
        var budget = WarningBudgetProblems(counts, verify, cliProgram, self);
        check(budget.Length == 0, "P1-48 static: the step's ::warning:: lines fit GitHub's " + N(WarningsPerStep) + " per step (the link-skip warning and the deadline summary come before any other gate's)",
            budget + " | " + string.Join(", ", counts.Select(entry => entry.Key + " " + N(entry.Value))));
        foreach (var (label, mutantCounts, mutantVerify, mutantProgram, mutantSelf) in new (string, IReadOnlyDictionary<string, int>, string, string, string)[]
        {
            ("a ::warning:: in tests/mcp (a gate run before cli-inputs)", new Dictionary<string, int>(counts) { ["tests/mcp/Program.cs"] = 1 }, verify, cliProgram, self),
            ("mirror-journal run before cli-inputs", counts, verify.Replace("Invoke-EvidenceCommand cli-inputs ", "Invoke-EvidenceCommand cli-inputs-moved ", StringComparison.Ordinal) + "\nInvoke-EvidenceCommand cli-inputs x\n", cliProgram, self),
            ("DeletedCwdPathTests run before the deadline tests", counts, verify, cliProgram.Replace("RevitCommandDeadlineTests.Run(", "RevitCommandDeadlineTests.Later(", StringComparison.Ordinal) + "\nRevitCommandDeadlineTests.Run(root);\n", self),
            ("a fourth console write here", counts, verify, cliProgram, self + "\nstatic class G { static void W() => Console.WriteLine(\"::warning::extra\"); }\n"),
            ("nine mirror-journal warning sites", new Dictionary<string, int>(counts) { ["tests/mirror-journal/Extra.cs"] = 9 }, verify, cliProgram, self),
        })
            check(WarningBudgetProblems(mutantCounts, mutantVerify, mutantProgram, mutantSelf).Length > 0, "P1-48 warning budget mutation self-test: " + label + " is flagged", "no problem flagged");
    }

    // P1-44: ControlLocationProblem names a missing git apart from a path that is not gitignored, and resolves symlinks
    // between the temp folder and the root (a path reached through a link to the root is inside it). Self-test in a
    // scratch repository under the temp folder: real/repo (git init) and alias -> real.
    static void ControlLocationSelfTest(string root, Action<bool, string, string> check)
    {
        var missing = ControlLocationProblem(root, Path.Combine(root, "p1-44-visible", "Copy.cs"), "git-p1-44-missing-" + Guid.NewGuid().ToString("N"));
        check(missing.StartsWith("git missing", StringComparison.Ordinal) && !missing.Contains("not gitignored", StringComparison.Ordinal),
            "P1-44 control location self-test: a missing git is named 'git missing', not 'not gitignored'", missing);
        var visible = ControlLocationProblem(root, Path.Combine(root, "p1-44-visible", "Copy.cs"));
        check(visible.Contains("not gitignored", StringComparison.Ordinal) && !visible.StartsWith("git missing", StringComparison.Ordinal),
            "P1-44 control location self-test: a visible root folder is named 'not gitignored'", visible);

        // P1-46: the not-gitignored message keeps git's exit code (1), as the other failures do.
        check(visible.StartsWith("inside the repository root and not gitignored (git check-ignore exit 1): ", StringComparison.Ordinal),
            "P1-46 control location self-test: the not-gitignored message keeps the git check-ignore exit code", visible);

        // P1-46: a root that is not a repository makes git check-ignore fail (exit 128, a 'fatal:' line): named
        // 'check-ignore failed' with git's exit code, not 'not gitignored'. The scratch root sits under the temp folder,
        // which verify.ps1 points inside the repository (.bim/verification/tmp, gitignored), so git would find the outer
        // repository: a .git file naming a missing git directory stops git's discovery at the scratch root.
        var notRepository = Path.Combine(Path.GetTempPath(), "p1-46-not-a-repository-" + Guid.NewGuid().ToString("N"));
        string failed;
        string probe;
        try
        {
            Directory.CreateDirectory(notRepository);
            File.WriteAllText(Path.Combine(notRepository, ".git"), "gitdir: " + Path.Combine(notRepository, "missing").Replace('\\', '/') + "\n");
            var revParse = GitRun(notRepository, "rev-parse", "--git-dir");
            probe = "git rev-parse exit " + N(revParse.Exit) + (revParse.Error.Length > 0 ? " (" + revParse.Error + ")" : "");
            failed = ControlLocationProblem(notRepository, Path.Combine(notRepository, "visible", "Copy.cs"));
        }
        finally
        {
            try { if (Directory.Exists(notRepository)) Directory.Delete(notRepository, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        // git's wording differs by version ('not a git repository' on Linux git 2.43, 'gitfile does not point to a valid
        // repository' on the macOS runner's git, red run 45618724063): the exit code and git's own 'fatal:' line are pinned.
        check(failed.StartsWith("git check-ignore failed (exit 128, fatal: ", StringComparison.Ordinal) && failed.Contains("cannot tell whether visible/Copy.cs", StringComparison.Ordinal)
            && !failed.Contains("not gitignored", StringComparison.Ordinal) && !failed.StartsWith("git missing", StringComparison.Ordinal),
            "P1-46 control location self-test: a root that is not a repository is named 'check-ignore failed' with git's exit code (128), not 'not gitignored' or 'git missing'",
            failed + " | " + probe);
        check(!Directory.Exists(notRepository), "P1-46 control location self-test: the not-a-repository scratch folder is removed", notRepository);

        // P1-46: the symbolic-link outcome rule itself (LinkOutcome): no silent pass.
        var outcomes = new (string Label, bool Created, bool Resolved, bool Windows, bool Pass, bool Line)[]
        {
            ("created and resolved", true, true, false, true, false),
            ("created and resolved on Windows", true, true, true, true, false),
            ("created, not resolved", true, false, true, false, false),
            ("not created off Windows", false, false, false, false, false),
            ("not created on Windows", false, false, true, true, true),
        };
        foreach (var (label, created, resolved, windows, pass, line) in outcomes)
        {
            var outcome = LinkOutcome(created, resolved, windows, "IOException: P1-46 synthetic reason\r\nsecond line");
            check(outcome.Pass == pass && (outcome.Warning is not null) == line
                && (outcome.Warning is not { } warningLine || (warningLine.StartsWith("::warning::", StringComparison.Ordinal) && !warningLine.Contains('\n') && !warningLine.Contains('\r')
                    && warningLine.Contains("not created", StringComparison.Ordinal) && warningLine.Contains("IOException: P1-46 synthetic reason", StringComparison.Ordinal)))
                && outcome.Record.Contains(created ? "link created" : "link not created", StringComparison.Ordinal),
                "P1-46 link outcome self-test: " + label + (pass ? " passes" : " fails") + (line ? " with a ::warning:: line naming the skip reason" : ""),
                "pass " + outcome.Pass + "; warning " + (outcome.Warning ?? "none") + "; record " + outcome.Record);
        }

        var scratch = Path.Combine(Path.GetTempPath(), "p1-44-link-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(scratch, "real");
        var repo = Path.Combine(real, "repo");
        var alias = Path.Combine(scratch, "alias");
        var linkCreated = false;
        var resolvedThroughLink = false;
        var skipReason = "";
        string detail;
        try
        {
            Directory.CreateDirectory(repo);
            var init = GitRun(repo, "init", "-q");
            Directory.CreateSymbolicLink(alias, real);
            linkCreated = true;
            var aliasRoot = ControlLocationProblem(Path.Combine(alias, "repo"), Path.Combine(repo, "visible", "Copy.cs"));
            var aliasPath = ControlLocationProblem(repo, Path.Combine(alias, "repo", "visible", "Copy.cs"));
            var beside = ControlLocationProblem(Path.Combine(alias, "repo"), Path.Combine(real, "beside", "Copy.cs"));
            resolvedThroughLink = init.Exit == 0 && aliasRoot.Contains("not gitignored", StringComparison.Ordinal) && aliasPath.Contains("not gitignored", StringComparison.Ordinal) && beside.Length == 0;
            detail = "git init exit " + N(init.Exit) + "; root via link: " + (aliasRoot.Length > 0 ? aliasRoot : "accepted") + "; path via link: " + (aliasPath.Length > 0 ? aliasPath : "accepted")
                + "; beside via link: " + (beside.Length > 0 ? beside : "accepted");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows may refuse a symbolic link without the privilege; the resolution is then not exercised there
            // (P1-46: recorded and written as a warning by LinkOutcome, never a silent pass).
            skipReason = error.GetType().Name + ": " + error.Message;
            detail = (linkCreated ? "link created, then " : "no symbolic link (") + skipReason + (linkCreated ? "" : ")");
        }
        finally
        {
            try
            {
                if (Directory.Exists(alias) || File.Exists(alias))
                {
                    if (OperatingSystem.IsWindows()) Directory.Delete(alias);
                    else File.Delete(alias);
                }
                if (Directory.Exists(scratch))
                {
                    foreach (var file in Directory.EnumerateFiles(scratch, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        var link = LinkOutcome(linkCreated, resolvedThroughLink, OperatingSystem.IsWindows(), skipReason);
        if (link.Warning is { } skipWarning) Console.WriteLine(skipWarning);
        // P1-48: asserted once, under the P1-44 name; the P1-46 record (link created or not, and why) is in its detail.
        check(link.Pass, "P1-44 control location self-test: a symbolic link between the temp folder and the root is resolved (a path inside the root through a link is refused, one beside it accepted)", link.Record + "; " + detail);
        check(!Directory.Exists(scratch), "P1-44 control location self-test: the scratch link folder is removed", scratch);
        LinkWarningPinned(root, check);
    }

    // P1-44: the argument-mapping parser (with StripCode and MemberBody) exists once, in tests/cli/DeadlineSourceScan.cs:
    // Bim.Cli.Tests compiles it from its own folder (default items, nothing removed) and Bim.Mcp.Tests links it; neither
    // test keeps a copy (no StripCodeInto or ArgumentsAt here, no depth-0 argument split in tests/mcp/Program.cs).
    const string SharedScanFile = "tests/cli/DeadlineSourceScan.cs";
    static void SharedParserPinned(string root, Action<bool, string, string> check)
    {
        var shared = Path.GetFullPath(Path.Combine(root, "tests", "cli", "DeadlineSourceScan.cs"));
        bool Includes(string project, string attribute)
        {
            var dir = Path.GetDirectoryName(project)!;
            return File.Exists(project) && System.Xml.Linq.XDocument.Load(project).Descendants().Where(element => element.Name.LocalName == "Compile")
                .Select(element => (string?)element.Attribute(attribute)).Where(value => value is not null)
                .Any(value => string.Equals(Path.GetFullPath(Path.Combine(dir, value!.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar))), shared,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        var cliProject = Path.Combine(root, "tests", "cli", "Bim.Cli.Tests.csproj");
        var mcpProject = Path.Combine(root, "tests", "mcp", "Bim.Mcp.Tests.csproj");
        var cliText = File.Exists(cliProject) ? File.ReadAllText(cliProject) : "";
        check(File.Exists(shared) && !cliText.Contains("EnableDefaultCompileItems", StringComparison.Ordinal) && !Includes(cliProject, "Remove") && Includes(mcpProject, "Include"),
            "P1-44 the deadline argument parser exists once: " + SharedScanFile + " is compiled by Bim.Cli.Tests (default items) and linked by Bim.Mcp.Tests",
            (File.Exists(shared) ? "present" : "missing") + "; mcp link " + Includes(mcpProject, "Include"));
        var selfPath = Path.Combine(root, "tests", "cli", "RevitCommandDeadlineTests.cs");
        var self = StripCode((File.Exists(selfPath) ? File.ReadAllText(selfPath) : "").ReplaceLineEndings("\n"));
        var mcpTestPath = Path.Combine(root, "tests", "mcp", "Program.cs");
        var mcpTest = StripCode((File.Exists(mcpTestPath) ? File.ReadAllText(mcpTestPath) : "").ReplaceLineEndings("\n"));
        var copies = new List<string>();
        if (System.Text.RegularExpressions.Regex.IsMatch(self, @"\bStripCodeInto\s*\(")) copies.Add("RevitCommandDeadlineTests.cs StripCodeInto");
        if (System.Text.RegularExpressions.Regex.IsMatch(self, @"\bArgumentsAt\s*\(")) copies.Add("RevitCommandDeadlineTests.cs ArgumentsAt");
        if (System.Text.RegularExpressions.Regex.IsMatch(mcpTest, @"\bdepth\s*==\s*0\b")) copies.Add("tests/mcp/Program.cs depth-0 argument split");
        if (!mcpTest.Contains("DeadlineSourceScan.", StringComparison.Ordinal)) copies.Add("tests/mcp/Program.cs does not call DeadlineSourceScan");
        check(copies.Count == 0, "P1-44 neither deadline test keeps its own copy of the strip or argument-map helpers", string.Join("; ", copies));
    }

    static List<string> ReadTimeoutHelperReferences(string root, IEnumerable<string> files)
    {
        var found = new List<string>();
        foreach (var relative in files)
        {
            if (relative.Split('/').Any(part => part is "bin" or "obj" || part.StartsWith('.'))) continue;
            var code = StripCode(File.ReadAllText(Path.Combine(root, relative)).ReplaceLineEndings("\n"));
            if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bPipeTransportWithReadTimeout\b")) found.Add(relative);
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    // P1-40: RevitDeadlineTransport's constructor parameters, in order (the parsed argument list maps calls onto them).
    static readonly string[] DeadlineTransportParameters = { "pipeName", "policy", "transport", "readTimeout", "deadline", "elapsed", "listing", "cutReads", "probe", "connectThenRead" };

    // P1-40/P1-42: every construction in stripped code mapped onto DeadlineTransportParameters as C# does (P1-44: the
    // parser lives once in tests/cli/DeadlineSourceScan.cs, shared with tests/mcp).
    static List<(Dictionary<string, string>? Map, string Reason)> DeadlineTransportCalls(string code) => DeadlineSourceScan.Calls(code, DeadlineTransportParameters);

    // P1-40: what is wrong with the CLI's deadline transport construction in stripped Program.cs code (empty: one
    // construction, exactly pipeName, policy, transport null, readTimeout revitOptions.ReadTimeout and deadline).
    static string[] CliTransportProblems(string code)
    {
        var calls = DeadlineTransportCalls(code);
        if (calls.Count != 1) return new[] { N(calls.Count) + " RevitDeadlineTransport constructions, not one" };
        if (calls[0].Map is not { } arguments) return new[] { "the construction's arguments do not map onto the constructor parameters: " + calls[0].Reason };
        var problems = new List<string>();
        var names = arguments.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(new[] { "deadline", "pipeName", "policy", "readTimeout", "transport" })) problems.Add("parameters " + string.Join(", ", names));
        var transport = arguments.TryGetValue("transport", out var t) ? t : null;
        if (transport != "null") problems.Add("transport " + (transport ?? "absent"));
        var readTimeout = arguments.TryGetValue("readTimeout", out var r) ? r : null;
        if (readTimeout != "revitOptions.ReadTimeout") problems.Add("readTimeout " + (readTimeout ?? "absent"));
        return problems.ToArray();
    }

    // P1-42 (section 3a): what is wrong with McpServer's deadline transport construction in its source (empty: one
    // construction in the comment- and literal-stripped code, cutReads realPipe, no connectThenRead override and no
    // argument naming a connect bound or transport of its own), read from the parsed argument list.
    // P1-44: only a construction inside RevitCallAll's body (MemberBody on the stripped code) is the call; any other is
    // named as outside RevitCallAll, never counted as it.
    static string[] McpTransportProblems(string source)
    {
        var code = StripCode(source.ReplaceLineEndings("\n"));
        var body = MemberBody(code, "(string Text, bool IsError) RevitCallAll(");
        if (body is null) return new[] { "no RevitCallAll block body" };
        var calls = DeadlineTransportCalls(body);
        var outside = DeadlineTransportCalls(code).Count - calls.Count;
        var problems = new List<string>();
        if (outside != 0) problems.Add(N(outside) + " RevitDeadlineTransport constructions outside RevitCallAll (not the call)");
        if (calls.Count != 1) { problems.Add(N(calls.Count) + " RevitDeadlineTransport constructions inside RevitCallAll, not one"); return problems.ToArray(); }
        if (calls[0].Map is not { } arguments) { problems.Add("the construction's arguments do not map onto the constructor parameters: " + calls[0].Reason); return problems.ToArray(); }
        var cut = arguments.TryGetValue("cutReads", out var c) ? c : null;
        if (cut != "realPipe") problems.Add("cutReads " + (cut ?? "absent"));
        if (arguments.ContainsKey("connectThenRead")) problems.Add("connectThenRead " + arguments["connectThenRead"]);
        foreach (var (name, value) in arguments)
            foreach (var token in new[] { "connectTransport", "ConnectTimeout", "cutTransport", "connectThenRead" })
                if (value.Contains(token, StringComparison.Ordinal)) problems.Add(name + " names " + token + ": " + value);
        return problems.ToArray();
    }

    static void ConfiguredTransportTidy(string root, string wallsText, long[] model, Action<bool, string, string> check)
    {
        // (a) PipeTransportWithReadTimeout callers, over the tracked .cs files (P1-40).
        var scanned = ScannedSourceFiles(root, out var scanReason);
        var references = scanned is null ? new List<string>() : ReadTimeoutHelperReferences(root, scanned);
        var production = references.Where(file => !file.StartsWith("tests/", StringComparison.Ordinal) && file != ReadTimeoutHelperDefinition).ToArray();
        var listed = (scanned is null ? "no tracked-file list (" + scanReason + "); " : N(scanned.Count) + " files scanned; ") + "references: " + string.Join(", ", references);
        check(references.Contains(ReadTimeoutHelperDefinition) && production.SequenceEqual(ReadTimeoutHelperProductionCallers),
            "P1-38 static: RevitCommandService.PipeTransportWithReadTimeout is kept because production calls it (exactly RevitCommandOptions.cs and McpServer.cs)", listed);
        check(!references.Contains("interfaces/cli/RevitDeadlineTransport.cs"),
            "P1-38 static: RevitDeadlineTransport.cs does not name PipeTransportWithReadTimeout (P1-40: test references are not pinned; the helper is kept for its production callers alone)", listed);

        // P1-40 control: an untracked copy naming the helper, in an untracked non-dot folder, is on disk (the walk finds
        // it) but is not scanned, so it cannot add a caller. P1-42: the control runs in a scratch git repository under the
        // temp folder (outside the root, or under the gitignored .bim when verify.ps1 points TEMP there; pinned), with one
        // tracked caller beside the untracked copy, so a crash cannot leave the copy in the root.
        var controlRoot = Path.Combine(Path.GetTempPath(), "p1-42-scan-control-" + Guid.NewGuid().ToString("N"));
        var controlLocation = ControlLocationProblem(root, controlRoot);
        check(controlLocation.Length == 0, "P1-42 control: the untracked copy is written outside the repository root or under a gitignored folder (a crash cannot leave it in the root)",
            controlLocation.Length > 0 ? controlLocation : controlRoot);
        const string TrackedCaller = "tracked/Caller.cs", UntrackedCopy = "p1-40-untracked/Copy.cs";
        var walkedReferences = new List<string>();
        List<string>? scannedReferences = null;
        var controlReason = "";
        try
        {
            foreach (var file in new[] { TrackedCaller, UntrackedCopy })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(controlRoot, file))!);
                File.WriteAllText(Path.Combine(controlRoot, file),
                    "static class Copy\n{\n    static object M() => Bim.Core.RevitCommandService.PipeTransportWithReadTimeout(System.TimeSpan.FromSeconds(1));\n}\n");
            }
            var init = GitRun(controlRoot, "init", "-q");
            var add = init.Exit == 0 ? GitRun(controlRoot, "add", "--", TrackedCaller) : init;
            if (add.Exit != 0) controlReason = GitRequired + "git init/add exit " + N(add.Exit) + ": " + add.Error;
            walkedReferences = ReadTimeoutHelperReferences(controlRoot, WalkedSourceFiles(controlRoot));
            var controlScan = add.Exit == 0 ? ScannedSourceFiles(controlRoot, out controlReason) : null;
            scannedReferences = controlScan is null ? null : ReadTimeoutHelperReferences(controlRoot, controlScan);
        }
        finally
        {
            try
            {
                // git writes its objects read-only, which Directory.Delete refuses on Windows.
                if (Directory.Exists(controlRoot))
                    foreach (var file in Directory.EnumerateFiles(controlRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                        File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(controlRoot, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        check(walkedReferences.SequenceEqual(new[] { UntrackedCopy, TrackedCaller }) && scannedReferences is not null && scannedReferences.SequenceEqual(new[] { TrackedCaller }),
            "P1-40 control: an untracked copy naming PipeTransportWithReadTimeout (untracked non-dot folder) is on disk but not scanned, so the caller set is unchanged",
            "walk: " + string.Join(", ", walkedReferences) + "; scan: " + (scannedReferences is null ? "no tracked-file list (" + controlReason + ")" : string.Join(", ", scannedReferences)));
        check(!Directory.Exists(controlRoot), "P1-40 control: the untracked copy's folder is removed", controlRoot);
        check(ControlLocationProblem(root, Path.Combine(root, "p1-42-visible", "Copy.cs")).Length > 0 && ControlLocationProblem(root, Path.Combine(root, ".bim", "p1-42", "Copy.cs")).Length == 0
            && ControlLocationProblem(root, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "p1-42-outside", "Copy.cs")).Length == 0,
            "P1-42 control location self-test: a visible root folder is refused, a gitignored .bim folder and a folder beside the root are accepted", "");
        ControlLocationSelfTest(root, check);

        // P1-42: the parameter list the argument map uses is the constructor's own (one public constructor).
        var constructors = typeof(Bim.Cli.RevitDeadlineTransport).GetConstructors();
        var constructorParameters = constructors.Length == 1 ? constructors[0].GetParameters().Select(parameter => parameter.Name ?? "").ToArray() : Array.Empty<string>();
        check(constructorParameters.SequenceEqual(DeadlineTransportParameters),
            "P1-42 the parsed-argument pins map onto RevitDeadlineTransport's one public constructor's parameters, in order",
            N(constructors.Length) + " constructors; " + string.Join(", ", constructorParameters));
        SharedParserPinned(root, check);

        // (b) The CLI passes no never-called transport (P1-40: on the parsed argument list, any formatting or naming).
        var program = StripCode(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs")).ReplaceLineEndings("\n"));
        var cliProblems = CliTransportProblems(program);
        check(cliProblems.Length == 0,
            "P1-38 static: the CLI builds its one RevitDeadlineTransport with a null configured transport (cutReads true: never called) and its configured read timeout",
            string.Join("; ", cliProblems));
        const string CliCall = "new RevitDeadlineTransport(revitPipe!,revitPolicy,null,revitOptions.ReadTimeout,revitDeadline)";
        var cliCleans = new (string Label, string Code)[]
        {
            ("today's compact call", "var t = all ? " + CliCall + " : other;\n"),
            ("one argument per line", "var t = new RevitDeadlineTransport(\n    revitPipe!,\n    revitPolicy,\n    null,\n    revitOptions.ReadTimeout,\n    revitDeadline);\n"),
            ("named transport and read timeout", "var t = new RevitDeadlineTransport(revitPipe!, revitPolicy, transport: null, readTimeout: revitOptions.ReadTimeout, deadline: revitDeadline);\n"),
            ("reordered named arguments", "var t = new RevitDeadlineTransport(pipeName: revitPipe!, deadline: revitDeadline, transport: null, policy: revitPolicy, readTimeout: revitOptions . ReadTimeout);\n"),
            ("qualified type and a space before '('", "var t = new Bim.Cli.RevitDeadlineTransport (revitPipe!, revitPolicy, null, revitOptions.ReadTimeout, revitDeadline);\n"),
            ("a comment inside the list", "var t = new RevitDeadlineTransport(revitPipe!, revitPolicy, /* never called */ null, revitOptions.ReadTimeout, // read\n    revitDeadline);\n"),
            ("P1-42 positional arguments after an in-position named one", "var t = new RevitDeadlineTransport(pipeName: revitPipe!, revitPolicy, null, revitOptions.ReadTimeout, revitDeadline);\n"),
        };
        foreach (var (label, code) in cliCleans)
        {
            var problems = CliTransportProblems(StripCode(code));
            check(problems.Length == 0, "P1-40 CLI pin self-test: " + label + " passes", string.Join("; ", problems));
        }
        var cliMutants = new (string Label, string Code)[]
        {
            ("named configured transport", "var t = new RevitDeadlineTransport(revitPipe!, revitPolicy, transport: revitOptions.Transport, readTimeout: revitOptions.ReadTimeout, deadline: revitDeadline);\n"),
            ("positional configured transport", "var t = new RevitDeadlineTransport(revitPipe!,revitPolicy,revitOptions.Transport,revitOptions.ReadTimeout,revitDeadline);\n"),
            ("cutReads false", "var t = new RevitDeadlineTransport(revitPipe!, revitPolicy, null, revitOptions.ReadTimeout, revitDeadline, cutReads: false);\n"),
            ("another named read timeout", "var t = new RevitDeadlineTransport(revitPipe!, revitPolicy, null, deadline: revitDeadline, readTimeout: TimeSpan.FromSeconds(1));\n"),
            ("a \"null\" string", "var t = new RevitDeadlineTransport(revitPipe!, revitPolicy, \"null\", revitOptions.ReadTimeout, revitDeadline);\n"),
            ("the call only in a comment", "// " + CliCall + "\nvar t = new RevitServiceTransport(revitPipe!, revitPolicy, revitOptions.Transport);\n"),
            ("two constructions", "var t = " + CliCall + ";\nvar u = " + CliCall + ";\n"),
            ("P1-42 an empty argument", "var t = new RevitDeadlineTransport(revitPipe!, , null, revitOptions.ReadTimeout, revitDeadline);\n"),
        };
        foreach (var (label, code) in cliMutants)
            check(CliTransportProblems(StripCode(code)).Length > 0, "P1-40 CLI pin self-test: " + label + " is flagged", "no problem flagged");
        var outOfPosition = CliTransportProblems(StripCode("var t = new RevitDeadlineTransport(policy: revitPolicy, revitPipe!, null, revitOptions.ReadTimeout, revitDeadline);\n"));
        check(outOfPosition.Any(problem => problem.Contains("positional argument 2 follows the out-of-position named argument 'policy'", StringComparison.Ordinal)),
            "P1-42 CLI pin self-test: a positional argument after an out-of-position named one is refused with its reason (C# CS8323)", string.Join("; ", outOfPosition));
        var emptyArgument = CliTransportProblems(StripCode("var t = new RevitDeadlineTransport(revitPipe!, , null, revitOptions.ReadTimeout, revitDeadline);\n"));
        check(emptyArgument.Any(problem => problem.Contains("argument 2 is empty", StringComparison.Ordinal)),
            "P1-42 CLI pin self-test: an empty argument is refused with its reason", string.Join("; ", emptyArgument));
        const string TidyPipe = "bim-agentic-os-2025-3838";
        string? Paging(string pipe, string line) => PageReply(line, model);
        var waits = new List<(TimeSpan Connect, TimeSpan Read)>();
        var clock = TimeSpan.Zero;
        var nullConfigured = new Bim.Cli.RevitDeadlineTransport(TidyPipe, RevitCommandPolicy.None, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), () => clock,
            connectThenRead: (wait, readAfter) => { waits.Add((wait, readAfter())); return Paging; });
        var nullListing = RevitElementQueryPaging.All(wallsText, nullConfigured).ToJson();
        var direct = RevitElementQueryPaging.All(wallsText, new RevitServiceTransport(TidyPipe, RevitCommandPolicy.None, Paging)).ToJson();
        check(nullListing == direct && waits.Count == 23 && waits.All(w => w.Connect == Bim.Cli.RevitDeadlineTransport.ConnectWait && w.Read == TimeSpan.FromSeconds(30)),
            "P1-38 a null configured transport with cutReads true lists every page through connectThenRead, byte-equal to RevitServiceTransport paging",
            N(waits.Count) + " pages; " + (nullListing.Length > 200 ? nullListing[..200] : nullListing));
        Exception? refused = null;
        try { _ = new Bim.Cli.RevitDeadlineTransport(TidyPipe, RevitCommandPolicy.None, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), () => clock, cutReads: false); }
        catch (Exception error) { refused = error; }
        check(refused is ArgumentNullException { ParamName: "transport" }, "P1-38 cutReads false (pages through the configured transport) refuses a null transport when built (ArgumentNullException transport)",
            refused is null ? "no exception" : refused.GetType().Name + ": " + refused.Message);

        // P1-40: McpServer's never-called transport. With cutReads true Send never calls its configured transport: its one
        // use is the cutReads false return (static), and a cutReads true listing whose configured transport counts and
        // throws is byte-equal to RevitServiceTransport paging with no call. McpServer.RevitCallAll passes cutReads:
        // realPipe, so it passes a configured transport only when realPipe is false (realPipe ? null : revitTransport).
        var deadlineCode = StripCode(File.ReadAllText(Path.Combine(root, "interfaces", "cli", "RevitDeadlineTransport.cs")).ReplaceLineEndings("\n"));
        var transportUses = (MemberBody(deadlineCode, "public string? Send(") ?? "").Split('\n')
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\btransport\b")).Select(line => line.Trim()).ToArray();
        check(transportUses.Length == 1 && transportUses[0].StartsWith("if (!cutReads) return ", StringComparison.Ordinal),
            "P1-40 static: RevitDeadlineTransport.Send uses its configured transport only on the cutReads false return", string.Join(" | ", transportUses));
        var configuredCalls = 0;
        string? Configured(string pipe, string line) { configuredCalls++; throw new InvalidOperationException("the configured transport was called"); }
        var spied = new Bim.Cli.RevitDeadlineTransport(TidyPipe, RevitCommandPolicy.None, Configured, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), () => clock,
            listing: Bim.Cli.RevitDeadlineTransport.McpListing, connectThenRead: (_, _) => Paging);
        var spiedListing = RevitElementQueryPaging.All(wallsText, spied).ToJson();
        check(configuredCalls == 0 && spiedListing == direct,
            "P1-40 a cutReads true deadline transport never calls its configured transport (MCP listing, byte-equal to RevitServiceTransport paging)",
            N(configuredCalls) + " configured calls; " + (spiedListing.Length > 200 ? spiedListing[..200] : spiedListing));
        var mcpCalls = DeadlineTransportCalls(StripCode(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")).ReplaceLineEndings("\n")));
        var mcpArguments = mcpCalls.Count == 1 ? mcpCalls[0].Map : null;
        var mcpTransport = mcpArguments is not null && mcpArguments.TryGetValue("transport", out var mcpT) ? mcpT : null;
        var mcpCut = mcpArguments is not null && mcpArguments.TryGetValue("cutReads", out var mcpC) ? mcpC : null;
        check(mcpCut == "realPipe" && mcpTransport == "realPipe?null:revitTransport",
            "P1-40 static: McpServer.RevitCallAll passes no configured transport when cutReads (realPipe) is true (realPipe ? null : revitTransport)",
            N(mcpCalls.Count) + " constructions; transport " + (mcpTransport ?? "absent") + "; cutReads " + (mcpCut ?? "absent"));

        // (c) The Legacy adapter's unmodelled cut connect.
        var recorded = new List<(bool Ok, string Name, string Detail)>();
        void Record(bool ok, string name, string detail) => recorded.Add((ok, name, detail));
        string? direct3 = "unset";
        Exception? thrown = null;
        try { direct3 = Legacy(Record, TimeSpan.FromSeconds(30), (_, _) => "configured", _ => (_, _) => "cut")(TimeSpan.FromSeconds(1), () => TimeSpan.FromSeconds(1))(TidyPipe, "line"); }
        catch (Exception error) { thrown = error; }
        check(thrown is null && direct3 is null && recorded.Count == 1 && !recorded[0].Ok && recorded[0].Name == LegacyUnmodelled,
            "P1-38 the Legacy adapter given an unmodelled cut connect fails the named Check '" + LegacyUnmodelled + "' and answers null instead of throwing",
            (thrown is null ? "no throw" : "threw " + thrown.GetType().Name + ": " + thrown.Message) + "; line " + (direct3 ?? "null") + "; checks "
            + string.Join(" / ", recorded.Select(r => (r.Ok ? "pass " : "fail ") + r.Name + " | " + r.Detail)));
        recorded.Clear();
        string? listing3 = null;
        thrown = null;
        try
        {
            var cutConnect = new Bim.Cli.RevitDeadlineTransport(TidyPipe, RevitCommandPolicy.None, Paging, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), () => clock,
                connectThenRead: Legacy(Record, TimeSpan.FromSeconds(30), Paging, _ => Paging));
            listing3 = RevitElementQueryPaging.All(wallsText, cutConnect).ToJson();
        }
        catch (Exception error) { thrown = error; }
        var listing3Json = listing3 is null ? null : Node(listing3);
        check(thrown is null && Code(listing3Json) == RevitCommandCodes.NoResponse && Pages(listing3Json) == 0 && recorded.Count == 1 && recorded[0].Name == LegacyUnmodelled && !recorded[0].Ok,
            "P1-38 a deadline page whose cut connect a Legacy fake does not model ends no_response with the named failed Check, not an exception out of Run",
            (thrown is null ? "no throw" : "threw " + thrown.GetType().Name) + "; " + (listing3 ?? "no listing") + "; checks " + N(recorded.Count));
        recorded.Clear();
        var modelled = Legacy(Record, TimeSpan.FromSeconds(30), (_, _) => "configured", _ => (_, _) => "cut")(Bim.Cli.RevitDeadlineTransport.ConnectWait, () => TimeSpan.FromSeconds(30))(TidyPipe, "line");
        check(modelled == "configured" && recorded.Count == 0, "P1-38 the Legacy adapter records no Check for a modelled page", (modelled ?? "null") + " " + N(recorded.Count));

        // (d) StripComments: @$ verbatim and raw string literals (mutation self-test on SeamProblems).
        const string Head = "class T\n{\n    public string? Send(string line)\n    {\n        var pipe = connectThenRead(wait, ReadAfter);\n";
        const string Tail = "        return pipe(line);\n    }\n}\n";
        var cleans = new (string Label, string Body)[]
        {
            ("@$ verbatim string ending in a backslash", "        var p = @$\"{root}\\\"; // connectTransport\n"),
            ("$@ verbatim string ending in a backslash", "        var p = $@\"{root}\\\"; // connectTransport\n"),
            ("@$ verbatim string with a doubled quote", "        var p = @$\"say \"\"hi\"\" {root}\\\"; // measured\n"),
            ("multi-line raw string with a lone quote", "        var r = \"\"\"\n            say \"hi\n            \"\"\"; // measured\n"),
            ("interpolated raw string", "        var r = $$\"\"\"\n            {{root}} \"\n            \"\"\"; // cutTransport\n"),
            ("single-line raw string with a lone quote", "        var r = \"\"\"a \" b\"\"\"; // measured\n"),
            ("four-quote raw string holding three quotes", "        var r = \"\"\"\"\n            has \"\"\" inside \"\n            \"\"\"\"; // measured\n"),
        };
        foreach (var (label, body) in cleans)
        {
            var problems = SeamProblems(Head + body + Tail);
            check(problems.Length == 0, "P1-38 StripComments mutation self-test: a // after a " + label + " is a comment (no seam flagged)", string.Join("; ", problems));
        }
        var mutants = new (string Label, string Body)[]
        {
            ("@$ verbatim string ending in a backslash", "        var p = @$\"{root}\\\" + \"//\"; var c = cutTransport;\n"),
            ("multi-line raw string with a lone quote", "        var r = \"\"\"\n            say \"hi\n            \"\"\" + \"//\"; var c = cutTransport;\n"),
            ("interpolated raw string", "        var r = $\"\"\"\n            {root} \"\n            \"\"\" + \"//\"; var c = connectTransport;\n"),
            ("four-quote raw string holding three quotes", "        var r = \"\"\"\"\n            has \"\"\" inside \"\n            \"\"\"\" + \"//\"; var c = measured;\n"),
        };
        foreach (var (label, body) in mutants)
        {
            var problems = SeamProblems(Head + body + Tail);
            check(problems.Length > 0, "P1-38 StripComments mutation self-test: a seam in code after a " + label + " (followed by a \"//\" string) is flagged", "no problem flagged");
        }
    }

    // ---- 3e. P1-37: ReadReplyLine's abandoned read on a real pipe ----
    // An in-process same-user pipe server (StallingAddIn) accepts the client, reads its request line and never replies,
    // so ReadReplyLine, given a real StreamReader.ReadLineAsync on the client pipe, times out after its 200 ms wait. The
    // abandoned read then ends one of three ways: (A) it honours the cancel and the pipe is disposed at once, as
    // SendMeasured's `using` does; (B) it ignores the cancel and the pipe is disposed under it; (C) it ignores the cancel and
    // the server sends a byte that a throwing UTF-8 decoder rejects, so it faults (DecoderFallbackException). Once each
    // read is collected (bounded GC loop, at most 2 s, no sleep over 10 ms) TaskScheduler.UnobservedTaskException,
    // filtered to faults raised inside StreamReader (or decoder faults) and attributed to the step that ran, reports none;
    // (D) a control read faulting the same way as C but never given to ReadReplyLine is reported, proving the detector.
    // (E) The production transport (RevitCommandService.Run over PipeTransportWithTimeouts(200 ms, 5 s)) on the stalled
    // server is pipe_read_timeout naming 200 ms after the exact wire line; no unobserved fault is seen after a GC loop,
    // but that read is not reachable from the test, so its collection is not proven (P1-39: A proves the same read form,
    // collected). (D2, P1-39) a control read like B but never given to ReadReplyLine proves the StreamReader-frame filter
    // branch matches the pipe-dispose fault where that read faults; where it completes, it records its result.
    sealed class StallingAddIn : IDisposable
    {
        readonly ManualResetEventSlim served = new();
        readonly ManualResetEventSlim send = new();
        readonly ManualResetEventSlim release = new();
        readonly Thread thread;
        byte[]? late;

        public StallingAddIn(string name)
        {
            thread = new Thread(() =>
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    Line = reader.ReadLine();
                    served.Set();
                    if (send.Wait(TimeSpan.FromSeconds(10)) && late is { } bytes) { server.Write(bytes); server.Flush(); Sent = true; }
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                catch (Exception error) { Error = error.GetType().Name + ": " + error.Message; }
                finally { served.Set(); }
            }) { IsBackground = true };
            thread.Start();
        }

        public string? Line { get; private set; }
        public string? Error { get; private set; }
        public bool Sent { get; private set; }

        public bool WaitServed() => served.Wait(TimeSpan.FromSeconds(10)) && Error is null && Line is not null;

        public void Send(byte[] bytes) { late = bytes; send.Set(); }

        public string Describe() => "line " + (Line ?? "<none>") + (Sent ? ", sent" : "") + (Error is null ? "" : ", error " + Error);

        public void Dispose()
        {
            send.Set();
            release.Set();
            if (!thread.Join(TimeSpan.FromSeconds(10))) return;
            served.Dispose();
            send.Dispose();
            release.Dispose();
        }
    }

    // P1-39: a read that completed records its result (null: the pipe reached end of stream).
    static string TaskState(Task<string?>? task) => task is null ? "not started" : task.IsCanceled ? "canceled" : task.IsFaulted ? "faulted"
        : task.IsCompletedSuccessfully ? "completed " + (task.Result is null ? "null" : "line " + task.Result) : "pending";

    // Starts a real ReadLineAsync on a client pipe to a stalled server and abandons it: through ReadReplyLine (viaSeam) or
    // never observed (the control). badBytes: the read ignores the cancel and the server sends 0xFF '\n' to a throwing
    // decoder once the wait gave up; else the client pipe is disposed right after (honourCancel: the read gets the token).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference AbandonRealPipeRead(string label, bool viaSeam, bool honourCancel, bool badBytes, TimeSpan wait, List<string> outcome)
    {
        var pipe = AbsentPipe();
        using var addIn = new StallingAddIn(pipe);
        Task<string?>? started = null;
        var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        try
        {
            client.Connect(5000);
            client.Write(new UTF8Encoding(false).GetBytes("p1-37 " + label + "\n"));
            var reader = new StreamReader(client, badBytes ? new UTF8Encoding(false, true) : new UTF8Encoding(false), false, 4096, leaveOpen: true);
            Func<CancellationToken, Task<string?>> readLine = token => started = reader.ReadLineAsync(honourCancel ? token : CancellationToken.None).AsTask();
            var watch = Stopwatch.StartNew();
            if (viaSeam)
            {
                try { outcome.Add("no error, line " + (RevitCommandService.ReadReplyLine(readLine, wait) ?? "null")); }
                catch (RevitPipeReadTimeoutException error) { outcome.Add("timeout " + N((long)error.Timeout.TotalMilliseconds)); }
                catch (Exception error) { outcome.Add(error.GetType().Name); }
            }
            else
            {
                readLine(CancellationToken.None);
                outcome.Add("unobserved");
            }
            outcome.Add(addIn.WaitServed() ? "served " + addIn.Line : "not served (" + addIn.Describe() + ")");
            if (badBytes) addIn.Send(new byte[] { 0xFF, (byte)'\n' });
            else client.Dispose();
            var completed = SpinWait.SpinUntil(() => started is { IsCompleted: true }, TimeSpan.FromSeconds(2));
            outcome.Add(TaskState(started) + (completed ? "" : " after 2 s") + " in " + N(watch.ElapsedMilliseconds) + " ms");
        }
        catch (Exception error) { outcome.Add("harness " + error.GetType().Name + ": " + error.Message); }
        finally { client.Dispose(); }
        return new WeakReference(started);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static string ProductionStall(string wallsText, TimeSpan wait, out string? served)
    {
        var pipe = AbsentPipe();
        using var addIn = new StallingAddIn(pipe);
        var result = RevitCommandService.Run(pipe, wallsText, RevitCommandPolicy.None, RevitCommandService.PipeTransportWithTimeouts(wait, TimeSpan.FromSeconds(5)));
        served = addIn.WaitServed() ? addIn.Line : null;
        return result.Line;
    }

    static void LateCancelRealPipe(string wallsText, Action<bool, string, string> check)
    {
        var wait = TimeSpan.FromMilliseconds(200);
        var seen = new List<string>();
        var step = "";
        EventHandler<UnobservedTaskExceptionEventArgs> detector = (_, e) =>
        {
            var mine = e.Exception.Flatten().InnerExceptions
                .Where(inner => inner is DecoderFallbackException || inner.ToString().Contains("System.IO.StreamReader", StringComparison.Ordinal))
                .Select(inner => inner.GetType().Name).ToArray();
            if (mine.Length == 0) return;
            lock (seen) seen.AddRange(mine.Select(name => Volatile.Read(ref step) + ":" + name));
            e.SetObserved();
        };
        string[] Seen(string name) { lock (seen) return seen.Where(entry => entry.StartsWith(name + ":", StringComparison.Ordinal)).ToArray(); }
        TaskScheduler.UnobservedTaskException += detector;
        try
        {
            (List<string> Outcome, bool Collected, string[] Faults) Step(string name, bool viaSeam, bool honourCancel, bool badBytes)
            {
                Volatile.Write(ref step, name);
                var outcome = new List<string>();
                var collected = Collected(AbandonRealPipeRead(name, viaSeam, honourCancel, badBytes, wait, outcome));
                return (outcome, collected, Seen(name));
            }
            static string Describe((List<string> Outcome, bool Collected, string[] Faults) result)
                => string.Join(" | ", result.Outcome) + "; collected " + result.Collected + "; unobserved [" + string.Join(", ", result.Faults) + "]";
            var timeout = "timeout " + N((long)wait.TotalMilliseconds);

            var control = Step("D", false, false, true);
            check(control.Collected && control.Outcome.Count == 3 && control.Outcome[1] == "served p1-37 D" && control.Outcome[2].StartsWith("faulted in ", StringComparison.Ordinal)
                && control.Faults.Contains("D:" + nameof(DecoderFallbackException)),
                "P1-37 real pipe detector: a real-pipe read faulting (decoder) and never observed is reported by TaskScheduler.UnobservedTaskException once collected", Describe(control));
            // P1-39 D2: B's harness (a read ignoring the cancel, pipe disposed under it) never given to ReadReplyLine. Where
            // it faults (Linux, macOS: IOException) the detector reports it through its StreamReader-frame branch (not a
            // decoder fault), so the same filter would see B's fault; where it completes (Windows) it reads end of stream.
            var disposeControl = Step("D2", false, false, false);
            // P1-41: the failure text names the runtime the Windows D2/B pins were measured on and the one of this run.
            // P1-43: that runtime is declared once (WindowsMeasured*); on Windows a different one is a named annotation,
            // P1-45: written at the start of the step (VisibilitySelfTest), not from here.
            var framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            var runtime = WindowsMeasured + "; this run " + framework + ", " + os;
            check(disposeControl.Collected && disposeControl.Outcome.Count == 3 && disposeControl.Outcome[0] == "unobserved" && disposeControl.Outcome[1] == "served p1-37 D2"
                && (OperatingSystem.IsWindows()
                    ? disposeControl.Outcome[2].StartsWith("completed null in ", StringComparison.Ordinal) && disposeControl.Faults.Length == 0
                    : disposeControl.Outcome[2].StartsWith("faulted in ", StringComparison.Ordinal) && disposeControl.Faults.SequenceEqual(new[] { "D2:" + nameof(IOException) })),
                "P1-39 real pipe dispose control: a never-observed read whose pipe is disposed under it " + (OperatingSystem.IsWindows()
                    ? "completes with null (end of stream) on Windows, no fault to report"
                    : "faults IOException and is reported through the detector's StreamReader-frame branch"),
                Describe(disposeControl) + "; " + runtime);
            var faulting = Step("C", true, false, true);
            check(faulting.Collected && faulting.Outcome.Count == 3 && faulting.Outcome[0] == timeout && faulting.Outcome[1] == "served p1-37 C"
                && faulting.Outcome[2].StartsWith("faulted in ", StringComparison.Ordinal) && faulting.Faults.Length == 0,
                "P1-37 real pipe: a stalled server makes ReadReplyLine time out (200 ms) and the abandoned read faulting late (decoder) raises no UnobservedTaskException", Describe(faulting));
            var disposed = Step("B", true, false, false);
            check(disposed.Collected && disposed.Outcome.Count == 3 && disposed.Outcome[0] == timeout && disposed.Outcome[1] == "served p1-37 B"
                && !disposed.Outcome[2].StartsWith("pending", StringComparison.Ordinal) && !disposed.Outcome[2].Contains("after 2 s", StringComparison.Ordinal) && disposed.Faults.Length == 0,
                "P1-37 real pipe: a read ignoring the cancel, whose pipe is disposed after ReadReplyLine timed out, ends within 2 s and raises no UnobservedTaskException", Describe(disposed));
            var honoured = Step("A", true, true, false);
            check(honoured.Collected && honoured.Outcome.Count == 3 && honoured.Outcome[0] == timeout && honoured.Outcome[1] == "served p1-37 A"
                && !honoured.Outcome[2].StartsWith("pending", StringComparison.Ordinal) && !honoured.Outcome[2].Contains("after 2 s", StringComparison.Ordinal) && honoured.Faults.Length == 0,
                "P1-37 real pipe: the production read form (honouring the cancel, pipe disposed at once) times out and raises no UnobservedTaskException", Describe(honoured));
            // Measured (CI probe run 44788387750; P1-39 re-measured on the WindowsMeasured runtime, OS and run,
            // Ubuntu 26.04.1, macOS 25.6): A ends canceled on Linux, macOS and Windows; B (pipe disposed under a read
            // ignoring the cancel) ends faulted on Linux and macOS (IOException, the late fault the continuation observes;
            // D2 shows its type) and on Windows completes successfully with null, end of stream (no fault); C and D fault
            // (decoder) everywhere. Runtime behaviour, not a documented guarantee: a newer runtime may move it.
            var disposedState = OperatingSystem.IsWindows() ? "completed null in " : "faulted in ";
            check(honoured.Outcome[^1].StartsWith("canceled in ", StringComparison.Ordinal) && disposed.Outcome[^1].StartsWith(disposedState, StringComparison.Ordinal)
                && faulting.Outcome[^1].StartsWith("faulted in ", StringComparison.Ordinal) && control.Outcome[^1].StartsWith("faulted in ", StringComparison.Ordinal),
                "P1-37 real pipe states pinned: the production read form ends canceled; a pipe disposed under a read ignoring the cancel ends " + disposedState[..^4] + " on this OS",
                "A " + honoured.Outcome[^1] + " ; B " + disposed.Outcome[^1] + " ; C " + faulting.Outcome[^1] + " ; D " + control.Outcome[^1] + " ; " + runtime);

            Volatile.Write(ref step, "E");
            var expected = RevitCommandResponse.Failure(RevitCommandCodes.PipeReadTimeout, "pipe_name",
                RevitCommandService.ReadTimeoutDetail(RevitCommandProtocol.Parse(wallsText), wait)).ToJson();
            var watch = Stopwatch.StartNew();
            var line = ProductionStall(wallsText, wait, out var served);
            var ended = watch.ElapsedMilliseconds;
            var rounds = Stopwatch.StartNew();
            for (var round = 0; round < 5 && rounds.Elapsed < TimeSpan.FromSeconds(2); round++) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(10); }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var production = Seen("E");
            check(line == expected && served == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(wallsText)) && ended >= 190 && production.Length == 0,
                "P1-37 real pipe through RevitCommandService.Run (PipeTransportWithTimeouts 200 ms): a stalled add-in is pipe_read_timeout naming 200 ms after the exact wire line; none seen after a GC loop (that read is not reachable here, so its collection is unproven: A proves the same read form)",
                line + " in " + N(ended) + " ms; served " + (served ?? "<none>") + "; unobserved [" + string.Join(", ", production) + "]");
        }
        finally { TaskScheduler.UnobservedTaskException -= detector; }
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
                connectThenRead: Legacy(check, readTimeout, configured, left => { calls.Add("cut " + Label(left)); return Throws(error); },
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
        // P1-42: read from the parsed argument list (formatting and named arguments, self-test).
        var mcpSource = File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")).ReplaceLineEndings("\n");
        var mcpProblems = McpTransportProblems(mcpSource);
        check(mcpProblems.Length == 0,
            "P1-30/P1-32 static: McpServer.RevitCallAll builds RevitDeadlineTransport with cutReads: realPipe and the default (shared) connect bound and read after connect (no connectTransport, connectThenRead or cutTransport override)",
            string.Join("; ", mcpProblems));
        const string McpCall = "new RevitDeadlineTransport(pipeName, RevitCommandPolicy.None, realPipe ? null : revitTransport, readTimeout, QueryAllDeadline,\n            RevitDeadlineTransport.ElapsedOn(QueryAllClock), listing: RevitDeadlineTransport.McpListing, cutReads: realPipe, probe: revitProbe)";
        static string InCallAll(string construction) => "(string Text, bool IsError) RevitCallAll(JsonObject arguments)\n{\n    var transport = " + construction + ";\n    return (\"\", false);\n}\n";
        var mcpCleans = new (string Label, string Code)[]
        {
            ("today's call", InCallAll(McpCall)),
            ("one argument per line", InCallAll(McpCall.Replace(", ", ",\n    ", StringComparison.Ordinal))),
            ("cutReads:realPipe without spaces", InCallAll(McpCall.Replace("cutReads: realPipe", "cutReads:realPipe", StringComparison.Ordinal))),
            ("reordered named arguments", InCallAll("new RevitDeadlineTransport(pipeName, RevitCommandPolicy.None, realPipe ? null : revitTransport, readTimeout, QueryAllDeadline, cutReads: realPipe, probe: revitProbe, listing: RevitDeadlineTransport.McpListing, elapsed: RevitDeadlineTransport.ElapsedOn(QueryAllClock))")),
            ("a comment naming connectThenRead", InCallAll(McpCall.Replace("cutReads: realPipe", "/* no connectThenRead override */ cutReads : realPipe", StringComparison.Ordinal))),
        };
        foreach (var (label, code) in mcpCleans)
        {
            var problems = McpTransportProblems(code);
            check(problems.Length == 0, "P1-42 MCP transport pin self-test: " + label + " passes", string.Join("; ", problems));
        }
        var mcpMutants = new (string Label, string Code)[]
        {
            ("cutReads false", InCallAll(McpCall.Replace("cutReads: realPipe", "cutReads: false", StringComparison.Ordinal))),
            ("cutReads absent", InCallAll(McpCall.Replace(", cutReads: realPipe", "", StringComparison.Ordinal))),
            ("a connectThenRead override", InCallAll(McpCall.Replace("probe: revitProbe", "probe: revitProbe, connectThenRead: Cut", StringComparison.Ordinal))),
            ("a ConnectTimeout bound", InCallAll(McpCall.Replace("QueryAllDeadline,", "TimeSpan.FromMilliseconds(RevitCommandService.ConnectTimeoutMs),", StringComparison.Ordinal))),
            ("the call only in a comment", InCallAll("Other();\n    // " + McpCall.ReplaceLineEndings(" "))),
            ("two constructions", InCallAll(McpCall) + InCallAll(McpCall).Replace("RevitCallAll", "RevitCallOther", StringComparison.Ordinal)),
            // P1-44: only a construction inside RevitCallAll is the call; one in another member is not counted as it.
            ("P1-44 the construction only in another member", InCallAll("new RevitServiceTransport(pipeName, RevitCommandPolicy.None, revitTransport)")
                + InCallAll(McpCall).Replace("RevitCallAll", "RevitCallOther", StringComparison.Ordinal)),
            ("P1-44 no RevitCallAll block (the construction in a field)", "static readonly object Transport = " + McpCall + ";\n"),
        };
        foreach (var (label, code) in mcpMutants)
            check(McpTransportProblems(code).Length > 0, "P1-42 MCP transport pin self-test: " + label + " is flagged", "no problem flagged");

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
