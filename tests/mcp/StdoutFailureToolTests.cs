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
    //      throws is pinned as measured: the guarded write drops the line and exit 1 stands.
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
            var program = File.Exists(programPath) ? DocUpdateLinkTests.CodeOf(File.ReadAllText(programPath).ReplaceLineEndings("\n")) : "";
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
                "PINNED AS MEASURED: a write exception whose Message getter throws makes the formula throw, so Program.cs's guarded write drops the line and exit 1 stands alone | " + (threw.Length == 0 ? "did not throw" : threw));
        }

        check(failures.Count == 0, "P5-39 mcp stdout edges: every check passed; failing (" + failures.Count.ToString(CultureInfo.InvariantCulture) + "): " + string.Join(" || ", failures));
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
