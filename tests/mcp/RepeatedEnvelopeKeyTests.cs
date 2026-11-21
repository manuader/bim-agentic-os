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
    //   4. The real Release Bim.Mcp.dll over stdio answers the P10-34 refusals and the new -32602 byte for byte; stdin is
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
        const string UnknownTool = "result@{0}:{\"status\":\"failed\",\"code\":\"unknown_tool\"}";

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
            ("M1 tools/call _meta lone name", Request("10", "tools/call", """{"name":"p10_36_unknown","arguments":{},"_meta":{"\ud800":1}}"""), string.Format(CultureInfo.InvariantCulture, UnknownTool, "10")),
            ("M2 tools/call _meta progressToken lone", Request("11", "tools/call", """{"name":"p10_36_unknown","_meta":{"progressToken":"\ud800"}}"""), string.Format(CultureInfo.InvariantCulture, UnknownTool, "11")),
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
            measured.Add(label + " => " + outcome);
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
            var plainInit = server.Handle(Request("50", "initialize", "{" + Init + "}"));
            var metaInit = server.Handle(Request("50", "initialize", "{" + Init + ",\"_meta\":{\"\\ud800\":1}}"));
            Check(plainInit is not null && plainInit == metaInit, "initialize with a lone surrogate inside _meta answers the plain initialize bytes | " + Short(metaInit ?? "none"));
            var plainList = server.Handle("""{"jsonrpc":"2.0","id":51,"method":"tools/list"}""");
            var cursorList = server.Handle(Request("51", "tools/list", """{"cursor":"x"}"""));
            Check(plainList is not null && plainList == cursorList, "tools/list with a valid params object answers the plain tools/list bytes");
        }

        // ---- 4. the real Release Bim.Mcp.dll over stdio ----
        {
            var mcpDll = Path.Combine(root, "interfaces", "mcp", "bin", "Release", "net8.0", "Bim.Mcp.dll");
            Check(File.Exists(mcpDll), "fixture: the Release Bim.Mcp.dll exists (" + mcpDll + ")");
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
            static byte[] Ascii(string text) => System.Text.Encoding.UTF8.GetBytes(text);
            byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
            var wtf8Lone = new byte[] { 0xED, 0xA0, 0x80 };
            var utf8Pair = new byte[] { 0xF0, 0x9F, 0x98, 0x80 };
            var replaced = "\uFFFD\uFFFD\uFFFD";
            var lines = new List<(string Label, byte[] Line, string Expected)>
            {
                // P10-34 refusals (exact bytes pinned in RunP10_34)
                ("-32600 repeated member", Ascii("""{"jsonrpc":"2.0","id":6,"method":"ping","method":"tools/list"}"""),
                    """{"jsonrpc":"2.0","id":6,"error":{"code":-32600,"message":"invalid_request: repeated member method"}}"""),
                ("-32600 repeated id", Ascii("""{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}"""),
                    """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"invalid_request: repeated member id"}}"""),
                ("-32602 repeated params member", Ascii(Request("20", "tools/call", """{"name":"p10_33_unknown","name":"validate","arguments":{"project":{}}}""")),
                    """{"jsonrpc":"2.0","id":20,"error":{"code":-32602,"message":"invalid_params: repeated params member name"}}"""),
                ("-32600 member name not text", Ascii("""{"jsonrpc":"2.0","id":1,"method":"ping","\ud800":1}"""),
                    """{"jsonrpc":"2.0","id":1,"error":{"code":-32600,"message":"invalid_request: member name is not valid text"}}"""),
                ("-32600 method not text", Ascii("""{"jsonrpc":"2.0","id":"m","method":"\ud800"}"""),
                    """{"jsonrpc":"2.0","id":"m","error":{"code":-32600,"message":"invalid_request: method is not valid text"}}"""),
                ("-32602 params member name not text", Ascii(Request("21", "tools/call", """{"name":"validate","\ud800":1}""")),
                    """{"jsonrpc":"2.0","id":21,"error":{"code":-32602,"message":"invalid_params: params member name is not valid text"}}"""),
                ("-32602 name not text", Ascii(Request("22", "tools/call", """{"name":"\ud800"}""")),
                    """{"jsonrpc":"2.0","id":22,"error":{"code":-32602,"message":"invalid_params: name is not valid text"}}"""),
                // P10-36
                ("-32602 ping params name not text", Ascii(Request("1", "ping", """{"\ud800":1}""")), PingParamsRefusal),
                ("escaped surrogate pair id", Ascii("""{"jsonrpc":"2.0","id":"\ud83d\ude00","method":"ping"}"""), PairIdEcho),
                ("raw UTF-8 surrogate pair id", Concat(Ascii("{\"jsonrpc\":\"2.0\",\"id\":\""), utf8Pair, Ascii("\",\"method\":\"ping\"}")), PairIdEcho),
                ("WTF-8 lone surrogate member name", Concat(Ascii("{\"jsonrpc\":\"2.0\",\"id\":60,\"method\":\"ping\",\""), wtf8Lone, Ascii("\":1}")), """{"jsonrpc":"2.0","id":60,"result":{}}"""),
                ("WTF-8 lone surrogate in method", Concat(Ascii("{\"jsonrpc\":\"2.0\",\"id\":61,\"method\":\"ping"), wtf8Lone, Ascii("\"}")),
                    """{"jsonrpc":"2.0","id":61,"error":{"code":-32601,"message":"method_not_found"}}"""),
                ("WTF-8 lone surrogate id", Concat(Ascii("{\"jsonrpc\":\"2.0\",\"id\":\""), wtf8Lone, Ascii("\",\"method\":\"ping\"}")),
                    "{\"jsonrpc\":\"2.0\",\"id\":\"" + replaced + "\",\"result\":{}}"),
                ("ping", Ascii("""{"jsonrpc":"2.0","id":62,"method":"ping"}"""), """{"jsonrpc":"2.0","id":62,"result":{}}"""),
            };
            var stdin = lines.SelectMany(line => line.Line.Concat(new byte[] { (byte)'\n' })).ToArray();
            byte[] stdout = [];
            var stderr = "";
            var exit = -1;
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo(dotnet) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                start.ArgumentList.Add(mcpDll);
                using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("MCP process did not start: " + dotnet);
                using var collected = new MemoryStream();
                var reading = process.StandardOutput.BaseStream.CopyToAsync(collected);
                var errors = process.StandardError.ReadToEndAsync();
                try { process.StandardInput.BaseStream.Write(stdin); process.StandardInput.BaseStream.Flush(); process.StandardInput.Close(); } catch (IOException) { }
                if (!process.WaitForExit(120000)) { process.Kill(true); stderr = "<timed out>"; }
                else
                {
                    process.WaitForExit();
                    reading.Wait();
                    stdout = collected.ToArray();
                    stderr = errors.Result;
                    exit = process.ExitCode;
                }
            }
            catch (Exception error) { stderr = "threw " + error.GetType().Name + ": " + error.Message; }
            var expectedBytes = System.Text.Encoding.UTF8.GetBytes(string.Concat(lines.Select(line => line.Expected + "\n")));
            var got = System.Text.Encoding.UTF8.GetString(stdout).Split('\n');
            var differing = new List<string>();
            for (var i = 0; i < lines.Count; i++)
                if (i >= got.Length || got[i] != lines[i].Expected) differing.Add(lines[i].Label + " => " + (i < got.Length ? got[i] : "<missing>"));
            Check(exit == 0 && stdout.AsSpan().SequenceEqual(expectedBytes),
                "the real Bim.Mcp.dll over stdio answers the P10-34 and P10-36 lines byte for byte (exit " + exit.ToString(CultureInfo.InvariantCulture) + ", "
                + stdout.Length.ToString(CultureInfo.InvariantCulture) + " bytes, expected " + expectedBytes.Length.ToString(CultureInfo.InvariantCulture) + ") | differing: "
                + string.Join(" ; ", differing) + " | hex of the WTF-8 id line: " + (got.Length > 12 ? Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(got[12])) : "-") + " | stderr: " + Short(stderr));
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
    }
}
