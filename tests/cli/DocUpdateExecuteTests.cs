using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Cli;
using Bim.Core;

// P4-12 `doc-update ... --probe-dependents process-pipe-name [--accept uid]... --execute --expected-document <sha256>`:
// the P4-10 probed plan, checked against the SHA-256 of the dry plan a human reviewed, written, then run through the
// revit-command transport. Oracles:
//   1. In process (DocUpdateCommand.Execute, the code the CLI runs, with a fake executor behind the pipe function): the
//      wire lines are exactly those of DocUpdateDependents.Update (the library) on an independent fake with the same
//      state: the dry runs, then every write of the plan in DocReplace order (deletes with exactly the planned
//      confirm_dependents, then creates); the file holds the dry plan bytes; the realized map printed is the library's.
//   2. The first failure stops the run: a typed per-step report (done / failed / not_sent); a write whose reply was
//      lost (no reply, broken pipe) is reported failed with reply_lost true and is never sent again; nothing after it is
//      sent.
//   3. --execute needs --probe-dependents and --expected-document (and the reverse); a plan whose SHA-256 differs from
//      --expected-document is refused before any write is sent and writes no file. Without --execute the dry plan and
//      summary are unchanged byte for byte.
// Unit-tested only; no Revit.
internal static class DocUpdateExecuteTests
{
    const string Pipe = "bim-agentic-os-2025-4243";
    const string PlanChanged = "doc_update_plan_changed";
    static readonly Provenance provenance = new("cli-doc-update-execute-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-12\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd12");
    static readonly RevitCommandPolicy enabled = new(new[] { @"C:\Repo\.bim\native-runs" });

    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("cli-doc-update-execute:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static Guid RoomOf(int room) => Id("room" + N(room));
    static Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
    static Guid Door(int room) => Id("door" + N(room));

    static string Fake(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    static Project Build(params double[] elevations)
    {
        var elements = new List<Element>();
        for (var room = 0; room < elevations.Length; room++)
        {
            foreach (var element in RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements)
                elements.Add(element.Kind == "level" ? element with { GeometryMetres = new[] { elevations[room] } } : element);
            var x = room * 10 + 0.1;
            elements.Add(new(Door(room), "door", new[] { x + 1, 0.1, x + 1.9, 0.1, 0, 2.1 }, new[] { Role(room, "south"), Role(room, "level") }, provenance));
        }
        return new(projectId, 0, elements.ToArray());
    }

    static RevitBindings Bindings(Project project) => new(document,
        project.Elements.Where(element => element.Kind == "level").ToDictionary(element => element.Id, element => "Level " + element.Id.ToString("N")[..8]),
        Array.Empty<RevitWallTypeBinding>(), null, null);

    static IEnumerable<string> Keys(DocumentationSet set) =>
        set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key));

    static Dictionary<string, string> RealizedOf(Project project)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in Keys(DocDefaults.From(project))) map[DocRegeneration.ItemSource(key)] = Fake("stale:" + key);
        foreach (var element in project.Elements) map[DocRegeneration.ElementSource(element.Id)] = Fake("element:" + element.Id.ToString("D"));
        return map;
    }

    static string BindingsJson(RevitBindings bindings) =>
        "{\"document\":{\"path\":" + JsonSerializer.Serialize(bindings.Document.Path) + ",\"unique_id\":" + JsonSerializer.Serialize(bindings.Document.UniqueId) + "},\"levels\":{"
        + string.Join(",", bindings.Levels.Select(pair => JsonSerializer.Serialize(pair.Key.ToString("D")) + ":" + JsonSerializer.Serialize(pair.Value))) + "}}";

    static string MapJson(IEnumerable<KeyValuePair<string, string>> map) =>
        "{" + string.Join(",", map.Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + JsonSerializer.Serialize(pair.Value))) + "}";

    static string DeleteReadback(IReadOnlyList<string> targets, IReadOnlyList<string> dependents, bool dryRun)
    {
        var text = string.Join("\n", dependents.Select(uid => uid.ToLowerInvariant()).OrderBy(uid => uid, StringComparer.Ordinal));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("delete");
            writer.WriteBoolean("dry_run", dryRun);
            writer.WriteStartArray("targets");
            foreach (var uid in targets) writer.WriteStringValue(uid);
            writer.WriteEndArray();
            writer.WriteStartArray("dependents");
            foreach (var uid in dependents) writer.WriteStringValue(uid);
            writer.WriteEndArray();
            writer.WriteNumber("dependents_count", dependents.Count);
            writer.WriteString("dependents_sha256", sha);
            writer.WriteBoolean("dependents_listed", true);
            writer.WriteNumber("deleted_count", targets.Count + dependents.Count);
            writer.WriteBoolean("confirmed", !dryRun);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The add-in behind the process pipe: element.delete dry runs (rolled back, no receipt), real deletes (refused
    // args_invalid at args.confirm_dependents unless confirm_dependents names exactly the dependents present now; a
    // missing target is reference_not_found) and any other command as a create (one new UniqueId). A write that ran
    // stores a receipt by operation id: a resend replays it (replayed true, nothing executed). Executed lists the
    // operation ids of writes that ran, in order. LoseReplyAfter / BreakAfter run the request, then drop the reply or
    // throw (the reply is lost after the write happened); OnWrite runs before a real write executes.
    sealed class FakeExecutor
    {
        public readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string[]> DependentsOf = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Lines = new();
        public readonly List<RevitCommandEnvelope> Envelopes = new();
        public readonly List<string> Executed = new();
        public readonly List<string> Violations = new();
        public Func<RevitCommandEnvelope, bool>? LoseReplyAfter;
        public Func<RevitCommandEnvelope, bool>? BreakAfter;
        public Action<RevitCommandEnvelope>? OnWrite;
        readonly Dictionary<string, (string Sha, string[] Created, string[] Deleted)> receipts = new(StringComparer.Ordinal);

        public string? Send(string pipe, string line)
        {
            Lines.Add(line);
            if (pipe != Pipe) Violations.Add("pipe " + pipe);
            RevitCommandEnvelope envelope;
            try { using var wire = JsonDocument.Parse(line); envelope = RevitCommandProtocol.Parse(wire.RootElement.GetProperty("Command").GetRawText()); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or RevitCommandException) { Violations.Add("not a wire line"); return null; }
            Envelopes.Add(envelope);
            var reply = Answer(envelope);
            if (LoseReplyAfter?.Invoke(envelope) == true) return null;
            if (BreakAfter?.Invoke(envelope) == true) throw new IOException("pipe broke after the request");
            return reply;
        }

        string Answer(RevitCommandEnvelope envelope)
        {
            if (envelope.Args is ElementDeleteArgs { DryRun: true } dry)
            {
                if (Missing(dry.ElementUids) is { } missing) return missing;
                return RevitCommandResponse.Success(Receipt(envelope, false), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                    DeleteReadback(dry.ElementUids, DependentsNow(dry.ElementUids), true), Array.Empty<string>()).ToJson();
            }
            if (receipts.TryGetValue(envelope.OperationId, out var stored))
            {
                if (stored.Sha != envelope.RequestSha256) return RevitCommandResponse.Failure(RevitCommandCodes.OperationIdConflict, "operation_id", "another request with this operation id ran").ToJson();
                return RevitCommandResponse.Success(Receipt(envelope, true), stored.Created, Array.Empty<string>(), stored.Deleted, null, Array.Empty<string>()).ToJson();
            }
            OnWrite?.Invoke(envelope);
            string[] created;
            string[] deleted;
            if (envelope.Args is ElementDeleteArgs delete)
            {
                if (Missing(delete.ElementUids) is { } missing) return missing;
                var dependents = DependentsNow(delete.ElementUids);
                var confirmed = delete.ConfirmDependents;
                if (confirmed.Count != dependents.Length || !confirmed.All(uid => dependents.Contains(uid, StringComparer.OrdinalIgnoreCase)))
                    return RevitCommandResponse.Failure(RevitCommandCodes.ArgsInvalid, "args.confirm_dependents", "confirm_dependents does not name the dependents Revit would delete now").ToJson();
                deleted = delete.ElementUids.Concat(dependents).ToArray();
                foreach (var uid in deleted) Existing.Remove(uid);
                created = Array.Empty<string>();
            }
            else
            {
                var uid = Fake("created:" + envelope.OperationId);
                Existing.Add(uid);
                created = new[] { uid };
                deleted = Array.Empty<string>();
            }
            Executed.Add(envelope.OperationId);
            receipts[envelope.OperationId] = (envelope.RequestSha256, created, deleted);
            return RevitCommandResponse.Success(Receipt(envelope, false), created, Array.Empty<string>(), deleted,
                envelope.Args is ElementDeleteArgs real ? DeleteReadback(real.ElementUids, deleted.Skip(real.ElementUids.Count).ToArray(), false) : null, Array.Empty<string>()).ToJson();
        }

        string? Missing(IReadOnlyList<string> targets)
        {
            for (var i = 0; i < targets.Count; i++)
                if (!Existing.Contains(targets[i]))
                    return RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.element_uids[" + N(i) + "]", "no element with UniqueId " + targets[i]).ToJson();
            return null;
        }

        string[] DependentsNow(IReadOnlyList<string> targets) => targets.SelectMany(uid => DependentsOf.TryGetValue(uid, out var list) ? list : Array.Empty<string>())
            .Where(Existing.Contains).Where(uid => !targets.Contains(uid, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(uid => uid, StringComparer.Ordinal).ToArray();

        static RevitCommandReceipt Receipt(RevitCommandEnvelope envelope, bool replayed) =>
            new(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, replayed);

        // The writes (everything but dry runs), in the order received.
        public RevitCommandEnvelope[] Writes => Envelopes.Where(envelope => envelope.Args is not ElementDeleteArgs { DryRun: true }).ToArray();
    }

    // The same executor as an IRevitCommandTransport for the in-process library oracle (envelope in, reply out).
    sealed class LibraryTransport(FakeExecutor fake) : IRevitCommandTransport
    {
        public string? Send(string envelopeJson) => fake.Send(Pipe, RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(envelopeJson)));
    }

    static string CanonicalArgs(RevitCommandEnvelope envelope)
    {
        using var canonical = JsonDocument.Parse(envelope.CanonicalJson);
        return canonical.RootElement.GetProperty("args").GetRawText();
    }

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "doc-update-execute");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "doc-update execute: " + name, detail);
        var counter = 0;
        string Input(string name, string content) { var path = Path.Combine(scratch, N(++counter) + "-" + name); File.WriteAllText(path, content); return path; }
        string NewOutput() => Path.Combine(scratch, "plan-" + N(++counter) + ".json");
        static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

        (int Exit, string Out) Execute(string[] arguments, DocUpdateRevit revit)
        {
            var stdout = new StringWriter { NewLine = "\n" };
            var exit = DocUpdateCommand.Execute(arguments, stdout, revit);
            var text = stdout.ToString();
            if (text.EndsWith('\n')) text = text[..^1];
            return (exit, text);
        }
        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        static string D((int Exit, string Out) r) => "exit " + N(r.Exit) + " " + (r.Out.Length > 900 ? r.Out[..900] + "..." : r.Out);
        static JsonObject? Parse(string json)
        {
            try { return JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { return null; }
        }
        static string? Member(string json, string name) => Parse(json)?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        // The per-step report: (operation_id, status) pairs in order, and the entry of one operation.
        static (string Op, string Status)[] Steps(string json) =>
            Parse(json)?["report"] is JsonArray report
                ? report.Select(entry => (entry?["operation_id"]?.GetValue<string>() ?? "", entry?["status"]?.GetValue<string>() ?? "")).ToArray()
                : Array.Empty<(string, string)>();
        static JsonObject? Entry(string json, string operation) =>
            Parse(json)?["report"] is JsonArray report ? report.OfType<JsonObject>().FirstOrDefault(entry => entry["operation_id"]?.GetValue<string>() == operation) : null;

        // Fixture (as P4-10): a level inserted below renumbers every plan; deleting room 0's stale plan takes its tags, its
        // viewport (each deleted earlier at its own step) and one untracked annotation a human accepted.
        var before = Build(0, 3);
        var realized = RealizedOf(before);
        var inserted = Build(0, 3, -3);
        var bindings = Bindings(inserted);
        var insertedRealized = new Dictionary<string, string>(realized, StringComparer.Ordinal);
        foreach (var element in inserted.Elements) insertedRealized.TryAdd(DocRegeneration.ElementSource(element.Id), Fake("element:" + element.Id.ToString("D")));
        var plan0Stale = realized[DocRegeneration.ItemSource("plan-" + Role(0, "level").ToString("N"))];
        var annotation = Fake("annotation:plan0");
        var reported = new[] { realized[DocRegeneration.ItemSource("tag-room-" + Role(0, "space").ToString("N"))], realized[DocRegeneration.ItemSource("tag-door-" + Door(0).ToString("N"))],
            realized[DocRegeneration.ItemSource("place-plan-" + Role(0, "level").ToString("N"))], annotation };
        FakeExecutor Revit()
        {
            var fake = new FakeExecutor();
            foreach (var uid in insertedRealized.Values.Append(annotation)) fake.Existing.Add(uid);
            fake.DependentsOf[plan0Stale] = reported;
            return fake;
        }
        DocUpdateRevit Host(FakeExecutor fake, Action? onPolicy = null) => new(() => { onPolicy?.Invoke(); return enabled; }, fake.Send);

        var beforePath = Input("before.json", JsonSerializer.Serialize(before));
        var afterPath = Input("after.json", JsonSerializer.Serialize(inserted));
        var realizedPath = Input("realized.json", MapJson(insertedRealized));
        var bindingsPath = Input("bindings.json", BindingsJson(bindings));
        string[] Args(string output, params string[] extra) =>
            new[] { "doc-update", "--before", beforePath, "--after", afterPath, "--realized", realizedPath, "--bindings", bindingsPath, "--output", output }.Concat(extra).ToArray();

        // ---- Library oracle on an independent fake: DocUpdateDependents.Update ----
        var oracleFake = Revit();
        var oracle = DocUpdateDependents.Update(before, inserted, bindings, insertedRealized, new LibraryTransport(oracleFake), new[] { annotation });
        var plan = oracle.Planned.Plan;
        var oracleRealized = oracle.Run?.Realized;
        Check(oracle.Planned.Failed is null && plan is not null && oracleRealized is not null && oracle.Run?.Report.Status == RevitPlanRunStatus.Completed,
            "fixture: the library probes and runs the level insertion", oracle.Planned.Failed?.Code ?? oracle.Run?.Report.Failed?.Code ?? "");
        if (plan is null || oracleRealized is null) return;
        var steps = plan.Replacement.Commands.Steps;
        var deletes = steps.Where(step => step.Command == DocReplace.DeleteCommand).ToArray();
        var creates = steps.Where(step => step.Command != DocReplace.DeleteCommand).ToArray();
        Check(deletes.Length > 1 && creates.Length > 1 && deletes.Any(step => step.ArgsJson.Contains("confirm_dependents", StringComparison.Ordinal))
            && oracleFake.Violations.Count == 0 && oracleFake.Executed.Count == steps.Count,
            "fixture: several deletes (one with confirm_dependents) and creates, each executed once by the library", N(deletes.Length) + " deletes " + N(creates.Length) + " creates " + string.Join(";", oracleFake.Violations));
        var expected = Utf8(JsonSerializer.Serialize(plan));
        var sha = Convert.ToHexString(SHA256.HashData(expected));

        // ---- Dry run (no --execute): unchanged P4-10 bytes and summary; its sha256 is what --expected-document takes ----
        var dryFake = Revit();
        var dryOutput = NewOutput();
        var dry = Execute(Args(dryOutput, "--probe-dependents", Pipe, "--accept", annotation), Host(dryFake));
        var drySummary = JsonSerializer.Serialize(new
        {
            status = "succeeded", output = dryOutput, bytes = (long)expected.Length, sha256 = sha,
            regenerated = plan.Regeneration.Regenerated.Count, steps = plan.Replacement.Commands.Steps.Count, forced = plan.Forced.Count, retired = plan.Retired.Count,
            probed = oracle.Planned.Probes.Count, dependents = oracle.Planned.Dependents.Values.Sum(list => list.Count), applied = false,
        });
        Check(dry.Exit == 0 && File.Exists(dryOutput) && File.ReadAllBytes(dryOutput).AsSpan().SequenceEqual(expected) && dry.Out == drySummary && dryFake.Writes.Length == 0,
            "without --execute: the P4-10 dry plan bytes and summary, no write sent", D(dry));

        // ---- 1. Success: the dry runs, then every write in DocReplace order, exactly as the library sends them ----
        {
            var fake = Revit();
            var output = NewOutput();
            var policyReads = 0;
            var run = Execute(Args(output, "--probe-dependents", Pipe, "--accept", annotation, "--execute", "--expected-document", sha.ToLowerInvariant()), Host(fake, () => policyReads++));
            var written = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
            Check(run.Exit == 0 && Member(run.Out, "status") == "succeeded" && written.AsSpan().SequenceEqual(expected),
                "--execute writes the dry plan bytes and succeeds", D(run));
            Check(fake.Lines.SequenceEqual(oracleFake.Lines, StringComparer.Ordinal),
                "the wire lines are exactly those of DocUpdateDependents.Update (dry runs, then the writes)", N(fake.Lines.Count) + " vs " + N(oracleFake.Lines.Count));
            var writes = fake.Writes;
            Check(writes.Select(envelope => envelope.OperationId).SequenceEqual(steps.Select(step => step.OperationId), StringComparer.Ordinal)
                && fake.Executed.SequenceEqual(steps.Select(step => step.OperationId), StringComparer.Ordinal),
                "every write is sent once, in plan order: deletes first, then creates", string.Join(",", writes.Select(envelope => envelope.Command)));
            Check(deletes.All(step => writes.Any(envelope => envelope.OperationId == step.OperationId && envelope.Args is ElementDeleteArgs { DryRun: false } && CanonicalArgs(envelope) == step.ArgsJson)),
                "each real delete carries exactly the planned args (target and confirm_dependents)");
            Check(fake.Violations.Count == 0 && !fake.Existing.Contains(annotation) && !fake.Existing.Contains(plan0Stale), "the accepted annotation is deleted with its plan, confirmed", string.Join(";", fake.Violations));
            Check(policyReads == 1, "the enabled-roots policy is read once for the probe and the run", N(policyReads));
            var parsed = Parse(run.Out);
            var dryParsed = Parse(drySummary)!;
            Check(parsed is not null && dryParsed.Where(pair => pair.Key is not "output" and not "applied").All(pair => JsonNode.DeepEquals(parsed[pair.Key], pair.Value))
                && parsed["output"]?.GetValue<string>() == output && parsed["applied"]?.GetValue<bool>() == true && parsed["run"]?.GetValue<string>() == RevitPlanRunStatus.Completed,
                "the summary keeps the dry members and adds applied true and run completed", D(run));
            var report = Steps(run.Out);
            Check(report.Select(entry => entry.Op).SequenceEqual(steps.Select(step => step.OperationId), StringComparer.Ordinal) && report.All(entry => entry.Status == "done"),
                "the per-step report lists every step done, in plan order", D(run));
            var firstCreate = Entry(run.Out, creates[0].OperationId);
            Check(firstCreate?["created"] is JsonArray created && created.Count == 1 && created[0]?.GetValue<string>() == Fake("created:" + creates[0].OperationId)
                && firstCreate["replayed"]?.GetValue<bool>() == false && firstCreate["key"]?.GetValue<string>() == plan.Replacement.Replacements.Single(item => item.CreateOperation == creates[0].OperationId).Key,
                "a done step reports its key, created UniqueIds and replayed", firstCreate?.ToJsonString() ?? "");
            var map = parsed?["realized"] as JsonObject;
            Check(map is not null && map.Count == oracleRealized.Count && oracleRealized.All(pair => map[pair.Key]?.GetValue<string>() == pair.Value),
                "prints the updated realized map of the library run", D(run));
        }

        // ---- 2. The first failure stops the run; a lost reply is never resent ----
        void Stopped(string name, FakeExecutor fake, string operation, string code, bool replyLost, bool executed)
        {
            var output = NewOutput();
            var run = Execute(Args(output, "--probe-dependents", Pipe, "--accept", annotation, "--execute", "--expected-document", sha), Host(fake));
            var index = steps.Select(step => step.OperationId).ToList().IndexOf(operation);
            var report = Steps(run.Out);
            Check(run.Exit == 1 && Member(run.Out, "status") == "failed" && Member(run.Out, "code") == code && Member(run.Out, "operation_id") == operation
                && Parse(run.Out)?["applied"]?.GetValue<bool>() == false && Member(run.Out, "run") == RevitPlanRunStatus.Failed,
                name + ": exit 1, typed failure " + code + " at the failed step", D(run));
            Check(index >= 0 && report.Length == steps.Count && report.Take(index).All(entry => entry.Status == "done") && report[index].Status == "failed"
                && report.Skip(index + 1).All(entry => entry.Status == "not_sent"),
                name + ": done before, failed at, not_sent after the failure", D(run));
            Check(Entry(run.Out, operation)?["reply_lost"]?.GetValue<bool>() == replyLost && Entry(run.Out, operation)?["code"]?.GetValue<string>() == code,
                name + ": the failed step says reply_lost " + (replyLost ? "true" : "false"), Entry(run.Out, operation)?.ToJsonString() ?? "");
            var writes = fake.Writes;
            Check(writes.Count(envelope => envelope.OperationId == operation) == 1 && writes.Length > 0 && writes[^1].OperationId == operation
                && writes.Select(envelope => envelope.OperationId).SequenceEqual(steps.Take(index + 1).Select(step => step.OperationId), StringComparer.Ordinal),
                name + ": the failed write is sent exactly once and nothing is sent after it", N(writes.Length) + " writes");
            Check(fake.Executed.Contains(operation, StringComparer.Ordinal) == executed, name + ": the executor " + (executed ? "ran" : "did not run") + " the failed write");
            Check(File.Exists(output) && File.ReadAllBytes(output).AsSpan().SequenceEqual(expected), name + ": the plan file was written before the first write");
        }
        {
            var fake = Revit();
            fake.LoseReplyAfter = envelope => envelope.OperationId == creates[0].OperationId;
            Stopped("a create whose reply was lost", fake, creates[0].OperationId, RevitCommandCodes.NoResponse, true, true);
        }
        {
            var fake = Revit();
            fake.LoseReplyAfter = envelope => envelope.OperationId == deletes[0].OperationId;
            Stopped("the first delete whose reply was lost", fake, deletes[0].OperationId, RevitCommandCodes.NoResponse, true, true);
        }
        {
            var fake = Revit();
            fake.BreakAfter = envelope => envelope.OperationId == creates[1].OperationId;
            Stopped("a create whose pipe broke after the request", fake, creates[1].OperationId, RevitCommandCodes.PipeIoError, true, true);
        }
        {
            // A new dependent appears after the confirm pass: the executor refuses the plan's delete (typed reply).
            var fake = Revit();
            var late = Fake("annotation:late");
            var plan0Delete = deletes.Single(step => step.ArgsJson.Contains(plan0Stale, StringComparison.OrdinalIgnoreCase)).OperationId;
            fake.OnWrite = envelope =>
            {
                if (envelope.OperationId != deletes[0].OperationId) return;
                fake.Existing.Add(late);
                fake.DependentsOf[plan0Stale] = reported.Append(late).ToArray();
            };
            Stopped("a delete whose dependents changed after the confirm pass", fake, plan0Delete, RevitCommandCodes.ArgsInvalid, false, false);
        }

        // ---- 3. --expected-document guards the reviewed plan ----
        {
            var fake = Revit();
            var output = NewOutput();
            var other = new string('0', 64);
            var run = Execute(Args(output, "--probe-dependents", Pipe, "--accept", annotation, "--execute", "--expected-document", other), Host(fake));
            Check(run.Exit == 1 && Member(run.Out, "status") == "failed" && Member(run.Out, "code") == PlanChanged && Member(run.Out, "field") == "expected_document"
                && Member(run.Out, "detail") == sha && !File.Exists(output) && fake.Writes.Length == 0 && fake.Violations.Count == 0,
                "a plan whose SHA-256 is not --expected-document is refused before any write, with no file", D(run));
        }
        {
            // A probe refusal in execute mode is the P4-10 refusal: nothing written, no write sent.
            var fake = Revit();
            var output = NewOutput();
            var run = Execute(Args(output, "--probe-dependents", Pipe, "--execute", "--expected-document", sha), Host(fake));
            Check(run.Exit == 1 && Member(run.Out, "code") == DocUpdateDependentsCodes.DependentUnknown && !File.Exists(output) && fake.Writes.Length == 0,
                "a probe refusal stops --execute before any write", D(run));
        }
        foreach (var bad in new[]
        {
            Args(NewOutput(), "--probe-dependents", Pipe, "--accept", annotation, "--execute"),
            Args(NewOutput(), "--probe-dependents", Pipe, "--accept", annotation, "--expected-document", sha),
            Args(NewOutput(), "--execute", "--expected-document", sha),
            Args(NewOutput(), "--dependents", realizedPath, "--execute", "--expected-document", sha),
            Args(NewOutput(), "--probe-dependents", Pipe, "--execute", "--execute", "--expected-document", sha),
            Args(NewOutput(), "--probe-dependents", Pipe, "--execute", "--expected-document", sha, "--expected-document", sha),
            Args(NewOutput(), "--probe-dependents", Pipe, "--execute", "--expected-document", "not-a-sha"),
            Args(NewOutput(), "--probe-dependents", Pipe, "--execute", "--expected-document", sha[..63]),
            Args(NewOutput(), "--probe-dependents", Pipe, "--execute", "--expected-document"),
            Args(NewOutput(), "--probe-dependents", Pipe, "--expected-document", "--execute"),
        })
        {
            var fake = Revit(); var policyReads = 0;
            var target = bad[10];
            var r = Execute(bad, Host(fake, () => policyReads++));
            Check(r.Exit == 1 && r.Out == JsonSerializer.Serialize(new { status = "failed", code = DocUpdateCommand.UsageCode }) && !File.Exists(target) && fake.Lines.Count == 0 && policyReads == 0,
                "usage refused: " + string.Join(" ", bad.Skip(11)), D(r));
        }

        // ---- 4. A stale target already gone: reference_not_found on the run's delete, replanned as DocUpdate does ----
        {
            var removed = before with { Elements = before.Elements.Where(element => element.Id != Door(1)).ToArray() };
            var doorUid = realized[DocRegeneration.ElementSource(Door(1))];
            var tagKey = "tag-door-" + Door(1).ToString("N");
            var tagUid = realized[DocRegeneration.ItemSource(tagKey)];
            FakeExecutor Gone()
            {
                var gone = new FakeExecutor();
                foreach (var uid in realized.Values.Where(value => value != doorUid && value != tagUid)) gone.Existing.Add(uid);
                return gone;
            }
            var goneOracleFake = Gone();
            var goneOracle = DocUpdateDependents.Update(before, removed, Bindings(removed), realized, new LibraryTransport(goneOracleFake));
            Check(goneOracle.Run is { Realized: not null } && goneOracle.Run.Plan.Gone.Contains(tagKey, StringComparer.Ordinal),
                "fixture: the library run reports the stale tag already deleted in Revit as gone", goneOracle.Planned.Failed?.Code ?? goneOracle.Run?.Report.Failed?.Code ?? "");
            if (goneOracle.Planned.Plan is { } gonePlan)
            {
                var goneSha = Convert.ToHexString(SHA256.HashData(Utf8(JsonSerializer.Serialize(gonePlan))));
                var f = Gone(); var o = NewOutput();
                var arguments = new[] { "doc-update", "--before", beforePath, "--after", Input("removed.json", JsonSerializer.Serialize(removed)), "--realized", Input("realized-removed.json", MapJson(realized)),
                    "--bindings", Input("removed-bindings.json", BindingsJson(Bindings(removed))), "--output", o, "--probe-dependents", Pipe, "--execute", "--expected-document", goneSha };
                var r = Execute(arguments, Host(f));
                Check(r.Exit == 0 && Member(r.Out, "status") == "succeeded" && Parse(r.Out)?["gone"] is JsonArray gone && gone.Count == 1 && gone[0]?.GetValue<string>() == tagKey
                    && f.Lines.SequenceEqual(goneOracleFake.Lines, StringComparer.Ordinal) && f.Executed.Distinct(StringComparer.Ordinal).Count() == f.Executed.Count,
                    "a gone stale target is reported in gone and the run completes like the library, each write executed once", D(r));
            }
        }

        // ---- 5. The real Release CLI ----
        {
            // No edit: nothing to probe or send; the policy is never read and the empty plan is written and applied.
            var sameAfter = Input("same-after.json", JsonSerializer.Serialize(before));
            var sameBindings = Input("same-bindings.json", BindingsJson(Bindings(before)));
            var sameRealized = Input("same-realized.json", MapJson(realized));
            var empty = Utf8(JsonSerializer.Serialize(DocUpdate.Plan(before, before, Bindings(before), realized)));
            var emptySha = Convert.ToHexString(SHA256.HashData(empty));
            var e = NewOutput();
            var cli = Cli("doc-update", "--before", beforePath, "--after", sameAfter, "--realized", sameRealized, "--bindings", sameBindings, "--output", e,
                "--probe-dependents", "bim-agentic-os-2025-1", "--execute", "--expected-document", emptySha);
            Check(cli.Exit == 0 && File.Exists(e) && File.ReadAllBytes(e).AsSpan().SequenceEqual(empty) && Member(cli.Out, "status") == "succeeded" && Member(cli.Out, "run") == RevitPlanRunStatus.Completed
                && Parse(cli.Out)?["report"] is JsonArray { Count: 0 },
                "the real CLI executes an empty plan without sending anything", "exit " + N(cli.Exit) + " " + cli.Out + " " + cli.Err);
            var u = NewOutput();
            var usage = Cli(Args(u, "--probe-dependents", "bim-agentic-os-2025-1", "--execute"));
            Check(usage.Exit == 1 && usage.Out == JsonSerializer.Serialize(new { status = "failed", code = DocUpdateCommand.UsageCode }) && !File.Exists(u),
                "the real CLI refuses --execute without --expected-document", "exit " + N(usage.Exit) + " " + usage.Out + " " + usage.Err);
        }

        // ---- Static: the run lives in DocUpdateExecute.cs, runs the plan with DocUpdate.Run, never resumes or retries ----
        var executeSourcePath = Path.Combine(root, "interfaces", "cli", "DocUpdateExecute.cs");
        Check(File.Exists(executeSourcePath), "interfaces/cli/DocUpdateExecute.cs exists");
        if (File.Exists(executeSourcePath))
        {
            var source = File.ReadAllText(executeSourcePath);
            Check(source.Contains("DocUpdate.Run(", StringComparison.Ordinal)
                && new[] { "DocUpdate.Resume", "DocReplace.Resume", "PlanRunner.Resume", "DocUpdateDependents.Update", "File.", "Directory." }.All(api => !source.Contains(api, StringComparison.Ordinal)),
                "the executor runs the probed plan with DocUpdate.Run and never resumes, re-probes or touches files");
        }
    }
}
