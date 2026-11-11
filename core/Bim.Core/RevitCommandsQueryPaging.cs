using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Bim.Core;

// P1-15 query.elements client paging: RevitElementQueryPaging.All sends one query.elements envelope, then the same
// envelope (same document, operation id, filter and limit) with args.cursor set to each reply's next_cursor until it is
// null, and concatenates the elements. Read-only by construction: any other command is refused before anything is sent.
// Every reply is checked like PlanRunner checks a step (strict parse, success, a receipt answering this very request, no
// created/modified/deleted) and its readback like the P1-13 contract states it (limit and filter echoed, matched >=
// returned = element count <= limit, truncated_reason consistent, after = the cursor's id, next_cursor present exactly when
// truncated, decodable, bound to this document and filter, and at or after the page's last id, element ids strictly
// ascending after the cursor id, and a byte cut or a full page resuming exactly after its last row). A cursor whose id does not move forward is cursor_loop; more than maxPages (at most
// 1000) pages is page_limit_exceeded; more than maxElements elements or maxBytes UTF-8 bytes of element JSON in all is
// listing_too_large, with no further page sent. A refusal carries no partial elements. Contract: docs/contracts/module-contracts.md,
// "query.elements client paging (P1-15)".
public static class RevitElementQueryPagingCodes
{
    // The envelope names another command than query.elements (decided before anything is sent).
    public const string CommandNotPageable = "paging_command_invalid";
    // A next_cursor whose element id is not strictly after the page's own cursor id.
    public const string CursorLoop = "cursor_loop";
    // The listing needs more pages than the cap.
    public const string PageLimitExceeded = "page_limit_exceeded";
    // The listing holds more elements, or more element JSON bytes, than the cumulative cap.
    public const string ListingTooLarge = "listing_too_large";
}

// Status succeeded (code ok) or failed (a typed code). Pages: replies accepted. Matched: the last page's matched (null on
// a failure). Elements: each element object's JSON text, in listing order (empty on a failure).
public sealed record ElementQueryAllResult(string Status, string Code, string? Field, string? Detail, int Pages, int? Matched, IReadOnlyList<string> Elements)
{
    public bool IsSuccess => Status == RevitCommandResponse.Succeeded;

    // A refusal before any page (the CLI's pipe, policy and envelope-read refusals under --all).
    public static ElementQueryAllResult Refusal(string code, string? field, string? detail) =>
        new(RevitCommandResponse.Failed, code, field, detail, 0, null, Array.Empty<string>());

    // {"status","code","field","detail","pages","matched","returned","elements":[...]}
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", Status);
            writer.WriteString("code", Code);
            if (Field is null) writer.WriteNull("field"); else writer.WriteString("field", Field);
            if (Detail is null) writer.WriteNull("detail"); else writer.WriteString("detail", Detail);
            writer.WriteNumber("pages", Pages);
            if (Matched is { } matched) writer.WriteNumber("matched", matched); else writer.WriteNull("matched");
            writer.WriteNumber("returned", Elements.Count);
            writer.WriteStartArray("elements");
            foreach (var element in Elements) writer.WriteRawValue(element);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public static class RevitElementQueryPaging
{
    public const int MaxPages = 1000;
    // Cumulative caps over the whole listing: element count and UTF-8 bytes of the element JSON kept.
    public const int MaxElements = 1_000_000;
    public const long MaxListingBytes = 256L * 1024 * 1024;
    public const string Command = "query.elements";

    public static ElementQueryAllResult All(string envelopeJson, IRevitCommandTransport transport, int maxPages = MaxPages, int maxElements = MaxElements, long maxBytes = MaxListingBytes)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        ArgumentNullException.ThrowIfNull(transport);
        if (maxPages < 1 || maxPages > MaxPages) throw new ArgumentOutOfRangeException(nameof(maxPages), "1 to " + MaxPages.ToString(CultureInfo.InvariantCulture) + " pages");
        if (maxElements < 1 || maxElements > MaxElements) throw new ArgumentOutOfRangeException(nameof(maxElements), "1 to " + MaxElements.ToString(CultureInfo.InvariantCulture) + " elements");
        if (maxBytes < 1 || maxBytes > MaxListingBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes), "1 to " + MaxListingBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
        if (OtherCommand(envelopeJson) is { } other)
            return Fail(RevitElementQueryPagingCodes.CommandNotPageable, "command", "only " + Command + " is paged, not " + other, 0);
        RevitCommandEnvelope first;
        try { first = RevitCommandProtocol.Parse(envelopeJson); }
        catch (RevitCommandException error) { return Fail(error.Code, error.Field, error.Detail, 0); }
        if (first.Command != Command || first.Spec.Writes || first.Args is not ElementQueryArgs query)
            return Fail(RevitElementQueryPagingCodes.CommandNotPageable, "command", "only " + Command + " is paged", 0);

        var template = JsonNode.Parse(first.CanonicalJson)!.AsObject();
        List<string> elements = [];
        long bytes = 0;
        var pages = 0;
        var envelope = first;
        while (true)
        {
            var after = ((ElementQueryArgs)envelope.Args).Cursor?.After;
            var (readback, failure) = Send(envelope, transport, pages);
            if (failure is not null) return failure;
            var (page, invalid) = Page(readback!, query, after, pages);
            if (invalid is not null) return invalid;
            var (rows, matched, next, reason) = page!.Value;
            ElementQueryCursor? decoded = null;
            if (next is not null)
            {
                try
                {
                    decoded = RevitElementQueryCursor.Decode(next, "readback.next_cursor");
                    RevitElementQueryCursor.CheckDocument(decoded, envelope.Document, "readback.next_cursor");
                    RevitElementQueryCursor.CheckFilter(decoded, query, "readback.next_cursor");
                }
                catch (RevitCommandException error) { return Fail(RevitCommandCodes.ResponseInvalid, "readback.next_cursor", "next_cursor refused: " + error.Code, pages); }
                // P1-13: a byte cut resumes after the last returned row (and keeps at least one); a limit cut after the last
                // paged id, which is the last returned row on a full page and can only be further on when rows vanished.
                if (reason == RevitElementQueryJson.TruncatedBytes && rows.Count == 0)
                    return Fail(RevitCommandCodes.ResponseInvalid, "readback.elements", "a byte cut keeps at least one row", pages);
                var exact = reason == RevitElementQueryJson.TruncatedBytes || rows.Count == query.Limit;
                if (exact && decoded.After != rows[^1].Id)
                    return Fail(RevitCommandCodes.ResponseInvalid, "readback.next_cursor", "next_cursor must resume after the page's last element", pages);
                if (rows.Count > 0 && decoded.After < rows[^1].Id)
                    return Fail(RevitCommandCodes.ResponseInvalid, "readback.next_cursor", "next_cursor is before the page's last element", pages);
                if (after is { } previous && decoded.After <= previous)
                    return Fail(RevitElementQueryPagingCodes.CursorLoop, "readback.next_cursor", "next_cursor does not move past the cursor id " + previous.ToString(CultureInfo.InvariantCulture), pages);
            }
            pages++;
            elements.AddRange(rows.Select(row => row.Json));
            bytes += rows.Sum(row => (long)Encoding.UTF8.GetByteCount(row.Json));
            if (elements.Count > maxElements || bytes > maxBytes)
                return Fail(RevitElementQueryPagingCodes.ListingTooLarge, "elements", "more than " + maxElements.ToString(CultureInfo.InvariantCulture) + " elements or " + maxBytes.ToString(CultureInfo.InvariantCulture) + " bytes of element JSON", pages);
            if (next is null) return new ElementQueryAllResult(RevitCommandResponse.Succeeded, RevitCommandCodes.Ok, null, null, pages, matched, elements.ToArray());
            if (pages >= maxPages)
                return Fail(RevitElementQueryPagingCodes.PageLimitExceeded, "readback.next_cursor", "more than " + maxPages.ToString(CultureInfo.InvariantCulture) + " pages", pages);
            var request = (JsonObject)template.DeepClone();
            request["args"]!.AsObject()["cursor"] = next;
            try { envelope = RevitCommandProtocol.Parse(request.ToJsonString()); }
            catch (RevitCommandException error) { return Fail(error.Code, error.Field, error.Detail, pages); }
        }
    }

    // The command named by a well-formed envelope when it is not query.elements (null otherwise: Parse decides the rest).
    static string? OtherCommand(string envelopeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(envelopeJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String && command.GetString() != Command ? command.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    static ElementQueryAllResult Fail(string code, string? field, string? detail, int pages) =>
        new(RevitCommandResponse.Failed, code, field, detail, pages, null, Array.Empty<string>());

    // One page: transport, strict reply, success, a receipt answering this request, nothing changed, a readback.
    static (string? Readback, ElementQueryAllResult? Failure) Send(RevitCommandEnvelope envelope, IRevitCommandTransport transport, int pages)
    {
        string? line;
        try { line = transport.Send(envelope.CanonicalJson); }
        catch (RevitPipeReadTimeoutException error) { return (null, Fail(RevitCommandCodes.PipeReadTimeout, null, RevitCommandService.ReadTimeoutDetail(envelope, error.Timeout), pages)); }
        catch (TimeoutException error) { return (null, Fail(RevitCommandCodes.PipeConnectTimeout, null, error.GetType().Name, pages)); }
        // Any other transport failure is pipe_io_error, with only the exception type name (messages vary by culture).
        catch (Exception error) { return (null, Fail(RevitCommandCodes.PipeIoError, null, error.GetType().Name, pages)); }
        if (line is null) return (null, Fail(RevitCommandCodes.NoResponse, null, "the transport returned no reply", pages));
        RevitCommandResponse response;
        try { response = RevitCommandResponse.Parse(line); }
        catch (RevitCommandException error) { return (null, Fail(RevitCommandCodes.ResponseInvalid, error.Field, PlanRunner.MalformedReplyDetail, pages)); }
        if (!response.IsSuccess)
            return (null, response.Code == RevitCommandCodes.Ok ? Fail(RevitCommandCodes.ResponseInvalid, "code", "a failed reply with code ok", pages) : Fail(response.Code, response.Field, response.Detail, pages));
        var receipt = response.Receipt!;
        var mismatch = receipt.OperationId != envelope.OperationId ? "operation_id"
            : receipt.Command != envelope.Command ? "command"
            : receipt.DocumentUniqueId != envelope.Document.UniqueId ? "document_unique_id"
            : receipt.RequestSha256 != envelope.RequestSha256 ? "request_sha256"
            : null;
        if (mismatch is not null) return (null, Fail(RevitCommandCodes.ResponseInvalid, "receipt", "receipt " + mismatch + " does not match the request", pages));
        if (receipt.Replayed) return (null, Fail(RevitCommandCodes.ResponseInvalid, "receipt.replayed", "a query is never replayed", pages));
        if (response.Created.Count > 0 || response.Modified.Count > 0 || response.Deleted.Count > 0)
            return (null, Fail(RevitCommandCodes.ResponseInvalid, "modified", "a query reported changes", pages));
        if (response.ReadbackJson is null) return (null, Fail(RevitCommandCodes.ResponseInvalid, "readback", "query.elements returned no readback", pages));
        return (response.ReadbackJson, null);
    }

    readonly record struct Row(long Id, string Json);

    // The readback of one page, checked against the query and the cursor id it was sent with.
    static ((IReadOnlyList<Row> Rows, int Matched, string? Next, string? Reason)? Page, ElementQueryAllResult? Failure) Page(string readback, ElementQueryArgs query, long? after, int pages)
    {
        ElementQueryAllResult Invalid(string member, string detail) => Fail(RevitCommandCodes.ResponseInvalid, "readback." + member, detail, pages);
        try
        {
            using var document = JsonDocument.Parse(readback);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, Fail(RevitCommandCodes.ResponseInvalid, "readback", "query.elements readback is not an object", pages));
            bool Has(string name, out JsonElement value) => root.TryGetProperty(name, out value);
            string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!Has("category", out var category) || !(category.ValueKind == JsonValueKind.Null ? query.Category is null : Text(category) == query.Category && query.Category is not null))
                return (null, Invalid("category", "not the query's category"));
            if (!Has("built_in_category", out var builtIn) || !(builtIn.ValueKind == JsonValueKind.Null ? query.BuiltInCategory is null : Text(builtIn) == query.BuiltInCategory && query.BuiltInCategory is not null))
                return (null, Invalid("built_in_category", "not the query's category"));
            if (!Has("limit", out var limit) || limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt32(out var limitValue) || limitValue != query.Limit)
                return (null, Invalid("limit", "not the query's limit"));
            if (!Has("matched", out var matched) || matched.ValueKind != JsonValueKind.Number || !matched.TryGetInt32(out var matchedValue) || matchedValue < 0)
                return (null, Invalid("matched", "matched must be a whole, non-negative count"));
            if (!Has("returned", out var returned) || returned.ValueKind != JsonValueKind.Number || !returned.TryGetInt32(out var returnedValue))
                return (null, Invalid("returned", "returned must be a whole count"));
            if (!Has("truncated", out var truncated) || truncated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (null, Invalid("truncated", "truncated must be a boolean"));
            var cut = truncated.GetBoolean();
            if (!Has("truncated_reason", out var reason) || !(cut ? Text(reason) is RevitElementQueryJson.TruncatedLimit or RevitElementQueryJson.TruncatedBytes : reason.ValueKind == JsonValueKind.Null))
                return (null, Invalid("truncated_reason", "truncated_reason must be limit or bytes exactly when truncated"));
            if (!Has("after", out var afterElement) || !(afterElement.ValueKind == JsonValueKind.Null ? after is null
                    : afterElement.ValueKind == JsonValueKind.Number && afterElement.TryGetInt64(out var afterValue) && after == afterValue))
                return (null, Invalid("after", "after must be the cursor's element id (null without a cursor)"));
            if (!Has("next_cursor", out var nextElement) || !(cut ? nextElement.ValueKind == JsonValueKind.String : nextElement.ValueKind == JsonValueKind.Null))
                return (null, Invalid("next_cursor", "next_cursor must be present exactly when truncated"));
            if (!Has("elements", out var list) || list.ValueKind != JsonValueKind.Array)
                return (null, Invalid("elements", "elements must be an array"));
            List<Row> rows = [];
            var last = after;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var idValue))
                    return (null, Invalid("elements", "each element is an object with a whole id"));
                if (last is { } before && idValue <= before)
                    return (null, Invalid("elements", "element ids must ascend strictly after the cursor id"));
                rows.Add(new Row(idValue, item.GetRawText()));
                last = idValue;
            }
            if (rows.Count != returnedValue) return (null, Invalid("returned", "returned is not the element count"));
            if (rows.Count > query.Limit) return (null, Invalid("elements", "more elements than the limit"));
            if (matchedValue < rows.Count) return (null, Invalid("matched", "matched is below returned"));
            return ((rows, matchedValue, cut ? nextElement.GetString() : null, cut ? reason.GetString() : null), null);
        }
        catch (JsonException) { return (null, Fail(RevitCommandCodes.ResponseInvalid, "readback", "query.elements readback is not JSON", pages)); }
    }
}

// The CLI adapter: each page goes through RevitCommandService.Run (explicit process pipe, envelope validation, guards,
// wire size cap, one line on the pipe, a receipt answering the request), so --all pages exactly like single calls do.
public sealed class RevitServiceTransport : IRevitCommandTransport
{
    readonly string pipeName;
    readonly RevitCommandPolicy policy;
    readonly Func<string, string, string?> pipe;

    public RevitServiceTransport(string pipeName, RevitCommandPolicy policy, Func<string, string, string?> pipe)
    {
        this.pipeName = pipeName;
        this.policy = policy;
        this.pipe = pipe;
    }

    public string? Send(string envelopeJson) => RevitCommandService.Run(pipeName, envelopeJson, policy, pipe).Line;
}
