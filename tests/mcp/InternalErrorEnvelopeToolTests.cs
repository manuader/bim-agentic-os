using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Bim.Mcp;

// P3-21 MCP internal-error envelopes from IntentPlan. Oracles:
//   1. audit_verify: AuditVerifyTool.Run's catch-all, forced by an actor list whose enumeration throws (after the path and
//      Count checks, inside Actors), returns {"status":"failed","code":"audit_verify_internal_error"} byte for byte (the
//      pre-P3-21 hand-built bytes) and writes "audit_verify internal error: <type>: <message>" to stderr once; both are
//      IntentPlan.Internal("audit_verify", error)'s Line and Diagnostic.
//   2. Static: AuditVerifyTool.cs builds its catch-all from IntentPlan.Internal("audit_verify", error) alone (no
//      InternalDiagnostic call, no Failure(InternalError ...) envelope); no interfaces/mcp source hand-builds an
//      audit_verify_internal_error envelope (the literal appears only as AuditVerifyTool.InternalError's declaration).
//   3. generate_typology: the MCP Document path, forced through GenerateTypologyCommand.GuardedWorkProbe, is isError with
//      the typed generate_typology_internal_error line (IntentPlan.Internal("generate-typology", error).Line, not
//      McpServer's generic failure carrying the exception message), the IntentPlan diagnostic on stderr once; with the
//      seam reset the next call answers the P5-12 pinned success bytes again.
internal static class InternalErrorEnvelopeToolTests
{
    const string TypologyPack = "bim.typology.vivienda-unifamiliar";
    const string Id = "00000000-0000-4000-8000-0000000007a7";
    // tests/cli/GenerateTypologyTests.cs TypologyPipelineTests.PinnedAcceptedSha256 (P5-12).
    const string PinnedAcceptedSha256 = "eb8720c1a98a749048df6ac69f1794b965e833efc963eb9ddfe58db0ae886f4e";

    public static void Run(string root, string work, Func<string, JsonObject> call, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P3-21 internal-error envelopes: " + name);
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
            var audit = CodeOf(File.ReadAllText(Path.Combine(mcpDir, "AuditVerifyTool.cs")));
            Check(audit.Contains("IntentPlan.Internal(\"audit_verify\", error)", StringComparison.Ordinal),
                "AuditVerifyTool.cs builds its catch-all from IntentPlan.Internal(\"audit_verify\", error)");
            Check(!audit.Contains("InternalDiagnostic(", StringComparison.Ordinal),
                "AuditVerifyTool.cs does not build the stderr line separately (no IntentPlan.InternalDiagnostic call)");
            var handBuilt = Directory.GetFiles(mcpDir, "*.cs").Order(StringComparer.Ordinal).SelectMany(file =>
            {
                var code = CodeOf(File.ReadAllText(file));
                var name = Path.GetFileName(file);
                var found = new List<string>();
                // A bare InternalError names audit_verify_internal_error only inside AuditVerifyTool.cs (MirrorDriftTool's own
                // InternalError is mirror_drift_internal_error); elsewhere the code is reached as AuditVerifyTool.InternalError
                // or the literal.
                var patterns = new List<string> { "Failure(AuditVerifyTool.InternalError", "Failed(AuditVerifyTool.InternalError", "Failed(\"audit_verify_internal_error\"" };
                if (name == "AuditVerifyTool.cs") patterns.AddRange(new[] { "Failure(InternalError", "Failed(InternalError" });
                foreach (var pattern in patterns)
                    if (code.Contains(pattern, StringComparison.Ordinal)) found.Add(name + ": " + pattern);
                var literals = Occurrences(code, "\"audit_verify_internal_error\"");
                var declared = name == "AuditVerifyTool.cs" && code.Contains("public const string InternalError = \"audit_verify_internal_error\";", StringComparison.Ordinal) ? 1 : 0;
                if (literals != declared) found.Add(name + ": " + literals.ToString(CultureInfo.InvariantCulture) + " audit_verify_internal_error literal(s)");
                return found;
            }).ToArray();
            Check(handBuilt.Length == 0, "no interfaces/mcp source hand-builds an audit_verify_internal_error envelope [" + string.Join(", ", handBuilt) + "]");
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
    }

    static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // C# source without // and /* */ comments; string and char literals are kept verbatim.
    static string CodeOf(string source)
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
            if (c is '"' or '\'')
            {
                var verbatim = c == '"' && i > 0 && source[i - 1] == '@';
                code.Append(c);
                for (i++; i < source.Length; i++)
                {
                    code.Append(source[i]);
                    if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"') { code.Append(source[++i]); continue; }
                    if (!verbatim && source[i] == '\\' && i + 1 < source.Length) { code.Append(source[++i]); continue; }
                    if (source[i] == c) break;
                }
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
