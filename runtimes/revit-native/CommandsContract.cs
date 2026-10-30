using Bim.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// C03 (ADR0009) executor decisions that need no Autodesk assembly. Autodesk-free on purpose:
// tests/integration/RevitCommandGuard.Tests source-links this file. CommandsExecutor.cs supplies the Revit facts.

// A revit.command request after binding: the envelope the executor re-validated and the hash of the Command text
// exactly as it arrived on the pipe.
public sealed record CommandRequest(RevitCommandEnvelope Envelope, string RequestSha256);

public static class CommandWire
{
    // module-contracts "Hash contract for C03": lowercase hex SHA-256 of the UTF-8 bytes of the Command text as received.
    public static string Sha256(string commandText) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(commandText))).ToLowerInvariant();

    // commandText is the raw JSON of Request.Command (JsonElement.GetRawText keeps the received text). The envelope is
    // parsed again with the Core protocol: the executor never trusts the client's validation. The outer Request fields
    // must repeat the envelope's document and carry nothing else.
    public static CommandRequest Bind(string? documentPath, string? uniqueId, double[]? metres, bool hasOtherPayload, string? commandText)
    {
        if (commandText is null) throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, "Command", "revit.command requires the Command envelope");
        if (hasOtherPayload || metres is { Length: > 0 }) throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, "Request", "revit.command carries no Metres, Batch or Checkpoint");
        var envelope = RevitCommandProtocol.Parse(commandText);
        if (!string.Equals(documentPath, envelope.Document.Path, StringComparison.Ordinal)) throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, "DocumentPath", "must equal the envelope's document.path");
        if (!string.Equals(uniqueId, envelope.Document.UniqueId, StringComparison.Ordinal)) throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, "UniqueId", "must equal the envelope's document.unique_id");
        return new CommandRequest(envelope, Sha256(commandText));
    }
}

// What the add-in reads from the active Document before deciding anything.
public sealed record CommandDocumentFacts(string? PathName, string? ProjectUniqueId, bool IsReadOnly, bool IsFamilyDocument, bool IsWorkshared, bool IsModifiable);

public static class CommandAdmission
{
    // Throws the refusal; returns when the command may run. Identity first (any command), then for writes the
    // copies-only and link guards on Document.PathName as opened (any same-user process can write to the pipe, so the
    // client's checks are not enough), then the document state. The policy is loaded only for writes.
    public static void Check(RevitCommandEnvelope envelope, CommandDocumentFacts document, Func<RevitCommandPolicy> policy, RevitFileSystemProbe probe)
    {
        if (document.ProjectUniqueId is null || !string.Equals(document.ProjectUniqueId, envelope.Document.UniqueId, StringComparison.OrdinalIgnoreCase))
            throw new RevitCommandException(RevitCommandCodes.DocumentConflict, "document.unique_id", "the active document's ProjectInformation UniqueId differs from the envelope");
        if (document.PathName is not { Length: > 0 } path || !SamePath(path, envelope.Document.Path))
            throw new RevitCommandException(RevitCommandCodes.DocumentConflict, "document.path", "the active document is not the saved document the envelope names");
        if (!envelope.Spec.Writes) return;
        var enabled = policy();
        if (RevitCopiesGuard.Check(true, path, enabled) is { } refused)
            throw new RevitCommandException(refused, "document.path", refused == RevitCommandCodes.DocumentNotEnabled ? "write commands need a document under an enabled test root" : null);
        var root = RevitCopiesGuard.MatchingRoot(path, enabled) ?? throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "no enabled root matches");
        if (RevitLinkGuard.Check(path, root, probe) is { } linked)
            throw new RevitCommandException(linked, "document.path", linked == RevitCommandCodes.DocumentLinkRefused ? "a symlink, junction or hard link between the enabled root and the opened document" : null);
        if (document.IsReadOnly) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "the document is read-only");
        if (document.IsFamilyDocument) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "family documents are not enabled");
        if (document.IsWorkshared) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "workshared documents are not enabled in catalogue v1");
        if (document.IsModifiable) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the document already has an open transaction");
    }

    // Same document path: whole-segment equality with RevitCopiesGuard's case rules (drive/UNC insensitive, '/' exact).
    public static bool SamePath(string a, string b) => RevitCopiesGuard.IsUnder(a, b) && RevitCopiesGuard.IsUnder(b, a);
}

// One element a name-or-UniqueId reference can resolve to. QualifiedName is "Family: Type" for family symbols.
public sealed record CommandCandidate(string UniqueId, string Name, string? QualifiedName = null);

public static class CommandReferences
{
    // Rules (catalogue "name or UniqueId"): a reference shaped like a UniqueId matches only by UniqueId (hex
    // case-insensitive) and never falls back to a name; otherwise it must equal exactly one Name or QualifiedName
    // (ordinal). No match, or more than one, is reference_not_found; an ambiguous name asks for the UniqueId.
    public static int Resolve(string reference, IReadOnlyList<CommandCandidate> candidates, string field, string kind)
    {
        var byUniqueId = RevitCommandProtocol.IsUniqueId(reference);
        var matches = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var hit = byUniqueId
                ? string.Equals(candidate.UniqueId, reference, StringComparison.OrdinalIgnoreCase)
                : string.Equals(candidate.Name, reference, StringComparison.Ordinal) || string.Equals(candidate.QualifiedName, reference, StringComparison.Ordinal);
            if (hit) matches.Add(i);
        }
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, field, "no " + kind + (byUniqueId ? " with UniqueId " : " named ") + reference);
        throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, field, matches.Count + " " + kind + "s match '" + reference + "'; pass the UniqueId instead");
    }

    public static string Qualified(string family, string type) => family + ": " + type;
}

// One persisted receipt: the exact success response that was returned, bound to the request text hash.
public sealed record StoredCommandReceipt(string Command, string DocumentUniqueId, string RequestSha256, string ResponseJson);
public enum CommandReceiptAction { Execute, Replay, Conflict }

// Idempotency by operation_id. The add-in persists the receipts in one ExtensibleStorage DataStorage with its own
// schema; this class decides and (de)serializes the payload.
public static class CommandReceiptStore
{
    // Own schema, independent of BatchExecutor (9f5a8262-...) and NativeRoomFixture (be8622df-...).
    public static readonly Guid SchemaGuid = new("f1ab754d-5370-4412-9d79-7066e4627326");
    public const string SchemaName = "BimAgenticRevitCommandReceiptsV1";
    public const string PayloadField = "Payload";
    public const int PayloadVersion = 1;
    static readonly string[] ReceiptMembers = { "command", "document_unique_id", "request_sha256", "response" };

    public static CommandReceiptAction Decide(IReadOnlyDictionary<string, StoredCommandReceipt> receipts, string operationId, string requestSha256) =>
        !receipts.TryGetValue(operationId, out var stored) ? CommandReceiptAction.Execute
        : string.Equals(stored.RequestSha256, requestSha256, StringComparison.Ordinal) ? CommandReceiptAction.Replay
        : CommandReceiptAction.Conflict;

    public static RevitCommandException Conflict(string operationId) =>
        new(RevitCommandCodes.OperationIdConflict, "operation_id", "operation_id " + operationId + " was already used for a different request; nothing was written");

    // Only a success that answers this very request is recorded.
    public static StoredCommandReceipt Record(CommandRequest request, RevitCommandResponse response)
    {
        var envelope = request.Envelope;
        if (!response.IsSuccess || response.Receipt is not { Replayed: false } receipt) throw new InvalidOperationException("only a first-run success is recorded");
        if (receipt != new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, request.RequestSha256, false))
            throw new InvalidOperationException("the response receipt does not answer this request");
        return new StoredCommandReceipt(envelope.Command, envelope.Document.UniqueId, request.RequestSha256, response.ToJson());
    }

    // The stored response with replayed true; created/modified/readback/warnings unchanged.
    public static string ReplayJson(StoredCommandReceipt stored)
    {
        var response = RevitCommandResponse.Parse(stored.ResponseJson);
        return (response with { Receipt = response.Receipt! with { Replayed = true } }).ToJson();
    }

    // {"version":1,"receipts":{"<operation_id>":{"command","document_unique_id","request_sha256","response"}}}, keys ordinal.
    public static string Serialize(IReadOnlyDictionary<string, StoredCommandReceipt> receipts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", PayloadVersion);
            writer.WriteStartObject("receipts");
            foreach (var pair in receipts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(pair.Key);
                writer.WriteString("command", pair.Value.Command);
                writer.WriteString("document_unique_id", pair.Value.DocumentUniqueId);
                writer.WriteString("request_sha256", pair.Value.RequestSha256);
                writer.WriteString("response", pair.Value.ResponseJson);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Null (no store yet) is empty. Anything malformed, or a stored response that does not answer its own receipt,
    // fails closed as transaction_failed: the executor then neither replays nor writes.
    public static Dictionary<string, StoredCommandReceipt> Parse(string? payload)
    {
        var receipts = new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal);
        if (payload is null) return receipts;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Unreadable("object required");
            ExactMembers(root, "version", "receipts");
            if (root.GetProperty("version").ValueKind != JsonValueKind.Number || !root.GetProperty("version").TryGetInt32(out var version) || version != PayloadVersion) throw Unreadable("version 1 required");
            var stored = root.GetProperty("receipts");
            if (stored.ValueKind != JsonValueKind.Object) throw Unreadable("receipts object required");
            foreach (var property in stored.EnumerateObject())
            {
                if (!RevitCommandProtocol.IsOperationId(property.Name) || receipts.ContainsKey(property.Name)) throw Unreadable("invalid or duplicate operation_id");
                if (property.Value.ValueKind != JsonValueKind.Object) throw Unreadable("receipt object required");
                ExactMembers(property.Value, ReceiptMembers);
                string Text(string name) => property.Value.GetProperty(name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : throw Unreadable(name + " string required");
                var receipt = new StoredCommandReceipt(Text("command"), Text("document_unique_id"), Text("request_sha256"), Text("response"));
                RevitCommandResponse response;
                try { response = RevitCommandResponse.Parse(receipt.ResponseJson); }
                catch (RevitCommandException error) { throw Unreadable("stored response: " + error.Field); }
                if (!response.IsSuccess || response.Receipt != new RevitCommandReceipt(property.Name, receipt.Command, receipt.DocumentUniqueId, receipt.RequestSha256, false))
                    throw Unreadable("stored response does not answer receipt " + property.Name);
                receipts.Add(property.Name, receipt);
            }
            return receipts;
        }
        catch (JsonException error) { throw Unreadable(error.Message); }
        catch (InvalidOperationException error) { throw Unreadable(error.Message); }
    }

    static void ExactMembers(JsonElement element, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Unreadable("unexpected or duplicate member " + property.Name);
        if (seen.Count != names.Length) throw Unreadable("missing member");
    }

    static RevitCommandException Unreadable(string detail) => new(RevitCommandCodes.TransactionFailed, null, "receipt store unreadable: " + detail);
}

public static class CommandResponses
{
    public static RevitCommandResponse Success(CommandRequest request, IReadOnlyList<string> created, IReadOnlyList<string> modified, string readbackJson, IReadOnlyList<string> warnings)
    {
        var envelope = request.Envelope;
        var receipt = new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, request.RequestSha256, false);
        return RevitCommandResponse.Success(receipt, created, modified, Array.Empty<string>(), readbackJson, warnings);
    }

    public static string Failure(RevitCommandException error) => RevitCommandResponse.Failure(error.Code, error.Field, error.Detail).ToJson();

    // Anything Revit throws (or a failed commit) is transaction_failed with Revit's own message.
    public static string Failure(Exception error) => RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, null, error.Message).ToJson();
}
