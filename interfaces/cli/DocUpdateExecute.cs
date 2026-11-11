#nullable enable
using System.Text.Json.Nodes;
using Bim.Core;

namespace Bim.Cli;

// P4-12 `doc-update ... --probe-dependents process-pipe-name [--accept uid]... --execute --expected-document <sha256>`:
// the run half of the doc-update verb. DocUpdateCommand plans with the P4-10 probe (DocUpdateProbe), refuses
// doc_update_plan_changed (field "expected_document", detail the SHA-256 of the plan it made) unless that plan's bytes
// hash to --expected-document (the "sha256" a dry run printed for the plan a human reviewed; hex, any case), writes the
// plan file exactly as a dry run does, and only then calls Run here, on the same channel the dry runs used (the
// enabled-roots policy is read once for both, every request goes through RevitCommandService.Run with its guards).
// Run sends the plan once with DocUpdate.Run: PlanRunner sends DocReplace's steps in order (every element.delete with
// exactly the confirm_dependents the confirmed dry runs reported, children before parents, then every create, parents
// before children, created[0] of earlier steps filled in) and stops at the first failure. Nothing is ever retried or
// resumed here: a write whose reply was lost (no_response, pipe_io_error) is reported failed with reply_lost true and is
// never sent again (a later run replays it by operation id only if the add-in stored its receipt). The one re-run is
// the library's: a stale delete answered reference_not_found on its target is already gone in Revit, so DocUpdate drops
// that key ("gone") and re-runs; every step that already ran replays from its receipt (nothing is executed twice).
// Output, one line: the dry summary members (output, bytes, sha256, regenerated, steps, forced, retired, probed,
// dependents), then "applied", "run" (the report status), "gone" (keys) and "report", one entry per step of the plan
// that ran, in run order: {"operation_id","key","command","status":"done","replayed","created"} |
// {"operation_id","key","command","status":"failed","code","field","detail","reply_lost"} |
// {"operation_id","key","command","status":"not_sent"}. A completed run prints status "succeeded", applied true and
// "realized" (the updated realized map, sorted), exit 0. A failed run prints {"status":"failed","code"[,"key"]
// [,"operation_id"][,"field"][,"detail"], ...} with the failed step's code, then the same members with applied false,
// exit 1 (the plan file stays: it is what was being run). An exception out of the library after writes may have been
// sent is doc_update_internal_error (type and message on stderr) with applied false and no report.
internal static class DocUpdateExecute
{
    public const string PlanChanged = "doc_update_plan_changed";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string NotSent = "not_sent";

    // The request may have reached Revit and run: only the reply is missing.
    static readonly string[] LostReplyCodes = { RevitCommandCodes.NoResponse, RevitCommandCodes.PipeIoError };

    // plan: the probed plan whose bytes were written; summaryLine: the dry summary of that plan (JSON object).
    public static int Run(DocUpdatePlan plan, IRevitCommandTransport channel, string summaryLine, TextWriter stdout)
    {
        var summary = JsonNode.Parse(summaryLine) as JsonObject ?? throw new InvalidOperationException("summary is not an object");
        DocUpdateResult result;
        try { result = DocUpdate.Run(plan, channel); }
        catch (Exception error)
        {
            Console.Error.WriteLine("doc-update internal error: " + error.GetType().FullName + ": " + error.Message);
            var internalError = new JsonObject { ["status"] = Failed, ["code"] = DocUpdateCommand.InternalError };
            Copy(summary, internalError);
            internalError["applied"] = false;
            stdout.WriteLine(internalError.ToJsonString());
            return 1;
        }

        var report = result.Report;
        var completed = report.Status == RevitPlanRunStatus.Completed && report.Pending.Count == 0 && result.Realized is not null;
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in result.Plan.Replacement.Replacements)
        {
            if (item.DeleteOperation is { } deleteOperation) keys[deleteOperation] = item.Key;
            if (item.CreateOperation is { } createOperation) keys[createOperation] = item.Key;
        }
        string? KeyOf(string operation) => keys.TryGetValue(operation, out var key) ? key : null;

        var done = new Dictionary<string, RevitCompletedStep>(StringComparer.Ordinal);
        foreach (var step in report.Completed) done[step.OperationId] = step;
        var entries = new JsonArray();
        foreach (var step in result.Plan.Replacement.Commands.Steps)
        {
            var entry = new JsonObject { ["operation_id"] = step.OperationId, ["key"] = KeyOf(step.OperationId), ["command"] = step.Command };
            if (done.TryGetValue(step.OperationId, out var ran))
            {
                entry["status"] = Done;
                entry["replayed"] = ran.Replayed;
                entry["created"] = Strings(ran.Created);
            }
            else if (report.Failed is { } stopped && stopped.OperationId == step.OperationId)
            {
                entry["status"] = Failed;
                entry["code"] = stopped.Code;
                entry["field"] = stopped.Field;
                entry["detail"] = stopped.Detail;
                entry["reply_lost"] = LostReplyCodes.Contains(stopped.Code, StringComparer.Ordinal);
            }
            else entry["status"] = NotSent;
            entries.Add(entry);
        }

        JsonObject line;
        if (completed)
        {
            line = new JsonObject { ["status"] = "succeeded" };
            Copy(summary, line);
        }
        else
        {
            line = new JsonObject { ["status"] = Failed, ["code"] = report.Failed?.Code ?? report.Status };
            if (report.Failed is { } failure)
            {
                if (KeyOf(failure.OperationId) is { } key) line["key"] = key;
                line["operation_id"] = failure.OperationId;
                if (failure.Field is not null) line["field"] = failure.Field;
                if (failure.Detail is not null) line["detail"] = failure.Detail;
            }
            Copy(summary, line);
        }
        line["applied"] = completed;
        line["run"] = report.Status;
        line["gone"] = Strings(result.Plan.Gone);
        line["report"] = entries;
        if (completed && result.Realized is { } realized)
        {
            var map = new JsonObject();
            foreach (var pair in realized.OrderBy(pair => pair.Key, StringComparer.Ordinal)) map[pair.Key] = pair.Value;
            line["realized"] = map;
        }
        stdout.WriteLine(line.ToJsonString());
        return completed ? 0 : 1;
    }

    // The dry summary members except status and applied, in their order.
    static void Copy(JsonObject summary, JsonObject into)
    {
        foreach (var pair in summary)
            if (pair.Key is not "status" and not "applied") into[pair.Key] = pair.Value?.DeepClone();
    }

    static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }
}
