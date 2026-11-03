using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    public static void Run(RevitCommandPlan plan, Action<bool, string, string> check)
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
    }
}
