#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P10-11 `audit-verify log.jsonl [--anchor head.json] [--actors a,b] [--summary] [--write-anchor head.json]`: the P10-09
// verifier (core/Bim.Core/AuditVerify.cs) over a P10-03 audit log, with an optional head anchor kept outside the log.
// The log is only read (its raw bytes, BoundedInput.ReadBytes with MaxLogBytes; a byte order mark is not stripped, so
// it is reported as the verifier reports it). Options in any order, each at most once, values non-empty and not
// starting with "--"; exactly one positional log path (also not "--..."); anything else is audit_verify_usage.
//   --anchor head.json      an audit-anchor file (below), read through BoundedInput.ReadText (8 MiB) and passed as
//                           AuditVerifyOptions.Anchor: tail truncation and a whole-file rehash become detectable.
//   --actors a,b            the known actor set (AuditVerifyOptions.KnownActors): comma-separated, items verbatim (no
//                           trimming), each PermissionPolicy.IsActor; repeats ignored. An actor containing a comma
//                           cannot be named. Empty items or invalid actors: audit_verify_actors_invalid.
//   --summary               print AuditSummary.ToJson(report.VerifiedEntries) instead of the report.
//   --write-anchor h.json   after a report with no finding, write the head (last verified entry, or sequence 0 with the
//                           genesis hash for an empty log) to a new file; never overwrites (output_exists). Nothing is
//                           written when the report has any finding.
// Anchor file (new CLI-side format, UTF-8 without BOM, compact, LF-terminated when written):
//   {"kind":"audit-anchor","version":1,"sequence":<integer >= 0>,"hash":"<64 lowercase hex>"}
// Read strictly: exactly those four members (any order, insignificant whitespace, a BOM tolerated), no duplicates, kind and
// version exact, sequence a non-negative 64-bit integer, hash 64 lowercase hex; otherwise audit_verify_anchor_invalid.
// A sequence-0 anchor whose hash is not the genesis hash is passed on and reported by the verifier (audit_anchor_mismatch).
// Output: stdout is AuditVerifyReport.ToJson() (or the summary) byte for byte; exit 0 when the report has no finding,
// exit 1 on any finding. Refusals print {"status":"failed","code"[,"detail"]} and exit 1: audit_verify_usage (detail: the
// usage text), audit_verify_actors_invalid, output_* (GltfExportCommand.OutputProblem with .json, output_exists,
// output_io_error), input_* (BoundedInput; a missing log is input_not_found, never audit_file_missing),
// audit_verify_anchor_invalid, audit_verify_internal_error (a defect: exception on stderr).
// Precedence: usage, actors, write-anchor path, log read, anchor read, anchor parse, verification, anchor write.
internal static class AuditVerifyCommand
{
    public const string Usage = "usage: audit-verify log.jsonl [--anchor head.json] [--actors a,b] [--summary] [--write-anchor head.json]";
    public const string UsageCode = "audit_verify_usage";
    public const string ActorsInvalid = "audit_verify_actors_invalid";
    public const string AnchorInvalid = "audit_verify_anchor_invalid";
    public const string InternalError = "audit_verify_internal_error";
    public const string AnchorKind = "audit-anchor";
    public const int AnchorVersion = 1;
    // The log may outgrow the 8 MiB JSON input cap; 64 MiB is roughly 150,000 entries.
    public const int MaxLogBytes = 64 * 1024 * 1024;

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
        catch (Exception error)
        {
            Console.Error.WriteLine("audit-verify internal error: " + error.GetType().FullName + ": " + error.Message);
            Fail(InternalError, null);
        }
    }

    static void Execute(string[] args)
    {
        string? logPath = null, anchorPath = null, actorsText = null, writePath = null;
        var summary = false;
        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--summary":
                    if (summary) throw new Refusal(UsageCode, Usage);
                    summary = true;
                    break;
                case "--anchor": anchorPath = Value(args, ref i, anchorPath); break;
                case "--actors": actorsText = Value(args, ref i, actorsText); break;
                case "--write-anchor": writePath = Value(args, ref i, writePath); break;
                default:
                    if (arg.Length == 0 || arg.StartsWith("--", StringComparison.Ordinal) || logPath is not null) throw new Refusal(UsageCode, Usage);
                    logPath = arg;
                    break;
            }
        }
        if (logPath is null) throw new Refusal(UsageCode, Usage);

        IReadOnlyCollection<string>? actors = null;
        if (actorsText is not null)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var actor in actorsText.Split(','))
            {
                if (!PermissionPolicy.IsActor(actor)) throw new Refusal(ActorsInvalid, "--actors: comma-separated actor ids, no empty items, at most " + PermissionPolicy.MaxActorChars.ToString(CultureInfo.InvariantCulture) + " characters each, no control characters");
                set.Add(actor);
            }
            actors = set.ToArray();
        }

        if (writePath is not null && GltfExportCommand.OutputProblem(writePath, [".json"], out _) is string refused) throw new Refusal(refused);

        byte[] bytes;
        string? anchorText = null;
        try
        {
            bytes = BoundedInput.ReadBytes(logPath, MaxLogBytes);
            if (anchorPath is not null) anchorText = BoundedInput.ReadText(anchorPath);
        }
        catch (InputRejectedException error) { throw new Refusal(error.Message); }
        var anchor = anchorText is null ? null : ParseAnchor(anchorText);

        var report = AuditVerifier.VerifyBytes(bytes, new AuditVerifyOptions(actors, anchor));
        var printed = summary ? AuditSummary.ToJson(report.VerifiedEntries) : report.ToJson();

        if (writePath is not null && report.Ok)
        {
            var head = report.VerifiedEntries.Count == 0 ? new AuditAnchor(0, AuditLogFormat.GenesisHash) : new AuditAnchor(report.VerifiedEntries[^1].Seq, report.VerifiedEntries[^1].Hash);
            try { GltfExporter.WriteNew(AnchorBytes(head), writePath); }
            catch (GltfExportException e) { throw new Refusal(e.Code); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", e.GetType().Name); }
        }

        Console.WriteLine(printed);
        if (!report.Ok) Environment.ExitCode = 1;
    }

    static string Value(string[] args, ref int i, string? existing)
    {
        if (existing is not null || i + 1 >= args.Length) throw new Refusal(UsageCode, Usage);
        var value = args[++i];
        if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) throw new Refusal(UsageCode, Usage);
        return value;
    }

    // {"kind":"audit-anchor","version":1,"sequence":n,"hash":"..."} + LF, UTF-8 without BOM.
    internal static byte[] AnchorBytes(AuditAnchor head)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", AnchorKind);
            writer.WriteNumber("version", AnchorVersion);
            writer.WriteNumber("sequence", head.Seq);
            writer.WriteString("hash", head.Hash);
            writer.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    static AuditAnchor ParseAnchor(string text)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { throw new Refusal(AnchorInvalid, "not JSON"); }
        catch (ArgumentException) { throw new Refusal(AnchorInvalid, "not JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new Refusal(AnchorInvalid, "a JSON object required");
            var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name is not ("kind" or "version" or "sequence" or "hash")) throw new Refusal(AnchorInvalid, "unknown member " + property.Name);
                if (!members.TryAdd(property.Name, property.Value)) throw new Refusal(AnchorInvalid, "duplicate member " + property.Name);
            }
            if (members.Count != 4) throw new Refusal(AnchorInvalid, "members kind, version, sequence and hash required");
            if (members["kind"].ValueKind != JsonValueKind.String || members["kind"].GetString() != AnchorKind) throw new Refusal(AnchorInvalid, "kind must be \"" + AnchorKind + "\"");
            if (members["version"].ValueKind != JsonValueKind.Number || !members["version"].TryGetInt32(out var version) || version != AnchorVersion) throw new Refusal(AnchorInvalid, "version must be 1");
            if (members["sequence"].ValueKind != JsonValueKind.Number || !members["sequence"].TryGetInt64(out var sequence) || sequence < 0) throw new Refusal(AnchorInvalid, "sequence must be a non-negative integer");
            var hash = members["hash"].ValueKind == JsonValueKind.String ? members["hash"].GetString() : null;
            if (hash is null || hash.Length != 64 || !hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) throw new Refusal(AnchorInvalid, "hash must be 64 lowercase hex characters");
            return new AuditAnchor(sequence, hash);
        }
    }

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
