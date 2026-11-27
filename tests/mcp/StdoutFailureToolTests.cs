using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Mcp;

// P5-37: an MCP server stdout that fails while a jurisdiction_resolve or skill_list reply is written. Oracles:
//   a. In process: McpServer.Run over a stdin of three requests (the first a jurisdiction_resolve success, a
//      jurisdiction_unknown refusal or a skill_list listing, then the other tools and a ping) with an output whose every
//      write throws (the shared RejectingWriter: IOException, ObjectDisposedException, InvalidOperationException): nothing
//      escapes Run, which returns that very exception (null at the end of input), exactly one write attempt (the first
//      reply), and serving stops there: the rest of stdin is left
//      unread, byte for byte, so no later request is executed. Control: with a working writer the replies are the
//      McpServer.Handle bytes of each request in order (success and refusal bytes unchanged).
//   b. Real Release Bim.Mcp.dll (Linux; SKIP on Windows: no /bin/sh): stdout >/dev/full (every write fails ENOSPC) or
//      open read-only (1</dev/null, EBADF) while the first reply (jurisdiction_resolve or skill_list) is written: exit 1,
//      nothing on the reader's stdout, and stderr exactly one line "mcp stdout failed: <type>: <message>" (no
//      unhandled-exception crash), the type as the runtime maps the errno (measured on Linux): ENOSPC is
//      System.IO.IOException, EBADF System.UnauthorizedAccessException.
//   c. Real process, every OS this suite runs on (Linux, Windows): a stdout pipe the reader closes right after start,
//      before any request is sent: exit 0 and stderr empty after answering every request, because the runtime's console
//      stream drops EPIPE / ERROR_NO_DATA, so the server never sees the failure (as P3-34 measured for stderr).
//   d. In process: jurisdiction_resolve with JurisdictionResolveCommand.WorkProbe throwing an exception whose Message
//      getter throws (the one case Resolve throws, tests/cli/SkillListTests.cs section 18) is McpServer.CallTool's
//      generic failure {"status":"failed","code":<the getter's message>}, isError, and the server keeps answering.
// The macOS CI job runs no tests/mcp section but section 9 (P3-38), so the MCP stdout probes are measured on Linux and
// Windows only; the macOS stdout-failure equivalent is measured for the CLI verbs (tests/cli section 18, 1</dev/null).
internal static class StdoutFailureToolTests
{
    const string GetterMessage = "message getter P5-37 mcp";

    public static void Run(string root, string dotnet, string mcpDll, Action<bool, string> check)
    {
        // Every check is recorded; failures are reported together at the end, so one run shows every measurement.
        var failures = new List<string>();
        void Check(bool condition, string name)
        {
            if (condition) check(true, "P5-37 mcp stdout: " + name);
            else failures.Add(name);
        }
        static string Short(string text) => text.Length > 300 ? text[..300] + "..." : text;
        static string Call(int id, string tool, string arguments) =>
            "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}}";
        var resolved = Call(1, "jurisdiction_resolve", "{\"code\":\"ar.caba\"}");
        var unknown = Call(2, "jurisdiction_resolve", "{\"code\":\"zz.nowhere\"}");
        var listing = Call(3, "skill_list", "{}");
        const string Ping = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"ping\"}";
        Check(Directory.Exists(Path.Combine(root, "skills")), "fixture: the repository skills directory exists");

        // ---- a. in process ----
        var orders = new (string Label, string[] Requests)[]
        {
            ("jurisdiction_resolve success first", [resolved, listing, Ping]),
            ("jurisdiction_resolve refusal first", [unknown, resolved, Ping]),
            ("skill_list first", [listing, resolved, Ping]),
        };
        foreach (var (label, requests) in orders)
        {
            var stdin = string.Join("\n", requests) + "\n";
            var working = new StringWriter(CultureInfo.InvariantCulture);
            var escapedWorking = "";
            Exception? returnedWorking = null;
            try { returnedWorking = McpServer.Run(new StringReader(stdin), working, new McpServer()); }
            catch (Exception error) { escapedWorking = error.GetType().FullName + ": " + error.Message; }
            var expected = string.Concat(requests.Select(request => new McpServer().Handle(request) + "\n"));
            var first = JsonNode.Parse(new McpServer().Handle(requests[0]) ?? "null")?["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
            Check(escapedWorking.Length == 0 && returnedWorking is null && working.ToString() == expected && first.StartsWith("{\"status\":", StringComparison.Ordinal),
                "control (" + label + "): a working stdout gets each request's McpServer.Handle bytes in order and Run returns null at the end of input | " + Short(working.ToString()) + " " + escapedWorking);
            foreach (var rejection in new Exception[] { new IOException("stdout full P5-37"), new ObjectDisposedException("stdout P5-37"), new InvalidOperationException("stdout rejected P5-37") })
            {
                var writer = new RejectingWriter(rejection);
                var reader = new StringReader(stdin);
                var escaped = "";
                Exception? returned = null;
                try { returned = McpServer.Run(reader, writer, new McpServer()); }
                catch (Exception error) { escaped = error.GetType().FullName + ": " + error.Message; }
                var rest = reader.ReadToEnd();
                var unread = string.Join("\n", requests.Skip(1)) + "\n";
                Check(escaped.Length == 0 && ReferenceEquals(returned, rejection) && writer.Attempts == 1 && rest == unread,
                    "stdout throws " + rejection.GetType().Name + " on the first reply (" + label + "): nothing escapes McpServer.Run, which returns that exception, one write attempt, serving stops (the rest of stdin unread) | escaped "
                    + (escaped.Length == 0 ? "nothing" : escaped) + " attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture) + " rest " + JsonSerializer.Serialize(Short(rest)));
            }
        }

        // ---- b. real process, stdout rejecting every write (Linux) ----
        (int Exit, string Out, string Err) Server(string? redirect, bool closeStdout, string stdin)
        {
            var start = new ProcessStartInfo(redirect is null ? dotnet : "/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            if (redirect is not null) { start.ArgumentList.Add("-c"); start.ArgumentList.Add("exec \"$@\" " + redirect); start.ArgumentList.Add("sh"); start.ArgumentList.Add(dotnet); }
            start.ArgumentList.Add(mcpDll);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: " + start.FileName);
            Task<string> stdout;
            if (closeStdout) { process.StandardOutput.Close(); stdout = Task.FromResult(""); }
            else stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { process.StandardInput.Write(stdin); process.StandardInput.Close(); } catch (IOException) { }
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result, stderr.Result);
        }
        static string P((int Exit, string Out, string Err) r) =>
            "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " out " + JsonSerializer.Serialize(Short(r.Out)) + " err " + JsonSerializer.Serialize(Short(r.Err));
        var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        if (OperatingSystem.IsWindows())
            Console.WriteLine("SKIP P5-37 mcp stdout: >/dev/full and 1</dev/null need /bin/sh (Linux); the closed pipe runs on Windows");
        else
        {
            foreach (var (redirect, type) in new[] { (">/dev/full", "System.IO.IOException"), ("1</dev/null", "System.UnauthorizedAccessException") })
            {
                var failurePrefix = "mcp stdout failed: " + type + ": ";
                if (redirect == ">/dev/full" && !File.Exists("/dev/full")) { Console.WriteLine("SKIP P5-37 mcp stdout: no /dev/full on " + os); continue; }
                foreach (var (label, requests) in new (string, string[])[] { ("jurisdiction_resolve", [resolved, listing, Ping]), ("skill_list", [listing, resolved, Ping]) })
                {
                    var run = Server(redirect, false, string.Join("\n", requests) + "\n");
                    var err = run.Err.TrimEnd('\r', '\n');
                    Check(run.Exit == 1 && run.Out.Length == 0 && err.StartsWith(failurePrefix, StringComparison.Ordinal) && !err.Contains('\n', StringComparison.Ordinal)
                        && !run.Err.Contains("Unhandled exception", StringComparison.Ordinal),
                        "real Bim.Mcp.dll with stdout " + redirect + " during the " + label + " reply (" + os + "): exit 1, one stderr line '" + failurePrefix + "...', no unhandled exception | " + P(run));
                }
            }
        }

        // ---- c. real process, stdout pipe closed by the reader before any request ----
        {
            var run = Server(null, true, resolved + "\n" + unknown + "\n" + listing + "\n" + Ping + "\n");
            Check(run.Exit == 0 && run.Err.Length == 0,
                "real Bim.Mcp.dll with its stdout pipe closed by the reader before the requests (" + os + "): exit 0, stderr empty (the runtime drops the broken pipe; the server never sees it) | " + P(run));
        }

        // ---- d. in process: the one case Resolve throws, through the MCP tool ----
        {
            var server = new McpServer();
            Bim.Cli.JurisdictionResolveCommand.WorkProbe = () => throw new MessageGetterThrowsException();
            string? response;
            try { response = server.Handle(resolved); }
            finally { Bim.Cli.JurisdictionResolveCommand.WorkProbe = null; }
            var result = response is null ? null : JsonNode.Parse(response)?["result"];
            var text = result?["content"]?[0]?["text"]?.GetValue<string>();
            Check(result?["isError"]?.GetValue<bool>() == true && text == JsonSerializer.Serialize(new { status = "failed", code = GetterMessage }),
                "jurisdiction_resolve with WorkProbe throwing an exception whose Message getter throws: McpServer's generic failure carrying the getter's message, isError | " + Short(response ?? "none"));
            var after = server.Handle(Ping);
            Check(after == "{\"jsonrpc\":\"2.0\",\"id\":4,\"result\":{}}" && Bim.Cli.JurisdictionResolveCommand.WorkProbe is null,
                "the server keeps answering after it (ping) and the seam is null again | " + (after ?? "none"));
        }

        check(failures.Count == 0, "P5-37 mcp stdout: every check passed; failing (" + failures.Count.ToString(CultureInfo.InvariantCulture) + "): " + string.Join(" || ", failures));

        // P5-41 runs before P5-39: the harness Check throws on the first failing collector, and P5-41's checks name the
        // stderr fallback directly, while P5-39 section c only reads the P5-41 delta reversed.
        RunP5_41(root, dotnet, mcpDll, check);
        RunP5_39(root, check);
    }

    // P5-39: the remaining MCP stdout edges of the P5-37 oracle. Oracles (a and b in process, no transport sends):
    //   a. request_too_large: a line longer than McpServer.MaxLineChars + 1 is answered by Run itself, not by Handle, so
    //      its reply goes through the same guarded Emit. With a rejecting writer (IOException, ObjectDisposedException,
    //      InvalidOperationException) nothing escapes Run, which returns that very exception, exactly one write attempt,
    //      and serving stops there: the rest of stdin is left unread, byte for byte — whether the oversized line ends
    //      with '\n' or at end of input. Control: a working writer gets the literal
    //      {"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"request_too_large"}} line and then each later
    //      request's McpServer.Handle bytes in order.
    //   b. The two-argument Run(input, output) overload DISCARDS the failure: with a rejecting writer it returns normally,
    //      nothing escapes, one write attempt, serving stops, and no signal reaches the caller. PINNED AS MEASURED (its
    //      only callers are tests; interfaces/mcp/Program.cs calls the three-argument overload, which reports it). The
    //      overload stays `public static void Run(` — the text tests/mcp/RepeatedEnvelopeKeyTests.cs uses as the end
    //      marker of Handle's body in its P10-36 static pin — so making it internal must update that pin; this section
    //      pins the marker, its uniqueness and both overloads' shapes.
    //   c. interfaces/mcp/Program.cs stderr line: "mcp stdout failed: <type>: <message>" stays exactly one line even when
    //      the write exception's Message holds CR, LF, CRLF or another Unicode line break, because the message is folded
    //      with ReplaceLineEndings(" "); a Message that is null prints as empty instead of losing the line to a
    //      NullReferenceException. The source statement is pinned and the formula is self-tested on a replica, with the
    //      unfolded formula as the mutant (it makes more than one line for the same messages). A Message getter that
    //      throws makes this P5-39 formula throw (pinned as measured on the replica). Program.cs no longer runs it: since
    //      P5-41 this section reads Program.cs with ProgramDeltaP541 reversed, and RunP5_41 pins the fallback that prints
    //      the getter's exception type instead.
    static void RunP5_39(string root, Action<bool, string> check)
    {
        var failures = new List<string>();
        void Check(bool condition, string name)
        {
            if (condition) check(true, "P5-39 mcp stdout edges: " + name);
            else failures.Add(name);
        }
        static string Short(string text) => text.Length > 300 ? text[..300] + "..." : text;
        static int Count(string text, string part)
        {
            var n = 0;
            for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal)) n++;
            return n;
        }
        const string TooLarge = """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"request_too_large"}}""";
        const string Resolved = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"jurisdiction_resolve","arguments":{"code":"ar.caba"}}}""";
        const string Ping = """{"jsonrpc":"2.0","id":4,"method":"ping"}""";
        var oversized = "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/list\",\"params\":{\"pad\":\"" + new string('a', McpServer.MaxLineChars) + "\"}}";
        Check(oversized.Length > McpServer.MaxLineChars + 1, "fixture: the oversized line is longer than MaxLineChars + 1 (" + oversized.Length.ToString(CultureInfo.InvariantCulture) + " chars)");
        Check(new McpServer().Handle(oversized) == TooLarge, "fixture: Handle answers an oversized line with the same literal request_too_large line | " + Short(new McpServer().Handle(oversized) ?? "none"));

        // ---- a. the request_too_large reply ----
        foreach (var (label, stdin, unread) in new (string, string, string)[]
        {
            ("the oversized line first", oversized + "\n" + Resolved + "\n" + Ping + "\n", Resolved + "\n" + Ping + "\n"),
            ("the oversized line at end of input", oversized, ""),
        })
        {
            var expected = TooLarge + "\n" + (unread.Length == 0 ? "" : new McpServer().Handle(Resolved) + "\n" + new McpServer().Handle(Ping) + "\n");
            var working = new StringWriter(CultureInfo.InvariantCulture);
            var escapedWorking = "";
            Exception? returnedWorking = null;
            try { returnedWorking = McpServer.Run(new StringReader(stdin), working, new McpServer()); }
            catch (Exception error) { escapedWorking = error.GetType().FullName + ": " + error.Message; }
            Check(escapedWorking.Length == 0 && returnedWorking is null && working.ToString() == expected,
                "control (" + label + "): a working stdout gets the literal request_too_large line and then each later request's McpServer.Handle bytes in order, Run returns null | "
                + Short(working.ToString()) + " " + escapedWorking);
            foreach (var rejection in new Exception[] { new IOException("stdout full P5-39"), new ObjectDisposedException("stdout P5-39"), new InvalidOperationException("stdout rejected P5-39") })
            {
                var writer = new RejectingWriter(rejection);
                var reader = new StringReader(stdin);
                var escaped = "";
                Exception? returned = null;
                try { returned = McpServer.Run(reader, writer, new McpServer()); }
                catch (Exception error) { escaped = error.GetType().FullName + ": " + error.Message; }
                var rest = reader.ReadToEnd();
                Check(escaped.Length == 0 && ReferenceEquals(returned, rejection) && writer.Attempts == 1 && rest == unread,
                    "stdout throws " + rejection.GetType().Name + " on the request_too_large reply (" + label + "): nothing escapes McpServer.Run, which returns that exception, one write attempt, serving stops (the rest of stdin unread) | escaped "
                    + (escaped.Length == 0 ? "nothing" : escaped) + " attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture) + " rest " + JsonSerializer.Serialize(Short(rest)));
            }
        }

        // ---- b. the two-argument overload discards the failure ----
        {
            var stdin = oversized + "\n" + Resolved + "\n" + Ping + "\n";
            var writer = new RejectingWriter(new IOException("stdout full P5-39 two-argument"));
            var reader = new StringReader(stdin);
            var escaped = "";
            try { McpServer.Run(reader, writer); }
            catch (Exception error) { escaped = error.GetType().FullName + ": " + error.Message; }
            var rest = reader.ReadToEnd();
            Check(escaped.Length == 0 && writer.Attempts == 1 && rest == Resolved + "\n" + Ping + "\n",
                "PINNED AS MEASURED: the two-argument Run(input, output) with a rejecting stdout returns normally and discards the exception (nothing escapes, one write attempt, serving stops); only the three-argument overload reports it, which interfaces/mcp/Program.cs calls | escaped "
                + (escaped.Length == 0 ? "nothing" : escaped) + " attempts " + writer.Attempts.ToString(CultureInfo.InvariantCulture) + " rest " + JsonSerializer.Serialize(Short(rest)));
            var working = new StringWriter(CultureInfo.InvariantCulture);
            McpServer.Run(new StringReader(Resolved + "\n" + Ping + "\n"), working);
            Check(working.ToString() == new McpServer().Handle(Resolved) + "\n" + new McpServer().Handle(Ping) + "\n",
                "control: over a working stdout the two-argument overload answers exactly like the three-argument one | " + Short(working.ToString()));
            var twoArgument = typeof(McpServer).GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(TextReader), typeof(TextWriter) }, null);
            var threeArgument = typeof(McpServer).GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, new[] { typeof(TextReader), typeof(TextWriter), typeof(McpServer) }, null);
            Check(twoArgument is { IsPublic: true, IsStatic: true } && twoArgument.ReturnType == typeof(void)
                && threeArgument is { IsPublic: true, IsStatic: true } && threeArgument.ReturnType == typeof(Exception),
                "Run: a public static void two-argument overload (the discarding one) and a public static Exception? three-argument overload | "
                + (twoArgument?.ReturnType.FullName ?? "<missing>") + " / " + (threeArgument?.ReturnType.FullName ?? "<missing>"));
            var serverPath = Path.Combine(root, "interfaces", "mcp", "McpServer.cs");
            var serverSource = File.Exists(serverPath) ? DocUpdateLinkTests.CodeOf(File.ReadAllText(serverPath).ReplaceLineEndings("\n")) : "";
            var markers = Count(serverSource, "public static void Run(");
            Check(markers == 1 && serverSource.Contains("public static void Run(TextReader input, TextWriter output) => Run(input, output, new McpServer());", StringComparison.Ordinal),
                "the end marker tests/mcp/RepeatedEnvelopeKeyTests.cs's P10-36 static pin searches for (\"public static void Run(\") occurs exactly once in McpServer.cs, on the discarding overload: making that overload internal must update that pin | markers "
                + markers.ToString(CultureInfo.InvariantCulture));
        }

        // ---- c. the "mcp stdout failed" stderr line stays one line ----
        {
            static int Lines(string text) => text.ReplaceLineEndings("\n").Split('\n').Length;
            // A replica of the interfaces/mcp/Program.cs statement pinned below.
            static string Line(Exception failure)
            {
                string? failureMessage = failure.Message;
                return "mcp stdout failed: " + failure.GetType().FullName + ": " + (failureMessage?.ReplaceLineEndings(" ") ?? "");
            }
            static string Unfolded(Exception failure) => "mcp stdout failed: " + failure.GetType().FullName + ": " + failure.Message;
            var programPath = Path.Combine(root, "interfaces", "mcp", "Program.cs");
            // P5-41 documented delta: Program.cs is read with ProgramDeltaP541 reversed (the P5-39 text; RunP5_41 pins the new statement).
            var program = File.Exists(programPath) ? DocUpdateLinkTests.CodeOf(ReverseDelta(File.ReadAllText(programPath).ReplaceLineEndings("\n"), ProgramDeltaP541) ?? "") : "";
            const string Statement = "try { string? failureMessage=stdoutFailure.Message; Console.Error.WriteLine(\"mcp stdout failed: \"+stdoutFailure.GetType().FullName+\": \"+(failureMessage?.ReplaceLineEndings(\" \")??\"\")); } catch(Exception) { }";
            Check(Count(program, Statement) == 1 && Count(program, "mcp stdout failed") == 1,
                "interfaces/mcp/Program.cs writes the diagnostic exactly once, through the pinned statement that folds the message with ReplaceLineEndings(\" \") and prints a null Message as empty | occurrences "
                + Count(program, Statement).ToString(CultureInfo.InvariantCulture) + "/" + Count(program, "mcp stdout failed").ToString(CultureInfo.InvariantCulture));
            foreach (var (label, message) in new (string, string)[]
            {
                ("CRLF", "first P5-39\r\nsecond"),
                ("LF", "first P5-39\nsecond"),
                ("CR", "first P5-39\rsecond"),
                ("a trailing newline", "first P5-39\n"),
                ("a leading newline", "\nfirst P5-39"),
                ("two newlines in a row", "a\n\nb P5-39"),
                ("U+0085 NEL", "a\u0085b P5-39"),
                ("U+2028 LINE SEPARATOR", "a\u2028b P5-39"),
                ("no line break at all", "plain P5-39"),
            })
            {
                var failure = new IOException(message);
                var folded = Line(failure);
                Check(folded.StartsWith("mcp stdout failed: System.IO.IOException: ", StringComparison.Ordinal) && Lines(folded) == 1,
                    "the stderr line formula folds " + label + " into one line | " + JsonSerializer.Serialize(folded));
                if (label != "no line break at all")
                    Check(Lines(Unfolded(failure)) > 1,
                        "mutant self-test: the unfolded formula makes more than one line for " + label + " | " + JsonSerializer.Serialize(Unfolded(failure)));
            }
            Check(Line(new NullMessageException()) == "mcp stdout failed: " + typeof(NullMessageException).FullName + ": ",
                "a Message that is null prints as empty, so the line is not lost to a NullReferenceException inside the guarded write | " + JsonSerializer.Serialize(Line(new NullMessageException())));
            var threw = "";
            try { Line(new MessageGetterThrowsException()); }
            catch (Exception error) { threw = error.GetType().FullName ?? ""; }
            Check(threw == "System.InvalidOperationException",
                "PINNED AS MEASURED: a write exception whose Message getter throws makes the P5-39 formula throw (Program.cs replaced it in P5-41; RunP5_41 pins the type fallback) | " + (threw.Length == 0 ? "did not throw" : threw));
        }

        check(failures.Count == 0, "P5-39 mcp stdout edges: every check passed; failing (" + failures.Count.ToString(CultureInfo.InvariantCulture) + "): " + string.Join(" || ", failures));
    }

    // P5-41: the 'mcp stdout failed' stderr line falls back to types when the write exception's Message getter throws.
    // Oracles:
    //   a. interfaces/mcp/Program.cs is the P5-39 file plus the documented delta ProgramDeltaP541 (the stderr statement and
    //      its comment sentence): reversed, it gives the P5-39 text RunP5_39 section c reads; the new statement occurs once
    //      and "mcp stdout failed" once.
    //   b. Replica self-test of the new statement: for every P5-39 message (line breaks, null) it prints the P5-39 line
    //      byte for byte; for an exception whose Message getter throws it prints exactly one line
    //      "mcp stdout failed: <type>: <Message threw <the getter's exception type>>" (also two levels deep: only types are
    //      read), where the P5-39 replica (the mutant) throws and the line was lost.
    //   c. Real Release Bim.Mcp.dll (Linux; SKIP elsewhere: /bin/sh and /dev/full): an oversized first line (longer than
    //      MaxLineChars + 1, answered by Run's request_too_large reply) with stdout >/dev/full or 1</dev/null: exit 1,
    //      stdout empty, stderr exactly one line "mcp stdout failed: <type>: ..." (IOException / UnauthorizedAccessException
    //      as P5-37 measured), no unhandled exception. Its reach, stated: a real IOException has a readable Message, so the
    //      real run never reaches the throwing-getter fallback: only the replica (b) covers it (no probe in Program.cs can
    //      plant such an exception in a real process). The run overlaps P5-37 b and P5-39 section a: P5-37 b runs the same
    //      redirects and one-line stderr through a regular first reply, and P5-39 section a the same request_too_large
    //      reply in process; this is that reply through the real process, kept separate, not folded into P5-39 section a,
    //      because that section runs in process on every OS.
    //   d. P5-43: the wording of these headers, P5-39 section c's measured check and tests/cli/SkillListTests.cs header
    //      (width, oracle 19 wrapper sentence) is pinned on the sources.
    static void RunP5_41(string root, string dotnet, string mcpDll, Action<bool, string> check)
    {
        var failures = new List<string>();
        void Check(bool condition, string name)
        {
            if (condition) check(true, "P5-41 mcp stderr fallback: " + name);
            else failures.Add(name);
        }
        static string Short(string text) => text.Length > 300 ? text[..300] + "..." : text;
        static int Count(string text, string part)
        {
            var n = 0;
            for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + 1, StringComparison.Ordinal)) n++;
            return n;
        }
        static int Lines(string text) => text.ReplaceLineEndings("\n").Split('\n').Length;

        // ---- a. static: the documented delta and the new statement ----
        var programPath = Path.Combine(root, "interfaces", "mcp", "Program.cs");
        var raw = File.Exists(programPath) ? File.ReadAllText(programPath).ReplaceLineEndings("\n") : "";
        var reversed = ReverseDelta(raw, ProgramDeltaP541);
        Check(reversed is not null && Count(reversed, ProgramDeltaP541[1].Old) == 1,
            "interfaces/mcp/Program.cs with ProgramDeltaP541 reversed holds the P5-39 statement once (each delta text occurs exactly once in the current file)");
        var program = DocUpdateLinkTests.CodeOf(raw);
        const string Statement = "try { string failureText; try { failureText=stdoutFailure.Message?.ReplaceLineEndings(\" \")??\"\"; } catch(Exception getterFailure) { failureText=\"<Message threw \"+getterFailure.GetType().FullName+\">\"; } Console.Error.WriteLine(\"mcp stdout failed: \"+stdoutFailure.GetType().FullName+\": \"+failureText); } catch(Exception) { }";
        Check(Count(program, Statement) == 1 && Count(program, "mcp stdout failed") == 1,
            "interfaces/mcp/Program.cs writes the diagnostic once, through the pinned P5-41 statement (fold, null as empty, a throwing Message getter replaced by its exception type) | occurrences "
            + Count(program, Statement).ToString(CultureInfo.InvariantCulture) + "/" + Count(program, "mcp stdout failed").ToString(CultureInfo.InvariantCulture));

        // ---- b. replica self-test ----
        // A replica of the P5-41 statement pinned above, and of the P5-39 one (the mutant for a throwing getter).
        static string Line(Exception stdoutFailure)
        {
            string failureText;
            try { failureText = stdoutFailure.Message?.ReplaceLineEndings(" ") ?? ""; }
            catch (Exception getterFailure) { failureText = "<Message threw " + getterFailure.GetType().FullName + ">"; }
            return "mcp stdout failed: " + stdoutFailure.GetType().FullName + ": " + failureText;
        }
        static string LineP539(Exception failure)
        {
            string? failureMessage = failure.Message;
            return "mcp stdout failed: " + failure.GetType().FullName + ": " + (failureMessage?.ReplaceLineEndings(" ") ?? "");
        }
        foreach (var failure in new Exception[] { new IOException("first P5-41\r\nsecond"), new IOException("a\nb\rc\u0085d\u2028e"), new IOException("plain P5-41"), new NullMessageException() })
            Check(Line(failure) == LineP539(failure) && Lines(Line(failure)) == 1,
                "the P5-41 formula prints the P5-39 line byte for byte when Message can be read (" + JsonSerializer.Serialize(LineP539(failure)) + ") | " + JsonSerializer.Serialize(Line(failure)));
        var getter = new MessageGetterThrowsException();
        var expected = "mcp stdout failed: " + typeof(MessageGetterThrowsException).FullName + ": <Message threw System.InvalidOperationException>";
        Check(Line(getter) == expected && Lines(Line(getter)) == 1,
            "a write exception whose Message getter throws prints exactly " + JsonSerializer.Serialize(expected) + " | " + JsonSerializer.Serialize(Line(getter)));
        var twoLevels = new DoubleMessageGetterThrowsException();
        var expectedTwo = "mcp stdout failed: " + typeof(DoubleMessageGetterThrowsException).FullName + ": <Message threw " + typeof(MessageGetterThrowsException).FullName + ">";
        Check(Line(twoLevels) == expectedTwo,
            "two levels: only types are read, so the getter's own throwing Message is never touched | " + JsonSerializer.Serialize(Line(twoLevels)));
        var mutant = "";
        try { LineP539(getter); }
        catch (Exception error) { mutant = error.GetType().FullName ?? ""; }
        Check(mutant == "System.InvalidOperationException",
            "mutant self-test: the P5-39 formula throws for that exception (the line Program.cs used to drop) | " + (mutant.Length == 0 ? "did not throw" : mutant));

        // ---- c. real process: oversized first line, failing stdout (Linux) ----
        if (!OperatingSystem.IsLinux())
            Console.WriteLine("SKIP P5-41 mcp stderr fallback: the real oversized-line run needs /bin/sh and /dev/full (Linux)");
        else
        {
            var oversized = "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/list\",\"params\":{\"pad\":\"" + new string('a', McpServer.MaxLineChars) + "\"}}";
            const string Ping = """{"jsonrpc":"2.0","id":4,"method":"ping"}""";
            Check(oversized.Length > McpServer.MaxLineChars + 1, "fixture: the oversized line is longer than MaxLineChars + 1");
            foreach (var (redirect, type) in new[] { (">/dev/full", "System.IO.IOException"), ("1</dev/null", "System.UnauthorizedAccessException") })
            {
                var start = new ProcessStartInfo("/bin/sh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
                start.ArgumentList.Add("-c"); start.ArgumentList.Add("exec \"$@\" " + redirect); start.ArgumentList.Add("sh"); start.ArgumentList.Add(dotnet);
                start.ArgumentList.Add(mcpDll);
                (int Exit, string Out, string Err) run;
                using (var process = Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: /bin/sh"))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    try { process.StandardInput.Write(oversized + "\n" + Ping + "\n"); process.StandardInput.Close(); } catch (IOException) { }
                    if (!process.WaitForExit(120000)) { process.Kill(true); run = (-1, "<timed out>", ""); }
                    else { process.WaitForExit(); run = (process.ExitCode, stdout.Result, stderr.Result); }
                }
                var prefix = "mcp stdout failed: " + type + ": ";
                var err = run.Err.EndsWith('\n') ? run.Err[..^1] : run.Err;
                Check(run.Exit == 1 && run.Out.Length == 0 && err.StartsWith(prefix, StringComparison.Ordinal) && Lines(err) == 1 && run.Err.EndsWith('\n')
                    && !run.Err.Contains("Unhandled exception", StringComparison.Ordinal),
                    "real Bim.Mcp.dll, an oversized first line (request_too_large reply) with stdout " + redirect + " (Linux): exit 1, stdout empty, stderr exactly one line '" + prefix + "...', no unhandled exception | exit "
                    + run.Exit.ToString(CultureInfo.InvariantCulture) + " out " + JsonSerializer.Serialize(Short(run.Out)) + " err " + JsonSerializer.Serialize(Short(run.Err)));
            }
        }

        // ---- d. P5-43: the wording of these oracles (text-only clauses, pinned on the sources) ----
        {
            // A comment block's words: the lines from the one starting with `first` (trimmed) up to the one containing
            // `last`, with the comment markers removed and runs of whitespace collapsed.
            static string Words(string source, string first, string last)
            {
                var lines = source.ReplaceLineEndings("\n").Split('\n');
                var start = Array.FindIndex(lines, line => line.TrimStart().StartsWith(first, StringComparison.Ordinal));
                var end = start < 0 ? -1 : Array.FindIndex(lines, start, line => line.Contains(last, StringComparison.Ordinal));
                if (start < 0 || end < 0) return "";
                var text = string.Join(" ", lines[start..end].Select(line => line.Trim().TrimStart('/')));
                return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            var herePath = Path.Combine(root, "tests", "mcp", "StdoutFailureToolTests.cs");
            var here = File.Exists(herePath) ? File.ReadAllText(herePath).ReplaceLineEndings("\n") : "";
            var p539 = Words(here, "// P5-39: the remaining MCP stdout edges", "static void RunP5_39(");
            var p541 = Words(here, "// P5-41: the 'mcp stdout failed' stderr line falls back", "static void RunP5_41(");
            Check(p541.Contains("a real IOException has a readable Message, so the real run never reaches the throwing-getter fallback: only the replica (b) covers it", StringComparison.Ordinal),
                "P5-43: the P5-41 header states that the real Bim.Mcp.dll run (c) cannot reach the throwing-getter fallback (a real IOException's Message is readable; only the replica covers it) | " + Short(p541));
            Check(p541.Contains("overlaps P5-37 b and P5-39 section a", StringComparison.Ordinal),
                "P5-43: the P5-41 header states the overlap of the real oversized-line run with P5-37 b and P5-39 section a | " + Short(p541));
            var stale = "guarded write " + "drops the line";
            Check(p539.Length > 0 && !p539.Contains("drops the line", StringComparison.Ordinal) && p539.Contains("RunP5_41 pins the fallback", StringComparison.Ordinal)
                && !here.Contains(stale, StringComparison.Ordinal),
                "P5-43: neither the P5-39 header nor its PINNED AS MEASURED check text says Program.cs drops the line (P5-41 replaced it; the P5-39 header points to RunP5_41) | header " + Short(p539));
            var cliPath = Path.Combine(root, "tests", "cli", "SkillListTests.cs");
            var cli = File.Exists(cliPath) ? File.ReadAllText(cliPath).ReplaceLineEndings("\n") : "";
            var headerLines = cli.Split('\n').TakeWhile(line => !line.StartsWith("internal static class SkillListTests", StringComparison.Ordinal)).ToArray();
            var wide = headerLines.Select((line, index) => (Line: index + 1, line.Length)).Where(entry => entry.Length > 125).ToArray();
            Check(cli.Length > 0 && headerLines.Length < cli.Split('\n').Length && wide.Length == 0,
                "P5-43: tests/cli/SkillListTests.cs header lines are at most 125 characters (the file's comment width) | wider: "
                + string.Join(", ", wide.Select(entry => entry.Line.ToString(CultureInfo.InvariantCulture) + " (" + entry.Length.ToString(CultureInfo.InvariantCulture) + ")")));
            var oracle19 = Words(cli, "//  19. P5-39", "internal static class SkillListTests");
            Check(oracle19.Contains("the leading System.InvalidOperationException is the wrapper Run's fallback builds", StringComparison.Ordinal),
                "P5-43: tests/cli/SkillListTests.cs oracle 19 states that the two-level diagnostic's leading System.InvalidOperationException is the wrapper Run's fallback builds | " + Short(oracle19));

            // P5-45 a: one comment-word collapse in this file, the shared CommentWords helper, self-tested on a planted comment.
            var collapseToken = "StringSplitOptions." + "RemoveEmptyEntries";
            var helperAt = here.IndexOf("    internal static string CommentWords(string source, string first, string last)\n", StringComparison.Ordinal);
            var helperEnd = helperAt < 0 ? -1 : here.IndexOf("\n    }\n", helperAt, StringComparison.Ordinal);
            var collapseAt = here.IndexOf(collapseToken, StringComparison.Ordinal);
            Check(Count(here, collapseToken) == 1 && helperAt >= 0 && collapseAt > helperAt && collapseAt < helperEnd,
                "P5-45: this file collapses comment words in one place only, inside the class-level CommentWords helper (no ad-hoc copy in section d) | collapses "
                + Count(here, collapseToken).ToString(CultureInfo.InvariantCulture) + " helper at " + helperAt.ToString(CultureInfo.InvariantCulture));
            const string Planted = "class Planted\n{\n    // Planted header (P5-45):   two  spaces\n\t//     indented   continuation\n///triple slash line\n"
                + "    // last comment line\n    static void Planted(\n}\n";
            var plantedWords = Words(Planted, "// Planted header (P5-45)", "static void Planted(");
            Check(plantedWords == "Planted header (P5-45): two spaces indented continuation triple slash line last comment line",
                "P5-45: the comment-word collapse self-test on a planted comment (markers removed, whitespace runs collapsed, stops before the end-marker line) | " + JsonSerializer.Serialize(plantedWords));
            Check(Words(Planted, "// Not planted", "static void Planted(") == "" && Words(Planted, "// Planted header (P5-45)", "static void Missing(") == ""
                && Words(Planted, "// Planted header (P5-45)", "last comment line") == "Planted header (P5-45): two spaces indented continuation triple slash line",
                "P5-45: the collapse self-test: a missing first or last marker gives the empty text, and the last marker's own line is excluded");

            // P5-45 b: the P5-39 and P5-41 headers of this file are at most 125 columns (the comment width, as P5-43 pins for tests/cli).
            static string Wide(string source, string first, string last)
            {
                var lines = source.Split('\n');
                var start = Array.FindIndex(lines, line => line.TrimStart().StartsWith(first, StringComparison.Ordinal));
                var end = start < 0 ? -1 : Array.FindIndex(lines, start, line => line.Contains(last, StringComparison.Ordinal));
                if (start < 0 || end < 0) return "<header not found>";
                return string.Join(", ", Enumerable.Range(start, end - start).Where(index => lines[index].Length > 125)
                    .Select(index => (index + 1).ToString(CultureInfo.InvariantCulture) + " (" + lines[index].Length.ToString(CultureInfo.InvariantCulture) + ")"));
            }
            var wide539 = Wide(here, "// P5-39: the remaining MCP stdout edges", "static void RunP5_39(");
            var wide541 = Wide(here, "// P5-41: the 'mcp stdout failed' stderr line falls back", "static void RunP5_41(");
            Check(wide539.Length == 0 && wide541.Length == 0,
                "P5-45: the P5-39 and P5-41 headers of tests/mcp/StdoutFailureToolTests.cs are at most 125 characters per line | P5-39 wider: " + wide539 + "; P5-41 wider: " + wide541);
            var plantedWide = "x\n// Planted width\n// " + new string('w', 123) + "\n// " + new string('w', 122) + "\nstatic void End(\n";
            Check(Wide(plantedWide, "// Planted width", "static void End(") == "3 (126)" && Wide(plantedWide, "// Absent", "static void End(") == "<header not found>",
                "P5-45: the width self-test flags a planted 126-character header line, keeps a 125-character one and refuses a missing header | " + Wide(plantedWide, "// Planted width", "static void End("));

            // P5-45 c: the cross-executable wording pin is stated in both headers.
            Check(p541.Contains("P5-45: that is a cross-executable pin (tests/mcp reads the tests/cli header; both headers state it)", StringComparison.Ordinal),
                "P5-45: the P5-41 header states that section d pins tests/cli/SkillListTests.cs wording from tests/mcp (cross-executable) | " + Short(p541));
            var cliHeader = Words(cli, "// Wording pins, stated (P5-45)", "internal static class SkillListTests");
            Check(cliHeader.Contains("are pinned from the other test executable, tests/mcp/StdoutFailureToolTests.cs RunP5_41 section d (cross-executable)", StringComparison.Ordinal)
                && cliHeader.Contains("a tests/cli run alone (the macOS job) does not check them", StringComparison.Ordinal),
                "P5-45: the tests/cli/SkillListTests.cs header states that its width and oracle 19 wording are pinned from tests/mcp (cross-executable) and not by a tests/cli run alone | " + Short(cliHeader));

            // P5-45 d: the reach of section d, stated in the P5-41 header and read against the workflow.
            Check(p541.Contains("section d runs only where tests/mcp runs whole, the core (ubuntu-latest) and core-windows (windows-latest) jobs through scripts/verify.ps1", StringComparison.Ordinal)
                && p541.Contains("the macOS job (ifc-portable, macos-latest) runs tests/mcp with BIM_TEST_P3_38_SECTION=9 only (P3-38 section 9), so it measures no section d check", StringComparison.Ordinal),
                "P5-45: the P5-41 header names the jobs that measure section d (core, core-windows) and states that the macOS job runs no tests/mcp section but 9 | " + Short(p541));
            var ciPath = Path.Combine(root, ".github", "workflows", "ci.yml");
            var ci = File.Exists(ciPath) ? File.ReadAllText(ciPath).ReplaceLineEndings("\n") : "";
            var verifyPath = Path.Combine(root, "scripts", "verify.ps1");
            var verify = File.Exists(verifyPath) ? File.ReadAllText(verifyPath).ReplaceLineEndings("\n") : "";
            Check(ci.Contains("\n  core:\n    runs-on: ubuntu-latest\n", StringComparison.Ordinal) && ci.Contains("\n  core-windows:\n    runs-on: windows-latest\n", StringComparison.Ordinal)
                && Count(ci, "run: ./scripts/verify.ps1") == 2 && ci.Contains("os: [windows-latest, macos-latest]", StringComparison.Ordinal)
                && ci.Contains("BIM_TEST_P3_38_SECTION=9 DOTNET_STARTUP_HOOKS=", StringComparison.Ordinal) && Count(ci, "tests/mcp/bin/Release") == 1
                && verify.Contains("Invoke-EvidenceCommand mcp-contract $Dotnet @('run','--project',\"$root/tests/mcp\"", StringComparison.Ordinal),
                "P5-45: the stated reach matches the workflow: core (ubuntu-latest) and core-windows (windows-latest) run scripts/verify.ps1, which runs tests/mcp whole; the one macOS tests/mcp run is the BIM_TEST_P3_38_SECTION=9 step");

            // P5-45 e: the duplication left in tests/mcp/TwiceReadToolTests.cs (P5-38) is stated.
            Check(p541.Contains("tests/mcp/TwiceReadToolTests.cs P5-38 keeps its own inline collapse of the provenance paragraph (a stated duplication", StringComparison.Ordinal),
                "P5-45: the P5-41 header states the P5-38 inline collapse left in tests/mcp/TwiceReadToolTests.cs | " + Short(p541));
        }

        check(failures.Count == 0, "P5-41 mcp stderr fallback: every check passed; failing (" + failures.Count.ToString(CultureInfo.InvariantCulture) + "): " + string.Join(" || ", failures));
    }

    // P5-41 documented delta (new text, P5-39 text) of interfaces/mcp/Program.cs, "\n" line endings.
    static readonly (string New, string Old)[] ProgramDeltaP541 =
    {
        ("// NullReferenceException. P5-41: a Message getter that throws no longer drops the line: the message part becomes\n"
            + "// \"<Message threw <the getter's exception type>>\" (only types are read), exit 1.\n",
            "// NullReferenceException. A Message getter that throws still drops the line (the guarded write), exit 1 alone.\n"),
        ("    try { string failureText; try { failureText=stdoutFailure.Message?.ReplaceLineEndings(\" \")??\"\"; } catch(Exception getterFailure) { failureText=\"<Message threw \"+getterFailure.GetType().FullName+\">\"; } Console.Error.WriteLine(\"mcp stdout failed: \"+stdoutFailure.GetType().FullName+\": \"+failureText); } catch(Exception) { }\n",
            "    try { string? failureMessage=stdoutFailure.Message; Console.Error.WriteLine(\"mcp stdout failed: \"+stdoutFailure.GetType().FullName+\": \"+(failureMessage?.ReplaceLineEndings(\" \")??\"\")); } catch(Exception) { }\n"),
    };

    // The text with each (new, old) pair replaced back (new -> old), or null when a new text does not occur exactly once.
    static string? ReverseDelta(string text, IReadOnlyList<(string New, string Old)> delta)
    {
        foreach (var (newText, oldText) in delta)
        {
            var at = text.IndexOf(newText, StringComparison.Ordinal);
            if (at < 0 || text.IndexOf(newText, at + 1, StringComparison.Ordinal) >= 0) return null;
            text = text[..at] + oldText + text[(at + newText.Length)..];
        }
        return text;
    }

    // P5-41: an exception whose Message getter throws MessageGetterThrowsException (two levels).
    sealed class DoubleMessageGetterThrowsException : Exception
    {
        public DoubleMessageGetterThrowsException() { }
        public DoubleMessageGetterThrowsException(string message) : base(message) { }
        public DoubleMessageGetterThrowsException(string message, Exception inner) : base(message, inner) { }
        public override string Message => throw new MessageGetterThrowsException();
    }

    // P5-39: an exception whose Message is null (the formula must print "" for it).
    sealed class NullMessageException : Exception
    {
        public NullMessageException() { }
        public NullMessageException(string message) : base(message) { }
        public NullMessageException(string message, Exception inner) : base(message, inner) { }
        public override string Message => null!;
    }

    // An exception whose Message getter throws InvalidOperationException(GetterMessage).
    sealed class MessageGetterThrowsException : Exception
    {
        public MessageGetterThrowsException() { }
        public MessageGetterThrowsException(string message) : base(message) { }
        public MessageGetterThrowsException(string message, Exception inner) : base(message, inner) { }
        public override string Message => throw new InvalidOperationException(GetterMessage);
    }
}
