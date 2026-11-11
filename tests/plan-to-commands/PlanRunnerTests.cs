using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// P3-03 oracle: PlanRunner executes a RevitCommandPlan step by step through an injected transport. It checks the level
// preconditions first (one query.levels, no write on issues), fills hosts from the previous created[0], stops at the
// first failed step with a deterministic resumable report, and a resume re-sends only pending steps, counting a
// replayed receipt as done.

// A fake executor: parses every envelope, keeps receipts by operation_id like the add-in (same id and hash replays,
// another hash conflicts) and lets a test override single replies.
sealed class FakeTransport : IRevitCommandTransport
{
    public const string Episode = "60f91daf-3dd7-4283-a86d-24137b73f3da-";
    readonly Dictionary<string, (string Sha, RevitCommandResponse Response)> receipts = new(StringComparer.Ordinal);
    int next = 0x500;

    public List<RevitCommandEnvelope> Sent { get; } = new();
    // Write operation ids that changed the model, in order; a replay adds nothing.
    public List<string> Executed { get; } = new();
    public List<string> Problems { get; } = new();
    public string LevelsReadback { get; set; } = PlanRunnerTests.Levels(("L1", PlanRunnerTests.UidL1, 0), ("L2", PlanRunnerTests.UidL2, 3000));
    // One-shot replies by operation id; the step is not executed.
    public Dictionary<string, Func<RevitCommandEnvelope, string?>> Replies { get; } = new(StringComparer.Ordinal);
    // Writes that succeed with an empty created list.
    public HashSet<string> NoCreated { get; } = new(StringComparer.Ordinal);
    // One-shot: the write runs and its receipt is stored, but the reply is lost in a malformed line.
    public HashSet<string> GarbleAfterExecute { get; } = new(StringComparer.Ordinal);

    public string? CreatedOf(string operationId) => receipts.TryGetValue(operationId, out var stored) && stored.Response.Created.Count > 0 ? stored.Response.Created[0] : null;

    public string? Send(string envelopeJson)
    {
        var envelope = RevitCommandProtocol.Parse(envelopeJson);
        if (envelope.CanonicalJson != envelopeJson) Problems.Add("not canonical " + envelope.OperationId);
        if (envelope.Args is OpeningPlaceArgs opening && opening.HostWallUid == PlanToCommands.PlaceholderUniqueId) Problems.Add("placeholder sent " + envelope.OperationId);
        Sent.Add(envelope);
        if (Replies.Remove(envelope.OperationId, out var reply)) return reply(envelope);
        var receipt = new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false);
        if (!envelope.Spec.Writes)
            return RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), envelope.Command == "query.levels" ? LevelsReadback : "{}", Array.Empty<string>()).ToJson();
        string json;
        if (receipts.TryGetValue(envelope.OperationId, out var stored))
        {
            if (stored.Sha != envelope.RequestSha256) return RevitCommandResponse.Failure(RevitCommandCodes.OperationIdConflict, "operation_id", "another request").ToJson();
            json = (stored.Response with { Receipt = stored.Response.Receipt! with { Replayed = true } }).ToJson();
        }
        else
        {
            var created = NoCreated.Contains(envelope.OperationId) ? Array.Empty<string>() : new[] { Episode + (next++).ToString("x8", CultureInfo.InvariantCulture) };
            var response = RevitCommandResponse.Success(receipt, created, Array.Empty<string>(), Array.Empty<string>(), null, new[] { "warning " + envelope.Command });
            receipts[envelope.OperationId] = (envelope.RequestSha256, response);
            Executed.Add(envelope.OperationId);
            json = response.ToJson();
        }
        return GarbleAfterExecute.Remove(envelope.OperationId) ? "{\"status\":" : json;
    }
}

static class PlanRunnerTests
{
    public const string UidL1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000311";
    public const string UidL2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000312";

    public static string Levels(params (string Name, string Uid, double Elevation)[] rows) =>
        JsonSerializer.Serialize(new { levels = rows.Select(row => new { name = row.Name, unique_id = row.Uid, elevation_mm = row.Elevation }).ToArray() });

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error.Code; }
        catch (ArgumentException error) { return "argument:" + error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    static string Names(JsonElement element) => string.Join(",", element.EnumerateObject().Select(property => property.Name));

    // plan: the P3-02 base layout (query.levels, 11 walls, 3 openings, 3 rooms; preconditions L1 at 0 mm, L2 at 3000 mm).
    // levelsOnly and allUnsupported: real Translate outputs with level preconditions and no steps.
    public static void Run(RevitCommandPlan plan, Func<RevitCommandPlan> levelsOnly, Func<RevitCommandPlan> allUnsupported, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "runner: " + name, detail);
        string Show(RevitPlanRunReport report) => report.ToJson();
        string[] Ids(IEnumerable<RevitCompletedStep> steps) => steps.Select(step => step.OperationId).ToArray();
        string[] SentIds(FakeTransport transport, int skip = 0) => transport.Sent.Skip(skip).Select(envelope => envelope.OperationId).ToArray();
        string? HostOf(FakeTransport transport, string operationId, int skip = 0) =>
            transport.Sent.Skip(skip).LastOrDefault(envelope => envelope.OperationId == operationId)?.Args is OpeningPlaceArgs opening ? opening.HostWallUid : null;

        var checkId = plan.Steps[0].OperationId;
        var writes = plan.Steps.Skip(1).Select(step => step.OperationId).ToArray();
        var door = plan.Steps.First(step => step.Command == "opening.place" && step.ElementKind == "door");
        var hostId = door.Inputs[0].FromOperation;
        var doorIndex = Array.IndexOf(writes, door.OperationId);
        var room = plan.Steps.Last(step => step.Command == "room.create");
        Check(plan.Steps[0].Command == "query.levels" && writes.Length == 17 && doorIndex > Array.IndexOf(writes, hostId) && Array.IndexOf(writes, hostId) >= 0, "fixture: level check first, 17 writes, the door after its host wall");

        // ---- Happy path ----
        var fake = new FakeTransport();
        var report = PlanRunner.Run(plan, fake);
        Check(report.Status == RevitPlanRunStatus.Completed && report.Failed is null && report.Pending.Count == 0 && Ids(report.Completed).SequenceEqual(writes),
            "happy path completes every write step in plan order", Show(report));
        Check(SentIds(fake).SequenceEqual(new[] { checkId }.Concat(writes)), "the level check is sent first, then each step exactly once", string.Join(",", SentIds(fake)));
        Check(report.LevelsCheck is { } levelsCheck && levelsCheck.OperationId == checkId && levelsCheck.Issues.Count == 0, "the report records the passed level check", Show(report));
        Check(report.Completed.All(step => step.Created.Count == 1 && step.Created[0] == fake.CreatedOf(step.OperationId) && !step.Replayed && step.Warnings.SequenceEqual(new[] { "warning " + step.Command })),
            "completed steps carry their created UniqueIds, replayed false and warnings", Show(report));
        Check(report.Completed.Zip(plan.Steps.Skip(1)).All(pair => pair.First.Command == pair.Second.Command && pair.First.ElementId == pair.Second.ElementId), "completed steps name their command and element");
        Check(HostOf(fake, door.OperationId) == fake.CreatedOf(hostId) && fake.CreatedOf(hostId) is not null, "the door host_wall_uid is the host wall step's created[0]", HostOf(fake, door.OperationId) ?? "none");
        Check(fake.Problems.Count == 0, "every envelope is canonical and no placeholder reaches the transport", string.Join(";", fake.Problems));
        Check(report.PlanSha256 == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan.ToJson()))).ToLowerInvariant() && report.PlanSha256 == PlanRunner.PlanSha256(plan)
            && report.Version == PlanRunner.Version, "the report binds the plan by the SHA-256 of its ToJson()", report.PlanSha256);

        // ---- Precondition failure: zero writes ----
        var missingL2 = new FakeTransport { LevelsReadback = Levels(("L1", UidL1, 0)) };
        var refused = PlanRunner.Run(plan, missingL2);
        Check(refused.Status == RevitPlanRunStatus.PreconditionFailed && missingL2.Sent.Count == 1 && missingL2.Sent[0].Command == "query.levels" && missingL2.Executed.Count == 0
            && refused.Completed.Count == 0 && refused.Failed is null && refused.Pending.SequenceEqual(writes),
            "a missing level aborts before any write; every step stays pending", Show(refused));
        Check(refused.LevelsCheck is { } missingCheck && missingCheck.OperationId == checkId && missingCheck.Issues.Count == 1 && missingCheck.Issues[0].Code == PlanToCommandsCodes.LevelMissing && missingCheck.Issues[0].Level == "L2",
            "the report lists the precondition issue", Show(refused));
        var high = new FakeTransport { LevelsReadback = Levels(("L1", UidL1, 0), ("L2", UidL2, 4000)) };
        var mismatch = PlanRunner.Run(plan, high);
        Check(mismatch.Status == RevitPlanRunStatus.PreconditionFailed && high.Executed.Count == 0 && high.Sent.Count == 1 && mismatch.LevelsCheck?.Issues.Single().Code == PlanToCommandsCodes.LevelElevationMismatch,
            "a level at the wrong elevation aborts before any write", Show(mismatch));
        missingL2.LevelsReadback = Levels(("L1", UidL1, 0), ("L2", UidL2, 3000));
        var fixedDocument = PlanRunner.Resume(plan, refused, missingL2);
        Check(fixedDocument.Status == RevitPlanRunStatus.Completed && SentIds(missingL2, 1).SequenceEqual(new[] { checkId }.Concat(writes)) && missingL2.Executed.Count == writes.Length,
            "resume re-checks the levels, then runs the pending steps", Show(fixedDocument));

        // ---- Level check failures: zero writes ----
        var deaf = new FakeTransport();
        deaf.Replies[checkId] = _ => RevitCommandResponse.Failure(RevitCommandCodes.DocumentConflict, "document", "another document is active").ToJson();
        var conflict = PlanRunner.Run(plan, deaf);
        Check(conflict.Status == RevitPlanRunStatus.Failed && conflict.Failed is { } checkFailure && checkFailure.OperationId == checkId && checkFailure.Code == RevitCommandCodes.DocumentConflict
            && checkFailure.Field == "document" && checkFailure.Detail == "another document is active" && conflict.LevelsCheck is null && deaf.Sent.Count == 1 && deaf.Executed.Count == 0 && conflict.Pending.SequenceEqual(writes),
            "a failed level check stops before any write", Show(conflict));
        var badReadback = new FakeTransport { LevelsReadback = "{\"levels\":3}" };
        var unreadable = PlanRunner.Run(plan, badReadback);
        Check(unreadable.Status == RevitPlanRunStatus.Failed && unreadable.Failed is { } unreadableFailure && unreadableFailure.OperationId == checkId && unreadableFailure.Code == RevitCommandCodes.ResponseInvalid && badReadback.Executed.Count == 0,
            "a malformed level readback stops before any write (response_invalid)", Show(unreadable));

        // ---- Mid-plan failure and resume ----
        var flaky = new FakeTransport();
        flaky.Replies[door.OperationId] = _ => RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, null, "host wall moved").ToJson();
        var broken = PlanRunner.Run(plan, flaky);
        Check(broken.Status == RevitPlanRunStatus.Failed && broken.Failed is { } doorFailure && doorFailure.OperationId == door.OperationId && doorFailure.Command == "opening.place"
            && doorFailure.ElementId == door.ElementId && doorFailure.Code == RevitCommandCodes.TransactionFailed && doorFailure.Field is null && doorFailure.Detail == "host wall moved",
            "an error reply fails the step with the executor code and detail", Show(broken));
        Check(Ids(broken.Completed).SequenceEqual(writes.Take(doorIndex)) && broken.Pending.SequenceEqual(writes.Skip(doorIndex)) && SentIds(flaky).Last() == door.OperationId,
            "the run stops at the first failed step: earlier steps completed, the failed step and the rest pending", Show(broken));
        var sentBefore = flaky.Sent.Count;
        var resumed = PlanRunner.Resume(plan, broken, flaky);
        Check(SentIds(flaky, sentBefore).SequenceEqual(new[] { checkId }.Concat(writes.Skip(doorIndex))), "resume sends the level check and the pending steps only, never a completed one",
            string.Join(",", SentIds(flaky, sentBefore)));
        Check(resumed.Status == RevitPlanRunStatus.Completed && resumed.Failed is null && resumed.Pending.Count == 0 && Ids(resumed.Completed).SequenceEqual(writes)
            && resumed.Completed.Take(doorIndex).Zip(broken.Completed).All(pair => pair.First.Created.SequenceEqual(pair.Second.Created)),
            "resume completes the plan and keeps the earlier completed steps", Show(resumed));
        Check(HostOf(flaky, door.OperationId, sentBefore) == broken.Completed.Single(step => step.OperationId == hostId).Created[0], "after resume the door host comes from the report's created[0]");
        Check(flaky.Executed.Count == writes.Length && flaky.Executed.Distinct(StringComparer.Ordinal).Count() == writes.Length, "every write ran exactly once across run and resume");
        var finished = PlanRunner.Resume(plan, resumed, flaky);
        Check(finished.ToJson() == resumed.ToJson() && flaky.Sent.Count == sentBefore + 1 + writes.Length - doorIndex, "resuming a completed report sends nothing");

        // ---- Replayed receipts ----
        var crash = new FakeTransport();
        crash.GarbleAfterExecute.Add(hostId);
        var lost = PlanRunner.Run(plan, crash);
        Check(lost.Status == RevitPlanRunStatus.Failed && lost.Failed is { } lostFailure && lostFailure.OperationId == hostId && lostFailure.Code == RevitCommandCodes.ResponseInvalid && crash.Executed.Contains(hostId) && lost.Pending[0] == hostId,
            "a reply lost after the write is response_invalid and the step stays pending", Show(lost));
        var again = PlanRunner.Resume(plan, lost, crash);
        var replayed = again.Completed.SingleOrDefault(step => step.OperationId == hostId);
        Check(again.Status == RevitPlanRunStatus.Completed && replayed is not null && replayed.Replayed && replayed.Created.SequenceEqual(new[] { crash.CreatedOf(hostId)! })
            && crash.Executed.Count(id => id == hostId) == 1, "the resent step replays its receipt: it counts as done and wrote once", Show(again));
        Check(again.Completed.Count(step => step.Replayed) == 1, "only the resent step is replayed");
        Check(HostOf(crash, door.OperationId) == crash.CreatedOf(hostId), "a replayed created[0] feeds the next step");
        var rerun = PlanRunner.Run(plan, crash);
        Check(rerun.Status == RevitPlanRunStatus.Completed && rerun.Completed.All(step => step.Replayed) && crash.Executed.Count == writes.Length
            && rerun.Completed.Zip(again.Completed).All(pair => pair.First.Created.SequenceEqual(pair.Second.Created)),
            "running a finished plan again only replays receipts", Show(rerun));

        // ---- Malformed and foreign replies ----
        RevitPlanRunReport FailOn(string operationId, Func<RevitCommandEnvelope, string?> reply, out FakeTransport transport)
        {
            transport = new FakeTransport();
            transport.Replies[operationId] = reply;
            return PlanRunner.Run(plan, transport);
        }
        var garbage = FailOn(writes[0], _ => "not json", out var garbageTransport);
        Check(garbage.Status == RevitPlanRunStatus.Failed && garbage.Failed is { } garbageFailure && garbageFailure.OperationId == writes[0] && garbageFailure.Code == RevitCommandCodes.ResponseInvalid && garbage.Completed.Count == 0
            && garbage.Pending.SequenceEqual(writes) && garbageTransport.Sent.Count == 2, "a reply that is not JSON is response_invalid and stops the run", Show(garbage));
        var partial = FailOn(writes[0], _ => "{\"status\":\"succeeded\",\"code\":\"ok\"}", out _);
        Check(partial.Failed?.Code == RevitCommandCodes.ResponseInvalid, "a reply missing members is response_invalid", Show(partial));
        var silent = FailOn(writes[0], _ => null, out _);
        Check(silent.Failed is { Code: RevitCommandCodes.NoResponse } silentFailure && silentFailure.OperationId == writes[0], "no reply is no_response", Show(silent));
        string Foreign(RevitCommandEnvelope envelope, RevitCommandReceipt receipt) =>
            RevitCommandResponse.Success(receipt, new[] { FakeTransport.Episode + "00000999" }, Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>()).ToJson();
        var otherOperation = FailOn(writes[0], envelope => Foreign(envelope, new RevitCommandReceipt("op.other", envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false)), out _);
        Check(otherOperation.Failed is { Code: RevitCommandCodes.ResponseInvalid, Field: "receipt" } && otherOperation.Completed.Count == 0,
            "a success whose receipt names another operation is response_invalid@receipt", Show(otherOperation));
        var otherRequest = FailOn(writes[0], envelope => Foreign(envelope, new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, new string('0', 64), false)), out _);
        Check(otherRequest.Failed is { Code: RevitCommandCodes.ResponseInvalid, Field: "receipt" }, "a success whose receipt hashes another request is response_invalid@receipt", Show(otherRequest));
        var otherDocument = FailOn(writes[0], envelope => Foreign(envelope, new RevitCommandReceipt(envelope.OperationId, envelope.Command, FakeTransport.Episode + "00000001", envelope.RequestSha256, false)), out _);
        Check(otherDocument.Failed is { Code: RevitCommandCodes.ResponseInvalid, Field: "receipt" }, "a success whose receipt names another document is response_invalid@receipt", Show(otherDocument));

        // ---- Missing created[0] ----
        var hostless = new FakeTransport();
        hostless.NoCreated.Add(hostId);
        var noHost = PlanRunner.Run(plan, hostless);
        Check(noHost.Status == RevitPlanRunStatus.Failed && noHost.Failed is { } noHostFailure && noHostFailure.OperationId == hostId && noHostFailure.Code == PlanRunnerCodes.CreatedMissing && noHost.Pending[0] == hostId
            && SentIds(hostless).Last() == hostId && hostless.Sent.All(envelope => envelope.OperationId != door.OperationId),
            "a write that creates nothing while a later step needs its created[0] fails (created_missing) before that step is sent", Show(noHost));
        var leafless = new FakeTransport();
        leafless.NoCreated.Add(room.OperationId);
        var noRoom = PlanRunner.Run(plan, leafless);
        Check(noRoom.Status == RevitPlanRunStatus.Completed && noRoom.Completed.Single(step => step.OperationId == room.OperationId).Created.Count == 0,
            "a step no later step depends on may create nothing", Show(noRoom));

        // ---- Deterministic, serializable report ----
        var json = PlanRunner.Run(plan, new FakeTransport()).ToJson();
        Check(json == PlanRunner.Run(plan, new FakeTransport()).ToJson() && json == report.ToJson(), "the same plan and replies give byte-identical report JSON");
        using (var parsed = JsonDocument.Parse(broken.ToJson()))
        {
            var root = parsed.RootElement;
            Check(Names(root) == "runner_version,plan_sha256,status,levels_check,completed,failed,pending", "report members in fixed order", Names(root));
            Check(Names(root.GetProperty("completed")[0]) == "operation_id,command,element_id,created,replayed,warnings", "completed step members in fixed order", root.GetProperty("completed")[0].GetRawText());
            Check(Names(root.GetProperty("failed")) == "operation_id,command,element_id,code,field,detail" && root.GetProperty("failed").GetProperty("field").ValueKind == JsonValueKind.Null,
                "failed step members in fixed order, nulls written", root.GetProperty("failed").GetRawText());
            Check(Names(root.GetProperty("levels_check")) == "operation_id,issues" && root.GetProperty("status").GetString() == "failed", "levels check members in fixed order");
        }
        using (var parsed = JsonDocument.Parse(refused.ToJson()))
        {
            var issue = parsed.RootElement.GetProperty("levels_check").GetProperty("issues")[0];
            Check(Names(issue) == "element_id,level,code,detail" && parsed.RootElement.GetProperty("failed").ValueKind == JsonValueKind.Null, "issue members in fixed order; no failed step on a precondition failure", issue.GetRawText());
        }
        foreach (var sample in new[] { report, refused, conflict, broken, lost, noRoom })
            Check(RevitPlanRunReport.Parse(sample.ToJson()).ToJson() == sample.ToJson(), "Parse(ToJson()) round-trips a " + sample.Status + " report", sample.ToJson());
        var viaJson = new FakeTransport();
        viaJson.Replies[door.OperationId] = _ => RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, null, "host wall moved").ToJson();
        var stored = PlanRunner.Run(plan, viaJson).ToJson();
        var fromJson = PlanRunner.Resume(plan, RevitPlanRunReport.Parse(stored), viaJson);
        Check(fromJson.Status == RevitPlanRunStatus.Completed && SentIds(viaJson).Count(id => id != checkId) == writes.Length + 1, "a report read back from JSON resumes like the original", Show(fromJson));

        // ---- Report and plan checks ----
        Check(Code(() => PlanRunner.Resume(plan with { ExpectedRevision = plan.ExpectedRevision + 1 }, broken, new FakeTransport())) == "argument:report_plan_mismatch", "a report from another plan is refused");
        Check(Code(() => PlanRunner.Resume(plan, broken with { Pending = broken.Pending.Skip(1).ToArray() }, new FakeTransport())) == "argument:report_invalid", "a report that loses a pending step is refused");
        Check(Code(() => PlanRunner.Resume(plan, broken with { Completed = broken.Completed.Reverse().ToArray() }, new FakeTransport())) == "argument:report_invalid", "a report whose completed steps are out of plan order is refused");
        Check(Code(() => PlanRunner.Resume(plan, broken with { Status = RevitPlanRunStatus.Completed }, new FakeTransport())) == "argument:report_invalid", "a report whose status contradicts its pending steps is refused");
        var hostEntry = broken.Completed.Single(step => step.OperationId == hostId);
        Check(Code(() => PlanRunner.Resume(plan, broken with { Completed = broken.Completed.Select(step => step == hostEntry ? step with { Created = Array.Empty<string>() } : step).ToArray() }, new FakeTransport()))
            == "argument:report_invalid", "a report whose completed host wall has no created[0] is refused");
        Check(Code(() => RevitPlanRunReport.Parse("{}")) == "argument:report_invalid" && Code(() => RevitPlanRunReport.Parse("not json")) == "argument:report_invalid"
            && Code(() => RevitPlanRunReport.Parse(broken.ToJson().Replace("\"status\":\"failed\"", "\"status\":\"maybe\"", StringComparison.Ordinal))) == "argument:report_invalid",
            "a malformed report JSON is refused");
        var nothing = new FakeTransport();
        var empty = PlanRunner.Run(plan with { Steps = Array.Empty<RevitCommandStep>(), Preconditions = Array.Empty<RevitLevelPrecondition>() }, nothing);
        Check(empty.Status == RevitPlanRunStatus.Completed && nothing.Sent.Count == 0 && empty.LevelsCheck is null && empty.Completed.Count == 0, "a plan without steps completes without sending anything", Show(empty));
        Check(Code(() => PlanRunner.Run(plan with { Steps = plan.Steps.Skip(1).ToArray() }, new FakeTransport())) == "argument:levels_check_missing", "preconditions without their query.levels step are refused");

        // ======== Review follow-ups ========
        RevitPlanRunReport? Safe(Func<RevitPlanRunReport> run, out string error)
        {
            try { error = ""; return run(); }
            catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; return null; }
        }
        string ShowSafe(RevitPlanRunReport? outcome, string error) => outcome?.ToJson() ?? error;

        // ---- 1) A translated plan with preconditions but no steps completes with zero sends ----
        RevitPlanRunReport? levelsOnlyReport = null;
        RevitCommandPlan? levelsOnlyPlan = null;
        foreach (var (label, make) in new (string, Func<RevitCommandPlan>)[] { ("levels-only intent", levelsOnly), ("all-unsupported plan", allUnsupported) })
        {
            RevitCommandPlan? source = null;
            var idle = new FakeTransport();
            var outcome = Safe(() => PlanRunner.Run(source = make(), idle), out var error);
            Check(source is { Steps.Count: 0, Preconditions.Count: > 0 } && source.Preconditions.All(item => item.CheckOperation is null)
                && outcome is { Status: RevitPlanRunStatus.Completed, LevelsCheck: null, Failed: null, Completed.Count: 0, Pending.Count: 0 } && idle.Sent.Count == 0,
                "a translated " + label + " with level preconditions and no steps completes with zero sends", ShowSafe(outcome, error) + " " + source?.ToJson());
            if (outcome is { } done && source is { } translated)
            {
                var itself = Safe(() => PlanRunner.Resume(translated, RevitPlanRunReport.Parse(done.ToJson()), idle), out var itselfError);
                Check(itself?.ToJson() == done.ToJson() && idle.Sent.Count == 0, "the " + label + " report resumes to itself", ShowSafe(itself, itselfError));
                if (label == "levels-only intent") { levelsOnlyReport = done; levelsOnlyPlan = translated; }
            }
        }

        // ---- 2) and 3) Transport exceptions ----
        RevitPlanRunReport? Throwing(int index, Exception thrown, out FakeTransport transport, out string error)
        {
            var target = new FakeTransport();
            target.Replies[writes[index]] = _ => throw thrown;
            transport = target;
            return Safe(() => PlanRunner.Run(plan, target), out error);
        }
        var odd = Throwing(2, new InvalidOperationException("pipe state 42"), out var oddTransport, out var oddError);
        Check(odd is { Status: RevitPlanRunStatus.Failed, Failed: { Code: RevitCommandCodes.PipeIoError, Detail: "InvalidOperationException", OperationId: var oddOperation }, Completed.Count: 2 }
            && oddOperation == writes[2] && odd.Pending[0] == writes[2] && oddTransport.Executed.Count == 2,
            "any other transport exception fails the step as pipe_io_error with only its type name", ShowSafe(odd, oddError));
        var slow = Throwing(0, new TimeoutException("no listener"), out _, out var slowError);
        Check(slow is { Status: RevitPlanRunStatus.Failed, Failed: { Code: RevitCommandCodes.PipeConnectTimeout, Detail: "TimeoutException" }, Completed.Count: 0 }, "a transport timeout is pipe_connect_timeout", ShowSafe(slow, slowError));
        var readTimeoutRun = Throwing(1, new RevitPipeReadTimeoutException(TimeSpan.FromMilliseconds(250)), out var readTimeoutTransport, out var readTimeoutError);
        Check(readTimeoutRun is { Status: RevitPlanRunStatus.Failed, Failed: { Code: RevitCommandCodes.PipeReadTimeout, Detail: { } readTimeoutDetail, OperationId: var readTimeoutOperation }, Completed.Count: 1 }
            && readTimeoutOperation == writes[1] && readTimeoutDetail.StartsWith("outcome_unknown: no reply within 250 ms", StringComparison.Ordinal) && readTimeoutDetail.Contains("same operation_id", StringComparison.Ordinal)
            && readTimeoutTransport.Executed.Count == 1,
            "a raw transport read timeout on a write is pipe_read_timeout, outcome unknown, and the run stops", ShowSafe(readTimeoutRun, readTimeoutError));
        var cut = Throwing(1, new IOException("broken pipe"), out _, out var cutError);
        Check(cut is { Status: RevitPlanRunStatus.Failed, Failed: { Code: RevitCommandCodes.PipeIoError, Detail: "IOException" }, Completed.Count: 1 }, "a transport IOException is pipe_io_error", ShowSafe(cut, cutError));

        // ---- 3) and 6) Reply checks ----
        var otherCommand = FailOn(writes[0], envelope => Foreign(envelope, new RevitCommandReceipt(envelope.OperationId, "query.levels", envelope.Document.UniqueId, envelope.RequestSha256, false)), out _);
        Check(otherCommand.Failed is { Code: RevitCommandCodes.ResponseInvalid, Field: "receipt", Detail: "receipt command does not match the request" },
            "a success whose receipt names another command is response_invalid@receipt", Show(otherCommand));
        var okFailure = FailOn(writes[0], _ => RevitCommandResponse.Failure(RevitCommandCodes.Ok, null, "odd").ToJson(), out _);
        Check(okFailure.Failed is { Code: RevitCommandCodes.ResponseInvalid, Field: "code" } && okFailure.Completed.Count == 0, "a failed reply with code ok is response_invalid@code", Show(okFailure));
        Check(garbage.Failed?.Detail == PlanRunner.MalformedReplyDetail && partial.Failed?.Detail == PlanRunner.MalformedReplyDetail,
            "a malformed reply has a fixed detail, never a parser message", Show(garbage) + " " + Show(partial));
        Check(unreadable.Failed?.Detail == PlanRunner.MalformedReadbackDetail, "a malformed level readback has a fixed detail", Show(unreadable));

        // ---- 3) Resume whose level check fails, or finds issues, after steps completed ----
        var storedBroken = RevitPlanRunReport.Parse(broken.ToJson());
        var blocked = new FakeTransport();
        blocked.Replies[checkId] = _ => RevitCommandResponse.Failure(RevitCommandCodes.DocumentConflict, "document", "another document is active").ToJson();
        var stalled = Safe(() => PlanRunner.Resume(plan, storedBroken, blocked), out var stalledError);
        Check(stalled is { Status: RevitPlanRunStatus.Failed, LevelsCheck: null, Failed.OperationId: var stalledOperation } && stalledOperation == checkId && stalled.Completed.Count == doorIndex
            && stalled.Pending.SequenceEqual(broken.Pending) && blocked.Sent.Count == 1 && blocked.Executed.Count == 0,
            "a level check failing on resume keeps the completed steps and sends no write", ShowSafe(stalled, stalledError));
        if (stalled is { } stalledReport)
        {
            var unstalled = Safe(() => PlanRunner.Resume(plan, RevitPlanRunReport.Parse(stalledReport.ToJson()), blocked), out var unstalledError);
            Check(unstalled is { Status: RevitPlanRunStatus.Completed } && Ids(unstalled.Completed).SequenceEqual(writes) && SentIds(blocked, 1).SequenceEqual(new[] { checkId }.Concat(writes.Skip(doorIndex))),
                "that report, read back from JSON, resumes to completion", ShowSafe(unstalled, unstalledError));
        }
        var moved = new FakeTransport { LevelsReadback = Levels(("L1", UidL1, 0)) };
        var held = Safe(() => PlanRunner.Resume(plan, storedBroken, moved), out var heldError);
        Check(held is { Status: RevitPlanRunStatus.PreconditionFailed, Failed: null, LevelsCheck.Issues.Count: 1 } && held.Completed.Count == doorIndex && moved.Sent.Count == 1 && moved.Executed.Count == 0,
            "a precondition issue found on resume keeps the completed steps and sends no write", ShowSafe(held, heldError));
        if (held is { } heldReport)
        {
            moved.LevelsReadback = Levels(("L1", UidL1, 0), ("L2", UidL2, 3000));
            var released = Safe(() => PlanRunner.Resume(plan, RevitPlanRunReport.Parse(heldReport.ToJson()), moved), out var releasedError);
            Check(released is { Status: RevitPlanRunStatus.Completed } && Ids(released.Completed).SequenceEqual(writes) && moved.Executed.Count == writes.Length - doorIndex,
                "that precondition_failed report, read back from JSON, resumes to completion", ShowSafe(released, releasedError));
        }

        // ---- 3) and 4) Tampered reports ----
        void Refused(RevitPlanRunReport tampered, string name, RevitCommandPlan? target = null) =>
            Check(Code(() => PlanRunner.Resume(target ?? plan, tampered, new FakeTransport())) == "argument:report_invalid", "Resume refuses " + name, tampered.ToJson());
        RevitCompletedStep[] FirstChanged(Func<RevitCompletedStep, RevitCompletedStep> change) => broken.Completed.Select((step, i) => i == 0 ? change(step) : step).ToArray();
        Refused(broken with { Completed = FirstChanged(step => step with { Command = "room.create" }) }, "a completed step with another command");
        Refused(broken with { Completed = FirstChanged(step => step with { ElementId = Guid.Empty }) }, "a completed step with another element_id");
        Refused(broken with { Completed = FirstChanged(step => step with { Created = new[] { "not-a-uid" } }) }, "a completed step whose created is not a UniqueId");
        Refused(broken with { Failed = broken.Failed! with { OperationId = writes[0] } }, "a failed step that is neither pending[0] nor the level check");
        Refused(broken with { Failed = broken.Failed! with { Command = "wall.create" } }, "a failed step with another command");
        Refused(broken with { Failed = broken.Failed! with { ElementId = null } }, "a failed step with another element_id");
        Refused(broken with { Version = 2 }, "runner_version 2");
        Refused(refused with { LevelsCheck = refused.LevelsCheck! with { Issues = Array.Empty<RevitPreconditionIssue>() } }, "precondition_failed without issues");
        Refused(broken with { LevelsCheck = broken.LevelsCheck! with { OperationId = writes[0] } }, "a levels_check naming another step");
        Refused(broken with { LevelsCheck = null }, "a failed write step without the levels_check that preceded it");
        Refused(conflict with { LevelsCheck = new RevitLevelsCheck(checkId, Array.Empty<RevitPreconditionIssue>()) }, "a failed level check that also reports a levels_check");
        Refused(conflict with { Failed = conflict.Failed! with { Command = "wall.create" } }, "a failed level check with another command");
        if (levelsOnlyReport is not null && levelsOnlyPlan is not null)
            Refused(levelsOnlyReport with { LevelsCheck = new RevitLevelsCheck(checkId, Array.Empty<RevitPreconditionIssue>()) }, "a levels_check for a plan without a level check", levelsOnlyPlan);
        Check(Code(() => PlanRunner.Run(plan with { Steps = plan.Steps.Append(plan.Steps[1]).ToArray() }, new FakeTransport())) == "argument:plan_duplicate_operation", "a plan with a repeated operation id is refused");

        // ---- 3) Parse refuses extra, duplicate and mistyped members ----
        string Mutate(string json, Action<JsonObject> change)
        {
            var node = JsonNode.Parse(json)!.AsObject();
            change(node);
            return node.ToJsonString();
        }
        var brokenJson = broken.ToJson();
        foreach (var (label, text) in new[]
        {
            ("an extra member", Mutate(brokenJson, node => node["extra"] = 1)),
            ("an extra completed member", Mutate(brokenJson, node => node["completed"]![0]!.AsObject()["extra"] = true)),
            ("an extra failed member", Mutate(brokenJson, node => node["failed"]!.AsObject()["extra"] = "x")),
            ("a duplicate member", "{\"status\":\"failed\"," + brokenJson[1..]),
            ("a missing member", Mutate(brokenJson, node => node.Remove("pending"))),
            ("a string runner_version", Mutate(brokenJson, node => node["runner_version"] = "1")),
            ("a string replayed", Mutate(brokenJson, node => node["completed"]![0]!["replayed"] = "false")),
            ("an object pending", Mutate(brokenJson, node => node["pending"] = new JsonObject())),
            ("a numeric created item", Mutate(brokenJson, node => node["completed"]![0]!["created"] = new JsonArray(JsonValue.Create(1)))),
            ("a non-GUID element_id", Mutate(brokenJson, node => node["failed"]!["element_id"] = "door")),
            ("a numeric code", Mutate(brokenJson, node => node["failed"]!["code"] = 7)),
            ("an array levels_check", Mutate(brokenJson, node => node["levels_check"] = new JsonArray())),
        })
            Check(Code(() => RevitPlanRunReport.Parse(text)) == "argument:report_invalid", "Parse refuses " + label, text);

        // ---- 6) Whatever ToJson writes, Parse reads back (empty strings included) ----
        var blank = new RevitPlanRunReport(PlanRunner.Version, "", RevitPlanRunStatus.Failed, new RevitLevelsCheck("", new[] { new RevitPreconditionIssue(Guid.Empty, "", "", "") }),
            new[] { new RevitCompletedStep("", "", null, new[] { "" }, false, new[] { "" }) }, new RevitFailedStep("", "", null, "", "", ""), new[] { "" });
        Check(Code(() => { if (RevitPlanRunReport.Parse(blank.ToJson()).ToJson() != blank.ToJson()) throw new InvalidOperationException("differs"); }) is null, "a report of empty strings round-trips", blank.ToJson());
        var emptyReply = FailOn(writes[0], _ => RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, "", "").ToJson(), out _);
        Check(emptyReply.Failed is { Field: "", Detail: "" } && RevitPlanRunReport.Parse(emptyReply.ToJson()).ToJson() == emptyReply.ToJson(), "an empty field and detail from the executor round-trip", Show(emptyReply));
        var quiet = new FakeTransport();
        quiet.Replies[writes[0]] = envelope => RevitCommandResponse.Success(new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false),
            new[] { FakeTransport.Episode + "00000aaa" }, Array.Empty<string>(), Array.Empty<string>(), null, new[] { "" }).ToJson();
        var quietReport = PlanRunner.Run(plan, quiet);
        Check(quietReport.Status == RevitPlanRunStatus.Completed && quietReport.Completed[0].Warnings.SequenceEqual(new[] { "" }) && RevitPlanRunReport.Parse(quietReport.ToJson()).ToJson() == quietReport.ToJson(),
            "an empty warning round-trips", Show(quietReport));

        // ---- 5) created_missing does not clear on resume: the receipt replays the empty created ----
        var hostlessSent = hostless.Sent.Count;
        var hostlessExecuted = hostless.Executed.Count;
        var stillNoHost = Safe(() => PlanRunner.Resume(plan, RevitPlanRunReport.Parse(noHost.ToJson()), hostless), out var stillError);
        Check(stillNoHost is { Status: RevitPlanRunStatus.Failed, Failed: { Code: PlanRunnerCodes.CreatedMissing, OperationId: var stillOperation } } && stillOperation == hostId
            && hostless.Executed.Count == hostlessExecuted && hostless.Sent.Count == hostlessSent + 2 && hostless.Sent[^1].OperationId == hostId,
            "resuming a created_missing report fails again with zero new writes (the receipt replays)", ShowSafe(stillNoHost, stillError));
    }
}
