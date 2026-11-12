using System.Globalization;
using Bim.Core;

namespace Bim.Cli;

// The options of `revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n] [--deadline-seconds n]`
// (args[3] on), in any order, each at most once, checked before any file or pipe I/O. A refusal is one RevitCommandResponse line whose
// field names the offending args[i]: an unknown or repeated option keeps explicit_process_pipe_required (the call is
// not the explicit pipe form; tests/revit-commands pins that code), a bad value is read_timeout_invalid.
// --all (P1-15) pages a query.elements envelope. --read-timeout-seconds (P1-20) is the pipe read timeout in whole
// seconds from 1 to 86400 (RevitCommandService.MaxReadTimeout), validated by RevitCommandService.
// PipeTransportWithReadTimeout; absent, Transport is RevitCommandService.PipeTransport (DefaultReadTimeout, 1800 s).
// --deadline-seconds (P1-21) is the overall deadline of the --all paging run (RevitDeadlineTransport), whole seconds
// from 1 to 86400, only with --all; a bad or missing value, or the flag without --all, is deadline_invalid with
// DeadlineUsage. The deadline counts from just before paging and does not cut a page's 5 s pipe connect (deadline
// + up to 5 s). Absent, Deadline is null and the run has no overall deadline (today's bytes). The refusals of the
// other options keep Usage unchanged.
public sealed record RevitCommandOptions(bool All, TimeSpan ReadTimeout, Func<string, string, string?> Transport, TimeSpan? Deadline = null)
{
    public const string Usage = "usage: revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n]";
    public const string DeadlineUsage = "usage: revit-command envelope.json process-pipe-name --all [--read-timeout-seconds n] --deadline-seconds n";
    public const string ReadTimeoutOption = "--read-timeout-seconds";
    public const string DeadlineOption = "--deadline-seconds";
    public const string DeadlineInvalid = "deadline_invalid";
    static readonly TimeSpan MaxDeadline = TimeSpan.FromHours(24);
    const int FirstOption = 3;

    public static RevitCommandOptions? Parse(IReadOnlyList<string> args, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(args);
        var all = false;
        TimeSpan? readTimeout = null;
        TimeSpan? deadline = null;
        var deadlineIndex = 0;
        Func<string, string, string?> transport = RevitCommandService.PipeTransport;
        for (var index = FirstOption; index < args.Count; index++)
        {
            var option = args[index];
            var seen = option switch { "--all" => all, ReadTimeoutOption => readTimeout is not null, DeadlineOption => deadline is not null, _ => (bool?)null };
            if (seen is null) { refusal = Refuse(RevitCommandCodes.ExplicitProcessPipeRequired, index, "unknown option " + option); return null; }
            if (seen.Value) { refusal = Refuse(RevitCommandCodes.ExplicitProcessPipeRequired, index, "repeated option " + option); return null; }
            if (option == "--all") { all = true; continue; }
            if (option == DeadlineOption)
            {
                if (index + 1 >= args.Count) { refusal = Refuse(DeadlineInvalid, index, DeadlineOption + " needs a value", DeadlineUsage); return null; }
                deadlineIndex = index++;
                if (WholeSeconds(args[index], MaxDeadline) is not { } seconds) { refusal = Refuse(DeadlineInvalid, index, DeadlineOption + " takes whole seconds from 1 to 86400, got " + args[index], DeadlineUsage); return null; }
                deadline = seconds;
                continue;
            }
            if (index + 1 >= args.Count) { refusal = Refuse(RevitCommandCodes.ReadTimeoutInvalid, index, ReadTimeoutOption + " needs a value"); return null; }
            index++;
            if (ReadTimeoutTransport(args[index]) is not { } timed) { refusal = Refuse(RevitCommandCodes.ReadTimeoutInvalid, index, ReadTimeoutOption + " takes whole seconds from 1 to 86400, got " + args[index]); return null; }
            (readTimeout, transport) = timed;
        }
        if (deadline is not null && !all) { refusal = Refuse(DeadlineInvalid, deadlineIndex, DeadlineOption + " needs --all", DeadlineUsage); return null; }
        refusal = null;
        return new RevitCommandOptions(all, readTimeout ?? RevitCommandService.DefaultReadTimeout, transport, deadline);
    }

    // Whole seconds (digits only), in the range RevitCommandService accepts; null otherwise.
    static (TimeSpan, Func<string, string, string?>)? ReadTimeoutTransport(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        var timeout = TimeSpan.FromSeconds(seconds);
        try { return (timeout, RevitCommandService.PipeTransportWithReadTimeout(timeout)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // Whole seconds (digits only) from 1 to max; null otherwise.
    static TimeSpan? WholeSeconds(string text, TimeSpan max) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 1 && TimeSpan.FromSeconds(seconds) <= max ? TimeSpan.FromSeconds(seconds) : null;

    static string Refuse(string code, int index, string detail, string usage = Usage) =>
        RevitCommandResponse.Failure(code, "args[" + index.ToString(CultureInfo.InvariantCulture) + "]", detail + "; " + usage).ToJson();
}
