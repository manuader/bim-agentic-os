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
// (RevitCommandService.ConnectTimeoutMs) goes through connectTransport(read, left), the real pipe with that read timeout
// (the time left when the read is cut, else the configured one) and a connect wait of the time left; its
// pipe_connect_timeout is reported as the deadline. With time left of at least 5 s at the page the connect keeps its
// full 5 s and a pipe_connect_timeout passes through: on an absent pipe that takes N >= 6 in practice (at N = 5 the
// first page already has just under 5 s left, so its connect is cut and the run ends query_all_deadline_exceeded at
// 5 s), ending pipe_connect_timeout after 5 s, within the deadline. cutReads false never cuts the connect either. Known limit: the read of a page whose connect was
// cut is still given the time left measured before the connect, so a connect that succeeds just before the deadline can
// extend the run by up to that time left. Tests inject the clock, the cut transport and the connect transport.
internal sealed class RevitDeadlineTransport : IRevitCommandTransport
{
    public const string Code = "query_all_deadline_exceeded";
    public const string CliListing = "--all";
    public const string McpListing = "all:true";
    public static readonly TimeSpan ConnectWait = TimeSpan.FromMilliseconds(RevitCommandService.ConnectTimeoutMs);

    readonly string pipeName;
    readonly RevitCommandPolicy policy;
    readonly Func<string, string, string?> transport;
    readonly TimeSpan readTimeout;
    readonly TimeSpan deadline;
    readonly Func<TimeSpan> elapsed;
    readonly Func<TimeSpan, Func<string, string, string?>> cutTransport;
    readonly Func<TimeSpan, TimeSpan, Func<string, string, string?>> connectTransport;
    readonly TimeSpan start;
    readonly string listing;
    readonly bool cutReads;
    readonly RevitFileSystemProbe? probe;

    public RevitDeadlineTransport(string pipeName, RevitCommandPolicy policy, Func<string, string, string?> transport, TimeSpan readTimeout, TimeSpan deadline,
        Func<TimeSpan>? elapsed = null, Func<TimeSpan, Func<string, string, string?>>? cutTransport = null,
        string listing = CliListing, bool cutReads = true, RevitFileSystemProbe? probe = null,
        Func<TimeSpan, TimeSpan, Func<string, string, string?>>? connectTransport = null)
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
        this.cutTransport = cutTransport ?? RevitCommandService.PipeTransportWithReadTimeout;
        this.connectTransport = connectTransport ?? RevitCommandService.PipeTransportWithTimeouts;
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
        var pipe = cutConnect ? connectTransport(cut ? left : readTimeout, left) : cut ? cutTransport(left) : transport;
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
