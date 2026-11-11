using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Bim.Core;

// P1-20 `revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n]` through the real Release CLI.
// Oracles:
//   1. A value that is not whole seconds from 1 to 86400 (RevitCommandService.MaxReadTimeout), a missing value, a
//      repeated option and an unknown option are refused typed in the RevitCommandResponse shape (field args[i], usage
//      in the detail, exit 1) before any file or pipe I/O: the envelope file does not exist, the pipe name may be bad,
//      and the stalled add-in pipe receives nothing. In range (1 and 86400) the run goes on to the envelope read.
//   2. A stalled add-in pipe (StalledAddIn from tests/revit-commands/PipeReadTimeout.cs: connected, request read, no
//      line back) ends with pipe_read_timeout naming the chosen timeout, within the bound, sent once, for a single
//      command and for --all (listing shape).
//   3. Without the flag the CLI keeps the 1800 s default (still waiting on a stalled pipe after 3 s), and on a
//      replying add-in the stdout and the wire line are byte-equal with and without the flag.
// Unit-tested only; no Revit.
internal static class RevitCommandReadTimeoutTests
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string Usage = "usage: revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n]";
    const string Flag = "--read-timeout-seconds";
    // The code the CLI (and the MCP server option) print for a bad --read-timeout-seconds value.
    const string ReadTimeoutInvalid = "read_timeout_invalid";

    static string Envelope(string command, JsonObject args) => new JsonObject
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-p1-20",
        ["document"] = new JsonObject { ["path"] = @"C:\Projects\P1-20\Model.rvt", ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args,
    }.ToJsonString();

    static RevitCommandResponse? Reply(string text)
    {
        try { return RevitCommandResponse.Parse(text); }
        catch (Exception) { return null; }
    }

    static JsonObject? Node(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (Exception) { return null; }
    }

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

    // True when the CLI has not exited after the wait (then it is killed): a stalled pipe is not cut short.
    static bool StillWaiting(string dotnet, string cliDll, TimeSpan wait, params string[] arguments)
    {
        using var process = Process.Start(Start(dotnet, cliDll, arguments)) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit((int)wait.TotalMilliseconds);
        if (!exited) process.Kill(true);
        process.WaitForExit();
        return !exited;
    }

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        _ = root;
        var dir = Path.Combine(work, "p1-20");
        Directory.CreateDirectory(dir);
        var levelsText = Envelope("query.levels", new JsonObject());
        var levels = Path.Combine(dir, "levels.json");
        File.WriteAllText(levels, levelsText);
        var walls = Path.Combine(dir, "walls.json");
        File.WriteAllText(walls, Envelope("query.elements", new JsonObject { ["category"] = "wall", ["limit"] = 5 }));
        var missing = Path.Combine(dir, "missing-envelope.json");
        string Refusal(string code, int index, string detail) => RevitCommandResponse.Failure(code, "args[" + N(index) + "]", detail + "; " + Usage).ToJson();
        (int Exit, string Out, long Ms) Rc(params string[] arguments) => Cli(dotnet, cliDll, ["revit-command", .. arguments]);

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
                var single = Rc(missing, pipe, Flag, value);
                Check(single.Exit == 1 && single.Out == Refusal(ReadTimeoutInvalid, 4, Flag + " takes whole seconds from 1 to 86400, got " + value),
                    "revit-command " + Flag + " " + label + " is read_timeout_invalid at args[4] before any I/O", "exit " + single.Exit + " " + single.Out);
                var all = Rc(missing, pipe, "--all", Flag, value);
                Check(all.Exit == 1 && all.Out == Refusal(ReadTimeoutInvalid, 5, Flag + " takes whole seconds from 1 to 86400, got " + value),
                    "revit-command --all " + Flag + " " + label + " is read_timeout_invalid at args[5] in the single-command shape before any I/O", "exit " + all.Exit + " " + all.Out);
            }
            var noValue = Rc(missing, pipe, Flag);
            Check(noValue.Exit == 1 && noValue.Out == Refusal(ReadTimeoutInvalid, 3, Flag + " needs a value"), "revit-command " + Flag + " without a value is read_timeout_invalid at the flag", noValue.Out);
            var badPipe = Rc(missing, "not-a-process-pipe", Flag, "0");
            Check(badPipe.Exit == 1 && badPipe.Out == Refusal(ReadTimeoutInvalid, 4, Flag + " takes whole seconds from 1 to 86400, got 0"), "the options are checked before the pipe name", badPipe.Out);
            var repeated = Rc(missing, pipe, Flag, "5", Flag, "6");
            Check(repeated.Exit == 1 && repeated.Out == Refusal(RevitCommandCodes.ExplicitProcessPipeRequired, 5, "repeated option " + Flag), "a repeated " + Flag + " is refused at the repeat", repeated.Out);
            var repeatedAll = Rc(missing, pipe, "--all", "--all");
            Check(repeatedAll.Exit == 1 && repeatedAll.Out == Refusal(RevitCommandCodes.ExplicitProcessPipeRequired, 4, "repeated option --all"), "a repeated --all is refused at the repeat", repeatedAll.Out);
            var unknown = Rc(missing, pipe, Flag, "5", "--deadline");
            Check(unknown.Exit == 1 && unknown.Out == Refusal(RevitCommandCodes.ExplicitProcessPipeRequired, 5, "unknown option --deadline"), "an unknown option after " + Flag + " n is refused by name", unknown.Out);
            var unknownFirst = Rc(missing, pipe, "--everything");
            Check(unknownFirst.Exit == 1 && unknownFirst.Out == Refusal(RevitCommandCodes.ExplicitProcessPipeRequired, 3, "unknown option --everything"), "an unknown 4th argument keeps explicit_process_pipe_required at args[3]", unknownFirst.Out);
            foreach (var value in new[] { "1", "86400", "0030" })
            {
                var accepted = Rc(missing, pipe, Flag, value);
                Check(accepted.Exit == 1 && Reply(accepted.Out) is { Code: RevitCommandCodes.EnvelopeInvalid, Field: "envelope" },
                    "revit-command " + Flag + " " + value + " is accepted (the run goes on to the envelope read)", accepted.Out);
                var acceptedAll = Rc(missing, pipe, Flag, value, "--all");
                Check(acceptedAll.Exit == 1 && Node(acceptedAll.Out) is { } listing && listing["code"]?.GetValue<string>() == RevitCommandCodes.EnvelopeInvalid && listing["pages"]?.GetValue<int>() == 0,
                    "revit-command " + Flag + " " + value + " --all is accepted in any order (listing-shape envelope refusal)", acceptedAll.Out);
            }
            Check(addIn.Received.Count == 0, "no refusal or envelope failure reached the add-in pipe", N(addIn.Received.Count));

            // ---- 2. A stalled add-in pipe ends with pipe_read_timeout within the bound ----
            var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(levelsText));
            var stalled = Rc(levels, pipe, Flag, "1");
            var stalledReply = stalled.Exit == 1 ? Reply(stalled.Out) : null;
            Check(stalledReply is { Code: RevitCommandCodes.PipeReadTimeout, Field: "pipe_name", Detail: { } stalledDetail } && stalledDetail.StartsWith("no reply within 1000 ms; read-only", StringComparison.Ordinal),
                "revit-command " + Flag + " 1 on a stalled add-in pipe is pipe_read_timeout after 1000 ms", "exit " + stalled.Exit + " " + stalled.Out);
            Check(stalled.Ms >= 1000 && stalled.Ms < 30000, "the stalled run ends within the bound (about the read timeout, not 1800 s)", N(stalled.Ms) + " ms");
            Check(addIn.WaitForRequests(1) && addIn.Received.Count == 1 && addIn.Received[0] == wire, "the stalled request was sent once with the exact wire line", N(addIn.Received.Count));
            var stalledAll = Rc(walls, pipe, "--all", Flag, "2");
            Check(stalledAll.Exit == 1 && Node(stalledAll.Out) is { } allReply && allReply["status"]?.GetValue<string>() == "failed"
                && allReply["code"]?.GetValue<string>() == RevitCommandCodes.PipeReadTimeout && allReply["pages"]?.GetValue<int>() == 0
                && allReply["detail"]?.GetValue<string>() is { } allDetail && allDetail.Contains("2000 ms", StringComparison.Ordinal),
                "revit-command --all " + Flag + " 2 on a stalled pipe is pipe_read_timeout in the listing shape", stalledAll.Out);
            Check(stalledAll.Ms >= 2000 && stalledAll.Ms < 30000 && addIn.WaitForRequests(2) && addIn.Received.Count == 2, "the stalled --all run sends one page and ends within the bound", N(stalledAll.Ms) + " ms");

            // ---- 3. Absent flag: the default is kept and the bytes are today's ----
            Check(StillWaiting(dotnet, cliDll, TimeSpan.FromSeconds(3), "revit-command", levels, pipe), "without " + Flag + " the CLI still waits on a stalled pipe after 3 s (1800 s default)");
            Check(addIn.WaitForRequests(3) && addIn.Received.Count == 3 && addIn.Received[2] == wire, "the default run sent the same wire line");
            var addInReply = RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, "args", "p1-20 fake add-in").ToJson();
            addIn.Reply = _ => addInReply;
            var plain = Rc(levels, pipe);
            var flagged = Rc(levels, pipe, Flag, "30");
            Check(plain.Exit == 1 && plain.Out == addInReply && flagged.Exit == 1 && flagged.Out == plain.Out, "a replying add-in gives byte-equal stdout with and without " + Flag, plain.Out + " | " + flagged.Out);
            Check(addIn.WaitForRequests(5) && addIn.Received.Count == 5 && addIn.Received[3] == wire && addIn.Received[4] == wire, "the wire line is the same with and without " + Flag);
            var plainAll = Rc(walls, pipe, "--all");
            var flaggedAll = Rc(walls, pipe, Flag, "30", "--all");
            Check(plainAll.Exit == 1 && Node(plainAll.Out)?["code"]?.GetValue<string>() == RevitCommandCodes.TransactionFailed && flaggedAll.Out == plainAll.Out,
                "--all on a replying add-in gives byte-equal stdout with and without " + Flag, plainAll.Out + " | " + flaggedAll.Out);
            addIn.Reply = null;
        }
        finally
        {
            if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR", tmpDir);
        }
    }
}
