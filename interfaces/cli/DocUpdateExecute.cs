#nullable enable
using System.Text.Json.Nodes;
using Bim.Core;

namespace Bim.Cli;

// P4-12 `doc-update ... --probe-dependents process-pipe-name [--accept uid]... --execute --expected-document plan-sha256`:
// the run half of the doc-update verb. DocUpdateCommand plans with the P4-10 probe (DocUpdateProbe), refuses
// doc_update_plan_changed (field "expected_document", detail the SHA-256 of the plan it made) unless that plan's bytes
// hash to --expected-document (the "sha256" a dry run printed for the plan a human reviewed; hex, any case), writes the
// plan file exactly as a dry run does (--output is always a new file), and only then calls Run here, on the same channel
// the dry runs used (the enabled-roots policy is read once for both, every request goes through RevitCommandService.Run
// with its guards).
// Run sends the plan once with DocUpdate.Run: PlanRunner sends DocReplace's steps in order (every element.delete with
// exactly the confirm_dependents the confirmed dry runs reported, children before parents, then every create, parents
// before children, created[0] of earlier steps filled in) and stops at the first failure. This verb never retries a
// failed request and never resumes a failed run. A request whose outcome is unknown (no_response, pipe_io_error,
// pipe_read_timeout, or response_invalid: it may have reached Revit and run, only a usable reply is missing) is reported
// failed with reply_lost true and is never sent again in this invocation. Requests are re-sent in exactly one case, by
// the library: a stale delete answered reference_not_found on its target is already gone in Revit, so DocUpdate drops
// that key ("gone") and re-runs the re-made plan from the start; every step that already completed in this invocation
// (each with a reply) is sent again with the same operation id and the add-in answers it from its stored receipt
// without executing it again. Every request goes through a recorder, so the report can say how often each step was sent
// and whether it ran in this invocation.
// Output, one line: the dry summary members (output, bytes, sha256, regenerated, steps, forced, retired, probed,
// dependents), then "applied", "run" (the report status), "gone" (keys) and "report", one entry per step of the plan
// that ran, in run order: {"operation_id","key","command","sent","status":"done","replayed","created"} |
// {"operation_id","key","command","sent","status":"failed","code","field","detail","reply_lost"} |
// {"operation_id","key","command","sent","status":"not_sent"}. sent: how many times the request went out in this
// invocation; replayed: the add-in answered from a receipt stored by an earlier invocation (false for a step that ran
// in this one, even when its re-send after a gone re-plan was answered from that receipt). A completed run prints
// status "succeeded", applied true and "realized" (the updated realized map, sorted), exit 0. A failed run prints
// {"status":"failed","code"[,"key"][,"operation_id"][,"field"][,"detail"], ...} with the failed step's code, then the
// same members with applied false, exit 1 (the plan file stays: it is what was being run). An exception out of the
// library once writes may have been sent is doc_update_internal_error (type and message on stderr) with applied false
// and a "report" rebuilt from the recorder over the steps of the plan file (the last reply of each step decides).
internal static class DocUpdateExecute
{
    public const string PlanChanged = "doc_update_plan_changed";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string NotSent = "not_sent";
    // The client's read timeout (no reply line in time); a string so this file needs no newer core constant.
    public const string PipeReadTimeout = "pipe_read_timeout";

    // The request may have reached Revit and run: a usable reply is missing.
    static readonly string[] OutcomeUnknownCodes = { RevitCommandCodes.NoResponse, RevitCommandCodes.PipeIoError, PipeReadTimeout, RevitCommandCodes.ResponseInvalid };

    // plan: the probed plan whose bytes were written; summaryLine: the dry summary of that plan (JSON object).
    public static int Run(DocUpdatePlan plan, IRevitCommandTransport channel, string summaryLine, TextWriter stdout)
    {
        var summary = JsonNode.Parse(summaryLine) as JsonObject ?? throw new InvalidOperationException("summary is not an object");
        var recorder = new Recorder(channel);
        DocUpdateResult result;
        try { result = DocUpdate.Run(plan, recorder); }
        catch (Exception error)
        {
            Console.Error.WriteLine("doc-update internal error: " + error.GetType().FullName + ": " + error.Message);
            var internalError = new JsonObject { ["status"] = Failed, ["code"] = DocUpdateCommand.InternalError };
            Copy(summary, internalError);
            internalError["applied"] = false;
            internalError["report"] = Recorded(plan, recorder);
            stdout.WriteLine(internalError.ToJsonString());
            return 1;
        }

        var report = result.Report;
        var completed = report.Status == RevitPlanRunStatus.Completed && report.Pending.Count == 0 && result.Realized is not null;
        var keys = Keys(result.Plan);
        var done = new Dictionary<string, RevitCompletedStep>(StringComparer.Ordinal);
        foreach (var step in report.Completed) done[step.OperationId] = step;
        var entries = new JsonArray();
        foreach (var step in result.Plan.Replacement.Commands.Steps)
        {
            var entry = Entry(step, keys, recorder);
            if (done.TryGetValue(step.OperationId, out var ran)) MarkDone(entry, ran.Replayed && !recorder.RanHere(step.OperationId), ran.Created);
            else if (report.Failed is { } stopped && stopped.OperationId == step.OperationId) MarkFailed(entry, stopped.Code, stopped.Field, stopped.Detail);
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
                if (keys.TryGetValue(failure.OperationId, out var key)) line["key"] = key;
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

    // The per-step record of the plan file's steps from what the recorder saw: the last reply of a step decides (a
    // success is done, anything else failed with its code); a step never sent is not_sent.
    static JsonArray Recorded(DocUpdatePlan plan, Recorder recorder)
    {
        var keys = Keys(plan);
        var entries = new JsonArray();
        foreach (var step in plan.Replacement.Commands.Steps)
        {
            var entry = Entry(step, keys, recorder);
            var last = recorder.Last(step.OperationId);
            if (last is null) entry["status"] = NotSent;
            else if (last.Reply is { IsSuccess: true } reply) MarkDone(entry, reply.Receipt is { Replayed: true } && !recorder.RanHere(step.OperationId), reply.Created);
            else if (last.Reply is { } refused) MarkFailed(entry, refused.Code, refused.Field, refused.Detail);
            else MarkFailed(entry, last.Line is null ? RevitCommandCodes.NoResponse : RevitCommandCodes.ResponseInvalid, null, last.Line is null ? "no reply" : "reply is not a well-formed response");
            entries.Add(entry);
        }
        return entries;
    }

    static Dictionary<string, string> Keys(DocUpdatePlan plan)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in plan.Replacement.Replacements)
        {
            if (item.DeleteOperation is { } deleteOperation) keys[deleteOperation] = item.Key;
            if (item.CreateOperation is { } createOperation) keys[createOperation] = item.Key;
        }
        return keys;
    }

    static JsonObject Entry(RevitCommandStep step, Dictionary<string, string> keys, Recorder recorder) => new()
    {
        ["operation_id"] = step.OperationId,
        ["key"] = keys.TryGetValue(step.OperationId, out var key) ? key : null,
        ["command"] = step.Command,
        ["sent"] = recorder.SentCount(step.OperationId),
    };

    static void MarkDone(JsonObject entry, bool replayed, IReadOnlyList<string> created)
    {
        entry["status"] = Done;
        entry["replayed"] = replayed;
        entry["created"] = Strings(created);
    }

    static void MarkFailed(JsonObject entry, string code, string? field, string? detail)
    {
        entry["status"] = Failed;
        entry["code"] = code;
        entry["field"] = field;
        entry["detail"] = detail;
        entry["reply_lost"] = OutcomeUnknownCodes.Contains(code, StringComparer.Ordinal);
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

    // One request of this invocation: its operation id (null when the envelope did not parse), the reply line (null when
    // none came or the channel threw) and that line parsed (null when it did not parse).
    sealed record Sent(string? OperationId, string? Line, RevitCommandResponse? Reply);

    // Passes every request to the channel unchanged and records it with its reply.
    sealed class Recorder(IRevitCommandTransport inner) : IRevitCommandTransport
    {
        readonly List<Sent> records = new();

        public string? Send(string envelopeJson)
        {
            string? operation;
            try { operation = RevitCommandProtocol.Parse(envelopeJson).OperationId; }
            catch (RevitCommandException) { operation = null; }
            string? line;
            try { line = inner.Send(envelopeJson); }
            catch
            {
                records.Add(new Sent(operation, null, null));
                throw;
            }
            RevitCommandResponse? reply = null;
            if (line is not null)
            {
                try { reply = RevitCommandResponse.Parse(line); }
                catch (RevitCommandException) { reply = null; }
            }
            records.Add(new Sent(operation, line, reply));
            return line;
        }

        public int SentCount(string operation) => records.Count(record => record.OperationId == operation);

        public Sent? Last(string operation) => records.LastOrDefault(record => record.OperationId == operation);

        // The add-in executed this step in this invocation (a success not answered from a receipt).
        public bool RanHere(string operation) => records.Any(record => record.OperationId == operation && record.Reply is { IsSuccess: true, Receipt.Replayed: false });
    }
}
