using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;
using Bim.Mcp;

// P10-31: every MCP tool refuses a repeated argument name uniformly. The arguments object of a tools/call is raw JSON text,
// so a name can appear twice; System.Text.Json keeps both members and a property lookup sees one of them, so without a
// server-wide rule the outcome depended on the tool (P10-27/P5-22 had tool-local checks, the others silently used one
// value). Oracle, through McpServer tools/call on a server with an injected Revit transport (counting sends) and no
// write policy, for every tool in tools/list order and every name its inputSchema declares (all other required names
// given a well-typed value; strings name an absent path under the harness scratch directory):
//   1. Measurement (the probe; reported in the failure message so CI annotations carry it): the call is sent with the
//      name twice in three orders: (wrong type, well typed), (well typed, wrong type), (well typed, well typed). Before the
//      fix the (well typed, wrong type) order was refused "<name> must be <type>" by CallTool's type check and the other
//      orders ran the tool (the last value wins), except jurisdiction_resolve and skill_list whose tool-local check
//      answered their own usage message.
//   2. After the fix every order (and a name given three times, and an undeclared name given twice) is JSON-RPC -32602
//      with exactly "invalid_params: repeated argument <name>" and no result, and no Revit transport send happens; so is a
//      second occurrence whose name spells its first character as a \u00XX escape (either order), for every declared name.
//      The probed tools are exactly the pinned tools/list names in order.
//   3. Non-duplicate calls are untouched: a well-typed revit_* call reaches the transport, and a repeated member nested in
//      an argument value (document, same value twice) is not a repeated argument: it reaches the tool, whose strict
//      envelope check answers the typed envelope_invalid (field document.path) line, with no transport send (only the
//      top-level arguments object is checked by CallTool).
//   4. Static (CallTool's body, up to the next method Matches): McpServer.CallTool holds the one repeated-name check before the unexpected-argument check and the tool run;
//      JurisdictionResolveTool.cs and SkillListTool.cs no longer keep a tool-local duplicate-name check.
internal static class RepeatedArgumentToolTests
{
    public const string MessagePrefix = "invalid_params: repeated argument ";

    public static void Run(string root, string work, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-31 repeated argument names: " + name);
        var absent = Path.GetFullPath(Path.Combine(work, "p10-31-absent", "x.json"));
        var sends = 0;
        var server = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => { sends++; return null; }, null);
        var id = 31000;
        JsonObject Call(string tool, string argumentsText)
        {
            var line = "{\"jsonrpc\":\"2.0\",\"id\":" + (++id).ToString(CultureInfo.InvariantCulture) + ",\"method\":\"tools/call\",\"params\":{\"name\":"
                + JsonSerializer.Serialize(tool) + ",\"arguments\":" + argumentsText + "}}";
            return JsonNode.Parse(server.Handle(line) ?? throw new InvalidOperationException("no response for " + line))!.AsObject();
        }
        static string Outcome(JsonObject response) => response["error"] is JsonObject error
            ? error["code"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) + ":" + error["message"]!.GetValue<string>()
            : response["result"] is JsonObject ? "ran" : "other:" + response.ToJsonString();
        string Valid(string type) => type switch
        {
            "string" => JsonSerializer.Serialize(absent),
            "integer" => "1",
            "boolean" => "false",
            "object" => "{}",
            "array" => "[\"a\"]",
            _ => throw new InvalidOperationException("unexpected schema type " + type),
        };
        static string Wrong(string type) => type == "string" ? "1" : "\"x\"";

        var listed = JsonNode.Parse(server.Handle("{\"jsonrpc\":\"2.0\",\"id\":30999,\"method\":\"tools/list\"}")!)!["result"]!["tools"]!.AsArray();
        var measured = new List<string>();
        var bad = new List<string>();
        var cases = 0;
        var sendsBefore = sends;
        foreach (var tool in listed)
        {
            var name = tool!["name"]!.GetValue<string>();
            var schema = tool["inputSchema"]!;
            var properties = schema["properties"]!.AsObject().Select(p => (Name: p.Key, Type: p.Value!["type"]!.GetValue<string>())).ToArray();
            var required = schema["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            var perTool = new List<string>();
            foreach (var (parameter, type) in properties)
            {
                string Members(IEnumerable<string> repeated) => "{" + string.Join(",",
                    properties.Where(p => p.Name != parameter && required.Contains(p.Name)).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Valid(p.Type)).Concat(repeated)) + "}";
                string Arguments(params string[] values) => Members(values.Select(v => JsonSerializer.Serialize(parameter) + ":" + v));
                // The same name with its first character spelled as a \u00XX escape (JSON-equal, textually different).
                var escapedKey = "\"\\u" + ((int)parameter[0]).ToString("x4", CultureInfo.InvariantCulture) + parameter[1..] + "\"";
                var expected = "-32602:" + MessagePrefix + parameter;
                var orders = new (string Label, string Text)[]
                {
                    ("wrong,valid", Arguments(Wrong(type), Valid(type))),
                    ("valid,wrong", Arguments(Valid(type), Wrong(type))),
                    ("valid,valid", Arguments(Valid(type), Valid(type))),
                    ("valid x3", Arguments(Valid(type), Valid(type), Valid(type))),
                    ("valid,escaped valid", Members([JsonSerializer.Serialize(parameter) + ":" + Valid(type), escapedKey + ":" + Valid(type)])),
                    ("escaped valid,valid", Members([escapedKey + ":" + Valid(type), JsonSerializer.Serialize(parameter) + ":" + Valid(type)])),
                };
                var outcomes = new List<string>();
                foreach (var (label, text) in orders)
                {
                    var response = Call(name, text);
                    var outcome = Outcome(response);
                    cases++;
                    if (outcome != expected || response.ContainsKey("result")) bad.Add(name + "." + parameter + " " + label);
                    if (label is "wrong,valid" or "valid,wrong" or "valid,valid") outcomes.Add(outcome == expected ? "R" : outcome == "ran" ? "ran" : outcome.Replace("-32602:invalid_params: ", "", StringComparison.Ordinal));
                }
                perTool.Add(parameter + "=" + string.Join("|", outcomes));
            }
            // An undeclared name given twice is a repeated argument too (the repeated check precedes the unexpected one).
            var undeclared = Outcome(Call(name, "{\"p10_31_undeclared\":1,\"p10_31_undeclared\":1}"));
            cases++;
            if (undeclared != "-32602:" + MessagePrefix + "p10_31_undeclared") bad.Add(name + ".undeclared");
            measured.Add(name + "{" + string.Join(";", perTool) + "}");
        }
        var summary = string.Join(" ", measured);
        if (summary.Length > 12000) summary = summary[..12000] + "...";
        string[] expectedTools = ["diff", "validate", "room", "plan", "mirror_drift", "mirror_drift_select", "mirror_drift_adopt", "skills_lock_verify", "audit_verify", "diff_intent",
            "generate_typology", "doc_update", "plan_layouts", "doc_realized", "generate_prototypes", "jurisdiction_resolve", "skill_list", .. RevitCommandCatalogue.All.Select(c => c.ToolName)];
        var listedNames = listed.Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Check(listedNames.SequenceEqual(expectedTools), "the probed tools are exactly the pinned tools/list, in order | " + string.Join(",", listedNames));
        Check(bad.Count == 0,
            "every tool in tools/list refuses a repeated argument name with -32602 '" + MessagePrefix + "<name>' in every order (" + cases.ToString(CultureInfo.InvariantCulture)
            + " cases; R = that refusal; orders wrong,valid|valid,wrong|valid,valid) | failing " + bad.Count.ToString(CultureInfo.InvariantCulture) + ": "
            + string.Join(", ", bad.Take(12)) + " | measured: " + summary);
        Check(sends == sendsBefore, "no repeated-name call reaches the Revit transport | sends " + (sends - sendsBefore).ToString(CultureInfo.InvariantCulture));

        // ---- 3. non-duplicate calls are untouched ----
        {
            var query = RevitCommandCatalogue.All.First(spec => !spec.Writes).ToolName;
            var document = "{\"path\":" + JsonSerializer.Serialize(absent) + ",\"unique_id\":\"00000000-0000-0000-0000-000000000000-00000001\"}";
            var before = sends;
            var plain = Call(query, "{\"pipe_name\":\"bim-agentic-os-2025-1\",\"operation_id\":\"p10-31\",\"document\":" + document + ",\"args\":{}}");
            Check(plain.ContainsKey("result") && sends == before + 1, "a well-typed " + query + " call without a repeated name still reaches the transport | " + plain.ToJsonString());
            // The same document with its path member given twice (same value): not CallTool's refusal; the tool's own strict
            // envelope check answers it (measured on CI run 44705390113): isError with the pinned envelope_invalid line, no send.
            var nestedDocument = "{\"path\":" + JsonSerializer.Serialize(absent) + ",\"path\":" + JsonSerializer.Serialize(absent) + ",\"unique_id\":\"00000000-0000-0000-0000-000000000000-00000001\"}";
            var beforeNested = sends;
            var nested = Call(query, "{\"pipe_name\":\"bim-agentic-os-2025-1\",\"operation_id\":\"p10-31\",\"document\":" + nestedDocument + ",\"args\":{}}");
            const string NestedLine = "{\"status\":\"failed\",\"code\":\"envelope_invalid\",\"field\":\"document.path\",\"detail\":\"unexpected or duplicate member\",\"created\":[],\"modified\":[],\"deleted\":[],\"readback\":null,\"receipt\":null,\"warnings\":[]}";
            Check(nested["result"] is JsonObject nestedResult && !nested.ContainsKey("error") && nestedResult["isError"]?.GetValue<bool>() == true
                && nestedResult["content"]?[0]?["text"]?.GetValue<string>() == NestedLine && sends == beforeNested,
                "a repeated member nested in an argument value is not a repeated argument: the tool's own envelope_invalid (document.path) refusal, no transport send | sends "
                + (sends - beforeNested).ToString(CultureInfo.InvariantCulture) + " | " + nested.ToJsonString());
        }

        // ---- 4. static ----
        {
            static string CodeOf(string text) => DocUpdateLinkTests.CodeOf(text);
            var server_ = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")));
            var start = server_.IndexOf("string CallTool(", StringComparison.Ordinal);
            var end = start < 0 ? -1 : server_.IndexOf("static bool Matches(", start, StringComparison.Ordinal);
            var body = start < 0 || end < 0 ? "" : server_[start..end];
            var repeated = body.IndexOf("repeated argument", StringComparison.Ordinal);
            var unexpected = body.IndexOf("unexpected argument", StringComparison.Ordinal);
            var execute = body.IndexOf("tool.Execute(", StringComparison.Ordinal);
            Check(repeated > 0 && unexpected > repeated && execute > unexpected,
                "McpServer.CallTool checks repeated names before unexpected arguments and before the tool runs | " + repeated.ToString(CultureInfo.InvariantCulture) + " " + unexpected.ToString(CultureInfo.InvariantCulture) + " " + execute.ToString(CultureInfo.InvariantCulture));
            foreach (var file in new[] { "JurisdictionResolveTool.cs", "SkillListTool.cs" })
            {
                var code = CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", file)));
                Check(!code.Contains("HashSet", StringComparison.Ordinal) && !code.Contains("EnumerateObject", StringComparison.Ordinal),
                    file + " keeps no tool-local duplicate-name check (CallTool's rule is the only one)");
            }
        }
    }
}
