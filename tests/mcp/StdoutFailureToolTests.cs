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
//      nothing on the reader's stdout, and stderr exactly one line starting "mcp stdout failed: System.IO.IOException: "
//      (no unhandled-exception crash).
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
    const string FailurePrefix = "mcp stdout failed: System.IO.IOException: ";

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
            foreach (var redirect in new[] { ">/dev/full", "1</dev/null" })
            {
                if (redirect == ">/dev/full" && !File.Exists("/dev/full")) { Console.WriteLine("SKIP P5-37 mcp stdout: no /dev/full on " + os); continue; }
                foreach (var (label, requests) in new (string, string[])[] { ("jurisdiction_resolve", [resolved, listing, Ping]), ("skill_list", [listing, resolved, Ping]) })
                {
                    var run = Server(redirect, false, string.Join("\n", requests) + "\n");
                    var err = run.Err.TrimEnd('\r', '\n');
                    Check(run.Exit == 1 && run.Out.Length == 0 && err.StartsWith(FailurePrefix, StringComparison.Ordinal) && !err.Contains('\n', StringComparison.Ordinal)
                        && !run.Err.Contains("Unhandled exception", StringComparison.Ordinal),
                        "real Bim.Mcp.dll with stdout " + redirect + " during the " + label + " reply (" + os + "): exit 1, one stderr line '" + FailurePrefix + "...', no unhandled exception | " + P(run));
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
