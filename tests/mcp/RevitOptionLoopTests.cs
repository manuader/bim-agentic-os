using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Bim.Core;
using Bim.Mcp;

// P1-24 one any-order option loop for `revit-command` and the MCP server. Oracles:
//   1. Refusal bytes: a table of option lists (any order, repeats, unknown names, missing and bad values, a value that
//      looks like a flag, which refusal wins when several apply) pins the exact refusal line of
//      RevitCommandOptions.Parse and McpServer.ParseOptions in process, and a few through the real Release Bim.Cli.dll /
//      Bim.Mcp.dll. The lines are written out literally here (no RevitCommandResponse.Failure), so they are the bytes
//      that held before the refactor; accepted lists keep their values.
//   2. Static: the at-most-once/any-order loop is one RevitOptionGrammar method (RevitOptionGrammar.Options, taking the
//      accepted option set) and the only loop in interfaces/cli/RevitCommandOptions.cs; RevitCommandOptions.Parse and
//      McpServer.ParseOptions call it and have no loop or unknown/repeated refusal of their own.
//   3. Static: RevitOptionGrammar and RevitCommandOptions are not exported by any assembly that compiles the shared file
//      (Bim.Cli.dll, Bim.Mcp.dll and the three test assemblies), read from their metadata.
// Unit-tested only; no Revit.
internal static class RevitOptionLoopTests
{
    const string CliUsage = "usage: revit-command envelope.json process-pipe-name [--all] [--read-timeout-seconds n]";
    const string CliDeadlineUsage = "usage: revit-command envelope.json process-pipe-name --all [--read-timeout-seconds n] --deadline-seconds n";
    const string McpUsage = "usage: Bim.Mcp.dll [--read-timeout-seconds n]";
    const string Rt = "--read-timeout-seconds";
    const string Dl = "--deadline-seconds";
    const string Pipe = "explicit_process_pipe_required";
    const string Unknown = "mcp_option_unknown";
    const string ReadBad = "read_timeout_invalid";
    const string DeadlineBad = "deadline_invalid";

    // The RevitCommandResponse failure line, spelled out (details here hold no character JSON escapes).
    static string Line(string code, int index, string detail, string usage) =>
        "{\"status\":\"failed\",\"code\":\"" + code + "\",\"field\":\"args[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]\",\"detail\":\"" + detail + "; " + usage
        + "\",\"created\":[],\"modified\":[],\"deleted\":[],\"readback\":null,\"receipt\":null,\"warnings\":[]}";

    static string Whole(string option, string value) => option + " takes whole seconds from 1 to 86400, got " + value;

    public static void Run(string root, string cliDll, string mcpDll, Func<string[], string> cli, Func<string[], (int Exit, string Out, string Err)> mcp, Action<bool, string> check)
    {
        // ---- 1. refusal bytes, in process ----
        string[] Cmd(params string[] options) => ["revit-command", "missing-envelope.json", "bim-agentic-os-2025-1", .. options];
        var cliCases = new (string[] Options, string Expected)[]
        {
            (["--bogus"], Line(Pipe, 3, "unknown option --bogus", CliUsage)),
            (["--all", "--all"], Line(Pipe, 4, "repeated option --all", CliUsage)),
            ([Rt, "5", Rt, "6"], Line(Pipe, 5, "repeated option " + Rt, CliUsage)),
            ([Dl, "5", "--all", Dl, "6"], Line(Pipe, 6, "repeated option " + Dl, CliUsage)),
            ([Rt], Line(ReadBad, 3, Rt + " needs a value", CliUsage)),
            (["--all", Rt], Line(ReadBad, 4, Rt + " needs a value", CliUsage)),
            ([Rt, "--all"], Line(ReadBad, 4, Whole(Rt, "--all"), CliUsage)),
            (["--all", Rt, "0", Rt, "5"], Line(ReadBad, 5, Whole(Rt, "0"), CliUsage)),
            ([Rt, "0", "--bogus"], Line(ReadBad, 4, Whole(Rt, "0"), CliUsage)),
            (["--bogus", Rt, "0"], Line(Pipe, 3, "unknown option --bogus", CliUsage)),
            ([Rt, "5", "--deadline"], Line(Pipe, 5, "unknown option --deadline", CliUsage)),
            (["--all", Dl], Line(DeadlineBad, 4, Dl + " needs a value", CliDeadlineUsage)),
            ([Dl, "0", "--all"], Line(DeadlineBad, 4, Whole(Dl, "0"), CliDeadlineUsage)),
            ([Dl, Dl], Line(DeadlineBad, 4, Whole(Dl, Dl), CliDeadlineUsage)),
            ([Dl, "5"], Line(DeadlineBad, 3, Dl + " needs --all", CliDeadlineUsage)),
            ([Rt, "5", Dl, "5"], Line(DeadlineBad, 5, Dl + " needs --all", CliDeadlineUsage)),
            ([Dl, "5", "--bogus"], Line(Pipe, 5, "unknown option --bogus", CliUsage)),
            ([Dl, "0", "--bogus"], Line(DeadlineBad, 4, Whole(Dl, "0"), CliDeadlineUsage)),
            (["--all", Dl, "5", Rt, "0"], Line(ReadBad, 7, Whole(Rt, "0"), CliUsage)),
        };
        foreach (var (options, expected) in cliCases)
        {
            var parsed = Bim.Cli.RevitCommandOptions.Parse(Cmd(options), out var refusal);
            check(parsed is null && refusal == expected, "P1-24 revit-command [" + string.Join(" ", options) + "] keeps its refusal line | " + (refusal ?? "accepted"));
        }
        foreach (var (options, all, read, deadline) in new (string[] Options, bool All, int Read, int? Deadline)[]
        {
            ([], false, 1800, null), (["--all"], true, 1800, null), ([Rt, "9"], false, 9, null),
            ([Dl, "7", "--all", Rt, "9"], true, 9, 7), ([Rt, "0030", "--all", Dl, "86400"], true, 30, 86400),
        })
        {
            var parsed = Bim.Cli.RevitCommandOptions.Parse(Cmd(options), out var refusal);
            check(refusal is null && parsed is { } accepted && accepted.All == all && accepted.ReadTimeout == TimeSpan.FromSeconds(read)
                && accepted.Deadline == (deadline is { } seconds ? TimeSpan.FromSeconds(seconds) : null)
                && (read == 1800) == accepted.Transport.Equals((Func<string, string, string?>)RevitCommandService.PipeTransport),
                "P1-24 revit-command [" + string.Join(" ", options) + "] is accepted with its values, any order | " + (refusal ?? ""));
        }
        var mcpCases = new (string[] Options, string Expected)[]
        {
            (["--bogus"], Line(Unknown, 0, "unknown option --bogus", McpUsage)),
            (["--all"], Line(Unknown, 0, "unknown option --all", McpUsage)),
            ([Dl, "5"], Line(Unknown, 0, "unknown option " + Dl, McpUsage)),
            ([Rt, "5", Rt, "6"], Line(Unknown, 2, "repeated option " + Rt, McpUsage)),
            ([Rt], Line(ReadBad, 0, Rt + " needs a value", McpUsage)),
            ([Rt, Rt], Line(ReadBad, 1, Whole(Rt, Rt), McpUsage)),
            ([Rt, "5", "--bogus"], Line(Unknown, 2, "unknown option --bogus", McpUsage)),
            ([Rt, "0", "--bogus"], Line(ReadBad, 1, Whole(Rt, "0"), McpUsage)),
            (["--bogus", Rt, "0"], Line(Unknown, 0, "unknown option --bogus", McpUsage)),
            ([Rt, "5", Dl, "5"], Line(Unknown, 2, "unknown option " + Dl, McpUsage)),
            ([Rt, "5", "--all"], Line(Unknown, 2, "unknown option --all", McpUsage)),
        };
        foreach (var (options, expected) in mcpCases)
        {
            var refusal = McpServer.ParseOptions(options, out _);
            check(refusal == expected, "P1-24 Bim.Mcp [" + string.Join(" ", options) + "] keeps its refusal line | " + (refusal ?? "accepted"));
        }
        check(McpServer.ParseOptions([Rt, "0030"], out var mcpThirty) is null && mcpThirty == TimeSpan.FromSeconds(30)
            && McpServer.ParseOptions([], out var mcpDefault) is null && mcpDefault == RevitCommandService.DefaultReadTimeout,
            "P1-24 Bim.Mcp [--read-timeout-seconds 0030] is 30 s and no option keeps the 1800 s default");

        // ---- 1b. the same bytes through the real Release binaries ----
        foreach (var options in new[] { new[] { Rt, "--all" }, new[] { Dl, "5", "--bogus" }, new[] { Dl, "5" }, new[] { "--all", Dl, "5", Rt, "0" } })
        {
            var expected = cliCases.Single(entry => entry.Options.SequenceEqual(options)).Expected;
            var output = cli(Cmd(options));
            check(output == expected, "P1-24 real Bim.Cli.dll revit-command [" + string.Join(" ", options) + "] writes its refusal line | " + output);
        }
        foreach (var options in new[] { new[] { "--all" }, new[] { Rt, "5", "--bogus" }, new[] { Rt, Rt } })
        {
            var expected = mcpCases.Single(entry => entry.Options.SequenceEqual(options)).Expected;
            var run = mcp(options);
            check(run.Exit == 1 && run.Out.Length == 0 && run.Err == expected, "P1-24 real Bim.Mcp.dll [" + string.Join(" ", options) + "] writes its refusal line on stderr | " + run.Err);
        }

        // ---- 2. static: one option loop ----
        var problems = new List<string>();
        var optionsSource = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "RevitCommandOptions.cs"));
        var serverSource = File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs"));
        var loop = new Regex(@"\b(for|foreach|while)\s*\(", RegexOptions.CultureInvariant);
        var optionsCode = Code(optionsSource);
        var grammarStart = optionsCode.IndexOf("class RevitOptionGrammar", StringComparison.Ordinal);
        var loops = loop.Matches(optionsCode).Select(match => match.Index).ToArray();
        if (loops.Length != 1 || grammarStart < 0 || loops[0] < grammarStart)
            problems.Add("RevitCommandOptions.cs has " + loops.Length + " loops; expected exactly one, inside RevitOptionGrammar");
        var grammarOptions = Body(optionsCode, "static RevitOptionValues? Options(");
        if (grammarOptions.Length == 0 || !loop.IsMatch(grammarOptions)) problems.Add("RevitOptionGrammar.Options(args, first, accepted, ...) holding the loop is missing");
        foreach (var (name, body) in new[] { ("RevitCommandOptions.Parse", Body(optionsCode, "static RevitCommandOptions? Parse(")), ("McpServer.ParseOptions", Body(Code(serverSource), "static string? ParseOptions(")) })
        {
            if (body.Length == 0) { problems.Add(name + " not found"); continue; }
            if (!body.Contains("RevitOptionGrammar.Options(", StringComparison.Ordinal)) problems.Add(name + " does not call RevitOptionGrammar.Options");
            if (loop.IsMatch(body)) problems.Add(name + " has a loop of its own");
            foreach (var token in new[] { "Unknown(", "Repeated(", "args[index]", "seen" })
                if (body.Contains(token, StringComparison.Ordinal)) problems.Add(name + " has '" + token + "'");
        }

        // ---- 3. static: not exported by any linking assembly ----
        // Only assemblies this run already depends on (and builds before it); other test projects' bin output may be stale or absent.
        var assemblies = new[] { cliDll, mcpDll, typeof(RevitOptionLoopTests).Assembly.Location };
        foreach (var assembly in assemblies)
        {
            if (!File.Exists(assembly)) { problems.Add("assembly missing: " + assembly); continue; }
            var exported = Exported(assembly);
            if (Path.GetFileName(assembly) == "Bim.Mcp.dll" && !exported.Contains("Bim.Mcp.McpServer")) problems.Add("Bim.Mcp.dll export list unreadable (no Bim.Mcp.McpServer)");
            foreach (var type in new[] { "Bim.Cli.RevitOptionGrammar", "Bim.Cli.RevitCommandOptions", "Bim.Cli.RevitOptionValues" })
                if (exported.Contains(type)) problems.Add(Path.GetFileName(assembly) + " exports " + type);
        }
        check(problems.Count == 0, "P1-24 one RevitOptionGrammar.Options loop used by revit-command and Bim.Mcp, and its types are internal [" + string.Join("; ", problems) + "]");
    }

    // The source without // line comments (and without string contents that could look like code: none here hold "//").
    static string Code(string source) => string.Join("\n", source.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line));

    // From the signature to the brace matching the first '{' after it; "" when absent.
    static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        var open = start < 0 ? -1 : source.IndexOf('{', start);
        if (open < 0) return "";
        var depth = 0;
        for (var at = open; at < source.Length; at++)
        {
            if (source[at] == '{') depth++;
            else if (source[at] == '}' && --depth == 0) return source[start..(at + 1)];
        }
        return "";
    }

    // Top-level public type names (namespace.name) from the assembly metadata, without loading it.
    static string[] Exported(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
            .Where(type => (type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public)
            .Select(type => metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name)).ToArray();
    }
}
