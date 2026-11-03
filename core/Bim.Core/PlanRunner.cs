using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P3-03: runs a RevitCommandPlan (PlanToCommands.Translate) one step at a time through an injected transport. The
// level preconditions are checked first with the plan's own query.levels step; then each step's envelope is built by
// PlanToCommands.Envelope with hosts filled from earlier created[0], sent, and its reply verified. The first failure
// stops the run and the report says what completed, what failed and what is pending, so a resume sends only pending
// steps; operation_id receipts make a resent step that already ran a replay. Contract:
// docs/contracts/module-contracts.md, "Plan runner (P3-03)".

// One request out, one reply back. envelopeJson is a canonical protocol envelope (RevitCommandEnvelope.CanonicalJson);
// the reply is the executor's RevitCommandResponse JSON, or null when no reply came. RevitCommandService.Run(pipe,
// envelopeJson, policy, RevitCommandService.PipeTransport).Line has this shape.
public interface IRevitCommandTransport
{
    string? Send(string envelopeJson);
}

public static class RevitPlanRunStatus
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string PreconditionFailed = "precondition_failed";
}

public static class PlanRunnerCodes
{
    // A write answered with an empty created list while a later step takes its created[0].
    public const string CreatedMissing = "created_missing";
}

// A step whose reply succeeded (Replayed: the executor answered from its receipt store and wrote nothing).
public sealed record RevitCompletedStep(string OperationId, string Command, Guid? ElementId, IReadOnlyList<string> Created, bool Replayed, IReadOnlyList<string> Warnings);

public sealed record RevitFailedStep(string OperationId, string Command, Guid? ElementId, string Code, string? Field, string? Detail);

// The query.levels step that was read back and the precondition issues it showed (empty when all hold).
public sealed record RevitLevelsCheck(string OperationId, IReadOnlyList<RevitPreconditionIssue> Issues);

// Completed is a prefix of the plan's steps (the query.levels check excluded) and Pending the rest, in plan order;
// a failed step is Pending[0] (or the check step, then every step is pending).
public sealed record RevitPlanRunReport(int Version, string PlanSha256, string Status, RevitLevelsCheck? LevelsCheck,
    IReadOnlyList<RevitCompletedStep> Completed, RevitFailedStep? Failed, IReadOnlyList<string> Pending)
{
    static readonly string[] RootMembers = { "runner_version", "plan_sha256", "status", "levels_check", "completed", "failed", "pending" };
    static readonly string[] CheckMembers = { "operation_id", "issues" };
    static readonly string[] IssueMembers = { "element_id", "level", "code", "detail" };
    static readonly string[] CompletedMembers = { "operation_id", "command", "element_id", "created", "replayed", "warnings" };
    static readonly string[] FailedMembers = { "operation_id", "command", "element_id", "code", "field", "detail" };

    // Fixed member order, compact, default escaping: the same plan and replies give byte-identical text.
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("runner_version", Version);
            writer.WriteString("plan_sha256", PlanSha256);
            writer.WriteString("status", Status);
            writer.WritePropertyName("levels_check");
            if (LevelsCheck is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                writer.WriteString("operation_id", LevelsCheck.OperationId);
                writer.WriteStartArray("issues");
                foreach (var issue in LevelsCheck.Issues)
                {
                    writer.WriteStartObject();
                    writer.WriteString("element_id", issue.ElementId.ToString("D"));
                    writer.WriteString("level", issue.Level);
                    writer.WriteString("code", issue.Code);
                    writer.WriteString("detail", issue.Detail);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteStartArray("completed");
            foreach (var step in Completed)
            {
                writer.WriteStartObject();
                writer.WriteString("operation_id", step.OperationId);
                writer.WriteString("command", step.Command);
                WriteNullable(writer, "element_id", step.ElementId?.ToString("D"));
                WriteArray(writer, "created", step.Created);
                writer.WriteBoolean("replayed", step.Replayed);
                WriteArray(writer, "warnings", step.Warnings);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("failed");
            if (Failed is null) writer.WriteNullValue();
            else
            {
                writer.WriteStartObject();
                writer.WriteString("operation_id", Failed.OperationId);
                writer.WriteString("command", Failed.Command);
                WriteNullable(writer, "element_id", Failed.ElementId?.ToString("D"));
                writer.WriteString("code", Failed.Code);
                WriteNullable(writer, "field", Failed.Field);
                WriteNullable(writer, "detail", Failed.Detail);
                writer.WriteEndObject();
            }
            WriteArray(writer, "pending", Pending);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Strict reader of ToJson(): every member present, no other member, types as written. Anything else is
    // ArgumentException("report_invalid"). Whether the report fits a plan is checked by PlanRunner.Resume.
    public static RevitPlanRunReport Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = Members(document.RootElement, RootMembers);
            var version = root.GetProperty("runner_version");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var runnerVersion)) throw Invalid();
            var status = Text(root.GetProperty("status"));
            if (status != RevitPlanRunStatus.Completed && status != RevitPlanRunStatus.Failed && status != RevitPlanRunStatus.PreconditionFailed) throw Invalid();
            RevitLevelsCheck? check = null;
            var checkElement = root.GetProperty("levels_check");
            if (checkElement.ValueKind != JsonValueKind.Null)
            {
                Members(checkElement, CheckMembers);
                var issues = Items(checkElement.GetProperty("issues")).Select(item =>
                {
                    Members(item, IssueMembers);
                    return new RevitPreconditionIssue(Id(item.GetProperty("element_id")), Text(item.GetProperty("level")), Text(item.GetProperty("code")), Text(item.GetProperty("detail")));
                }).ToArray();
                check = new RevitLevelsCheck(Text(checkElement.GetProperty("operation_id")), issues);
            }
            var completed = Items(root.GetProperty("completed")).Select(item =>
            {
                Members(item, CompletedMembers);
                var replayed = item.GetProperty("replayed");
                if (replayed.ValueKind != JsonValueKind.True && replayed.ValueKind != JsonValueKind.False) throw Invalid();
                return new RevitCompletedStep(Text(item.GetProperty("operation_id")), Text(item.GetProperty("command")), NullableId(item.GetProperty("element_id")),
                    Texts(item.GetProperty("created")), replayed.GetBoolean(), Texts(item.GetProperty("warnings")));
            }).ToArray();
            RevitFailedStep? failed = null;
            var failedElement = root.GetProperty("failed");
            if (failedElement.ValueKind != JsonValueKind.Null)
            {
                Members(failedElement, FailedMembers);
                failed = new RevitFailedStep(Text(failedElement.GetProperty("operation_id")), Text(failedElement.GetProperty("command")), NullableId(failedElement.GetProperty("element_id")),
                    Text(failedElement.GetProperty("code")), NullableText(failedElement.GetProperty("field")), NullableText(failedElement.GetProperty("detail")));
            }
            return new RevitPlanRunReport(runnerVersion, Text(root.GetProperty("plan_sha256")), status, check, completed, failed, Texts(root.GetProperty("pending")));
        }
        catch (JsonException) { throw Invalid(); }
        catch (InvalidOperationException) { throw Invalid(); }
        catch (FormatException) { throw Invalid(); }
    }

    static ArgumentException Invalid() => new("report_invalid");

    static JsonElement Members(JsonElement element, string[] members)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !members.Contains(property.Name, StringComparer.Ordinal)) throw Invalid();
        if (seen.Count != members.Length) throw Invalid();
        return element;
    }

    static IEnumerable<JsonElement> Items(JsonElement element) => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToArray() : throw Invalid();

    static string Text(JsonElement element) => element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text ? text : throw Invalid();

    static string? NullableText(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.ValueKind == JsonValueKind.String ? element.GetString() : throw Invalid();

    static string[] Texts(JsonElement element) => Items(element).Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Invalid()).ToArray();

    static Guid Id(JsonElement element) => Guid.ParseExact(Text(element), "D");

    static Guid? NullableId(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : Id(element);

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

public static class PlanRunner
{
    public const int Version = 1;

    // Lowercase hex SHA-256 of the plan's UTF-8 ToJson(): a report resumes only the plan it was made from.
    public static string PlanSha256(RevitCommandPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.ToJson()))).ToLowerInvariant();
    }

    public static RevitPlanRunReport Run(RevitCommandPlan plan, IRevitCommandTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _ = Layout.Of(plan);
        throw new NotImplementedException("P3-03 red stub");
    }

    // Re-runs the level check (the document may have changed since) and then only the pending steps; completed steps
    // are never resent. A report from another plan is report_plan_mismatch; one that does not fit its plan (steps out
    // of order or missing, a status that contradicts the rest, a completed step whose created[0] a pending step needs
    // but that has none) is report_invalid. A completed report is returned as is, without sending anything.
    public static RevitPlanRunReport Resume(RevitCommandPlan plan, RevitPlanRunReport report, IRevitCommandTransport transport)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(transport);
        var layout = Layout.Of(plan);
        var sha = PlanSha256(plan);
        if (report.Version != Version || report.Completed is null || report.Pending is null) throw new ArgumentException("report_invalid");
        if (report.PlanSha256 != sha) throw new ArgumentException("report_plan_mismatch");
        Validate(layout, report);
        throw new NotImplementedException("P3-03 red stub");
    }

    sealed record Layout(RevitCommandStep? Check, IReadOnlyList<RevitCommandStep> Steps, IReadOnlySet<string> Needed)
    {
        // The check step is the query.levels step every precondition names; the other steps run in plan order.
        public static Layout Of(RevitCommandPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (plan.Steps.Select(step => step.OperationId).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count) throw new ArgumentException("plan_duplicate_operation");
            RevitCommandStep? check = null;
            if (plan.Preconditions.Count > 0)
            {
                var ids = plan.Preconditions.Select(item => item.CheckOperation).Distinct().ToArray();
                check = ids.Length == 1 && ids[0] is { } id ? plan.Steps.FirstOrDefault(step => step.OperationId == id && step.Command == "query.levels") : null;
                if (check is null) throw new ArgumentException("levels_check_missing");
            }
            var steps = plan.Steps.Where(step => !ReferenceEquals(step, check)).ToArray();
            var needed = steps.SelectMany(step => step.Inputs).Select(input => input.FromOperation).ToHashSet(StringComparer.Ordinal);
            return new Layout(check, steps, needed);
        }
    }

    static void Validate(Layout layout, RevitPlanRunReport report)
    {
        static ArgumentException Invalid() => new("report_invalid");
        if (report.Completed.Count + report.Pending.Count != layout.Steps.Count) throw Invalid();
        for (var i = 0; i < report.Completed.Count; i++)
        {
            var done = report.Completed[i];
            var step = layout.Steps[i];
            if (done is null || done.OperationId != step.OperationId || done.Command != step.Command || done.ElementId != step.ElementId || done.Created is null || done.Warnings is null) throw Invalid();
            if (done.Created.Any(uid => !RevitCommandProtocol.IsUniqueId(uid))) throw Invalid();
            if (done.Created.Count == 0 && layout.Needed.Contains(done.OperationId)) throw Invalid();
        }
        for (var i = 0; i < report.Pending.Count; i++)
            if (report.Pending[i] != layout.Steps[report.Completed.Count + i].OperationId) throw Invalid();
        var consistent = report.Status switch
        {
            RevitPlanRunStatus.Completed => report.Pending.Count == 0 && report.Failed is null,
            RevitPlanRunStatus.Failed => report.Pending.Count > 0 && report.Failed is { } failed
                && (failed.OperationId == report.Pending[0] || failed.OperationId == layout.Check?.OperationId),
            RevitPlanRunStatus.PreconditionFailed => report.Pending.Count > 0 && report.Failed is null && report.LevelsCheck is { Issues.Count: > 0 },
            _ => false,
        };
        if (!consistent) throw Invalid();
    }

    static RevitPlanRunReport Execute(RevitCommandPlan plan, Layout layout, string sha, IReadOnlyList<RevitCompletedStep> done, IRevitCommandTransport transport)
    {
        var completed = new List<RevitCompletedStep>(done);
        var created = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in done.Where(step => step.Created.Count > 0)) created[step.OperationId] = step.Created[0];
        RevitLevelsCheck? levels = null;
        RevitPlanRunReport Report(string status, RevitFailedStep? failed) =>
            new(Version, sha, status, levels, completed.ToArray(), failed, layout.Steps.Skip(completed.Count).Select(step => step.OperationId).ToArray());

        if (layout.Check is { } check)
        {
            var (response, failure) = Send(plan, check, created, transport);
            if (failure is not null) return Report(RevitPlanRunStatus.Failed, failure);
            if (response!.ReadbackJson is not { } readback) return Report(RevitPlanRunStatus.Failed, Fail(check, RevitCommandCodes.ResponseInvalid, "readback", "query.levels returned no readback"));
            IReadOnlyList<RevitPreconditionIssue> issues;
            try { issues = PlanToCommands.CheckPreconditions(plan, readback); }
            catch (RevitCommandException error) { return Report(RevitPlanRunStatus.Failed, Fail(check, error.Code, error.Field, error.Detail)); }
            levels = new RevitLevelsCheck(check.OperationId, issues.ToArray());
            if (issues.Count > 0) return Report(RevitPlanRunStatus.PreconditionFailed, null);
        }

        for (var i = completed.Count; i < layout.Steps.Count; i++)
        {
            var step = layout.Steps[i];
            var (response, failure) = Send(plan, step, created, transport);
            if (failure is not null) return Report(RevitPlanRunStatus.Failed, failure);
            var reply = response!;
            if (reply.Created.Count == 0 && layout.Needed.Contains(step.OperationId))
                return Report(RevitPlanRunStatus.Failed, Fail(step, PlanRunnerCodes.CreatedMissing, "created", "a later step takes created[0] of this step"));
            completed.Add(new RevitCompletedStep(step.OperationId, step.Command, step.ElementId, reply.Created.ToArray(), reply.Receipt!.Replayed, reply.Warnings.ToArray()));
            if (reply.Created.Count > 0) created[step.OperationId] = reply.Created[0];
        }
        return Report(RevitPlanRunStatus.Completed, null);
    }

    // One step: envelope, transport, strict reply parse, and a receipt that answers this very request (operation_id,
    // command, document UniqueId and request SHA-256), as RevitCommandService checks it.
    static (RevitCommandResponse? Response, RevitFailedStep? Failure) Send(RevitCommandPlan plan, RevitCommandStep step, IReadOnlyDictionary<string, string> created, IRevitCommandTransport transport)
    {
        RevitCommandEnvelope envelope;
        try { envelope = PlanToCommands.Envelope(plan, step, created); }
        catch (RevitCommandException error) { return (null, Fail(step, error.Code, error.Field, error.Detail)); }
        catch (ArgumentException error) { return (null, Fail(step, error.Message, null, null)); }
        string? line;
        try { line = transport.Send(envelope.CanonicalJson); }
        catch (TimeoutException error) { return (null, Fail(step, RevitCommandCodes.PipeConnectTimeout, null, error.GetType().Name)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (null, Fail(step, RevitCommandCodes.PipeIoError, null, error.GetType().Name)); }
        if (line is null) return (null, Fail(step, RevitCommandCodes.NoResponse, null, "the transport returned no reply"));
        RevitCommandResponse response;
        try { response = RevitCommandResponse.Parse(line); }
        catch (RevitCommandException error) { return (null, Fail(step, RevitCommandCodes.ResponseInvalid, error.Field, error.Detail)); }
        if (!response.IsSuccess) return (null, Fail(step, response.Code, response.Field, response.Detail));
        var receipt = response.Receipt!;
        var mismatch = receipt.OperationId != envelope.OperationId ? "operation_id"
            : receipt.Command != envelope.Command ? "command"
            : receipt.DocumentUniqueId != envelope.Document.UniqueId ? "document_unique_id"
            : receipt.RequestSha256 != envelope.RequestSha256 ? "request_sha256"
            : null;
        return mismatch is null ? (response, null) : (null, Fail(step, RevitCommandCodes.ResponseInvalid, "receipt", "receipt " + mismatch + " does not match the request"));
    }

    static RevitFailedStep Fail(RevitCommandStep step, string code, string? field, string? detail) => new(step.OperationId, step.Command, step.ElementId, code, field, detail);
}
