using System.Diagnostics;
using System.Globalization;
using Bim.Core;

namespace Bim.Cli;

// P1-21: the overall deadline of `revit-command … --all --deadline-seconds n`; P1-23: also the MCP all:true deadline
// (McpServer.RevitCallAll, this file linked into Bim.Mcp), one cut/exceeded rule with the same code. Each page goes
// through RevitCommandService.Run (as RevitServiceTransport), within one deadline counted from the creation of this
// transport: a page due after the deadline is answered query_all_deadline_exceeded without being sent, so
// RevitElementQueryPaging.All reports it in the listing shape without partial elements; a page whose time left is below
// the read timeout is read with the time left (cut), its pipe_read_timeout reported as the deadline; any other outcome
// of a cut page (no_response, pipe_io_error, a reply) passes through unchanged, and time left equal to the read timeout
// is not cut. Only the listing name in the exceeded detail differs: "--all" (CLI, the default) or "all:true" (MCP).
// cutReads false never cuts (the MCP server on an injected test transport): the configured transport serves every page
// and its pipe_read_timeout passes through. probe is the file-system probe RevitCommandService.Run checks paths with.
// The clock starts when this transport is built: in the CLI after the option, pipe name, policy and envelope reads, just
// before paging, as the MCP transport starts when its all:true call builds it.
// P1-30: the pipe connect is bounded too. A page whose time left is below the 5 s connect wait
// (RevitCommandService.ConnectTimeoutMs) goes through connectThenRead (see P1-32 below), the real pipe with a connect
// wait of the time left and a read timeout taken once connected; its pipe_connect_timeout is reported as the deadline. With time left of at least 5 s at the page the connect keeps its
// full 5 s and a pipe_connect_timeout passes through: on an absent pipe that takes N >= 6 in practice (at N = 5 the
// first page already has just under 5 s left, so its connect is cut and the run ends query_all_deadline_exceeded at
// 5 s), ending pipe_connect_timeout after 5 s, within the deadline. cutReads false never cuts the connect either.
// P1-32: the read of a page whose connect was cut is measured after the connect: connectThenRead(left, readAfter) is the
// real pipe (RevitCommandService.PipeTransportWithReadAfterConnect) with a connect wait of the time left, asking
// readAfter() for its read timeout once connected: the time left then when it is below the configured read timeout
// (cut, its pipe_read_timeout reported as the deadline), else the configured one. A connect that leaves under 1 ms
// (or completes at or after the deadline) leaves no read: the line is not sent and the page is the deadline. So an add-in that accepts just before
// the deadline and never replies ends the run at the deadline, not up to the time left before the connect later.
// Tests inject the clock, the cut transport and connectThenRead; an injected P1-30 connectTransport(read, left) is
// still accepted and given readAfter() measured when the page's transport is made (its fakes move no clock in a connect).
// P1-34: a page with time left of at least 5 s (its connect keeps the full wait) is measured after its connect too: via
// connectThenRead(5 s, readAfter), read with min(configured read timeout, time left once connected), cut (its
// pipe_read_timeout reported as the deadline) only when the time left is the smaller; its pipe_connect_timeout after the
// full 5 s still passes through. This replaces both the configured transport and the cut transport on such pages, as
// the same real pipe (5 s connect, the configured or cut read) with the read now counted from the connect. Kept as
// before (read measured when the page's transport is made) only for the legacy seams: an injected cutTransport or P1-30
// connectTransport without connectThenRead (the P1-21..P1-31 fakes). cutReads false never measures (configured transport).
internal sealed class RevitDeadlineTransport : IRevitCommandTransport
{
    public const string Code = "query_all_deadline_exceeded";
    public const string CliListing = "--all";
    public const string McpListing = "all:true";
    public static readonly TimeSpan ConnectWait = TimeSpan.FromMilliseconds(RevitCommandService.ConnectTimeoutMs);
    static readonly TimeSpan MinRead = TimeSpan.FromMilliseconds(1);

    readonly string pipeName;
    readonly RevitCommandPolicy policy;
    readonly Func<string, string, string?> transport;
    readonly TimeSpan readTimeout;
    readonly TimeSpan deadline;
    readonly Func<TimeSpan> elapsed;
    // Reached only through the legacy seams (an injected cutTransport or connectTransport); production uses connectThenRead.
    readonly Func<TimeSpan, Func<string, string, string?>> cutTransport;
    readonly Func<TimeSpan, Func<TimeSpan>, Func<string, string, string?>> connectThenRead;
    readonly TimeSpan start;
    readonly bool measured;
    readonly string listing;
    readonly bool cutReads;
    readonly RevitFileSystemProbe? probe;

    public RevitDeadlineTransport(string pipeName, RevitCommandPolicy policy, Func<string, string, string?> transport, TimeSpan readTimeout, TimeSpan deadline,
        Func<TimeSpan>? elapsed = null, Func<TimeSpan, Func<string, string, string?>>? cutTransport = null,
        string listing = CliListing, bool cutReads = true, RevitFileSystemProbe? probe = null,
        Func<TimeSpan, TimeSpan, Func<string, string, string?>>? connectTransport = null,
        Func<TimeSpan, Func<TimeSpan>, Func<string, string, string?>>? connectThenRead = null)
    {
        this.pipeName = pipeName;
        this.policy = policy;
        this.transport = transport;
        this.readTimeout = readTimeout;
        this.deadline = deadline;
        if (elapsed is null)
        {
            var watch = Stopwatch.StartNew();
            elapsed = () => watch.Elapsed;
        }
        this.elapsed = elapsed;
        // P1-34: the default transports (or an injected connectThenRead) measure every page's read after its connect.
        measured = connectThenRead is not null || (cutTransport is null && connectTransport is null);
        this.cutTransport = cutTransport ?? RevitCommandService.PipeTransportWithReadTimeout;
        if (connectThenRead is not null) this.connectThenRead = connectThenRead;
        else if (connectTransport is { } legacy) this.connectThenRead = (left, readAfter) => legacy(readAfter(), left);
        else this.connectThenRead = RevitCommandService.PipeTransportWithReadAfterConnect;
        this.listing = listing;
        this.cutReads = cutReads;
        this.probe = probe;
        start = elapsed();
    }

    // The elapsed-time source of a wall clock (the MCP QueryAllClock): only differences of it are used.
    public static Func<TimeSpan> ElapsedOn(Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return () => clock() - DateTimeOffset.MinValue;
    }

    string Exceeded() => RevitCommandResponse.Failure(Code, null,
        "the " + listing + " listing took longer than its " + ((long)deadline.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
        + " ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor").ToJson();

    public string? Send(string envelopeJson)
    {
        var left = deadline - (elapsed() - start);
        if (left <= TimeSpan.Zero) return Exceeded();
        var cut = cutReads && left < readTimeout;
        var cutConnect = cutReads && left < ConnectWait;
        Func<string, string, string?> pipe;
        // P1-32: asked once connected; whether the read is cut is decided by the time left then.
        TimeSpan ReadAfter()
        {
            var after = deadline - (elapsed() - start);
            cut = after < readTimeout;
            // Under 1 ms left is no time left (a sub-millisecond wait would time out at once): not sent.
            return !cut ? readTimeout : after < MinRead ? TimeSpan.Zero : after;
        }
        // P1-34: a measured page is cut only as ReadAfter decides once connected; a connect that throws first leaves it uncut.
        if (cutConnect) { cut = false; pipe = connectThenRead(left, ReadAfter); }
        // P1-34: time left of at least 5 s: the full connect wait, the read still measured once connected.
        else if (cutReads && measured) { cut = false; pipe = connectThenRead(ConnectWait, ReadAfter); }
        else pipe = cut ? cutTransport(left) : transport;
        var line = RevitCommandService.Run(pipeName, envelopeJson, policy, pipe, probe).Line;
        if (!cut && !cutConnect) return line;
        try
        {
            var code = RevitCommandResponse.Parse(line).Code;
            return (cut && code == RevitCommandCodes.PipeReadTimeout) || (cutConnect && code == RevitCommandCodes.PipeConnectTimeout) ? Exceeded() : line;
        }
        catch (RevitCommandException) { return line; }
    }
}
