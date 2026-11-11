using Bim.Core;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
// P1-17 pipe read timeout: a connected add-in pipe that never answers ends with the typed client code
// pipe_read_timeout after a bounded, caller-chosen read timeout instead of blocking forever. A write whose reply timed
// out is reported as outcome unknown (sent once, never retried, replay guidance in the detail); a read-only command
// says it may be resent. Exercised with a throwing fake transport and with a real same-user pipe server that reads
// the request line and never replies (Unix domain socket on Linux, named pipe on Windows). Wire format unchanged.
static class PipeReadTimeoutChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string FakePipe = "bim-agentic-os-2025-4242";
    static readonly RevitCommandPolicy Policy = new(new[] { @"C:\Repo\.bim\native-runs" });
    static readonly RevitFileSystemProbe NoLinks = new(_ => false, _ => null);

    static string Envelope(string command, JsonNode args) => new JsonObject
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-read-timeout",
        ["document"] = new JsonObject { ["path"] = FixturePath, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args,
    }.ToJsonString();

    static string Query() => Envelope("query.levels", new JsonObject());
    static string WallWrite() => Envelope("wall.create", new JsonObject
    {
        ["level"] = "Level 1",
        ["type"] = "Generic - 200mm",
        ["height_mm"] = 3000,
        ["curve"] = new JsonObject { ["kind"] = "line", ["start"] = new JsonArray(0, 0), ["end"] = new JsonArray(5000, 0) },
    });

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
        catch (Exception) { return false; }
    }

    static RevitCommandResponse Parse(RevitCommandResult result) => RevitCommandResponse.Parse(result.Line);

    public static void Run(Action<bool, string, string> check, string pipeName)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

        // ---- Code, default and bounds ----
        Check(RevitCommandCodes.PipeReadTimeout == "pipe_read_timeout" && RevitCommandCodes.ClientSide.Contains(RevitCommandCodes.PipeReadTimeout)
            && !RevitCommandCodes.ExecutorReserved.Contains(RevitCommandCodes.PipeReadTimeout), "pipe_read_timeout is a client-side transport code");
        Check(RevitCommandService.DefaultReadTimeout == TimeSpan.FromSeconds(1800), "the default read timeout is 1800 s (the longest owner catalogue step budget)", RevitCommandService.DefaultReadTimeout.ToString());
        Check(RevitCommandService.MaxReadTimeout == TimeSpan.FromHours(24) && RevitCommandService.DefaultReadTimeout <= RevitCommandService.MaxReadTimeout
            && RevitCommandService.DefaultReadTimeout > TimeSpan.FromMilliseconds(RevitCommandService.ConnectTimeoutMs), "the default lies between the connect timeout and the 24 h maximum");
        foreach (var (label, bad) in new[] { ("zero", TimeSpan.Zero), ("negative", TimeSpan.FromMilliseconds(-1)), ("infinite", Timeout.InfiniteTimeSpan), ("over 24 h", RevitCommandService.MaxReadTimeout + TimeSpan.FromTicks(1)) })
        {
            Check(Throws<ArgumentOutOfRangeException>(() => RevitCommandService.PipeTransportWithReadTimeout(bad)), "a " + label + " read timeout is refused when the transport is made");
            // Refused before any I/O: no server listens on this name, so a connect attempt would be a 5 s TimeoutException.
            var watch = Stopwatch.StartNew();
            Check(Throws<ArgumentOutOfRangeException>(() => RevitCommandService.PipeTransport("bim-agentic-os-2025-999999998", "{}", bad)) && watch.ElapsedMilliseconds < 2000,
                "a " + label + " read timeout is refused before connecting", watch.ElapsedMilliseconds + " ms");
        }
        var timeoutError = new RevitPipeReadTimeoutException(TimeSpan.FromMilliseconds(250));
        Exception asException = timeoutError;
        Check(asException is IOException && asException is not TimeoutException && timeoutError.Timeout == TimeSpan.FromMilliseconds(250) && timeoutError.Message == RevitCommandCodes.PipeReadTimeout,
            "the read timeout exception is an IOException carrying its timeout, never a connect TimeoutException");

        // ---- Fake transport that throws the read timeout ----
        var sent = new List<string>();
        string? Stalled(string pipe, string line) { sent.Add(line); throw new RevitPipeReadTimeoutException(TimeSpan.FromMilliseconds(250)); }
        var read = RevitCommandService.Run(FakePipe, Query(), Policy, Stalled, NoLinks);
        var readReply = Parse(read);
        Check(read.IsError && read.Forwarded && readReply.Code == RevitCommandCodes.PipeReadTimeout && readReply.Field == "pipe_name" && sent.Count == 1,
            "a read-only command whose reply times out is pipe_read_timeout, sent once", read.Line);
        Check(readReply.Detail is { } readDetail && readDetail.Contains("250 ms", StringComparison.Ordinal) && readDetail.Contains("read-only", StringComparison.Ordinal)
            && !readDetail.Contains("outcome_unknown", StringComparison.Ordinal), "the read-only timeout detail names the timeout and says nothing was written", readReply.Detail ?? "");
        sent.Clear();
        var write = RevitCommandService.Run(FakePipe, WallWrite(), Policy, Stalled, NoLinks);
        var writeReply = Parse(write);
        Check(write.IsError && write.Forwarded && writeReply.Code == RevitCommandCodes.PipeReadTimeout && writeReply.Field == "pipe_name" && sent.Count == 1,
            "a write whose reply times out is pipe_read_timeout and is never retried", write.Line + " sent " + sent.Count);
        Check(writeReply.Detail is { } writeDetail && writeDetail.StartsWith("outcome_unknown:", StringComparison.Ordinal) && writeDetail.Contains("250 ms", StringComparison.Ordinal)
            && writeDetail.Contains("not retried", StringComparison.Ordinal) && writeDetail.Contains("same operation_id", StringComparison.Ordinal),
            "the write timeout detail says outcome unknown, not retried, and how to replay", writeReply.Detail ?? "");
        Check(writeReply.Created.Count == 0 && writeReply.Receipt is null, "a timed-out write reports nothing created and no receipt");
        // A plain TimeoutException from the transport is still the connect timeout (unchanged).
        var connect = Parse(RevitCommandService.Run(FakePipe, WallWrite(), Policy, (_, _) => throw new TimeoutException(), NoLinks));
        Check(connect.Code == RevitCommandCodes.PipeConnectTimeout, "a transport TimeoutException stays pipe_connect_timeout");

        // ---- Real stalled pipe: connected, request read, no line back ----
        using var addIn = new StalledAddIn(pipeName);
        var envelope = RevitCommandProtocol.Parse(WallWrite());
        var wire = RevitCommandProtocol.WireLine(envelope);
        var shortTimeout = TimeSpan.FromMilliseconds(400);
        var watchWrite = Stopwatch.StartNew();
        var stalledWrite = RevitCommandService.Run(pipeName, WallWrite(), Policy, RevitCommandService.PipeTransportWithReadTimeout(shortTimeout), NoLinks);
        watchWrite.Stop();
        var stalledReply = Parse(stalledWrite);
        Check(stalledWrite.IsError && stalledReply.Code == RevitCommandCodes.PipeReadTimeout && stalledReply.Detail is { } stalledDetail
            && stalledDetail.StartsWith("outcome_unknown:", StringComparison.Ordinal) && stalledDetail.Contains("400 ms", StringComparison.Ordinal),
            "a stalled add-in pipe ends a write with pipe_read_timeout, outcome unknown", stalledWrite.Line);
        Check(watchWrite.ElapsedMilliseconds >= 350 && watchWrite.ElapsedMilliseconds < 15000, "the stalled write waits about the read timeout, not forever", watchWrite.ElapsedMilliseconds + " ms");
        Check(addIn.WaitForRequests(1) && addIn.Received.Count == 1 && addIn.Received[0] == wire, "the stalled add-in received the exact wire line once (no retry)", addIn.Received.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var watchDirect = Stopwatch.StartNew();
        RevitPipeReadTimeoutException? direct = null;
        try { RevitCommandService.PipeTransport(pipeName, "ping", TimeSpan.FromMilliseconds(300)); }
        catch (RevitPipeReadTimeoutException error) { direct = error; }
        watchDirect.Stop();
        Check(direct is not null && direct.Timeout == TimeSpan.FromMilliseconds(300) && watchDirect.ElapsedMilliseconds >= 250 && watchDirect.ElapsedMilliseconds < 15000,
            "PipeTransport throws RevitPipeReadTimeoutException on a stalled pipe", watchDirect.ElapsedMilliseconds + " ms");
        Check(addIn.WaitForRequests(2) && addIn.Received.Count == 2 && addIn.Received[1] == "ping", "the direct request reached the stalled add-in");

        // ---- Replies within the timeout are unchanged ----
        addIn.Reply = line => "pong:" + line;
        Check(RevitCommandService.PipeTransport(pipeName, "ping", TimeSpan.FromSeconds(10)) == "pong:ping", "a reply within the read timeout is returned verbatim");
        addIn.Delay = TimeSpan.FromMilliseconds(200);
        Check(RevitCommandService.PipeTransportWithReadTimeout(TimeSpan.FromSeconds(10))(pipeName, "slow") == "pong:slow", "a reply that comes after a delay below the timeout is returned");
        addIn.Delay = TimeSpan.Zero;
        addIn.Reply = null;
        addIn.CloseWithoutReply = true;
        Check(RevitCommandService.PipeTransport(pipeName, "bye", TimeSpan.FromSeconds(10)) is null, "a pipe closed without a line is still null (no_response), not a timeout");
    }
}

// A same-user pipe server standing in for an add-in that reads a request line and never answers (Reply null), answers
// Reply(line) after Delay, or closes the connection without a line (CloseWithoutReply). The next listening instance
// is created right after each accept, so a client reconnecting never races a closing listener (P1-19).
sealed class StalledAddIn : IDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    readonly object gate = new();
    readonly List<string> received = new();

    public StalledAddIn(string name)
    {
        Name = name;
        loop = Task.Run(Serve);
    }

    public string Name { get; }
    public Func<string, string>? Reply { get; set; }
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public bool CloseWithoutReply { get; set; }
    public IReadOnlyList<string> Received { get { lock (gate) return received.ToArray(); } }

    public bool WaitForRequests(int count)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 5000)
        {
            if (Received.Count >= count) return true;
            Thread.Sleep(10);
        }
        return Received.Count >= count;
    }

    NamedPipeServerStream Listen() => new(Name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    async Task Serve()
    {
        var next = Listen();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var current = next;
                try { await current.WaitForConnectionAsync(stop.Token); }
                catch (OperationCanceledException) { break; }
                next = Listen();
                await Handle(current);
            }
        }
        finally { await next.DisposeAsync(); }
    }

    async Task Handle(NamedPipeServerStream server)
    {
        var utf8 = new UTF8Encoding(false);
        try
        {
            using var reader = new StreamReader(server, utf8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(server, utf8, 4096, leaveOpen: true) { AutoFlush = true };
            var line = await reader.ReadLineAsync(stop.Token);
            if (line is null) return;
            lock (gate) received.Add(line);
            if (CloseWithoutReply) return;
            if (Reply is { } reply)
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, stop.Token);
                await writer.WriteLineAsync(reply(line));
            }
            // Stall (or wait after replying) until the client closes its end.
            while (await reader.ReadLineAsync(stop.Token) is not null) { }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { await server.DisposeAsync(); }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop.Wait(5000); } catch (AggregateException) { }
        stop.Dispose();
    }
}
