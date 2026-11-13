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
// Known limit: the pipe connect inside RevitCommandService (ConnectTimeoutMs, 5 s) is not cut, so a run can last the
// deadline plus up to 5 s, ending pipe_connect_timeout when no add-in is listening. Tests inject the clock and the cut transport.
internal sealed class RevitDeadlineTransport : IRevitCommandTransport
{
    public const string Code = "query_all_deadline_exceeded";
    public const string CliListing = "--all";
    public const string McpListing = "all:true";

    readonly string pipeName;
    readonly RevitCommandPolicy policy;
    readonly Func<string, string, string?> transport;
    readonly TimeSpan readTimeout;
    readonly TimeSpan deadline;
    readonly Func<TimeSpan> elapsed;
    readonly Func<TimeSpan, Func<string, string, string?>> cutTransport;
    readonly TimeSpan start;
    readonly string listing;
    readonly bool cutReads;
    readonly RevitFileSystemProbe? probe;

    public RevitDeadlineTransport(string pipeName, RevitCommandPolicy policy, Func<string, string, string?> transport, TimeSpan readTimeout, TimeSpan deadline,
        Func<TimeSpan>? elapsed = null, Func<TimeSpan, Func<string, string, string?>>? cutTransport = null,
        string listing = CliListing, bool cutReads = true, RevitFileSystemProbe? probe = null)
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
        var line = RevitCommandService.Run(pipeName, envelopeJson, policy, cut ? cutTransport(left) : transport, probe).Line;
        if (!cut) return line;
        try { return RevitCommandResponse.Parse(line).Code == RevitCommandCodes.PipeReadTimeout ? Exceeded() : line; }
        catch (RevitCommandException) { return line; }
    }
}
