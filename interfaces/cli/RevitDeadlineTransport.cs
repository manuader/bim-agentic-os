using System.Diagnostics;
using System.Globalization;
using Bim.Core;

namespace Bim.Cli;

// P1-21: the overall deadline of `revit-command … --all --deadline-seconds n`, the rule of the MCP all:true deadline
// (McpServer.DeadlineTransport, P1-16) with the same code. Each page goes through RevitCommandService.Run (as
// RevitServiceTransport), within one deadline counted from the creation of this transport: a page due after the
// deadline is answered query_all_deadline_exceeded without being sent, so RevitElementQueryPaging.All reports it in
// the listing shape without partial elements; a page whose time left is below the read timeout is read with the time
// left (cut), its pipe_read_timeout reported as the deadline; any other outcome of a cut page (no_response,
// pipe_io_error, a reply) passes through unchanged, and time left equal to the read timeout is not cut.
// The clock starts when this transport is built: in the CLI after the option, pipe name, policy and envelope reads, just
// before paging, as the MCP transport starts when its all:true call builds it.
// Known limit: the pipe connect inside RevitCommandService (ConnectTimeoutMs, 5 s) is not cut, so a run can last the
// deadline plus up to 5 s, ending pipe_connect_timeout when no add-in is listening. Tests inject the clock and the cut transport.
public sealed class RevitDeadlineTransport : IRevitCommandTransport
{
    public const string Code = "query_all_deadline_exceeded";

    readonly string pipeName;
    readonly RevitCommandPolicy policy;
    readonly Func<string, string, string?> transport;
    readonly TimeSpan readTimeout;
    readonly TimeSpan deadline;
    readonly Func<TimeSpan> elapsed;
    readonly Func<TimeSpan, Func<string, string, string?>> cutTransport;
    readonly TimeSpan start;

    public RevitDeadlineTransport(string pipeName, RevitCommandPolicy policy, Func<string, string, string?> transport, TimeSpan readTimeout, TimeSpan deadline,
        Func<TimeSpan>? elapsed = null, Func<TimeSpan, Func<string, string, string?>>? cutTransport = null)
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
        start = elapsed();
    }

    string Exceeded() => RevitCommandResponse.Failure(Code, null,
        "the --all listing took longer than its " + ((long)deadline.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
        + " ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor").ToJson();

    public string? Send(string envelopeJson)
    {
        var left = deadline - (elapsed() - start);
        if (left <= TimeSpan.Zero) return Exceeded();
        var cut = left < readTimeout;
        var line = RevitCommandService.Run(pipeName, envelopeJson, policy, cut ? cutTransport(left) : transport).Line;
        if (!cut) return line;
        try { return RevitCommandResponse.Parse(line).Code == RevitCommandCodes.PipeReadTimeout ? Exceeded() : line; }
        catch (RevitCommandException) { return line; }
    }
}
