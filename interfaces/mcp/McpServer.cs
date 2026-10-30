using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;

namespace Bim.Mcp;

// MCP (2024-11-05) over newline-delimited JSON-RPC 2.0. Tools call exactly the Core functions used by
// interfaces/cli/Program.cs, in the same input order, and serialize with the same JsonSerializer defaults.
// Byte equality of content[0].text with the CLI stdout line (without its newline) covers successful output and
// failures raised by Core or by Guid.Parse/ProjectStore. Known differences from the CLI:
// - JSON deserialization error messages: tool inputs arrive as JSON values inside the request, so the reported
//   line/byte positions differ from those of a separate input file; malformed request JSON is -32700 instead.
// - revision: the CLI applies int.Parse to a string argument (accepting e.g. " 0" or "+0" and failing with
//   status failed); here revision must be a JSON integer in Int32 range, otherwise the call is rejected with -32602.
// - missing or wrongly typed arguments are rejected with -32602 instead of the CLI usage/null failures.
// Side-effecting CLI commands (native, evaluate, init, commit-intent, ifc-export) are deliberately not exposed.
// ADR0009: one revit_<command> tool per RevitCommandCatalogue.V1 entry is generated from the catalogue. Each builds the
// command envelope from its arguments and runs RevitCommandService.Run, the same code as the CLI revit-command verb,
// so its text equals that verb's stdout for the same envelope. Write commands pass the copies-only guard first; the
// policy comes from the server's own launch (repository .bim/native-runs plus --revit-allowlist), never from a tool call.
public sealed class McpServer
{
    public const int MaxLineChars = 8 * 1024 * 1024;
    public const string ProtocolVersion = "2024-11-05";
    public const string ServerName = "bim-agentic-os";
    public const string ServerVersion = "0.1.0";

    const int ParseError = -32700;
    const int InvalidRequest = -32600;
    const int MethodNotFound = -32601;
    const int InvalidParams = -32602;
    const int InternalError = -32603;

    sealed record ParameterSpec(string Name, string Type, string Description);
    sealed record ToolSpec(string Name, string Description, ParameterSpec[] Parameters, Func<JsonElement, (string Text, bool IsError)> Execute, string? InputSchemaJson = null);

    static readonly ToolSpec[] CoreTools =
    [
        new ToolSpec("diff", "Deterministic execution plan between two Project IR objects (CLI: diff before.json after.json).",
            [new ParameterSpec("before", "object", "Project IR before the change"), new ParameterSpec("after", "object", "Project IR after the change")],
            Diff),
        new ToolSpec("validate", "Validation issues of a Project IR object; isError when any issue is present (CLI: validate project.json).",
            [new ParameterSpec("project", "object", "Project IR to validate")],
            Validate),
        new ToolSpec("room", "Compile a rectangular room intent into Project IR (CLI: room project-guid revision intent.json).",
            [new ParameterSpec("project_guid", "string", "Project identity GUID"), new ParameterSpec("revision", "integer", "Project revision"), new ParameterSpec("intent", "object", "RoomIntent in metres with provenance")],
            Room),
        new ToolSpec("plan", "Read-only execution plan from the intended state in a ProjectStore to a desired Project IR (CLI: plan store desired.json).",
            [new ParameterSpec("store_path", "string", "Directory of an initialized ProjectStore"), new ParameterSpec("desired", "object", "Desired Project IR")],
            Plan),
    ];

    static readonly JsonElement EmptyArguments = CreateEmptyArguments();

    readonly ToolSpec[] tools;
    readonly RevitCommandPolicy revitPolicy;
    readonly Func<string, string, string?> revitTransport;

    // Defaults: the repository's .bim/native-runs as the only enabled write root, and the real process pipe.
    public McpServer(RevitCommandPolicy? revitPolicy = null, Func<string, string, string?>? revitTransport = null)
    {
        this.revitPolicy = revitPolicy ?? DefaultPolicy();
        this.revitTransport = revitTransport ?? RevitCommandService.PipeTransport;
        tools = [.. CoreTools, .. RevitCommandCatalogue.V1.Select(RevitTool)];
    }

    static RevitCommandPolicy DefaultPolicy()
    {
        try { return RevitCommandPolicy.ForRepository(AppContext.BaseDirectory, null); }
        catch (RevitCommandException) { return RevitCommandPolicy.None; }
    }

    ToolSpec RevitTool(RevitCommandSpec spec) => new(spec.ToolName,
        spec.Description + " Forwards to the Revit add-in on the explicit process pipe; " + (spec.Writes ? "write: refused unless the document is under an enabled test root." : "read-only: allowed on any document."),
        [new ParameterSpec("pipe_name", "string", ""), new ParameterSpec("operation_id", "string", ""), new ParameterSpec("document", "object", ""), new ParameterSpec("args", "object", "")],
        arguments => RevitCall(spec, arguments),
        RevitInputSchema(spec));

    static string RevitInputSchema(RevitCommandSpec spec)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["pipe_name"] = new JsonObject { ["type"] = "string", ["description"] = "Explicit Revit process pipe bim-agentic-os-2025-<pid>, from the fixture or session result", ["pattern"] = "^bim-agentic-os-2025-[1-9][0-9]*$" },
                ["operation_id"] = new JsonObject { ["type"] = "string", ["description"] = "Idempotency key; retry with the same value and the same request to avoid duplicates", ["pattern"] = "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$" },
                ["document"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "The open Revit document",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Exact saved absolute path of the document" },
                        ["unique_id"] = new JsonObject { ["type"] = "string", ["description"] = "ProjectInformation UniqueId of the document" },
                    },
                    ["required"] = new JsonArray("path", "unique_id"),
                    ["additionalProperties"] = false,
                },
                ["args"] = JsonNode.Parse(spec.ArgsSchemaJson),
            },
            ["required"] = new JsonArray("pipe_name", "operation_id", "document", "args"),
            ["additionalProperties"] = false,
        };
        return schema.ToJsonString();
    }

    // Raw argument values are copied into the envelope unchanged, so the CLI reading the same envelope agrees byte for byte.
    (string Text, bool IsError) RevitCall(RevitCommandSpec spec, JsonElement arguments)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol_version", RevitCommandProtocol.Version);
            writer.WritePropertyName("operation_id");
            arguments.GetProperty("operation_id").WriteTo(writer);
            writer.WritePropertyName("document");
            arguments.GetProperty("document").WriteTo(writer);
            writer.WriteString("command", spec.Name);
            writer.WritePropertyName("args");
            arguments.GetProperty("args").WriteTo(writer);
            writer.WriteEndObject();
        }
        var result = RevitCommandService.Run(arguments.GetProperty("pipe_name").GetString() ?? "", Encoding.UTF8.GetString(buffer.ToArray()), revitPolicy, revitTransport);
        return (result.Line, result.IsError);
    }

    public string? Handle(string line)
    {
        try
        {
            if (line.Length > MaxLineChars) return Error(null, InvalidRequest, "request_too_large");
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { return Error(null, ParseError, "parse_error"); }
            using (document) return Dispatch(document.RootElement);
        }
        catch (Exception) { return Error(null, InternalError, "internal_error"); }
    }

    // Reads newline-delimited messages with a bounded buffer; writes only JSON-RPC responses.
    public static void Run(TextReader input, TextWriter output) => Run(input, output, new McpServer());

    public static void Run(TextReader input, TextWriter output, McpServer server)
    {
        var line = new StringBuilder();
        var oversized = false;
        while (true)
        {
            var next = input.Read();
            if (next == -1 || next == '\n')
            {
                if (oversized) Emit(output, Error(null, InvalidRequest, "request_too_large"));
                else
                {
                    if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                    var text = line.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var response = server.Handle(text);
                        if (response != null) Emit(output, response);
                    }
                }
                line.Clear();
                oversized = false;
                if (next == -1) return;
                continue;
            }
            if (oversized) continue;
            line.Append((char)next);
            if (line.Length > MaxLineChars + 1)
            {
                oversized = true;
                line.Clear();
            }
        }
    }

    static void Emit(TextWriter output, string response)
    {
        output.Write(response);
        output.Write('\n');
        output.Flush();
    }

    string? Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return Error(null, InvalidRequest, "invalid_request");
        JsonElement? id = null;
        var hasId = root.TryGetProperty("id", out var idElement);
        if (hasId)
        {
            if (idElement.ValueKind != JsonValueKind.String && idElement.ValueKind != JsonValueKind.Number && idElement.ValueKind != JsonValueKind.Null)
                return Error(null, InvalidRequest, "invalid_request");
            id = idElement;
        }
        if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            return Error(id, InvalidRequest, "invalid_request");
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            return Error(id, InvalidRequest, "invalid_request");
        // Notifications (including notifications/initialized) never receive a response.
        if (!hasId) return null;
        JsonElement? parameters = null;
        if (root.TryGetProperty("params", out var paramsElement)) parameters = paramsElement;
        try
        {
            switch (methodElement.GetString())
            {
                case "initialize": return Result(id, Initialize());
                case "ping": return Result(id, new JsonObject());
                case "tools/list": return Result(id, ListTools());
                case "tools/call": return CallTool(id, parameters);
                default: return Error(id, MethodNotFound, "method_not_found");
            }
        }
        catch (Exception) { return Error(id, InternalError, "internal_error"); }
    }

    static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = ServerVersion },
    };

    JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray(tools.Select(tool => (JsonNode?)new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = tool.InputSchemaJson is { } inputSchema ? JsonNode.Parse(inputSchema) : Schema(tool.Parameters),
        }).ToArray()),
    };

    static JsonObject Schema(ParameterSpec[] parameters)
    {
        var properties = new JsonObject();
        foreach (var parameter in parameters)
            properties[parameter.Name] = new JsonObject { ["type"] = parameter.Type, ["description"] = parameter.Description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(parameters.Select(parameter => (JsonNode?)JsonValue.Create(parameter.Name)).ToArray()),
            ["additionalProperties"] = false,
        };
    }

    string CallTool(JsonElement? id, JsonElement? parameters)
    {
        if (parameters == null || parameters.Value.ValueKind != JsonValueKind.Object) return Error(id, InvalidParams, "invalid_params: params object required");
        var call = parameters.Value;
        if (!call.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String) return Error(id, InvalidParams, "invalid_params: name required");
        var arguments = EmptyArguments;
        if (call.TryGetProperty("arguments", out var argumentsElement))
        {
            if (argumentsElement.ValueKind != JsonValueKind.Object) return Error(id, InvalidParams, "invalid_params: arguments must be an object");
            arguments = argumentsElement;
        }
        var name = nameElement.GetString();
        var tool = tools.FirstOrDefault(candidate => candidate.Name == name);
        if (tool == null) return Result(id, ToolResult(Failed("unknown_tool"), true));
        foreach (var property in arguments.EnumerateObject())
            if (!tool.Parameters.Any(parameter => parameter.Name == property.Name)) return Error(id, InvalidParams, "invalid_params: unexpected argument " + property.Name);
        foreach (var parameter in tool.Parameters)
            if (!arguments.TryGetProperty(parameter.Name, out var value) || !Matches(value, parameter.Type)) return Error(id, InvalidParams, "invalid_params: " + parameter.Name + " must be " + parameter.Type);
        string text;
        bool isError;
        try { (text, isError) = tool.Execute(arguments); }
        catch (Exception e) { text = Failed(e.Message); isError = true; }
        return Result(id, ToolResult(text, isError));
    }

    static bool Matches(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
        _ => false,
    };

    // Each tool mirrors the matching CLI case, including the order in which inputs are read.
    static (string Text, bool IsError) Diff(JsonElement arguments)
    {
        var a = Read<Project>(arguments, "before");
        var b = Read<Project>(arguments, "after");
        return (JsonSerializer.Serialize(Compiler.Diff(a, b)), false);
    }

    static (string Text, bool IsError) Validate(JsonElement arguments)
    {
        var issues = Compiler.Validate(Read<Project>(arguments, "project"));
        return (JsonSerializer.Serialize(issues), issues.Length > 0);
    }

    static (string Text, bool IsError) Room(JsonElement arguments)
    {
        var roomIntent = Read<RoomIntent>(arguments, "intent");
        var projectGuid = Guid.Parse(arguments.GetProperty("project_guid").GetString() ?? "");
        var revision = arguments.GetProperty("revision").GetInt32();
        return (JsonSerializer.Serialize(RoomCompiler.Compile(projectGuid, revision, roomIntent)), false);
    }

    static (string Text, bool IsError) Plan(JsonElement arguments)
    {
        var current = new ProjectStore(arguments.GetProperty("store_path").GetString() ?? "").Read();
        var desired = Read<Project>(arguments, "desired");
        return (JsonSerializer.Serialize(Compiler.Diff(current, desired)), false);
    }

    static T Read<T>(JsonElement arguments, string name) =>
        JsonSerializer.Deserialize<T>(arguments.GetProperty(name).GetRawText()) ?? throw new ArgumentException(name + "_required");

    static string Failed(string code) => JsonSerializer.Serialize(new { status = "failed", code });

    static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    static string Result(JsonElement? id, JsonNode result) => Envelope(id, writer =>
    {
        writer.WritePropertyName("result");
        result.WriteTo(writer);
    });

    static string Error(JsonElement? id, int code, string message) => Envelope(id, writer =>
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    });

    static string Envelope(JsonElement? id, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        // Relaxed escaping applies only to the envelope; tool text is produced with JsonSerializer defaults.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id.HasValue) id.Value.WriteTo(writer);
            else writer.WriteNullValue();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static JsonElement CreateEmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
