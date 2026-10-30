using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// ADR0009 Revit command protocol v1. This file and its RevitCommands*.cs siblings hold only vendor-free contracts:
// the envelope, typed arguments in millimetres/degrees, validators, the copies-only guard, the catalogue and the
// client that forwards an envelope over the explicit per-process pipe. The add-in executor consumes these types.
public static class RevitCommandCodes
{
    public const string Ok = "ok";
    // Envelope and identity.
    public const string EnvelopeInvalid = "envelope_invalid";
    public const string ProtocolVersionUnsupported = "protocol_version_unsupported";
    public const string OperationIdInvalid = "operation_id_invalid";
    public const string DocumentPathInvalid = "document_path_invalid";
    public const string DocumentIdentityInvalid = "document_identity_invalid";
    public const string CommandUnknown = "command_unknown";
    // Arguments: structure (schema) and typed units/geometry.
    public const string ArgsInvalid = "args_invalid";
    public const string LengthInvalid = "length_invalid";
    public const string AngleInvalid = "angle_invalid";
    public const string CurveTooShort = "curve_too_short";
    public const string ArcCollinear = "arc_collinear";
    public const string ArcRadiusInvalid = "arc_radius_invalid";
    public const string ArcSweepInvalid = "arc_sweep_invalid";
    public const string BoundaryTooFewPoints = "boundary_too_few_points";
    public const string BoundaryEdgeTooShort = "boundary_edge_too_short";
    public const string BoundaryZeroArea = "boundary_zero_area";
    public const string BoundarySelfIntersecting = "boundary_self_intersecting";
    // Copies-only guard.
    public const string DocumentNotEnabled = "document_not_enabled";
    public const string DocumentLinkRefused = "document_link_refused";
    public const string AllowlistInvalid = "allowlist_invalid";
    // Transport.
    public const string ExplicitProcessPipeRequired = "explicit_process_pipe_required";
    public const string RequestTooLarge = "request_too_large";
    public const string PipeConnectTimeout = "pipe_connect_timeout";
    public const string PipeIoError = "pipe_io_error";
    public const string NoResponse = "no_response";
    // A reply that is malformed or answers another request.
    public const string ResponseInvalid = "response_invalid";
    // Reserved for the add-in executor; never produced by the client.
    public const string OperationIdConflict = "operation_id_conflict";
    public const string DocumentConflict = "document_conflict";
    public const string ReferenceNotFound = "reference_not_found";
    public const string TransactionFailed = "transaction_failed";

    public static IReadOnlyList<string> ClientSide { get; } = new[]
    {
        EnvelopeInvalid, ProtocolVersionUnsupported, OperationIdInvalid, DocumentPathInvalid, DocumentIdentityInvalid, CommandUnknown,
        ArgsInvalid, LengthInvalid, AngleInvalid, CurveTooShort, ArcCollinear, ArcRadiusInvalid, ArcSweepInvalid,
        BoundaryTooFewPoints, BoundaryEdgeTooShort, BoundaryZeroArea, BoundarySelfIntersecting,
        DocumentNotEnabled, DocumentLinkRefused, AllowlistInvalid, ExplicitProcessPipeRequired, RequestTooLarge, PipeConnectTimeout, PipeIoError, NoResponse, ResponseInvalid,
    };

    public static IReadOnlyList<string> ExecutorReserved { get; } = new[] { OperationIdConflict, DocumentConflict, ReferenceNotFound, TransactionFailed };
}

// A typed refusal. Message equals Code; Field is the JSON path of the offending value (for example "args.curve.start[0]").
public sealed class RevitCommandException : ArgumentException
{
    public RevitCommandException(string code, string? field = null, string? detail = null) : base(code)
    {
        Code = code;
        Field = field;
        Detail = detail;
    }

    public string Code { get; }
    public string? Field { get; }
    public string? Detail { get; }
}

public sealed record RevitDocumentRef(string Path, string UniqueId);

public abstract record RevitCommandArgs;
public sealed record EmptyQueryArgs : RevitCommandArgs;
public sealed record FamilySymbolQueryArgs(string Category) : RevitCommandArgs;

public abstract record WallCurve;
public sealed record LineWallCurve(Point2 Start, Point2 End) : WallCurve;
// Three-point arc; Center, Radius and Sweep (positive, start -> through -> end) are derived and already validated.
public sealed record ArcThroughWallCurve(Point2 Start, Point2 End, Point2 Through, Point2 Center, Length Radius, Angle Sweep, bool CounterClockwise) : WallCurve;
// Centre arc, counter-clockwise from StartAngle to EndAngle (0 deg = +X), 0 < sweep < 360.
public sealed record ArcCenterWallCurve(Point2 Center, Length Radius, Angle StartAngle, Angle EndAngle) : WallCurve
{
    public Angle Sweep => Angle.FromRadians(EndAngle.Radians - StartAngle.Radians);
}

public sealed record WallCreateArgs(string Level, string Type, Length BaseOffset, Length Height, bool Structural, WallCurve Curve) : RevitCommandArgs;
public sealed record OpeningPlaceArgs(string Category, string Symbol, string HostWallUid, Length PositionAlong, Length? Sill) : RevitCommandArgs;
public sealed record FloorCreateArgs(string Level, string Type, IReadOnlyList<Point2> Boundary) : RevitCommandArgs;
public sealed record RoomCreateArgs(string Level, Point2 Point, string? Name, string? Number) : RevitCommandArgs;

// A parsed, fully validated request. CanonicalJson (sorted keys, shortest round-trip numbers) is what travels on
// the pipe and what RequestSha256 binds, so a retry with the same operation_id can be compared by the executor.
public sealed record RevitCommandEnvelope(int ProtocolVersion, string OperationId, RevitDocumentRef Document, string Command, RevitCommandSpec Spec, RevitCommandArgs Args, string CanonicalJson)
{
    public string RequestSha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson))).ToLowerInvariant();
}

public sealed record RevitCommandReceipt(string OperationId, string Command, string DocumentUniqueId, string RequestSha256, bool Replayed);

// One response shape for every outcome. Created/Modified/Deleted carry stable element UniqueIds.
public sealed record RevitCommandResponse(string Status, string Code, string? Field, string? Detail,
    IReadOnlyList<string> Created, IReadOnlyList<string> Modified, IReadOnlyList<string> Deleted,
    string? ReadbackJson, RevitCommandReceipt? Receipt, IReadOnlyList<string> Warnings)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public bool IsSuccess => Status == Succeeded;

    public static RevitCommandResponse Failure(string code, string? field = null, string? detail = null) =>
        new(Failed, code, field, detail, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, null, Array.Empty<string>());

    public static RevitCommandResponse Success(RevitCommandReceipt receipt, IReadOnlyList<string> created, IReadOnlyList<string> modified,
        IReadOnlyList<string> deleted, string? readbackJson, IReadOnlyList<string> warnings) =>
        new(Succeeded, RevitCommandCodes.Ok, null, null, created, modified, deleted, readbackJson, receipt, warnings);

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", Status);
            writer.WriteString("code", Code);
            WriteNullable(writer, "field", Field);
            WriteNullable(writer, "detail", Detail);
            WriteArray(writer, "created", Created);
            WriteArray(writer, "modified", Modified);
            WriteArray(writer, "deleted", Deleted);
            writer.WritePropertyName("readback");
            if (ReadbackJson is null) writer.WriteNullValue();
            else
            {
                using var readback = JsonDocument.Parse(ReadbackJson);
                readback.RootElement.WriteTo(writer);
            }
            writer.WritePropertyName("receipt");
            if (Receipt is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                writer.WriteString("operation_id", Receipt.OperationId);
                writer.WriteString("command", Receipt.Command);
                writer.WriteString("document_unique_id", Receipt.DocumentUniqueId);
                writer.WriteString("request_sha256", Receipt.RequestSha256);
                writer.WriteBoolean("replayed", Receipt.Replayed);
                writer.WriteEndObject();
            }
            WriteArray(writer, "warnings", Warnings);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Strict reader for executor output: every member present, UniqueIds well formed, receipt required on success.
    public static RevitCommandResponse Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("response");
            var status = RequiredString(root, "status");
            if (status != Succeeded && status != Failed) throw Invalid("status");
            var code = RequiredString(root, "code");
            var field = NullableString(root, "field");
            var detail = NullableString(root, "detail");
            var created = UniqueIds(root, "created");
            var modified = UniqueIds(root, "modified");
            var deleted = UniqueIds(root, "deleted");
            var readback = Member(root, "readback");
            var receiptElement = Member(root, "receipt");
            RevitCommandReceipt? receipt = null;
            if (receiptElement.ValueKind == JsonValueKind.Object)
            {
                var replayed = Member(receiptElement, "replayed");
                if (replayed.ValueKind != JsonValueKind.True && replayed.ValueKind != JsonValueKind.False) throw Invalid("receipt.replayed");
                receipt = new RevitCommandReceipt(RequiredString(receiptElement, "operation_id"), RequiredString(receiptElement, "command"),
                    RequiredString(receiptElement, "document_unique_id"), RequiredString(receiptElement, "request_sha256"), replayed.GetBoolean());
            }
            else if (receiptElement.ValueKind != JsonValueKind.Null) throw Invalid("receipt");
            if (status == Succeeded && (code != RevitCommandCodes.Ok || receipt is null)) throw Invalid("receipt");
            var warnings = Member(root, "warnings");
            if (warnings.ValueKind != JsonValueKind.Array) throw Invalid("warnings");
            var warningTexts = warnings.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid("warnings")).ToArray();
            return new RevitCommandResponse(status, code, field, detail, created, modified, deleted,
                readback.ValueKind == JsonValueKind.Null ? null : readback.GetRawText(), receipt, warningTexts);
        }
        catch (JsonException error) { throw new RevitCommandException(RevitCommandCodes.ResponseInvalid, null, error.Message); }
        catch (InvalidOperationException error) { throw new RevitCommandException(RevitCommandCodes.ResponseInvalid, null, error.Message); }
    }

    static RevitCommandException Invalid(string field) => new(RevitCommandCodes.ResponseInvalid, field);

    static JsonElement Member(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value : throw Invalid(name);

    static string RequiredString(JsonElement element, string name) =>
        Member(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : throw Invalid(name);

    static string? NullableString(JsonElement element, string name) => Member(element, name) switch
    {
        { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        _ => throw Invalid(name),
    };

    static string[] UniqueIds(JsonElement element, string name)
    {
        var array = Member(element, name);
        if (array.ValueKind != JsonValueKind.Array) throw Invalid(name);
        return array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && RevitCommandProtocol.IsUniqueId(item.GetString()) ? item.GetString()! : throw Invalid(name)).ToArray();
    }

    static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}

// Lengths cross the boundary in millimetres and angles in degrees; inside they are the Core Length/Angle types.
public static class RevitUnits
{
    // Revit refuses geometry far from the origin; anything beyond 100 km is treated as a units mistake.
    public const double MaxAbsMillimetres = 1e8;

    public static Length Millimetres(double value, string field, bool allowNegative, bool allowZero)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxAbsMillimetres) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "finite millimetres within 1e8 required");
        if (value < 0 && !allowNegative) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "negative length");
        if (value == 0 && !allowZero) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "zero length");
        return Length.FromMillimetres(value);
    }

    public static Angle Degrees(double value, string field)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 1e6) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, field, "finite degrees required");
        return Angle.FromDegrees(value);
    }
}

public static class RevitCommandProtocol
{
    public const int Version = 1;
    // Same cap as the add-in's per-line limit on the pipe.
    public const int MaxRequestChars = 65536;
    public const int MaxPathChars = 1024;
    // Request.Operation value the CLI and MCP tools put on the pipe; the envelope travels in Request.Command.
    public const string WireOperation = "revit.command";

    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    static readonly Regex ProcessPipe = new(@"\Abim-agentic-os-2025-[1-9][0-9]*\z", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex OperationId = new(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly Regex UniqueId = new(@"\A[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}-[0-9a-fA-F]{8}\z", RegexOptions.CultureInvariant, RegexTimeout);
    static readonly string[] EnvelopeMembers = { "protocol_version", "operation_id", "document", "command", "args" };

    public static bool IsProcessPipe(string? name) => name is not null && ProcessPipe.IsMatch(name);
    public static bool IsOperationId(string? value) => value is not null && OperationId.IsMatch(value);
    // Revit UniqueId: an episode GUID plus an 8-hex element id.
    public static bool IsUniqueId(string? value) => value is not null && UniqueId.IsMatch(value);

    public static RevitCommandEnvelope Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException error) { throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, null, error.Message); }
        using (document) return Parse(document.RootElement);
    }

    // Strings that are not valid UTF-16 (a lone surrogate escape such as \ud800) cannot be read and make the whole
    // envelope envelope_invalid, whichever member holds them.
    public static RevitCommandEnvelope Parse(JsonElement root)
    {
        try { return ParseChecked(root); }
        catch (InvalidOperationException error) { throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, null, error.Message); }
        catch (ArgumentException error) when (error is not RevitCommandException) { throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, null, error.Message); }
    }

    static RevitCommandEnvelope ParseChecked(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, null, "object required");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, property.Name, "duplicate member");
            if (!EnvelopeMembers.Contains(property.Name, StringComparer.Ordinal)) throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, property.Name, "unexpected member");
        }
        if (!root.TryGetProperty("protocol_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var protocolVersion) || protocolVersion != Version)
            throw new RevitCommandException(RevitCommandCodes.ProtocolVersionUnsupported, "protocol_version", "1 required");
        if (!root.TryGetProperty("operation_id", out var operation) || operation.ValueKind != JsonValueKind.String || !IsOperationId(operation.GetString()))
            throw new RevitCommandException(RevitCommandCodes.OperationIdInvalid, "operation_id", "1-128 of [A-Za-z0-9._:-], starting alphanumeric");
        var documentRef = ParseDocument(root);
        if (!root.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String)
            throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", "string required");
        var command = commandElement.GetString()!;
        var spec = RevitCommandCatalogue.Find(command) ?? throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", command);
        if (!root.TryGetProperty("args", out var args)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args", "required");
        using (var schema = JsonDocument.Parse(spec.ArgsSchemaJson))
        {
            if (RevitCommandSchema.Validate(args, schema.RootElement, "args") is { } failure) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, failure.Field, failure.Detail);
        }
        var typed = spec.Bind(args);
        var canonical = CanonicalEnvelope(protocolVersion, operation.GetString()!, documentRef, command, args);
        return new RevitCommandEnvelope(protocolVersion, operation.GetString()!, documentRef, command, spec, typed, canonical);
    }

    static RevitDocumentRef ParseDocument(JsonElement root)
    {
        if (!root.TryGetProperty("document", out var document) || document.ValueKind != JsonValueKind.Object)
            throw new RevitCommandException(RevitCommandCodes.DocumentPathInvalid, "document", "object with path and unique_id required");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.EnumerateObject())
        {
            if (!seen.Add(property.Name) || (property.Name != "path" && property.Name != "unique_id"))
                throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, "document." + property.Name, "unexpected or duplicate member");
        }
        if (!document.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String || !RevitCopiesGuard.IsAbsoluteNormalized(path.GetString()))
            throw new RevitCommandException(RevitCommandCodes.DocumentPathInvalid, "document.path", "absolute saved path without . or .. segments");
        if (!document.TryGetProperty("unique_id", out var uniqueId) || uniqueId.ValueKind != JsonValueKind.String || !IsUniqueId(uniqueId.GetString()))
            throw new RevitCommandException(RevitCommandCodes.DocumentIdentityInvalid, "document.unique_id", "ProjectInformation UniqueId required");
        return new RevitDocumentRef(path.GetString()!, uniqueId.GetString()!);
    }

    static string CanonicalEnvelope(int version, string operationId, RevitDocumentRef document, string command, JsonElement args)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("args");
            WriteCanonical(writer, args);
            writer.WriteString("command", command);
            writer.WriteStartObject("document");
            writer.WriteString("path", document.Path);
            writer.WriteString("unique_id", document.UniqueId);
            writer.WriteEndObject();
            writer.WriteString("operation_id", operationId);
            writer.WriteNumber("protocol_version", version);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Deterministic JSON: object members sorted ordinally, numbers as shortest round-trip doubles, default escaping.
    public static string Canonical(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, element);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                if (!element.TryGetDouble(out var number)) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, null, "non-finite number");
                // -0 and 0 are the same length; write both as 0.
                writer.WriteNumberValue(number == 0 ? 0.0 : number);
                break;
            case JsonValueKind.String: writer.WriteStringValue(element.GetString()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            default: writer.WriteNullValue(); break;
        }
    }

    // The line written to the add-in pipe: the existing Request shape (DocumentPath, Operation, UniqueId, Metres)
    // plus Command, the canonical envelope. DocumentPath keeps the add-in's exact saved-path check in force.
    public static string WireLine(RevitCommandEnvelope envelope)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("DocumentPath", envelope.Document.Path);
            writer.WriteString("Operation", WireOperation);
            writer.WriteString("UniqueId", envelope.Document.UniqueId);
            writer.WriteStartArray("Metres");
            writer.WriteEndArray();
            writer.WritePropertyName("Command");
            writer.WriteRawValue(envelope.CanonicalJson, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
