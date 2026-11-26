using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;
using Bim.Mcp;

// P10-33: repeated keys outside the tools/call arguments object, and an argument name that is not valid text. A request
// line is raw JSON text, so a member can appear twice; System.Text.Json keeps both and a property lookup sees one of them.
// Oracle, through McpServer.Handle on a server with an injected Revit transport (counting sends) and no write policy:
//   1. Measurement (the probe; every case's outcome "code:message@id", "result@id:text" or "none" is reported in the
//      failure message so CI annotations carry it).
//   2. After the fix: any member of the JSON-RPC request object given more than once (id, method, params, jsonrpc or any
//      other, also through a \u-escaped spelling) is -32600 "invalid_request: repeated member <name>", before any other
//      envelope check; its id is the request id when id itself is given once and is a valid id, else null (a repeated id
//      is never echoed). A notification with a repeated member gets that error with id null, as other invalid requests do.
//      Any member of tools/call params given more than once (name, arguments or any other) is -32602
//      "invalid_params: repeated params member <name>" with the request id. An arguments member name that is not valid
//      UTF-16 text (a lone surrogate escape, once or repeated) is -32602 "invalid_params: argument name is not valid text"
//      with the request id. None of these reaches a tool or the Revit transport.
//   3. Well-formed requests are unchanged byte for byte (ping, unknown method, missing method, unknown tool with _meta once,
//      a valid surrogate pair in an undeclared argument name, a revit_* call that reaches the transport, P10-31's refusal).
//   4. Static: Dispatch checks repeated members before the notification return; CallTool checks repeated params members
//      before the name check and the argument-name text check before the unexpected-argument check.
internal static class RepeatedEnvelopeKeyTests
{
    public const string EnvelopePrefix = "invalid_request: repeated member ";
    public const string ParamsPrefix = "invalid_params: repeated params member ";
    public const string NotText = "invalid_params: argument name is not valid text";

    public static void Run(string root, string work, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-33 repeated envelope keys: " + name);
        var absent = Path.GetFullPath(Path.Combine(work, "p10-33-absent", "x.rvt"));
        var sends = 0;
        var server = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => { sends++; return null; }, null);
        var revit = RevitCommandCatalogue.All.First(spec => !spec.Writes).ToolName;
        var document = "{\"path\":" + JsonSerializer.Serialize(absent) + ",\"unique_id\":\"00000000-0000-0000-0000-000000000000-00000001\"}";
        var revitArguments = "{\"pipe_name\":\"bim-agentic-os-2025-1\",\"operation_id\":\"p10-33\",\"document\":" + document + ",\"args\":{}}";
        var revitParams = "{\"name\":" + JsonSerializer.Serialize(revit) + ",\"arguments\":" + revitArguments + "}";
        static string Call(string id, string parameters) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":" + parameters + "}";

        static string Outcome(string? response)
        {
            if (response is null) return "none";
            var node = JsonNode.Parse(response)!.AsObject();
            var id = node["id"]?.ToJsonString() ?? "null";
            if (node["error"] is JsonObject error)
                return error["code"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) + ":" + error["message"]!.GetValue<string>() + "@" + id;
            if (node["result"] is JsonObject result)
                return "result@" + id + ":" + (result["content"]?[0]?["text"]?.GetValue<string>() ?? result.ToJsonString());
            return "other:" + response;
        }

        // (label, request line, expected outcome); none of these may reach the transport
        var cases = new List<(string Label, string Line, string Expected)>
        {
            // ---- envelope: -32600 ----
            ("id twice", """{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}""", "-32600:" + EnvelopePrefix + "id@null"),
            ("id twice same value", """{"jsonrpc":"2.0","id":1,"id":1,"method":"ping"}""", "-32600:" + EnvelopePrefix + "id@null"),
            ("id twice string,number", """{"jsonrpc":"2.0","id":"a","method":"ping","id":3}""", "-32600:" + EnvelopePrefix + "id@null"),
            ("id twice escaped spelling", """{"jsonrpc":"2.0","id":4,"\u0069d":5,"method":"ping"}""", "-32600:" + EnvelopePrefix + "id@null"),
            ("method twice ping,tools/list", """{"jsonrpc":"2.0","id":6,"method":"ping","method":"tools/list"}""", "-32600:" + EnvelopePrefix + "method@6"),
            ("method twice tools/list,ping", """{"jsonrpc":"2.0","id":7,"method":"tools/list","method":"ping"}""", "-32600:" + EnvelopePrefix + "method@7"),
            ("method twice same value", """{"jsonrpc":"2.0","id":"m","method":"ping","method":"ping"}""", "-32600:" + EnvelopePrefix + "method@\"m\""),
            ("method twice escaped spelling", """{"jsonrpc":"2.0","id":8,"method":"ping","\u006dethod":"ping"}""", "-32600:" + EnvelopePrefix + "method@8"),
            ("params twice (core)", """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"p10_33_unknown"},"params":{"name":"validate","arguments":{"project":{}}}}""", "-32600:" + EnvelopePrefix + "params@9"),
            ("params twice (revit, both valid)", "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":" + revitParams + ",\"params\":" + revitParams + "}", "-32600:" + EnvelopePrefix + "params@10"),
            ("jsonrpc twice", """{"jsonrpc":"2.0","jsonrpc":"2.0","id":11,"method":"ping"}""", "-32600:" + EnvelopePrefix + "jsonrpc@11"),
            ("undeclared member twice", """{"jsonrpc":"2.0","id":12,"method":"ping","x":1,"x":2}""", "-32600:" + EnvelopePrefix + "x@12"),
            ("notification method twice", """{"jsonrpc":"2.0","method":"notifications/initialized","method":"ping"}""", "-32600:" + EnvelopePrefix + "method@null"),
            ("notification params twice", """{"jsonrpc":"2.0","method":"notifications/initialized","params":{},"params":{}}""", "-32600:" + EnvelopePrefix + "params@null"),
            // ---- tools/call params: -32602 ----
            ("params.name twice", Call("20", """{"name":"p10_33_unknown","name":"validate","arguments":{"project":{}}}"""), "-32602:" + ParamsPrefix + "name@20"),
            ("params.name twice reversed", Call("21", """{"name":"validate","name":"p10_33_unknown","arguments":{"project":{}}}"""), "-32602:" + ParamsPrefix + "name@21"),
            ("params.name twice (revit, same)", Call("22", "{\"name\":" + JsonSerializer.Serialize(revit) + ",\"name\":" + JsonSerializer.Serialize(revit) + ",\"arguments\":" + revitArguments + "}"), "-32602:" + ParamsPrefix + "name@22"),
            ("params.name twice escaped spelling", Call("23", """{"name":"validate","\u006eame":"validate","arguments":{"project":{}}}"""), "-32602:" + ParamsPrefix + "name@23"),
            ("params.arguments twice", Call("24", """{"name":"validate","arguments":{"project":{}},"arguments":{}}"""), "-32602:" + ParamsPrefix + "arguments@24"),
            ("params.arguments twice reversed", Call("\"s\"", """{"name":"validate","arguments":{},"arguments":{"project":{}}}"""), "-32602:" + ParamsPrefix + "arguments@\"s\""),
            ("params.arguments twice (revit, both valid)", Call("25", "{\"name\":" + JsonSerializer.Serialize(revit) + ",\"arguments\":" + revitArguments + ",\"arguments\":" + revitArguments + "}"), "-32602:" + ParamsPrefix + "arguments@25"),
            ("params._meta twice", Call("26", """{"name":"validate","arguments":{"project":{}},"_meta":{},"_meta":{}}"""), "-32602:" + ParamsPrefix + "_meta@26"),
            // ---- an argument name that is not valid text: -32602 ----
            ("lone high surrogate argument name", Call("30", """{"name":"validate","arguments":{"\ud800":1}}"""), "-32602:" + NotText + "@30"),
            ("lone high surrogate argument name twice", Call("31", """{"name":"validate","arguments":{"\ud800":1,"\ud800":1}}"""), "-32602:" + NotText + "@31"),
            ("lone low surrogate argument name", Call("32", """{"name":"validate","arguments":{"\udc00":1}}"""), "-32602:" + NotText + "@32"),
            ("lone surrogate after a declared argument", Call("33", """{"name":"validate","arguments":{"project":{},"\ud800":1}}"""), "-32602:" + NotText + "@33"),
            ("lone surrogate in a revit call", Call("34", "{\"name\":" + JsonSerializer.Serialize(revit) + ",\"arguments\":" + revitArguments[..^1] + ",\"\\ud800\":1}}"), "-32602:" + NotText + "@34"),
            ("lone surrogate on an unknown tool", Call("35", """{"name":"p10_33_unknown","arguments":{"\ud800":1}}"""), "result@35:{\"status\":\"failed\",\"code\":\"unknown_tool\"}"),
        };

        var measured = new List<string>();
        var bad = new List<string>();
        var before = sends;
        foreach (var (label, line, expected) in cases)
        {
            string outcome;
            try { outcome = Outcome(server.Handle(line)); }
            catch (Exception error) { outcome = "threw " + error.GetType().Name + ": " + error.Message; }
            measured.Add(label + " => " + outcome);
            if (outcome != expected) bad.Add(label);
        }
        var refusedSends = sends - before;
        Check(bad.Count == 0 && refusedSends == 0,
            "every repeated envelope/params member and every non-text argument name is refused one documented way with the request id when unambiguous, no transport send (sends "
            + refusedSends.ToString(CultureInfo.InvariantCulture) + ") | failing " + bad.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", bad)
            + " | measured: " + string.Join(" ; ", measured));

        // ---- 3. well-formed requests are unchanged byte for byte ----
        {
            var exact = new List<(string Label, string Line, string Expected)>
            {
                ("ping", """{"jsonrpc":"2.0","id":40,"method":"ping"}""", """{"jsonrpc":"2.0","id":40,"result":{}}"""),
                ("unknown method", """{"jsonrpc":"2.0","id":"u","method":"resources/list"}""", """{"jsonrpc":"2.0","id":"u","error":{"code":-32601,"message":"method_not_found"}}"""),
                ("missing method", """{"jsonrpc":"2.0","id":41}""", """{"jsonrpc":"2.0","id":41,"error":{"code":-32600,"message":"invalid_request"}}"""),
                ("unknown tool with _meta once", Call("42", """{"name":"p10_33_unknown","arguments":{},"_meta":{"progressToken":1}}"""),
                    """{"jsonrpc":"2.0","id":42,"result":{"content":[{"type":"text","text":"{\"status\":\"failed\",\"code\":\"unknown_tool\"}"}],"isError":true}}"""),
                ("missing name", Call("43", """{"arguments":{}}"""), """{"jsonrpc":"2.0","id":43,"error":{"code":-32602,"message":"invalid_params: name required"}}"""),
                ("P10-31 repeated argument", Call("44", """{"name":"validate","arguments":{"project":{},"project":{}}}"""),
                    """{"jsonrpc":"2.0","id":44,"error":{"code":-32602,"message":"invalid_params: repeated argument project"}}"""),
                ("notification", """{"jsonrpc":"2.0","method":"notifications/initialized"}""", "none"),
            };
            var differing = new List<string>();
            foreach (var (label, line, expected) in exact)
            {
                var response = server.Handle(line) ?? "none";
                if (response != expected) differing.Add(label + " => " + response);
            }
            Check(differing.Count == 0, "well-formed requests keep their exact bytes | " + string.Join(" ; ", differing));
            // A valid surrogate pair (U+1F600) in an undeclared argument name is valid text: still the unexpected-argument refusal.
            var pair = Outcome(server.Handle(Call("45", """{"name":"validate","arguments":{"\ud83d\ude00":1}}""")));
            Check(pair == "-32602:invalid_params: unexpected argument \U0001F600@45", "a valid surrogate pair in an argument name is still unexpected argument | " + pair);
            var sendsBefore = sends;
            var plain = Outcome(server.Handle(Call("46", revitParams)));
            Check(plain.StartsWith("result@46:", StringComparison.Ordinal) && sends == sendsBefore + 1, "a well-formed " + revit + " call still reaches the transport once | " + plain);
        }

        // ---- 4. static ----
        {
            var source = DocUpdateLinkTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")));
            string Body(string start, string next)
            {
                var from = source.IndexOf(start, StringComparison.Ordinal);
                var to = from < 0 ? -1 : source.IndexOf(next, from, StringComparison.Ordinal);
                return from < 0 || to < 0 ? "" : source[from..to];
            }
            var dispatch = Body("string? Dispatch(", "static JsonObject Initialize(");
            var envelope = dispatch.IndexOf("repeated member ", StringComparison.Ordinal);
            var notification = dispatch.IndexOf("if (!hasId) return null;", StringComparison.Ordinal);
            Check(envelope > 0 && notification > envelope, "Dispatch checks repeated request members before the notification return | " + envelope.ToString(CultureInfo.InvariantCulture) + " " + notification.ToString(CultureInfo.InvariantCulture));
            var call = Body("string CallTool(", "static bool Matches(");
            var paramsCheck = call.IndexOf("repeated params member ", StringComparison.Ordinal);
            var nameCheck = call.IndexOf("name required", StringComparison.Ordinal);
            var textCheck = call.IndexOf("argument name is not valid text", StringComparison.Ordinal);
            var unexpected = call.IndexOf("unexpected argument", StringComparison.Ordinal);
            Check(paramsCheck > 0 && nameCheck > paramsCheck && textCheck > nameCheck && unexpected > textCheck,
                "CallTool checks repeated params members before the name and argument names as text before unexpected arguments | "
                + string.Join(" ", new[] { paramsCheck, nameCheck, textCheck, unexpected }.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        }

        RunP10_34(root, check);
        RunP10_36(root, check);
    }

    public const string EnvelopeNotText = "invalid_request: member name is not valid text";
    public const string MethodNotText = "invalid_request: method is not valid text";
    public const string ParamsNotText = "invalid_params: params member name is not valid text";
    public const string NameNotText = "invalid_params: name is not valid text";

    // P10-34: text that is not valid UTF-16 (a lone surrogate \u escape) outside the arguments object, and exact bytes of
    // the repeated-member refusals. Oracle, through McpServer.Handle with a counting Revit transport:
    //   1. Measurement (the probe): each case's outcome is reported in the failure message.
    //   2. After the fix: a request member name that is not valid text (once, repeated, first or last, also beside a
    //      repeated valid member) is -32600 "invalid_request: member name is not valid text", checked before the repeated
    //      member check; its id is the request id when id is given once with a valid id value (a string id must be valid
    //      text), else null (notifications too). A method value that is not valid text is -32600
    //      "invalid_request: method is not valid text" (id kept; a notification gets id null, like any invalid method). A
    //      jsonrpc value that is not valid text is the existing -32600 "invalid_request" with the id; an id string that is
    //      not valid text is the existing -32600 "invalid_request" with id null. A tools/call params member name that is not
    //      valid text is -32602 "invalid_params: params member name is not valid text" before the repeated params member
    //      check; a params.name value that is not valid text is -32602 "invalid_params: name is not valid text" after the
    //      name type check, before the tool lookup. None reaches a tool or the Revit transport; none is internal_error.
    //   3. Exact bytes: one -32600 and one -32602 repeated-member refusal, the new refusals, and well-formed neighbours.
    //   4. Static: Dispatch checks member names as text before repeated members and the method text before the
    //      notification return; CallTool checks params member names as text before repeated params members and the name
    //      value as text after "name required", before the tool lookup.
    static void RunP10_34(string root, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-34 lone-surrogate envelope text: " + name);
        var sends = 0;
        var server = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => { sends++; return null; }, null);
        var revit = RevitCommandCatalogue.All.First(spec => !spec.Writes).ToolName;
        var document = "{\"path\":\"p10-34-absent.rvt\",\"unique_id\":\"00000000-0000-0000-0000-000000000000-00000001\"}";
        var revitArguments = "{\"pipe_name\":\"bim-agentic-os-2025-1\",\"operation_id\":\"p10-34\",\"document\":" + document + ",\"args\":{}}";
        static string Call(string id, string parameters) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":" + parameters + "}";

        static string Outcome(string? response)
        {
            if (response is null) return "none";
            var node = JsonNode.Parse(response)!.AsObject();
            var id = node["id"]?.ToJsonString() ?? "null";
            if (node["error"] is JsonObject error)
                return error["code"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) + ":" + error["message"]!.GetValue<string>() + "@" + id;
            if (node["result"] is JsonObject result)
                return "result@" + id + ":" + (result["content"]?[0]?["text"]?.GetValue<string>() ?? result.ToJsonString());
            return "other:" + response;
        }

        var cases = new List<(string Label, string Line, string Expected)>
        {
            // ---- envelope member names: -32600 ----
            ("E1 lone high name", """{"jsonrpc":"2.0","id":1,"method":"ping","\ud800":1}""", "-32600:" + EnvelopeNotText + "@1"),
            ("E2 lone name twice", """{"jsonrpc":"2.0","id":2,"method":"ping","\ud800":1,"\ud800":2}""", "-32600:" + EnvelopeNotText + "@2"),
            ("E3 lone name beside repeated method", """{"jsonrpc":"2.0","id":3,"method":"ping","method":"ping","\ud800":1}""", "-32600:" + EnvelopeNotText + "@3"),
            ("E4 lone low name first", """{"\udc00":1,"jsonrpc":"2.0","id":4,"method":"ping"}""", "-32600:" + EnvelopeNotText + "@4"),
            ("E5 lone name beside repeated id", """{"jsonrpc":"2.0","id":5,"id":6,"method":"ping","\ud800":1}""", "-32600:" + EnvelopeNotText + "@null"),
            ("E6 lone name in notification", """{"jsonrpc":"2.0","method":"notifications/initialized","\ud800":1}""", "-32600:" + EnvelopeNotText + "@null"),
            ("E7 lone name in tools/call", Call("7", """{"name":"validate","arguments":{"project":{}}}""")[..^1] + ",\"\\ud800\":1}", "-32600:" + EnvelopeNotText + "@7"),
            ("E8 lone name with lone id", """{"jsonrpc":"2.0","id":"\ud800","method":"ping","\ud800":1}""", "-32600:" + EnvelopeNotText + "@null"),
            // ---- envelope values ----
            ("V1 method lone high", """{"jsonrpc":"2.0","id":11,"method":"\ud800"}""", "-32600:" + MethodNotText + "@11"),
            ("V2 method ping+lone low", """{"jsonrpc":"2.0","id":"v","method":"ping\udc00"}""", "-32600:" + MethodNotText + "@\"v\""),
            ("V3 method lone in notification", """{"jsonrpc":"2.0","method":"\ud800"}""", "-32600:" + MethodNotText + "@null"),
            ("V4 jsonrpc lone", """{"jsonrpc":"\ud800","id":13,"method":"ping"}""", "-32600:invalid_request@13"),
            ("V5 id lone", """{"jsonrpc":"2.0","id":"\ud800","method":"ping"}""", "-32600:invalid_request@null"),
            ("V6 id lone with repeated method", """{"jsonrpc":"2.0","id":"\ud800","method":"ping","method":"ping"}""", "-32600:" + EnvelopePrefix + "method@null"),
            // ---- tools/call params: -32602 ----
            ("P1 params lone name", Call("20", """{"name":"validate","\ud800":1,"arguments":{"project":{}}}"""), "-32602:" + ParamsNotText + "@20"),
            ("P2 params lone name twice", Call("21", """{"name":"validate","\ud800":1,"\ud800":1,"arguments":{}}"""), "-32602:" + ParamsNotText + "@21"),
            ("P3 params lone name beside repeated name", Call("22", """{"name":"validate","name":"validate","\udc00":1}"""), "-32602:" + ParamsNotText + "@22"),
            ("P4 params lone name (revit)", Call("23", "{\"name\":" + JsonSerializer.Serialize(revit) + ",\"arguments\":" + revitArguments + ",\"\\ud800\":1}"), "-32602:" + ParamsNotText + "@23"),
            ("P5 params lone name (unknown tool)", Call("24", """{"\ud800":1,"name":"p10_34_unknown","arguments":{}}"""), "-32602:" + ParamsNotText + "@24"),
            ("P6 params.name lone high", Call("25", """{"name":"\ud800","arguments":{}}"""), "-32602:" + NameNotText + "@25"),
            ("P7 params.name validate+lone low", Call("\"n\"", """{"name":"validate\udc00","arguments":{"project":{}}}"""), "-32602:" + NameNotText + "@\"n\""),
            ("P8 params.name revit_+lone", Call("26", "{\"name\":\"revit_\\ud800\",\"arguments\":" + revitArguments + "}"), "-32602:" + NameNotText + "@26"),
            ("P9 params lone name and lone name value", Call("27", """{"name":"\ud800","\ud800":1}"""), "-32602:" + ParamsNotText + "@27"),
        };

        var measured = new List<string>();
        var bad = new List<string>();
        foreach (var (label, line, expected) in cases)
        {
            string outcome;
            try { outcome = Outcome(server.Handle(line)); }
            catch (Exception error) { outcome = "threw " + error.GetType().Name + ": " + error.Message; }
            measured.Add(label + " => " + outcome);
            if (outcome != expected) bad.Add(label);
        }
        Check(bad.Count == 0 && sends == 0,
            "non-text names and values outside arguments are refused one documented way with the id when unambiguous, no transport send (sends "
            + sends.ToString(CultureInfo.InvariantCulture) + ") | failing " + bad.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", bad)
            + " | measured: " + string.Join(" ; ", measured));

        // ---- 3. exact response bytes ----
        {
            var exact = new List<(string Label, string Line, string Expected)>
            {
                ("-32600 repeated member", """{"jsonrpc":"2.0","id":6,"method":"ping","method":"tools/list"}""",
                    """{"jsonrpc":"2.0","id":6,"error":{"code":-32600,"message":"invalid_request: repeated member method"}}"""),
                ("-32600 repeated id", """{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}""",
                    """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"invalid_request: repeated member id"}}"""),
                ("-32602 repeated params member", Call("20", """{"name":"p10_33_unknown","name":"validate","arguments":{"project":{}}}"""),
                    """{"jsonrpc":"2.0","id":20,"error":{"code":-32602,"message":"invalid_params: repeated params member name"}}"""),
                ("-32600 member name not text", """{"jsonrpc":"2.0","id":1,"method":"ping","\ud800":1}""",
                    """{"jsonrpc":"2.0","id":1,"error":{"code":-32600,"message":"invalid_request: member name is not valid text"}}"""),
                ("-32600 method not text", """{"jsonrpc":"2.0","id":"m","method":"\ud800"}""",
                    """{"jsonrpc":"2.0","id":"m","error":{"code":-32600,"message":"invalid_request: method is not valid text"}}"""),
                ("-32602 params member name not text", Call("21", """{"name":"validate","\ud800":1}"""),
                    """{"jsonrpc":"2.0","id":21,"error":{"code":-32602,"message":"invalid_params: params member name is not valid text"}}"""),
                ("-32602 name not text", Call("22", """{"name":"\ud800"}"""),
                    """{"jsonrpc":"2.0","id":22,"error":{"code":-32602,"message":"invalid_params: name is not valid text"}}"""),
                // well-formed neighbours: unchanged
                ("ping", """{"jsonrpc":"2.0","id":40,"method":"ping"}""", """{"jsonrpc":"2.0","id":40,"result":{}}"""),
                ("ping with a valid surrogate pair member", """{"jsonrpc":"2.0","id":41,"method":"ping","\ud83d\ude00":1}""", """{"jsonrpc":"2.0","id":41,"result":{}}"""),
                ("unknown method with valid escaped text", """{"jsonrpc":"2.0","id":42,"method":"\u0070ing\u00e9"}""", """{"jsonrpc":"2.0","id":42,"error":{"code":-32601,"message":"method_not_found"}}"""),
                ("unknown tool with a valid surrogate pair params member", Call("43", """{"name":"p10_34_unknown","\ud83d\ude00":1}"""),
                    """{"jsonrpc":"2.0","id":43,"result":{"content":[{"type":"text","text":"{\"status\":\"failed\",\"code\":\"unknown_tool\"}"}],"isError":true}}"""),
                ("unknown tool with a valid surrogate pair name", Call("44", """{"name":"\ud83d\ude00"}"""),
                    """{"jsonrpc":"2.0","id":44,"result":{"content":[{"type":"text","text":"{\"status\":\"failed\",\"code\":\"unknown_tool\"}"}],"isError":true}}"""),
                ("name wrong type", Call("45", """{"name":1}"""), """{"jsonrpc":"2.0","id":45,"error":{"code":-32602,"message":"invalid_params: name required"}}"""),
            };
            var differing = new List<string>();
            foreach (var (label, line, expected) in exact)
            {
                string response;
                try { response = server.Handle(line) ?? "none"; }
                catch (Exception error) { response = "threw " + error.GetType().Name + ": " + error.Message; }
                if (response != expected) differing.Add(label + " => " + response);
            }
            Check(differing.Count == 0, "refusal and well-formed response bytes are exact | " + string.Join(" ; ", differing));
            // A valid surrogate pair string id is still echoed (parsed, not byte-pinned: the escaping is the writer's).
            var pairId = server.Handle("""{"jsonrpc":"2.0","id":"\ud83d\ude00","method":"ping"}""") ?? "none";
            var pairNode = JsonNode.Parse(pairId)!.AsObject();
            Check(pairNode["id"]?.GetValue<string>() == "\U0001F600" && pairNode["result"] is JsonObject { Count: 0 }, "a valid surrogate pair string id is still echoed with the ping result | " + pairId);
        }

        // ---- 4. static ----
        {
            var source = DocUpdateLinkTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")));
            string Body(string start, string next)
            {
                var from = source.IndexOf(start, StringComparison.Ordinal);
                var to = from < 0 ? -1 : source.IndexOf(next, from, StringComparison.Ordinal);
                return from < 0 || to < 0 ? "" : source[from..to];
            }
            var dispatch = Body("string? Dispatch(", "static JsonObject Initialize(");
            var envelopeText = dispatch.IndexOf("member name is not valid text", StringComparison.Ordinal);
            var envelopeRepeat = dispatch.IndexOf("repeated member ", StringComparison.Ordinal);
            var methodText = dispatch.IndexOf("method is not valid text", StringComparison.Ordinal);
            var notification = dispatch.IndexOf("if (!hasId) return null;", StringComparison.Ordinal);
            Check(envelopeText > 0 && envelopeRepeat > envelopeText && methodText > envelopeRepeat && notification > methodText,
                "Dispatch checks member names as text, then repeated members, then the method text, before the notification return | "
                + string.Join(" ", new[] { envelopeText, envelopeRepeat, methodText, notification }.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            var call = Body("string CallTool(", "static bool Matches(");
            var paramsText = call.IndexOf("params member name is not valid text", StringComparison.Ordinal);
            var paramsRepeat = call.IndexOf("repeated params member ", StringComparison.Ordinal);
            var nameRequired = call.IndexOf("name required", StringComparison.Ordinal);
            var nameText = call.IndexOf("params: name is not valid text", StringComparison.Ordinal);
            var lookup = call.IndexOf("unknown_tool", StringComparison.Ordinal);
            Check(paramsText > 0 && paramsRepeat > paramsText && nameRequired > paramsRepeat && nameText > nameRequired && lookup > nameText,
                "CallTool checks params member names as text before repeated params members and the name text after name required, before the tool lookup | "
                + string.Join(" ", new[] { paramsText, paramsRepeat, nameRequired, nameText, lookup }.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        }
    }

    public const string LineNotText = "parse_error: line is not valid text";

    // P10-36: lone surrogates in the params of the other methods, inside _meta, as raw (unescaped) chars in the line
    // given to Handle, and through the real Release Bim.Mcp.dll over stdio. Oracle, through McpServer.Handle with a
    // counting Revit transport, then the real process:
    //   1. Measurement (the probe): each case's outcome is reported in the failure message.
    //   2. After the fix: a params object member name that is not valid text on initialize, ping or tools/list is -32602
    //      "invalid_params: params member name is not valid text" with the id (the tools/call refusal of P10-34). A line
    //      holding an unpaired surrogate char (raw, not a \u escape; anywhere, also outside strings or in a notification)
    //      is not JSON text: -32700 "parse_error: line is not valid text" with id null, checked after request_too_large,
    //      before parsing (it was internal_error). Pinned as measured, with the reason: a lone surrogate inside _meta
    //      (names or values; tools/call, ping, initialize) is accepted, since _meta is never read, compared or echoed; a
    //      notification's params are never read (no response); an unknown method's params are never read (-32601).
    //   3. Exact bytes: the new refusals, the echoed id of a valid surrogate pair (escaped and raw), well-formed neighbours.
    //   4. The real Release Bim.Mcp.dll over stdio answers the P10-34 refusals and the new -32602 byte for byte (P10-40: each
    //      response line matched to its request by id, then the whole stream in order); stdin is
    //      UTF-8 decoded with replacement, so WTF-8 lone-surrogate bytes (ED A0 80) become three U+FFFD and are valid text
    //      (pinned as measured); a raw UTF-8 surrogate pair id is echoed like the escaped one.
    //   5. Static: Handle checks the line as text after the size check and before JsonDocument.Parse; Dispatch checks
    //      the params names of the other methods after the notification return.
    static void RunP10_36(string root, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-36 lone surrogates in other params and the raw line: " + name);
        static string Short(string text) => text.Length > 900 ? text[..900] + "..." : text;
        var sends = 0;
        var server = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => { sends++; return null; }, null);
        var lone = new string((char)0xD800, 1);
        var loneLow = new string((char)0xDC00, 1);
        var pair = "\U0001F600";
        static string Request(string id, string method, string parameters) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + parameters + "}";
        const string Init = "\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"p10-36\",\"version\":\"0\"}";

        static string Outcome(string? response)
        {
            if (response is null) return "none";
            var node = JsonNode.Parse(response)!.AsObject();
            var id = node["id"]?.ToJsonString() ?? "null";
            if (node["error"] is JsonObject error)
                return error["code"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) + ":" + error["message"]!.GetValue<string>() + "@" + id;
            if (node["result"] is JsonObject result)
                return "result@" + id + ":" + (result["content"]?[0]?["text"]?.GetValue<string>() ?? result.ToJsonString());
            return "other:" + response;
        }
        string Safe(string line)
        {
            try { return Outcome(server.Handle(line)); }
            catch (Exception error) { return "threw " + error.GetType().Name + ": " + error.Message; }
        }

        var initialized = Safe(Request("90", "initialize", "{" + Init + "}"));
        var listed = Safe("""{"jsonrpc":"2.0","id":91,"method":"tools/list"}""");
        Check(initialized.StartsWith("result@90:{\"protocolVersion\":", StringComparison.Ordinal) && listed.StartsWith("result@91:{\"tools\":[", StringComparison.Ordinal),
            "fixture: initialize and tools/list answer | " + Short(initialized) + " / " + Short(listed));
        string As(string outcome, string id) => outcome[..outcome.IndexOf('@')] + "@" + id + outcome[outcome.IndexOf(':')..];
        static string UnknownTool(string id) => "result@" + id + ":{\"status\":\"failed\",\"code\":\"unknown_tool\"}";

        var cases = new List<(string Label, string Line, string Expected)>
        {
            // ---- params member names of the other methods: -32602 ----
            ("O1 ping params lone name", Request("1", "ping", """{"\ud800":1}"""), "-32602:" + ParamsNotText + "@1"),
            ("O2 initialize params lone name", Request("2", "initialize", "{" + Init + ",\"\\ud800\":1}"), "-32602:" + ParamsNotText + "@2"),
            ("O3 tools/list params lone low name", Request("3", "tools/list", """{"\udc00":1}"""), "-32602:" + ParamsNotText + "@3"),
            ("O4 tools/list params lone name twice", Request("\"l\"", "tools/list", """{"cursor":"x","\ud800":1,"\ud800":2}"""), "-32602:" + ParamsNotText + "@\"l\""),
            ("O5 initialize params lone name first", Request("4", "initialize", "{\"\\udc00\":1," + Init + "}"), "-32602:" + ParamsNotText + "@4"),
            // pinned as measured: never read
            ("O6 notification params lone name", """{"jsonrpc":"2.0","method":"notifications/initialized","params":{"\ud800":1}}""", "none"),
            ("O7 unknown method params lone name", Request("6", "resources/list", """{"\ud800":1}"""), "-32601:method_not_found@6"),
            // ---- inside _meta: pinned as measured (accepted, never read) ----
            ("M1 tools/call _meta lone name", Request("10", "tools/call", """{"name":"p10_36_unknown","arguments":{},"_meta":{"\ud800":1}}"""), UnknownTool("10")),
            ("M2 tools/call _meta progressToken lone", Request("11", "tools/call", """{"name":"p10_36_unknown","_meta":{"progressToken":"\ud800"}}"""), UnknownTool("11")),
            ("M3 ping _meta lone name", Request("12", "ping", """{"_meta":{"\ud800":1}}"""), "result@12:{}"),
            ("M4 initialize _meta lone value", Request("13", "initialize", "{" + Init + ",\"_meta\":{\"progressToken\":\"\\udc00\"}}"), As(initialized, "13")),
            ("M5 tools/list _meta lone name", Request("14", "tools/list", """{"_meta":{"\udc00":"\ud800"}}"""), As(listed, "14")),
            // ---- a raw (unescaped) unpaired surrogate char in the line: -32700 ----
            ("R1 raw lone in envelope name", "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"ping\",\"" + lone + "\":1}", "-32700:" + LineNotText + "@null"),
            ("R2 raw lone low in method", "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"ping" + loneLow + "\"}", "-32700:" + LineNotText + "@null"),
            ("R3 raw lone in id", "{\"jsonrpc\":\"2.0\",\"id\":\"" + lone + "\",\"method\":\"ping\"}", "-32700:" + LineNotText + "@null"),
            ("R4 raw lone in argument name", Request("23", "tools/call", "{\"name\":\"validate\",\"arguments\":{\"" + lone + "\":1}}"), "-32700:" + LineNotText + "@null"),
            ("R5 raw lone in ping params name", Request("24", "ping", "{\"" + lone + "\":1}"), "-32700:" + LineNotText + "@null"),
            ("R6 raw lone in _meta value", Request("25", "tools/call", "{\"name\":\"p10_36_unknown\",\"_meta\":{\"progressToken\":\"" + lone + "\"}}"), "-32700:" + LineNotText + "@null"),
            ("R7 raw lone after the object", """{"jsonrpc":"2.0","id":26,"method":"ping"}""" + lone, "-32700:" + LineNotText + "@null"),
            ("R8 raw lone in a notification", "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"" + lone + "\":1}", "-32700:" + LineNotText + "@null"),
            ("R9 raw reversed pair in name", "{\"jsonrpc\":\"2.0\",\"id\":28,\"method\":\"ping\",\"" + loneLow + lone + "\":1}", "-32700:" + LineNotText + "@null"),
            ("R10 raw lone in a tools/call name", Request("29", "tools/call", "{\"name\":\"validate" + lone + "\",\"arguments\":{\"project\":{}}}"), "-32700:" + LineNotText + "@null"),
            // raw valid pairs stay valid text
            ("W1 raw pair envelope name", "{\"jsonrpc\":\"2.0\",\"id\":30,\"method\":\"ping\",\"" + pair + "\":1}", "result@30:{}"),
            ("W2 raw pair method", "{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"ping" + pair + "\"}", "-32601:method_not_found@31"),
            ("W3 raw pair ping params name", Request("32", "ping", "{\"" + pair + "\":1}"), "result@32:{}"),
            ("W4 escaped pair ping params name", Request("33", "ping", """{"\ud83d\ude00":1}"""), "result@33:{}"),
        };

        var measured = new List<string>();
        var bad = new List<string>();
        foreach (var (label, line, expected) in cases)
        {
            var outcome = Safe(line);
            measured.Add(label + " => " + (outcome.Length > 160 ? outcome[..160] + "..." : outcome));
            if (outcome != expected) bad.Add(label + " (expected " + expected + ")");
        }
        Check(bad.Count == 0 && sends == 0,
            "lone surrogates in other params, _meta and the raw line give one documented outcome, no transport send (sends "
            + sends.ToString(CultureInfo.InvariantCulture) + ") | failing " + bad.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", bad)
            + " | measured: " + string.Join(" ; ", measured));

        // ---- 3. exact bytes ----
        const string PairIdEcho = """{"jsonrpc":"2.0","id":"\uD83D\uDE00","result":{}}""";
        const string PingParamsRefusal = """{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"invalid_params: params member name is not valid text"}}""";
        const string RawLineRefusal = """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"parse_error: line is not valid text"}}""";
        {
            var exact = new List<(string Label, string Line, string Expected)>
            {
                ("-32602 ping params name not text", Request("1", "ping", """{"\ud800":1}"""), PingParamsRefusal),
                ("-32602 tools/list params name not text", Request("\"t\"", "tools/list", """{"\udc00":1}"""),
                    """{"jsonrpc":"2.0","id":"t","error":{"code":-32602,"message":"invalid_params: params member name is not valid text"}}"""),
                ("-32700 raw lone surrogate", "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\",\"" + lone + "\":1}", RawLineRefusal),
                ("escaped surrogate pair id", """{"jsonrpc":"2.0","id":"\ud83d\ude00","method":"ping"}""", PairIdEcho),
                ("escaped surrogate pair id, upper hex", """{"jsonrpc":"2.0","id":"\uD83D\uDE00","method":"ping"}""", PairIdEcho),
                ("raw surrogate pair id", "{\"jsonrpc\":\"2.0\",\"id\":\"" + pair + "\",\"method\":\"ping\"}", PairIdEcho),
                ("ping with empty params", Request("40", "ping", "{}"), """{"jsonrpc":"2.0","id":40,"result":{}}"""),
                ("ping with a params member", Request("41", "ping", """{"x":1,"_meta":{"progressToken":1}}"""), """{"jsonrpc":"2.0","id":41,"result":{}}"""),
                ("ping with array params", Request("42", "ping", "[1]"), """{"jsonrpc":"2.0","id":42,"result":{}}"""),
                ("malformed JSON", """{"jsonrpc":"2.0","id":43,""", """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"parse_error"}}"""),
                ("raw U+FFFD in a member name", "{\"jsonrpc\":\"2.0\",\"id\":44,\"method\":\"ping\",\"\uFFFD\":1}", """{"jsonrpc":"2.0","id":44,"result":{}}"""),
            };
            var differing = new List<string>();
            foreach (var (label, line, expected) in exact)
            {
                string response;
                try { response = server.Handle(line) ?? "none"; }
                catch (Exception error) { response = "threw " + error.GetType().Name + ": " + error.Message; }
                if (response != expected) differing.Add(label + " => " + response);
            }
            Check(differing.Count == 0, "refusal, surrogate-pair id and well-formed response bytes are exact | " + string.Join(" ; ", differing));
            // P10-40: compared against literal bytes (initialize) and a separate server's plain tools/list, not this server's.
            var metaInit = server.Handle(Request("50", "initialize", "{" + Init + ",\"_meta\":{\"\\ud800\":1}}"));
            Check(metaInit == InitializeBytes("50"), "initialize with a lone surrogate inside _meta answers the literal initialize bytes | " + Short(metaInit ?? "none"));
            var cursorList = server.Handle(Request("51", "tools/list", """{"cursor":"x"}"""));
            Check(ToolsListMatches(cursorList, "51"), "tools/list with a valid params object answers the plain tools/list bytes of a separate server | " + Short(cursorList ?? "none"));
        }

        // ---- 4. the real Release Bim.Mcp.dll over stdio ----
        {
            var mcpDll = Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll");
            // P10-38: the dll is a named precondition; without it the process is never launched (one failure, not two).
            var dllExists = File.Exists(mcpDll);
            Check(dllExists, "precondition: the Release Bim.Mcp.dll exists, else the stdio process is not launched (" + mcpDll + ")");
            static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
            byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
            var wtf8Lone = new byte[] { 0xED, 0xA0, 0x80 };
            var utf8Pair = new byte[] { 0xF0, 0x9F, 0x98, 0x80 };
            var replaced = "\uFFFD\uFFFD\uFFFD";
            var lines = new List<(string Label, byte[] Line, string Expected)>
            {
                // P10-34 refusals (exact bytes pinned in RunP10_34)
                ("-32600 repeated member", Utf8("""{"jsonrpc":"2.0","id":6,"method":"ping","method":"tools/list"}"""),
                    """{"jsonrpc":"2.0","id":6,"error":{"code":-32600,"message":"invalid_request: repeated member method"}}"""),
                ("-32600 repeated id", Utf8("""{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}"""),
                    """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"invalid_request: repeated member id"}}"""),
                ("-32602 repeated params member", Utf8(Request("20", "tools/call", """{"name":"p10_33_unknown","name":"validate","arguments":{"project":{}}}""")),
                    """{"jsonrpc":"2.0","id":20,"error":{"code":-32602,"message":"invalid_params: repeated params member name"}}"""),
                ("-32600 member name not text", Utf8("""{"jsonrpc":"2.0","id":1,"method":"ping","\ud800":1}"""),
                    """{"jsonrpc":"2.0","id":1,"error":{"code":-32600,"message":"invalid_request: member name is not valid text"}}"""),
                ("-32600 method not text", Utf8("""{"jsonrpc":"2.0","id":"m","method":"\ud800"}"""),
                    """{"jsonrpc":"2.0","id":"m","error":{"code":-32600,"message":"invalid_request: method is not valid text"}}"""),
                ("-32602 params member name not text", Utf8(Request("21", "tools/call", """{"name":"validate","\ud800":1}""")),
                    """{"jsonrpc":"2.0","id":21,"error":{"code":-32602,"message":"invalid_params: params member name is not valid text"}}"""),
                ("-32602 name not text", Utf8(Request("22", "tools/call", """{"name":"\ud800"}""")),
                    """{"jsonrpc":"2.0","id":22,"error":{"code":-32602,"message":"invalid_params: name is not valid text"}}"""),
                // P10-36
                // P10-40: every line has its own id (the per-line comparison is keyed by id)
                ("-32602 ping params name not text", Utf8(Request("64", "ping", """{"\ud800":1}""")),
                    """{"jsonrpc":"2.0","id":64,"error":{"code":-32602,"message":"invalid_params: params member name is not valid text"}}"""),
                ("escaped surrogate pair id", Utf8("""{"jsonrpc":"2.0","id":"e\ud83d\ude00","method":"ping"}"""), """{"jsonrpc":"2.0","id":"e\uD83D\uDE00","result":{}}"""),
                ("raw UTF-8 surrogate pair id", Concat(Utf8("{\"jsonrpc\":\"2.0\",\"id\":\""), utf8Pair, Utf8("\",\"method\":\"ping\"}")), PairIdEcho),
                ("WTF-8 lone surrogate member name", Concat(Utf8("{\"jsonrpc\":\"2.0\",\"id\":60,\"method\":\"ping\",\""), wtf8Lone, Utf8("\":1}")), """{"jsonrpc":"2.0","id":60,"result":{}}"""),
                ("WTF-8 lone surrogate in method", Concat(Utf8("{\"jsonrpc\":\"2.0\",\"id\":61,\"method\":\"ping"), wtf8Lone, Utf8("\"}")),
                    """{"jsonrpc":"2.0","id":61,"error":{"code":-32601,"message":"method_not_found"}}"""),
                ("WTF-8 lone surrogate id", Concat(Utf8("{\"jsonrpc\":\"2.0\",\"id\":\""), wtf8Lone, Utf8("\",\"method\":\"ping\"}")),
                    "{\"jsonrpc\":\"2.0\",\"id\":\"" + replaced + "\",\"result\":{}}"),
                // P10-38
                ("-32602 ping repeated params member", Utf8(Request("63", "ping", """{"x":1,"\u0078":2}""")), P10_38PingRefusal),
                ("ping", Utf8("""{"jsonrpc":"2.0","id":62,"method":"ping"}"""), """{"jsonrpc":"2.0","id":62,"result":{}}"""),
            };
            var wtf8Id = IdKey(lines.Single(line => line.Label == "WTF-8 lone surrogate id").Expected);
            var expectedKeys = lines.Select(line => IdKey(line.Expected)).ToList();
            Check(expectedKeys.Distinct(StringComparer.Ordinal).Count() == lines.Count, "fixture: every stdio line has its own response id | " + string.Join(" ", expectedKeys));
            var stdin = lines.SelectMany(line => line.Line.Concat(new byte[] { (byte)'\n' })).ToArray();
            byte[] stdout = [];
            var stderr = "";
            var exit = -1;
            // P10-50: launched through Launch, the one launcher this file shares with the P10-48 Cli (async read, kill and
            // wait on timeout); stdin is written then closed at once.
            if (dllExists)
            {
                try
                {
                    var launched = Launch(mcpDll, stdin, 120000, 0, Array.Empty<string>());
                    if (launched.TimedOut) stderr = "<timed out after 120000 ms, killed (exited " + launched.KilledExited + ")> stderr " + BoundedStderr(launched.Stderr);
                    else { stdout = launched.Stdout; stderr = launched.Stderr; exit = launched.Exit; }
                }
                catch (Exception error) { stderr = "threw " + error.GetType().Name + ": " + error.Message; }
            }
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line.Expected + "\n")));
            // P10-40: each response line is matched to its request line by id, so a missing or extra line names that line only.
            var answer = System.Text.Encoding.UTF8.GetString(stdout);
            var got = answer.Split('\n').ToList();
            if (answer.EndsWith('\n')) got.RemoveAt(got.Count - 1);
            else if (got.Count > 0 && got[^1].Length == 0) got.RemoveAt(got.Count - 1);
            var byKey = got.GroupBy(IdKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            var differing = new List<string>();
            foreach (var (label, _, expected) in lines)
            {
                var key = IdKey(expected);
                if (!byKey.TryGetValue(key, out var answered)) differing.Add(label + " (id " + key + ") => <missing>");
                else if (answered.Count != 1 || answered[0] != expected) differing.Add(label + " (id " + key + ") => " + string.Join(" | ", answered));
            }
            foreach (var (key, answered) in byKey)
                if (!expectedKeys.Contains(key, StringComparer.Ordinal)) differing.Add("unexpected (id " + key + ") => " + string.Join(" | ", answered));
            if (dllExists)
            {
                Check(exit == 0 && differing.Count == 0,
                    "the real Bim.Mcp.dll over stdio answers each P10-34, P10-36 and P10-38 line byte for byte, matched by id (exit " + exit.ToString(CultureInfo.InvariantCulture) + ", "
                    + got.Count.ToString(CultureInfo.InvariantCulture) + " lines, expected " + lines.Count.ToString(CultureInfo.InvariantCulture) + ") | differing: "
                    + string.Join(" ; ", differing) + " | hex of the WTF-8 id line (id " + wtf8Id + "): " + (byKey.TryGetValue(wtf8Id, out var wtf8Lines) ? Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(wtf8Lines[0])) : "-") + " | stderr: " + Short(stderr));
                // P10-42: run only when every per-id line matched (a missing or wrong line already failed once above); then a
                // difference here is order or framing.
                if (differing.Count == 0)
                    Check(stdout.AsSpan().SequenceEqual(expectedBytes), "the stdio responses come in request order, one LF-terminated line each (" + stdout.Length.ToString(CultureInfo.InvariantCulture)
                        + " bytes, expected " + expectedBytes.Length.ToString(CultureInfo.InvariantCulture) + ") | " + Short(string.Join(" ", got.Select(IdKey))));
            }
        }

        // ---- 5. static ----
        {
            var source = DocUpdateLinkTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")));
            string Body(string start, string next)
            {
                var from = source.IndexOf(start, StringComparison.Ordinal);
                var to = from < 0 ? -1 : source.IndexOf(next, from, StringComparison.Ordinal);
                return from < 0 || to < 0 ? "" : source[from..to];
            }
            var handle = Body("public string? Handle(", "public static void Run(");
            var size = handle.IndexOf("request_too_large", StringComparison.Ordinal);
            var text = handle.IndexOf("line is not valid text", StringComparison.Ordinal);
            var parse = handle.IndexOf("JsonDocument.Parse(", StringComparison.Ordinal);
            Check(size > 0 && text > size && parse > text, "Handle checks the line as text after the size check and before parsing | "
                + string.Join(" ", new[] { size, text, parse }.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            var dispatch = Body("string? Dispatch(", "static JsonObject Initialize(");
            var notification = dispatch.IndexOf("if (!hasId) return null;", StringComparison.Ordinal);
            var paramsText = dispatch.IndexOf("params member name is not valid text", StringComparison.Ordinal);
            var switchAt = dispatch.IndexOf("switch (method)", StringComparison.Ordinal);
            Check(notification > 0 && paramsText > notification && switchAt > paramsText, "Dispatch checks other methods' params names as text after the notification return, before the method switch | "
                + string.Join(" ", new[] { notification, paramsText, switchAt }.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        }

        RunP10_38(root, check);
    }

    public const string P10_38PingRefusal = """{"jsonrpc":"2.0","id":63,"error":{"code":-32602,"message":"invalid_params: repeated params member x"}}""";

    // P10-38: a params member given more than once on initialize, ping or tools/list. Oracle, through McpServer.Handle with
    // a counting Revit transport:
    //   1. Measurement (the probe): each case's outcome is reported in the failure message (before the fix: ignored).
    //   2. After the fix: a repeated params member (also a \u-escaped spelling of the same name, also _meta) on those methods
    //      is -32602 "invalid_params: repeated params member <name>" with the id, as tools/call does (P10-33); a params
    //      member name that is not valid text keeps the P10-36 refusal (checked first). Pinned as measured with the reason:
    //      a notification's and an unknown method's params are never read (none / -32601); members repeated inside _meta
    //      (one level down) are never read, compared or echoed (answered); array params are not an object (answered).
    //   3. Exact bytes: one refusal per method and well-formed neighbours.
    //   4. Static: Dispatch checks repeated params members of the other methods after their text check, before the switch.
    static void RunP10_38(string root, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-38 repeated params members of other methods: " + name);
        var sends = 0;
        var server = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => { sends++; return null; }, null);
        static string Request(string id, string method, string parameters) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + parameters + "}";
        const string Init = "\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"p10-38\",\"version\":\"0\"}";
        static string Outcome(string? response)
        {
            if (response is null) return "none";
            var node = JsonNode.Parse(response)!.AsObject();
            var id = node["id"]?.ToJsonString() ?? "null";
            if (node["error"] is JsonObject error)
                return error["code"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture) + ":" + error["message"]!.GetValue<string>() + "@" + id;
            if (node["result"] is JsonObject) return "result@" + id;
            return "other:" + response;
        }
        string Safe(string line)
        {
            try { return Outcome(server.Handle(line)); }
            catch (Exception error) { return "threw " + error.GetType().Name + ": " + error.Message; }
        }
        static string Refused(string name, string id) => "-32602:" + ParamsPrefix + name + "@" + id;

        var cases = new List<(string Label, string Line, string Expected)>
        {
            ("P1 ping x twice", Request("1", "ping", """{"x":1,"x":2}"""), Refused("x", "1")),
            ("P2 ping x twice escaped", Request("2", "ping", """{"x":1,"\u0078":2}"""), Refused("x", "2")),
            ("P3 ping _meta twice", Request("\"p\"", "ping", """{"_meta":{},"_meta":{"progressToken":1}}"""), Refused("_meta", "\"p\"")),
            ("P4 initialize protocolVersion twice", Request("3", "initialize", "{" + Init + ",\"protocolVersion\":\"2025-06-18\"}"), Refused("protocolVersion", "3")),
            ("P5 initialize capabilities twice escaped", Request("4", "initialize", "{" + Init + ",\"\\u0063apabilities\":{}}"), Refused("capabilities", "4")),
            ("P6 initialize clientInfo twice first", Request("5", "initialize", "{\"clientInfo\":{}," + Init + "}"), Refused("clientInfo", "5")),
            ("P7 tools/list cursor twice", Request("6", "tools/list", """{"cursor":"a","cursor":"b"}"""), Refused("cursor", "6")),
            ("P8 tools/list cursor twice escaped", Request("\"l\"", "tools/list", """{"\u0063ursor":"a","cursor":"a"}"""), Refused("cursor", "\"l\"")),
            ("P9 tools/list two names repeated", Request("7", "tools/list", """{"a":1,"b":1,"b":2,"a":2}"""), Refused("b", "7")),
            ("P10 tools/list lone name repeated stays not-text", Request("8", "tools/list", """{"cursor":"x","cursor":"y","\ud800":1}"""), "-32602:" + ParamsNotText + "@8"),
            // pinned as measured: never read
            ("N1 notification params member twice", """{"jsonrpc":"2.0","method":"notifications/initialized","params":{"x":1,"x":2}}""", "none"),
            ("N2 unknown method params member twice", Request("9", "resources/list", """{"x":1,"x":2}"""), "-32601:method_not_found@9"),
            ("N3 ping repeated inside _meta", Request("10", "ping", """{"_meta":{"a":1,"a":2}}"""), "result@10"),
            ("N4 tools/list array params", Request("11", "tools/list", "[1,1]"), "result@11"),
            // unchanged: tools/call keeps its own check
            ("C1 tools/call name twice", Request("12", "tools/call", """{"name":"p10_38_unknown","name":"validate"}"""), Refused("name", "12")),
        };
        var measured = new List<string>();
        var bad = new List<string>();
        foreach (var (label, line, expected) in cases)
        {
            var outcome = Safe(line);
            measured.Add(label + " => " + outcome);
            if (outcome != expected) bad.Add(label + " (expected " + expected + ")");
        }
        Check(bad.Count == 0 && sends == 0,
            "a repeated params member on initialize/ping/tools/list is refused -32602 with the id like tools/call, no transport send (sends "
            + sends.ToString(CultureInfo.InvariantCulture) + ") | failing " + bad.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", bad)
            + " | measured: " + string.Join(" ; ", measured));

        // ---- 3. exact bytes ----
        {
            var exact = new List<(string Label, string Line, string? Expected)>
            {
                ("ping repeated escaped", Request("63", "ping", """{"x":1,"\u0078":2}"""), P10_38PingRefusal),
                ("initialize repeated", Request("\"i\"", "initialize", "{" + Init + ",\"protocolVersion\":\"x\"}"),
                    """{"jsonrpc":"2.0","id":"i","error":{"code":-32602,"message":"invalid_params: repeated params member protocolVersion"}}"""),
                ("tools/list repeated", Request("21", "tools/list", """{"cursor":"a","cursor":"a"}"""),
                    """{"jsonrpc":"2.0","id":21,"error":{"code":-32602,"message":"invalid_params: repeated params member cursor"}}"""),
                ("ping distinct members", Request("22", "ping", """{"x":1,"y":1,"_meta":{"progressToken":1}}"""), """{"jsonrpc":"2.0","id":22,"result":{}}"""),
                ("initialize distinct members", Request("20", "initialize", "{" + Init + "}"), InitializeBytes("20")),
            };
            var differing = new List<string>();
            foreach (var (label, line, expected) in exact)
            {
                string response;
                try { response = server.Handle(line) ?? "none"; }
                catch (Exception error) { response = "threw " + error.GetType().Name + ": " + error.Message; }
                if (expected is null || response != expected) differing.Add(label + " => " + response);
            }
            // P10-40: a separate server's plain tools/list in the literal envelope, not this server's own answer.
            var cursorList = server.Handle(Request("23", "tools/list", """{"cursor":"x","_meta":{}}"""));
            if (!ToolsListMatches(cursorList, "23")) differing.Add("tools/list distinct members differ from the plain tools/list of a separate server => " + (cursorList is { Length: > 300 } ? cursorList[..300] + "..." : cursorList ?? "none"));
            Check(differing.Count == 0, "refusal and well-formed response bytes are exact | " + string.Join(" ; ", differing));
        }

        // ---- 4. static ----
        {
            var source = DocUpdateLinkTests.CodeOf(File.ReadAllText(Path.Combine(root, "interfaces", "mcp", "McpServer.cs")));
            var from = source.IndexOf("string? Dispatch(", StringComparison.Ordinal);
            var to = from < 0 ? -1 : source.IndexOf("static JsonObject Initialize(", from, StringComparison.Ordinal);
            var dispatch = from < 0 || to < 0 ? "" : source[from..to];
            var text = dispatch.IndexOf("params member name is not valid text", StringComparison.Ordinal);
            var repeated = dispatch.IndexOf("repeated params member ", StringComparison.Ordinal);
            var switchAt = dispatch.IndexOf("switch (method)", StringComparison.Ordinal);
            Check(text > 0 && repeated > text && switchAt > repeated, "Dispatch checks other methods' repeated params members after their text check, before the method switch | "
                + string.Join(" ", new[] { text, repeated, switchAt }.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        }

        RunP10_40(root, check);
    }

    // P10-42: the server identity the initialize bytes carry. One source (McpServer.ServerName/ServerVersion), pinned to
    // these literals by one named check in RunP10_40, so a version bump fails that check alone, not every initialize case.
    public const string ExpectedServerName = "bim-agentic-os";
    public const string ExpectedServerVersion = "0.1.0";

    // P10-40 helpers. The literal initialize response for a request id (the JSON text of the id); name and version come
    // from McpServer (asserted against ExpectedServerName/ExpectedServerVersion, P10-42).
    public static string InitializeBytes(string id) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\""
        + McpServer.ServerName + "\",\"version\":\"" + McpServer.ServerVersion + "\"}}}";

    // The literal unknown_tool result for a request id (it carries no server name or version).
    public static string UnknownToolBytes(string id) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"{\\\"status\\\":\\\"failed\\\",\\\"code\\\":\\\"unknown_tool\\\"}\"}],\"isError\":true}}";

    // A tools/list response is the literal envelope around a non-empty tools array and equals the plain (no params)
    // tools/list of a separate, fresh server with the same catalogue and policy. The tool list itself is owned by the
    // listing tests; this proves the params were not read.
    static bool ToolsListMatches(string? response, string id)
    {
        var fresh = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => null, null);
        var plain = fresh.Handle("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/list\"}");
        var prefix = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[{\"name\":\"";
        return response is not null && response == plain && response.StartsWith(prefix, StringComparison.Ordinal) && response.EndsWith("}]}}", StringComparison.Ordinal);
    }

    // The JSON text of a response line's id ("null" for a null id), or a marker when the line has none or is not JSON.
    static string IdKey(string line)
    {
        try { return JsonNode.Parse(line) is JsonObject node && node.ContainsKey("id") ? node["id"]?.ToJsonString() ?? "null" : "<no id: " + line + ">"; }
        catch (Exception) { return "<not JSON: " + line + ">"; }
    }

    // P10-50: the one child-process launcher of this file, shared by the P10-36 stdio run and the P10-48 Cli. stdout is
    // read asynchronously as bytes and stderr as text, so a child that keeps a stream open cannot block a synchronous
    // read; stdin gets the given bytes, then is closed at once (releaseStdinMs 0) or after releaseStdinMs by a release
    // task that is cancelled when the process ends, so it never outlives the launch (Release: its status afterwards). On
    // timeout the process tree is killed and waited for (KilledExited); stdout is not read then. P10-52: stderr is kept
    // on timeout too (what the killed child wrote, waited for at most 5 s), so a timeout message can report it.
    // P10-54: environment, when given, is added to the child's environment (the stderr self-tests set COREHOST_TRACE=1
    // so the child writes host trace text to stderr; the parity and stdio runs pass none).
    internal readonly record struct Launched(byte[] Stdout, string Stderr, int Exit, bool TimedOut, bool KilledExited, TaskStatus Release);

    static Launched Launch(string dll, byte[] stdin, int timeoutMs, int releaseStdinMs, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new System.Diagnostics.ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (environment is not null) foreach (var (name, value) in environment) start.Environment[name] = value;
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("process did not start: " + dotnet);
        using var collected = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(collected);
        var errors = process.StandardError.ReadToEndAsync();
        var input = process.StandardInput.BaseStream;
        using var release = new CancellationTokenSource();
        var releasing = Task.CompletedTask;
        TaskStatus Ended()
        {
            release.Cancel();
            try { releasing.Wait(5000); } catch (AggregateException) { }
            return releasing.Status;
        }
        try
        {
            try { input.Write(stdin); input.Flush(); } catch (IOException) { }
            if (releaseStdinMs <= 0) CloseInput(input);
            else releasing = ReleaseAfter(input, releaseStdinMs, release.Token);
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                var exited = process.WaitForExit(10000);
                var written = "<stderr not read>";
                try { if (errors.Wait(5000)) written = errors.Result; } catch (AggregateException) { }
                return new Launched([], written, -1, true, exited, Ended());
            }
            process.WaitForExit();
            reading.Wait();
            return new Launched(collected.ToArray(), errors.Result, process.ExitCode, false, true, Ended());
        }
        finally { release.Cancel(); }
    }

    // P10-52: a child's stderr as reported in a measured entry or a timeout message, bounded (the head kept, the full
    // length stated).
    internal const int StderrBound = 600;
    internal static string BoundedStderr(string text) =>
        text.Length <= StderrBound ? text : text[..StderrBound] + "...(" + text.Length.ToString(CultureInfo.InvariantCulture) + " chars)";

    // P10-52: the process starts written in a source's code (comments removed by DocUpdateLinkTests.CodeOf; string
    // literals kept, so a self-test builds its snippets by concatenation): Process.Start( with or without the
    // System.Diagnostics (or global::) prefix, and new Process( or new Process { with or without it. P10-54: also a
    // target-typed new() (or new {) in a declaration of a Process variable (Process p = new(), nullable, using, with or
    // without the prefix). Not recognised (stated limit, also in the self-test label): a using alias, a using static
    // Start(, and a target-typed new() assigned to an untyped or earlier-declared variable.
    static readonly System.Text.RegularExpressions.Regex ProcessStartCall = new(@"\bProcess\s*\.\s*Start\s*\(");
    static readonly System.Text.RegularExpressions.Regex NewProcess = new(@"\bnew\s+(?:global::\s*)?(?:System\s*\.\s*Diagnostics\s*\.\s*)?Process\s*[({]");
    static readonly System.Text.RegularExpressions.Regex TargetTypedProcess = new(@"\bProcess\s*\??\s+[A-Za-z_@][A-Za-z0-9_]*\s*=\s*new\s*[({]");
    internal static int ProcessStarts(string source)
    {
        var code = DocUpdateLinkTests.CodeOf(source);
        return ProcessStartCall.Matches(code).Count + NewProcess.Matches(code).Count + TargetTypedProcess.Matches(code).Count;
    }

    static async Task ReleaseAfter(Stream input, int delayMs, CancellationToken token)
    {
        await Task.Delay(delayMs, token).ConfigureAwait(false);
        CloseInput(input);
    }

    static void CloseInput(Stream input)
    {
        try { input.Close(); } catch (Exception) { }
    }

    // P10-40: members repeated one level or more below the params of initialize (inside capabilities or clientInfo) and
    // inside _meta (initialize, ping, tools/list, tools/call). Oracle, through McpServer.Handle with a counting Revit
    // transport, every outcome compared to literal response bytes:
    //   1. Measurement (the probe): each case's response is reported in the failure message.
    //   2. Pinned as measured, by design: the server never reads, compares or echoes capabilities, clientInfo or _meta
    //      content (initialize answers a fixed result; ping, tools/list and an unknown tool ignore _meta), so a member
    //      repeated inside them is answered exactly as without the repeat; so is a lone-surrogate escape nested there. Only
    //      the params object's own members are checked (P10-36/P10-38): a repeat of capabilities itself stays -32602 even
    //      when a nested repeat is also present. A refusal here would reject requests whose meaning to this server is
    //      unambiguous. A notification's nested repeat gets no response.
    //   3. No case reaches the Revit transport.
    static void RunP10_40(string root, Action<bool, string> check)
    {
        void Check(bool condition, string name) => check(condition, "P10-40 nested repeated params members: " + name);
        // P10-42: the one source of the initialize bytes' server identity.
        Check(McpServer.ServerName == ExpectedServerName && McpServer.ServerVersion == ExpectedServerVersion,
            "server identity: McpServer.ServerName/ServerVersion are " + ExpectedServerName + " " + ExpectedServerVersion + " (InitializeBytes takes them from McpServer) | got "
            + McpServer.ServerName + " " + McpServer.ServerVersion);
        var sends = 0;
        var server = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => { sends++; return null; }, null);
        static string Request(string id, string method, string parameters) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + parameters + "}";
        static string Initialize(string capabilities, string clientInfo) =>
            "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":" + capabilities + ",\"clientInfo\":" + clientInfo + "}";
        const string Client = """{"name":"p10-40","version":"0"}""";
        static string Ping(string id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{}}";

        var cases = new List<(string Label, string Line, string Expected)>
        {
            // ---- initialize capabilities / clientInfo: answered (never read) ----
            ("I1 capabilities member twice", Request("1", "initialize", Initialize("""{"roots":{},"roots":{"listChanged":true}}""", Client)), InitializeBytes("1")),
            ("I2 capabilities member twice escaped", Request("2", "initialize", Initialize("""{"sampling":{},"\u0073ampling":{}}""", Client)), InitializeBytes("2")),
            ("I3 capabilities nested two levels", Request("3", "initialize", Initialize("""{"roots":{"listChanged":true,"listChanged":false}}""", Client)), InitializeBytes("3")),
            ("I4 clientInfo name twice", Request("\"c\"", "initialize", Initialize("{}", """{"name":"a","name":"b","version":"0"}""")), InitializeBytes("\"c\"")),
            ("I5 clientInfo version twice escaped", Request("4", "initialize", Initialize("{}", """{"name":"a","version":"0","\u0076ersion":"1"}""")), InitializeBytes("4")),
            ("I6 both nested repeats", Request("5", "initialize", Initialize("""{"x":1,"x":2}""", """{"name":"a","name":"a","version":"0"}""")), InitializeBytes("5")),
            ("I7 capabilities lone-surrogate name", Request("6", "initialize", Initialize("""{"\ud800":{}}""", Client)), InitializeBytes("6")),
            ("I8 initialize _meta member twice", Request("7", "initialize", Initialize("{}", Client)[..^1] + ""","_meta":{"progressToken":1,"progressToken":2}}"""), InitializeBytes("7")),
            // ---- _meta of the other methods: answered (never read) ----
            ("M1 ping _meta member twice escaped", Request("10", "ping", """{"_meta":{"progressToken":1,"\u0070rogressToken":2}}"""), Ping("10")),
            ("M2 ping _meta nested two levels", Request("11", "ping", """{"_meta":{"x":{"a":1,"a":2}}}"""), Ping("11")),
            ("M3 tools/call _meta member twice (unknown tool)", Request("12", "tools/call", """{"name":"p10_40_unknown","arguments":{},"_meta":{"progressToken":1,"progressToken":2}}"""), UnknownToolBytes("12")),
            ("M4 tools/call _meta nested two levels (unknown tool)", Request("\"t\"", "tools/call", """{"name":"p10_40_unknown","_meta":{"x":{"a":1,"\u0061":2}}}"""), UnknownToolBytes("\"t\"")),
            // ---- the params object's own members stay checked (P10-38) ----
            ("C1 capabilities twice beside a nested repeat", Request("20", "initialize", Initialize("""{"a":1,"a":2}""", Client)[..^1] + ""","capabilities":{}}"""),
                """{"jsonrpc":"2.0","id":20,"error":{"code":-32602,"message":"invalid_params: repeated params member capabilities"}}"""),
            ("C2 ping _meta twice, each with a nested repeat", Request("21", "ping", """{"_meta":{"a":1,"a":2},"_meta":{"b":1,"b":2}}"""),
                """{"jsonrpc":"2.0","id":21,"error":{"code":-32602,"message":"invalid_params: repeated params member _meta"}}"""),
            // ---- notification: never answered ----
            ("N1 notification with a nested repeat", """{"jsonrpc":"2.0","method":"notifications/initialized","params":{"_meta":{"a":1,"a":2}}}""", "none"),
        };
        // P10-46: each case counts its own transport sends (the counter before and after its Handle), so a stray send fails
        // the case that made it and nothing earlier counts; every case's response and send count is listed.
        List<string> RunNested(IEnumerable<(string Label, string Line, string Expected)> list, List<string> measured)
        {
            var failing = new List<string>();
            foreach (var (label, line, expected) in list)
            {
                var before = sends;
                string response;
                try { response = server.Handle(line) ?? "none"; }
                catch (Exception error) { response = "threw " + error.GetType().Name + ": " + error.Message; }
                var made = sends - before;
                var ok = response == expected && made == 0;
                measured.Add((ok ? "ok " : "FAIL ") + label + " (sends " + made.ToString(CultureInfo.InvariantCulture) + ") => " + (response.Length > 200 ? response[..200] + "..." : response));
                if (!ok) failing.Add(label);
            }
            return failing;
        }
        var measured = new List<string>();
        var bad = RunNested(cases, measured);
        Check(bad.Count == 0,
            "members repeated inside capabilities, clientInfo and _meta are answered exactly as without the repeat (never read), the params' own repeats stay -32602, no transport send by any case (own per-case count) | failing "
            + bad.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", bad) + " | measured: " + string.Join(" ; ", measured));

        // tools/list with a member repeated inside _meta: the plain tools/list bytes of a separate server (own send count).
        var listBefore = sends;
        var listed = server.Handle(Request("30", "tools/list", """{"cursor":"x","_meta":{"progressToken":1,"progressToken":2}}"""));
        var listSends = sends - listBefore;
        Check(ToolsListMatches(listed, "30") && listSends == 0, "tools/list with a member repeated inside _meta answers the plain tools/list bytes, no send (sends "
            + listSends.ToString(CultureInfo.InvariantCulture) + ") | " + (listed is { Length: > 300 } ? listed[..300] + "..." : listed ?? "none"));

        // P10-42: members repeated inside a tools/call arguments value (one level or more below the arguments object), for
        // the validate tool. The arguments object's own names are checked (P10-31: -32602 repeated argument); a value is
        // handed to the tool as raw JSON text and deserialized like the CLI reads its file (System.Text.Json, .NET 8).
        // P10-44: also diff (two Project values, same deserializer) and one Revit-forwarding tool (revit_query_elements: the
        // raw args/document values are written into the command envelope, which RevitCommandProtocol.Parse reads with its
        // own duplicate-member check), and a lone-surrogate member name nested in a value. These cases run on their own
        // server and count transport sends per case, so a stray send fails the case that made it (and nothing earlier
        // counts). Measured: every case's response and send count is listed in the check message (P10-46: ok and FAIL alike);
        // expected literal bytes.
        {
            const string Guid1 = "00000000-0000-0000-0000-000000000001";
            const string Guid2 = "00000000-0000-0000-0000-000000000002";
            const string Dangling = "\"00000000-0000-0000-0000-000000000009\"";
            static string Element(string dependencies, string provenance = """{"Source":"s","Approval":"specified"}""") =>
                "{\"Id\":\"" + Guid2 + "\",\"Kind\":\"plan\",\"GeometryMetres\":[],\"Dependencies\":" + dependencies + ",\"Provenance\":" + provenance + "}";
            static string Validate(string id, string project) => Request(id, "tools/call", "{\"name\":\"validate\",\"arguments\":{\"project\":" + project + "}}");
            static string Diff(string id, string before, string after) => Request(id, "tools/call", "{\"name\":\"diff\",\"arguments\":{\"before\":" + before + ",\"after\":" + after + "}}");
            static string ToolText(string id, string text, bool isError) =>
                "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal)
                + "\"}],\"isError\":" + (isError ? "true" : "false") + "}}";
            var issue = "[{\"Code\":\"missing_dependency\",\"Id\":\"" + Guid2 + "\",\"Message\":\"Dangling dependency\"}]";
            var head = "{\"Id\":\"" + Guid1 + "\",\"Revision\":0,";
            // P10-44 diff: the empty project and the project with one plain element, and their literal plans (SHA-256 of the
            // System.Text.Json serialization of each project, uppercase hex).
            var plain = Element("[]");
            var empty = head + "\"Elements\":[]}";
            var one = head + "\"Elements\":[" + plain + "]}";
            const string EmptyHash = "DD35600AC5E804616F05B812C16FDD8C8EF40B7E8531036AAA1A8375AC1284D2";
            const string OneHash = "C82909AA730F993429C007E3AC3D6816AE30C9E3FFAEBACD5584F8618F79C1D4";
            static string PlanText(string before, string after, string operations, string dirty) =>
                "{\"ProjectId\":\"" + Guid1 + "\",\"ExpectedRevision\":0,\"BeforeHash\":\"" + before + "\",\"AfterHash\":\"" + after + "\",\"Operations\":[" + operations + "],\"DirtyIds\":[" + dirty + "]}";
            // P10-44: the measured System.Text.Json 8 message for a member name it cannot read (a lone surrogate escape).
            static string NotConverted(string type, string path, int position) =>
                "{\"status\":\"failed\",\"code\":\"The JSON value could not be converted to Bim.Core." + type + ". Path: " + path + " | LineNumber: 0 | BytePositionInLine: "
                + position.ToString(CultureInfo.InvariantCulture) + ".\"}";
            var createPlan = PlanText(EmptyHash, OneHash, "{\"Action\":\"create\",\"Element\":" + plain + "}", "\"" + Guid2 + "\"");
            var samePlan = PlanText(OneHash, OneHash, "", "");
            var emptyPlan = PlanText(EmptyHash, EmptyHash, "", "");
            // P10-44 Revit-forwarding tool: a read-only query on an absent document path (never stat'ed), with a pipe name
            // the transport accepts; the transport answers nothing, so a forwarded call is no_response.
            var revit = RevitCommandCatalogue.Find("query.elements")!.ToolName;
            var absent = JsonSerializer.Serialize(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "p10-44-absent", "x.rvt")));
            string Revit(string id, string args, string? document = null) => Request(id, "tools/call", "{\"name\":\"" + revit + "\",\"arguments\":{\"pipe_name\":\"bim-agentic-os-2025-1\",\"operation_id\":\"p10-44\",\"document\":"
                + (document ?? "{\"path\":" + absent + ",\"unique_id\":\"00000000-0000-0000-0000-000000000000-00000001\"}") + ",\"args\":" + args + "}}");
            static string RevitFailure(string code, string? field, string? detail) =>
                "{\"status\":\"failed\",\"code\":\"" + code + "\",\"field\":" + (field is null ? "null" : "\"" + field + "\"") + ",\"detail\":" + (detail is null ? "null" : "\"" + detail + "\"")
                + ",\"created\":[],\"modified\":[],\"deleted\":[],\"readback\":null,\"receipt\":null,\"warnings\":[]}";

            var argumentSends = 0;
            var forwarded = new List<string>();
            var argumentServer = new McpServer(RevitCommandCatalogue.All, RevitCommandPolicy.None, (pipe, line) => { argumentSends++; forwarded.Add(line); return null; }, null);
            // Each case: its response must equal the literal bytes and it must make exactly its own expected number of sends.
            List<string> RunCases(IEnumerable<(string Label, string Line, string Expected, int Sends)> list, List<string> measured)
            {
                var failing = new List<string>();
                foreach (var (label, line, expected, expectedSends) in list)
                {
                    var before = argumentSends;
                    string response;
                    try { response = argumentServer.Handle(line) ?? "none"; }
                    catch (Exception error) { response = "threw " + error.GetType().Name + ": " + error.Message; }
                    var made = argumentSends - before;
                    // P10-46: every case is listed (ok or FAIL), not only the failing ones.
                    var ok = response == expected && made == expectedSends;
                    measured.Add((ok ? "ok " : "FAIL ") + label + " (sends " + made.ToString(CultureInfo.InvariantCulture) + ") => " + (response.Length > 400 ? response[..400] + "..." : response));
                    if (!ok) failing.Add(label);
                }
                return failing;
            }

            // P10-44: the last-wins pins below hold for System.Text.Json 8 on .NET 8 (JsonDocument keeps every repeated
            // member, JsonElement lookups and JsonSerializer take the last). A target or package move that changes it fails
            // this one named check first.
            var stj = typeof(JsonSerializer).Assembly.GetName().Version;
            var lastWins = JsonSerializer.Deserialize<Provenance>("""{"Source":"a","Source":"b","Approval":"specified"}""")?.Source;
            Check(Environment.Version.Major == 8 && stj?.Major == 8 && lastWins == "b",
                "runtime dependence: the arguments last-wins pins need System.Text.Json 8 on .NET 8 (a repeated member deserializes last-wins) | runtime "
                + Environment.Version + ", System.Text.Json " + stj + ", repeated Source read as " + (lastWins ?? "null"));

            // P10-44: own-counter self-test. A send made before the cases fails none of them; a case that sends when it
            // expects none fails alone (the next case still passes).
            argumentSends++;
            var selfMeasured = new List<string>();
            var selfFailing = RunCases(new (string, string, string, int)[]
            {
                ("S1 planted send", Revit("90", """{"category":"OST_Walls"}"""), ToolText("90", RevitFailure("no_response", "pipe_name", "pipe closed without a response line"), true), 0),
                ("S2 after it", Validate("91", empty), ToolText("91", "[]", false), 0),
            }, selfMeasured);
            Check(selfFailing.Count == 1 && selfFailing[0] == "S1 planted send",
                "own send counter: a send before the arguments cases fails none, a planted send fails its own case alone | failing: " + string.Join(", ", selfFailing) + " | " + string.Join(" ; ", selfMeasured));

            var argumentCases = new List<(string Label, string Line, string Expected, int Sends)>
            {
                // ---- validate (P10-42): neighbours without a repeat, then repeats inside the project value ----
                ("A0 no repeat, dangling dependency", Validate("40", head + "\"Elements\":[" + Element("[" + Dangling + "]") + "]}"), ToolText("40", issue, true), 0),
                ("A0b no repeat, no element", Validate("41", empty), ToolText("41", "[]", false), 0),
                ("A1 Elements twice, last dangling", Validate("42", head + "\"Elements\":[],\"Elements\":[" + Element("[" + Dangling + "]") + "]}"), ToolText("42", issue, true), 0),
                ("A2 Elements twice, first dangling", Validate("43", head + "\"Elements\":[" + Element("[" + Dangling + "]") + "],\"Elements\":[]}"), ToolText("43", "[]", false), 0),
                ("A3 Dependencies twice in an element, last dangling", Validate("44", head + "\"Elements\":[" + Element("[],\"Dependencies\":[" + Dangling + "]") + "]}"), ToolText("44", issue, true), 0),
                ("A4 Dependencies twice escaped, first dangling", Validate("45", head + "\"Elements\":[" + Element("[" + Dangling + "],\"\\u0044ependencies\":[]") + "]}"), ToolText("45", "[]", false), 0),
                ("A5 Provenance Source twice (three levels down)", Validate("46", head + "\"Elements\":[" + Element("[]", """{"Source":"a","Source":"b","Approval":"specified"}""") + "]}"), ToolText("46", "[]", false), 0),
                // ---- diff (P10-44): neighbours, then repeats inside before/after ----
                ("D0 no repeat, create", Diff("50", empty, one), ToolText("50", createPlan, false), 0),
                ("D0b no repeat, same", Diff("51", one, one), ToolText("51", samePlan, false), 0),
                ("D0c no repeat, both empty", Diff("52", empty, empty), ToolText("52", emptyPlan, false), 0),
                ("D1 before Elements twice, last empty", Diff("53", head + "\"Elements\":[" + plain + "],\"Elements\":[]}", one), ToolText("53", createPlan, false), 0),
                ("D2 before Elements twice, first empty", Diff("54", head + "\"Elements\":[],\"Elements\":[" + plain + "]}", one), ToolText("54", samePlan, false), 0),
                ("D3 after Elements twice escaped, last empty", Diff("55", empty, head + "\"Elements\":[" + plain + "],\"\\u0045lements\":[]}"), ToolText("55", emptyPlan, false), 0),
                ("D4 before Revision twice, last 0", Diff("56", "{\"Id\":\"" + Guid1 + "\",\"Revision\":1,\"Revision\":0,\"Elements\":[]}", empty), ToolText("56", emptyPlan, false), 0),
                ("D5 before Revision twice, last 1", Diff("57", "{\"Id\":\"" + Guid1 + "\",\"Revision\":0,\"Revision\":1,\"Elements\":[]}", empty), ToolText("57", "{\"status\":\"failed\",\"code\":\"revision_conflict\"}", true), 0),
                ("D6 after Provenance Source twice (three levels down), last as before", Diff("58", one, head + "\"Elements\":[" + Element("[]", """{"Source":"a","Source":"s","Approval":"specified"}""") + "]}"), ToolText("58", samePlan, false), 0),
                // ---- revit_query_elements (P10-44): the raw value goes into the command envelope ----
                ("R0 no repeat, forwarded once", Revit("60", """{"category":"OST_Walls"}"""), ToolText("60", RevitFailure("no_response", "pipe_name", "pipe closed without a response line"), true), 1),
                ("R1 args member twice", Revit("61", """{"category":"OST_Walls","category":"OST_Doors"}"""), ToolText("61", RevitFailure("args_invalid", "args.category", "duplicate member"), true), 0),
                ("R2 args member twice escaped", Revit("62", """{"category":"OST_Walls","\u0063ategory":"OST_Walls"}"""), ToolText("62", RevitFailure("args_invalid", "args.category", "duplicate member"), true), 0),
                ("R3 document path twice", Revit("63", """{"category":"OST_Walls"}""", "{\"path\":" + absent + ",\"path\":" + absent + ",\"unique_id\":\"00000000-0000-0000-0000-000000000000-00000001\"}"),
                    ToolText("63", RevitFailure("envelope_invalid", "document.path", "unexpected or duplicate member"), true), 0),
                // ---- a lone-surrogate member name nested in an arguments value (P10-44), pinned as measured: the deserializer
                // cannot read the name, so its JsonException message becomes the failed code (McpServer generic catch);
                // the Revit command envelope cannot be built or read with it, so RevitCall answers envelope_invalid with the reader message.
                // P10-46: these expected bytes carry .NET 8 / System.Text.Json 8 runtime message text (wording and byte position),
                // so each label names that dependence; L1-L3 are also compared with the CLI's answer below ----
                ("L1 validate project lone-surrogate name (System.Text.Json 8 runtime message as code)", Validate("70", head + "\"Elements\":[],\"\\ud800\":1}"), ToolText("70", NotConverted("Project", "$", 81), true), 0),
                ("L2 validate element lone-surrogate name (System.Text.Json 8 runtime message as code)", Validate("71", head + "\"Elements\":[" + plain[..^1] + ",\"\\udc00\":1}]}"), ToolText("71", NotConverted("Element", "$.Elements[0]", 227), true), 0),
                ("L3 diff after lone-surrogate name (System.Text.Json 8 runtime message as code)", Diff("72", empty, head + "\"Elements\":[],\"\\ud800\":1}"), ToolText("72", NotConverted("Project", "$", 81), true), 0),
                ("L4 revit args lone-surrogate name (System.Text.Json 8 runtime message as detail)", Revit("73", """{"category":"OST_Walls","\ud800":1}"""),
                    ToolText("73", RevitFailure("envelope_invalid", null, "Cannot read incomplete UTF-16 JSON text as string with missing low surrogate."), true), 0),
            };
            forwarded.Clear();
            var argumentMeasured = new List<string>();
            var argumentBad = RunCases(argumentCases, argumentMeasured);
            Check(argumentBad.Count == 0,
                "members repeated inside a validate/diff arguments value answer last-wins as System.Text.Json 8 on .NET 8 reads them, revit_query_elements refuses a repeated args/document member typed, a nested lone-surrogate name answers the measured System.Text.Json 8 runtime message (L1-L4) (literal bytes, own per-case send count) | failing "
                + argumentBad.Count.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", argumentBad) + " | measured: " + string.Join(" ; ", argumentMeasured));
            // P10-46: R0 forwarded exactly one line, compared in full: the add-in request shape (DocumentPath, Operation,
            // UniqueId, Metres) and the canonical command (sorted members, default escaping) with the raw args value; the
            // temp document path (machine-dependent) is substituted as its JSON string (default escaping, as the writer).
            var forwardLine = """{"DocumentPath":<path>,"Operation":"revit.command","UniqueId":"00000000-0000-0000-0000-000000000000-00000001","Metres":[],"Command":{"args":{"category":"OST_Walls"},"command":"query.elements","document":{"path":<path>,"unique_id":"00000000-0000-0000-0000-000000000000-00000001"},"operation_id":"p10-44","protocol_version":1}}"""
                .Replace("<path>", absent, StringComparison.Ordinal);
            Check(forwarded.Count == 1 && forwarded[0] == forwardLine,
                "revit_query_elements R0 forwards exactly the full wire line (temp DocumentPath substituted) | forwarded " + forwarded.Count.ToString(CultureInfo.InvariantCulture)
                + ": " + string.Join(" ; ", forwarded) + " | expected: " + forwardLine);

            // P10-46: the nested lone-surrogate answers on validate/diff (a System.Text.Json message through the McpServer
            // generic catch) compared with the real Release CLI on the same project text in files: the MCP tool text equals
            // the CLI stdout line and isError equals a non-zero exit. Equal, so the code is shared CLI parity (a P10-43-class
            // runtime message as code on both surfaces), recorded in the contract, not changed here.
            // P10-48: L4 (revit_query_elements) is compared too, with the CLI revit-command path on the same command envelope
            // in a file (MCP text == CLI stdout, isError == non-zero exit, envelope_invalid). The repository root is the one
            // tests/mcp/Program.cs FindRoot walk, passed in as root (no second walk in this file).
            var cliDll = Path.Combine(root, "interfaces", "cli", "bin", "Release", "net8.0", "Bim.Cli.dll");
            var launches = 0;
            // P10-48: Cli enforces its timeout. P10-50: through Launch (shared with the P10-36 stdio run): stdout is read
            // asynchronously, WaitForExit(timeout) bounds the wait, and on timeout the process tree is killed and waited for
            // before TimeoutException (its message says whether the killed process exited and the stdin release task's
            // status). stdin is closed after releaseStdinMs (0: at once; the timeout self-test holds it open so the child
            // never exits), and the release task is cancelled when the process ends.
            // P10-52: Cli also returns the child's stderr and the release task's status (a self-test checks it after a
            // normal exit), and the timeout message ends with the killed child's stderr, bounded (BoundedStderr).
            // P10-54: Traced runs the child with COREHOST_TRACE=1 (host trace text on its stderr) for the stderr self-tests.
            var traced = new Dictionary<string, string> { ["COREHOST_TRACE"] = "1" };
            IReadOnlyDictionary<string, string>? cliEnvironment = null;
            (string Stdout, int Exit, string Stderr, TaskStatus Release) Cli(string dll, int timeoutMs, int releaseStdinMs, params string[] arguments)
            {
                launches++;
                var launched = Launch(dll, [], timeoutMs, releaseStdinMs, arguments, cliEnvironment);
                if (launched.TimedOut)
                    throw new TimeoutException("CLI timed out after " + timeoutMs.ToString(CultureInfo.InvariantCulture) + " ms, killed (exited " + launched.KilledExited
                        + "), stdin release task " + launched.Release + ": " + string.Join(" ", arguments) + " | stderr " + BoundedStderr(launched.Stderr));
                using var reader = new StreamReader(new MemoryStream(launched.Stdout), new System.Text.UTF8Encoding(false));
                return (reader.ReadToEnd().TrimEnd('\r', '\n'), launched.Exit, launched.Stderr, launched.Release);
            }
            var surrogateProject = head + "\"Elements\":[],\"\\ud800\":1}";
            var surrogateElement = head + "\"Elements\":[" + plain[..^1] + ",\"\\udc00\":1}]}";
            const string SurrogateArgs = """{"category":"OST_Walls","\ud800":1}""";
            var surrogateEnvelope = "{\"protocol_version\":1,\"operation_id\":\"p10-44\",\"document\":{\"path\":" + absent
                + ",\"unique_id\":\"00000000-0000-0000-0000-000000000000-00000001\"},\"command\":\"query.elements\",\"args\":" + SurrogateArgs + "}";
            const string ConvertedPrefix = "{\"status\":\"failed\",\"code\":\"The JSON value could not be converted to Bim.Core.";
            const string EnvelopeInvalidPrefix = "{\"status\":\"failed\",\"code\":\"envelope_invalid\",";
            // P10-50: the CLI side of L4 depends on the repository policy files: revit-command reads
            // RevitCommandPolicySource (.bim/revit-enabled-roots.txt, config/revit-content-libraries.txt of the CLI binary's
            // repository) before reading or parsing the envelope, so a malformed owner file answers allowlist_invalid there,
            // not envelope_invalid (the MCP side here runs with RevitCommandPolicy.None). The label names it.
            // P10-52: L4 is isolated from this repository's owner files: its CLI runs from a copy of the Release CLI folder
            // under a temporary repository root (a global.json of its own, so RepositoryRoot stops there; no .bim or config
            // files unless a self-test plants a malformed .bim/revit-enabled-roots.txt, which then answers
            // allowlist_invalid). The L4 entry lists which root it ran from.
            const string L4ParityLabel = "L4 (CLI revit-command reads .bim/revit-enabled-roots.txt through RevitCommandPolicySource before parsing the envelope; run from a temporary repository root)";
            static bool L4Named(string? label) => label is not null && label.StartsWith("L4 (", StringComparison.Ordinal)
                && label.Contains(".bim/revit-enabled-roots.txt", StringComparison.Ordinal) && label.Contains("RevitCommandPolicySource", StringComparison.Ordinal);
            // P10-52: the measured CLI part of a parity entry. A failed case, or any child that wrote to stderr, carries its
            // stderr, bounded; a passing case with empty stderr reads as before.
            static string CliPart(string stdout, int exit, string stderr, bool failed) =>
                "cli " + stdout + " exit " + exit.ToString(CultureInfo.InvariantCulture) + (failed || stderr.Length > 0 ? " stderr " + BoundedStderr(stderr) : "");
            // P10-52: a copy of the CLI folder under a temporary repository root inside work; policyText, when given, is
            // written as that root's .bim/revit-enabled-roots.txt.
            static string IsolatedCli(string dll, string work, string? policyText)
            {
                var isolatedRoot = Path.Combine(work, "repo");
                var folder = Path.Combine(isolatedRoot, "cli");
                var source = Path.GetDirectoryName(dll)!;
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(folder, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, true);
                }
                File.WriteAllText(Path.Combine(isolatedRoot, "global.json"), "{}");
                if (policyText is not null)
                {
                    Directory.CreateDirectory(Path.Combine(isolatedRoot, ".bim"));
                    File.WriteAllText(Path.Combine(isolatedRoot, ".bim", "revit-enabled-roots.txt"), policyText);
                }
                return Path.Combine(folder, Path.GetFileName(dll));
            }
            // P10-48: one parity run against a given CLI dll. The dll is a named precondition: without it no CLI process is
            // launched and no parity check is made (one failure). Each case's Handle runs under its own send count (the
            // counter before and after), so a stray send fails that case alone; every case is listed with its send count.
            // P10-50: only, when given, runs just the named cases (the planted-send self-test needs only L2).
            // P10-52: Labels are the labels of the case table itself (returned also when the dll is missing; no launch), so
            // the L4 label self-test reads the label the L4 case actually uses; l4Label replaces it for the mutant
            // self-test only. policyText is planted in L4's temporary repository root (malformed-file self-test). A CLI
            // timeout is a failing entry carrying the timeout message (with the child's stderr), not a crash of the run.
            (List<string> Failing, List<string> Measured, int Launches, List<string> Labels, List<string> Stderrs) Parity(string dll, Func<string, string> handle, Action<bool, string> sink,
                string[]? only = null, string? l4Label = null, string? policyText = null, bool trace = false)
            {
                cliEnvironment = trace ? traced : null;
                var failing = new List<string>();
                var measured = new List<string>();
                var stderrs = new List<string>();
                var launchesBefore = launches;
                var cliWork = Path.Combine(Path.GetTempPath(), "p10-46-cli-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(cliWork);
                try
                {
                    string Input(string name, string content) { var path = Path.Combine(cliWork, name); File.WriteAllText(path, content); return path; }
                    var parity = new (string Label, string Line, string[] Arguments, string Prefix, bool Isolated)[]
                    {
                        ("L1", Validate("80", surrogateProject), new[] { "validate", Input("l1.json", surrogateProject) }, ConvertedPrefix, false),
                        ("L2", Validate("81", surrogateElement), new[] { "validate", Input("l2.json", surrogateElement) }, ConvertedPrefix, false),
                        ("L3", Diff("82", empty, surrogateProject), new[] { "diff", Input("l3-before.json", empty), Input("l3-after.json", surrogateProject) }, ConvertedPrefix, false),
                        (l4Label ?? L4ParityLabel, Revit("83", SurrogateArgs), new[] { "revit-command", Input("l4-envelope.json", surrogateEnvelope), "bim-agentic-os-2025-1" }, EnvelopeInvalidPrefix, true),
                    };
                    var labels = parity.Select(entry => entry.Label).ToList();
                    var exists = File.Exists(dll);
                    sink(exists, "CLI parity precondition: the Release Bim.Cli.dll exists, else no CLI process is launched | " + dll);
                    if (!exists) return (failing, measured, 0, labels, stderrs);
                    foreach (var (label, line, arguments, prefix, isolated) in parity)
                    {
                        if (only is not null && !only.Contains(label, StringComparer.Ordinal)) continue;
                        var before = argumentSends;
                        string response;
                        try { response = handle(line); }
                        catch (Exception error) { response = "threw " + error.GetType().Name + ": " + error.Message; }
                        var made = argumentSends - before;
                        var copyClock = System.Diagnostics.Stopwatch.StartNew();
                        var caseDll = isolated ? IsolatedCli(dll, cliWork, policyText) : dll;
                        var copyMs = copyClock.ElapsedMilliseconds;
                        string stdout, stderr;
                        int exit;
                        try { (stdout, exit, stderr, _) = Cli(caseDll, 120000, 0, arguments); }
                        catch (TimeoutException error) { (stdout, exit, stderr) = ("<" + error.Message + ">", -1, ""); }
                        stderrs.Add(stderr);
                        string? text = null;
                        bool? isError = null;
                        try
                        {
                            var result = JsonNode.Parse(response)?["result"];
                            text = result?["content"]?[0]?["text"]?.GetValue<string>();
                            isError = result?["isError"]?.GetValue<bool>();
                        }
                        catch (Exception error) when (error is JsonException or InvalidOperationException) { }
                        var ok = text == stdout && isError == (exit != 0) && stdout.StartsWith(prefix, StringComparison.Ordinal) && made == 0;
                        measured.Add((ok ? "ok " : "FAIL ") + label + " (sends " + made.ToString(CultureInfo.InvariantCulture) + ") mcp " + (text ?? response) + " isError " + (isError?.ToString() ?? "?")
                            + " | " + CliPart(stdout, exit, stderr, !ok)
                            + (isolated ? " | temporary repository root (copied in " + copyMs.ToString(CultureInfo.InvariantCulture) + " ms), .bim/revit-enabled-roots.txt " + (policyText is null ? "absent" : "planted") : ""));
                        if (!ok) failing.Add(label);
                    }
                    sink(failing.Count == 0,
                        "CLI parity: a nested lone-surrogate name on validate/diff answers the same System.Text.Json 8 message as the CLI, and on revit_query_elements (L4) the same envelope_invalid as CLI revit-command on the same envelope (MCP text == CLI stdout, isError == non-zero exit, no transport send per case; shared P10-43-class runtime-message text) | failing "
                        + string.Join(", ", failing) + " | " + string.Join(" ; ", measured));
                    return (failing, measured, launches - launchesBefore, labels, stderrs);
                }
                finally { cliEnvironment = null; try { Directory.Delete(cliWork, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            string Handled(string line) => argumentServer.Handle(line) ?? "none";
            var full = Parity(cliDll, Handled, Check);

            // P10-48 self-tests of the parity run and Cli (collected, then one named check listing each):
            //   a. a missing dll: one sink call (the failed precondition), no CLI launch, no case run;
            //   b. a send planted in L2's Handle: L2 fails, listed with (sends 1), the parity sink fails. P10-50: only L2 is
            //      run (one CLI launch instead of the full run's four; the saved count is listed);
            //   c. a child that never exits on its own (the Release Bim.Mcp.dll with stdin held open): Cli throws
            //      TimeoutException near its timeout, well before stdin is released, the killed process exited, and the
            //      stdin release task ended with it (cancelled, no stray 30 s task). P10-50: the dll is that self-test's own
            //      named precondition (a missing dll is one precondition failure, nothing launched), self-tested too;
            //   d. P10-50: one process launcher in this file (Launch, shared by the P10-36 stdio run and Cli), and the L4 parity
            //      label names its .bim policy-file dependence;
            //   e. P10-52: the start count's forms (snippets; P10-54: target-typed into a declared Process variable too), the
            //      L4 label read from the case table (a plain L4 mutant reds), bounded stderr, a failed launch's stderr in
            //      the measured part, the stdin release task cancelled after a normal exit, and L4 isolated from a malformed
            //      policy file planted in its temporary repository root (P10-54: the copy time is listed in the L4 entry);
            //   f. P10-54: b and c assert the captured stderr text (host trace under COREHOST_TRACE=1), not only the marker.
            var selfTest = new List<string>();
            var missingSinks = new List<(bool Ok, string Name)>();
            var missingLaunchesBefore = launches;
            var missing = Parity(Path.Combine(root, "p10-48-absent", "Bim.Cli.dll"), Handled, (ok, name) => missingSinks.Add((ok, name)));
            var missingOk = missingSinks.Count == 1 && !missingSinks[0].Ok && missingSinks[0].Name.StartsWith("CLI parity precondition:", StringComparison.Ordinal)
                && missing.Launches == 0 && launches == missingLaunchesBefore && missing.Measured.Count == 0;
            selfTest.Add((missingOk ? "ok" : "FAIL") + " a missing dll (sinks " + string.Join(", ", missingSinks.Select(sink => (sink.Ok ? "pass " : "fail ") + sink.Name[..Math.Min(40, sink.Name.Length)]))
                + "; launches " + (launches - missingLaunchesBefore).ToString(CultureInfo.InvariantCulture) + "; cases " + missing.Measured.Count.ToString(CultureInfo.InvariantCulture) + ")");
            var plantedSinks = new List<(bool Ok, string Name)>();
            var onlyL2 = new[] { "L2" };
            var planted = Parity(cliDll, line =>
            {
                if (line.Contains("\"id\":81,", StringComparison.Ordinal)) argumentSends++;
                return Handled(line);
            }, (ok, name) => plantedSinks.Add((ok, name)), onlyL2, trace: true);
            // P10-52: the failing entry carries the CLI stderr part (CliPart, bounded). P10-54: the captured stderr text
            // itself, not only the marker: the planted run's child writes host trace to stderr (COREHOST_TRACE), so the
            // entry must carry that non-empty text bounded; an empty capture reds.
            var plantedErr = planted.Stderrs.Count == 1 ? planted.Stderrs[0] : "";
            var plantedOk = plantedSinks.Count == 2 && plantedSinks[0].Ok && !plantedSinks[1].Ok && planted.Failing.Count == 1 && planted.Failing[0] == "L2"
                && planted.Measured.Count == 1 && planted.Measured[0].StartsWith("FAIL L2 (sends 1) ", StringComparison.Ordinal)
                && plantedErr.Contains("Tracing enabled", StringComparison.Ordinal) && planted.Measured[0].EndsWith(" stderr " + BoundedStderr(plantedErr), StringComparison.Ordinal)
                && planted.Launches == 1;
            selfTest.Add((plantedOk ? "ok" : "FAIL") + " a send planted in L2 (failing " + string.Join(", ", planted.Failing) + "; " + string.Join(" ; ", planted.Measured.Select(entry => entry[..Math.Min(30, entry.Length)]))
                + "; CLI launches " + planted.Launches.ToString(CultureInfo.InvariantCulture) + " of the full run's " + full.Launches.ToString(CultureInfo.InvariantCulture) + ", "
                + (full.Launches - planted.Launches).ToString(CultureInfo.InvariantCulture) + " saved; captured stderr " + plantedErr.Length.ToString(CultureInfo.InvariantCulture) + " chars)");

            // c. the timeout self-test, its dll a named precondition.
            (bool Ran, bool Ok, string Entry) TimeoutSelfTest(string dll, Action<bool, string> sink)
            {
                var exists = File.Exists(dll);
                sink(exists, "Cli timeout self-test precondition: the Release Bim.Mcp.dll exists (the child that never exits while stdin is open), else it is not launched | " + dll);
                if (!exists) return (false, true, "not run: no " + dll);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string timeout;
                cliEnvironment = traced;
                try { var (stdout, exit, _, _) = Cli(dll, 3000, 30000); timeout = "returned exit " + exit.ToString(CultureInfo.InvariantCulture) + " stdout " + stdout; }
                catch (TimeoutException error) { timeout = error.Message; }
                finally { cliEnvironment = null; }
                var waited = clock.ElapsedMilliseconds;
                // P10-52: the timeout message ends with the killed child's stderr (bounded). P10-54: the text itself: the child
                // writes host trace to stderr (COREHOST_TRACE) before it blocks on stdin, so the part after the marker is that
                // captured text; an empty capture reds.
                var marker = timeout.IndexOf(" | stderr ", StringComparison.Ordinal);
                var killedErr = marker < 0 ? "" : timeout[(marker + " | stderr ".Length)..];
                var ok = timeout.StartsWith("CLI timed out after 3000 ms, killed (exited True), stdin release task Canceled:", StringComparison.Ordinal)
                    && killedErr.Contains("Tracing enabled", StringComparison.Ordinal) && waited < 20000;
                return (true, ok, timeout + "; " + waited.ToString(CultureInfo.InvariantCulture) + " ms");
            }
            var timeoutSinks = new List<(bool Ok, string Name)>();
            var timeoutLaunchesBefore = launches;
            var absentTimeout = TimeoutSelfTest(Path.Combine(root, "p10-50-absent", "Bim.Mcp.dll"), (ok, name) => timeoutSinks.Add((ok, name)));
            var absentTimeoutOk = timeoutSinks.Count == 1 && !timeoutSinks[0].Ok && timeoutSinks[0].Name.StartsWith("Cli timeout self-test precondition:", StringComparison.Ordinal)
                && !absentTimeout.Ran && launches == timeoutLaunchesBefore;
            selfTest.Add((absentTimeoutOk ? "ok" : "FAIL") + " a missing timeout-child dll (sinks " + string.Join(", ", timeoutSinks.Select(sink => (sink.Ok ? "pass " : "fail ") + sink.Name[..Math.Min(40, sink.Name.Length)]))
                + "; launches " + (launches - timeoutLaunchesBefore).ToString(CultureInfo.InvariantCulture) + "; " + absentTimeout.Entry + ")");
            var mcpDll = Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll");
            var timeoutRun = TimeoutSelfTest(mcpDll, Check);
            selfTest.Add((timeoutRun.Ok ? "ok" : "FAIL") + " a child that never exits (" + timeoutRun.Entry + ")");

            // d. static: one launcher, the L4 label. P10-52: the file is read once (ownSource, also used by the root-walk
            // check below); the start count recognises Process.Start( and new Process( with or without the namespace
            // (ProcessStarts, self-tested on snippets built by concatenation so this file's own strings do not count; a
            // message string spelling either form would count, so the messages here name them in words).
            var ownSource = File.ReadAllText(Path.Combine(root, "tests", "mcp", "RepeatedEnvelopeKeyTests.cs"));
            var starts = ProcessStarts(ownSource);
            var oneLauncher = starts == 1;
            selfTest.Add((oneLauncher ? "ok" : "FAIL") + " one process launcher (Launch) in this file (process starts " + starts.ToString(CultureInfo.InvariantCulture) + ")");
            var startForms = new (string Snippet, int Expected)[]
            {
                ("using var p = System.Diagnostics.Process" + ".Start(info);", 1),
                ("using var p = Process" + ".Start(info);", 1),
                ("using var p = Process" + " . Start (info);", 1),
                ("var p = new " + "Process();", 1),
                ("var p = new " + "Process { StartInfo = info }; p.Start();", 1),
                ("var p = new " + "System.Diagnostics.Process();", 1),
                ("var p = new " + "global::System.Diagnostics.Process();", 1),
                ("Process p = new" + "();", 1),
                ("using Process p = new" + "() { StartInfo = info };", 1),
                ("System.Diagnostics.Process? p = new" + " ();", 1),
                ("global::System.Diagnostics.Process p = new" + "();", 1),
                ("ProcessStartInfo info = new" + "(\"dotnet\");", 0),
                ("var info = new " + "ProcessStartInfo(\"dotnet\"); var q = new " + "System.Diagnostics.ProcessStartInfo();", 0),
                ("// Process" + ".Start(info); new " + "Process();\nvar watch = System.Diagnostics.Stopwatch.StartNew();", 0),
            };
            var formsWrong = startForms.Where(form => ProcessStarts(form.Snippet) != form.Expected)
                .Select(form => form.Snippet + " => " + ProcessStarts(form.Snippet).ToString(CultureInfo.InvariantCulture) + " (expected " + form.Expected.ToString(CultureInfo.InvariantCulture) + ")").ToList();
            var formsOk = formsWrong.Count == 0;
            selfTest.Add((formsOk ? "ok" : "FAIL") + " the start count recognises both start forms (the Start call and the constructor, also target-typed into a declared Process variable) with and without the namespace, not ProcessStartInfo or comments; not recognised: a using alias, a using static Start call, a target-typed constructor assigned to an untyped or earlier-declared variable ("
                + startForms.Length.ToString(CultureInfo.InvariantCulture) + " snippets; wrong: " + string.Join(" ; ", formsWrong) + ")");
            // P10-52: the L4 label the parity case actually uses (from the case table), and a mutant table with a plain L4
            // label (missing dll: no launch) that must red the same predicate.
            var l4Used = full.Labels.SingleOrDefault(label => label == "L4" || label.StartsWith("L4 ", StringComparison.Ordinal));
            var mutantLaunchesBefore = launches;
            var mutant = Parity(Path.Combine(root, "p10-52-absent", "Bim.Cli.dll"), Handled, (_, _) => { }, l4Label: "L4");
            var l4Mutant = mutant.Labels.SingleOrDefault(label => label == "L4" || label.StartsWith("L4 ", StringComparison.Ordinal));
            var l4Named = L4Named(l4Used) && l4Mutant == "L4" && !L4Named(l4Mutant) && launches == mutantLaunchesBefore;
            selfTest.Add((l4Named ? "ok" : "FAIL") + " the L4 parity case's own label names its .bim policy-file dependence (" + (l4Used ?? "<no L4 case>")
                + "); a plain L4 label reds it (mutant " + (l4Mutant ?? "<no L4 case>") + ": " + (L4Named(l4Mutant) ? "passes" : "fails") + ")");
            // P10-52: bounded stderr; a failed launch (dotnet on a missing dll) reports its stderr in the measured CLI part.
            var longStderr = BoundedStderr(new string('e', 5000));
            var boundOk = longStderr.Length < StderrBound + 40 && longStderr.EndsWith("...(5000 chars)", StringComparison.Ordinal) && BoundedStderr("short") == "short";
            selfTest.Add((boundOk ? "ok" : "FAIL") + " stderr is bounded (" + longStderr.Length.ToString(CultureInfo.InvariantCulture) + " chars of 5000)");
            string failedLaunch;
            bool failedOk;
            try
            {
                var (failedOut, failedExit, failedErr, _) = Cli(Path.Combine(root, "p10-52-absent", "Bim.Cli.dll"), 60000, 0);
                var part = CliPart(failedOut, failedExit, failedErr, failedExit != 0);
                failedOk = failedExit != 0 && failedErr.Trim().Length > 0 && part.Contains(" stderr " + BoundedStderr(failedErr), StringComparison.Ordinal);
                failedLaunch = part;
            }
            catch (TimeoutException error) { failedOk = false; failedLaunch = error.Message; }
            selfTest.Add((failedOk ? "ok" : "FAIL") + " a failed CLI launch (dotnet on a missing dll) reports its stderr in the measured part (" + BoundedStderr(failedLaunch) + ")");
            // P10-52: the stdin release task after a normal exit: the CLI exits on its own long before the 30 s release,
            // and Launch cancels the release task (Canceled), so nothing outlives the launch.
            string normalEntry;
            bool normalOk;
            if (!File.Exists(cliDll)) { normalOk = false; normalEntry = "no " + cliDll; }
            else
            {
                var normalWork = Path.Combine(Path.GetTempPath(), "p10-52-normal-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(normalWork);
                try
                {
                    var input = Path.Combine(normalWork, "project.json");
                    File.WriteAllText(input, surrogateProject);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var (normalOut, normalExit, _, normalRelease) = Cli(cliDll, 120000, 30000, "validate", input);
                    var waited = clock.ElapsedMilliseconds;
                    normalOk = normalRelease == TaskStatus.Canceled && normalOut.StartsWith(ConvertedPrefix, StringComparison.Ordinal) && waited < 20000;
                    normalEntry = "release task " + normalRelease + ", exit " + normalExit.ToString(CultureInfo.InvariantCulture) + ", " + waited.ToString(CultureInfo.InvariantCulture) + " ms";
                }
                catch (TimeoutException error) { normalOk = false; normalEntry = error.Message; }
                finally { try { Directory.Delete(normalWork, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            selfTest.Add((normalOk ? "ok" : "FAIL") + " the stdin release task is cancelled after a normal exit (" + normalEntry + ")");
            // P10-52: L4 isolation: a malformed .bim/revit-enabled-roots.txt planted in L4's temporary repository root turns
            // the CLI side into allowlist_invalid (L4 fails alone), so the full run's L4 does not read this repository's file.
            var malformedSinks = new List<(bool Ok, string Name)>();
            var malformed = Parity(cliDll, Handled, (ok, name) => malformedSinks.Add((ok, name)), new[] { L4ParityLabel }, policyText: "relative/not-absolute\n");
            var malformedOk = malformedSinks.Count == 2 && malformedSinks[0].Ok && !malformedSinks[1].Ok && malformed.Failing.Count == 1 && malformed.Failing[0] == L4ParityLabel
                && malformed.Measured.Count == 1 && malformed.Measured[0].Contains("| cli {\"status\":\"failed\",\"code\":\"allowlist_invalid\"", StringComparison.Ordinal)
                && full.Measured.Count(entry => System.Text.RegularExpressions.Regex.IsMatch(entry, @" \| temporary repository root \(copied in [0-9]+ ms\), \.bim/revit-enabled-roots\.txt absent\z")) == 1
                && System.Text.RegularExpressions.Regex.IsMatch(malformed.Measured[0], @" \| temporary repository root \(copied in [0-9]+ ms\), \.bim/revit-enabled-roots\.txt planted\z") && malformed.Launches == 1;
            selfTest.Add((malformedOk ? "ok" : "FAIL") + " a malformed .bim/revit-enabled-roots.txt in L4's temporary root answers allowlist_invalid there, the copy time listed in the entry (" + string.Join(" ; ", malformed.Measured.Select(entry => BoundedStderr(entry) + " ..." + entry[Math.Max(0, entry.Length - 120)..])) + ")");
            Check(missingOk && plantedOk && absentTimeoutOk && timeoutRun.Ok && oneLauncher && formsOk && l4Named && boundOk && failedOk && normalOk && malformedOk,
                "P10-48/P10-50/P10-52 parity/Cli self-tests: a missing Release Bim.Cli.dll fails the precondition once and launches nothing; a planted send fails its parity case (L2 only launched, stderr listed); a missing timeout-child Bim.Mcp.dll is one named precondition failure; Cli kills a child at its timeout, its stdin release task ends with it and the message carries its stderr; one launcher (all start forms); the L4 case's own label names its policy-file dependence (a plain L4 label reds); stderr bounded and reported on a failed launch; release task cancelled after a normal exit; L4 isolated from a malformed policy file | "
                + string.Join(" ; ", selfTest));
            // P10-48: the repository root comes from Program.cs FindRoot (root), not a second walk in this file.
            Check(!ownSource.Contains("AppContext." + "BaseDirectory", StringComparison.Ordinal),
                "one repository-root walk: RepeatedEnvelopeKeyTests.cs takes root from tests/mcp/Program.cs FindRoot, no walk of its own");

            // P10-46: the RunCases listing (own-counter self-test above) names every case, the passing one too.
            Check(selfMeasured.Count == 2 && selfMeasured[0].StartsWith("FAIL S1 planted send (sends 1) => ", StringComparison.Ordinal)
                    && selfMeasured[1].StartsWith("ok S2 after it (sends 0) => ", StringComparison.Ordinal),
                "measured listing: RunCases lists every case's response and send count, ok and FAIL alike | " + string.Join(" ; ", selfMeasured));

            // P10-46: the P10-40 cases count sends per case. Self-test on that server: a send planted before the cases fails
            // none; a case that sends when it expects none fails alone, and the listing names both cases.
            sends++;
            var nestedSelf = new List<string>();
            var nestedSelfFailing = RunNested(new (string, string, string)[]
            {
                ("S1 planted send", Revit("94", """{"category":"OST_Walls"}"""), ToolText("94", RevitFailure("no_response", "pipe_name", "pipe closed without a response line"), true)),
                ("S2 after it", Request("95", "ping", "{}"), Ping("95")),
            }, nestedSelf);
            Check(nestedSelfFailing.Count == 1 && nestedSelfFailing[0] == "S1 planted send" && nestedSelf.Count == 2
                    && nestedSelf[0].StartsWith("FAIL S1 planted send (sends 1) => ", StringComparison.Ordinal) && nestedSelf[1].StartsWith("ok S2 after it (sends 0) => ", StringComparison.Ordinal),
                "P10-40 per-case send count: a send before the cases fails none, a sending case fails alone, every case listed | failing: " + string.Join(", ", nestedSelfFailing) + " | " + string.Join(" ; ", nestedSelf));
        }
    }
}
