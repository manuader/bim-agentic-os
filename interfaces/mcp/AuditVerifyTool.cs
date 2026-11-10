#nullable enable
using System.Globalization;
using System.Text.Json;
using Bim.Cli;
using Bim.Core;

namespace Bim.Mcp;

// A typed audit-verify refusal: printed as {"status":"failed","code"[,"detail"]}.
internal sealed class AuditVerifyRefusal(string code, string? detail = null) : Exception(code)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}

// P10-12: the read-only engine behind the CLI verb `audit-verify` (interfaces/cli/AuditVerifyCommand.cs, P10-11) and the
// MCP tool audit_verify (McpServer), whose text equals the CLI stdout line byte for byte for the same log, anchor, actor
// set and summary flag. It reads the log (BoundedInput.ReadBytes, MaxLogBytes; a byte order mark is not stripped) and the
// optional anchor (BoundedInput.ReadText, 8 MiB), parses the anchor strictly (ParseAnchor) and runs AuditVerifier.VerifyBytes
// (core/Bim.Core/AuditVerify.cs); the text is AuditVerifyReport.ToJson() or, with summary, AuditSummary.ToJson of the
// verified entries. It never writes: --write-anchor stays CLI-only (AuditVerifyCommand).
// Anchor file: {"kind":"audit-anchor","version":1,"sequence":<integer >= 0>,"hash":"<64 lowercase hex>"}, read strictly:
// exactly those four members (any order, insignificant whitespace, a BOM tolerated), no duplicates, kind and version
// exact, sequence a non-negative 64-bit integer, hash 64 lowercase hex; otherwise audit_verify_anchor_invalid.
// MCP-only refusals (Run): log_path / anchor_path blank, with NUL, over MaxPathChars or rejected by Path.GetFullPath:
// audit_verify_path_invalid (detail "log" or "anchor"); actors empty, or an item that is not a string or not
// PermissionPolicy.IsActor: audit_verify_actors_invalid. Then input_* (BoundedInput; a missing log is input_not_found),
// audit_verify_anchor_invalid, and audit_verify_internal_error for a defect (exception type on stderr).
// Precedence (MCP): log path, anchor path, actors, log read, anchor read, anchor parse, verification.
internal static class AuditVerifyTool
{
    public const string ActorsInvalid = "audit_verify_actors_invalid";
    public const string AnchorInvalid = "audit_verify_anchor_invalid";
    public const string PathInvalid = "audit_verify_path_invalid";
    public const string InternalError = "audit_verify_internal_error";
    public const string AnchorKind = "audit-anchor";
    public const int AnchorVersion = 1;
    // The log may outgrow the 8 MiB JSON input cap; 64 MiB is roughly 150,000 entries.
    public const int MaxLogBytes = 64 * 1024 * 1024;
    public const int MaxPathChars = 4096;

    public sealed record Outcome(bool Succeeded, string Text);

    public sealed record Verified(AuditVerifyReport Report, string Text);

    public static string Failure(string code, string? detail) =>
        detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });

    // The known actor set, ordinal and without repeats; any item that is null or not PermissionPolicy.IsActor is refused
    // as audit_verify_actors_invalid with the caller's detail.
    public static IReadOnlyCollection<string> Actors(IEnumerable<string?> items, string detail)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var actor in items)
        {
            if (actor is null || !PermissionPolicy.IsActor(actor)) throw new AuditVerifyRefusal(ActorsInvalid, detail);
            set.Add(actor);
        }
        return set.ToArray();
    }

    // Reads the log and the optional anchor and verifies; refusals throw AuditVerifyRefusal. Never writes.
    public static Verified VerifyFiles(string logPath, string? anchorPath, IReadOnlyCollection<string>? actors, bool summary)
    {
        byte[] bytes;
        string? anchorText = null;
        try
        {
            bytes = BoundedInput.ReadBytes(logPath, MaxLogBytes);
            if (anchorPath is not null) anchorText = BoundedInput.ReadText(anchorPath);
        }
        catch (InputRejectedException error) { throw new AuditVerifyRefusal(error.Message); }
        AuditAnchor? anchor = anchorText is null ? null : ParseAnchor(anchorText);

        var report = AuditVerifier.VerifyBytes(bytes, new AuditVerifyOptions(actors, anchor));
        return new Verified(report, summary ? AuditSummary.ToJson(report.VerifiedEntries) : report.ToJson());
    }

    public static string McpActorsDetail => "actors: a non-empty array of actor ids, at most " + PermissionPolicy.MaxActorChars.ToString(CultureInfo.InvariantCulture) + " characters each, no control characters";

    // The MCP tool: succeeded exactly when the report has no finding (the CLI exit 0).
    public static Outcome Run(string logPath, string? anchorPath, IReadOnlyList<string?>? actors, bool summary)
    {
        try
        {
            if (PathProblem(logPath)) return new Outcome(false, Failure(PathInvalid, "log"));
            if (anchorPath is not null && PathProblem(anchorPath)) return new Outcome(false, Failure(PathInvalid, "anchor"));
            IReadOnlyCollection<string>? known = null;
            if (actors is not null)
            {
                if (actors.Count == 0) throw new AuditVerifyRefusal(ActorsInvalid, McpActorsDetail);
                known = Actors(actors, McpActorsDetail);
            }
            var verified = VerifyFiles(logPath, anchorPath, known, summary);
            return new Outcome(verified.Report.Ok, verified.Text);
        }
        catch (AuditVerifyRefusal refusal) { return new Outcome(false, Failure(refusal.Code, refusal.Detail)); }
        catch (Exception error)
        {
            Console.Error.WriteLine("audit_verify internal error: " + error.GetType().FullName + ": " + error.Message);
            return new Outcome(false, Failure(InternalError, null));
        }
    }

    static bool PathProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Length > MaxPathChars) return true;
        try { Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return true; }
        return false;
    }

    public static AuditAnchor ParseAnchor(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { throw new AuditVerifyRefusal(AnchorInvalid, "not JSON"); }
        catch (ArgumentException) { throw new AuditVerifyRefusal(AnchorInvalid, "not JSON"); }
        // A lone escaped surrogate ("\ud800") in a member name or string value is valid JSON but not text: reading it
        // throws InvalidOperationException, which is refused like any other malformed anchor.
        try { return ReadAnchor(document); }
        catch (InvalidOperationException) { throw new AuditVerifyRefusal(AnchorInvalid, "member names and strings must be valid text"); }
        finally { document.Dispose(); }
    }

    static AuditAnchor ReadAnchor(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AuditVerifyRefusal(AnchorInvalid, "a JSON object required");
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("kind" or "version" or "sequence" or "hash")) throw new AuditVerifyRefusal(AnchorInvalid, "unknown member " + property.Name);
            if (!members.TryAdd(property.Name, property.Value)) throw new AuditVerifyRefusal(AnchorInvalid, "duplicate member " + property.Name);
        }
        if (members.Count != 4) throw new AuditVerifyRefusal(AnchorInvalid, "members kind, version, sequence and hash required");
        if (members["kind"].ValueKind != JsonValueKind.String || members["kind"].GetString() != AnchorKind) throw new AuditVerifyRefusal(AnchorInvalid, "kind must be \"" + AnchorKind + "\"");
        if (members["version"].ValueKind != JsonValueKind.Number || !members["version"].TryGetInt32(out var version) || version != AnchorVersion) throw new AuditVerifyRefusal(AnchorInvalid, "version must be 1");
        if (members["sequence"].ValueKind != JsonValueKind.Number || !members["sequence"].TryGetInt64(out var sequence) || sequence < 0) throw new AuditVerifyRefusal(AnchorInvalid, "sequence must be a non-negative integer");
        var hash = members["hash"].ValueKind == JsonValueKind.String ? members["hash"].GetString() : null;
        if (hash is null || hash.Length != 64 || !hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) throw new AuditVerifyRefusal(AnchorInvalid, "hash must be 64 lowercase hex characters");
        return new AuditAnchor(sequence, hash);
    }
}
