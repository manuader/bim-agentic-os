using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bim.Mcp;

// P3-21 MCP internal-error envelopes from IntentPlan. Oracles:
//   1. audit_verify: AuditVerifyTool.Run's catch-all, forced by an actor list whose enumeration throws (after the path and
//      Count checks, inside Actors), returns {"status":"failed","code":"audit_verify_internal_error"} byte for byte (the
//      pre-P3-21 hand-built bytes) and writes "audit_verify internal error: <type>: <message>" to stderr once; both are
//      IntentPlan.Internal("audit_verify", error)'s Line and Diagnostic.
//   2. Static (layout-independent regexes over comment-stripped code, each checker self-tested on fake snippets):
//      AuditVerifyTool.cs's catch-all binds IntentPlan.Internal("audit_verify", error) to one variable whose Diagnostic is
//      the stderr line and whose Line is the Outcome text (no InternalDiagnostic call); InternalError is named only in its
//      declaration and no interfaces/mcp source hand-builds an audit_verify_internal_error envelope. The P5-12 pin equals
//      the tests/cli constant.
//   3. generate_typology: the MCP Document path, forced through GenerateTypologyCommand.GuardedWorkProbe, is isError with
//      the typed generate_typology_internal_error line (IntentPlan.Internal("generate-typology", error).Line, not
//      McpServer's generic failure carrying the exception message), the IntentPlan diagnostic on stderr once; with the
//      seam reset the next call answers the P5-12 pinned success bytes again.
//   4. P3-23 generate_typology outside the guarded block: a non-refusal exception in the parse / pack-load stretch of
//      GenerateTypologyCommand.Document, forced through GenerateTypologyCommand.UnguardedWorkProbe (null in production),
//      is isError with the same typed generate_typology_internal_error line (not McpServer's generic Failed carrying the
//      exception message), the IntentPlan diagnostic on stderr once and nothing on stdout; with the seam reset the next
//      call answers the P5-12 pinned success bytes again.
//      Static (comments and literals stripped, checker self-tested): over interfaces/**/*.cs UnguardedWorkProbe appears
//      exactly twice, its declaration and one UnguardedWorkProbe?.Invoke(); inside Generate before its Parse( call, and
//      is never assigned outside tests/.
internal static class InternalErrorEnvelopeToolTests
{
    const string TypologyPack = "bim.typology.vivienda-unifamiliar";
    const string Id = "00000000-0000-4000-8000-0000000007a7";
    // tests/cli/GenerateTypologyTests.cs TypologyPipelineTests.PinnedAcceptedSha256 (P5-12).
    const string PinnedAcceptedSha256 = "eb8720c1a98a749048df6ac69f1794b965e833efc963eb9ddfe58db0ae886f4e";

    public static void Run(string root, string work, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P3-21 internal-error envelopes: " + name);
        void Check23(bool condition, string name) => check(condition, "P3-23 generate_typology unguarded internal error: " + name);
        var nl = Environment.NewLine;
        static string Short(string text) => text.Length > 400 ? text[..400] + "..." : text;

        // ---- 1. audit_verify catch-all (in process) ----
        {
            var failure = new InvalidOperationException("boom audit_verify P3-21");
            var envelope = Bim.Cli.IntentPlan.Internal("audit_verify", failure);
            const string HandBuilt = "{\"status\":\"failed\",\"code\":\"audit_verify_internal_error\"}";
            Check(envelope.Line == HandBuilt && envelope.Exit == 1 && envelope.Diagnostic == "audit_verify internal error: System.InvalidOperationException: boom audit_verify P3-21",
                "fixture: IntentPlan.Internal(\"audit_verify\") is the pre-P3-21 hand-built line and diagnostic | " + envelope.Line + " / " + envelope.Diagnostic);
            var log = Path.Combine(work, "p3-21-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
            var errors = new StringWriter();
            var output = new StringWriter();
            var savedErr = Console.Error;
            var savedOut = Console.Out;
            AuditVerifyTool.Outcome outcome;
            Console.SetError(errors);
            Console.SetOut(output);
            try { outcome = AuditVerifyTool.Run(log, null, new ThrowingActors(failure), summary: false); }
            finally { Console.SetError(savedErr); Console.SetOut(savedOut); }
            Check(!outcome.Succeeded && outcome.Text == envelope.Line && outcome.Text == HandBuilt,
                "a forced audit_verify internal error returns IntentPlan.Internal's line, byte-equal to the hand-built envelope | " + Short(outcome.Text));
            Check(errors.ToString() == envelope.Diagnostic + nl && output.ToString().Length == 0,
                "the forced audit_verify internal error writes IntentPlan.Internal's diagnostic to stderr once and nothing to stdout | " + Short(errors.ToString()));
        }

        // ---- 2. static: one IntentPlan helper, no hand-built audit_verify_internal_error envelope in interfaces/mcp ----
        {
            var mcpDir = Path.Combine(root, "interfaces", "mcp");
            var auditSource = File.ReadAllText(Path.Combine(mcpDir, "AuditVerifyTool.cs"));
            var catchAll = CatchAllProblems(auditSource);
            Check(catchAll.Count == 0, "AuditVerifyTool.cs's catch-all is one IntentPlan.Internal(\"audit_verify\", error) envelope whose Diagnostic goes to stderr and whose Line is the Outcome text, no InternalDiagnostic call [" + string.Join("; ", catchAll) + "]");
            var sources = Directory.GetFiles(mcpDir, "*.cs").Order(StringComparer.Ordinal).Select(file => (Path.GetFileName(file), File.ReadAllText(file))).ToArray();
            var handBuilt = HandBuiltProblems(sources);
            Check(handBuilt.Count == 0, "no interfaces/mcp source hand-builds an audit_verify_internal_error envelope [" + string.Join("; ", handBuilt) + "]");

            // Checker self-tests: the evasions are flagged, layout-independent equivalents of the real shape pass.
            const string Declaration = "public const string InternalError = \"audit_verify_internal_error\";";
            string AuditFile(string body) => "namespace Bim.Mcp; internal static class AuditVerifyTool { " + Declaration + " static object Run() { " + body + " } }";
            var evasions = new (string Label, (string, string)[] Files)[]
            {
                ("Failure( InternalError, null)", new[] { ("AuditVerifyTool.cs", AuditFile("return new Outcome(false, Failure( InternalError, null));")) }),
                ("Failure(code: InternalError, detail: null)", new[] { ("AuditVerifyTool.cs", AuditFile("return new Outcome(false, Failure(code: InternalError, detail: null));")) }),
                ("Serialize(new{status=\"failed\",code=InternalError})", new[] { ("AuditVerifyTool.cs", AuditFile("return JsonSerializer.Serialize(new{status=\"failed\",code=InternalError});")) }),
                ("\"audit_verify\"+\"_internal_error\" in AuditVerifyTool.cs", new[] { ("AuditVerifyTool.cs", AuditFile("return Failure(\"audit_verify\"+\"_internal_error\", null);")) }),
                ("a second audit_verify_internal_error literal", new[] { ("AuditVerifyTool.cs", AuditFile("return Failure(\"audit_verify_internal_error\", null);")) }),
                ("AuditVerifyTool . InternalError in another file", new[] { ("AuditVerifyTool.cs", AuditFile("return null;")), ("McpServer.cs", "var t = Failed(AuditVerifyTool . InternalError);") }),
                ("\"audit_verify\" + \"_internal_error\" in another file", new[] { ("AuditVerifyTool.cs", AuditFile("return null;")), ("McpServer.cs", "var t = Failed(\"audit_verify\" + \"_internal_error\");") }),
                ("the literal in another file", new[] { ("AuditVerifyTool.cs", AuditFile("return null;")), ("McpServer.cs", "var t = Failed(\"audit_verify_internal_error\");") }),
                ("the declaration missing", new[] { ("AuditVerifyTool.cs", "internal static class AuditVerifyTool { }") }),
            };
            foreach (var (label, files) in evasions)
                Check(HandBuiltProblems(files).Count > 0, "self-test: the hand-built checker flags " + label);
            Check(HandBuiltProblems(new[] { ("AuditVerifyTool.cs", AuditFile("// Failure(InternalError) in a comment\nreturn null;")), ("MirrorDriftTool.cs", "const string InternalError = \"mirror_drift_internal_error\"; var t = Failure(InternalError, null);") }).Count == 0,
                "self-test: the hand-built checker passes the declaration alone, a comment, and another tool's own InternalError");

            const string Good = "catch (Exception error) { var envelope = IntentPlan.Internal(\"audit_verify\", error); Console.Error.WriteLine(envelope.Diagnostic); return new Outcome(false, envelope.Line); }";
            const string Spaced = "catch (Exception error)\n{\n var  e2 =\n IntentPlan . Internal ( \"audit_verify\" ,\n error ) ;\n Console . Error . WriteLine( e2 . Diagnostic ) ;\n return new Outcome( false , e2 . Line );\n}";
            Check(CatchAllProblems(Good).Count == 0 && CatchAllProblems(Spaced).Count == 0, "self-test: the catch-all checker accepts the shape at any spacing and variable name");
            var badShapes = new (string Label, string Source)[]
            {
                ("the P3-20 body", "catch (Exception error) { Console.Error.WriteLine(IntentPlan.InternalDiagnostic(\"audit_verify\", error)); return new Outcome(false, Failure(InternalError, null)); }"),
                ("Diagnostic from another variable", "catch (Exception error) { var envelope = IntentPlan.Internal(\"audit_verify\", error); var other = IntentPlan.Internal(\"x\", error); Console.Error.WriteLine(other.Diagnostic); return new Outcome(false, envelope.Line); }"),
                ("Line from another variable", "catch (Exception error) { var envelope = IntentPlan.Internal(\"audit_verify\", error); var other = IntentPlan.Internal(\"x\", error); Console.Error.WriteLine(envelope.Diagnostic); return new Outcome(false, other.Line); }"),
                ("an extra InternalDiagnostic call", Good + " void X(Exception error) { Console.Error.WriteLine(IntentPlan.InternalDiagnostic(\"audit_verify\", error)); }"),
                ("the shape only in a comment", "// var envelope = IntentPlan.Internal(\"audit_verify\", error); Console.Error.WriteLine(envelope.Diagnostic); return new Outcome(false, envelope.Line);\n"),
            };
            foreach (var (label, source) in badShapes)
                Check(CatchAllProblems(source).Count > 0, "self-test: the catch-all checker flags " + label);

            // CodeOf self-test: comments go, string / verbatim / raw / char literals stay verbatim.
            const string Sample = """"
                var a = "x // kept"; // gone "q"
                var b = """
                  raw " // kept too
                  """; /* gone */ var c = '"'; var d = @"v "" // kept three";
                """";
            var stripped = CodeOf(Sample);
            Check(!stripped.Contains("gone", StringComparison.Ordinal) && stripped.Contains("\"x // kept\"", StringComparison.Ordinal) && stripped.Contains("raw \" // kept too", StringComparison.Ordinal)
                && stripped.Contains("'\"'", StringComparison.Ordinal) && stripped.Contains("@\"v \"\" // kept three\"", StringComparison.Ordinal), "self-test: CodeOf strips comments and keeps string, raw, verbatim and char literals | " + Short(stripped));

            // The P5-12 pin used below is the tests/cli constant.
            var cliPin = Regex.Match(File.ReadAllText(Path.Combine(root, "tests", "cli", "GenerateTypologyTests.cs")), "PinnedAcceptedSha256\\s*=\\s*\"([0-9a-f]{64})\"");
            Check(cliPin.Success && cliPin.Groups[1].Value == PinnedAcceptedSha256, "the P5-12 pin equals tests/cli/GenerateTypologyTests.cs PinnedAcceptedSha256");
        }

        // ---- 3. generate_typology Document path forced through GuardedWorkProbe ----
        {
            var scratch = Path.Combine(work, "p3-21-generate-typology");
            Directory.CreateDirectory(scratch);
            var requestPath = Path.Combine(scratch, "request.json");
            File.WriteAllText(requestPath, "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}", new UTF8Encoding(false));
            JsonObject Tool() => call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 21, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = GenerateTypologyTool.Name, ["arguments"] = new JsonObject { [GenerateTypologyTool.RequestParameter] = requestPath } } }.ToJsonString());
            static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
            static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
            static string Hex(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();

            Check(Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe is null, "fixture: the generate_typology guarded-work test seam is null unless a test sets it");
            var baseline = Tool();
            Check(!IsError(baseline) && Hex(Text(baseline)) == PinnedAcceptedSha256, "fixture: with the seam unset the tool answers the P5-12 pinned document | " + Hex(Text(baseline)));

            var failure = new InvalidOperationException("boom generate_typology P3-21");
            var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
            Check(envelope.Line == "{\"status\":\"failed\",\"code\":\"generate_typology_internal_error\"}", "fixture: IntentPlan.Internal(\"generate-typology\") is the typed generate_typology_internal_error line | " + envelope.Line);
            var errors = new StringWriter();
            var savedErr = Console.Error;
            JsonObject forced;
            Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe = () => throw failure;
            try
            {
                Console.SetError(errors);
                try { forced = Tool(); }
                finally { Console.SetError(savedErr); }
            }
            finally { Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe = null; }
            Check(IsError(forced) && Text(forced) == envelope.Line && !Text(forced).Contains(failure.Message, StringComparison.Ordinal),
                "a forced exception in the guarded pipeline reaches the MCP tool as the typed generate_typology_internal_error line, not McpServer's generic failure | " + Short(Text(forced)));
            Check(errors.ToString() == envelope.Diagnostic + nl,
                "the forced generate_typology internal error writes the IntentPlan diagnostic to the server's stderr once | " + Short(errors.ToString()));
            var after = Tool();
            Check(Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe is null && !IsError(after) && Text(after) == Text(baseline) && Hex(Text(after)) == PinnedAcceptedSha256,
                "the seam is reset: the next call answers the unchanged pinned success bytes");
        }

        // ---- 4. P3-23 generate_typology Document path outside the guarded block (parse / pack load) ----
        {
            var scratch = Path.Combine(work, "p3-23-generate-typology");
            Directory.CreateDirectory(scratch);
            try
            {
                var requestPath = Path.Combine(scratch, "request.json");
                File.WriteAllText(requestPath, "{\"pack\":\"" + TypologyPack + "\",\"family_size\":3,\"lot\":{\"width_m\":16,\"depth_m\":16},\"alternatives\":4,\"seed\":0,\"project_id\":\"" + Id + "\"}", new UTF8Encoding(false));
                JsonObject Tool() => call(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 23, ["method"] = "tools/call",
                    ["params"] = new JsonObject { ["name"] = GenerateTypologyTool.Name, ["arguments"] = new JsonObject { [GenerateTypologyTool.RequestParameter] = requestPath } } }.ToJsonString());
                static string Text(JsonObject response) => response["result"]!["content"]![0]!["text"]!.GetValue<string>();
                static bool IsError(JsonObject response) => response["result"]!["isError"]!.GetValue<bool>();
                static string Hex(string text) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(text))).ToLowerInvariant();

                Check23(Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe is null && Bim.Cli.GenerateTypologyCommand.GuardedWorkProbe is null,
                    "fixture: the generate_typology unguarded and guarded test seams are null unless a test sets them");
                var baseline = Tool();
                Check23(!IsError(baseline) && Hex(Text(baseline)) == PinnedAcceptedSha256, "fixture: with the seams unset the tool answers the P5-12 pinned document | " + Hex(Text(baseline)));

                var failure = new InvalidOperationException("boom generate_typology P3-23 unguarded");
                var envelope = Bim.Cli.IntentPlan.Internal("generate-typology", failure);
                var errors = new StringWriter();
                var output = new StringWriter();
                var savedErr = Console.Error;
                var savedOut = Console.Out;
                (bool Succeeded, string Text) direct;
                JsonObject forced;
                Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe = () => throw failure;
                try
                {
                    Console.SetError(errors);
                    Console.SetOut(output);
                    try { direct = Bim.Cli.GenerateTypologyCommand.Document(requestPath); }
                    catch (Exception escaped) { direct = (true, "escaped: " + escaped.GetType().FullName + ": " + escaped.Message); }
                    finally { Console.SetError(savedErr); Console.SetOut(savedOut); }
                }
                finally { Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe = null; }
                Check23(!direct.Succeeded && direct.Text == envelope.Line,
                    "a forced exception before the guarded block makes GenerateTypologyCommand.Document return the typed generate_typology_internal_error line instead of throwing | " + Short(direct.Text));
                Check23(errors.ToString() == envelope.Diagnostic + nl && output.ToString().Length == 0,
                    "Document writes the IntentPlan diagnostic to stderr once and nothing to stdout | stderr=" + Short(errors.ToString()) + " stdout=" + Short(output.ToString()));

                errors = new StringWriter();
                Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe = () => throw failure;
                try
                {
                    Console.SetError(errors);
                    try { forced = Tool(); }
                    finally { Console.SetError(savedErr); }
                }
                finally { Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe = null; }
                Check23(IsError(forced) && Text(forced) == envelope.Line && !Text(forced).Contains(failure.Message, StringComparison.Ordinal),
                    "a forced exception in the parse / pack-load stretch reaches the MCP tool as the typed generate_typology_internal_error line, not McpServer's generic failure | " + Short(Text(forced)));
                Check23(errors.ToString() == envelope.Diagnostic + nl,
                    "the forced unguarded generate_typology internal error writes the IntentPlan diagnostic to the server's stderr once | " + Short(errors.ToString()));
                var after = Tool();
                Check23(Bim.Cli.GenerateTypologyCommand.UnguardedWorkProbe is null && !IsError(after) && Text(after) == Text(baseline) && Hex(Text(after)) == PinnedAcceptedSha256,
                    "the seam is reset: the next call answers the unchanged pinned success bytes");
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }

            // Static pin: the seam is declared once, invoked once before Parse( inside Generate, never assigned in production.
            var interfaces = Path.Combine(root, "interfaces");
            var sources = Directory.GetFiles(interfaces, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => (Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file))).ToArray();
            var seam = SeamProblems(sources);
            Check23(seam.Count == 0, "interfaces/**/*.cs names UnguardedWorkProbe only in its declaration and one ?.Invoke() before Parse( in Generate, never assigned [" + string.Join("; ", seam) + "]");

            const string Path23 = "interfaces/cli/GenerateTypologyCommand.cs";
            const string Decl = "internal static Action? UnguardedWorkProbe { get; set; }";
            string Command(string generateBody, string extra = "") => "internal static class GenerateTypologyCommand { static Generated Generate(string text) { " + generateBody + " } " + Decl + " " + extra + " }";
            const string GoodBody = "UnguardedWorkProbe?.Invoke(); var parsed = Parse(text); return null;";
            Check23(SeamProblems(new[] { (Path23, Command(GoodBody)) }).Count == 0
                && SeamProblems(new[] { (Path23, Command("UnguardedWorkProbe ?. Invoke ( ) ;\n var parsed = Parse ( text ); // UnguardedWorkProbe = null;\n var s = \"UnguardedWorkProbe = x\"; return null;")) }).Count == 0,
                "self-test: the seam checker accepts the real shape at any spacing, ignoring comments and string literals");
            var seamEvasions = new (string Label, (string, string)[] Files)[]
            {
                ("an assignment in the command", new[] { (Path23, Command(GoodBody, "static void Set() { UnguardedWorkProbe = () => throw null!; }")) }),
                ("an assignment in another interfaces file", new[] { (Path23, Command(GoodBody)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateTypologyCommand.UnguardedWorkProbe = null; } }") }),
                ("a compound assignment", new[] { (Path23, Command(GoodBody)), ("interfaces/mcp/McpServer.cs", "class S { void X() { GenerateTypologyCommand.UnguardedWorkProbe += null; } }") }),
                ("the invoke after Parse(", new[] { (Path23, Command("var parsed = Parse(text); UnguardedWorkProbe?.Invoke(); return null;")) }),
                ("the invoke outside Generate", new[] { (Path23, Command("var parsed = Parse(text); return null;", "static void Other() { UnguardedWorkProbe?.Invoke(); }")) }),
                ("the invoke missing", new[] { (Path23, Command("var parsed = Parse(text); return null;")) }),
                ("a second invoke", new[] { (Path23, Command("UnguardedWorkProbe?.Invoke(); UnguardedWorkProbe?.Invoke(); var parsed = Parse(text); return null;")) }),
                ("the declaration missing", new[] { (Path23, "internal static class GenerateTypologyCommand { static Generated Generate(string text) { " + GoodBody + " } }") }),
            };
            foreach (var (label, files) in seamEvasions)
                Check23(SeamProblems(files).Count > 0, "self-test: the seam checker flags " + label);
        }
    }

    // The positive shape of AuditVerifyTool.Run's catch-all, layout-independent: exactly one
    // var <v> = IntentPlan.Internal("audit_verify", error); then Console.Error.WriteLine(<v>.Diagnostic) and
    // new Outcome(false, <v>.Line) on the same <v>; no IntentPlan.InternalDiagnostic call anywhere in the file.
    static List<string> CatchAllProblems(string source)
    {
        var code = CodeOf(source);
        var problems = new List<string>();
        var internals = Regex.Matches(code, @"\bIntentPlan\s*\.\s*Internal\s*\(\s*""audit_verify""\s*,");
        var bound = Regex.Matches(code, @"\bvar\s+(?<v>[A-Za-z_]\w*)\s*=\s*IntentPlan\s*\.\s*Internal\s*\(\s*""audit_verify""\s*,\s*error\s*\)\s*;");
        if (internals.Count != 1 || bound.Count != 1) problems.Add("IntentPlan.Internal(\"audit_verify\", error) bound to one variable exactly once (" + internals.Count.ToString(CultureInfo.InvariantCulture) + " calls, " + bound.Count.ToString(CultureInfo.InvariantCulture) + " bindings)");
        else
        {
            var v = Regex.Escape(bound[0].Groups["v"].Value);
            if (!Regex.IsMatch(code, @"\bConsole\s*\.\s*Error\s*\.\s*WriteLine\s*\(\s*" + v + @"\s*\.\s*Diagnostic\s*\)")) problems.Add("Console.Error.WriteLine(" + bound[0].Groups["v"].Value + ".Diagnostic)");
            if (!Regex.IsMatch(code, @"\bnew\s+Outcome\s*\(\s*false\s*,\s*" + v + @"\s*\.\s*Line\s*\)")) problems.Add("new Outcome(false, " + bound[0].Groups["v"].Value + ".Line)");
        }
        if (Regex.IsMatch(code, @"\bInternalDiagnostic\s*\(")) problems.Add("an InternalDiagnostic call");
        return problems;
    }

    // No hand-built audit_verify_internal_error envelope over (file name, source) pairs, comments stripped:
    // AuditVerifyTool.cs names InternalError exactly once (its declaration) and contains "internal_error" only there;
    // no other file names AuditVerifyTool.InternalError, contains audit_verify_internal_error, or concatenates
    // "audit_verify..." + "...internal_error".
    static List<string> HandBuiltProblems(IEnumerable<(string Name, string Source)> files)
    {
        var problems = new List<string>();
        var declarations = 0;
        foreach (var (name, source) in files)
        {
            var code = CodeOf(source);
            if (name == "AuditVerifyTool.cs")
            {
                declarations += Regex.Matches(code, @"\bpublic\s+const\s+string\s+InternalError\s*=\s*""audit_verify_internal_error""\s*;").Count;
                var named = Regex.Matches(code, @"\bInternalError\b").Count;
                if (named != 1) problems.Add(name + ": InternalError named " + named.ToString(CultureInfo.InvariantCulture) + " times (the declaration only)");
                var texts = Occurrences(code, "internal_error");
                if (texts != 1) problems.Add(name + ": internal_error appears " + texts.ToString(CultureInfo.InvariantCulture) + " times (the declaration only)");
                continue;
            }
            if (Regex.IsMatch(code, @"\bAuditVerifyTool\s*\.\s*InternalError\b")) problems.Add(name + ": AuditVerifyTool.InternalError");
            if (code.Contains("audit_verify_internal_error", StringComparison.Ordinal)) problems.Add(name + ": the audit_verify_internal_error literal");
            if (Regex.IsMatch(code, @"""audit_verify[^""]*""\s*\+\s*""[^""]*internal_error")) problems.Add(name + ": a concatenated audit_verify internal_error code");
        }
        if (declarations != 1) problems.Add("AuditVerifyTool.cs: the InternalError declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
        return problems;
    }

    static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // P3-23: over (relative path, source) pairs under interfaces/, comments and literals stripped: UnguardedWorkProbe is
    // named exactly twice in total, both in interfaces/cli/GenerateTypologyCommand.cs: its declaration and one
    // UnguardedWorkProbe?.Invoke(); inside Generate before Generate's first Parse( call; no assignment (=, +=, ...) anywhere.
    static List<string> SeamProblems(IEnumerable<(string Name, string Source)> files)
    {
        var problems = new List<string>();
        var total = 0;
        var sawCommand = false;
        foreach (var (name, source) in files)
        {
            var code = CodeOf(source, keepLiterals: false);
            total += Regex.Matches(code, @"\bUnguardedWorkProbe\b").Count;
            if (Regex.IsMatch(code, @"\bUnguardedWorkProbe\s*(?:[-+*/%&|^]|\?\?|<<|>>)?=(?!=)")) problems.Add(name + ": UnguardedWorkProbe assigned");
            if (name != "interfaces/cli/GenerateTypologyCommand.cs") continue;
            sawCommand = true;
            var declarations = Regex.Matches(code, @"\binternal\s+static\s+Action\s*\?\s*UnguardedWorkProbe\s*\{\s*get\s*;\s*set\s*;\s*\}").Count;
            if (declarations != 1) problems.Add(name + ": the declaration found " + declarations.ToString(CultureInfo.InvariantCulture) + " times");
            var invokes = Regex.Matches(code, @"\bUnguardedWorkProbe\s*\?\s*\.\s*Invoke\s*\(\s*\)\s*;");
            var generate = Regex.Match(code, @"\bstatic\s+Generated\s+Generate\s*\(");
            if (invokes.Count != 1 || !generate.Success) { problems.Add(name + ": " + invokes.Count.ToString(CultureInfo.InvariantCulture) + " UnguardedWorkProbe?.Invoke() calls, Generate " + (generate.Success ? "found" : "missing")); continue; }
            var parse = Regex.Match(code[(generate.Index + generate.Length)..], @"\bParse\s*\(");
            var parseAt = parse.Success ? generate.Index + generate.Length + parse.Index : -1;
            if (parseAt < 0 || invokes[0].Index < generate.Index || invokes[0].Index > parseAt) problems.Add(name + ": UnguardedWorkProbe?.Invoke() not inside Generate before its Parse(");
        }
        if (!sawCommand) problems.Add("interfaces/cli/GenerateTypologyCommand.cs not found");
        if (total != 2) problems.Add("UnguardedWorkProbe named " + total.ToString(CultureInfo.InvariantCulture) + " times (the declaration and one invoke)");
        return problems;
    }

    // C# source without // and /* */ comments; string, verbatim, raw ("""...) and char literals are kept verbatim, or
    // with keepLiterals false (P3-23) reduced to their delimiters.
    internal static string CodeOf(string source, bool keepLiterals = true)
    {
        var code = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                if (i < source.Length) code.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
                code.Append(' ');
                continue;
            }
            if (c == '"' && i + 2 < source.Length && source[i + 1] == '"' && source[i + 2] == '"')
            {
                var quotes = 0;
                while (i < source.Length && source[i] == '"') { code.Append('"'); quotes++; i++; }
                var run = 0;
                for (; i < source.Length; i++)
                {
                    if (keepLiterals) code.Append(source[i]);
                    run = source[i] == '"' ? run + 1 : 0;
                    if (run == quotes) break;
                }
                if (!keepLiterals) code.Append('"', quotes);
                continue;
            }
            if (c is '"' or '\'')
            {
                var verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                code.Append(c);
                for (i++; i < source.Length; i++)
                {
                    if (keepLiterals) code.Append(source[i]);
                    if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"') { i++; if (keepLiterals) code.Append(source[i]); continue; }
                    if (!verbatim && source[i] == '\\' && i + 1 < source.Length) { i++; if (keepLiterals) code.Append(source[i]); continue; }
                    if (source[i] == c) break;
                }
                if (!keepLiterals) code.Append(c);
                continue;
            }
            code.Append(c);
        }
        return code.ToString();
    }

    sealed class ThrowingActors(Exception failure) : IReadOnlyList<string?>
    {
        public int Count => 1;
        public string? this[int index] => throw failure;
        public IEnumerator<string?> GetEnumerator() => throw failure;
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
