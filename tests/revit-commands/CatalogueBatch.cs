using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// P1-07 command.batch contract oracle: the catalogue entry and its schema, per-step validation with the step's own
// schema and binder (refusal paths under args.steps[i]), the step limits, excluded commands, operation_id rules, step
// envelopes and hashes, the guarded client path (copies-only, link and family source guards per step), and the
// Autodesk-free executor decisions in core RevitCommandsBatch.cs: fresh step operation_ids, step reply checks, the
// refusal naming the failed step, the net-change reply with per-step readback and receipts, and batch receipt replay
// through CommandReceiptStore. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorBatch.cs (one TransactionGroup, assimilated or rolled back) is compiled, never run.
static class CatalogueBatchChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string Library = @"C:\ProgramData\Autodesk\RVT 2025\Libraries";
    const string RepoStove = @"C:\Repo\.bim\families\Focus Gyrofocus.rfa";
    const string Pipe = "bim-agentic-os-2025-4242";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(JsonNode args, string path = FixturePath, string operationId = "op-batch") => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = operationId,
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = "command.batch",
        ["args"] = args.DeepClone(),
    };
    static JsonObject Single(string command, JsonNode args, string path = FixturePath, string operationId = "op-single") => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = operationId,
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonObject Wall(double height = 3000, double x1 = 5000) => new()
    {
        ["level"] = "Level 1", ["type"] = "Generic - 200mm", ["height_mm"] = height,
        ["curve"] = new JsonObject { ["kind"] = "line", ["start"] = new JsonArray(0, 0), ["end"] = new JsonArray(x1, 0) },
    };
    static JsonObject Room() => new() { ["level"] = "Level 1", ["point"] = new JsonArray(2000, 1500), ["name"] = "Kitchen" };
    static JsonObject Delete(bool? dryRun = null)
    {
        var args = new JsonObject { ["element_uids"] = new JsonArray(WallUid) };
        if (dryRun is { } value) args["dry_run"] = value;
        return args;
    }
    static JsonObject Step(string operationId, string command, JsonNode args) => new() { ["operation_id"] = operationId, ["command"] = command, ["args"] = args.DeepClone() };
    static JsonObject Steps(params JsonObject[] steps) => new() { ["steps"] = new JsonArray(steps.Select(step => step.DeepClone()).ToArray()) };
    static JsonObject Three() => Steps(Step("s-0", "wall.create", Wall()), Step("s-1", "room.create", Room()), Step("s-2", "element.delete", Delete()));
    static string Hex(int i) => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + i.ToString("x8");

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static void Rejects(string name, JsonNode envelope, string code, string? field)
    {
        var error = Refusal(envelope.ToJsonString());
        Check(error is not null && error.Code == code && error.Field == field, name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
    static RevitCommandEnvelope? Accepts(string name, JsonNode envelope)
    {
        try { var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString()); Check(true, name); return parsed; }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        OperationIds();
        StepEnvelopes();
        ClientPath();
        ExecutorDecisions();
        Replies();
        Receipts();
        Runner();
        Survivors();
        StepGuards();
        ReceiptGrowth();
    }

    static void Catalogue()
    {
        var spec = RevitCommandCatalogue.Find("command.batch");
        Check(spec is { Writes: true, ToolName: "revit_command_batch" } && RevitCommandCatalogue.Batch.Count == 1 && ReferenceEquals(RevitCommandCatalogue.Batch.FirstOrDefault(), spec),
            "command.batch is a write catalogue command with tool revit_command_batch that Find resolves");
        Check(spec is not null && ReferenceEquals(RevitCommandCatalogue.All.Last(), spec), "command.batch is the last catalogue entry (after document.save)");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with command.batch");
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec?.ArgsSchemaJson ?? "null"); RevitCommandSchema.CheckSupported(schema.RootElement); }
        catch (Exception error) { problem = error.Message; }
        Check(problem is null, "command.batch args schema is valid JSON using only enforced keywords", problem ?? "");
        var expected = RevitCommandCatalogue.All.Where(s => s.Writes && s.Name != "command.batch" && s.Name != "document.save").Select(s => s.Name).ToArray();
        var steps = RevitCommandCatalogue.BatchStepCommands.Select(s => s.Name).ToArray();
        Check(steps.SequenceEqual(expected), "batch step commands are exactly every catalogue write except command.batch and document.save, in catalogue order", string.Join(",", steps));
        Check(steps.Contains("wall.create") && steps.Contains("element.delete") && steps.Contains("family.load") && steps.Contains("stair.create") && !steps.Contains("query.levels") && !steps.Contains("view.export_image"),
            "steps include creators, modifiers and family.load and exclude queries and view.export_image");
        Check(RevitCommandBatch.ExcludedWrites.SequenceEqual(new[] { "command.batch", "document.save" }) && RevitCommandBatch.MinSteps == 1 && RevitCommandBatch.MaxSteps == 50, "1 to 50 steps; command.batch and document.save are excluded writes");
        if (spec is not null)
        {
            var schema = JsonNode.Parse(spec.ArgsSchemaJson)!;
            var items = schema["properties"]!["steps"]!;
            var listed = items["items"]!["properties"]!["command"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
            Check(listed.SequenceEqual(steps) && items["minItems"]!.GetValue<int>() == 1 && items["maxItems"]!.GetValue<int>() == 50, "the schema lists the step commands as an enum and bounds steps to 1..50");
            Check(spec.Description.Contains("undo", StringComparison.Ordinal) && spec.Description.Contains("rolled back", StringComparison.Ordinal) && spec.Description.Contains("enabled test copies", StringComparison.Ordinal) && spec.Description.EndsWith('.'),
                "the description states one undo entry, roll back and the copies rule");
        }
        Check(RevitCommandCatalogue.IsDestructive("command.batch"), "command.batch is destructive (a step may delete or change elements)");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("command.batch", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("command.batch", RealPath, copies) == PermissionLevel.WriteRealProject,
            "command.batch is write-copies on a copy and write-real-project elsewhere");
    }

    static void Binding()
    {
        var parsed = Accepts("a three-step batch (wall, room, delete) is accepted", Envelope(Three()));
        var batch = parsed?.Args as CommandBatchArgs;
        Check(batch is { Steps.Count: 3 } && batch.Steps.Select(s => s.Index).SequenceEqual(new[] { 0, 1, 2 }) && batch.Steps[0] is { OperationId: "s-0", Command: "wall.create", Args: WallCreateArgs }
            && batch.Steps[1].Args is RoomCreateArgs && batch.Steps[2].Args is ElementDeleteArgs { DryRun: false }, "steps bind to their commands' typed args in order");
        Check(batch is not null && batch.Steps[0].ArgsJson == RevitCommandProtocol.Canonical(JsonDocument.Parse(Wall().ToJsonString()).RootElement), "a step keeps the canonical JSON of its args");
        Check(parsed is not null && RevitCommandCatalogue.NeedsWriteGuard(parsed), "a batch gets the write admission");
        Accepts("one step is accepted", Envelope(Steps(Step("s-0", "wall.create", Wall()))));
        var fifty = Enumerable.Range(0, 50).Select(i => Step("s-" + i, "wall.create", Wall(3000, 1000 + i))).ToArray();
        var fiftyParsed = Accepts("fifty steps are accepted", Envelope(Steps(fifty)));
        Check(fiftyParsed is not null && RevitCommandProtocol.WireLine(fiftyParsed).Length < RevitCommandProtocol.MaxRequestChars, "a fifty-wall batch fits one 65536-character wire line");
        Rejects("no step is args_invalid at args.steps", Envelope(Steps()), RevitCommandCodes.ArgsInvalid, "args.steps");
        Rejects("51 steps are args_invalid at args.steps", Envelope(Steps(fifty.Append(Step("s-50", "wall.create", Wall())).ToArray())), RevitCommandCodes.ArgsInvalid, "args.steps");
        Rejects("missing steps is args_invalid", Envelope(new JsonObject()), RevitCommandCodes.ArgsInvalid, "args.steps");
        Rejects("an unknown step command is args_invalid at its command", Envelope(Steps(Step("s-0", "wall.create", Wall()), Step("s-1", "wall.delete", Wall()))), RevitCommandCodes.ArgsInvalid, "args.steps[1].command");
        foreach (var command in new[] { "query.levels", "query.elements", "view.export_image", "command.batch", "document.save" })
            Rejects(command + " cannot be a step", Envelope(Steps(Step("s-0", command, new JsonObject()))), RevitCommandCodes.ArgsInvalid, "args.steps[0].command");
        Rejects("a nested batch with steps cannot be a step", Envelope(Steps(Step("s-0", "command.batch", Three()))), RevitCommandCodes.ArgsInvalid, "args.steps[0].command");
        var noHeight = Wall(); noHeight.Remove("height_mm");
        Rejects("a step's args are checked with its own schema (missing height_mm)", Envelope(Steps(Step("s-0", "room.create", Room()), Step("s-1", "wall.create", noHeight))), RevitCommandCodes.ArgsInvalid, "args.steps[1].args.height_mm");
        var extra = Wall(); extra["colour"] = "red";
        Rejects("an unexpected member in a step's args is refused under that step", Envelope(Steps(Step("s-0", "wall.create", extra))), RevitCommandCodes.ArgsInvalid, "args.steps[0].args.colour");
        Rejects("a step's typed unit rule applies (zero height)", Envelope(Steps(Step("s-0", "wall.create", Wall(0)))), RevitCommandCodes.LengthInvalid, "args.steps[0].args.height_mm");
        var far = Wall(); far["curve"]!["start"] = new JsonArray(1e9, 0);
        Rejects("a step's coordinate rule applies with its nested path", Envelope(Steps(Step("s-0", "room.create", Room()), Step("s-1", "room.create", Room()), Step("s-2", "wall.create", far))), RevitCommandCodes.LengthInvalid, "args.steps[2].args.curve.start[0]");
        Rejects("a step whose args are not an object is args_invalid at its args", Envelope(Steps(Step("s-0", "wall.create", JsonValue.Create(5)!))), RevitCommandCodes.ArgsInvalid, "args.steps[0].args");
        Rejects("a dry-run step is refused", Envelope(Steps(Step("s-0", "element.delete", Delete(true)))), RevitCommandCodes.ArgsInvalid, "args.steps[0].args.dry_run");
        Rejects("a dry-run set_parameter step is refused", Envelope(Steps(Step("s-0", "element.set_parameter", new JsonObject { ["element_uid"] = WallUid, ["parameter"] = new JsonObject { ["name"] = "Comments" }, ["value"] = new JsonObject { ["kind"] = "text", ["text"] = "x" }, ["dry_run"] = true }))), RevitCommandCodes.ArgsInvalid, "args.steps[0].args.dry_run");
        Accepts("dry_run false is an ordinary step", Envelope(Steps(Step("s-0", "element.delete", Delete(false)))));
        var stepExtra = Step("s-0", "wall.create", Wall()); stepExtra["note"] = "x";
        Rejects("an unexpected member in a step is refused", Envelope(Steps(stepExtra)), RevitCommandCodes.ArgsInvalid, "args.steps[0].note");
        var noArgs = Step("s-0", "wall.create", Wall()); noArgs.Remove("args");
        Rejects("a step without args is refused", Envelope(Steps(noArgs)), RevitCommandCodes.ArgsInvalid, "args.steps[0].args");
        var a = RevitCommandProtocol.Parse(Envelope(Steps(Step("s-0", "wall.create", Wall()))).ToJsonString());
        var b = RevitCommandProtocol.Parse("""{"args":{"steps":[{"args":{"curve":{"end":[5.0e3,0],"kind":"line","start":[0,-0]},"height_mm":3000.0,"type":"Generic - 200mm","level":"Level 1"},"command":"wall.create","operation_id":"s-0"}]},"command":"command.batch","document":{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b","path":"C:\\Repo\\.bim\\native-runs\\run-1\\BIM-Agentic-Test.rvt"},"operation_id":"op-batch","protocol_version":1}""");
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "a batch has one canonical form and hash whatever the member order and number spelling (replay key)", a.CanonicalJson + " vs " + b.CanonicalJson);
        var reordered = RevitCommandProtocol.Parse(Envelope(Steps(Step("s-1", "room.create", Room()), Step("s-0", "wall.create", Wall()))).ToJsonString());
        Check(reordered.RequestSha256 != RevitCommandProtocol.Parse(Envelope(Steps(Step("s-0", "wall.create", Wall()), Step("s-1", "room.create", Room()))).ToJsonString()).RequestSha256, "step order is part of the request hash");
    }

    static void OperationIds()
    {
        Rejects("a step without operation_id is refused", Envelope(Steps(new JsonObject { ["command"] = "wall.create", ["args"] = Wall() })), RevitCommandCodes.ArgsInvalid, "args.steps[0].operation_id");
        Rejects("a step operation_id outside the operation_id pattern is refused", Envelope(Steps(Step("-bad", "wall.create", Wall()))), RevitCommandCodes.ArgsInvalid, "args.steps[0].operation_id");
        Rejects("a 129-character step operation_id is refused", Envelope(Steps(Step(new string('a', 129), "wall.create", Wall()))), RevitCommandCodes.ArgsInvalid, "args.steps[0].operation_id");
        Accepts("a 128-character step operation_id is accepted", Envelope(Steps(Step(new string('a', 128), "wall.create", Wall()))));
        Rejects("duplicate step operation_ids are refused at the second", Envelope(Steps(Step("s-0", "wall.create", Wall()), Step("s-1", "room.create", Room()), Step("s-0", "room.create", Room()))), RevitCommandCodes.ArgsInvalid, "args.steps[2].operation_id");
        Rejects("a step operation_id equal to the batch operation_id is refused", Envelope(Steps(Step("s-0", "wall.create", Wall()), Step("op-batch", "room.create", Room()))), RevitCommandCodes.ArgsInvalid, "args.steps[1].operation_id");
        Accepts("step operation_ids compare ordinally (S-0 and s-0 differ)", Envelope(Steps(Step("s-0", "wall.create", Wall()), Step("S-0", "room.create", Room()))));
        var error = Refusal(Envelope(Steps(Step("s-0", "wall.create", Wall()), Step("s-0", "room.create", Room()))).ToJsonString());
        Check(error?.Detail?.Contains("steps[0]", StringComparison.Ordinal) == true, "the duplicate refusal names the first step using it", error?.Detail ?? "");
    }

    static void StepEnvelopes()
    {
        var batch = RevitCommandProtocol.Parse(Envelope(Three()).ToJsonString());
        var step = RevitCommandBatch.StepEnvelope(batch, 1);
        var alone = RevitCommandProtocol.Parse(Single("room.create", Room(), FixturePath, "s-1").ToJsonString());
        Check(step.OperationId == "s-1" && step.Command == "room.create" && step.Document == batch.Document && step.ProtocolVersion == 1 && step.Args == alone.Args,
            "a step envelope carries its operation_id and command on the batch's document with the same typed args");
        Check(step.CanonicalJson == alone.CanonicalJson && step.RequestSha256 == alone.RequestSha256 && step.RequestSha256 == CommandWire.Sha256(step.CanonicalJson),
            "a step envelope is the canonical stand-alone request; its hash is the SHA-256 of its canonical text");
        Check(RevitCommandBatch.StepEnvelope(batch, 0).Args is WallCreateArgs && RevitCommandBatch.StepEnvelope(batch, 2).Args is ElementDeleteArgs, "every step has its envelope");
        var notBatch = false;
        try { RevitCommandBatch.StepEnvelope(alone, 0); } catch (ArgumentException) { notBatch = true; }
        Check(notBatch, "StepEnvelope refuses an envelope that is not a batch");
        var guarded = RevitCommandBatch.GuardedArgs(batch).ToArray();
        Check(guarded.Length == 3 && guarded.Select(g => g.Prefix).SequenceEqual(new[] { "args.steps[0].", "args.steps[1].", "args.steps[2]." }) && guarded[1].Args is RoomCreateArgs, "guarded args of a batch are its steps with their field prefixes");
        Check(RevitCommandBatch.GuardedArgs(alone).Single() is { Prefix: "", Args: RoomCreateArgs }, "guarded args of a single command are its own args with no prefix");
        Check(RevitCommandBatch.StepField(3, null) == "args.steps[3]" && RevitCommandBatch.StepField(3, "") == "args.steps[3]" && RevitCommandBatch.StepField(12, "args.level") == "args.steps[12].args.level", "step refusal paths are args.steps[i] plus the step-relative path");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" }) with { RepositoryDirectory = @"C:\Repo", ContentLibraryRoots = new[] { Library } };
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var ok = RevitCommandService.Run(Pipe, Envelope(Three()).ToJsonString(), policy, Transport, noLinks);
        Check(!ok.IsError && forwarded.Count == 1 && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(Three()).ToJsonString())), "a batch on an enabled copy is forwarded once as the canonical wire line", ok.Line);
        var real = RevitCommandService.Run(Pipe, Envelope(Three(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(real.IsError && !real.Forwarded && Code(real.Line) == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, "a batch on a real project is document_not_enabled and never forwarded", real.Line);
        var linked = RevitCommandService.Run(Pipe, Envelope(Three()).ToJsonString(), policy, Transport, new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null));
        Check(linked.IsError && Code(linked.Line) == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == 1, "a batch through a linked directory is document_link_refused");
        var invalid = RevitCommandService.Run(Pipe, Envelope(Steps(Step("s-0", "room.create", Room()), Step("s-1", "wall.create", Wall(0)))).ToJsonString(), policy, Transport, noLinks);
        Check(invalid.IsError && !invalid.Forwarded && Code(invalid.Line) == RevitCommandCodes.LengthInvalid && Field(invalid.Line) == "args.steps[1].args.height_mm" && forwarded.Count == 1, "an invalid step refuses the whole batch before the pipe, naming the step path", invalid.Line);
        var outside = RevitCommandService.Run(Pipe, Envelope(Steps(Step("s-0", "family.load", new JsonObject { ["path"] = RepoStove }), Step("s-1", "family.load", new JsonObject { ["path"] = @"C:\Users\owner\Downloads\Stove.rfa" }))).ToJsonString(), policy, Transport, noLinks);
        Check(outside.IsError && !outside.Forwarded && Code(outside.Line) == RevitCommandCodes.FamilySourceRefused && Field(outside.Line) == "args.steps[1].args.path" && forwarded.Count == 1, "a family.load step from outside the sources is family_source_refused at args.steps[1].args.path before the pipe", outside.Line);
        var inside = RevitCommandService.Run(Pipe, Envelope(Steps(Step("s-0", "family.load", new JsonObject { ["path"] = RepoStove }))).ToJsonString(), policy, Transport, noLinks);
        Check(!inside.IsError && forwarded.Count == 2, "a family.load step from .bim/families is forwarded", inside.Line);
        var single = RevitCommandService.Run(Pipe, Single("family.load", new JsonObject { ["path"] = @"C:\Users\owner\Downloads\Stove.rfa" }).ToJsonString(), policy, Transport, noLinks);
        Check(single.IsError && Code(single.Line) == RevitCommandCodes.FamilySourceRefused && Field(single.Line) == "args.path" && forwarded.Count == 2, "a single family.load keeps its args.path refusal field", single.Line);
        string? WrongReceipt(string pipe, string line) => Answer(line.Replace("\"op-batch\"", "\"op-other\"", StringComparison.Ordinal));
        var mismatched = RevitCommandService.Run(Pipe, Envelope(Three()).ToJsonString(), policy, WrongReceipt, noLinks);
        Check(mismatched.IsError && Code(mismatched.Line) == RevitCommandCodes.ResponseInvalid, "a batch success that answers another operation_id is response_invalid", mismatched.Line);
    }

    static string Code(string line) => JsonNode.Parse(line)!["code"]!.GetValue<string>();
    static string? Field(string line) => JsonNode.Parse(line)!["field"]?.GetValue<string>();

    static string Answer(string wireLine)
    {
        using var wire = JsonDocument.Parse(wireLine);
        var commandText = wire.RootElement.GetProperty("Command").GetRawText();
        using var command = JsonDocument.Parse(commandText);
        var receipt = new RevitCommandReceipt(command.RootElement.GetProperty("operation_id").GetString()!, command.RootElement.GetProperty("command").GetString()!,
            command.RootElement.GetProperty("document").GetProperty("unique_id").GetString()!, CommandWire.Sha256(commandText), false);
        return RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>()).ToJson();
    }

    static (RevitCommandEnvelope Batch, CommandBatchArgs Args, RevitCommandEnvelope[] Steps) Parsed()
    {
        var batch = RevitCommandProtocol.Parse(Envelope(Three()).ToJsonString());
        var args = (CommandBatchArgs)batch.Args;
        return (batch, args, args.Steps.Select(step => RevitCommandBatch.StepEnvelope(batch, step.Index)).ToArray());
    }

    static RevitCommandResponse StepReply(RevitCommandEnvelope step, string[] created, string[] modified, string[] deleted, string readback, params string[] warnings) =>
        RevitCommandResponse.Success(new RevitCommandReceipt(step.OperationId, step.Command, step.Document.UniqueId, step.RequestSha256, false), created, modified, deleted, readback, warnings);

    static void ExecutorDecisions()
    {
        var (_, args, steps) = Parsed();
        Check(Thrown(() => RevitCommandBatch.CheckFreshSteps(args, _ => false)) is null, "fresh step operation_ids pass");
        var used = Thrown(() => RevitCommandBatch.CheckFreshSteps(args, id => id == "s-1"));
        Check(used is { Code: RevitCommandCodes.OperationIdConflict, Field: "args.steps[1].operation_id" } && (used.Detail ?? "").Contains("room.create", StringComparison.Ordinal) && (used.Detail ?? "").Contains("nothing was written", StringComparison.Ordinal),
            "a step operation_id already in the receipt store is operation_id_conflict naming the step, before anything runs", used?.Detail ?? "");
        Check(RevitCommandCodes.ExecutorReserved.Contains(RevitCommandCodes.OperationIdConflict), "operation_id_conflict stays an executor-reserved code");

        var good = StepReply(steps[0], new[] { Hex(1) }, Array.Empty<string>(), Array.Empty<string>(), """{"wall":{}}""");
        Check(RevitCommandBatch.CheckStepReply(args.Steps[0], steps[0], steps[0].RequestSha256, good) is null, "a first-run success answering the step is accepted");
        var failed = RevitCommandBatch.CheckStepReply(args.Steps[2], steps[2], steps[2].RequestSha256, RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.element_uids[0]", "no element"));
        Check(failed is { Status: RevitCommandResponse.Failed, Code: RevitCommandCodes.ReferenceNotFound, Field: "args.steps[2].args.element_uids[0]" } && failed.Receipt is null && failed.Created.Count == 0
            && (failed.Detail ?? "").StartsWith("step 2 (element.delete, operation_id s-2) failed: no element", StringComparison.Ordinal) && (failed.Detail ?? "").Contains("whole batch was rolled back", StringComparison.Ordinal),
            "a failed step becomes the batch refusal: its code, its field under args.steps[2], the step index, command and operation_id, and the roll back", failed?.Detail ?? "");
        var noField = RevitCommandBatch.CheckStepReply(args.Steps[1], steps[1], steps[1].RequestSha256, RevitCommandResponse.Failure(RevitCommandCodes.TransactionFailed, null, null));
        Check(noField is { Code: RevitCommandCodes.TransactionFailed, Field: "args.steps[1]" } && (noField.Detail ?? "").Contains("failed: transaction_failed", StringComparison.Ordinal), "a step failure without field or detail is reported at args.steps[i] with its code", noField?.Detail ?? "");
        var other = RevitCommandBatch.CheckStepReply(args.Steps[0], steps[0], steps[0].RequestSha256, StepReply(steps[1], Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "{}"));
        Check(other is { Code: RevitCommandCodes.TransactionFailed, Field: "args.steps[0]" }, "a success answering another step is refused as transaction_failed");
        var replayed = RevitCommandBatch.CheckStepReply(args.Steps[0], steps[0], steps[0].RequestSha256, good with { Receipt = good.Receipt! with { Replayed = true } });
        Check(replayed is { Code: RevitCommandCodes.TransactionFailed }, "a replayed step reply is refused (a step never replays inside a batch)");
        var wrongHash = RevitCommandBatch.CheckStepReply(args.Steps[0], steps[0], new string('0', 64), good);
        Check(wrongHash is { Code: RevitCommandCodes.TransactionFailed }, "a step reply bound to another request hash is refused");
        Check(RevitCommandResponse.Parse(failed!.ToJson()) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.steps[2].args.element_uids[0]" }, "the batch refusal round-trips through the strict response reader");
    }

    static void Replies()
    {
        var (batch, args, steps) = Parsed();
        string A = Hex(1), B = Hex(2), C = Hex(3), D = Hex(4);
        var replies = new[]
        {
            StepReply(steps[0], new[] { A, B }, Array.Empty<string>(), Array.Empty<string>(), """{"wall":{"unique_id":"w"}}""", "joined"),
            StepReply(steps[1], Array.Empty<string>(), new[] { A.ToUpperInvariant(), C }, Array.Empty<string>(), """{"room":{"area_m2":12}}"""),
            StepReply(steps[2], Array.Empty<string>(), Array.Empty<string>(), new[] { B, D }, """{"delete":{"confirmed":true}}""", "cut", "gone"),
        };
        var receipt = new RevitCommandReceipt(batch.OperationId, batch.Command, batch.Document.UniqueId, batch.RequestSha256, false);
        var response = RevitCommandBatch.Success(receipt, args, replies);
        Check(response.IsSuccess && response.Receipt == receipt, "the batch success carries the batch receipt");
        Check(response.Created.SequenceEqual(new[] { A }) && response.Modified.SequenceEqual(new[] { C }) && response.Deleted.SequenceEqual(new[] { D }),
            "created/modified/deleted are the net change: created-then-deleted is in none, created-then-modified only created (UniqueIds compare case-insensitively)",
            string.Join(",", response.Created) + " | " + string.Join(",", response.Modified) + " | " + string.Join(",", response.Deleted));
        Check(response.Warnings.SequenceEqual(new[] { "step 0 wall.create: joined", "step 2 element.delete: cut", "step 2 element.delete: gone" }), "warnings are prefixed with their step index and command", string.Join(" / ", response.Warnings));
        var readback = JsonNode.Parse(response.ReadbackJson!)!;
        Check(readback["batch"]!["steps"]!.GetValue<int>() == 3 && readback["batch"]!["undo_entries"]!.GetValue<int>() == 1 && readback["steps"]!.AsArray().Count == 3, "the readback states the step count and one undo entry");
        var ok = true;
        for (var i = 0; i < 3; i++)
        {
            var entry = readback["steps"]![i]!;
            var stepResponse = RevitCommandResponse.Parse(entry["response"]!.ToJsonString());
            ok &= entry["index"]!.GetValue<int>() == i && entry["operation_id"]!.GetValue<string>() == args.Steps[i].OperationId && entry["command"]!.GetValue<string>() == args.Steps[i].Command
                && stepResponse.Receipt == replies[i].Receipt && JsonNode.DeepEquals(JsonNode.Parse(stepResponse.ReadbackJson!), JsonNode.Parse(replies[i].ReadbackJson!))
                && stepResponse.Created.SequenceEqual(replies[i].Created) && stepResponse.Deleted.SequenceEqual(replies[i].Deleted);
        }
        Check(ok, "each step entry carries its index, operation_id, command and its own reply (readback and receipt)");
        var round = RevitCommandResponse.Parse(response.ToJson());
        Check(round.Receipt == receipt && round.Created.SequenceEqual(response.Created) && round.ReadbackJson is not null, "the batch reply round-trips through the strict response reader");
        var mismatch = false;
        try { RevitCommandBatch.Success(receipt, args, replies.Take(2).ToArray()); } catch (ArgumentException) { mismatch = true; }
        Check(mismatch, "a batch success needs exactly one reply per step");
    }

    static void Receipts()
    {
        var (batch, args, steps) = Parsed();
        var request = new CommandRequest(batch, batch.RequestSha256);
        var receipt = new RevitCommandReceipt(batch.OperationId, batch.Command, batch.Document.UniqueId, request.RequestSha256, false);
        var replies = steps.Select(step => StepReply(step, new[] { Hex(10 + Array.IndexOf(steps, step)) }, Array.Empty<string>(), Array.Empty<string>(), "{}")).ToArray();
        var response = RevitCommandBatch.Success(receipt, args, replies);
        var store = new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Length; i++) store[steps[i].OperationId] = CommandReceiptStore.Record(new CommandRequest(steps[i], steps[i].RequestSha256), replies[i]);
        store[batch.OperationId] = CommandReceiptStore.Record(request, response);
        var reread = CommandReceiptStore.Parse(CommandReceiptStore.SerializeBounded(store));
        Check(reread.Count == 4 && reread.ContainsKey("op-batch") && reread.ContainsKey("s-0"), "the batch receipt and the step receipts persist together in the receipt store");
        Check(CommandReceiptStore.Decide(reread, batch.OperationId, request.RequestSha256) == CommandReceiptAction.Replay, "re-sending the same batch replays it");
        var changed = RevitCommandProtocol.Parse(Envelope(Steps(Step("s-0", "wall.create", Wall(3100)))).ToJsonString());
        Check(CommandReceiptStore.Decide(reread, changed.OperationId, changed.RequestSha256) == CommandReceiptAction.Conflict, "the batch operation_id with different steps is a conflict");
        var replay = RevitCommandResponse.Parse(CommandReceiptStore.ReplayJson(reread["op-batch"]));
        Check(replay.Receipt == receipt with { Replayed = true } && replay.Created.SequenceEqual(response.Created) && replay.ReadbackJson == response.ReadbackJson, "the replay returns the stored batch reply (per-step readback included) with replayed true");
        Check(Thrown(() => RevitCommandBatch.CheckFreshSteps(args, reread.ContainsKey)) is { Code: RevitCommandCodes.OperationIdConflict, Field: "args.steps[0].operation_id" },
            "after a stored batch its step operation_ids cannot run again in a new batch");
    }

    // A fake step runner and transaction group around RevitCommandBatch.Run: counts calls, roll backs and persists.
    sealed class Fake
    {
        public List<string> Ran { get; } = new();
        public int RollBacks;
        public int Persists;
        public RevitCommandResponse? Persisted;
    }

    static string RunFake(RevitCommandEnvelope batch, Fake fake, Func<RevitCommandEnvelope, RevitCommandResponse> reply, Func<string, bool>? exists = null,
        Func<string, bool>? used = null, int receiptCount = 0, Func<RevitCommandResponse, string>? persist = null) =>
        RevitCommandBatch.Run(batch, batch.RequestSha256, used ?? (_ => false), receiptCount, 2000,
            step => { fake.Ran.Add(step.OperationId); return reply(step); },
            exists ?? (_ => true),
            () => fake.RollBacks++,
            persist ?? (response => { fake.Persists++; fake.Persisted = response; return "persisted"; }));

    static RevitCommandResponse Ok(RevitCommandEnvelope step) => StepReply(step, new[] { Hex(20 + int.Parse(step.OperationId[2..])) }, Array.Empty<string>(), Array.Empty<string>(), "{}");

    static void Runner()
    {
        var (batch, args, _) = Parsed();
        var fake = new Fake();
        var line = RunFake(batch, fake, Ok);
        Check(line == "persisted" && fake.Ran.SequenceEqual(new[] { "s-0", "s-1", "s-2" }) && fake.RollBacks == 0 && fake.Persists == 1 && fake.Persisted?.Receipt == new RevitCommandReceipt("op-batch", "command.batch", Uid, batch.RequestSha256, false),
            "every step runs in order, nothing is rolled back and the batch success is persisted once with the batch receipt");
        fake = new Fake();
        line = RunFake(batch, fake, step => step.OperationId == "s-1" ? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.level", "no level named Level 9") : Ok(step));
        Check(fake.Ran.SequenceEqual(new[] { "s-0", "s-1" }) && fake.RollBacks == 1 && fake.Persists == 0 && Code(line) == RevitCommandCodes.ReferenceNotFound && Field(line) == "args.steps[1].args.level",
            "a step that throws stops the batch: the next step never runs, the group is rolled back once, nothing is persisted, the field is args.steps[1].args.level", line);
        fake = new Fake();
        line = RunFake(batch, fake, step => step.OperationId == "s-0" ? throw new InvalidOperationException("Revit said no") : Ok(step));
        Check(fake.Ran.Count == 1 && fake.RollBacks == 1 && fake.Persists == 0 && Code(line) == RevitCommandCodes.TransactionFailed && Field(line) == "args.steps[0]" && line.Contains("Revit said no", StringComparison.Ordinal),
            "any other exception from a step is transaction_failed at args.steps[i] with its message", line);
        fake = new Fake();
        line = RunFake(batch, fake, step => step.OperationId == "s-2" ? RevitCommandResponse.Failure(RevitCommandCodes.ArgsInvalid, "args.confirm_dependents", "unconfirmed") : Ok(step));
        Check(fake.Ran.Count == 3 && fake.RollBacks == 1 && fake.Persists == 0 && Field(line) == "args.steps[2].args.confirm_dependents", "a failure reply from the last step rolls everything back", line);
        fake = new Fake();
        line = RunFake(batch, fake, step => StepReply(RevitCommandBatch.StepEnvelope(batch, 0), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), "{}"));
        Check(fake.Ran.SequenceEqual(new[] { "s-0", "s-1" }) && fake.RollBacks == 1 && Code(line) == RevitCommandCodes.TransactionFailed && Field(line) == "args.steps[1]", "a reply answering another step stops the batch at that step", line);
        fake = new Fake();
        var thrown = false;
        try { RunFake(batch, fake, Ok, persist: _ => throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "receipt store full")); }
        catch (RevitCommandException error) { thrown = error.Detail == "receipt store full"; }
        Check(thrown && fake.Ran.Count == 3 && fake.RollBacks == 1, "a persist that throws rolls the group back once and the error propagates");
        fake = new Fake();
        line = RunFake(batch, fake, Ok, used: id => id == "s-2");
        Check(fake.Ran.Count == 0 && fake.RollBacks == 1 && fake.Persists == 0 && Code(line) == RevitCommandCodes.OperationIdConflict && Field(line) == "args.steps[2].operation_id", "a used step operation_id refuses the batch before any step runs", line);
        var twenty = RevitCommandProtocol.Parse(Envelope(Steps(Enumerable.Range(0, 20).Select(i => Step("s-" + i, "wall.create", Wall(3000, 1000 + i))).ToArray())).ToJsonString());
        fake = new Fake();
        line = RunFake(twenty, fake, Ok, receiptCount: 1990);
        Check(fake.Ran.Count == 0 && fake.RollBacks == 1 && fake.Persists == 0 && Code(line) == RevitCommandCodes.TransactionFailed && line.Contains("receipt store full", StringComparison.Ordinal),
            "1990 stored receipts + a 20-step batch + its receipt exceed 2000: refused before any step runs", line);
        fake = new Fake();
        RunFake(twenty, fake, Ok, receiptCount: 1979);
        Check(fake.Ran.Count == 20 && fake.Persists == 1, "1979 stored receipts + 20 steps + 1 exactly fill 2000: the batch runs");
        Check(Thrown(() => RevitCommandBatch.CheckRoom((CommandBatchArgs)twenty.Args, 1980, 2000)) is { Code: RevitCommandCodes.TransactionFailed } && Thrown(() => RevitCommandBatch.CheckRoom(args, 1996, 2000)) is null,
            "CheckRoom counts every step receipt plus the batch receipt");
        Check(RevitCommandBatch.StepReplyField(2, "operation_id") == "args.steps[2].operation_id" && RevitCommandBatch.StepReplyField(2, "command") == "args.steps[2].command"
            && RevitCommandBatch.StepReplyField(2, "args") == "args.steps[2].args" && RevitCommandBatch.StepReplyField(2, null) == "args.steps[2]"
            && RevitCommandBatch.StepReplyField(2, "document.unique_id") == "document.unique_id" && RevitCommandBatch.StepReplyField(2, "receipt") == "receipt",
            "only the step's own fields (args..., operation_id, command) move under args.steps[i]; batch-level fields are kept");
        fake = new Fake();
        line = RunFake(batch, fake, step => step.OperationId == "s-1" ? RevitCommandResponse.Failure(RevitCommandCodes.DocumentConflict, "document.path", "moved") : Ok(step));
        Check(Field(line) == "document.path" && line.Contains("step 1 (room.create", StringComparison.Ordinal), "a document-level step failure keeps its field and still names the step", line);
    }

    static void Survivors()
    {
        var (batch, _, _) = Parsed();
        var fake = new Fake();
        var line = RunFake(batch, fake, Ok, exists: uid => uid != Hex(21));
        Check(fake.Ran.Count == 3 && fake.RollBacks == 1 && fake.Persists == 0 && Code(line) == RevitCommandCodes.TransactionFailed && Field(line) == "args.steps[1]" && line.Contains(Hex(21) + " created", StringComparison.Ordinal),
            "an element a step created that no longer exists after the batch refuses it, naming the step that created it", line);
        fake = new Fake();
        line = RunFake(batch, fake, step => step.OperationId == "s-2" ? StepReply(step, Array.Empty<string>(), new[] { Hex(99) }, Array.Empty<string>(), "{}") : Ok(step), exists: uid => uid != Hex(99));
        Check(fake.RollBacks == 1 && fake.Persists == 0 && Field(line) == "args.steps[2]" && line.Contains(Hex(99) + " modified", StringComparison.Ordinal), "a modified element that no longer exists names the step that modified it", line);
        fake = new Fake();
        RunFake(batch, fake, step => step.OperationId == "s-2" ? StepReply(step, Array.Empty<string>(), Array.Empty<string>(), new[] { Hex(20) }, "{}") : Ok(step), exists: uid => uid != Hex(20));
        Check(fake.RollBacks == 0 && fake.Persists == 1, "an element created then deleted inside the batch is not required to exist");
    }

    static void StepGuards()
    {
        var withDryRun = RevitCommandCatalogue.BatchStepCommands.Where(spec => JsonNode.Parse(spec.ArgsSchemaJson)!["properties"]?["dry_run"] is not null).ToArray();
        Check(withDryRun.Select(spec => spec.Name).Contains("element.set_parameter") && withDryRun.Select(spec => spec.Name).Contains("element.delete"), "the step commands offering dry_run include the modify commands", string.Join(",", withDryRun.Select(spec => spec.Name)));
        foreach (var spec in withDryRun)
            Rejects(spec.Name + " with dry_run true is not a step", Envelope(Steps(Step("s-0", "room.create", Room()), Step("s-1", spec.Name, new JsonObject { ["dry_run"] = true }))), RevitCommandCodes.ArgsInvalid, "args.steps[1].args.dry_run");
        var surrogate = Envelope(Steps(Step("s-0", "room.create", Room()), Step("s-1", "wall.create", Wall()))).ToJsonString().Replace("Generic - 200mm", "\\ud800", StringComparison.Ordinal);
        var error = Refusal(surrogate);
        Check(error is { Code: RevitCommandCodes.EnvelopeInvalid, Field: "args.steps[1]" }, "unreadable text in a step is envelope_invalid at that step", error is null ? "accepted" : error.Code + " at " + error.Field);
    }

    static void ReceiptGrowth()
    {
        var batch = RevitCommandProtocol.Parse(Envelope(Steps(Enumerable.Range(0, 50).Select(i => Step("s-" + i, "family.load", new JsonObject { ["path"] = RepoStove })).ToArray())).ToJsonString());
        var args = (CommandBatchArgs)batch.Args;
        var steps = args.Steps.Select(step => RevitCommandBatch.StepEnvelope(batch, step.Index)).ToArray();
        string Readback(int i) => new JsonObject { ["family"] = new JsonObject { ["name"] = "Family " + i.ToString("00"), ["types"] = new JsonArray(Enumerable.Range(0, 48).Select(j => (JsonNode?)new JsonObject { ["name"] = "Type " + j.ToString("000"), ["unique_id"] = Hex(1000 * i + j) }).ToArray()) } }.ToJsonString();
        var replies = steps.Select((step, i) => StepReply(step, new[] { Hex(i + 1) }, Array.Empty<string>(), Array.Empty<string>(), Readback(i))).ToArray();
        var request = new CommandRequest(batch, batch.RequestSha256);
        var response = RevitCommandBatch.Success(new RevitCommandReceipt(batch.OperationId, batch.Command, batch.Document.UniqueId, request.RequestSha256, false), args, replies);
        var stepsOnly = new Dictionary<string, StoredCommandReceipt>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Length; i++) stepsOnly[steps[i].OperationId] = CommandReceiptStore.Record(new CommandRequest(steps[i], steps[i].RequestSha256), replies[i]);
        var all = new Dictionary<string, StoredCommandReceipt>(stepsOnly, StringComparer.Ordinal) { [batch.OperationId] = CommandReceiptStore.Record(request, response) };
        var stepBytes = System.Text.Encoding.UTF8.GetByteCount(CommandReceiptStore.Serialize(stepsOnly));
        var totalBytes = System.Text.Encoding.UTF8.GetByteCount(CommandReceiptStore.SerializeBounded(all));
        Check(Readback(0).Length is > 3500 and < 4500, "each step readback is family.load sized (about 4 KB)", Readback(0).Length.ToString());
        Check(totalBytes < 800_000 && totalBytes - stepBytes < 400_000 && totalBytes > 2 * 50 * 3500,
            "a 50-step batch of 4 KB replies stores under 0.8 MB (step receipts plus a batch receipt of about the same size; the 4 MB store holds about six)", totalBytes + " bytes, batch receipt " + (totalBytes - stepBytes));
    }
}
