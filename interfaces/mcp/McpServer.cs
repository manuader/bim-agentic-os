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
// mirror_drift (P2-08) is read-only: its text is the file the CLI mirror-drift verb writes (MirrorDriftTool.cs).
// mirror_drift_select (P2-10) is read-only: its text is the file the CLI mirror-drift-select verb writes (the canonical
// P2-09 selection JSON, MirrorDriftSelectTool.cs); nothing is applied.
// skills_lock_verify (P5-09) is read-only: its text is the CLI skills-lock verify stdout (SkillLockTool.cs); pin and
// update write the lock and stay CLI-only.
// audit_verify (P10-12) is read-only: its text is the CLI audit-verify stdout for the same log, anchor, actors and summary
// (AuditVerifyTool.cs); --write-anchor stays CLI-only. Listed with permission level read (readOnlyHint true). Its
// anchor_path, actors and summary are optional (ParameterSpec.Required false); actors is a JSON array of actor ids.
// Present-but-null or wrongly typed arguments are -32602; an actors array that is empty or holds a non-string or invalid
// item (the schema's items/minItems) is the typed tool error audit_verify_actors_invalid, not -32602.
// ADR0009/P9-05: one revit_<command> tool per RevitCommandCatalogue.All entry (v1, v2 and any later entry) is generated
// from the catalogue at construction; there is no hardcoded tool list. Each lists the P10-03 permission level it needs on
// a test copy, derived from PermissionPolicy.RequiredLevel, as _meta "bim-agentic-os/permission_level" (read or
// write-copies; a write to a real project needs write-real-project plus an exact owner enablement), and best-effort
// annotations. Tool names must be unique and match ^[a-zA-Z0-9_-]{1,64}$, or construction throws. Each builds the
// command envelope from its arguments and runs RevitCommandService.Run, the same code as the CLI revit-command verb,
// so its text equals that verb's stdout for the same envelope. Write commands pass the copies-only and link guards
// first; the policy is the repository's .bim/native-runs plus every directory root listed in the owner file
// .bim/revit-enabled-roots.txt (re-read when it changes, no restart needed), never a tool call. The MCP server does not apply the per-document enablement or the
// per-actor grants of PermissionPolicy, and writes no audit log.
// P1-16: revit_query_elements also takes the optional boolean all (a tool argument beside args, never sent to the add-in).
// all:true pages the listing with RevitElementQueryPaging.All over the same transport, each page through
// RevitCommandService.Run (RevitServiceTransport, as the CLI revit-command --all verb), and its text is the
// ElementQueryAllResult JSON, byte-equal to that verb's stdout; refusals (cursor_loop, page_limit_exceeded,
// listing_too_large and the early ones) are isError in the same listing shape. Absent or false, the call is unchanged.
// The permission level stays read. Because this server is long-lived, all:true has tighter caps than the CLI: at most
// DefaultQueryAllBytes (32 MiB) of element JSON (listing_too_large beyond it; CLI byte-equality holds for listings under
// this cap, the CLI allowing 256 MiB) and an overall deadline DefaultQueryAllDeadline (1800 s, the CLI's per-page read
// timeout) for the whole listing: a page due after it is not sent, and on the real pipe each page's read timeout is
// cut to the time left, both reported as query_all_deadline_exceeded in the listing shape (the CLI has no overall
// deadline). Known difference: the CLI reads the owner policy files even for this read-only command (a malformed owner
// file refuses it); here, as without all, they are not read.
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

    sealed record ParameterSpec(string Name, string Type, string Description, bool Required = true);
    sealed record ToolSpec(string Name, string Description, ParameterSpec[] Parameters, Func<JsonElement, (string Text, bool IsError)> Execute, string? InputSchemaJson = null, PermissionLevel? Permission = null, bool Destructive = false, bool WritesFiles = false);

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
        new ToolSpec("mirror_drift", "Read-only drift report (P2-04) and resolution (P2-07: adopt/restore proposals and a restore plan) of a query.snapshot against the Core store; never applies anything, every proposal requires approval (CLI: mirror-drift store snapshot.json mirror.json output.json).",
            [new ParameterSpec("store_path", "string", "Directory of an initialized ProjectStore (read only)"), new ParameterSpec("snapshot", "object", "query.snapshot readback (snapshot_version 1) or its catalogue response"),
             new ParameterSpec("mirror", "object", "document {path, unique_id}, core_project_id, core_revision, map {BIM-IR id: UniqueId}, optional levels, wall_types, door_symbol, window_symbol, journal, options")],
            DriftTool),
        new ToolSpec("mirror_drift_select", "Read-only selective drift resolution (P2-09) of the mirror_drift resolution for a reviewer's adopt/restore choice per proposal: returns the canonical selection JSON (BIM-IR operations, filtered restore plan, invalidated map entries); never applies anything, applied false, requires approval (CLI: mirror-drift-select store snapshot.json mirror.json choices.json output.json).",
            [new ParameterSpec("store_path", "string", "Directory of an initialized ProjectStore (read only)"), new ParameterSpec("snapshot", "object", "query.snapshot readback (snapshot_version 1) or its catalogue response"),
             new ParameterSpec("mirror", "object", "the mirror_drift mirror request: document, core_project_id, core_revision, map, optional levels, wall_types, door_symbol, window_symbol, journal, options"),
             new ParameterSpec("choices", "object", "{\"choices\":[{\"proposal_id\": proposal UniqueId or core:<id>, \"action\": \"adopt\" or \"restore\"}]}, at most 20000 entries")],
            DriftSelectTool),
        new ToolSpec("skills_lock_verify", "Read-only verify of a project skill lock (P5-08 skills.lock.json) against a skills directory: reports skill_lock_drift / skill_lock_missing per pack; never pins, updates or writes (CLI: skills-lock verify skills-directory lock.json).",
            [new ParameterSpec("skills_path", "string", "Skills directory (skills/<id>/skill.json), read only"), new ParameterSpec("lock_path", "string", "Project lock file (skills.lock.json), read only")],
            LockVerifyTool),
        new ToolSpec("audit_verify", "Read-only verification (P10-09) of a P10-03 audit log, with an optional head anchor file and known actor set: the report (or with summary the decision summary); isError on any finding; never writes the log or an anchor (CLI: audit-verify log.jsonl [--anchor head.json] [--actors a,b] [--summary]).",
            [new ParameterSpec("log_path", "string", "Audit log (JSONL), read only"), new ParameterSpec("anchor_path", "string", "Head anchor file {kind audit-anchor, version 1, sequence, hash}, read only", false),
             new ParameterSpec("actors", "array", "Known actor ids", false), new ParameterSpec("summary", "boolean", "Return the decision summary instead of the report", false)],
            AuditTool, AuditInputSchema, PermissionLevel.Read),
    ];

    const string AuditInputSchema = """{"type":"object","properties":{"log_path":{"type":"string","description":"Audit log (JSONL), read only"},"anchor_path":{"type":"string","description":"Head anchor file {kind audit-anchor, version 1, sequence, hash}, read only"},"actors":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Known actor ids; an entry by any other actor is audit_actor_unknown"},"summary":{"type":"boolean","description":"Return the decision summary instead of the report"}},"required":["log_path"],"additionalProperties":false}""";

    static readonly JsonElement EmptyArguments = CreateEmptyArguments();

    readonly ToolSpec[] tools;
    readonly Func<RevitCommandPolicy> revitPolicy;
    readonly Func<string, string, string?> revitTransport;
    readonly RevitFileSystemProbe revitProbe;

    // Caps of revit_query_elements all:true: the core page and element caps, a 32 MiB element-JSON cap (below the
    // CLI's 256 MiB) and an overall deadline on QueryAllClock. Tests lower them and inject the clock.
    public const long DefaultQueryAllBytes = 32L * 1024 * 1024;
    public static readonly TimeSpan DefaultQueryAllDeadline = RevitCommandService.DefaultReadTimeout;
    public const string QueryAllDeadlineExceeded = "query_all_deadline_exceeded";
    internal (int Pages, int Elements, long Bytes) QueryAllCaps { get; init; } =
        (RevitElementQueryPaging.MaxPages, RevitElementQueryPaging.MaxElements, DefaultQueryAllBytes);
    internal TimeSpan QueryAllDeadline { get; init; } = DefaultQueryAllDeadline;
    internal Func<DateTimeOffset> QueryAllClock { get; init; } = () => DateTimeOffset.UtcNow;
    // True when revitTransport is the real process pipe, whose read timeout all:true can cut to the time left.
    readonly bool realPipe;

    // Defaults: the repository policy (see RevitCommandPolicy.ForRepository), re-read whenever the owner files change
    // (RevitCommandPolicySource: one stat per write call), the real process pipe and file system. A malformed owner
    // file enables no write at all: writes answer allowlist_invalid until it is fixed; reads keep working.
    public McpServer(RevitCommandPolicy? revitPolicy = null, Func<string, string, string?>? revitTransport = null, RevitFileSystemProbe? revitProbe = null)
        : this(RevitCommandCatalogue.All, revitPolicy, revitTransport, revitProbe) { }

    // Tests inject a synthetic catalogue here; production always uses RevitCommandCatalogue.All.
    internal McpServer(IReadOnlyList<RevitCommandSpec> catalogue, RevitCommandPolicy? revitPolicy, Func<string, string, string?>? revitTransport, RevitFileSystemProbe? revitProbe)
        : this(catalogue, revitPolicy is { } fixedPolicy ? () => fixedPolicy : DefaultPolicy(), revitTransport, revitProbe) { }

    // Tests inject a policy source (for example a RevitCommandPolicySource on a temporary repository) here. The function
    // is called once per write call and may throw RevitCommandException (allowlist_invalid).
    internal McpServer(IReadOnlyList<RevitCommandSpec> catalogue, Func<RevitCommandPolicy> revitPolicy, Func<string, string, string?>? revitTransport, RevitFileSystemProbe? revitProbe)
    {
        ArgumentNullException.ThrowIfNull(revitPolicy);
        this.revitPolicy = revitPolicy;
        this.revitTransport = revitTransport ?? RevitCommandService.PipeTransport;
        realPipe = revitTransport is null;
        this.revitProbe = revitProbe ?? RevitFileSystemProbe.Real;
        ArgumentNullException.ThrowIfNull(catalogue);
        // Names first, so a collision is reported as such before any entry is classified.
        CheckToolNames([.. CoreTools.Select(tool => tool.Name), .. catalogue.Select(spec => spec.ToolName)]);
        tools = [.. CoreTools, .. catalogue.Select(RevitTool)];
    }

    static Func<RevitCommandPolicy> DefaultPolicy() => new RevitCommandPolicySource(AppContext.BaseDirectory).Current;

    // MCP tool names: unique, 1-64 characters of [a-zA-Z0-9_-]. A collision (a.b_c and a_b.c both map to revit_a_b_c)
    // would make one command unreachable or shadow another, so the server refuses to start.
    static readonly System.Text.RegularExpressions.Regex ToolNamePattern = new(@"\A[a-zA-Z0-9_-]{1,64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static void CheckToolNames(IReadOnlyList<string> names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name is null || !ToolNamePattern.IsMatch(name)) throw new InvalidOperationException("tool_name_invalid: " + name);
            if (!seen.Add(name)) throw new InvalidOperationException("tool_name_duplicate: " + name);
        }
    }

    // The P10-03 level a command needs on a test copy, asked of PermissionPolicy itself with a probe document under a
    // probe copies root. A catalogue entry the policy cannot classify fails construction.
    const string CopiesProbeRoot = @"C:\bim-agentic-os\.bim\native-runs";
    const string CopiesProbeDocument = @"C:\bim-agentic-os\.bim\native-runs\probe\probe.rvt";

    static PermissionLevel LevelOnCopy(RevitCommandSpec spec) =>
        PermissionPolicy.RequiredLevel(spec.Name, CopiesProbeDocument, new[] { CopiesProbeRoot })
        ?? throw new InvalidOperationException("permission_level_unknown: " + spec.Name);

    ToolSpec RevitTool(RevitCommandSpec spec) => new(spec.ToolName,
        spec.Description + " Forwards to the Revit add-in on the explicit process pipe; " + (spec.Writes
            ? "write: refused unless the document is under the repository .bim/native-runs or a directory root listed in .bim/revit-enabled-roots.txt (no per-document or per-actor check)."
            : RevitCommandCatalogue.WritesFiles(spec.Name)
                ? "read-only for the model: allowed on any document, writing image files only under the repository .bim/exports; with visual_style it gets the write rule (document under .bim/native-runs or a directory root listed in .bim/revit-enabled-roots.txt)."
                : "read-only: allowed on any document."),
        [new ParameterSpec("pipe_name", "string", ""), new ParameterSpec("operation_id", "string", ""), new ParameterSpec("document", "object", ""), new ParameterSpec("args", "object", ""),
            .. (Pageable(spec) ? new[] { new ParameterSpec(AllParameter, "boolean", "", Required: false) } : Array.Empty<ParameterSpec>())],
        arguments => RevitCall(spec, arguments),
        RevitInputSchema(spec),
        LevelOnCopy(spec),
        RevitCommandCatalogue.IsDestructive(spec.Name),
        RevitCommandCatalogue.WritesFiles(spec.Name));

    const string AllParameter = "all";

    static bool Pageable(RevitCommandSpec spec) => spec.Name == RevitElementQueryPaging.Command;

    public const string PermissionMetaKey = "bim-agentic-os/permission_level";

    // Level names as written in OWNER.md and the P10-03 policy comments.
    public static string PermissionLevelName(PermissionLevel level) => level switch
    {
        PermissionLevel.Read => "read",
        PermissionLevel.WriteCopies => "write-copies",
        PermissionLevel.WriteRealProject => "write-real-project",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

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
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Exact saved absolute path of the document", ["minLength"] = 1, ["maxLength"] = RevitCommandProtocol.MaxPathChars },
                        ["unique_id"] = new JsonObject { ["type"] = "string", ["description"] = "ProjectInformation UniqueId of the document", ["minLength"] = 45, ["maxLength"] = 45, ["pattern"] = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-[0-9a-fA-F]{8}$" },
                    },
                    ["required"] = new JsonArray("path", "unique_id"),
                    ["additionalProperties"] = false,
                },
                ["args"] = JsonNode.Parse(spec.ArgsSchemaJson),
            },
            ["required"] = new JsonArray("pipe_name", "operation_id", "document", "args"),
            ["additionalProperties"] = false,
        };
        if (Pageable(spec))
            schema["properties"]!.AsObject()[AllParameter] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "true: follow next_cursor until the listing is complete (one query.elements request per page, like the CLI revit-command --all) and return the concatenated listing {status, code, field, detail, pages, matched, returned, elements}; refused with cursor_loop, page_limit_exceeded (more than " + RevitElementQueryPaging.MaxPages.ToString(System.Globalization.CultureInfo.InvariantCulture) + " pages) or listing_too_large, without partial elements",
            };
        return schema.ToJsonString();
    }

    // Raw argument values are copied into the envelope unchanged, so the CLI reading the same envelope agrees byte for byte.
    (string Text, bool IsError) RevitCall(RevitCommandSpec spec, JsonElement arguments)
    {
        var all = Pageable(spec) && arguments.TryGetProperty(AllParameter, out var allElement) && allElement.GetBoolean();
        try { return all ? RevitCallAll(arguments) : RevitCallChecked(spec, arguments); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            // A lone surrogate in a tool argument cannot be read as a string.
            return (all ? ElementQueryAllResult.Refusal(RevitCommandCodes.EnvelopeInvalid, null, error.Message).ToJson()
                : RevitCommandResponse.Failure(RevitCommandCodes.EnvelopeInvalid, null, error.Message).ToJson(), true);
        }
    }

    // all:true, in the CLI revit-command --all order: the pipe name first (an early refusal in the listing shape), then
    // RevitElementQueryPaging.All on the envelope, every page through RevitCommandService.Run. query.elements never
    // needs the write guard, so the policy is None.
    (string Text, bool IsError) RevitCallAll(JsonElement arguments)
    {
        var pipeName = arguments.GetProperty("pipe_name").GetString() ?? "";
        if (RevitCommandService.RejectPipe(pipeName) is { } rejected)
        {
            var refusal = RevitCommandResponse.Parse(rejected.Line);
            return (ElementQueryAllResult.Refusal(refusal.Code, refusal.Field, refusal.Detail).ToJson(), true);
        }
        var (pages, elements, bytes) = QueryAllCaps;
        var transport = new DeadlineTransport(pipeName, revitTransport, realPipe, revitProbe, QueryAllClock, QueryAllDeadline);
        var listing = RevitElementQueryPaging.All(CommandEnvelope(RevitElementQueryPaging.Command, arguments), transport, pages, elements, bytes);
        return (listing.ToJson(), !listing.IsSuccess);
    }

    // Each page through RevitCommandService.Run (as RevitServiceTransport), within one overall deadline from its creation:
    // a page due after the deadline is answered query_all_deadline_exceeded without being sent, and on the real pipe a
    // page's read timeout is the time left when that is below the default, its pipe_read_timeout reported as the deadline.
    sealed class DeadlineTransport(string pipeName, Func<string, string, string?> transport, bool realPipe, RevitFileSystemProbe probe, Func<DateTimeOffset> clock, TimeSpan deadline) : IRevitCommandTransport
    {
        readonly DateTimeOffset start = clock();

        string Exceeded() => RevitCommandResponse.Failure(QueryAllDeadlineExceeded, null,
            "the all:true listing took longer than its " + ((long)deadline.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " ms deadline; read-only, the document is unchanged and the request may be resent, or page it with args.cursor").ToJson();

        public string? Send(string envelopeJson)
        {
            var left = deadline - (clock() - start);
            if (left <= TimeSpan.Zero) return Exceeded();
            var cut = realPipe && left < RevitCommandService.DefaultReadTimeout;
            var line = RevitCommandService.Run(pipeName, envelopeJson, RevitCommandPolicy.None, cut ? RevitCommandService.PipeTransportWithReadTimeout(left) : transport, probe).Line;
            if (!cut) return line;
            try { return RevitCommandResponse.Parse(line).Code == RevitCommandCodes.PipeReadTimeout ? Exceeded() : line; }
            catch (RevitCommandException) { return line; }
        }
    }

    // The command envelope from the tool arguments, raw values copied unchanged.
    static string CommandEnvelope(string command, JsonElement arguments)
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
            writer.WriteString("command", command);
            writer.WritePropertyName("args");
            arguments.GetProperty("args").WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    (string Text, bool IsError) RevitCallChecked(RevitCommandSpec spec, JsonElement arguments)
    {
        var envelope = CommandEnvelope(spec.Name, arguments);
        // The policy matters only for requests with the write guard (writes and exports with a visual_style; other
        // read-only commands are allowed on any document), so only those stat the owner files. A malformed owner file
        // refuses them as allowlist_invalid.
        RevitCommandPolicy policy;
        try { policy = RevitCommandCatalogue.NeedsWriteGuard(spec, arguments.GetProperty("args")) ? revitPolicy() : RevitCommandPolicy.None; }
        catch (RevitCommandException error) { return (RevitCommandResponse.Failure(error.Code, error.Field, error.Detail).ToJson(), true); }
        var result = RevitCommandService.Run(arguments.GetProperty("pipe_name").GetString() ?? "", envelope, policy, revitTransport, revitProbe);
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
        ["tools"] = new JsonArray(tools.Select(tool => (JsonNode?)ListedTool(tool)).ToArray()),
    };

    static JsonObject ListedTool(ToolSpec tool)
    {
        var listed = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["inputSchema"] = tool.InputSchemaJson is { } inputSchema ? JsonNode.Parse(inputSchema) : Schema(tool.Parameters),
        };
        if (tool.Permission is { } level)
        {
            // Tool annotations and _meta postdate the advertised 2024-11-05 protocol: best-effort hints that clients
            // may ignore. Writes replay by operation_id, hence idempotent (document.save_copy and document.save_as keep no
            // receipt, but a repeat is refused target_exists without writing, so they are idempotent too); they are additive (not destructive) except the
            // P1-05 commands, wall.set_profile, material.set and element.change_type, which change or delete existing elements,
            // and document.save, which overwrites the saved file (RevitCommandCatalogue.IsDestructive).
            // view.export_image needs only read on the model but writes new image files on every call: readOnlyHint
            // false, destructiveHint false, idempotentHint false.
            var annotations = new JsonObject { ["readOnlyHint"] = level == PermissionLevel.Read && !tool.WritesFiles };
            if (level != PermissionLevel.Read)
            {
                annotations["destructiveHint"] = tool.Destructive;
                annotations["idempotentHint"] = true;
            }
            else if (tool.WritesFiles)
            {
                annotations["destructiveHint"] = false;
                annotations["idempotentHint"] = false;
            }
            listed["annotations"] = annotations;
            listed["_meta"] = new JsonObject { [PermissionMetaKey] = PermissionLevelName(level) };
        }
        return listed;
    }

    // Test seams: the names of a core tool's parameters, of its required ones, and the schema Schema() generates from them.
    internal static string[] CoreParameterNames(string tool, bool requiredOnly) =>
        [.. CoreTools.Single(spec => spec.Name == tool).Parameters.Where(parameter => !requiredOnly || parameter.Required).Select(parameter => parameter.Name)];

    internal static string GeneratedSchemaJson(string tool) => Schema(CoreTools.Single(spec => spec.Name == tool).Parameters).ToJsonString();

    static JsonObject Schema(ParameterSpec[] parameters)
    {
        var properties = new JsonObject();
        foreach (var parameter in parameters)
            properties[parameter.Name] = new JsonObject { ["type"] = parameter.Type, ["description"] = parameter.Description };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(parameters.Where(parameter => parameter.Required).Select(parameter => (JsonNode?)JsonValue.Create(parameter.Name)).ToArray()),
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
        {
            if (!arguments.TryGetProperty(parameter.Name, out var value))
            {
                if (parameter.Required) return Error(id, InvalidParams, "invalid_params: " + parameter.Name + " must be " + parameter.Type);
                continue;
            }
            if (!Matches(value, parameter.Type)) return Error(id, InvalidParams, "invalid_params: " + parameter.Name + " must be " + parameter.Type);
        }
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
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
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

    // P2-08: MirrorDriftTool, the same engine and input order as the CLI mirror-drift verb; the text is its output file.
    static (string Text, bool IsError) DriftTool(JsonElement arguments)
    {
        var outcome = MirrorDriftTool.Run(arguments.GetProperty("store_path").GetString() ?? "", arguments.GetProperty("snapshot").GetRawText(), arguments.GetProperty("mirror").GetRawText());
        return (outcome.Text, !outcome.Succeeded);
    }

    // P2-10: MirrorDriftSelectTool, the same engine and input order as the CLI mirror-drift-select verb; the text is its output file.
    static (string Text, bool IsError) DriftSelectTool(JsonElement arguments)
    {
        var outcome = MirrorDriftSelectTool.Run(arguments.GetProperty("store_path").GetString() ?? "", arguments.GetProperty("snapshot").GetRawText(),
            arguments.GetProperty("mirror").GetRawText(), arguments.GetProperty("choices").GetRawText());
        return (outcome.Text, !outcome.Succeeded);
    }

    // P5-09: SkillLockTool.Verify, the same engine as the CLI skills-lock verify verb; the text is its stdout line.
    static (string Text, bool IsError) LockVerifyTool(JsonElement arguments)
    {
        var outcome = SkillLockTool.Verify(arguments.GetProperty("skills_path").GetString() ?? "", arguments.GetProperty("lock_path").GetString() ?? "");
        return (outcome.Text, !outcome.Succeeded);
    }

    // P10-12: AuditVerifyTool.Run, the same engine as the CLI audit-verify verb; the text is its stdout line. A string that is
    // not valid text (a lone surrogate) is passed as "" (path) or null (actor), so it is refused by the engine.
    static (string Text, bool IsError) AuditTool(JsonElement arguments)
    {
        string? anchorPath = arguments.TryGetProperty("anchor_path", out var anchorElement) ? AuditString(anchorElement) ?? "" : null;
        IReadOnlyList<string?>? actors = arguments.TryGetProperty("actors", out var actorsElement)
            ? actorsElement.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? AuditString(item) : null).ToArray()
            : null;
        var summary = arguments.TryGetProperty("summary", out var summaryElement) && summaryElement.GetBoolean();
        var outcome = AuditVerifyTool.Run(AuditString(arguments.GetProperty("log_path")) ?? "", anchorPath, actors, summary);
        return (outcome.Text, !outcome.Succeeded);
    }

    static string? AuditString(JsonElement element)
    {
        try { return element.GetString(); }
        catch (InvalidOperationException) { return null; }
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
