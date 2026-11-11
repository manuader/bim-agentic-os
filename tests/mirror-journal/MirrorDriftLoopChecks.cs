using Bim.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
// P2-14 / P2-16 drift loop end to end on the fake add-in: real processes only. A fake add-in pipe
// (tests/integration/fake-revit-command-pipe.ps1, the headless stand-in the catalogue session test uses) answers the real
// Release CLI (interfaces/cli/bin/Release/net8.0/Bim.Cli.dll), and the chain
//   revit-command query.snapshot -> mirror-drift -> mirror-drift-select -> mirror-drift-adopt -> revit-command query.snapshot
//   -> mirror-drift
// runs over files exactly as an operator would. Two scenarios, each run twice:
// P2-14 (no journal, update adopt only). Oracles:
//   1. Baseline: walls created through the CLI, a Core store seeded with the CLI init verb from that first snapshot (own
//      Core ids, a map Core id -> UniqueId) and mirror-drift reports no drift (the fixture is consistent).
//   2. An external edit (element.move of one wall through the CLI) is reported as one changed finding with an adopt
//      proposal; mirror-drift-select with {adopt} writes one BIM-IR operation; mirror-drift-adopt commits revision 2.
//   3. The store head is the adopted state: the moved wall's Core element has the Revit geometry, every other element
//      is untouched; a replay prints replayed; the pre-adopt mirror is refused stale.
//   4. A fresh query.snapshot (same readback as before the adopt) against the mirror at the new revision reports no drift
//      for the adopted element (no drift at all).
//   5. The fake add-in serves exactly the eight expected requests (operation ids in order) and exits 0 by itself.
//   6. Deterministic: a second complete run (new fake add-in, new store) produces byte-identical snapshot readbacks,
//      files and outputs.
// P2-16 (journal; update, create and delete adopts). The mirror request carries a journal.read reply (peek, complete,
// attached) naming the external edits; options.journal_after_sequence is the commit point. Oracles:
//   1. Baseline as above, with an empty journal: no drift.
//   2. Three external edits through the CLI (element.move of wall B, wall.create of wall C, element.delete of wall A) are
//      reported as exactly three findings, each external_edit with its journal evidence: B changed (journal:1:external),
//      C extra (journal:2:external), A missing (journal:3:external:deleted_id_match); evidence complete; every proposal
//      recommends adopt (update, create, delete).
//   3. mirror-drift-select {adopt x3} writes three operations, one map add (C) and one map removal (A); mirror-drift-adopt
//      commits revision 2: B at the Revit geometry, C created under its snapshot id (Revit geometry, Level 1 dependency,
//      adopt provenance), A gone, the levels untouched; replay prints replayed; the revision-1 mirror is refused stale.
//   4. A fresh query.snapshot against the mirror at revision 2 (map updated from the selection's map_adds/map_removals,
//      journal_after_sequence at the last entry) reports no drift at all.
//   5. The fake add-in serves exactly the ten expected requests and exits 0; two runs are byte-identical.
// The journal is written by this test: the fake add-in's journal.read answers no entries (as the production recorder, it
// skips the add-in's own transactions, and every edit here reaches the fake through the add-in pipe), and the CLI has no
// journal verb. Its deletion entry carries the element id Revit's UniqueId convention puts in the hex suffix.
// Bounded: 5 minutes per run, 60 s per process, the fake add-in gives up after 120 s. Needs pwsh (BIM_PWSH, set by
// scripts/verify.ps1, else pwsh on PATH; a bare local run without pwsh skips, verify.ps1 and CI fail) and the built
// CLI (scripts/verify.ps1 builds interfaces/cli before this project). No Revit is involved: a
// passing run is unit-tested evidence only.
static class MirrorDriftLoopChecks
{
    const string DocumentUid = "5e1f0a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b-0000e2e1";
    const string WallType = "Generic - 200mm";
    static readonly Guid ProjectId = Guid.Parse("a0000000-0000-4000-8000-0000000000e2");
    static readonly UTF8Encoding Utf8 = new(false);

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, "drift-loop: " + name, detail);
    static readonly List<string> Temporary = new();

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        try { Checks(); }
        catch (Exception error) { Check(false, "oracle threw", error.GetType().Name + ": " + error.Message); }
        finally { foreach (var directory in Temporary) try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root with global.json not found");
    }

    // What one complete run leaves behind, compared byte for byte between two runs.
    sealed record Loop(string Snapshot0, string Snapshot1, string Drift1, string Selection, string Adopt, string ProjectJson, string Snapshot2, string Drift2);

    // The fake add-in serves exactly these requests, in this order, and then exits by itself.
    static readonly string[] ExpectedRequests = { "p2-14-levels", "p2-14-wall-types", "p2-14-wall-a", "p2-14-wall-b", "p2-14-snapshot-0", "p2-14-move-b", "p2-14-snapshot-1", "p2-14-snapshot-2" };
    static readonly string[] JournalRequests = { "p2-16-levels", "p2-16-wall-types", "p2-16-wall-a", "p2-16-wall-b", "p2-16-snapshot-0", "p2-16-move-b", "p2-16-wall-c", "p2-16-delete-a", "p2-16-snapshot-1", "p2-16-snapshot-2" };
    // Bounds: the whole run, each CLI or pwsh process, and the fake add-in's own lifetime.
    static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(5);
    const int CallTimeoutMs = 60000;
    const int FakeTimeoutSeconds = 120;

    static void Checks()
    {
        var root = FindRoot();
        var cliDll = Path.Combine(root, "interfaces", "cli", "bin", "Release", "net8.0", "Bim.Cli.dll");
        if (!File.Exists(cliDll)) { Check(false, "CLI binary present (build interfaces/cli --configuration Release first, as scripts/verify.ps1 does)", cliDll); return; }
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        // scripts/verify.ps1 sets BIM_PWSH to the pwsh running it; otherwise pwsh on PATH.
        var configured = Environment.GetEnvironmentVariable("BIM_PWSH") is { Length: > 0 } configuredPwsh ? configuredPwsh : null;
        var pwsh = configured ?? "pwsh";
        if (!PwshAvailable(pwsh, out var why))
        {
            // Under verify.ps1 (BIM_PWSH set) and on every CI runner the loop must run; only a bare local run may skip.
            if (configured is not null || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") Check(false, "pwsh runnable (" + pwsh + ")", why);
            else Console.WriteLine("SKIP drift-loop: pwsh not found (" + why + ")");
            return;
        }
        // The document must sit under the repository's .bim/native-runs (the CLI's default enabled root) for writes; both
        // runs of a scenario name the same document path (it is in every restore envelope), one after the other.
        var runDirectory = Path.Combine(root, ".bim", "native-runs", "mirror-drift-loop-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(runDirectory);
        var first = RunLoop(root, dotnet, pwsh, cliDll, runDirectory, 1, report: true);
        var second = RunLoop(root, dotnet, pwsh, cliDll, runDirectory, 2, report: false);
        Same(first, second, "");
        var journalFirst = RunJournalLoop(root, dotnet, pwsh, cliDll, runDirectory, 1, report: true);
        var journalSecond = RunJournalLoop(root, dotnet, pwsh, cliDll, runDirectory, 2, report: false);
        Same(journalFirst, journalSecond, "journal: ");
    }

    static void Same(Loop? first, Loop? second, string scenario)
    {
        if (first is null || second is null) { Check(false, scenario + "both runs completed", first is null ? "run 1 stopped" : "run 2 stopped"); return; }
        Check(first.Snapshot0 == second.Snapshot0, scenario + "deterministic: the first query.snapshot readback is identical in both runs");
        Check(first.Snapshot2 == second.Snapshot2, scenario + "deterministic: the post-adopt query.snapshot readback is identical in both runs");
        Check(first.Snapshot1 == second.Snapshot1, scenario + "deterministic: the edited query.snapshot readback is identical in both runs");
        Check(first.Drift1 == second.Drift1, scenario + "deterministic: the mirror-drift file is byte-identical in both runs");
        Check(first.Selection == second.Selection, scenario + "deterministic: the mirror-drift-select file is byte-identical in both runs");
        Check(first.Adopt == second.Adopt, scenario + "deterministic: mirror-drift-adopt prints the same outcome, revision and key", first.Adopt + " vs " + second.Adopt);
        Check(first.ProjectJson == second.ProjectJson, scenario + "deterministic: the adopted store project.json is byte-identical in both runs");
        Check(first.Drift2 == second.Drift2, scenario + "deterministic: the second mirror-drift file is byte-identical in both runs");
    }

    static bool PwshAvailable(string pwsh, out string why)
    {
        why = "";
        try
        {
            var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) { why = "pwsh did not start"; return false; }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CallTimeoutMs)) { Kill(process); why = "pwsh timed out"; return false; }
            process.WaitForExit();
            if (process.ExitCode != 0) why = "exit " + process.ExitCode + " " + stdout.Result.Trim() + " " + stderr.Result.ReplaceLineEndings(" ").Trim();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception error) { why = error.Message; return false; }
    }

    // Best effort: the process may already have exited (or never be killable); a failed kill is not a test result.
    static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (NotSupportedException) { }
    }

    // .NET pipes on Unix are sockets under the temp directory; both processes use the same short one (108-byte limit).
    static void Unix(ProcessStartInfo start) { if (!OperatingSystem.IsWindows()) start.Environment["TMPDIR"] = "/tmp"; }

    // One run's processes and files: a scratch directory, an empty document under the run directory, the fake add-in
    // behind a fresh pipe (serving exactly maxRequests requests and logging their operation ids) and the CLI calls, all
    // within the run budget. Disposing it kills a fake add-in a failed run left running.
    sealed class Harness : IDisposable
    {
        readonly string dotnet, cliDll, ready, log;
        readonly DateTime deadline = DateTime.UtcNow + RunBudget;
        readonly Process server;
        readonly Task<string> serverOut, serverErr;
        readonly string pipe = "bim-agentic-os-2025-" + Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
        int n;
        public string Document { get; }
        public string Scratch { get; }

        public Harness(string root, string dotnet, string pwsh, string cliDll, string runDirectory, string documentName, int maxRequests)
        {
            this.dotnet = dotnet;
            this.cliDll = cliDll;
            Scratch = Path.Combine(Path.GetTempPath(), "bim-drift-loop-" + Guid.NewGuid().ToString("N"));
            Temporary.Add(Scratch);
            Directory.CreateDirectory(runDirectory);
            Directory.CreateDirectory(Scratch);
            Document = Path.GetFullPath(Path.Combine(runDirectory, documentName));
            File.WriteAllBytes(Document, Array.Empty<byte>());
            ready = Path.Combine(Scratch, "ready.txt");
            log = Path.Combine(Scratch, "received.txt");
            var serverStart = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root, "tests", "integration", "fake-revit-command-pipe.ps1"), "-PipeName", pipe, "-DocumentUid", DocumentUid,
                "-ReadyFile", ready, "-LogFile", log, "-MaxRequests", maxRequests.ToString(CultureInfo.InvariantCulture), "-TimeoutSeconds", FakeTimeoutSeconds.ToString(CultureInfo.InvariantCulture) }) serverStart.ArgumentList.Add(argument);
            Unix(serverStart);
            server = Process.Start(serverStart) ?? throw new InvalidOperationException("fake add-in pipe did not start");
            serverOut = server.StandardOutput.ReadToEndAsync();
            serverErr = server.StandardError.ReadToEndAsync();
        }

        public int Budget() => (int)Math.Max(0, Math.Min(CallTimeoutMs, (deadline - DateTime.UtcNow).TotalMilliseconds));

        public bool WaitReady(out string detail)
        {
            while (!File.Exists(ready) && Budget() > 0 && !server.HasExited) Thread.Sleep(100);
            var isReady = File.Exists(ready);
            detail = isReady ? "" : server.HasExited ? serverErr.Result : "timed out";
            return isReady;
        }

        public string NewPath(string stem) => Path.Combine(Scratch, (++n).ToString("D2", CultureInfo.InvariantCulture) + "-" + stem + ".json");
        public string Write(string stem, string content) { var path = NewPath(stem); File.WriteAllText(path, content, Utf8); return path; }

        public (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Utf8 };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Unix(start);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Budget())) { Kill(process); return (-1, "<timed out (60 s per call, 5 min per run)>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result.TrimEnd('\r', '\n'), stderr.Result.ReplaceLineEndings(" ").Trim());
        }

        string Envelope(string operationId, string command, Action<Utf8JsonWriter> args)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("protocol_version", 1);
                writer.WriteString("operation_id", operationId);
                writer.WriteStartObject("document");
                writer.WriteString("path", Document);
                writer.WriteString("unique_id", DocumentUid);
                writer.WriteEndObject();
                writer.WriteString("command", command);
                writer.WriteStartObject("args");
                args(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Utf8.GetString(buffer.ToArray());
        }

        public (int Exit, string Out, string Err) Command(string operationId, string command, Action<Utf8JsonWriter> args) =>
            Cli("revit-command", Write("envelope-" + command, Envelope(operationId, command, args)), pipe);

        // The fake add-in exits 0 by itself after its last request; the detail says how it ended otherwise.
        public (bool Ok, string Detail) Finish()
        {
            var exited = server.WaitForExit(30000);
            if (exited) server.WaitForExit();
            return (exited && server.ExitCode == 0, exited ? "exit " + server.ExitCode + " " + serverErr.Result.ReplaceLineEndings(" ").Trim() : "still running");
        }

        public string[] Received() => File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>();

        public void Dispose()
        {
            // Only a failed run leaves the fake add-in running.
            Kill(server);
            server.WaitForExit(10000);
            _ = serverOut.Wait(5000);
            _ = serverErr.Wait(5000);
            server.Dispose();
        }
    }

    static string R((int Exit, string Out, string Err) r) => "exit " + r.Exit + " " + (r.Out.Length > 1500 ? r.Out[..1500] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
    static bool Succeeded((int Exit, string Out, string Err) r) => r.Exit == 0 && Status(r.Out) == "succeeded";
    static string Head(string text) => text.Length > 1500 ? text[..1500] : text;

    static void WallArgs(Utf8JsonWriter w, string level, string type, double ax, double ay, double bx, double by)
    {
        w.WriteString("level", level);
        w.WriteString("type", type);
        w.WriteNumber("height_mm", 3000);
        w.WriteStartObject("curve");
        w.WriteString("kind", "line");
        w.WriteStartArray("start"); w.WriteNumberValue(ax); w.WriteNumberValue(ay); w.WriteEndArray();
        w.WriteStartArray("end"); w.WriteNumberValue(bx); w.WriteNumberValue(by); w.WriteEndArray();
        w.WriteEndObject();
    }

    static void MoveArgs(Utf8JsonWriter w, string uid)
    {
        w.WriteStartArray("element_uids"); w.WriteStringValue(uid); w.WriteEndArray();
        w.WriteStartArray("vector_mm"); w.WriteNumberValue(500); w.WriteNumberValue(0); w.WriteNumberValue(0); w.WriteEndArray();
    }

    static Loop? RunLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory, int run, bool report)
    {
        void Check(bool condition, string name, string detail = "") { if (report || !condition) MirrorDriftLoopChecks.Check(condition, "run " + run + ": " + name, detail); }
        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Loop.rvt", ExpectedRequests.Length);
        var document = h.Document;
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready", notReady); return null; }

        // ---- Seed the fake document through the CLI: two walls on Level 1 ----
        var levels = h.Command("p2-14-levels", "query.levels", _ => { });
        var types = h.Command("p2-14-wall-types", "query.wall_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed", R(levels) + " | " + R(types)); return null; }
        var level = Pick(levels.Out, "levels", "name", "Level 1");
        var type = Pick(types.Out, "wall_types", "name", WallType);
        var wallA = h.Command("p2-14-wall-a", "wall.create", w => WallArgs(w, level, type, 0, 0, 5000, 0));
        var wallB = h.Command("p2-14-wall-b", "wall.create", w => WallArgs(w, level, type, 5000, 0, 5000, 4000));
        if (!Succeeded(wallA) || !Succeeded(wallB)) { Check(false, "two wall.create commands succeed through the CLI", R(wallA) + " | " + R(wallB)); return null; }
        var uidA = Created(wallA.Out);
        var uidB = Created(wallB.Out);

        // ---- Core store at revision 1 = the first snapshot, with its own Core ids and the add-in map ----
        var snapshot0 = h.Command("p2-14-snapshot-0", "query.snapshot", _ => { });
        if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return null; }
        var (project, map, levelNames) = CoreFrom(snapshot0.Out, levels.Out);
        var coreB = map.Single(pair => pair.Value == uidB).Key;
        var store = Path.Combine(h.Scratch, "store");
        var init = h.Cli("init", store, h.Write("project-r1", JsonSerializer.Serialize(project)));
        if (init.Exit != 0 || Status(init.Out) != "succeeded") { Check(false, "the CLI init verb seeds the store at revision 1", R(init)); return null; }
        Check(new ProjectStore(store).Read() is { Revision: 1 } seeded && seeded.Elements.Length == project.Elements.Length, "the CLI init verb seeds the store at revision 1");
        string Mirror(int revision) => MirrorJson(document, revision, map, levelNames);
        var mirror1 = h.Write("mirror-r1", Mirror(1));
        var snapshot0Path = h.Write("snapshot-0", snapshot0.Out);
        var baseline = h.Cli("mirror-drift", store, snapshot0Path, mirror1, h.NewPath("drift-baseline"));
        Check(baseline.Exit == 0 && Number(baseline.Out, "drift") == 0 && Number(baseline.Out, "proposals") == 0,
            "baseline: the store built from the first snapshot has no drift", R(baseline));

        // ---- External edit: wall B moved 500 mm east in the (fake) Revit document ----
        var move = h.Command("p2-14-move-b", "element.move", w => MoveArgs(w, uidB));
        if (!Succeeded(move)) { Check(false, "element.move of wall B succeeds through the CLI", R(move)); return null; }
        var snapshot1 = h.Command("p2-14-snapshot-1", "query.snapshot", _ => { });
        if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the edit succeeds", R(snapshot1)); return null; }
        var snapshot1Path = h.Write("snapshot-1", snapshot1.Out);
        var observedB = SnapshotGeometry(snapshot1.Out, uidB);
        Check(observedB.SequenceEqual(new[] { 5.5, 0, 5.5, 4, 3, 0.2 }), "the edited snapshot reports wall B 500 mm east", string.Join(",", observedB));

        // ---- mirror-drift: one changed finding, an adopt proposal for wall B ----
        var drift1Path = h.NewPath("drift-1");
        var drift1 = h.Cli("mirror-drift", store, snapshot1Path, mirror1, drift1Path);
        if (drift1.Exit != 0 || !File.Exists(drift1Path)) { Check(false, "mirror-drift after the edit succeeds", R(drift1)); return null; }
        var drift1Text = File.ReadAllText(drift1Path, Utf8);
        using (var parsed = JsonDocument.Parse(drift1Text))
        {
            var reportElement = parsed.RootElement.GetProperty("report");
            var findings = reportElement.GetProperty("findings").EnumerateArray().ToArray();
            var proposals = parsed.RootElement.GetProperty("resolution").GetProperty("proposals").EnumerateArray().ToArray();
            Check(reportElement.GetProperty("summary").GetProperty("drift").GetInt32() == 1 && findings.Length == 1
                && findings[0].GetProperty("kind").GetString() == "changed" && findings[0].GetProperty("unique_id").GetString() == uidB
                && findings[0].GetProperty("core_id").GetString() == coreB.ToString("D"),
                "mirror-drift reports exactly one changed finding: wall B, by UniqueId and Core id", Head(drift1Text));
            Check(proposals.Length == 1 && proposals[0].GetProperty("unique_id").GetString() == uidB
                && proposals[0].GetProperty("adopt").GetProperty("status").GetString() == MirrorDriftResolveStatus.Proposed
                && proposals[0].GetProperty("adopt").GetProperty("action").GetString() == MirrorDriftAdoptActions.Update,
                "the resolution proposes adopt (update) for wall B");
            Check(parsed.RootElement.GetProperty("applied").GetBoolean() == false && parsed.RootElement.GetProperty("core_store").GetProperty("revision").GetInt32() == 1,
                "mirror-drift applies nothing and names the store at revision 1");
        }
        Check(new ProjectStore(store).Read().Revision == 1, "mirror-drift leaves the store at revision 1");

        // ---- mirror-drift-select: adopt wall B ----
        var choices = h.Write("choices", Choices(uidB));
        var selectionPath = h.NewPath("selection");
        var select = h.Cli("mirror-drift-select", store, snapshot1Path, mirror1, choices, selectionPath);
        Check(select.Exit == 0 && Number(select.Out, "operations") == 1 && File.Exists(selectionPath), "mirror-drift-select writes a selection with one BIM-IR operation", R(select));
        if (!File.Exists(selectionPath)) return null;
        var selectionText = File.ReadAllText(selectionPath, Utf8);
        Check(new ProjectStore(store).Read().Revision == 1, "mirror-drift-select leaves the store at revision 1");

        // ---- mirror-drift-adopt: one Core revision ----
        var adopt = h.Cli("mirror-drift-adopt", store, selectionPath);
        Check(adopt.Exit == 0 && Status(adopt.Out) == "succeeded" && Text(adopt.Out, "outcome") == "committed" && Number(adopt.Out, "revision") == 2,
            "mirror-drift-adopt commits the selection as revision 2", R(adopt));
        var head = new ProjectStore(store).Read();
        var adoptedB = head.Elements.SingleOrDefault(element => element.Id == coreB);
        Check(head.Revision == 2 && adoptedB is not null && adoptedB.GeometryMetres.SequenceEqual(observedB) && adoptedB.Kind == "wall",
            "the store head holds wall B at the Revit geometry under its Core id", adoptedB is null ? "missing" : string.Join(",", adoptedB.GeometryMetres));
        Check(adoptedB is not null && adoptedB.Provenance == MirrorDriftResolve.AdoptProvenance, "the adopted wall carries the adopt provenance", adoptedB?.Provenance.ToString() ?? "");
        Check(head.Id == ProjectId && head.Elements.Length == project.Elements.Length && Unchanged(project, head, new[] { coreB }), "every other Core element is unchanged by the adopt");
        var projectJson = File.ReadAllText(Path.Combine(store, "project.json"), Utf8);
        var replay = h.Cli("mirror-drift-adopt", store, selectionPath);
        Check(replay.Exit == 0 && Text(replay.Out, "outcome") == "replayed" && Number(replay.Out, "revision") == 2 && File.ReadAllText(Path.Combine(store, "project.json"), Utf8) == projectJson,
            "a replay of the same selection prints replayed and writes nothing", R(replay));
        var stale = h.Cli("mirror-drift", store, snapshot1Path, mirror1, h.NewPath("drift-stale"));
        Check(stale.Exit == 1 && Text(stale.Out, "code") == MirrorDriftResolveCodes.ReportStale, "the pre-adopt mirror (revision 1) is refused stale against the new head", R(stale));

        // ---- Second loop pass: a fresh snapshot against the mirror at revision 2 has no drift ----
        var snapshot2 = h.Command("p2-14-snapshot-2", "query.snapshot", _ => { });
        if (!Succeeded(snapshot2)) { Check(false, "the query.snapshot after the adopt succeeds", R(snapshot2)); return null; }
        Check(Readback(snapshot2.Out) == Readback(snapshot1.Out), "the Revit side is unchanged by the adopt (same snapshot readback)");
        var mirror2 = h.Write("mirror-r2", Mirror(2));
        var drift2Path = h.NewPath("drift-2");
        var drift2 = h.Cli("mirror-drift", store, h.Write("snapshot-2", snapshot2.Out), mirror2, drift2Path);
        if (drift2.Exit != 0 || !File.Exists(drift2Path)) { Check(false, "the second mirror-drift succeeds", R(drift2)); return null; }
        var drift2Text = File.ReadAllText(drift2Path, Utf8);
        using (var parsed = JsonDocument.Parse(drift2Text))
        {
            var reportElement = parsed.RootElement.GetProperty("report");
            var summary = reportElement.GetProperty("summary");
            Check(!reportElement.GetProperty("findings").EnumerateArray().Any(finding => finding.GetProperty("unique_id").GetString() == uidB),
                "the second mirror-drift reports no drift for the adopted wall B", Head(drift2Text));
            Check(summary.GetProperty("drift").GetInt32() == 0 && summary.GetProperty("unchanged").GetInt32() == project.Elements.Length
                && parsed.RootElement.GetProperty("resolution").GetProperty("proposals").GetArrayLength() == 0 && parsed.RootElement.GetProperty("core_store").GetProperty("revision").GetInt32() == 2,
                "the second mirror-drift reports no drift at all: every element unchanged, no proposals, store at revision 2", summary.GetRawText());
        }
        Check(Number(drift2.Out, "drift") == 0 && Number(drift2.Out, "proposals") == 0, "the second mirror-drift prints drift 0 and proposals 0", R(drift2));
        Check(uidA != uidB, "two distinct walls were created");

        // ---- The fake add-in served exactly the expected requests and exited by itself ----
        var (finished, how) = h.Finish();
        Check(finished, "the fake add-in exits 0 after exactly " + ExpectedRequests.Length + " requests", how);
        var received = h.Received();
        Check(received.SequenceEqual(ExpectedRequests), "the fake add-in received the expected operation ids in order", string.Join(",", received));
        return new Loop(Readback(snapshot0.Out), Readback(snapshot1.Out), drift1Text, selectionText, adopt.Out, projectJson, Readback(snapshot2.Out), drift2Text);
    }

    // P2-16: the same loop with the journal in the mirror request and three external edits (move B, create C, delete A),
    // all adopted in one selection.
    static Loop? RunJournalLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory, int run, bool report)
    {
        void Check(bool condition, string name, string detail = "") { if (report || !condition) MirrorDriftLoopChecks.Check(condition, "journal run " + run + ": " + name, detail); }
        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Journal.rvt", JournalRequests.Length);
        var document = h.Document;
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready", notReady); return null; }

        // ---- Seed: walls A and B on Level 1, the store at revision 1 from the first snapshot ----
        var levels = h.Command("p2-16-levels", "query.levels", _ => { });
        var types = h.Command("p2-16-wall-types", "query.wall_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed", R(levels) + " | " + R(types)); return null; }
        var level = Pick(levels.Out, "levels", "name", "Level 1");
        var type = Pick(types.Out, "wall_types", "name", WallType);
        var wallA = h.Command("p2-16-wall-a", "wall.create", w => WallArgs(w, level, type, 0, 0, 5000, 0));
        var wallB = h.Command("p2-16-wall-b", "wall.create", w => WallArgs(w, level, type, 5000, 0, 5000, 4000));
        if (!Succeeded(wallA) || !Succeeded(wallB)) { Check(false, "two wall.create commands succeed through the CLI", R(wallA) + " | " + R(wallB)); return null; }
        var uidA = Created(wallA.Out);
        var uidB = Created(wallB.Out);
        var snapshot0 = h.Command("p2-16-snapshot-0", "query.snapshot", _ => { });
        if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return null; }
        var (project, map, levelNames) = CoreFrom(snapshot0.Out, levels.Out);
        var coreA = map.Single(pair => pair.Value == uidA).Key;
        var coreB = map.Single(pair => pair.Value == uidB).Key;
        var coreLevel = map.Single(pair => pair.Value == level).Key;
        var store = Path.Combine(h.Scratch, "store");
        var init = h.Cli("init", store, h.Write("project-r1", JsonSerializer.Serialize(project)));
        if (init.Exit != 0 || Status(init.Out) != "succeeded") { Check(false, "the CLI init verb seeds the store at revision 1", R(init)); return null; }
        var baseline = h.Cli("mirror-drift", store, h.Write("snapshot-0", snapshot0.Out),
            h.Write("mirror-baseline", MirrorJson(document, 1, map, levelNames, JournalReply(document, 0, Array.Empty<string>()), 0)), h.NewPath("drift-baseline"));
        Check(baseline.Exit == 0 && Number(baseline.Out, "drift") == 0 && Number(baseline.Out, "proposals") == 0,
            "baseline with an empty journal: the store built from the first snapshot has no drift", R(baseline));

        // ---- Three external edits in the (fake) Revit document: move B, create C, delete A ----
        var move = h.Command("p2-16-move-b", "element.move", w => MoveArgs(w, uidB));
        var wallC = h.Command("p2-16-wall-c", "wall.create", w => WallArgs(w, level, type, 0, 4000, 5500, 4000));
        var delete = h.Command("p2-16-delete-a", "element.delete", w => { w.WriteStartArray("element_uids"); w.WriteStringValue(uidA); w.WriteEndArray(); });
        if (!Succeeded(move) || !Succeeded(wallC) || !Succeeded(delete))
        { Check(false, "element.move of B, wall.create of C and element.delete of A succeed through the CLI", R(move) + " | " + R(wallC) + " | " + R(delete)); return null; }
        var uidC = Created(wallC.Out);
        var snapshot1 = h.Command("p2-16-snapshot-1", "query.snapshot", _ => { });
        if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the edits succeeds", R(snapshot1)); return null; }
        var snapshot1Path = h.Write("snapshot-1", snapshot1.Out);
        var observedB = SnapshotGeometry(snapshot1.Out, uidB);
        var observedC = SnapshotGeometry(snapshot1.Out, uidC);
        var snapC = SnapshotId(snapshot1.Out, uidC);
        Check(observedB.SequenceEqual(new[] { 5.5, 0, 5.5, 4, 3, 0.2 }) && observedC.SequenceEqual(new[] { 0, 4, 5.5, 4, 3, 0.2 }) && SnapshotId(snapshot1.Out, uidA) is null,
            "the edited snapshot reports B 500 mm east, the new wall C and no wall A", string.Join(",", observedB) + " | " + string.Join(",", observedC));

        // ---- The journal the add-in recorder would report for those edits (external origin, after sequence 0) ----
        var journal = JournalReply(document, 3, new[]
        {
            JournalEntry(1, "Move", modified: uidB),
            JournalEntry(2, "Wall", added: uidC),
            JournalEntry(3, "Delete", deletedId: long.Parse(uidA[37..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)),
        });
        var mirror1 = h.Write("mirror-r1", MirrorJson(document, 1, map, levelNames, journal, 0));

        // ---- mirror-drift: three findings, all external_edit, all recommending adopt ----
        var drift1Path = h.NewPath("drift-1");
        var drift1 = h.Cli("mirror-drift", store, snapshot1Path, mirror1, drift1Path);
        if (drift1.Exit != 0 || !File.Exists(drift1Path)) { Check(false, "mirror-drift with the journal succeeds", R(drift1)); return null; }
        var drift1Text = File.ReadAllText(drift1Path, Utf8);
        using (var parsed = JsonDocument.Parse(drift1Text))
        {
            var reportElement = parsed.RootElement.GetProperty("report");
            var summary = reportElement.GetProperty("summary");
            int S(string name) => summary.GetProperty(name).GetInt32();
            Check(S("drift") == 3 && S("changed") == 1 && S("extra") == 1 && S("missing") == 1 && S("external_edit") == 3 && S("addin_pending") == 0 && S("unknown") == 0 && S("out_of_scope") == 0,
                "mirror-drift reports three drifted elements, all external_edit", summary.GetRawText());
            Check(!reportElement.GetProperty("evidence").GetProperty("incomplete").GetBoolean(), "the journal evidence is complete", reportElement.GetProperty("evidence").GetRawText());
            var findings = reportElement.GetProperty("findings").EnumerateArray().ToDictionary(finding => finding.GetProperty("unique_id").GetString()!, finding => finding);
            bool Finding(string uid, string kind, Guid? coreId, Guid? snapshotId, string evidence) => findings.TryGetValue(uid, out var finding)
                && finding.GetProperty("kind").GetString() == kind && finding.GetProperty("element_kind").GetString() == "wall"
                && finding.GetProperty("classification").GetString() == MirrorDriftClasses.ExternalEdit
                && OptionalId(finding, "core_id") == coreId && OptionalId(finding, "snapshot_id") == snapshotId
                && finding.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { evidence });
            Check(findings.Count == 3, "exactly three findings", Head(drift1Text));
            Check(Finding(uidB, "changed", coreB, SnapshotId(snapshot1.Out, uidB), "journal:1:external"),
                "wall B (moved) is changed / external_edit with evidence journal:1:external", Head(drift1Text));
            Check(Finding(uidC, "extra", null, snapC, "journal:2:external"), "wall C (created) is extra / external_edit with evidence journal:2:external", Head(drift1Text));
            Check(Finding(uidA, "missing", coreA, null, "journal:3:external:deleted_id_match"),
                "wall A (deleted) is missing / external_edit with evidence journal:3:external:deleted_id_match", Head(drift1Text));
            var proposals = parsed.RootElement.GetProperty("resolution").GetProperty("proposals").EnumerateArray().ToDictionary(proposal => proposal.GetProperty("unique_id").GetString()!, proposal => proposal);
            bool Proposal(string uid, string action) => proposals.TryGetValue(uid, out var proposal)
                && proposal.GetProperty("classification").GetString() == MirrorDriftClasses.ExternalEdit
                && proposal.GetProperty("recommended").GetString() == MirrorDriftResolveActions.Adopt
                && proposal.GetProperty("adopt").GetProperty("status").GetString() == MirrorDriftResolveStatus.Proposed
                && proposal.GetProperty("adopt").GetProperty("action").GetString() == action
                && proposal.GetProperty("adopt").GetProperty("requires").GetArrayLength() == 0;
            Check(proposals.Count == 3 && Proposal(uidB, MirrorDriftAdoptActions.Update) && Proposal(uidC, MirrorDriftAdoptActions.Create) && Proposal(uidA, MirrorDriftAdoptActions.Delete),
                "the resolution recommends adopt for all three: update B, create C, delete A, none coupled", Head(parsed.RootElement.GetProperty("resolution").GetRawText()));
            Check(parsed.RootElement.GetProperty("applied").GetBoolean() == false && parsed.RootElement.GetProperty("core_store").GetProperty("revision").GetInt32() == 1,
                "mirror-drift applies nothing and names the store at revision 1");
        }
        Check(new ProjectStore(store).Read().Revision == 1, "mirror-drift leaves the store at revision 1");

        // ---- mirror-drift-select: adopt all three; the selection names the map changes ----
        var selectionPath = h.NewPath("selection");
        var select = h.Cli("mirror-drift-select", store, snapshot1Path, mirror1, h.Write("choices", Choices(uidB, uidC, uidA)), selectionPath);
        Check(select.Exit == 0 && Number(select.Out, "operations") == 3 && Number(select.Out, "restore_steps") == 0 && File.Exists(selectionPath),
            "mirror-drift-select writes a selection with three BIM-IR operations and no restore step", R(select));
        if (!File.Exists(selectionPath)) return null;
        var selectionText = File.ReadAllText(selectionPath, Utf8);
        var nextMap = new SortedDictionary<Guid, string>(map);
        using (var parsed = JsonDocument.Parse(selectionText))
        {
            var actions = parsed.RootElement.GetProperty("operations").EnumerateArray()
                .Select(operation => operation.GetProperty("action").GetString() + ":" + operation.GetProperty("element").GetProperty("id").GetString()).Order(StringComparer.Ordinal).ToArray();
            var expected = new[] { "create:" + snapC.GetValueOrDefault().ToString("D"), "delete:" + coreA.ToString("D"), "update:" + coreB.ToString("D") }.Order(StringComparer.Ordinal).ToArray();
            Check(actions.SequenceEqual(expected), "the selection's operations are update B, create C (snapshot id), delete A (Core id)", string.Join(",", actions));
            var adds = parsed.RootElement.GetProperty("map_adds").EnumerateArray().Select(entry => (Key: entry.GetProperty("key").GetString()!, Uid: entry.GetProperty("unique_id").GetString()!)).ToArray();
            var removals = parsed.RootElement.GetProperty("map_removals").EnumerateArray().Select(entry => (Key: entry.GetProperty("key").GetString()!, Uid: entry.GetProperty("unique_id").GetString()!)).ToArray();
            Check(adds.Length == 1 && Guid.Parse(adds[0].Key) == snapC && adds[0].Uid == uidC && removals.Length == 1 && Guid.Parse(removals[0].Key) == coreA && removals[0].Uid == uidA,
                "the selection adds C to the map and removes A from it", parsed.RootElement.GetProperty("map_adds").GetRawText() + " " + parsed.RootElement.GetProperty("map_removals").GetRawText());
            // The add-in applies the selection's map changes after the adopt; the next mirror request carries them.
            foreach (var (key, _) in removals) nextMap.Remove(Guid.Parse(key));
            foreach (var (key, uid) in adds) nextMap[Guid.Parse(key)] = uid;
        }
        Check(new ProjectStore(store).Read().Revision == 1, "mirror-drift-select leaves the store at revision 1");

        // ---- mirror-drift-adopt: one Core revision holding every adopt ----
        var adopt = h.Cli("mirror-drift-adopt", store, selectionPath);
        Check(adopt.Exit == 0 && Status(adopt.Out) == "succeeded" && Text(adopt.Out, "outcome") == "committed" && Number(adopt.Out, "revision") == 2,
            "mirror-drift-adopt commits the selection as revision 2", R(adopt));
        var head = new ProjectStore(store).Read();
        var adoptedB = head.Elements.SingleOrDefault(element => element.Id == coreB);
        var adoptedC = snapC is { } c ? head.Elements.SingleOrDefault(element => element.Id == c) : null;
        Check(head.Revision == 2 && head.Elements.Length == project.Elements.Length, "the store head is revision 2 with one wall deleted and one created", head.Elements.Length.ToString(CultureInfo.InvariantCulture));
        Check(adoptedB is not null && adoptedB.Kind == "wall" && adoptedB.GeometryMetres.SequenceEqual(observedB) && adoptedB.Dependencies.SequenceEqual(new[] { coreLevel }) && adoptedB.Provenance == MirrorDriftResolve.AdoptProvenance,
            "update adopt: the head holds wall B at the Revit geometry under its Core id with the adopt provenance", adoptedB is null ? "missing" : string.Join(",", adoptedB.GeometryMetres));
        Check(adoptedC is not null && adoptedC.Kind == "wall" && adoptedC.GeometryMetres.SequenceEqual(observedC) && adoptedC.Dependencies.SequenceEqual(new[] { coreLevel }) && adoptedC.Provenance == MirrorDriftResolve.AdoptProvenance,
            "create adopt: the head holds the new wall C under its snapshot id, on Level 1, with the adopt provenance", adoptedC is null ? "missing" : string.Join(",", adoptedC.GeometryMetres));
        Check(head.Elements.All(element => element.Id != coreA), "delete adopt: wall A is gone from the head");
        Check(head.Id == ProjectId && Unchanged(project, head, new[] { coreA, coreB }), "every other Core element (the levels) is unchanged by the adopt");
        var projectJson = File.ReadAllText(Path.Combine(store, "project.json"), Utf8);
        var replay = h.Cli("mirror-drift-adopt", store, selectionPath);
        Check(replay.Exit == 0 && Text(replay.Out, "outcome") == "replayed" && Number(replay.Out, "revision") == 2 && File.ReadAllText(Path.Combine(store, "project.json"), Utf8) == projectJson,
            "a replay of the same selection prints replayed and writes nothing", R(replay));
        var stale = h.Cli("mirror-drift", store, snapshot1Path, mirror1, h.NewPath("drift-stale"));
        Check(stale.Exit == 1 && Text(stale.Out, "code") == MirrorDriftResolveCodes.ReportStale, "the pre-adopt mirror (revision 1) is refused stale against the new head", R(stale));

        // ---- Second loop pass: revision-2 mirror with the updated map, journal consumed up to sequence 3 ----
        var snapshot2 = h.Command("p2-16-snapshot-2", "query.snapshot", _ => { });
        if (!Succeeded(snapshot2)) { Check(false, "the query.snapshot after the adopt succeeds", R(snapshot2)); return null; }
        Check(Readback(snapshot2.Out) == Readback(snapshot1.Out), "the Revit side is unchanged by the adopt (same snapshot readback)");
        var mirror2 = h.Write("mirror-r2", MirrorJson(document, 2, nextMap, levelNames, journal, 3));
        var drift2Path = h.NewPath("drift-2");
        var drift2 = h.Cli("mirror-drift", store, h.Write("snapshot-2", snapshot2.Out), mirror2, drift2Path);
        if (drift2.Exit != 0 || !File.Exists(drift2Path)) { Check(false, "the second mirror-drift succeeds", R(drift2)); return null; }
        var drift2Text = File.ReadAllText(drift2Path, Utf8);
        using (var parsed = JsonDocument.Parse(drift2Text))
        {
            var reportElement = parsed.RootElement.GetProperty("report");
            var summary = reportElement.GetProperty("summary");
            Check(summary.GetProperty("drift").GetInt32() == 3 && reportElement.GetProperty("findings").GetArrayLength() == 0 && summary.GetProperty("unchanged").GetInt32() == head.Elements.Length
                && summary.GetProperty("out_of_scope").GetInt32() == 0 && summary.GetProperty("not_comparable").GetInt32() == 0
                && parsed.RootElement.GetProperty("resolution").GetProperty("proposals").GetArrayLength() == 0 && parsed.RootElement.GetProperty("core_store").GetProperty("revision").GetInt32() == 2,
                "the second mirror-drift reports no drift: every head element (B, C, levels) unchanged, no proposals, store at revision 2", summary.GetRawText());
        }
        Check(Number(drift2.Out, "drift") == 0 && Number(drift2.Out, "proposals") == 0, "the second mirror-drift prints drift 0 and proposals 0", R(drift2));
        Check(uidA != uidB && uidC != uidA && uidC != uidB, "three distinct walls were created");

        // ---- The fake add-in served exactly the expected requests and exited by itself ----
        var (finished, how) = h.Finish();
        Check(finished, "the fake add-in exits 0 after exactly " + JournalRequests.Length + " requests", how);
        var received = h.Received();
        Check(received.SequenceEqual(JournalRequests), "the fake add-in received the expected operation ids in order", string.Join(",", received));
        return new Loop(Readback(snapshot0.Out), Readback(snapshot1.Out), drift1Text, selectionText, adopt.Out, projectJson, Readback(snapshot2.Out), drift2Text);
    }

    // Every element of before other than the changed ids is in after, identical.
    static bool Unchanged(Project before, Project after, Guid[] changed)
    {
        var original = before.Elements.Where(element => !changed.Contains(element.Id)).ToDictionary(element => element.Id);
        return original.All(pair => after.Elements.SingleOrDefault(element => element.Id == pair.Key) is { } kept
            && kept.Kind == pair.Value.Kind && kept.GeometryMetres.SequenceEqual(pair.Value.GeometryMetres) && kept.Dependencies.SequenceEqual(pair.Value.Dependencies) && kept.Provenance == pair.Value.Provenance);
    }

    static string Choices(params string[] uids) =>
        "{\"choices\":[" + string.Join(",", uids.Select(uid => "{\"proposal_id\":" + JsonSerializer.Serialize(uid) + ",\"action\":\"adopt\"}")) + "]}";

    // One journal_version 1 entry as JournalRecorder writes it, external origin, for this test's document.
    static string JournalEntry(long sequence, string transaction, string? added = null, string? modified = null, long? deletedId = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", sequence);
            writer.WriteString("recorded_at", "2026-11-11T12:00:0" + sequence.ToString(CultureInfo.InvariantCulture) + ".000Z");
            writer.WriteString("document_unique_id", DocumentUid);
            writer.WriteString("document_path", "");
            writer.WriteString("operation", "TransactionCommitted");
            writer.WriteString("origin", MirrorJournalOrigins.External);
            writer.WriteStartArray("transaction_names"); writer.WriteStringValue(transaction); writer.WriteEndArray();
            writer.WriteStartArray("added"); if (added is not null) writer.WriteStringValue(added); writer.WriteEndArray();
            writer.WriteStartArray("modified"); if (modified is not null) writer.WriteStringValue(modified); writer.WriteEndArray();
            writer.WriteStartArray("deleted_element_ids"); if (deletedId is { } id) writer.WriteNumberValue(id); writer.WriteEndArray();
            writer.WriteNumber("added_total", added is null ? 0 : 1);
            writer.WriteNumber("modified_total", modified is null ? 0 : 1);
            writer.WriteNumber("deleted_total", deletedId is null ? 0 : 1);
            writer.WriteBoolean("truncated", false);
            writer.WriteEndObject();
        }
        return Utf8.GetString(buffer.ToArray());
    }

    // A journal.read reply (peek, read after sequence 0, complete, recorder attached) holding the given entries.
    static string JournalReply(string documentPath, long current, IReadOnlyList<string> entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "succeeded");
            writer.WriteString("verification", "unverified");
            writer.WriteBoolean("attached", true);
            writer.WriteNumber("dropped", 0);
            writer.WriteNumber("skipped_add_in", 0);
            writer.WriteNumber("failures", 0);
            writer.WriteNumber("unidentified", 0);
            writer.WriteStartObject("report");
            writer.WriteNumber("journal_version", 1);
            writer.WriteString("document_unique_id", DocumentUid);
            writer.WriteString("document_path", documentPath);
            writer.WriteNumber("baseline_sequence", 0);
            writer.WriteNumber("current_sequence", current);
            writer.WriteNumber("after_sequence", 0);
            writer.WriteBoolean("baseline_existed", true);
            writer.WriteBoolean("peek", true);
            writer.WriteBoolean("journal_complete", true);
            writer.WriteNumber("failures_since_baseline", 0);
            writer.WriteBoolean("has_more", false);
            writer.WriteNumber("next_after_sequence", current);
            writer.WriteStartArray("entries");
            foreach (var entry in entries) writer.WriteRawValue(entry);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Utf8.GetString(buffer.ToArray());
    }

    // ---- Small JSON helpers over CLI output ----
    static string? Status(string json) => Text(json, "status");
    static string? Text(string json, string member)
    {
        try { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null; }
        catch (JsonException) { return null; }
    }
    static int? Number(string json, string member)
    {
        try { using var parsed = JsonDocument.Parse(json); return parsed.RootElement.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null; }
        catch (JsonException) { return null; }
    }
    static Guid? OptionalId(JsonElement element, string member) =>
        element.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? Guid.Parse(value.GetString()!) : null;
    static string Readback(string response)
    {
        using var parsed = JsonDocument.Parse(response);
        return parsed.RootElement.GetProperty("readback").GetRawText();
    }
    static string Pick(string response, string list, string member, string value)
    {
        using var parsed = JsonDocument.Parse(response);
        foreach (var row in parsed.RootElement.GetProperty("readback").GetProperty(list).EnumerateArray())
            if (row.GetProperty(member).GetString() == value) return row.GetProperty("unique_id").GetString()!;
        throw new InvalidOperationException(list + " has no " + member + " " + value);
    }
    static string Created(string response)
    {
        using var parsed = JsonDocument.Parse(response);
        var created = parsed.RootElement.GetProperty("created");
        if (created.GetArrayLength() != 1) throw new InvalidOperationException("expected one created element: " + response);
        return created[0].GetString()!;
    }
    // The snapshot id of the source with this UniqueId, or null when the snapshot does not list it.
    static Guid? SnapshotId(string response, string uid)
    {
        using var parsed = JsonDocument.Parse(response);
        foreach (var source in parsed.RootElement.GetProperty("readback").GetProperty("sources").EnumerateArray())
            if (source.GetProperty("unique_id").GetString() == uid) return Guid.Parse(source.GetProperty("id").GetString()!);
        return null;
    }
    static double[] SnapshotGeometry(string response, string uid)
    {
        using var parsed = JsonDocument.Parse(response);
        var readback = parsed.RootElement.GetProperty("readback");
        var id = readback.GetProperty("sources").EnumerateArray().Single(source => source.GetProperty("unique_id").GetString() == uid).GetProperty("id").GetString();
        return readback.GetProperty("elements").EnumerateArray().Single(element => element.GetProperty("Id").GetString() == id)
            .GetProperty("GeometryMetres").EnumerateArray().Select(value => value.GetDouble()).ToArray();
    }

    // The Core project the add-in last pushed, rebuilt from the first snapshot: Core ids of its own (c0000000-0000-4000-8e2e-<index>), the
    // same kinds, geometry and dependencies (renamed), intent provenance; the map Core id -> UniqueId and the level names
    // query.levels reports.
    static (Project Project, SortedDictionary<Guid, string> Map, SortedDictionary<Guid, string> Levels) CoreFrom(string response, string levelsResponse)
    {
        using var levelRows = JsonDocument.Parse(levelsResponse);
        var levelName = levelRows.RootElement.GetProperty("readback").GetProperty("levels").EnumerateArray()
            .ToDictionary(row => row.GetProperty("unique_id").GetString()!, row => row.GetProperty("name").GetString()!);
        using var parsed = JsonDocument.Parse(response);
        var readback = parsed.RootElement.GetProperty("readback");
        var uidOf = readback.GetProperty("sources").EnumerateArray().ToDictionary(source => source.GetProperty("id").GetString()!, source => source.GetProperty("unique_id").GetString()!);
        var snapshotElements = readback.GetProperty("elements").EnumerateArray().ToArray();
        var coreOf = new Dictionary<string, Guid>();
        for (var i = 0; i < snapshotElements.Length; i++)
            coreOf[snapshotElements[i].GetProperty("Id").GetString()!] = Guid.Parse("c0000000-0000-4000-8e2e-" + (i + 1).ToString("D12", CultureInfo.InvariantCulture));
        var map = new SortedDictionary<Guid, string>();
        var levels = new SortedDictionary<Guid, string>();
        var elements = new List<Element>();
        foreach (var element in snapshotElements)
        {
            var snapshotId = element.GetProperty("Id").GetString()!;
            var id = coreOf[snapshotId];
            var kind = element.GetProperty("Kind").GetString()!;
            elements.Add(new Element(id, kind, element.GetProperty("GeometryMetres").EnumerateArray().Select(value => value.GetDouble()).ToArray(),
                element.GetProperty("Dependencies").EnumerateArray().Select(dependency => coreOf[dependency.GetString()!]).ToArray(), new Provenance("intent", "approved")));
            map[id] = uidOf[snapshotId];
            if (kind == "level") levels[id] = levelName[uidOf[snapshotId]];
        }
        return (new Project(ProjectId, 1, elements.ToArray()), map, levels);
    }

    // The mirror request; with a journal, it carries the journal.read reply and options.journal_after_sequence.
    static string MirrorJson(string documentPath, int revision, SortedDictionary<Guid, string> map, SortedDictionary<Guid, string> levels, string? journal = null, long journalAfter = 0)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("document");
            writer.WriteString("path", documentPath);
            writer.WriteString("unique_id", DocumentUid);
            writer.WriteEndObject();
            writer.WriteString("core_project_id", ProjectId.ToString("D"));
            writer.WriteNumber("core_revision", revision);
            writer.WriteStartObject("map");
            foreach (var (id, uid) in map) writer.WriteString(id.ToString("D"), uid);
            writer.WriteEndObject();
            writer.WriteStartObject("levels");
            foreach (var (id, name) in levels) writer.WriteString(id.ToString("D"), name);
            writer.WriteEndObject();
            writer.WriteStartArray("wall_types");
            writer.WriteStartObject();
            writer.WriteNumber("thickness_metres", 0.2);
            writer.WriteString("type", WallType);
            writer.WriteEndObject();
            writer.WriteEndArray();
            if (journal is not null)
            {
                writer.WritePropertyName("journal");
                writer.WriteRawValue(journal);
                writer.WriteStartObject("options");
                writer.WriteNumber("journal_after_sequence", journalAfter);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
