using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P1-07 command.batch: 1 to 50 catalogue write commands applied all-or-nothing as one undo entry. Same envelope
// (protocol_version 1), schema subset, copies-only guard and operation_id receipts as every other catalogue part,
// which are unchanged. Each step names its own operation_id, command and args; the args are validated with that
// command's own schema and typed binder, and every refusal path is rewritten under args.steps[i]. A step is never a
// query, a nested batch, document.save (a save cannot run inside a transaction group) or a dry run. The batch
// operation_id is the replay key of the whole batch; step operation_ids are distinct and differ from it. The add-in
// executor (CommandsExecutorBatch.cs) runs every step inside one transaction group that it assimilates (one undo
// entry) or rolls back entirely when any step fails; the decisions it needs that touch no Revit object live here.
// Vendor-free like every RevitCommands*.cs file.

// One validated step. ArgsJson is the canonical JSON of the step's args (sorted keys, shortest round-trip numbers).
public sealed record RevitBatchStep(int Index, string OperationId, string Command, RevitCommandArgs Args, string ArgsJson);
public sealed record CommandBatchArgs(IReadOnlyList<RevitBatchStep> Steps) : RevitCommandArgs;

public static class RevitCommandBatch
{
    public const string Command = "command.batch";
    public const int MinSteps = 1;
    public const int MaxSteps = 50;
    // Writes that cannot be a step: the batch itself, document.save and document.save_copy (no transaction group may be
    // open during a save).
    public static IReadOnlyList<string> ExcludedWrites { get; } = new[] { Command, "document.save", RevitSaveCopyGuard.Command };

    public static bool IsStepCommand([NotNullWhen(true)] RevitCommandSpec? spec) => spec is { Writes: true } && !ExcludedWrites.Contains(spec.Name, StringComparer.Ordinal);

    // The refusal path of step i: "args.steps[i]" alone, or followed by the step-relative path (args.level, operation_id...).
    public static string StepField(int index, string? field) =>
        "args.steps[" + index.ToString(CultureInfo.InvariantCulture) + "]" + (string.IsNullOrEmpty(field) ? "" : "." + field);

    // A step reply's field under the batch: the step's own members (args..., operation_id, command, or none) move under
    // args.steps[i]; anything else (document.*, receipt, pipe_name) names the batch request itself and is kept.
    public static string? StepReplyField(int index, string? field) =>
        string.IsNullOrEmpty(field) || field == "args" || field.StartsWith("args.", StringComparison.Ordinal) || field.StartsWith("args[", StringComparison.Ordinal)
            || field == "operation_id" || field == "command" ? StepField(index, field) : field;

    // Called by the protocol after binding: step operation_ids are distinct (ordinal, like the receipt store keys) and
    // differ from the batch's own, so every receipt the batch stores has its own key.
    public static void CheckOperationIds(string batchOperationId, CommandBatchArgs batch)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in batch.Steps)
        {
            if (string.Equals(step.OperationId, batchOperationId, StringComparison.Ordinal))
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, StepField(step.Index, "operation_id"), "a step operation_id must differ from the batch operation_id " + batchOperationId);
            if (seen.TryGetValue(step.OperationId, out var first))
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, StepField(step.Index, "operation_id"), "operation_id " + step.OperationId + " is already used by steps[" + first.ToString(CultureInfo.InvariantCulture) + "]");
            seen.Add(step.OperationId, step.Index);
        }
    }

    // Before anything runs: a step operation_id already present in the document's receipt store is refused, even for
    // the identical request (a step never replays inside a new batch; replay is by the batch operation_id).
    public static void CheckFreshSteps(CommandBatchArgs batch, Func<string, bool> alreadyUsed)
    {
        foreach (var step in batch.Steps)
            if (alreadyUsed(step.OperationId))
                throw new RevitCommandException(RevitCommandCodes.OperationIdConflict, StepField(step.Index, "operation_id"),
                    "step " + step.Index.ToString(CultureInfo.InvariantCulture) + " (" + step.Command + ") operation_id " + step.OperationId + " was already used in this document; nothing was written");
    }

    // The step as a stand-alone envelope on the batch's document: parsed (and therefore validated) again by the protocol.
    // Its RequestSha256 is the hash of its canonical text, which is what the step receipt binds.
    public static RevitCommandEnvelope StepEnvelope(RevitCommandEnvelope batch, int index)
    {
        if (batch.Args is not CommandBatchArgs args) throw new ArgumentException("not a command.batch envelope", nameof(batch));
        var step = args.Steps[index];
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol_version", batch.ProtocolVersion);
            writer.WriteString("operation_id", step.OperationId);
            writer.WriteStartObject("document");
            writer.WriteString("path", batch.Document.Path);
            writer.WriteString("unique_id", batch.Document.UniqueId);
            writer.WriteEndObject();
            writer.WriteString("command", step.Command);
            writer.WritePropertyName("args");
            writer.WriteRawValue(step.ArgsJson, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return RevitCommandProtocol.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    // Typed args a client-side guard must see, with the field prefix of their refusals: the envelope's own args, or
    // each step's (used for the family.load source guard).
    public static IEnumerable<(string Prefix, RevitCommandArgs Args)> GuardedArgs(RevitCommandEnvelope envelope)
    {
        if (envelope.Args is CommandBatchArgs batch)
            foreach (var step in batch.Steps) yield return (StepField(step.Index, null) + ".", step.Args);
        else yield return ("", envelope.Args);
    }

    // The batch refusal for a failed step: the step's code, its field under args.steps[i], and a detail naming the step
    // index, command and operation_id and stating that the whole batch was rolled back.
    public static RevitCommandResponse StepFailure(RevitBatchStep step, string code, string? field, string? detail) =>
        RevitCommandResponse.Failure(code, StepReplyField(step.Index, field),
            "step " + step.Index.ToString(CultureInfo.InvariantCulture) + " (" + step.Command + ", operation_id " + step.OperationId + ") failed: "
            + (string.IsNullOrEmpty(detail) ? code : detail) + "; the whole batch was rolled back and nothing was written");

    // Null when the step reply is a first-run success answering exactly this step; otherwise the batch refusal.
    public static RevitCommandResponse? CheckStepReply(RevitBatchStep step, RevitCommandEnvelope stepEnvelope, string stepRequestSha256, RevitCommandResponse reply)
    {
        if (!reply.IsSuccess) return StepFailure(step, reply.Code, reply.Field, reply.Detail);
        var expected = new RevitCommandReceipt(stepEnvelope.OperationId, stepEnvelope.Command, stepEnvelope.Document.UniqueId, stepRequestSha256, false);
        if (reply.Receipt != expected) return StepFailure(step, RevitCommandCodes.TransactionFailed, null, "the step reply does not answer this step (receipt mismatch)");
        return null;
    }

    // Before any step runs: room in the receipt store for every step receipt plus the batch receipt.
    public static void CheckRoom(CommandBatchArgs batch, int receiptCount, int maxReceipts)
    {
        if (receiptCount + batch.Steps.Count + 1 > maxReceipts)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "receipt store full: " + receiptCount.ToString(CultureInfo.InvariantCulture) + " receipts + "
                + batch.Steps.Count.ToString(CultureInfo.InvariantCulture) + " steps + 1 batch receipt exceed " + maxReceipts.ToString(CultureInfo.InvariantCulture) + "; nothing was written");
    }

    // After the last step, before the batch receipt: every element the batch reports as created or modified must still
    // exist (later steps or Revit's failure processing may have removed it). Null, or the refusal naming the first step
    // that reported the missing element.
    public static RevitCommandResponse? CheckSurvivors(CommandBatchArgs batch, IReadOnlyList<RevitCommandResponse> replies, RevitCommandResponse success, Func<string, bool> exists)
    {
        foreach (var (uid, created) in success.Created.Select(uid => (uid, true)).Concat(success.Modified.Select(uid => (uid, false))))
        {
            if (exists(uid)) continue;
            var index = Enumerable.Range(0, replies.Count).First(i => (created ? replies[i].Created : replies[i].Modified).Contains(uid, StringComparer.OrdinalIgnoreCase));
            return StepFailure(batch.Steps[index], RevitCommandCodes.TransactionFailed, null, "element " + uid + (created ? " created" : " modified") + " by this step no longer exists after the batch");
        }
        return null;
    }

    // The whole batch, without Revit: the caller has opened the outer transaction group. Pre-checks (fresh step
    // operation_ids, receipt store room), then each step in order through runStep (its stand-alone envelope); a refusal,
    // a thrown exception or a reply that does not answer the step stops the batch, rollBack is called once and no later
    // step runs; after the last step the survivors are checked, then persist stores the batch receipt and assimilates the
    // group. If persist throws, rollBack is called and the exception propagates. Returns the reply line.
    public static string Run(RevitCommandEnvelope batch, string batchRequestSha256, Func<string, bool> alreadyUsed, int receiptCount, int maxReceipts,
        Func<RevitCommandEnvelope, RevitCommandResponse> runStep, Func<string, bool> exists, Action rollBack, Func<RevitCommandResponse, string> persist)
    {
        if (batch.Args is not CommandBatchArgs args) throw new ArgumentException("not a command.batch envelope", nameof(batch));
        try
        {
            CheckFreshSteps(args, alreadyUsed);
            CheckRoom(args, receiptCount, maxReceipts);
        }
        catch (RevitCommandException error)
        {
            rollBack();
            return RevitCommandResponse.Failure(error.Code, error.Field, error.Detail).ToJson();
        }
        var replies = new List<RevitCommandResponse>();
        foreach (var step in args.Steps)
        {
            var stepEnvelope = StepEnvelope(batch, step.Index);
            RevitCommandResponse reply;
            try { reply = runStep(stepEnvelope); }
            catch (RevitCommandException error) { reply = RevitCommandResponse.Failure(error.Code, error.Field, error.Detail); }
            catch (Exception error) { reply = RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, null, error.Message); }
            if (CheckStepReply(step, stepEnvelope, stepEnvelope.RequestSha256, reply) is { } refused)
            {
                rollBack();
                return refused.ToJson();
            }
            replies.Add(reply);
        }
        var receipt = new RevitCommandReceipt(batch.OperationId, batch.Command, batch.Document.UniqueId, batchRequestSha256, false);
        var success = Success(receipt, args, replies);
        if (CheckSurvivors(args, replies, success, exists) is { } missing)
        {
            rollBack();
            return missing.ToJson();
        }
        try { return persist(success); }
        catch
        {
            rollBack();
            throw;
        }
    }

    // The batch success: the batch receipt; created/modified/deleted as the net change of all steps in step order
    // (an element created and deleted inside the batch is in none; one created then modified is only created);
    // warnings prefixed with their step; readback {"batch":{"steps":n,"undo_entries":1},"steps":[{index, operation_id,
    // command, response}]} where response is each step's own reply (its readback and receipt).
    public static RevitCommandResponse Success(RevitCommandReceipt receipt, CommandBatchArgs batch, IReadOnlyList<RevitCommandResponse> replies)
    {
        if (replies.Count != batch.Steps.Count) throw new ArgumentException("one reply per step required", nameof(replies));
        static List<string> Union(IEnumerable<IReadOnlyList<string>> lists)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var list in lists) foreach (var uid in list) if (seen.Add(uid)) result.Add(uid);
            return result;
        }
        var created = Union(replies.Select(reply => reply.Created));
        var modified = Union(replies.Select(reply => reply.Modified));
        var deleted = Union(replies.Select(reply => reply.Deleted));
        var createdSet = new HashSet<string>(created, StringComparer.OrdinalIgnoreCase);
        var deletedSet = new HashSet<string>(deleted, StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        for (var i = 0; i < replies.Count; i++)
            foreach (var warning in replies[i].Warnings) warnings.Add("step " + i.ToString(CultureInfo.InvariantCulture) + " " + batch.Steps[i].Command + ": " + warning);
        return RevitCommandResponse.Success(receipt,
            created.Where(uid => !deletedSet.Contains(uid)).ToArray(),
            modified.Where(uid => !createdSet.Contains(uid) && !deletedSet.Contains(uid)).ToArray(),
            deleted.Where(uid => !createdSet.Contains(uid)).ToArray(),
            ReadbackJson(batch, replies), warnings);
    }

    static string ReadbackJson(CommandBatchArgs batch, IReadOnlyList<RevitCommandResponse> replies)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("batch");
            writer.WriteNumber("steps", batch.Steps.Count);
            writer.WriteNumber("undo_entries", 1);
            writer.WriteEndObject();
            writer.WriteStartArray("steps");
            for (var i = 0; i < replies.Count; i++)
            {
                var step = batch.Steps[i];
                writer.WriteStartObject();
                writer.WriteNumber("index", step.Index);
                writer.WriteString("operation_id", step.OperationId);
                writer.WriteString("command", step.Command);
                writer.WritePropertyName("response");
                using (var reply = JsonDocument.Parse(replies[i].ToJson())) reply.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> BatchSpecs = new(BuildBatch);

    public static IReadOnlyList<RevitCommandSpec> Batch => BatchSpecs.Value;

    // The write commands a step may name, from every earlier catalogue part (not All, which contains the batch itself
    // and is still being built when this runs). A test pins this to All minus queries and ExcludedWrites, so a new
    // catalogue part must be listed here deliberately.
    public static IReadOnlyList<RevitCommandSpec> BatchStepCommands => BatchStepSpecs.Value;
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> BatchStepSpecs = new(BuildBatchSteps);

    static IReadOnlyList<RevitCommandSpec> BuildBatchSteps() =>
        V1.Concat(V2).Concat(Views).Concat(Mirror).Concat(Elements).Concat(Structure).Concat(Documentation).Concat(Modify).Concat(Materials)
            .Concat(Families).Concat(Site).Concat(WallProfile).Concat(MaterialTypes).Concat(ExportSave).Concat(RoofExtrusion).Concat(DirectShapes).Concat(Transform).Where(RevitCommandBatch.IsStepCommand).ToArray();

    static IReadOnlyList<RevitCommandSpec> BuildBatch()
    {
        var commands = string.Join(",", BatchStepCommands.Select(spec => "\"" + spec.Name + "\""));
        var schema = Fill("""{"type":"object","properties":{"steps":{"type":"array","description":"The write commands to apply in order, @0@ to @1@; all succeed or none is kept","minItems":@0@,"maxItems":@1@,"items":{"type":"object","properties":{"operation_id":{"type":"string","description":"This step's own operation_id: distinct within the batch, different from the batch operation_id and never used before in the document","minLength":1,"maxLength":128,"pattern":"^[A-Za-z0-9][A-Za-z0-9._:-]*$"},"command":{"type":"string","description":"A catalogue write command (no query, no nested command.batch, no document.save or document.save_copy)","enum":[@2@]},"args":{"description":"The step command's own args object, validated with that command's schema (see its revit_* tool); dry_run is not allowed"}},"required":["operation_id","command","args"],"additionalProperties":false}}},"required":["steps"],"additionalProperties":false}""",
            RevitCommandBatch.MinSteps.ToString(CultureInfo.InvariantCulture), RevitCommandBatch.MaxSteps.ToString(CultureInfo.InvariantCulture), commands);
        return new[]
        {
            new RevitCommandSpec(RevitCommandBatch.Command, true, "Apply 1 to 50 catalogue write commands (each step with its own operation_id, command and args) in order as one all-or-nothing change with a single undo entry: if any step is refused or fails, every step is rolled back and the refusal names the failed step index and command; the reply carries each step's readback and receipt. "
                + "Replaying the same batch operation_id returns the stored reply without writing again. Queries, nested batches, document.save, document.save_copy and dry runs are not steps. Allowed only on enabled test copies.", schema, BindBatch),
        };
    }

    static RevitCommandArgs BindBatch(JsonElement args)
    {
        var steps = new List<RevitBatchStep>();
        var index = 0;
        foreach (var item in args.GetProperty("steps").EnumerateArray())
        {
            steps.Add(BindStep(item, index));
            index++;
        }
        return new CommandBatchArgs(steps);
    }

    // One step. Any refusal is reported under args.steps[index]: a typed refusal keeps its code with its path rewritten;
    // text that cannot be read (a lone surrogate escape) is envelope_invalid at the step, as the protocol reports it
    // for a single command.
    static RevitBatchStep BindStep(JsonElement item, int index)
    {
        try
        {
            var command = item.GetProperty("command").GetString()!;
            var spec = Find(command);
            if (!RevitCommandBatch.IsStepCommand(spec)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "command", command + " cannot be a batch step");
            var stepArgs = item.GetProperty("args");
            // Generic: whatever command offers dry_run, a step never runs as one.
            if (stepArgs.ValueKind == JsonValueKind.Object && stepArgs.TryGetProperty("dry_run", out var dryRun) && dryRun.ValueKind == JsonValueKind.True)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.dry_run", "a batch step cannot be a dry run");
            using (var stepSchema = JsonDocument.Parse(spec.ArgsSchemaJson))
            {
                if (RevitCommandSchema.Validate(stepArgs, stepSchema.RootElement, "args") is { } failure)
                    throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, failure.Field, failure.Detail);
            }
            var typed = spec.Bind(stepArgs);
            var canonical = RevitCommandProtocol.Canonical(stepArgs);
            return new RevitBatchStep(index, item.GetProperty("operation_id").GetString()!, command, typed, canonical);
        }
        catch (RevitCommandException error) { throw new RevitCommandException(error.Code, RevitCommandBatch.StepField(index, error.Field), error.Detail); }
        catch (InvalidOperationException error) { throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, RevitCommandBatch.StepField(index, null), error.Message); }
        catch (ArgumentException error) { throw new RevitCommandException(RevitCommandCodes.EnvelopeInvalid, RevitCommandBatch.StepField(index, null), error.Message); }
    }
}
