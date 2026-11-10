using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Cli;
using Bim.Core;

// P4-10 `doc-update ... --probe-dependents process-pipe-name [--accept uid]...`: DocUpdateDependents.Plan (P4-09) over the
// Revit process pipe. Oracles:
//   1. In process (DocUpdateCommand.Execute, the code the CLI runs, with a fake executor behind the pipe function and an
//      enabled-roots policy): the output file holds exactly UTF-8(JsonSerializer.Serialize(DocUpdateDependents.Plan(...)
//      .Plan)) computed in process on an independent fake, which equals DocUpdate.Plan with the dependents the dry runs
//      reported; every wire line the executor receives is element.delete with dry_run true (nothing else is ever sent);
//      the same inputs (flags reordered, --accept repeated) give the same bytes and the same wire lines.
//   2. Every DocUpdateDependents failure (and every refusal before it) prints one typed {"status":"failed","code",...}
//      envelope, returns exit 1 and creates no file; the usage, pipe name and accepted UniqueIds are refused before the
//      policy is read or anything is sent.
//   3. Without --probe-dependents the verb never reads the policy or touches the pipe, and its bytes and summary are the
//      P4-08 ones (in process and through the real Release CLI).
//   4. The real Release CLI: a non-process pipe and a document outside this repository's enabled roots are refused
//      before any pipe I/O; a plan with no delete needs no dry run and writes the plain doc-update bytes.
// Unit-tested only; no Revit.
internal static class DocUpdateProbeTests
{
    const string Pipe = "bim-agentic-os-2025-4242";
    static readonly Provenance provenance = new("cli-doc-update-probe-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-10\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd11");
    static readonly RevitCommandPolicy enabled = new(new[] { @"C:\Repo\.bim\native-runs" });

    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("cli-doc-update-probe:" + name)).Take(16).ToArray());
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

    static RevitBindings Bindings(Project project, RevitDocumentRef? at = null) => new(at ?? document,
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

    // The bindings file format of P4-08, written by hand.
    static string BindingsJson(RevitBindings bindings) =>
        "{\"document\":{\"path\":" + JsonSerializer.Serialize(bindings.Document.Path) + ",\"unique_id\":" + JsonSerializer.Serialize(bindings.Document.UniqueId) + "},\"levels\":{"
        + string.Join(",", bindings.Levels.Select(pair => JsonSerializer.Serialize(pair.Key.ToString("D")) + ":" + JsonSerializer.Serialize(pair.Value))) + "}}";

    static string MapJson(IEnumerable<KeyValuePair<string, string>> map) =>
        "{" + string.Join(",", map.Select(pair => JsonSerializer.Serialize(pair.Key) + ":" + JsonSerializer.Serialize(pair.Value))) + "}";

    // The add-in's delete readback (CommandReadbackModify.DeleteJson) for a dry run.
    static string Readback(IReadOnlyList<string> targets, IReadOnlyList<string> dependents)
    {
        var text = string.Join("\n", dependents.Select(uid => uid.ToLowerInvariant()).OrderBy(uid => uid, StringComparer.Ordinal));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("delete");
            writer.WriteBoolean("dry_run", true);
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
            writer.WriteBoolean("confirmed", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The add-in behind the process pipe, answering only element.delete dry runs (rolled back: nothing deleted, no receipt
    // stored); a missing target is reference_not_found as the add-in reports it. Anything else is recorded as a violation
    // and refused. Lines holds every wire line received, in order.
    sealed class FakeExecutor
    {
        public readonly HashSet<string> Existing = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string[]> DependentsOf = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Lines = new();
        public readonly List<string> Violations = new();
        public readonly List<string> Pipes = new();
        // Before a dry run is answered: its target and how many dry runs of that target this is (1-based).
        public Action<string, int>? OnDryRun;
        public Func<string, string?>? Override;
        readonly Dictionary<string, int> seen = new(StringComparer.OrdinalIgnoreCase);

        public string? Send(string pipe, string line)
        {
            Pipes.Add(pipe);
            Lines.Add(line);
            if (Override is not null) return Override(line);
            string commandText;
            try { using var wire = JsonDocument.Parse(line); commandText = wire.RootElement.GetProperty("Command").GetRawText(); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException) { Violations.Add("not a wire line"); return null; }
            var envelope = RevitCommandProtocol.Parse(commandText);
            if (envelope.Args is not ElementDeleteArgs { DryRun: true } delete)
            {
                Violations.Add(envelope.Command + " " + envelope.OperationId);
                return RevitCommandResponse.Failure(RevitCommandCodes.ArgsInvalid, "args", "fake executor: only dry runs").ToJson();
            }
            var target = delete.ElementUids[0];
            seen[target] = seen.GetValueOrDefault(target) + 1;
            OnDryRun?.Invoke(target, seen[target]);
            for (var i = 0; i < delete.ElementUids.Count; i++)
                if (!Existing.Contains(delete.ElementUids[i]))
                    return RevitCommandResponse.Failure(RevitCommandCodes.ReferenceNotFound, "args.element_uids[" + N(i) + "]", "no element with UniqueId " + delete.ElementUids[i]).ToJson();
            var dependents = delete.ElementUids.SelectMany(uid => DependentsOf.TryGetValue(uid, out var list) ? list : Array.Empty<string>())
                .Where(Existing.Contains).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(uid => uid, StringComparer.Ordinal).ToArray();
            var receipt = new RevitCommandReceipt(envelope.OperationId, envelope.Command, envelope.Document.UniqueId, envelope.RequestSha256, false);
            return RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Readback(delete.ElementUids, dependents), Array.Empty<string>()).ToJson();
        }
    }

    // The same executor as an IRevitCommandTransport for the in-process library oracle (envelope in, reply out).
    sealed class LibraryTransport(FakeExecutor fake) : IRevitCommandTransport
    {
        public string? Send(string envelopeJson) => fake.Send(Pipe, RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(envelopeJson)));
    }

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        var scratch = Path.Combine(work, "doc-update-probe");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail = "") => check(condition, "doc-update probe: " + name, detail);
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
        static string D((int Exit, string Out) r) => "exit " + N(r.Exit) + " " + (r.Out.Length > 600 ? r.Out[..600] + "..." : r.Out);
        static string? Member(string json, string name)
        {
            try { return JsonNode.Parse(json) is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
            catch (JsonException) { return null; }
        }

        // Fixture: a level inserted below (every plan renumbered); deleting room 0's stale plan takes its tags, its
        // viewport and one annotation the documentation set does not track.
        var before = Build(0, 3);
        var realized = RealizedOf(before);
        var inserted = Build(0, 3, -3);
        var bindings = Bindings(inserted);
        var insertedRealized = new Dictionary<string, string>(realized, StringComparer.Ordinal);
        foreach (var element in inserted.Elements) insertedRealized.TryAdd(DocRegeneration.ElementSource(element.Id), Fake("element:" + element.Id.ToString("D")));
        var plan0 = "plan-" + Role(0, "level").ToString("N");
        var plan0Stale = realized[DocRegeneration.ItemSource(plan0)];
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
        DocUpdateRevit Host(FakeExecutor fake, RevitCommandPolicy? policy = null, Action? onPolicy = null) =>
            new(() => { onPolicy?.Invoke(); return policy ?? enabled; }, fake.Send);

        var beforePath = Input("before.json", JsonSerializer.Serialize(before));
        var afterPath = Input("after.json", JsonSerializer.Serialize(inserted));
        var realizedPath = Input("realized.json", MapJson(insertedRealized));
        var bindingsPath = Input("bindings.json", BindingsJson(bindings));
        string[] Args(string output, params string[] extra) =>
            new[] { "doc-update", "--before", beforePath, "--after", afterPath, "--realized", realizedPath, "--bindings", bindingsPath, "--output", output }.Concat(extra).ToArray();

        // ---- Library oracle on an independent fake ----
        var oracleFake = Revit();
        var oracle = DocUpdateDependents.Plan(before, inserted, bindings, insertedRealized, new LibraryTransport(oracleFake), new[] { annotation });
        Check(oracle.Failed is null && oracle.Plan is not null && oracle.Dependents.ContainsKey(plan0), "fixture: the library plans the level insertion with the accepted annotation",
            oracle.Failed is null ? "" : oracle.Failed.Code + " " + oracle.Failed.Detail);
        if (oracle.Plan is null) return;
        var expected = Utf8(JsonSerializer.Serialize(oracle.Plan));
        Check(JsonSerializer.Serialize(oracle.Plan) == JsonSerializer.Serialize(DocUpdate.Plan(before, inserted, bindings, insertedRealized, oracle.Dependents))
            && JsonSerializer.Serialize(oracle.Plan) != JsonSerializer.Serialize(oracle.First),
            "fixture: the probed plan is DocUpdate.Plan with the reported dependents and differs from the plan without them");
        Check(oracleFake.Violations.Count == 0 && oracleFake.Lines.Count > 0, "fixture: the library sends only dry runs", string.Join(";", oracleFake.Violations));

        // ---- 1. Success: the canonical plan, only dry-run deletes on the pipe ----
        var fake = Revit();
        var output = NewOutput();
        var run = Execute(Args(output, "--probe-dependents", Pipe, "--accept", annotation), Host(fake));
        var written = File.Exists(output) ? File.ReadAllBytes(output) : Array.Empty<byte>();
        Check(run.Exit == 0 && written.AsSpan().SequenceEqual(expected), "writes exactly JsonSerializer.Serialize(DocUpdateDependents.Plan(...).Plan)", D(run));
        var summary = JsonSerializer.Serialize(new
        {
            status = "succeeded", output, bytes = (long)expected.Length, sha256 = Convert.ToHexString(SHA256.HashData(expected)),
            regenerated = oracle.Plan.Regeneration.Regenerated.Count, steps = oracle.Plan.Replacement.Commands.Steps.Count, forced = oracle.Plan.Forced.Count, retired = oracle.Plan.Retired.Count,
            probed = oracle.Probes.Count, dependents = oracle.Dependents.Values.Sum(list => list.Count), applied = false,
        });
        Check(run.Out == summary, "prints the success summary with probed, dependents and applied false", D(run) + " expected " + summary);
        var dryRuns = fake.Lines.Select(line =>
        {
            using var wire = JsonDocument.Parse(line);
            return RevitCommandProtocol.Parse(wire.RootElement.GetProperty("Command").GetRawText());
        }).ToArray();
        Check(fake.Violations.Count == 0 && dryRuns.Length > 0 && dryRuns.All(envelope => envelope.Command == "element.delete" && envelope.Args is ElementDeleteArgs { DryRun: true }),
            "the fake executor receives only element.delete dry runs", string.Join(";", fake.Violations) + " lines " + N(fake.Lines.Count));
        var firstDeletes = oracle.First.Replacement.Commands.Steps.Count(step => step.Command == DocReplace.DeleteCommand);
        var secondDeletes = oracle.Plan.Replacement.Commands.Steps.Count(step => step.Command == DocReplace.DeleteCommand);
        Check(fake.Lines.Count == firstDeletes + secondDeletes && fake.Lines.SequenceEqual(oracleFake.Lines, StringComparer.Ordinal),
            "one dry run per planned delete in each pass, the same wire lines as the library call", N(fake.Lines.Count) + " vs " + N(firstDeletes) + "+" + N(secondDeletes));
        Check(fake.Pipes.All(pipe => pipe == Pipe) && dryRuns.All(envelope => envelope.Document.Path == document.Path && envelope.Document.UniqueId == document.UniqueId),
            "every dry run goes to the named process pipe for the bound document");

        // Deterministic: flags reordered, --accept repeated in another case: the same bytes and the same wire lines.
        var again = Revit();
        var second = NewOutput();
        var reordered = new[] { "doc-update", "--accept", annotation.ToUpperInvariant(), "--output", second, "--probe-dependents", Pipe, "--bindings", bindingsPath, "--accept", annotation,
            "--realized", realizedPath, "--after", afterPath, "--before", beforePath };
        var rerun = Execute(reordered, Host(again));
        Check(rerun.Exit == 0 && File.Exists(second) && File.ReadAllBytes(second).AsSpan().SequenceEqual(expected) && again.Lines.SequenceEqual(fake.Lines, StringComparer.Ordinal),
            "the same inputs in any flag order give the same bytes and the same dry runs", D(rerun));

        // ---- 2. Refusals: typed envelope, exit 1, no file ----
        void Refused(string name, string code, string[] arguments, FakeExecutor executor, DocUpdateRevit revit, Func<string, bool>? more = null, int? lines = null)
        {
            var target = arguments[Array.IndexOf(arguments, "--output") + 1];
            var r = Execute(arguments, revit);
            Check(r.Exit == 1 && Member(r.Out, "status") == "failed" && Member(r.Out, "code") == code && !File.Exists(target) && (more?.Invoke(r.Out) ?? true),
                "refuses " + name + " as " + code + " and writes no file", D(r));
            Check(executor.Violations.Count == 0 && (lines is null || executor.Lines.Count == lines), "sends only dry runs when refusing " + name,
                string.Join(";", executor.Violations) + " lines " + N(executor.Lines.Count));
        }
        {
            var f = Revit(); var o = NewOutput();
            Refused("an untracked dependent not accepted", DocUpdateDependentsCodes.DependentUnknown, Args(o, "--probe-dependents", Pipe), f, Host(f),
                text => Member(text, "key") == plan0 && Member(text, "field") == "dependents" && Member(text, "detail") == annotation && Member(text, "operation_id") is { Length: > 0 });
        }
        {
            var f = Revit(); var o = NewOutput();
            f.OnDryRun = (target, count) => { if (count == 2 && string.Equals(target, plan0Stale, StringComparison.OrdinalIgnoreCase)) f.DependentsOf[plan0Stale] = reported.Take(3).ToArray(); };
            Refused("dependents that changed between the passes", DocUpdateDependentsCodes.DependentsChanged, Args(o, "--probe-dependents", Pipe, "--accept", annotation), f, Host(f),
                text => Member(text, "key") == plan0 && Member(text, "detail") == annotation);
        }
        {
            var f = Revit(); var o = NewOutput();
            var element = insertedRealized[DocRegeneration.ElementSource(Door(0))];
            f.DependentsOf[plan0Stale] = reported.Append(element).ToArray();
            Refused("a dependent realized for a model element", DocReplaceCodes.DependentKept, Args(o, "--probe-dependents", Pipe, "--accept", annotation), f, Host(f),
                text => Member(text, "detail") == element);
        }
        {
            var f = Revit(); var o = NewOutput();
            var other = Fake("annotation:shared");
            f.Existing.Add(other);
            var plan1Stale = realized[DocRegeneration.ItemSource("plan-" + Role(1, "level").ToString("N"))];
            f.DependentsOf[plan0Stale] = reported.Append(other).ToArray();
            f.DependentsOf[plan1Stale] = new[] { other };
            Refused("an accepted dependent reported by two deletes", DocUpdateDependentsCodes.DependentShared, Args(o, "--probe-dependents", Pipe, "--accept", annotation, "--accept", other), f, Host(f),
                text => Member(text, "detail") == other);
        }
        {
            var f = Revit(); var o = NewOutput();
            f.Override = _ => null;
            Refused("a pipe that closes without a reply", RevitCommandCodes.NoResponse, Args(o, "--probe-dependents", Pipe, "--accept", annotation), f, Host(f), lines: 1);
        }
        {
            var f = Revit(); var o = NewOutput();
            f.Override = _ => throw new IOException("broken pipe");
            Refused("a broken pipe", RevitCommandCodes.PipeIoError, Args(o, "--probe-dependents", Pipe, "--accept", annotation), f, Host(f), lines: 1);
        }
        {
            var f = Revit(); var o = NewOutput();
            f.Override = _ => "not json";
            Refused("a malformed reply", RevitCommandCodes.ResponseInvalid, Args(o, "--probe-dependents", Pipe, "--accept", annotation), f, Host(f), lines: 1);
        }
        {
            var f = Revit(); var o = NewOutput();
            Refused("a document outside the enabled roots", RevitCommandCodes.DocumentNotEnabled, Args(o, "--probe-dependents", Pipe, "--accept", annotation), f, Host(f, RevitCommandPolicy.None),
                text => Member(text, "field") == "document.path", lines: 0);
        }
        {
            var f = Revit(); var o = NewOutput();
            var policyReads = 0;
            Refused("an unreadable enabled-roots policy", "enabled_roots_invalid", Args(o, "--probe-dependents", Pipe), f,
                new DocUpdateRevit(() => { policyReads++; throw new RevitCommandException("enabled_roots_invalid", "enabled_roots", "test"); }, f.Send),
                text => Member(text, "detail") == "enabled_roots", lines: 0);
            Check(policyReads == 1, "the policy is read once per probe run", N(policyReads));
        }
        {
            var f = Revit(); var o = NewOutput(); var policyReads = 0;
            Refused("an accepted entry that is not a UniqueId", DocUpdateDependentsCodes.AcceptedInvalid, Args(o, "--probe-dependents", Pipe, "--accept", "door-1"), f, Host(f, null, () => policyReads++), lines: 0);
            Check(policyReads == 0, "an invalid accepted entry is refused before the policy is read", N(policyReads));
        }
        {
            var f = Revit(); var o = NewOutput(); var policyReads = 0;
            Refused("a pipe that is not a process pipe", RevitCommandCodes.ExplicitProcessPipeRequired, Args(o, "--probe-dependents", "revit"), f, Host(f, null, () => policyReads++),
                text => Member(text, "detail") == "pipe_name", lines: 0);
            Check(policyReads == 0, "a bad pipe name is refused before the policy is read", N(policyReads));
        }
        {
            var f = Revit(); var o = NewOutput(); var policyReads = 0;
            var missing = Path.Combine(scratch, "absent.json");
            var arguments = Args(o, "--probe-dependents", Pipe); arguments[2] = missing;
            Refused("a missing input", "input_not_found", arguments, f, Host(f, null, () => policyReads++), lines: 0);
            Check(policyReads == 0, "inputs are read before the policy", N(policyReads));
        }
        {
            var f = Revit(); var existing = Input("existing.json", "keep me");
            var r = Execute(Args(existing, "--probe-dependents", Pipe, "--accept", annotation), Host(f));
            Check(r.Exit == 1 && Member(r.Out, "code") == "output_exists" && File.ReadAllText(existing) == "keep me" && f.Lines.Count == 0, "never overwrites an existing output and sends nothing", D(r));
        }
        // A DocUpdate.Plan refusal of the first plan is passed on unchanged, before anything is sent.
        {
            var f = Revit(); var o = NewOutput();
            var arguments = Args(o, "--probe-dependents", Pipe); arguments[6] = Input("realized-before.json", MapJson(realized));
            Refused("a first plan the library refuses", "input_unresolved", arguments, f, Host(f), lines: 0);
        }
        // Usage: --probe-dependents with --dependents, twice, or --accept without it.
        foreach (var bad in new[]
        {
            Args(NewOutput(), "--probe-dependents", Pipe, "--dependents", realizedPath),
            Args(NewOutput(), "--probe-dependents", Pipe, "--probe-dependents", Pipe),
            Args(NewOutput(), "--accept", annotation),
            Args(NewOutput(), "--dependents", realizedPath, "--accept", annotation),
            Args(NewOutput(), "--probe-dependents"),
            Args(NewOutput(), "--probe-dependents", "--accept"),
            Args(NewOutput(), "--probe-dependents", Pipe, "--accept", ""),
        })
        {
            var f = Revit(); var policyReads = 0;
            var target = bad[10];
            var r = Execute(bad, Host(f, null, () => policyReads++));
            Check(r.Exit == 1 && r.Out == JsonSerializer.Serialize(new { status = "failed", code = DocUpdateCommand.UsageCode }) && !File.Exists(target) && f.Lines.Count == 0 && policyReads == 0,
                "usage refused: " + string.Join(" ", bad.Skip(11)), D(r));
        }

        // ---- 3. Without --probe-dependents: no policy, no pipe, the P4-08 bytes and summary ----
        DocUpdateRevit Untouchable(List<string> touched) =>
            new(() => { touched.Add("policy"); throw new InvalidOperationException("policy read without --probe-dependents"); }, (_, _) => { touched.Add("pipe"); throw new IOException("pipe used without --probe-dependents"); });
        var movedBindingsPath = Input("moved-bindings.json", BindingsJson(Bindings(before)));
        var moved = before with
        {
            Elements = before.Elements.Select(e => e.Id == Door(1) ? e with { GeometryMetres = new[] { e.GeometryMetres[0] + 0.2, e.GeometryMetres[1], e.GeometryMetres[2] + 0.2, e.GeometryMetres[3], e.GeometryMetres[4], e.GeometryMetres[5] } } : e).ToArray()
        };
        var movedPath = Input("moved.json", JsonSerializer.Serialize(moved));
        var realizedBeforePath = Input("realized-plain.json", MapJson(realized));
        var plainExpected = Utf8(JsonSerializer.Serialize(DocUpdate.Plan(before, moved, Bindings(before), realized)));
        string[] Plain(string o) => new[] { "doc-update", "--before", beforePath, "--after", movedPath, "--realized", realizedBeforePath, "--bindings", movedBindingsPath, "--output", o };
        string PlainSummary(string o) => JsonSerializer.Serialize(new
        {
            status = "succeeded", output = o, bytes = (long)plainExpected.Length, sha256 = Convert.ToHexString(SHA256.HashData(plainExpected)),
            regenerated = DocUpdate.Plan(before, moved, Bindings(before), realized).Regeneration.Regenerated.Count, steps = DocUpdate.Plan(before, moved, Bindings(before), realized).Replacement.Commands.Steps.Count,
            forced = DocUpdate.Plan(before, moved, Bindings(before), realized).Forced.Count, retired = DocUpdate.Plan(before, moved, Bindings(before), realized).Retired.Count, applied = false,
        });
        {
            var touched = new List<string>();
            var o = NewOutput();
            var r = Execute(Plain(o), Untouchable(touched));
            Check(r.Exit == 0 && File.Exists(o) && File.ReadAllBytes(o).AsSpan().SequenceEqual(plainExpected) && r.Out == PlainSummary(o) && touched.Count == 0,
                "without --probe-dependents: P4-08 bytes and summary, no policy read, no pipe", D(r) + " touched " + string.Join(",", touched));
            var c = NewOutput();
            var cli = Cli(Plain(c));
            Check(cli.Exit == 0 && File.Exists(c) && File.ReadAllBytes(c).AsSpan().SequenceEqual(plainExpected) && cli.Out == PlainSummary(c) && cli.Err.Length == 0,
                "the real CLI without --probe-dependents writes the same bytes and summary", "exit " + N(cli.Exit) + " " + cli.Out + " " + cli.Err);
            var d = NewOutput();
            var dependentsPath = Input("dependents-empty.json", "{}");
            var withDependents = Execute(Plain(d).Concat(new[] { "--dependents", dependentsPath }).ToArray(), Untouchable(touched));
            var withDependentsExpected = Utf8(JsonSerializer.Serialize(DocUpdate.Plan(before, moved, Bindings(before), realized, new Dictionary<string, IReadOnlyList<string>>())));
            Check(withDependents.Exit == 0 && File.Exists(d) && File.ReadAllBytes(d).AsSpan().SequenceEqual(withDependentsExpected) && touched.Count == 0, "--dependents still plans offline", D(withDependents));
        }

        // ---- 4. The real Release CLI ----
        {
            var o = NewOutput();
            var r = Cli(Args(o, "--probe-dependents", "revit"));
            Check(r.Exit == 1 && r.Out == JsonSerializer.Serialize(new { status = "failed", code = RevitCommandCodes.ExplicitProcessPipeRequired, detail = "pipe_name" }) && !File.Exists(o),
                "the real CLI refuses a non-process pipe before reading anything", "exit " + N(r.Exit) + " " + r.Out + " " + r.Err);
            // C:\Repo\... is under no enabled root of this repository: the first dry run is refused by the copies guard
            // before the pipe is opened (no add-in is listening on this name).
            var n = NewOutput();
            var notEnabled = Cli(Args(n, "--probe-dependents", "bim-agentic-os-2025-1", "--accept", annotation));
            Check(notEnabled.Exit == 1 && Member(notEnabled.Out, "code") == RevitCommandCodes.DocumentNotEnabled && Member(notEnabled.Out, "field") == "document.path" && !File.Exists(n),
                "the real CLI refuses a document outside the enabled roots before the pipe and writes no file", "exit " + N(notEnabled.Exit) + " " + notEnabled.Out + " " + notEnabled.Err);
            // No edit: nothing to delete, so nothing is dry-run and the plan is the plain doc-update plan.
            var sameAfter = Input("same-after.json", JsonSerializer.Serialize(before));
            var e = NewOutput();
            var empty = Cli("doc-update", "--before", beforePath, "--after", sameAfter, "--realized", realizedBeforePath, "--bindings", movedBindingsPath, "--output", e, "--probe-dependents", "bim-agentic-os-2025-1");
            var emptyExpected = Utf8(JsonSerializer.Serialize(DocUpdate.Plan(before, before, Bindings(before), realized)));
            Check(empty.Exit == 0 && File.Exists(e) && File.ReadAllBytes(e).AsSpan().SequenceEqual(emptyExpected) && Member(empty.Out, "status") == "succeeded",
                "the real CLI probes nothing for a plan without deletes and writes the plain plan", "exit " + N(empty.Exit) + " " + empty.Out + " " + empty.Err);
        }

        // ---- Static: the probe plans only; it never runs a plan ----
        var probeSourcePath = Path.Combine(root, "interfaces", "cli", "DocUpdateProbe.cs");
        Check(File.Exists(probeSourcePath), "interfaces/cli/DocUpdateProbe.cs exists");
        if (File.Exists(probeSourcePath))
        {
            var source = File.ReadAllText(probeSourcePath);
            Check(source.Contains("DocUpdateDependents.Plan(", StringComparison.Ordinal) && source.Contains("new RevitServiceTransport(", StringComparison.Ordinal)
                && new[] { "DocUpdateDependents.Update", "DocUpdate.Run", "DocUpdate.Resume", "DocReplace.Run", "PlanRunner.Run", "File.", "Directory.", "ProjectStore" }.All(api => !source.Contains(api, StringComparison.Ordinal)),
                "the probe plans with DocUpdateDependents.Plan through RevitServiceTransport and never runs, resumes or writes");
        }
        Check(Cli("no-such-verb").Out.Contains("--probe-dependents process-pipe-name [--accept uid]...", StringComparison.Ordinal), "default usage lists --probe-dependents");
    }
}
