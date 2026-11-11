using System.Globalization;
using Bim.Core;

namespace Bim.Cli;

// The options of `revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n]` (args[3] on), in
// any order, each at most once, checked before any file or pipe I/O. A refusal is one RevitCommandResponse line whose
// field names the offending args[i]: an unknown or repeated option keeps explicit_process_pipe_required (the call is
// not the explicit pipe form; tests/revit-commands pins that code), a bad value is read_timeout_invalid.
// --all (P1-15) pages a query.elements envelope. --read-timeout-seconds (P1-20) is the pipe read timeout in whole
// seconds from 1 to 86400 (RevitCommandService.MaxReadTimeout), validated by RevitCommandService.
// PipeTransportWithReadTimeout; absent, Transport is RevitCommandService.PipeTransport (DefaultReadTimeout, 1800 s).
public sealed record RevitCommandOptions(bool All, TimeSpan ReadTimeout, Func<string, string, string?> Transport)
{
    public const string Usage = "usage: revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n]";
    public const string ReadTimeoutOption = "--read-timeout-seconds";
    const int FirstOption = 3;

    public static RevitCommandOptions? Parse(IReadOnlyList<string> args, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(args);
        var all = false;
        TimeSpan? readTimeout = null;
        Func<string, string, string?> transport = RevitCommandService.PipeTransport;
        for (var index = FirstOption; index < args.Count; index++)
        {
            var option = args[index];
            var seen = option switch { "--all" => all, ReadTimeoutOption => readTimeout is not null, _ => (bool?)null };
            if (seen is null) { refusal = Refuse(RevitCommandCodes.ExplicitProcessPipeRequired, index, "unknown option " + option); return null; }
            if (seen.Value) { refusal = Refuse(RevitCommandCodes.ExplicitProcessPipeRequired, index, "repeated option " + option); return null; }
            if (option == "--all") { all = true; continue; }
            if (index + 1 >= args.Count) { refusal = Refuse(RevitCommandCodes.ReadTimeoutInvalid, index, ReadTimeoutOption + " needs a value"); return null; }
            index++;
            if (ReadTimeoutTransport(args[index]) is not { } timed) { refusal = Refuse(RevitCommandCodes.ReadTimeoutInvalid, index, ReadTimeoutOption + " takes whole seconds from 1 to 86400, got " + args[index]); return null; }
            (readTimeout, transport) = timed;
        }
        refusal = null;
        return new RevitCommandOptions(all, readTimeout ?? RevitCommandService.DefaultReadTimeout, transport);
    }

    // Whole seconds (digits only), in the range RevitCommandService accepts; null otherwise.
    static (TimeSpan, Func<string, string, string?>)? ReadTimeoutTransport(string text)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return null;
        var timeout = TimeSpan.FromSeconds(seconds);
        try { return (timeout, RevitCommandService.PipeTransportWithReadTimeout(timeout)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static string Refuse(string code, int index, string detail) =>
        RevitCommandResponse.Failure(code, "args[" + index.ToString(CultureInfo.InvariantCulture) + "]", detail + "; " + Usage).ToJson();
}
