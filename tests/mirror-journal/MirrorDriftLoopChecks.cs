using Bim.Core;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
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
//   5. The fake add-in serves exactly the nine expected requests (operation ids in order, one journal.read wire line)
//      and exits 0 by itself. Its journal.read without the external-edit option is the pre-P2-18 reply: no entries,
//      sequences 0 (after_sequence ignored), every CLI write counted in skipped_add_in.
//   6. Deterministic: a second complete run (new fake add-in, new store) produces byte-identical snapshot readbacks,
//      files and outputs.
// P2-16 / P2-18 (journal; update, create and delete adopts). The mirror request carries the journal.read reply the fake
// add-in answers through its pipe (peek, complete, attached) naming the external edits; options.journal_after_sequence
// is the commit point. The fake is started with -ExternalEdits naming the three edit operation ids, so it records them
// as external journal entries (as Revit's DocumentChanged would for edits made in Revit) instead of add-in writes.
// Oracles:
//   1. Baseline as above, with an empty journal: no drift.
//   2. Three external edits through the CLI (element.move of wall B, wall.create of wall C, element.delete of wall A) are
//      reported as exactly three findings, each external_edit with its journal evidence: B changed (journal:1:external),
//      C extra (journal:2:external), A missing (journal:3:external:deleted_id_match); evidence complete; every proposal
//      recommends adopt (update, create, delete). Core's MirrorDrift.ParseJournal reads the fake's reply as three
//      external entries, and the reply equals, member for member with the snapshot diff included (P2-20), the production
//      serializer (RevitJournalQuery.RawRead over a Core MirrorJournal holding the same changes). Negative: the same
//      edits with the fake's reply after sequence 3 (no entries) give B and A unknown and C out of scope (no
//      external_edit); the P2-14 run (no journal) asserts its finding unknown.
//   3. mirror-drift-select {adopt x3} writes three operations, one map add (C) and one map removal (A); mirror-drift-adopt
//      commits revision 2: B at the Revit geometry, C created under its snapshot id (Revit geometry, Level 1 dependency,
//      adopt provenance), A gone, the levels untouched; replay prints replayed; the revision-1 mirror is refused stale.
//   4. A fresh query.snapshot against the mirror at revision 2 (map updated from the selection's map_adds/map_removals,
//      and a fresh peek reply after sequence 3 with no entries) reports no drift at all, evidence complete, no notes.
//   5. The fake's journal.read pages (max_entries, has_more, next_after_sequence), moves its baseline only without peek
//      (baseline_sequence, baseline_existed) and refuses an after_sequence past its last sequence like JournalRecorder.
//   6. The fake add-in serves exactly the expected requests (journal.read wire lines included) and exits 0; two runs are
//      byte-identical (journal replies included).
// The CLI has no journal verb: the test sends the journal.read wire line ({"DocumentPath","Operation","Journal"}) to the
// fake's pipe itself. The deletion entry carries the element id Revit's UniqueId convention puts in the hex suffix.
// Bounded: 5 minutes per run, 60 s per process, the fake add-in gives up after the run budget. Needs pwsh (BIM_PWSH, set by
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
    sealed record Loop(string Snapshot0, string Snapshot1, string Drift1, string Selection, string Adopt, string ProjectJson, string Snapshot2, string Drift2, string Journal = "");

    // The fake add-in serves exactly these requests, in this order, and then exits by itself.
    const string JournalWire = "journal.read";
    static readonly string[] ExpectedRequests = { "p2-14-levels", "p2-14-wall-types", "p2-14-wall-a", "p2-14-wall-b", "p2-14-snapshot-0", "p2-14-move-b", "p2-14-snapshot-1", JournalWire, "p2-14-snapshot-2" };
    // The three operations the fake add-in records as external edits (its -ExternalEdits option).
    static readonly string[] ExternalEdits = { "p2-16-move-b", "p2-16-wall-c", "p2-16-delete-a" };
    static readonly string[] JournalRequests = { "p2-16-levels", "p2-16-wall-types", "p2-16-wall-a", "p2-16-wall-b", "p2-16-snapshot-0", JournalWire, "p2-16-move-b", "p2-16-wall-c", "p2-16-delete-a",
        "p2-16-snapshot-1", JournalWire, JournalWire, "p2-16-snapshot-2", JournalWire, JournalWire, JournalWire, JournalWire, JournalWire, JournalWire, JournalWire };
    // Bounds: the whole run, each CLI or pwsh process, and the fake add-in's own lifetime.
    static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(5);
    const int CallTimeoutMs = 60000;
    static readonly int FakeTimeoutSeconds = (int)RunBudget.TotalSeconds;

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
        FakeOptionChecks(root, pwsh);
        FakeDiffChecks(root, pwsh);
        FakeBaselineChecks(root, pwsh);
        FakeFailureChecks(root, pwsh);
        FakeRetentionChecks(root, pwsh);
        RunFailureLoop(root, dotnet, pwsh, cliDll, runDirectory);
        FailureOptionChecks(root, pwsh);
        RunFailureGateLoop(root, dotnet, pwsh, cliDll, runDirectory);
        RunUnmatchedFailureLoops(root, dotnet, pwsh, cliDll, runDirectory);
        RunDroppedFailureLoop(root, dotnet, pwsh, cliDll, runDirectory);
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
        Check(first.Journal == second.Journal, scenario + "deterministic: the fake add-in's journal.read replies are byte-identical in both runs");
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
    const string UnixTemp = "/tmp";
    // .NET names a pipe's Unix socket <temp>/CoreFxPipe_<name>; with TMPDIR=UnixTemp both sides agree on it.
    const string UnixPipePrefix = UnixTemp + "/CoreFxPipe_";
    static void Unix(ProcessStartInfo start) { if (!OperatingSystem.IsWindows()) start.Environment["TMPDIR"] = UnixTemp; }

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

        public Harness(string root, string dotnet, string pwsh, string cliDll, string runDirectory, string documentName, int maxRequests, string? externalEdits = null, string? recorderFailures = null, string? dropReplyCommand = null, bool noColor = false)
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
            // Test-only: the operation ids the fake records as external journal entries (comma-separated).
            if (externalEdits is not null) { serverStart.ArgumentList.Add("-ExternalEdits"); serverStart.ArgumentList.Add(externalEdits); }
            // Test-only (P2-24): "<operation_id>:<count>,..." recorder failures the fake injects after those commands.
            if (recorderFailures is not null) { serverStart.ArgumentList.Add("-RecorderFailures"); serverStart.ArgumentList.Add(recorderFailures); }
            // Test-only (P4-14, P2-28): a write of this command runs, then its connection is closed without a reply.
            if (dropReplyCommand is not null) { serverStart.ArgumentList.Add("-DropReplyCommand"); serverStart.ArgumentList.Add(dropReplyCommand); }
            Unix(serverStart);
            // P2-28: plain error text (no ANSI colour) for runs that read the fake's error record.
            if (noColor) serverStart.Environment["NO_COLOR"] = "1";
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

        // The journal.read wire line (the add-in Request record, as scripts/owner-catalogue-steps.ps1 New-CatalogueWireLine
        // writes it) with the Journal options, sent straight to the fake's pipe; returns the one reply line.
        public string Journal(long? after, bool peek = true, int? maxEntries = null)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("DocumentPath", Document);
                writer.WriteString("Operation", JournalWire);
                writer.WriteNull("UniqueId");
                writer.WriteStartArray("Metres"); writer.WriteEndArray();
                writer.WriteStartObject("Journal");
                writer.WriteBoolean("peek", peek);
                if (after is { } a) writer.WriteNumber("after_sequence", a);
                if (maxEntries is { } m) writer.WriteNumber("max_entries", m);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Raw(Utf8.GetString(buffer.ToArray()));
        }

        // Any wire line as it is; a transport failure (no listener, broken pipe, timeout) answers a "<wire failed: ...>"
        // marker instead of throwing, so the check that reads the reply fails by name.
        public string Raw(string line)
        {
            Write("journal-read", line);
            string reply;
            try { reply = Wire(line); }
            catch (Exception error) when (error is IOException or SocketException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
            { reply = "<wire failed: " + error.GetType().Name + ": " + error.Message + ">"; }
            Write("journal-reply", reply);
            return reply;
        }

        // One line out, one line back. Windows: the named pipe through Core's transport. Elsewhere .NET pipes are Unix
        // sockets named <temp>/CoreFxPipe_<name>; the fake listens under TMPDIR=UnixTemp (Unix above), so connect there.
        string Wire(string line)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Max(1, Budget()));
            if (OperatingSystem.IsWindows()) return RevitCommandService.PipeTransport(pipe, line, wait) ?? "";
            var endpoint = new UnixDomainSocketEndPoint(UnixPipePrefix + pipe);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { socket.Connect(endpoint); }
                catch (SocketException) when (DateTime.UtcNow < until) { socket.Dispose(); Thread.Sleep(50); continue; }
                using var stream = new NetworkStream(socket, true) { ReadTimeout = (int)wait.TotalMilliseconds };
                stream.Write(Utf8.GetBytes(line + "\n"));
                using var reader = new StreamReader(stream, Utf8, false, 4096, leaveOpen: true);
                return reader.ReadLine() ?? "";
            }
        }

        // The fake add-in exits 0 by itself after its last request; the detail says how it ended otherwise.
        public (bool Ok, string Detail) Finish()
        {
            var exited = server.WaitForExit(30000);
            if (exited) server.WaitForExit();
            return (exited && server.ExitCode == 0, exited ? "exit " + server.ExitCode + " " + serverErr.Result.ReplaceLineEndings(" ").Trim() : "still running");
        }

        // P2-26: how the fake add-in ended, for runs that expect a non-zero exit: exited, exit code, stderr.
        public (bool Exited, int Exit, string Error) End()
        {
            var exited = server.WaitForExit(30000);
            if (exited) server.WaitForExit();
            return (exited, exited ? server.ExitCode : -1, exited ? serverErr.Result.ReplaceLineEndings(" ").Trim() : "still running");
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
        // P2-18 default: without -ExternalEdits the fake's journal.read is the pre-P2-18 reply, byte for byte up to the
        // diff: no entries, every sequence 0 (the after_sequence asked for is ignored), the three CLI writes skipped as add-in.
        var defaultJournal = h.Journal(5);
        var defaultExpected = "{\"status\":\"succeeded\",\"verification\":\"unverified\",\"attached\":true,\"dropped\":0,\"skipped_add_in\":3,\"failures\":0,\"unidentified\":0,"
            + "\"report\":{\"journal_version\":1,\"document_unique_id\":\"" + DocumentUid + "\",\"document_path\":" + JsonSerializer.Serialize(document, Relaxed)
            + ",\"baseline_sequence\":0,\"current_sequence\":0,\"after_sequence\":0,\"baseline_existed\":false,\"peek\":true,\"journal_complete\":true,\"failures_since_baseline\":0,"
            + "\"has_more\":false,\"next_after_sequence\":0,\"entries\":[],\"diff\":{";
        Check(defaultJournal.StartsWith(defaultExpected, StringComparison.Ordinal), "without -ExternalEdits the fake's journal.read reply is unchanged: no entries, sequences 0, skipped_add_in 3", Head(defaultJournal));

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
            var summary1 = reportElement.GetProperty("summary");
            Check(findings.Length == 1 && findings[0].GetProperty("classification").GetString() == MirrorDriftClasses.Unknown
                && summary1.GetProperty("unknown").GetInt32() == 1 && summary1.GetProperty("external_edit").GetInt32() == 0,
                "without a journal the finding is unknown (no external_edit)", summary1.GetRawText());
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
        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Journal.rvt", JournalRequests.Length, string.Join(",", ExternalEdits));
        var replies = new StringBuilder();
        string Journal(long? after, bool peek = true, int? maxEntries = null) { var reply = h.Journal(after, peek, maxEntries); replies.Append(reply).Append('\n'); return reply; }
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
        // The fake's journal before any external edit: peek after sequence 0, no entries.
        var journal0 = Journal(0);
        var read0 = MirrorDrift.ParseJournal(journal0);
        Check(read0.Entries.Count == 0 && read0.Complete && read0.Peek && read0.Attached && read0.AfterSequence == 0 && read0.CurrentSequence == 0,
            "the fake's journal.read before the edits has no entries (the CLI's own writes are add-in), current_sequence 0", Head(journal0));
        var baseline = h.Cli("mirror-drift", store, h.Write("snapshot-0", snapshot0.Out),
            h.Write("mirror-baseline", MirrorJson(document, 1, map, levelNames, journal0, 0)), h.NewPath("drift-baseline"));
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

        // ---- The journal the fake add-in recorded for those edits, read through its pipe (peek, after sequence 0) ----
        // Revit's UniqueId convention: the text after the last '-' is the element id in hex.
        if (!long.TryParse(uidA[(uidA.LastIndexOf('-') + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var deletedA) || deletedA <= 0)
        { Check(false, "wall A's UniqueId ends with a hex element id", uidA); return null; }
        var journal = Journal(0);
        // Core's journal reader reads the fake's reply as the recorder's format: a change there shows up here.
        var read = MirrorDrift.ParseJournal(journal);
        var entries = read.Entries;
        Check(entries.Count == 3 && entries.All(entry => entry.Origin == MirrorJournalOrigins.External && entry.DocumentUid == DocumentUid)
            && entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 1, 2, 3 })
            && entries[0].ModifiedUids.SequenceEqual(new[] { uidB }) && entries[0].AddedUids.Count == 0 && entries[0].DeletedIds.Count == 0
            && entries[1].AddedUids.SequenceEqual(new[] { uidC }) && entries[1].ModifiedUids.Count == 0 && entries[1].DeletedIds.Count == 0
            && entries[2].DeletedIds.SequenceEqual(new[] { deletedA }) && entries[2].AddedUids.Count == 0 && entries[2].ModifiedUids.Count == 0
            && read.Complete && read.Peek && read.Attached && !read.HasMore && read.AfterSequence == 0 && read.CurrentSequence == 3,
            "MirrorDrift.ParseJournal reads the fake's journal.read reply as three external entries (B modified, C added, A's id deleted), complete, peek, attached", Head(journal));
        Check(entries.Count == 3 && entries.Select(entry => string.Join("|", entry.TransactionNames)).SequenceEqual(new[] { "Move", "Wall", "Delete" })
            && entries.All(entry => entry.Operation == "TransactionCommitted" && string.Equals(entry.DocumentPath, document, StringComparison.OrdinalIgnoreCase) && !entry.Truncated),
            "the fake's entries are committed transactions Move, Wall, Delete of this document path", Head(journal));
        // The production serializer over the same changes (two add-in walls skipped, three external edits) gives the same
        // reply, member for member, the snapshot diff included (P2-20: the fake's diff is MirrorSnapshotDiffer's).
        var production = ProductionJournal(document, snapshot1.Out, uidB, uidC, deletedA);
        Check(Uniform(journal) == Uniform(production), "the fake's journal.read reply has the production format (RevitJournalQuery.RawRead), report.diff included",
            Uniform(journal) + " vs " + Uniform(production));
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

        // ---- Negative: the same edits with a journal that has nothing after sequence 3 give no external_edit ----
        // Without evidence, map_only scope leaves the unowned wall C out of scope; B and A are unknown.
        var noNews = Journal(3);
        var readNoNews = MirrorDrift.ParseJournal(noNews);
        Check(readNoNews.Entries.Count == 0 && readNoNews.AfterSequence == 3 && readNoNews.CurrentSequence == 3 && readNoNews.Complete && readNoNews.Peek,
            "the fake's journal.read after sequence 3 has no entries, current_sequence 3", Head(noNews));
        var barePath = h.NewPath("drift-no-news");
        var bare = h.Cli("mirror-drift", store, snapshot1Path, h.Write("mirror-r1-no-news", MirrorJson(document, 1, map, levelNames, noNews, 3)), barePath);
        if (bare.Exit != 0 || !File.Exists(barePath)) { Check(false, "mirror-drift with an empty journal after sequence 3 succeeds", R(bare)); return null; }
        using (var parsed = JsonDocument.Parse(File.ReadAllText(barePath, Utf8)))
        {
            var reportElement = parsed.RootElement.GetProperty("report");
            var summary = reportElement.GetProperty("summary");
            int S(string name) => summary.GetProperty(name).GetInt32();
            var classes = reportElement.GetProperty("findings").EnumerateArray()
                .Select(finding => finding.GetProperty("unique_id").GetString() + ":" + finding.GetProperty("kind").GetString() + ":" + finding.GetProperty("classification").GetString()).Order(StringComparer.Ordinal).ToArray();
            var outOfScope = reportElement.GetProperty("out_of_scope").EnumerateArray().Select(entry => entry.GetProperty("unique_id").GetString()).ToArray();
            Check(classes.SequenceEqual(new[] { uidA + ":missing:unknown", uidB + ":changed:unknown" }.Order(StringComparer.Ordinal)) && outOfScope.SequenceEqual(new[] { uidC })
                && S("external_edit") == 0 && S("unknown") == 2 && S("out_of_scope") == 1 && !reportElement.GetProperty("evidence").GetProperty("incomplete").GetBoolean(),
                "negative: with no journal entries after the commit point B and A are unknown and C is out of scope (no external_edit)", string.Join(",", classes) + " | " + summary.GetRawText());
        }

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
        Check(head.Revision == 2 && head.Elements.Length == project.Elements.Length, "the store head is revision 2 and keeps the element count (one wall deleted, one created)", head.Elements.Length.ToString(CultureInfo.InvariantCulture));
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
        // The next journal.read (peek, after sequence 3) has no entries: nothing happened in Revit since the adopt.
        var journal2 = Journal(3);
        Check(MirrorDrift.ParseJournal(journal2) is { Entries.Count: 0, AfterSequence: 3, CurrentSequence: 3, Complete: true } && WithoutDiff(journal2) == WithoutDiff(noNews),
            "the fake's journal.read after the adopt (peek, after sequence 3) has no entries: nothing happened in Revit since", Head(journal2));
        var mirror2 = h.Write("mirror-r2", MirrorJson(document, 2, nextMap, levelNames, journal2, 3));
        var drift2Path = h.NewPath("drift-2");
        var drift2 = h.Cli("mirror-drift", store, h.Write("snapshot-2", snapshot2.Out), mirror2, drift2Path);
        if (drift2.Exit != 0 || !File.Exists(drift2Path)) { Check(false, "the second mirror-drift succeeds", R(drift2)); return null; }
        var drift2Text = File.ReadAllText(drift2Path, Utf8);
        using (var parsed = JsonDocument.Parse(drift2Text))
        {
            var reportElement = parsed.RootElement.GetProperty("report");
            var summary = reportElement.GetProperty("summary");
            Check(summary.GetProperty("drift").GetInt32() == 0 && reportElement.GetProperty("findings").GetArrayLength() == 0 && summary.GetProperty("unchanged").GetInt32() == head.Elements.Length
                && summary.GetProperty("out_of_scope").GetInt32() == 0 && summary.GetProperty("not_comparable").GetInt32() == 0
                && parsed.RootElement.GetProperty("resolution").GetProperty("proposals").GetArrayLength() == 0 && parsed.RootElement.GetProperty("core_store").GetProperty("revision").GetInt32() == 2,
                "the second mirror-drift reports no drift: every head element (B, C, levels) unchanged, no proposals, store at revision 2", summary.GetRawText());
            var evidence = reportElement.GetProperty("evidence");
            Check(!evidence.GetProperty("incomplete").GetBoolean() && evidence.GetProperty("notes").GetArrayLength() == 0, "the second pass journal evidence is complete with no notes", evidence.GetRawText());
        }
        Check(Number(drift2.Out, "drift") == 0 && Number(drift2.Out, "proposals") == 0, "the second mirror-drift prints drift 0 and proposals 0", R(drift2));
        Check(uidA != uidB && uidC != uidA && uidC != uidB, "three distinct walls were created");

        // ---- The fake's journal.read options: paging, baseline moves only without peek, after_sequence bound ----
        var page1Reply = Journal(0, maxEntries: 2);
        var page1 = MirrorDrift.ParseJournal(page1Reply);
        var page2 = MirrorDrift.ParseJournal(Journal(2, maxEntries: 2));
        Check(page1.Entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 1, 2 }) && page1.HasMore && page1.Complete
            && page2.Entries.Select(entry => entry.Sequence).SequenceEqual(new long[] { 3 }) && !page2.HasMore && page2.AfterSequence == 2,
            "max_entries 2 pages the journal: sequences 1-2 with has_more, then 3 from after_sequence 2");
        Check(Member(page1Reply, "next_after_sequence") == 2, "a capped reply's next_after_sequence is its last entry's sequence");
        var consume = Journal(null, peek: false);
        Check(MirrorDrift.ParseJournal(consume) is { Entries.Count: 3, Peek: false, AfterSequence: 0 } && Member(consume, "baseline_sequence") == 0 && Flag(consume, "baseline_existed") == false,
            "a read without peek and without after_sequence starts at the (absent) baseline: all three entries, baseline_existed false", Head(consume));
        var after = Journal(null, peek: false);
        Check(MirrorDrift.ParseJournal(after) is { Entries.Count: 0, AfterSequence: 3, CurrentSequence: 3 } && Member(after, "baseline_sequence") == 3 && Flag(after, "baseline_existed") == true,
            "the next read without peek starts at the moved baseline (sequence 3): no entries, baseline_existed true", Head(after));
        // Duplicate option names are refused like MirrorJournalReadOptions.Parse, and the fake keeps serving (next read).
        var duplicate = h.Raw("{\"DocumentPath\":" + JsonSerializer.Serialize(document) + ",\"Operation\":\"journal.read\",\"UniqueId\":null,\"Metres\":[],\"Journal\":{\"peek\":true,\"peek\":true}}");
        replies.Append(duplicate).Append('\n');
        Check(Text(duplicate, "status") == "failed" && Text(duplicate, "code") == "journal_options_invalid duplicate peek", "a Journal with a duplicate member answers journal_options_invalid duplicate peek", duplicate);
        var past = Journal(4);
        Check(Text(past, "status") == "failed" && Text(past, "code") == "journal_after_sequence_invalid", "after_sequence past the journal's last sequence is refused journal_after_sequence_invalid like JournalRecorder", past);

        // ---- The fake add-in served exactly the expected requests and exited by itself ----
        var (finished, how) = h.Finish();
        Check(finished, "the fake add-in exits 0 after exactly " + JournalRequests.Length + " requests", how);
        var received = h.Received();
        Check(received.SequenceEqual(JournalRequests), "the fake add-in received the expected operation ids in order", string.Join(",", received));
        // A journal read with no fake listening answers the wire-failure marker within the 5 s connect bound, no throw.
        var clock = Stopwatch.StartNew();
        var gone = h.Journal(0);
        clock.Stop();
        Check(gone.StartsWith("<wire failed: ", StringComparison.Ordinal) && clock.Elapsed < TimeSpan.FromSeconds(6.5),
            "a journal read after the fake add-in exited answers <wire failed: ...> within about 6 s", gone + " after " + clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s");
        return new Loop(Readback(snapshot0.Out), Readback(snapshot1.Out), drift1Text, selectionText, adopt.Out, projectJson, Readback(snapshot2.Out), drift2Text, replies.ToString());
    }

    // P2-18 review: the fake executor in process (dot-sourced into one pwsh), for branches the pipe loop does not reach:
    // -ExternalEdits with no ids is off (an after_sequence is ignored, as without the option); option names are exact
    // case, a non-object Journal and duplicate names are refused with MirrorJournalReadOptions.Parse's codes (no throw);
    // a dry run of an external id records nothing, a replay records nothing more, and element.set_parameter under an
    // external id answers fake_external_unsupported.
    const string OptionScript = """
        param([string]$Executor, [string]$DocumentUid)
        $ErrorActionPreference = 'Stop'
        . $Executor
        $document = '/fake/Drift-Options.rvt'
        function Line([string]$Journal) { '{"DocumentPath":"' + $document + '","Operation":"journal.read","UniqueId":null,"Metres":[],"Journal":' + $Journal + '}' }
        function Env([string]$Id, [string]$Command, [string]$Arguments) { '{"protocol_version":1,"operation_id":"' + $Id + '","document":{"path":"' + $document + '","unique_id":"' + $DocumentUid + '"},"command":"' + $Command + '","args":' + $Arguments + '}' }
        function Wire($State, [string]$Line) { try { Invoke-FakeRevitWire $State $Line } catch { '<threw: ' + $_.Exception.Message + '>' } }
        function Member([string]$Reply, [string]$Name) { try { $value = ($Reply | ConvertFrom-Json).$Name; if ($null -eq $value) { '' } else { [string]$value } } catch { $Reply } }
        $r = [ordered]@{}
        foreach ($case in @(@{ Key = 'empty_array'; Ids = [string[]]@() }, @{ Key = 'empty_string'; Ids = [string[]]@('') }, @{ Key = 'blank'; Ids = [string[]]@(' ', '') })) {
            $s = New-FakeRevitState $DocumentUid @{} $case.Ids
            $reply = Wire $s (Line '{"peek":true,"after_sequence":5}')
            $r[$case.Key] = (Member $reply 'status') + '/' + $(try { [string]($reply | ConvertFrom-Json).report.after_sequence } catch { $reply }) + '/' + [string]($null -eq $s.ExternalEdits)
        }
        $s = New-FakeRevitState $DocumentUid @{} @('ext-move', 'ext-delete', 'ext-param')
        $r.case_name = Member (Wire $s (Line '{"Peek":true}')) 'code'
        $r.non_object = Member (Wire $s (Line '5')) 'code'
        $r.duplicate = Member (Wire $s (Line '{"peek":true,"peek":false}')) 'code'
        $r.after_refusals = Member (Wire $s (Line '{"peek":true}')) 'status'
        $wall = Invoke-FakeRevitCommand $s (Env 'seed-wall' 'wall.create' '{"level":"L1","type":"T1","height_mm":3000,"curve":{"kind":"line","start":[0,0],"end":[1000,0]}}')
        $uid = [string]@(($wall | ConvertFrom-Json).created)[0]
        $dry = Invoke-FakeRevitCommand $s (Env 'ext-delete' 'element.delete' ('{"element_uids":["' + $uid + '"],"dry_run":true}'))
        $r.dry = (Member $dry 'status') + '/' + $s.Journal.Count
        $move = Env 'ext-move' 'element.move' ('{"element_uids":["' + $uid + '"],"vector_mm":[500,0,0]}')
        $first = Invoke-FakeRevitCommand $s $move
        $again = Invoke-FakeRevitCommand $s $move
        $r.replay = (Member $first 'status') + '/' + [string]($again | ConvertFrom-Json).receipt.replayed + '/' + $s.Journal.Count
        $param = Invoke-FakeRevitCommand $s (Env 'ext-param' 'element.set_parameter' ('{"element_uid":"' + $uid + '","parameter":{"name":"Comments"},"value":{"kind":"text","text":"x"}}'))
        $r.set_parameter = (Member $param 'code') + '/' + $s.Journal.Count + '/' + $s.SkippedAddIn
        $r | ConvertTo-Json -Compress
        """;

    static void FakeOptionChecks(string root, string pwsh)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "fake options: " + name, detail);
        var scratch = Path.Combine(Path.GetTempPath(), "bim-drift-options-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(scratch);
        Directory.CreateDirectory(scratch);
        var script = Path.Combine(scratch, "options.ps1");
        File.WriteAllText(script, OptionScript, Utf8);
        var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Utf8 };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Executor", Path.Combine(root, "tests", "integration", "fake-revit-command-executor.ps1"), "-DocumentUid", DocumentUid }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CallTimeoutMs)) { Kill(process); Check(false, "the in-process option script finishes", "timed out"); return; }
        process.WaitForExit();
        var output = stdout.Result.Trim();
        if (process.ExitCode != 0 || !output.StartsWith('{')) { Check(false, "the in-process option script runs", "exit " + process.ExitCode + " " + output + " " + stderr.Result.ReplaceLineEndings(" ")); return; }
        using var parsed = JsonDocument.Parse(output);
        string R(string name) => parsed.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "<missing>";
        foreach (var key in new[] { "empty_array", "empty_string", "blank" })
            Check(R(key) == "succeeded/0/True", "-ExternalEdits with no ids (" + key + ") is off: after_sequence 5 ignored as without the option", R(key));
        Check(R("case_name") == "journal_options_invalid Peek", "option names are exact case: Peek is refused journal_options_invalid Peek", R("case_name"));
        Check(R("non_object") == "journal_options_invalid options must be an object", "a non-object Journal (5) is refused journal_options_invalid options must be an object", R("non_object"));
        Check(R("duplicate") == "journal_options_invalid duplicate peek", "a duplicate option name is a typed refusal, not a throw", R("duplicate"));
        Check(R("after_refusals") == "succeeded", "the fake answers a valid read after the refusals", R("after_refusals"));
        Check(R("dry") == "succeeded/0", "a dry run under an external id records no journal entry", R("dry"));
        Check(R("replay") == "succeeded/True/1", "an external move and its replay leave exactly one journal entry (the replay is flagged replayed)", R("replay"));
        Check(R("set_parameter") == "fake_external_unsupported/1/1", "element.set_parameter under an external id answers fake_external_unsupported and records nothing", R("set_parameter"));
    }

    // P2-20: the fake executor in process, for the journal.read report.diff. With -ExternalEdits the fake's diff must be
    // MirrorSnapshotDiffer.Compare over the same before/after snapshots (its query.snapshot readbacks): a baseline read
    // (non-peek, no baseline: every element added), six external edits (move wall B 333.3333 mm, which also moves its
    // hosted door; create wall C; delete straight wall A and the curved, unmapped wall D; a 0.0004 mm nudge of wall E,
    // within the differ's 1e-3 mm tolerance, and a 0.002 mm nudge of wall F, beyond it), then a peek read, a non-peek read
    // and a read after the moved baseline (empty diff), all under de-DE as the current culture. Each reply is compared
    // whole with RevitJournalQuery.RawRead over a Core MirrorJournal and tracker holding the same changes. A
    // SnapshotUnstable run compares the diff of its two stored baselines (the unmapped entry's detail changes), and
    // hand-built snapshot pairs (diffCases) drive Get-FakeSnapshotDiff directly for the field branches the fake snapshot
    // cannot reach (mapped to unmapped, kind, kind case, geometry length, unnamed geometry, dependencies, reason, category,
    // UniqueId case, duplicate and blank UniqueIds, the tolerance edge), each against MirrorSnapshotDiffer over the same
    // JSON. The fake's tolerance equals MirrorSnapshotDiffer.ToleranceMetres. Without -ExternalEdits the replies are the
    // pre-P2-20 ones, byte for byte (legacy diff: snapshot-order adds, changed always empty).
    const string DiffScript = """
        param([string]$Executor, [string]$DocumentUid, [string]$Cases)
        $ErrorActionPreference = 'Stop'
        . $Executor
        # Culture-independent output: everything below runs with a comma decimal separator as the current culture.
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('de-DE')
        $document = '/fake/Drift-Diff.rvt'
        function Line([string]$Journal) { '{"DocumentPath":"' + $document + '","Operation":"journal.read","UniqueId":null,"Metres":[],"Journal":' + $Journal + '}' }
        function Env([string]$Id, [string]$Command, [string]$Arguments) { '{"protocol_version":1,"operation_id":"' + $Id + '","document":{"path":"' + $document + '","unique_id":"' + $DocumentUid + '"},"command":"' + $Command + '","args":' + $Arguments + '}' }
        function Straight($Ax, $Ay, $Bx, $By) { '{"kind":"line","start":[' + $Ax + ',' + $Ay + '],"end":[' + $Bx + ',' + $By + ']}' }
        function Wall([string]$Level, [string]$Curve) { '{"level":"' + $Level + '","type":"T1","height_mm":3000,"curve":' + $Curve + '}' }
        function Ok($State, [string]$Id, [string]$Command, [string]$Arguments) {
            $reply = Invoke-FakeRevitCommand $State (Env $Id $Command $Arguments)
            if (($reply | ConvertFrom-Json).status -ne 'succeeded') { throw "$Id ($Command) did not succeed: $reply" }
            return $reply
        }
        function Created([string]$Reply) { [string]@(($Reply | ConvertFrom-Json).created)[0] }
        function Level($State) { [string]@((Ok $State 'levels' 'query.levels' '{}' | ConvertFrom-Json).readback.levels)[0].unique_id }
        $r = [ordered]@{}
        $s = New-FakeRevitState $DocumentUid @{} @('x-move-b', 'x-wall-c', 'x-delete-a', 'x-delete-d', 'x-nudge-e', 'x-nudge-f')
        $level = Level $s
        $r.a = Created (Ok $s 'wall-a' 'wall.create' (Wall $level (Straight 0 0 5000 0)))
        $r.b = Created (Ok $s 'wall-b' 'wall.create' (Wall $level (Straight 5000 0 5000 4000)))
        $r.d = Created (Ok $s 'wall-d' 'wall.create' (Wall $level '{"kind":"arc","start":[0,-1000],"end":[1000,-1000],"radius_mm":600,"center":[500,-1000]}'))
        $r.e = Created (Ok $s 'wall-e' 'wall.create' (Wall $level (Straight 0 8000 3000 8000)))
        $r.f = Created (Ok $s 'wall-f' 'wall.create' (Wall $level (Straight 0 12000 3000 12000)))
        $r.door = Created (Ok $s 'door-b' 'opening.place' ('{"host_wall_uid":"' + $r.b + '","category":"door","symbol":"S1","position_along_mm":1000}'))
        $r.s0 = Ok $s 'snapshot-0' 'query.snapshot' '{}'
        $r.r0 = Invoke-FakeRevitWire $s (Line 'null')
        $null = Ok $s 'x-move-b' 'element.move' ('{"element_uids":["' + $r.b + '"],"vector_mm":[333.3333,0,0]}')
        $r.c = Created (Ok $s 'x-wall-c' 'wall.create' (Wall $level (Straight 0 4000 5500 4000)))
        $null = Ok $s 'x-delete-a' 'element.delete' ('{"element_uids":["' + $r.a + '"]}')
        $null = Ok $s 'x-delete-d' 'element.delete' ('{"element_uids":["' + $r.d + '"]}')
        $null = Ok $s 'x-nudge-e' 'element.move' ('{"element_uids":["' + $r.e + '"],"vector_mm":[0.0004,0,0]}')
        $null = Ok $s 'x-nudge-f' 'element.move' ('{"element_uids":["' + $r.f + '"],"vector_mm":[0.002,0,0]}')
        $r.s1 = Ok $s 'snapshot-1' 'query.snapshot' '{}'
        $r.r1 = Invoke-FakeRevitWire $s (Line '{"peek":true}')
        $r.r2 = Invoke-FakeRevitWire $s (Line 'null')
        $r.r3 = Invoke-FakeRevitWire $s (Line 'null')
        $t = New-FakeRevitState $DocumentUid
        $tLevel = Level $t
        $r.default_wall = Created (Ok $t 'default-wall' 'wall.create' (Wall $tLevel (Straight 0 0 5000 0)))
        $r.d0 = Invoke-FakeRevitWire $t (Line 'null')
        $null = Ok $t 'default-move' 'element.move' ('{"element_uids":["' + $r.default_wall + '"],"vector_mm":[500,0,0]}')
        $r.d1 = Invoke-FakeRevitWire $t (Line 'null')
        $null = Ok $t 'default-delete' 'element.delete' ('{"element_uids":["' + $r.default_wall + '"]}')
        $r.d2 = Invoke-FakeRevitWire $t (Line '{"peek":true}')
        $r.d3 = Invoke-FakeRevitWire $t (Line 'null')
        # SnapshotUnstable: every snapshot's unmapped entry 'unstable' has a new detail; the two stored baselines are the
        # snapshots the second non-peek read compared.
        $u = New-FakeRevitState $DocumentUid @{ SnapshotUnstable = $true } @('u-move')
        $uLevel = Level $u
        $r.u_wall = Created (Ok $u 'u-wall' 'wall.create' (Wall $uLevel (Straight 0 0 1000 0)))
        $null = Invoke-FakeRevitWire $u (Line 'null')
        $r.u_before = (Get-FakeJournalBaseline $u $DocumentUid $document).Snapshot | ConvertTo-Json -Depth 20 -Compress
        $null = Ok $u 'u-move' 'element.move' ('{"element_uids":["' + $r.u_wall + '"],"vector_mm":[0,250,0]}')
        $r.u_reply = Invoke-FakeRevitWire $u (Line 'null')
        $r.u_after = (Get-FakeJournalBaseline $u $DocumentUid $document).Snapshot | ConvertTo-Json -Depth 20 -Compress
        # Hand-built snapshot pairs straight through Get-FakeSnapshotDiff.
        # (PowerShell names ignore case: $caseDiffs, not a name that would overwrite the $Cases parameter.)
        $caseDiffs = [ordered]@{}
        foreach ($case in @(Get-Content -Raw -LiteralPath $Cases | ConvertFrom-Json -AsHashtable)) { $caseDiffs[[string]$case['name']] = ConvertTo-FakeResponseJson (Get-FakeSnapshotDiff $DocumentUid $case['before'] $case['after']) }
        $r.cases = $caseDiffs | ConvertTo-Json -Compress
        $r.tolerance = ([double]$script:FakeDiffToleranceMetres).ToString('R', [Globalization.CultureInfo]::InvariantCulture)
        $r.culture = [Globalization.CultureInfo]::CurrentCulture.Name + ' ' + (1.5).ToString()
        $r | ConvertTo-Json -Compress
        """;

    static void FakeDiffChecks(string root, string pwsh)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "fake diff: " + name, detail);
        var scratch = Path.Combine(Path.GetTempPath(), "bim-drift-diff-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(scratch);
        Directory.CreateDirectory(scratch);
        var script = Path.Combine(scratch, "diff.ps1");
        File.WriteAllText(script, DiffScript, Utf8);
        var casesPath = Path.Combine(scratch, "cases.json");
        var cases = DiffCases();
        File.WriteAllText(casesPath, "[" + string.Join(",", cases.Select(item => "{\"name\":\"" + item.Name + "\",\"before\":" + item.Before + ",\"after\":" + item.After + "}")) + "]", Utf8);
        var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Utf8 };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Executor", Path.Combine(root, "tests", "integration", "fake-revit-command-executor.ps1"), "-DocumentUid", DocumentUid, "-Cases", casesPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CallTimeoutMs)) { Kill(process); Check(false, "the in-process diff script finishes", "timed out"); return; }
        process.WaitForExit();
        var output = stdout.Result.Trim();
        if (process.ExitCode != 0 || !output.StartsWith('{')) { Check(false, "the in-process diff script runs", "exit " + process.ExitCode + " " + Head(output) + " " + Head(stderr.Result.ReplaceLineEndings(" "))); return; }
        using var parsed = JsonDocument.Parse(output);
        string R(string name) => parsed.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "<missing>";
        const string path = "/fake/Drift-Diff.rvt";
        string uidA = R("a"), uidB = R("b"), uidC = R("c"), uidD = R("d"), uidE = R("e"), uidF = R("f"), door = R("door");
        Check(R("culture") == "de-DE 1,5", "the diff script ran with de-DE as the current culture", R("culture"));
        Check(R("tolerance") == MirrorSnapshotDiffer.ToleranceMetres.ToString("R", CultureInfo.InvariantCulture) && MirrorSnapshotDiffer.ToleranceMetres == MirrorSnapshotMapper.ToleranceMm / 1000,
            "the fake's diff tolerance equals MirrorSnapshotDiffer.ToleranceMetres (MirrorSnapshotMapper.ToleranceMm in metres)", R("tolerance") + " vs " + MirrorSnapshotDiffer.ToleranceMetres.ToString("R", CultureInfo.InvariantCulture));
        static MirrorSnapshot Snap(string response) { using var reply = JsonDocument.Parse(response); return MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText()); }
        static long Native(string uid) => long.Parse(uid[(uid.LastIndexOf('-') + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var s0 = Snap(R("s0"));
        var s1 = Snap(R("s1"));
        // The production side of the same session: six add-in writes (skipped), the baseline read, six external commits.
        var tick = 0;
        var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
        var tracker = new MirrorJournalTracker(journal);
        void Record(string name, string[] added, string[] modified, long[] deleted, bool addIn = false) =>
            journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { name }, added, modified, deleted, addIn) { DocumentPath = path });
        foreach (var _ in Enumerable.Range(0, 6)) Record("BIM Agentic OS wall.create", new[] { "x" }, Array.Empty<string>(), Array.Empty<long>(), true);
        var p0 = RevitJournalQuery.RawRead(tracker, true, s0, path, new MirrorJournalReadOptions());
        Record("Move", Array.Empty<string>(), new[] { uidB }, Array.Empty<long>());
        Record("Wall", new[] { uidC }, Array.Empty<string>(), Array.Empty<long>());
        Record("Delete", Array.Empty<string>(), Array.Empty<string>(), new[] { Native(uidA) });
        Record("Delete", Array.Empty<string>(), Array.Empty<string>(), new[] { Native(uidD) });
        Record("Move", Array.Empty<string>(), new[] { uidE }, Array.Empty<long>());
        Record("Move", Array.Empty<string>(), new[] { uidF }, Array.Empty<long>());
        var p1 = RevitJournalQuery.RawRead(tracker, true, s1, path, new MirrorJournalReadOptions(null, true));
        var p2 = RevitJournalQuery.RawRead(tracker, true, s1, path, new MirrorJournalReadOptions());
        var p3 = RevitJournalQuery.RawRead(tracker, true, s1, path, new MirrorJournalReadOptions());

        // The production differ over the same snapshots holds what the scenario is about (so the comparisons are not vacuous).
        var expected = MirrorSnapshotDiffer.Compare(s0, s1);
        var changed = expected.Changed.Select(change => change.UniqueId + ":" + string.Join("|", change.Fields.Select(field => field.Field))).ToArray();
        Check(expected.Added.Select(item => item.UniqueId).SequenceEqual(new[] { uidC }) && expected.Removed.Select(item => item.UniqueId + ":" + item.Category).SequenceEqual(new[] { uidA + ":wall", uidD + ":wall" })
            && changed.SequenceEqual(new[] { uidB + ":ax|bx", door + ":ax|bx", uidF + ":ax|bx" }.Order(StringComparer.Ordinal))
            && expected.Changed.Single(change => change.UniqueId == uidB).Fields[0].After == (5333.3333 / 1000).ToString("R", CultureInfo.InvariantCulture),
            "MirrorSnapshotDiffer over the fake's snapshots: C added, A and the curved D removed, B (333.3333 mm) and its door moved (ax, bx), the 0.0004 mm nudge of E within tolerance, the 0.002 mm nudge of F beyond it (ax, bx)", expected.ToJson());
        string Diff(string reply)
        {
            try { using var document = JsonDocument.Parse(reply); return Uniform(document.RootElement.GetProperty("report").GetProperty("diff").GetRawText()); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return "<no report.diff> " + Head(reply); }
        }
        Check(Diff(R("r0")) == Uniform(MirrorSnapshotDiffer.Compare(null, s0).ToJson()), "the first non-peek read (no baseline): report.diff is MirrorSnapshotDiffer.Compare(null, snapshot 0), every element added in ordinal UniqueId order",
            Diff(R("r0")) + " vs " + MirrorSnapshotDiffer.Compare(null, s0).ToJson());
        Check(Diff(R("r1")) == Uniform(expected.ToJson()), "after the edits (peek): report.diff is MirrorSnapshotDiffer.Compare(snapshot 0, snapshot 1), moved/created/deleted ids and fields in production member order",
            Diff(R("r1")) + " vs " + expected.ToJson());
        Check(Diff(R("r2")) == Uniform(expected.ToJson()), "the peek read kept the baseline: the next non-peek read has the same report.diff", Diff(R("r2")) + " vs " + expected.ToJson());
        Check(Diff(R("r3")) == Uniform(MirrorSnapshotDiffer.Compare(s1, s1).ToJson()), "after the non-peek read the baseline is snapshot 1: report.diff is empty", Diff(R("r3")));
        foreach (var (name, reply, production) in new[] { ("r0", R("r0"), p0), ("r1", R("r1"), p1), ("r2", R("r2"), p2), ("r3", R("r3"), p3) })
            Check(Uniform(reply) == Uniform(production), "reply " + name + " equals RevitJournalQuery.RawRead over the same journal and snapshots, member for member (diff included)", Uniform(reply) + " vs " + Uniform(production));

        // Without -ExternalEdits: the pre-P2-20 reply, byte for byte (legacy diff: adds in snapshot order, changed always empty).
        const string level1 = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-00001001", level2 = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-00001002", wall = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-00001003";
        string Default(int skipped, bool existed, string added, bool peek = false, string removed = "") => "{\"status\":\"succeeded\",\"verification\":\"unverified\",\"attached\":true,\"dropped\":0,\"skipped_add_in\":" + skipped
            + ",\"failures\":0,\"unidentified\":0,\"report\":{\"journal_version\":1,\"document_unique_id\":\"" + DocumentUid + "\",\"document_path\":\"" + path + "\",\"baseline_sequence\":0,\"current_sequence\":0,\"after_sequence\":0,"
            + "\"baseline_existed\":" + (existed ? "true" : "false") + ",\"peek\":" + (peek ? "true" : "false") + ",\"journal_complete\":true,\"failures_since_baseline\":0,\"has_more\":false,\"next_after_sequence\":0,\"entries\":[],"
            + "\"diff\":{\"diff_version\":1,\"document_unique_id\":\"" + DocumentUid + "\",\"added\":[" + added + "],\"removed\":[" + removed + "],\"changed\":[]}}}";
        static string Item(string uid, string category) => "{\"unique_id\":\"" + uid + "\",\"category\":\"" + category + "\"}";
        Check(R("default_wall") == wall, "the default run's wall has the third fake UniqueId", R("default_wall"));
        Check(R("d0") == Default(1, false, Item(level1, "level") + "," + Item(level2, "level") + "," + Item(wall, "wall")), "without -ExternalEdits the first non-peek read is the pre-P2-20 reply byte for byte (every element added, changed empty)", R("d0"));
        Check(R("d1") == Default(2, true, ""), "without -ExternalEdits a read after an add-in move is the pre-P2-20 reply byte for byte (changed stays empty)", R("d1"));
        Check(R("d2") == Default(3, true, "", peek: true, removed: Item(wall, "wall")), "without -ExternalEdits a peek read after an add-in delete is the pre-P2-20 reply byte for byte (removed names the wall)", R("d2"));
        Check(R("d3") == Default(3, true, "", removed: Item(wall, "wall")), "without -ExternalEdits the peek kept the legacy baseline: the next non-peek read is the pre-P2-20 reply byte for byte", R("d3"));

        // SnapshotUnstable: the fake's diff between its two stored baselines is MirrorSnapshotDiffer's over them.
        try
        {
            var before = MirrorDrift.ParseSnapshot(R("u_before"));
            var after = MirrorDrift.ParseSnapshot(R("u_after"));
            var unstable = MirrorSnapshotDiffer.Compare(before, after);
            var fields = unstable.Changed.Select(change => change.UniqueId + ":" + string.Join("|", change.Fields.Select(field => field.Field))).ToArray();
            Check(fields.SequenceEqual(new[] { R("u_wall") + ":ay|by", "unstable:detail" }), "SnapshotUnstable: the differ sees the wall moved (ay, by) and the unmapped entry's detail changed", unstable.ToJson());
            Check(Diff(R("u_reply")) == Uniform(unstable.ToJson()), "SnapshotUnstable: report.diff is MirrorSnapshotDiffer.Compare over the two stored baselines (detail field)", Diff(R("u_reply")) + " vs " + unstable.ToJson());
        }
        catch (ArgumentException error) { Check(false, "SnapshotUnstable: the stored baselines parse as snapshots", error.Message + " | " + Head(R("u_before"))); }

        // Hand-built snapshot pairs: Get-FakeSnapshotDiff equals MirrorSnapshotDiffer over the same JSON.
        JsonDocument? caseReplies = null;
        try { caseReplies = JsonDocument.Parse(R("cases")); } catch (JsonException) { }
        using (caseReplies)
            foreach (var item in cases)
            {
                var fake = caseReplies is not null && caseReplies.RootElement.TryGetProperty(item.Name, out var value) && value.ValueKind == JsonValueKind.String ? Uniform(value.GetString()!) : "<missing> " + Head(R("cases"));
                string production;
                try { production = MirrorSnapshotDiffer.Compare(MirrorDrift.ParseSnapshot(item.Before), MirrorDrift.ParseSnapshot(item.After)).ToJson(); }
                catch (ArgumentException error) { production = "<differ refused: " + error.Message + ">"; }
                Check(production.Contains("\"changed\":[{", StringComparison.Ordinal) || production.Contains("\"added\":[{", StringComparison.Ordinal), "case " + item.Name + ": the differ reports a change (the case is not vacuous)", production);
                Check(fake == Uniform(production), "case " + item.Name + ": Get-FakeSnapshotDiff equals MirrorSnapshotDiffer.Compare over the same snapshots", fake + " vs " + production);
            }
    }

    // P2-22: the -ExternalEdits fake keeps one journal.read baseline per (document UniqueId, path), as
    // MirrorJournalTracker does (both compared ignoring case), so switching the fake's document never diffs against
    // another document's snapshot. One fake state, in process: document A is read (baseline), an external move under A,
    // a first read of document B (no baseline of its own: baseline_existed false, every element added, no entries), a
    // peek of A (still A's baseline and A's entry), an external move under B, non-peek reads of A and B (each its own
    // entry and baseline), a peek of A under an upper-case path (same key), then the fake's DocumentUid switched (a new
    // document at path A: no baseline), an external move under it, its second read, and back to the first UniqueId
    // (A's own baseline, not the other document's entry). Every reply equals RevitJournalQuery.RawRead over one Core
    // MirrorJournalTracker fed the same journal changes and snapshots, member for member (diff included).
    const string OtherDocumentUid = "5e1f0a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b-0000e2e2";

    const string BaselineScript = """
        param([string]$Executor, [string]$DocumentUid, [string]$OtherUid)
        $ErrorActionPreference = 'Stop'
        . $Executor
        $a = '/fake/Drift-Doc-A.rvt'; $b = '/fake/Drift-Doc-B.rvt'
        function Line([string]$Path, [string]$Journal) { '{"DocumentPath":"' + $Path + '","Operation":"journal.read","UniqueId":null,"Metres":[],"Journal":' + $Journal + '}' }
        function Env([string]$Path, [string]$Id, [string]$Command, [string]$Arguments) { '{"protocol_version":1,"operation_id":"' + $Id + '","document":{"path":"' + $Path + '","unique_id":"' + $DocumentUid + '"},"command":"' + $Command + '","args":' + $Arguments + '}' }
        function Ok($State, [string]$Path, [string]$Id, [string]$Command, [string]$Arguments) {
            $reply = Invoke-FakeRevitCommand $State (Env $Path $Id $Command $Arguments)
            if (($reply | ConvertFrom-Json).status -ne 'succeeded') { throw "$Id ($Command) did not succeed: $reply" }
            return $reply
        }
        function Wire($State, [string]$Line) { try { Invoke-FakeRevitWire $State $Line } catch { '<threw: ' + $_.Exception.Message + '>' } }
        function MoveWall($State, [string]$Path, [string]$Id, [string]$Uid, [string]$Vector) { $null = Ok $State $Path $Id 'element.move' ('{"element_uids":["' + $Uid + '"],"vector_mm":' + $Vector + '}') }
        $r = [ordered]@{}
        $s = New-FakeRevitState $DocumentUid @{} @('x-move-a', 'x-move-b', 'x-move-u', 'x-move-c')
        $level = [string]@((Ok $s $a 'levels' 'query.levels' '{}' | ConvertFrom-Json).readback.levels)[0].unique_id
        $r.wall = [string]@((Ok $s $a 'wall-1' 'wall.create' ('{"level":"' + $level + '","type":"T1","height_mm":3000,"curve":{"kind":"line","start":[0,0],"end":[5000,0]}}') | ConvertFrom-Json).created)[0]
        $r.s0 = Ok $s $a 'snapshot-0' 'query.snapshot' '{}'
        $r.a0 = Wire $s (Line $a 'null')
        MoveWall $s $a 'x-move-a' $r.wall '[250,0,0]'
        $r.s1 = Ok $s $b 'snapshot-1' 'query.snapshot' '{}'
        $r.b0 = Wire $s (Line $b 'null')
        $r.a1 = Wire $s (Line $a '{"peek":true}')
        MoveWall $s $b 'x-move-b' $r.wall '[0,400,0]'
        $r.s2 = Ok $s $a 'snapshot-2' 'query.snapshot' '{}'
        $r.a2 = Wire $s (Line $a 'null')
        $r.b1 = Wire $s (Line $b 'null')
        $r.a3 = Wire $s (Line $a.ToUpperInvariant() '{"peek":true}')
        # Test-only lever: the fake has no document.open, so the open document's UniqueId is switched on the state itself.
        $s.DocumentUid = $OtherUid
        $r.su0 = Ok $s $a 'snapshot-u0' 'query.snapshot' '{}'
        $r.u0 = Wire $s (Line $a 'null')
        MoveWall $s $a 'x-move-u' $r.wall '[100,0,0]'
        $r.su1 = Ok $s $a 'snapshot-u1' 'query.snapshot' '{}'
        $r.u1 = Wire $s (Line $a 'null')
        $s.DocumentUid = $DocumentUid
        $r.s3 = Ok $s $a 'snapshot-3' 'query.snapshot' '{}'
        $r.a4 = Wire $s (Line $a 'null')
        # The entry filter ignores the path's case: a peek from sequence 0 under the upper-case path lists A's entry 1.
        $r.a5 = Wire $s (Line $a.ToUpperInvariant() '{"peek":true,"after_sequence":0}')
        # The baseline key ignores the UniqueId's case: the upper-cased UniqueId is A's document (its baseline, its entries).
        $s.DocumentUid = $DocumentUid.ToUpperInvariant()
        MoveWall $s $a 'x-move-c' $r.wall '[0,-100,0]'
        $r.s4 = Ok $s $a 'snapshot-4' 'query.snapshot' '{}'
        $r.a6 = Wire $s (Line $a 'null')
        $r | ConvertTo-Json -Compress
        """;

    static void FakeBaselineChecks(string root, string pwsh)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "fake baseline: " + name, detail);
        var scratch = Path.Combine(Path.GetTempPath(), "bim-drift-baseline-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(scratch);
        Directory.CreateDirectory(scratch);
        var script = Path.Combine(scratch, "baseline.ps1");
        File.WriteAllText(script, BaselineScript, Utf8);
        var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Utf8 };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Executor", Path.Combine(root, "tests", "integration", "fake-revit-command-executor.ps1"), "-DocumentUid", DocumentUid, "-OtherUid", OtherDocumentUid }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CallTimeoutMs)) { Kill(process); Check(false, "the in-process baseline script finishes", "timed out"); return; }
        process.WaitForExit();
        var output = stdout.Result.Trim();
        if (process.ExitCode != 0 || !output.StartsWith('{')) { Check(false, "the in-process baseline script runs", "exit " + process.ExitCode + " " + Head(output) + " " + Head(stderr.Result.ReplaceLineEndings(" "))); return; }
        using var parsed = JsonDocument.Parse(output);
        string R(string name) => parsed.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "<missing>";
        const string a = "/fake/Drift-Doc-A.rvt", b = "/fake/Drift-Doc-B.rvt";
        var wall = R("wall");
        static MirrorSnapshot Snap(string response) { using var reply = JsonDocument.Parse(response); return MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText()); }
        MirrorSnapshot s0, s1, s2, su0, su1, s3, s4;
        try { (s0, s1, s2, su0, su1, s3, s4) = (Snap(R("s0")), Snap(R("s1")), Snap(R("s2")), Snap(R("su0")), Snap(R("su1")), Snap(R("s3")), Snap(R("s4"))); }
        catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, "the fake's query.snapshot readbacks parse as snapshots", error.Message + " | " + Head(output)); return; }
        Check(su0.DocumentUid == OtherDocumentUid && s3.DocumentUid == DocumentUid, "the snapshots after the UniqueId switch name the other document, and the first one again after switching back", su0.DocumentUid + " / " + s3.DocumentUid);

        // The production side: one Core journal and tracker, the same add-in write (skipped), external commits and reads.
        var tick = 0;
        var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
        var tracker = new MirrorJournalTracker(journal);
        void Record(string uid, string path, string name, string[] modified, bool addIn = false) =>
            journal.Record(new MirrorJournalChange(uid, "TransactionCommitted", new[] { name }, addIn ? new[] { "x" } : Array.Empty<string>(), modified, Array.Empty<long>(), addIn) { DocumentPath = path });
        string Read(MirrorSnapshot snapshot, string path, bool peek = false, long? after = null) => RevitJournalQuery.RawRead(tracker, true, snapshot, path, new MirrorJournalReadOptions(after, peek));
        Record(DocumentUid, a, "BIM Agentic OS wall.create", Array.Empty<string>(), addIn: true);
        var production = new Dictionary<string, string>();
        production["a0"] = Read(s0, a);
        Record(DocumentUid, a, "Move", new[] { wall });
        production["b0"] = Read(s1, b);
        production["a1"] = Read(s1, a, peek: true);
        Record(DocumentUid, b, "Move", new[] { wall });
        production["a2"] = Read(s2, a);
        production["b1"] = Read(s2, b);
        production["a3"] = Read(s2, a.ToUpperInvariant(), peek: true);
        production["u0"] = Read(su0, a);
        Record(OtherDocumentUid, a, "Move", new[] { wall });
        production["u1"] = Read(su1, a);
        production["a4"] = Read(s3, a);
        production["a5"] = Read(s3, a.ToUpperInvariant(), peek: true, after: 0);
        Record(DocumentUid.ToUpperInvariant(), a, "Move", new[] { wall });
        production["a6"] = Read(s4, a);

        // What each read is about, from the fake's reply: document, baseline_existed, baseline_sequence, entry sequences.
        string Shape(string reply)
        {
            try
            {
                using var document = JsonDocument.Parse(reply);
                var report = document.RootElement.GetProperty("report");
                return report.GetProperty("document_unique_id").GetString()![^4..] + " existed=" + report.GetProperty("baseline_existed").GetBoolean().ToString().ToLowerInvariant()
                    + " since=" + report.GetProperty("baseline_sequence").GetInt64().ToString(CultureInfo.InvariantCulture)
                    + " entries=[" + string.Join(",", report.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64().ToString(CultureInfo.InvariantCulture))) + "]";
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return "<no report> " + Head(reply); }
        }
        string Diff(string reply)
        {
            try { using var document = JsonDocument.Parse(reply); return Uniform(document.RootElement.GetProperty("report").GetProperty("diff").GetRawText()); }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return "<no report.diff> " + Head(reply); }
        }
        // The differ holds a change for the reads that compare two different states (the diff checks are not vacuous).
        Check(MirrorSnapshotDiffer.Compare(s0, s1).Changed.Count == 1 && MirrorSnapshotDiffer.Compare(s1, s2).Changed.Count == 1 && MirrorSnapshotDiffer.Compare(s0, s2).Changed.Count == 1
            && MirrorSnapshotDiffer.Compare(su0, su1).Changed.Count == 1 && MirrorSnapshotDiffer.Compare(s2, s3).Changed.Count == 1 && MirrorSnapshotDiffer.Compare(s3, s4).Changed.Count == 1, "MirrorSnapshotDiffer sees the wall moved between each pair of snapshots the reads compare",
            MirrorSnapshotDiffer.Compare(s0, s1).ToJson() + " | " + MirrorSnapshotDiffer.Compare(s2, s3).ToJson());
        var cases = new (string Name, string Shape, MirrorSnapshot? Before, MirrorSnapshot After, string What)[]
        {
            ("a0", "e2e1 existed=false since=0 entries=[]", null, s0, "the first read of document A: no baseline, every element added"),
            ("b0", "e2e1 existed=false since=0 entries=[]", null, s1, "the first read of document B after A's baseline and A's external move: B has no baseline of its own (not A's), every element added, A's entry not listed"),
            ("a1", "e2e1 existed=true since=0 entries=[1]", s0, s1, "a peek of A after B's first read: still A's own baseline (snapshot 0, sequence 0) and A's entry"),
            ("a2", "e2e1 existed=true since=0 entries=[1]", s0, s2, "a non-peek read of A after B's external move: A's baseline, only A's entry"),
            ("b1", "e2e1 existed=true since=1 entries=[2]", s1, s2, "the second read of B: B's own baseline (snapshot 1, sequence 1), only B's entry"),
            ("a3", "e2e1 existed=true since=2 entries=[]", s2, s2, "a peek of A under the upper-case path: the same baseline key (case ignored), empty diff"),
            ("u0", "e2e2 existed=false since=0 entries=[]", null, su0, "the first read after the fake's document UniqueId changed at path A: no baseline for that document, every element added"),
            ("u1", "e2e2 existed=true since=2 entries=[3]", su0, su1, "the second read of the other document: its own baseline and its own entry"),
            ("a4", "e2e1 existed=true since=2 entries=[]", s2, s3, "back to the first UniqueId at path A: A's own baseline (snapshot 2, sequence 2), the other document's entry not listed"),
            ("a5", "e2e1 existed=true since=3 entries=[1]", s3, s3, "a peek from sequence 0 under the upper-case path: the entry filter ignores the path's case and lists A's entry 1"),
            ("a6", "E2E1 existed=true since=3 entries=[4]", s3, s4, "a read with the fake's UniqueId upper-cased: the same document (case ignored), A's baseline (snapshot 3, sequence 3) and the entry recorded under that UniqueId"),
        };
        foreach (var (name, shape, before, after, what) in cases)
        {
            var reply = R(name);
            Check(Shape(reply) == shape, "reply " + name + ": " + what + " (" + shape + ")", Shape(reply));
            var expected = Uniform(MirrorSnapshotDiffer.Compare(before, after).ToJson());
            Check(Diff(reply) == expected, "reply " + name + ": report.diff is MirrorSnapshotDiffer.Compare over that document's own baseline", Diff(reply) + " vs " + expected);
            Check(Uniform(reply) == Uniform(production[name]), "reply " + name + " equals RevitJournalQuery.RawRead over one two-document Core tracker, member for member (diff included)", Uniform(reply) + " vs " + Uniform(production[name]));
        }
    }

    // P2-23: the -ExternalEdits fake counts recorder failures as MirrorJournal.RecordFailure does and reports them per
    // (document UniqueId, path) baseline as MirrorJournalTracker does: failures is the running total, failures_since_baseline
    // the total minus the count stored with that document's baseline (all of them when it has none), and journal_complete
    // false while that difference is above zero. The test-only lever Add-FakeRecorderFailure injects them (and refuses a
    // state without -ExternalEdits, whose replies stay the pre-P2-18 ones). One fake state, in process: A is read (no
    // failure), one failure, a peek of A and a first read of B (both count it), two reads of A (the first stores the count,
    // the second is complete), a peek of B, two more failures and an external move under A, a read of A under the
    // upper-case path (the same baseline), a peek of A from sequence 0, a read of B, then the fake's DocumentUid switched (no
    // baseline: every failure so far) and its peek after a read. Every reply equals RevitJournalQuery.RawRead over one Core
    // MirrorJournalTracker given the same journal changes, RecordFailure calls and snapshots, member for member.
    const string FailureScript = """
        param([string]$Executor, [string]$DocumentUid, [string]$OtherUid)
        $ErrorActionPreference = 'Stop'
        . $Executor
        $a = '/fake/Drift-Doc-A.rvt'; $b = '/fake/Drift-Doc-B.rvt'
        function Line([string]$Path, [string]$Journal) { '{"DocumentPath":"' + $Path + '","Operation":"journal.read","UniqueId":null,"Metres":[],"Journal":' + $Journal + '}' }
        function Env([string]$Path, [string]$Id, [string]$Command, [string]$Arguments) { '{"protocol_version":1,"operation_id":"' + $Id + '","document":{"path":"' + $Path + '","unique_id":"' + $DocumentUid + '"},"command":"' + $Command + '","args":' + $Arguments + '}' }
        function Ok($State, [string]$Path, [string]$Id, [string]$Command, [string]$Arguments) {
            $reply = Invoke-FakeRevitCommand $State (Env $Path $Id $Command $Arguments)
            if (($reply | ConvertFrom-Json).status -ne 'succeeded') { throw "$Id ($Command) did not succeed: $reply" }
            return $reply
        }
        function Wire($State, [string]$Line) { try { Invoke-FakeRevitWire $State $Line } catch { '<threw: ' + $_.Exception.Message + '>' } }
        $r = [ordered]@{}
        $s = New-FakeRevitState $DocumentUid @{} @('x-move-a')
        $level = [string]@((Ok $s $a 'levels' 'query.levels' '{}' | ConvertFrom-Json).readback.levels)[0].unique_id
        $r.wall = [string]@((Ok $s $a 'wall-1' 'wall.create' ('{"level":"' + $level + '","type":"T1","height_mm":3000,"curve":{"kind":"line","start":[0,0],"end":[5000,0]}}') | ConvertFrom-Json).created)[0]
        $r.s0 = Ok $s $a 'snapshot-0' 'query.snapshot' '{}'
        $r.a0 = Wire $s (Line $a 'null')
        Add-FakeRecorderFailure $s
        $r.a1 = Wire $s (Line $a '{"peek":true}')
        $r.b0 = Wire $s (Line $b 'null')
        $r.a2 = Wire $s (Line $a 'null')
        $r.a3 = Wire $s (Line $a 'null')
        $r.b1 = Wire $s (Line $b '{"peek":true}')
        Add-FakeRecorderFailure $s 2
        $null = Ok $s $a 'x-move-a' 'element.move' ('{"element_uids":["' + $r.wall + '"],"vector_mm":[250,0,0]}')
        $r.s1 = Ok $s $a 'snapshot-1' 'query.snapshot' '{}'
        $r.a4 = Wire $s (Line $a.ToUpperInvariant() 'null')
        $r.a5 = Wire $s (Line $a '{"peek":true,"after_sequence":0}')
        $r.b2 = Wire $s (Line $b 'null')
        # Test-only lever: the fake has no document.open, so the open document's UniqueId is switched on the state itself.
        $s.DocumentUid = $OtherUid
        $r.su0 = Ok $s $a 'snapshot-u0' 'query.snapshot' '{}'
        $r.u0 = Wire $s (Line $a 'null')
        $r.u1 = Wire $s (Line $a '{"peek":true}')
        # Without -ExternalEdits the lever is refused, so a legacy reply can never carry a failure.
        $legacy = New-FakeRevitState $DocumentUid
        $r.legacy = try { Add-FakeRecorderFailure $legacy; 'accepted' } catch { 'refused: ' + $_.Exception.Message }
        $r.legacy_read = Wire $legacy (Line $a 'null')
        $r | ConvertTo-Json -Compress
        """;

    static void FakeFailureChecks(string root, string pwsh)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "fake failures: " + name, detail);
        var scratch = Path.Combine(Path.GetTempPath(), "bim-drift-failures-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(scratch);
        Directory.CreateDirectory(scratch);
        var script = Path.Combine(scratch, "failures.ps1");
        File.WriteAllText(script, FailureScript, Utf8);
        var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Utf8 };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Executor", Path.Combine(root, "tests", "integration", "fake-revit-command-executor.ps1"), "-DocumentUid", DocumentUid, "-OtherUid", OtherDocumentUid }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CallTimeoutMs)) { Kill(process); Check(false, "the in-process failure script finishes", "timed out"); return; }
        process.WaitForExit();
        var output = stdout.Result.Trim();
        if (process.ExitCode != 0 || !output.StartsWith('{')) { Check(false, "the in-process failure script runs (Add-FakeRecorderFailure injects a recorder failure)", "exit " + process.ExitCode + " " + Head(output) + " " + Head(stderr.Result.ReplaceLineEndings(" "))); return; }
        using var parsed = JsonDocument.Parse(output);
        string R(string name) => parsed.RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "<missing>";
        const string a = "/fake/Drift-Doc-A.rvt", b = "/fake/Drift-Doc-B.rvt";
        var wall = R("wall");
        static MirrorSnapshot Snap(string response) { using var reply = JsonDocument.Parse(response); return MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText()); }
        MirrorSnapshot s0, s1, su0;
        try { (s0, s1, su0) = (Snap(R("s0")), Snap(R("s1")), Snap(R("su0"))); }
        catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, "the fake's query.snapshot readbacks parse as snapshots", error.Message + " | " + Head(output)); return; }
        Check(su0.DocumentUid == OtherDocumentUid, "the snapshot after the UniqueId switch names the other document", su0.DocumentUid);
        Check(R("legacy").StartsWith("refused: ", StringComparison.Ordinal), "Add-FakeRecorderFailure refuses a state without -ExternalEdits", R("legacy"));
        Check(R("legacy_read").Contains("\"failures\":0,", StringComparison.Ordinal) && R("legacy_read").Contains("\"journal_complete\":true,\"failures_since_baseline\":0,", StringComparison.Ordinal),
            "a legacy state's reply after the refused lever still reports no failure", R("legacy_read"));

        // The production side: one Core journal and tracker, the same add-in write (skipped), failures, commit and reads.
        var tick = 0;
        var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
        var tracker = new MirrorJournalTracker(journal);
        void Record(string uid, string path, string name, string[] modified, bool addIn = false) =>
            journal.Record(new MirrorJournalChange(uid, "TransactionCommitted", new[] { name }, addIn ? new[] { "x" } : Array.Empty<string>(), modified, Array.Empty<long>(), addIn) { DocumentPath = path });
        string Read(MirrorSnapshot snapshot, string path, bool peek = false, long? after = null) => RevitJournalQuery.RawRead(tracker, true, snapshot, path, new MirrorJournalReadOptions(after, peek));
        Record(DocumentUid, a, "BIM Agentic OS wall.create", Array.Empty<string>(), addIn: true);
        var production = new Dictionary<string, string>();
        production["a0"] = Read(s0, a);
        journal.RecordFailure();
        production["a1"] = Read(s0, a, peek: true);
        production["b0"] = Read(s0, b);
        production["a2"] = Read(s0, a);
        production["a3"] = Read(s0, a);
        production["b1"] = Read(s0, b, peek: true);
        journal.RecordFailure();
        journal.RecordFailure();
        Record(DocumentUid, a, "Move", new[] { wall });
        production["a4"] = Read(s1, a.ToUpperInvariant());
        production["a5"] = Read(s1, a, peek: true, after: 0);
        production["b2"] = Read(s1, b);
        production["u0"] = Read(su0, a);
        production["u1"] = Read(su0, a, peek: true);

        // What each read says about failures, from the fake's reply.
        string Shape(string reply)
        {
            try
            {
                using var document = JsonDocument.Parse(reply);
                var top = document.RootElement;
                var report = top.GetProperty("report");
                return report.GetProperty("document_unique_id").GetString()![^4..] + " failures=" + top.GetProperty("failures").GetInt64().ToString(CultureInfo.InvariantCulture)
                    + " since=" + report.GetProperty("failures_since_baseline").GetInt64().ToString(CultureInfo.InvariantCulture)
                    + " complete=" + report.GetProperty("journal_complete").GetBoolean().ToString().ToLowerInvariant()
                    + " existed=" + report.GetProperty("baseline_existed").GetBoolean().ToString().ToLowerInvariant()
                    + " entries=[" + string.Join(",", report.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64().ToString(CultureInfo.InvariantCulture))) + "]";
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return "<no report> " + Head(reply); }
        }
        var cases = new (string Name, string Shape, string What)[]
        {
            ("a0", "e2e1 failures=0 since=0 complete=true existed=false entries=[]", "the first read of A before any failure: complete, nothing since"),
            ("a1", "e2e1 failures=1 since=1 complete=false existed=true entries=[]", "a peek of A after one recorder failure: one since A's baseline, incomplete"),
            ("b0", "e2e1 failures=1 since=1 complete=false existed=false entries=[]", "the first read of B: no baseline, so every failure so far is since it, incomplete"),
            ("a2", "e2e1 failures=1 since=1 complete=false existed=true entries=[]", "a non-peek read of A after the peek: the failure still counts (the peek stored nothing)"),
            ("a3", "e2e1 failures=1 since=0 complete=true existed=true entries=[]", "the next read of A: its baseline holds the count, so complete again"),
            ("b1", "e2e1 failures=1 since=0 complete=true existed=true entries=[]", "a peek of B: B's own baseline (from its first read) holds the count"),
            ("a4", "e2e1 failures=3 since=2 complete=false existed=true entries=[1]", "a read of A under the upper-case path after two more failures and an external move: A's baseline (case ignored), two since, A's entry"),
            ("a5", "e2e1 failures=3 since=0 complete=true existed=true entries=[1]", "a peek of A from sequence 0: the baseline the previous read stored, no failure since, the entry re-read"),
            ("b2", "e2e1 failures=3 since=2 complete=false existed=true entries=[]", "a read of B: two failures since B's own baseline"),
            ("u0", "e2e2 failures=3 since=3 complete=false existed=false entries=[]", "the first read after the fake's document UniqueId changed: no baseline, all three failures since"),
            ("u1", "e2e2 failures=3 since=0 complete=true existed=true entries=[]", "a peek of the other document after its read: its own baseline holds the count"),
        };
        foreach (var (name, shape, what) in cases)
        {
            var reply = R(name);
            Check(Shape(reply) == shape, "reply " + name + ": " + what + " (" + shape + ")", Shape(reply));
            Check(Uniform(reply) == Uniform(production[name]), "reply " + name + " equals RevitJournalQuery.RawRead over one Core tracker given the same failures, member for member (diff included)", Uniform(reply) + " vs " + Uniform(production[name]));
        }
    }

    // P2-24: recorder failures behind the named-pipe fake. The pipe fake's test-only -RecorderFailures option
    // ("<operation_id>:<count>,...", needs -ExternalEdits) calls Add-FakeRecorderFailure with that count once the named
    // revit.command has run (the first time), so a failure lands between two calls. Over the real pipe and the real CLI:
    // walls A (add-in write, one failure after it), snapshot 0, a first read without peek (no baseline: one failure since,
    // incomplete), a peek (its baseline holds the count: complete), an external move of A (two failures after it),
    // snapshot 1, a peek after sequence 0 (two since, incomplete, A's entry), mirror-drift with that reply (evidence
    // incomplete, note journal_incomplete), a read without peek (still two since) and a last peek (complete). Each reply
    // equals RevitJournalQuery.RawRead over one Core MirrorJournalTracker given the same changes, RecordFailure calls and
    // snapshots, member for member (diff included). The fake served exactly those requests (the injection adds none).
    // Without the option the P2-14 / P2-16 pipe runs above keep their pinned replies byte for byte; a malformed option,
    // a count below 1, a duplicate id or the option without -ExternalEdits is refused before the pipe is ready.
    static readonly string[] FailureEdits = { "p2-24-move-a" };
    const string FailureSchedule = "p2-24-wall-a:1,p2-24-move-a:2";
    static readonly string[] FailureRequests = { "p2-24-levels", "p2-24-wall-types", "p2-24-wall-a", "p2-24-snapshot-0", JournalWire, JournalWire, "p2-24-move-a", "p2-24-snapshot-1", JournalWire, JournalWire, JournalWire };

    // What a journal.read reply says about failures: document, failures, since, complete, baseline_existed, entry sequences.
    static string FailureShape(string reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply);
            var top = document.RootElement;
            var report = top.GetProperty("report");
            return "failures=" + top.GetProperty("failures").GetInt64().ToString(CultureInfo.InvariantCulture)
                + " since=" + report.GetProperty("failures_since_baseline").GetInt64().ToString(CultureInfo.InvariantCulture)
                + " complete=" + report.GetProperty("journal_complete").GetBoolean().ToString().ToLowerInvariant()
                + " existed=" + report.GetProperty("baseline_existed").GetBoolean().ToString().ToLowerInvariant()
                + " entries=[" + string.Join(",", report.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64().ToString(CultureInfo.InvariantCulture))) + "]";
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return "<no report> " + Head(reply); }
    }

    static void RunFailureLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "pipe failures: " + name, detail);
        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Failures.rvt", FailureRequests.Length, string.Join(",", FailureEdits), FailureSchedule);
        var document = h.Document;
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready with -RecorderFailures " + FailureSchedule, notReady); return; }
        var levels = h.Command("p2-24-levels", "query.levels", _ => { });
        var types = h.Command("p2-24-wall-types", "query.wall_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed", R(levels) + " | " + R(types)); return; }
        var level = Pick(levels.Out, "levels", "name", "Level 1");
        var type = Pick(types.Out, "wall_types", "name", WallType);
        var wallA = h.Command("p2-24-wall-a", "wall.create", w => WallArgs(w, level, type, 0, 0, 5000, 0));
        if (!Succeeded(wallA)) { Check(false, "wall.create of A succeeds through the CLI", R(wallA)); return; }
        var uidA = Created(wallA.Out);
        var snapshot0 = h.Command("p2-24-snapshot-0", "query.snapshot", _ => { });
        if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return; }
        var replies = new Dictionary<string, string>
        {
            ["j0"] = h.Journal(null, peek: false),
            ["j1"] = h.Journal(null),
        };
        var move = h.Command("p2-24-move-a", "element.move", w => MoveArgs(w, uidA));
        if (!Succeeded(move)) { Check(false, "the external element.move of A succeeds through the CLI", R(move)); return; }
        var snapshot1 = h.Command("p2-24-snapshot-1", "query.snapshot", _ => { });
        if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the move succeeds", R(snapshot1)); return; }
        replies["j2"] = h.Journal(0);

        // The drift loop reads that reply as incomplete evidence.
        var (project, map, levelNames) = CoreFrom(snapshot0.Out, levels.Out);
        var store = Path.Combine(h.Scratch, "store");
        var init = h.Cli("init", store, h.Write("project-r1", JsonSerializer.Serialize(project)));
        if (init.Exit != 0 || Status(init.Out) != "succeeded") { Check(false, "the CLI init verb seeds the store at revision 1", R(init)); return; }
        var driftPath = h.NewPath("drift-failures");
        var drift = h.Cli("mirror-drift", store, h.Write("snapshot-1", snapshot1.Out), h.Write("mirror-r1", MirrorJson(document, 1, map, levelNames, replies["j2"], 0)), driftPath);
        if (drift.Exit != 0 || !File.Exists(driftPath)) Check(false, "mirror-drift with the incomplete journal succeeds", R(drift));
        else
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(driftPath, Utf8));
            var evidence = parsed.RootElement.GetProperty("report").GetProperty("evidence");
            Check(evidence.GetProperty("incomplete").GetBoolean() && evidence.GetProperty("notes").EnumerateArray().Any(note => note.GetString() == MirrorDriftEvidenceNotes.JournalIncomplete),
                "mirror-drift with the pipe's reply after two failures since the baseline reports the journal evidence incomplete (journal_incomplete)", evidence.GetRawText());
        }
        replies["j3"] = h.Journal(null, peek: false);
        replies["j4"] = h.Journal(null);

        var (finished, how) = h.Finish();
        Check(finished, "the fake add-in exits 0 after exactly " + FailureRequests.Length + " requests", how);
        var received = h.Received();
        Check(received.SequenceEqual(FailureRequests), "the fake add-in received the expected requests in order (injected failures add none)", string.Join(",", received));

        // The production side: one Core journal and tracker, the same add-in write (skipped), failures, commit and reads.
        MirrorSnapshot s0, s1;
        try
        {
            using (var reply = JsonDocument.Parse(snapshot0.Out)) s0 = MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText());
            using (var reply = JsonDocument.Parse(snapshot1.Out)) s1 = MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText());
        }
        catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, "the fake's query.snapshot readbacks parse as snapshots", error.Message); return; }
        var tick = 0;
        var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
        var tracker = new MirrorJournalTracker(journal);
        string Read(MirrorSnapshot snapshot, bool peek, long? after = null) => RevitJournalQuery.RawRead(tracker, true, snapshot, document, new MirrorJournalReadOptions(after, peek));
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "BIM Agentic OS wall.create" }, new[] { "x" }, Array.Empty<string>(), Array.Empty<long>(), true) { DocumentPath = document });
        journal.RecordFailure();
        var production = new Dictionary<string, string> { ["j0"] = Read(s0, false), ["j1"] = Read(s0, true) };
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Move" }, Array.Empty<string>(), new[] { uidA }, Array.Empty<long>()) { DocumentPath = document });
        journal.RecordFailure();
        journal.RecordFailure();
        production["j2"] = Read(s1, true, 0);
        production["j3"] = Read(s1, false);
        production["j4"] = Read(s1, true);

        var cases = new (string Name, string Shape, string What)[]
        {
            ("j0", "failures=1 since=1 complete=false existed=false entries=[]", "the first read without peek after the failure injected after wall A: no baseline, one since, incomplete"),
            ("j1", "failures=1 since=0 complete=true existed=true entries=[]", "a peek after it: the baseline holds the count, complete"),
            ("j2", "failures=3 since=2 complete=false existed=true entries=[1]", "a peek after sequence 0 once two failures were injected after the external move: two since, incomplete, the move's entry"),
            ("j3", "failures=3 since=2 complete=false existed=true entries=[1]", "a read without peek: still two since (the peek stored nothing)"),
            ("j4", "failures=3 since=0 complete=true existed=true entries=[]", "the last peek: the read stored the count with its baseline, complete again"),
        };
        foreach (var (name, shape, what) in cases)
        {
            Check(FailureShape(replies[name]) == shape, "pipe reply " + name + ": " + what + " (" + shape + ")", FailureShape(replies[name]) + " | " + Head(replies[name]));
            Check(Uniform(replies[name]) == Uniform(production[name]), "pipe reply " + name + " equals RevitJournalQuery.RawRead over one Core tracker given the same failures, member for member (diff included)",
                Uniform(replies[name]) + " vs " + Uniform(production[name]));
        }
    }

    // P2-24: -RecorderFailures is refused before the pipe is ready when it cannot be applied.
    static void FailureOptionChecks(string root, string pwsh)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "pipe failure options: " + name, detail);
        var cases = new (string Name, string? ExternalEdits, string Schedule)[]
        {
            ("without -ExternalEdits", null, "p2-24-wall-a:1"),
            ("with an -ExternalEdits list of no ids", " , ", "p2-24-wall-a:1"),
            ("a pair without a count", "p2-24-move-a", "p2-24-wall-a"),
            ("a count of 0", "p2-24-move-a", "p2-24-wall-a:0"),
            ("a count that is not a number", "p2-24-move-a", "p2-24-wall-a:x"),
            ("the same operation id twice", "p2-24-move-a", "p2-24-wall-a:1,p2-24-wall-a:2"),
            ("no pair at all", "p2-24-move-a", " , "),
        };
        foreach (var (name, external, schedule) in cases)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "bim-drift-failure-option-" + Guid.NewGuid().ToString("N"));
            Temporary.Add(scratch);
            Directory.CreateDirectory(scratch);
            var ready = Path.Combine(scratch, "ready.txt");
            var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root, "tests", "integration", "fake-revit-command-pipe.ps1"), "-PipeName", "bim-agentic-os-2025-" + Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture),
                "-DocumentUid", DocumentUid, "-ReadyFile", ready, "-LogFile", Path.Combine(scratch, "received.txt"), "-MaxRequests", "1", "-TimeoutSeconds", "5", "-RecorderFailures", schedule }) start.ArgumentList.Add(argument);
            if (external is not null) { start.ArgumentList.Add("-ExternalEdits"); start.ArgumentList.Add(external); }
            Unix(start);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(CallTimeoutMs)) { Kill(process); Check(false, "-RecorderFailures " + name + " is refused", "timed out"); continue; }
            process.WaitForExit();
            var error = stderr.Result.ReplaceLineEndings(" ");
            Check(process.ExitCode != 0 && !File.Exists(ready) && error.Contains("-RecorderFailures", StringComparison.Ordinal),
                "-RecorderFailures " + name + " is refused before the pipe is ready (non-zero exit, no ready file, the error names the option)", "exit " + process.ExitCode + " " + Head(error) + " " + Head(stdout.Result));
        }
    }

    // P2-26: -RecorderFailures injects only after the named operation's reply is succeeded and the request is not a dry
    // run. Over the real pipe and the real CLI, with "p2-26-move-a:2,p2-26-dry-delete:3": wall A (add-in write), snapshot 0,
    // a read without peek (baseline, no failure), element.move named p2-26-move-a of an element that does not exist
    // (refused reference_not_found), a dry-run element.delete of A named p2-26-dry-delete (succeeded, dry run), a peek
    // (nothing injected: no failure, complete), the external move of A retried under the same id (succeeded: the two
    // failures land), snapshot 1 and a peek after sequence 0 (two since, incomplete, the move's entry). Each reply equals
    // RevitJournalQuery.RawRead over one Core tracker given the same changes and failures, member for member. P2-27: then
    // the external element.delete of A named p2-27-delete-a with an explicit dry_run false (scheduled :1, so it injects:
    // only dry_run true is a dry run), snapshot 2 and a peek after sequence 0 (three failures, the move's and the delete's
    // entries). At exit the scheduled id never matched (p2-26-dry-delete) is written to stderr and the fake exits exactly
    // 1; the matched ones are not named.
    const string GateSchedule = "p2-26-move-a:2,p2-26-dry-delete:3,p2-27-delete-a:1";
    const string GateExternal = "p2-26-move-a,p2-27-delete-a";
    const string MissingUid = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0-0000ffff";
    static readonly string[] GateRequests = { "p2-26-levels", "p2-26-wall-types", "p2-26-wall-a", "p2-26-snapshot-0", JournalWire, "p2-26-move-a", "p2-26-dry-delete", JournalWire, "p2-26-move-a", "p2-26-snapshot-1", JournalWire, "p2-27-delete-a", "p2-27-snapshot-2", JournalWire };

    static void RunFailureGateLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "pipe failure gate: " + name, detail);
        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Failure-Gate.rvt", GateRequests.Length, GateExternal, GateSchedule);
        var document = h.Document;
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready with -RecorderFailures " + GateSchedule, notReady); return; }
        var levels = h.Command("p2-26-levels", "query.levels", _ => { });
        var types = h.Command("p2-26-wall-types", "query.wall_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed", R(levels) + " | " + R(types)); return; }
        var level = Pick(levels.Out, "levels", "name", "Level 1");
        var type = Pick(types.Out, "wall_types", "name", WallType);
        var wallA = h.Command("p2-26-wall-a", "wall.create", w => WallArgs(w, level, type, 0, 0, 5000, 0));
        if (!Succeeded(wallA)) { Check(false, "wall.create of A succeeds through the CLI", R(wallA)); return; }
        var uidA = Created(wallA.Out);
        var snapshot0 = h.Command("p2-26-snapshot-0", "query.snapshot", _ => { });
        if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return; }
        var replies = new Dictionary<string, string> { ["g0"] = h.Journal(null, peek: false) };
        var refused = h.Command("p2-26-move-a", "element.move", w => MoveArgs(w, MissingUid));
        Check(Status(refused.Out) != "succeeded" && refused.Out.Contains("reference_not_found", StringComparison.Ordinal),
            "the element.move named p2-26-move-a of a missing element is refused by the fake (reference_not_found)", R(refused));
        var dry = h.Command("p2-26-dry-delete", "element.delete", w => { w.WriteStartArray("element_uids"); w.WriteStringValue(uidA); w.WriteEndArray(); w.WriteBoolean("dry_run", true); });
        Check(Succeeded(dry), "the dry-run element.delete of A named p2-26-dry-delete succeeds", R(dry));
        replies["g1"] = h.Journal(null);
        var move = h.Command("p2-26-move-a", "element.move", w => MoveArgs(w, uidA));
        if (!Succeeded(move)) { Check(false, "the external element.move of A retried under p2-26-move-a succeeds through the CLI", R(move)); return; }
        var snapshot1 = h.Command("p2-26-snapshot-1", "query.snapshot", _ => { });
        if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the move succeeds", R(snapshot1)); return; }
        replies["g2"] = h.Journal(0);
        // P2-27: an explicit dry_run false is a write, so the scheduled p2-27-delete-a injects its failure.
        var delete = h.Command("p2-27-delete-a", "element.delete", w => { w.WriteStartArray("element_uids"); w.WriteStringValue(uidA); w.WriteEndArray(); w.WriteBoolean("dry_run", false); });
        if (!Succeeded(delete)) { Check(false, "the external element.delete of A named p2-27-delete-a with dry_run false succeeds through the CLI", R(delete)); return; }
        var snapshot2 = h.Command("p2-27-snapshot-2", "query.snapshot", _ => { });
        if (!Succeeded(snapshot2)) { Check(false, "the query.snapshot after the delete succeeds", R(snapshot2)); return; }
        replies["g3"] = h.Journal(0);

        var (exited, exit, error) = h.End();
        Check(exited && exit == 1, "the fake add-in exits exactly 1 after exactly " + GateRequests.Length + " requests because p2-26-dry-delete was never matched", "exited " + exited + " exit " + exit + " " + error);
        // P2-28: the whole stderr is the gate line naming exactly the unmatched id, so an uncaught throw (also exit 1) fails.
        Check(error == GateLine("p2-26-dry-delete"),
            "the fake's whole stderr is the gate line naming exactly the unmatched p2-26-dry-delete (neither the matched p2-26-move-a nor the matched p2-27-delete-a, dry_run false)", error + " vs " + GateLine("p2-26-dry-delete"));
        var received = h.Received();
        Check(received.SequenceEqual(GateRequests), "the fake add-in received the expected requests in order", string.Join(",", received));

        MirrorSnapshot s0, s1, s2;
        try
        {
            using (var reply = JsonDocument.Parse(snapshot0.Out)) s0 = MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText());
            using (var reply = JsonDocument.Parse(snapshot1.Out)) s1 = MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText());
            using (var reply = JsonDocument.Parse(snapshot2.Out)) s2 = MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText());
        }
        catch (Exception parseError) when (parseError is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, "the fake's query.snapshot readbacks parse as snapshots", parseError.Message); return; }
        var tick = 0;
        var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
        var tracker = new MirrorJournalTracker(journal);
        string Read(MirrorSnapshot snapshot, bool peek, long? after = null) => RevitJournalQuery.RawRead(tracker, true, snapshot, document, new MirrorJournalReadOptions(after, peek));
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "BIM Agentic OS wall.create" }, new[] { "x" }, Array.Empty<string>(), Array.Empty<long>(), true) { DocumentPath = document });
        var production = new Dictionary<string, string> { ["g0"] = Read(s0, false), ["g1"] = Read(s0, true) };
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Move" }, Array.Empty<string>(), new[] { uidA }, Array.Empty<long>()) { DocumentPath = document });
        journal.RecordFailure();
        journal.RecordFailure();
        production["g2"] = Read(s1, true, 0);
        if (!long.TryParse(uidA[(uidA.LastIndexOf('-') + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var deletedA) || deletedA <= 0) { Check(false, "wall A's UniqueId ends in a hex element id", uidA); return; }
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Delete" }, Array.Empty<string>(), Array.Empty<string>(), new[] { deletedA }) { DocumentPath = document });
        journal.RecordFailure();
        production["g3"] = Read(s2, true, 0);

        var cases = new (string Name, string Shape, string What)[]
        {
            ("g0", "failures=0 since=0 complete=true existed=false entries=[]", "the first read without peek: no failure yet"),
            ("g1", "failures=0 since=0 complete=true existed=true entries=[]", "a peek after the refused move and the dry-run delete carrying scheduled ids: nothing injected"),
            ("g2", "failures=2 since=2 complete=false existed=true entries=[1]", "a peek after sequence 0 once the retried move succeeded: its two failures, incomplete, the move's entry"),
            ("g3", "failures=3 since=3 complete=false existed=true entries=[1,2]", "a peek after sequence 0 once the delete with dry_run false succeeded: its one failure added, the move's and the delete's entries"),
        };
        foreach (var (name, shape, what) in cases)
        {
            Check(FailureShape(replies[name]) == shape, "pipe reply " + name + ": " + what + " (" + shape + ")", FailureShape(replies[name]) + " | " + Head(replies[name]));
            Check(Uniform(replies[name]) == Uniform(production[name]), "pipe reply " + name + " equals RevitJournalQuery.RawRead over one Core tracker given the same failures, member for member (diff included)",
                Uniform(replies[name]) + " vs " + Uniform(production[name]));
        }
    }

    // P2-26: -RecorderFailures "nope:1" (an id no request carries) is accepted, injects nothing and leaves every journal.read
    // reply byte-equal to the same run without the option; at exit the fake writes the unmatched id to stderr and exits
    // exactly 1 (P2-27), where the option-less run exits 0.
    static readonly string[] UnmatchedRequests = { "p2-26-levels", "p2-26-wall-types", "p2-26-wall-a", "p2-26-snapshot-0", JournalWire, "p2-26-move-a", "p2-26-snapshot-1", JournalWire, JournalWire };

    static void RunUnmatchedFailureLoops(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "pipe failure unmatched: " + name, detail);
        (string Replies, bool Exited, int Exit, string Error, string[] Received)? Run(string? schedule)
        {
            var label = schedule ?? "no option";
            using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Failure-Unmatched.rvt", UnmatchedRequests.Length, "p2-26-move-a", schedule);
            if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready (" + label + ")", notReady); return null; }
            var levels = h.Command("p2-26-levels", "query.levels", _ => { });
            var types = h.Command("p2-26-wall-types", "query.wall_types", _ => { });
            if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed (" + label + ")", R(levels) + " | " + R(types)); return null; }
            var wallA = h.Command("p2-26-wall-a", "wall.create", w => WallArgs(w, Pick(levels.Out, "levels", "name", "Level 1"), Pick(types.Out, "wall_types", "name", WallType), 0, 0, 5000, 0));
            if (!Succeeded(wallA)) { Check(false, "wall.create of A succeeds (" + label + ")", R(wallA)); return null; }
            var snapshot0 = h.Command("p2-26-snapshot-0", "query.snapshot", _ => { });
            var j0 = h.Journal(null, peek: false);
            var move = h.Command("p2-26-move-a", "element.move", w => MoveArgs(w, Created(wallA.Out)));
            var snapshot1 = h.Command("p2-26-snapshot-1", "query.snapshot", _ => { });
            if (!Succeeded(snapshot0) || !Succeeded(move) || !Succeeded(snapshot1)) { Check(false, "the snapshots and the external move succeed (" + label + ")", R(snapshot0) + " | " + R(move) + " | " + R(snapshot1)); return null; }
            var j1 = h.Journal(0);
            var j2 = h.Journal(null, peek: false);
            var (exited, exit, error) = h.End();
            return (string.Join("\n", j0, j1, j2), exited, exit, error, h.Received());
        }
        var plain = Run(null);
        var nope = Run("nope:1");
        if (plain is not { } p || nope is not { } n) { Check(false, "both runs completed", plain is null ? "option-less run stopped" : "nope:1 run stopped"); return; }
        Check(p.Exited && p.Exit == 0, "the option-less fake exits 0", "exit " + p.Exit + " " + p.Error);
        Check(p.Received.SequenceEqual(UnmatchedRequests) && n.Received.SequenceEqual(UnmatchedRequests), "both fakes received the expected requests in order", string.Join(",", p.Received) + " | " + string.Join(",", n.Received));
        Check(FailureShape(p.Replies.Split('\n')[1]) == "failures=0 since=0 complete=true existed=true entries=[1]", "the option-less journal.read after the move reports no failure and the move's entry", FailureShape(p.Replies.Split('\n')[1]));
        Check(n.Replies == p.Replies, "with -RecorderFailures nope:1 every journal.read reply is byte-equal to the option-less run", Head(n.Replies) + " vs " + Head(p.Replies));
        Check(n.Exited && n.Exit == 1, "with -RecorderFailures nope:1 the fake exits exactly 1 (nope never matched)", "exited " + n.Exited + " exit " + n.Exit + " " + n.Error);
        Check(n.Error == GateLine("nope"), "with -RecorderFailures nope:1 the fake's whole stderr is the gate line naming exactly the unmatched id nope", n.Error + " vs " + GateLine("nope"));

        // P2-28: why the whole line: an uncaught throw naming the option and the id (here the refused "nope:0") also exits
        // exactly 1 and passes the P2-26 rule (stderr contains "-RecorderFailures" and "nope"), but is not the gate line.
        using var thrown = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Failure-Thrown.rvt", 1, "p2-26-move-a", "nope:0", noColor: true);
        var thrownReady = thrown.WaitReady(out _);
        var t = thrown.End();
        Check(!thrownReady && t.Exited && t.Exit == 1 && t.Error.Contains("nope", StringComparison.Ordinal) && t.Error.Contains("-RecorderFailures", StringComparison.Ordinal),
            "an uncaught throw naming -RecorderFailures and nope (the refused nope:0) also exits exactly 1 and passes the P2-26 contains rule", "ready " + thrownReady + " exited " + t.Exited + " exit " + t.Exit + " " + Head(t.Error));
        Check(t.Error.Contains("-RecorderFailures pair 'nope:0' needs a count of 1 or more", StringComparison.Ordinal) && t.Error != GateLine("nope"),
            "the uncaught throw's stderr is the fake's nope:0 count refusal (NO_COLOR), not the gate line, so the exact-line checks above reject it", Head(t.Error));

        // P2-28: several unmatched ids: one line, the ids sorted ordinally and comma-joined (zz-id before aa-id in the option).
        using var multi = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Failure-Multi.rvt", 1, "zz-id", "zz-id:1,aa-id:1");
        if (!multi.WaitReady(out var multiNotReady)) { Check(false, "fake add-in pipe ready with -RecorderFailures zz-id:1,aa-id:1", multiNotReady); return; }
        var multiReply = multi.Journal(null);
        var m = multi.End();
        Check(FailureShape(multiReply) == "failures=0 since=0 complete=true existed=false entries=[]", "with -RecorderFailures zz-id:1,aa-id:1 the one journal.read injects nothing", FailureShape(multiReply) + " | " + Head(multiReply));
        Check(multi.Received().SequenceEqual(new[] { JournalWire }), "with -RecorderFailures zz-id:1,aa-id:1 the fake received only the journal.read", string.Join(",", multi.Received()));
        Check(m.Exited && m.Exit == 1 && m.Error == GatePrefix + "aa-id,zz-id",
            "with -RecorderFailures zz-id:1,aa-id:1 and neither id sent the fake exits exactly 1 and its whole stderr is the gate line naming aa-id,zz-id (ordinal order)", "exited " + m.Exited + " exit " + m.Exit + " " + m.Error);
    }

    // P2-28: the one stderr line the fake writes at exit for the scheduled ids never matched (ordinally sorted, comma-joined).
    const string GatePrefix = "fake pipe: -RecorderFailures operation ids never matched by a succeeded, non-dry-run revit.command: ";
    static string GateLine(params string[] unmatched) => GatePrefix + string.Join(",", unmatched.Order(StringComparer.Ordinal));

    // P2-28: a write dropped by -DropReplyCommand (it ran, its connection closed without a reply) whose operation id is
    // scheduled still injects its failures and counts as matched (the P2-27 fake header). With -ExternalEdits
    // p2-28-move-a, -RecorderFailures p2-28-move-a:2 and -DropReplyCommand element.move: wall A, snapshot 0, a read without
    // peek, the external move of A named p2-28-move-a (the CLI gets no reply), snapshot 1 and a peek after sequence 0 (two
    // failures, incomplete, the move's entry), equal to RevitJournalQuery.RawRead over one Core tracker given the same
    // changes and failures, member for member. The fake exits 0 with an empty stderr: no gate line, the id never named.
    static readonly string[] DroppedRequests = { "p2-28-levels", "p2-28-wall-types", "p2-28-wall-a", "p2-28-snapshot-0", JournalWire, "p2-28-move-a", "p2-28-snapshot-1", JournalWire };

    static void RunDroppedFailureLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "pipe failure dropped: " + name, detail);
        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Failure-Dropped.rvt", DroppedRequests.Length, "p2-28-move-a", "p2-28-move-a:2", "element.move");
        var document = h.Document;
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready with -RecorderFailures p2-28-move-a:2 -DropReplyCommand element.move", notReady); return; }
        var levels = h.Command("p2-28-levels", "query.levels", _ => { });
        var types = h.Command("p2-28-wall-types", "query.wall_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed", R(levels) + " | " + R(types)); return; }
        var wallA = h.Command("p2-28-wall-a", "wall.create", w => WallArgs(w, Pick(levels.Out, "levels", "name", "Level 1"), Pick(types.Out, "wall_types", "name", WallType), 0, 0, 5000, 0));
        if (!Succeeded(wallA)) { Check(false, "wall.create of A succeeds through the CLI", R(wallA)); return; }
        var uidA = Created(wallA.Out);
        var snapshot0 = h.Command("p2-28-snapshot-0", "query.snapshot", _ => { });
        if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return; }
        var j0 = h.Journal(null, peek: false);
        var move = h.Command("p2-28-move-a", "element.move", w => MoveArgs(w, uidA));
        Check(Status(move.Out) == "failed" && (Text(move.Out, "code") is "no_response" or "pipe_io_error"),
            "the external element.move of A named p2-28-move-a gets no reply (its connection is dropped after the write): failed with no_response or pipe_io_error", R(move));
        var snapshot1 = h.Command("p2-28-snapshot-1", "query.snapshot", _ => { });
        if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the dropped move succeeds", R(snapshot1)); return; }
        var j1 = h.Journal(0);

        var (exited, exit, error) = h.End();
        Check(exited && exit == 0 && error.Length == 0, "the fake add-in exits 0 with an empty stderr (p2-28-move-a matched by the dropped write, never named)", "exited " + exited + " exit " + exit + " " + error);
        var received = h.Received();
        Check(received.SequenceEqual(DroppedRequests), "the fake add-in received the expected requests in order (the dropped move once)", string.Join(",", received));

        MirrorSnapshot s0, s1;
        try
        {
            using (var reply = JsonDocument.Parse(snapshot0.Out)) s0 = MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText());
            using (var reply = JsonDocument.Parse(snapshot1.Out)) s1 = MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText());
        }
        catch (Exception parseError) when (parseError is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, "the fake's query.snapshot readbacks parse as snapshots", parseError.Message); return; }
        var tick = 0;
        var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
        var tracker = new MirrorJournalTracker(journal);
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "BIM Agentic OS wall.create" }, new[] { "x" }, Array.Empty<string>(), Array.Empty<long>(), true) { DocumentPath = document });
        var p0 = RevitJournalQuery.RawRead(tracker, true, s0, document, new MirrorJournalReadOptions(null, false));
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Move" }, Array.Empty<string>(), new[] { uidA }, Array.Empty<long>()) { DocumentPath = document });
        journal.RecordFailure();
        journal.RecordFailure();
        var p1 = RevitJournalQuery.RawRead(tracker, true, s1, document, new MirrorJournalReadOptions(0, true));
        Check(FailureShape(j0) == "failures=0 since=0 complete=true existed=false entries=[]", "pipe reply d0: the first read without peek, no failure yet", FailureShape(j0) + " | " + Head(j0));
        Check(FailureShape(j1) == "failures=2 since=2 complete=false existed=true entries=[1]", "pipe reply d1: a peek after sequence 0 once the dropped move ran: its two failures, incomplete, the move's entry", FailureShape(j1) + " | " + Head(j1));
        Check(Uniform(j0) == Uniform(p0), "pipe reply d0 equals RevitJournalQuery.RawRead over one Core tracker, member for member (diff included)", Uniform(j0) + " vs " + Uniform(p0));
        Check(Uniform(j1) == Uniform(p1), "pipe reply d1 equals RevitJournalQuery.RawRead over one Core tracker given the same failures, member for member (diff included)", Uniform(j1) + " vs " + Uniform(p1));
    }

    // P2-25: the -ExternalEdits fake keeps at most the MirrorJournal retained-entry capacity (DefaultCapacity unless the
    // test-only lever Set-FakeJournalCapacity, before the first entry, sets a smaller one), drops the oldest entries over
    // all documents past it and counts them in dropped, and reports journal_complete false when FirstRetainedSequence >
    // after_sequence + 1, as MirrorJournalTracker does. One scenario, run on a fake with capacity 2 and on a default fake,
    // in process: A read; two external moves under A; a peek of A; a move under B (the capped fake drops sequence 1); a
    // peek of A, a peek of A after sequence 1, a first read of B, a read of A; two moves under A (two more drops); reads of
    // A and B; a peek of A from sequence 0; a recorder failure and a peek of A. Every reply equals RevitJournalQuery.RawRead
    // over a Core MirrorJournalTracker whose MirrorJournal has the same capacity and records the same commits, member for
    // member; the capped fake's replies before its first drop are byte-equal to the default fake's. Two edge scenarios in
    // the same child: a default fake fed 1026 entries (DefaultCapacity enforced: 1024 held from sequence 3, 2 dropped) and
    // a capacity-1 fake after two moves (sequence 2 held, 1 dropped), each against a Core journal of that capacity.
    const string RetentionScript = """
        param([string]$Executor, [string]$DocumentUid)
        $ErrorActionPreference = 'Stop'
        . $Executor
        $a = '/fake/Drift-Doc-A.rvt'; $b = '/fake/Drift-Doc-B.rvt'
        function Line([string]$Path, [string]$Journal) { '{"DocumentPath":"' + $Path + '","Operation":"journal.read","UniqueId":null,"Metres":[],"Journal":' + $Journal + '}' }
        function Env([string]$Path, [string]$Id, [string]$Command, [string]$Arguments) { '{"protocol_version":1,"operation_id":"' + $Id + '","document":{"path":"' + $Path + '","unique_id":"' + $DocumentUid + '"},"command":"' + $Command + '","args":' + $Arguments + '}' }
        function Ok($State, [string]$Path, [string]$Id, [string]$Command, [string]$Arguments) {
            $reply = Invoke-FakeRevitCommand $State (Env $Path $Id $Command $Arguments)
            if (($reply | ConvertFrom-Json).status -ne 'succeeded') { throw "$Id ($Command) did not succeed: $reply" }
            return $reply
        }
        function Wire($State, [string]$Line) { try { Invoke-FakeRevitWire $State $Line } catch { '<threw: ' + $_.Exception.Message + '>' } }
        function Refused([scriptblock]$Action) { try { $null = & $Action; 'accepted' } catch { 'refused: ' + $_.Exception.Message } }
        function Scenario([int]$Capacity) {
            $r = [ordered]@{}
            $s = New-FakeRevitState $DocumentUid @{} @('x-1', 'x-2', 'x-3', 'x-4', 'x-5')
            if ($Capacity -gt 0) { Set-FakeJournalCapacity $s $Capacity }
            $r.capacity = [string]$s.JournalCapacity
            $level = [string]@((Ok $s $a 'levels' 'query.levels' '{}' | ConvertFrom-Json).readback.levels)[0].unique_id
            $r.wall = [string]@((Ok $s $a 'wall-1' 'wall.create' ('{"level":"' + $level + '","type":"T1","height_mm":3000,"curve":{"kind":"line","start":[0,0],"end":[5000,0]}}') | ConvertFrom-Json).created)[0]
            $move = '{"element_uids":["' + $r.wall + '"],"vector_mm":[250,0,0]}'
            $r.s0 = Ok $s $a 'snapshot-0' 'query.snapshot' '{}'
            $r.a0 = Wire $s (Line $a 'null')
            $null = Ok $s $a 'x-1' 'element.move' $move
            $null = Ok $s $a 'x-2' 'element.move' $move
            $r.s1 = Ok $s $a 'snapshot-1' 'query.snapshot' '{}'
            $r.a1 = Wire $s (Line $a '{"peek":true}')
            $null = Ok $s $b 'x-3' 'element.move' $move
            $r.s2 = Ok $s $a 'snapshot-2' 'query.snapshot' '{}'
            $r.a2 = Wire $s (Line $a '{"peek":true}')
            $r.a3 = Wire $s (Line $a '{"peek":true,"after_sequence":1}')
            $r.b0 = Wire $s (Line $b 'null')
            $r.a4 = Wire $s (Line $a 'null')
            $null = Ok $s $a 'x-4' 'element.move' $move
            $null = Ok $s $a 'x-5' 'element.move' $move
            $r.s3 = Ok $s $a 'snapshot-3' 'query.snapshot' '{}'
            $r.a5 = Wire $s (Line $a 'null')
            $r.b1 = Wire $s (Line $b 'null')
            $r.a6 = Wire $s (Line $a '{"peek":true,"after_sequence":0}')
            Add-FakeRecorderFailure $s
            $r.a7 = Wire $s (Line $a '{"peek":true}')
            # The Core journal's capacity is fixed when it is made: once an entry is held the lever is refused.
            $r.late = Refused { Set-FakeJournalCapacity $s 1 }
            return $r
        }
        $out = [ordered]@{ capped = Scenario 2; default = Scenario 0 }
        $out.zero = Refused { Set-FakeJournalCapacity (New-FakeRevitState $DocumentUid @{} @('x-1')) 0 }
        $legacy = New-FakeRevitState $DocumentUid
        $out.legacy = Refused { Set-FakeJournalCapacity $legacy 2 }
        $out.legacy_read = Wire $legacy (Line $a 'null')
        # The default capacity enforced: 1026 entries fed straight into a default -ExternalEdits state.
        $bulk = New-FakeRevitState $DocumentUid @{} @('x-1')
        $out.bulk_snapshot = Ok $bulk $a 'snapshot-bulk' 'query.snapshot' '{}'
        foreach ($n in 1..1026) { Add-FakeJournalEntry $bulk $a 'Move' @() @('u-bulk') @() }
        $out.bulk_capacity = [string]$bulk.JournalCapacity
        $out.bulk_retained = [string]$bulk.Journal.Count
        $out.bulk_first = [string]$bulk.Journal[0].sequence
        $out.bulk_dropped = [string]$bulk.JournalDropped
        $out.bulk_r0 = Wire $bulk (Line $a '{"peek":true,"after_sequence":0,"max_entries":1}')
        $out.bulk_r2 = Wire $bulk (Line $a '{"peek":true,"after_sequence":2,"max_entries":1}')
        $out.bulk_read = Wire $bulk (Line $a '{"max_entries":1}')
        # The smallest capacity: 1, then two external moves under A.
        $one = New-FakeRevitState $DocumentUid @{} @('x-1', 'x-2')
        Set-FakeJournalCapacity $one 1
        $level = [string]@((Ok $one $a 'levels' 'query.levels' '{}' | ConvertFrom-Json).readback.levels)[0].unique_id
        $out.one_wall = [string]@((Ok $one $a 'wall-1' 'wall.create' ('{"level":"' + $level + '","type":"T1","height_mm":3000,"curve":{"kind":"line","start":[0,0],"end":[5000,0]}}') | ConvertFrom-Json).created)[0]
        $move = '{"element_uids":["' + $out.one_wall + '"],"vector_mm":[250,0,0]}'
        $null = Ok $one $a 'x-1' 'element.move' $move
        $null = Ok $one $a 'x-2' 'element.move' $move
        $out.one_snapshot = Ok $one $a 'snapshot-one' 'query.snapshot' '{}'
        $out.one_capacity = [string]$one.JournalCapacity
        $out.one_retained = [string]$one.Journal.Count
        $out.one_dropped = [string]$one.JournalDropped
        $out.one_r1 = Wire $one (Line $a '{"peek":true,"after_sequence":1}')
        $out.one_r0 = Wire $one (Line $a '{"peek":true,"after_sequence":0}')
        $out.one_read = Wire $one (Line $a 'null')
        $out | ConvertTo-Json -Compress -Depth 5
        """;

    static void FakeRetentionChecks(string root, string pwsh)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "fake retention: " + name, detail);
        var scratch = Path.Combine(Path.GetTempPath(), "bim-drift-retention-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(scratch);
        Directory.CreateDirectory(scratch);
        var script = Path.Combine(scratch, "retention.ps1");
        File.WriteAllText(script, RetentionScript, Utf8);
        var start = new ProcessStartInfo(pwsh) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Utf8 };
        foreach (var argument in new[] { "-NoProfile", "-File", script, "-Executor", Path.Combine(root, "tests", "integration", "fake-revit-command-executor.ps1"), "-DocumentUid", DocumentUid }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CallTimeoutMs)) { Kill(process); Check(false, "the in-process retention script finishes", "timed out"); return; }
        process.WaitForExit();
        var output = stdout.Result.Trim();
        if (process.ExitCode != 0 || !output.StartsWith('{')) { Check(false, "the in-process retention script runs (Set-FakeJournalCapacity sets a small journal capacity)", "exit " + process.ExitCode + " " + Head(output) + " " + Head(stderr.Result.ReplaceLineEndings(" "))); return; }
        using var parsed = JsonDocument.Parse(output);
        var top = parsed.RootElement;
        string T(string name) => top.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "<missing>";
        Check(T("zero").StartsWith("refused: ", StringComparison.Ordinal), "Set-FakeJournalCapacity refuses a capacity below 1 (journal_capacity_invalid)", T("zero"));
        Check(T("legacy").StartsWith("refused: ", StringComparison.Ordinal), "Set-FakeJournalCapacity refuses a state without -ExternalEdits", T("legacy"));
        Check(T("legacy_read").Contains("\"dropped\":0,", StringComparison.Ordinal) && T("legacy_read").Contains("\"journal_complete\":true,", StringComparison.Ordinal),
            "a legacy state's reply after the refused lever still reports nothing dropped", T("legacy_read"));
        const string a = "/fake/Drift-Doc-A.rvt", b = "/fake/Drift-Doc-B.rvt";
        static MirrorSnapshot Snap(string response) { using var reply = JsonDocument.Parse(response); return MirrorDrift.ParseSnapshot(reply.RootElement.GetProperty("readback").GetRawText()); }

        // What each read says about retention, from a reply.
        static string Shape(string reply)
        {
            try
            {
                using var document = JsonDocument.Parse(reply);
                var body = document.RootElement;
                var report = body.GetProperty("report");
                return "dropped=" + body.GetProperty("dropped").GetInt64().ToString(CultureInfo.InvariantCulture)
                    + " after=" + report.GetProperty("after_sequence").GetInt64().ToString(CultureInfo.InvariantCulture)
                    + " complete=" + report.GetProperty("journal_complete").GetBoolean().ToString().ToLowerInvariant()
                    + " since=" + report.GetProperty("failures_since_baseline").GetInt64().ToString(CultureInfo.InvariantCulture)
                    + " entries=[" + string.Join(",", report.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("sequence").GetInt64().ToString(CultureInfo.InvariantCulture))) + "]";
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return "<no report> " + Head(reply); }
        }

        var replies = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (run, capacity, cases) in new (string Run, int Capacity, (string Name, string Shape, string What)[] Cases)[]
        {
            ("capped", 2, new[]
            {
                ("a0", "dropped=0 after=0 complete=true since=0 entries=[]", "the first read of A: nothing recorded yet"),
                ("a1", "dropped=0 after=0 complete=true since=0 entries=[1,2]", "a peek of A at capacity: both entries held, nothing dropped"),
                ("a2", "dropped=1 after=0 complete=false since=0 entries=[2]", "a peek of A after B's move: sequence 1 dropped, FirstRetainedSequence 2 > after 0 + 1, incomplete"),
                ("a3", "dropped=1 after=1 complete=true since=0 entries=[2]", "a peek of A after sequence 1: FirstRetainedSequence 2 = after + 1, complete"),
                ("b0", "dropped=1 after=0 complete=false since=0 entries=[3]", "the first read of B: no baseline, after 0, a dropped entry (of another document) makes it incomplete"),
                ("a4", "dropped=1 after=0 complete=false since=0 entries=[2]", "a read of A from its baseline 0: still incomplete"),
                ("a5", "dropped=3 after=3 complete=true since=0 entries=[4,5]", "a read of A after two more moves: sequences 2 and 3 dropped, but none after A's baseline 3"),
                ("b1", "dropped=3 after=3 complete=true since=0 entries=[]", "a read of B: B's entry 3 was dropped, but at its baseline, so complete"),
                ("a6", "dropped=3 after=0 complete=false since=0 entries=[4,5]", "a peek of A from sequence 0: three dropped, incomplete"),
                ("a7", "dropped=3 after=5 complete=false since=1 entries=[]", "a peek of A after a recorder failure: no drop since, but a failure, incomplete"),
            }),
            ("default", MirrorJournal.DefaultCapacity, new[]
            {
                ("a0", "dropped=0 after=0 complete=true since=0 entries=[]", "the first read of A"),
                ("a1", "dropped=0 after=0 complete=true since=0 entries=[1,2]", "a peek of A"),
                ("a2", "dropped=0 after=0 complete=true since=0 entries=[1,2]", "a peek of A after B's move: nothing dropped"),
                ("a3", "dropped=0 after=1 complete=true since=0 entries=[2]", "a peek of A after sequence 1"),
                ("b0", "dropped=0 after=0 complete=true since=0 entries=[3]", "the first read of B"),
                ("a4", "dropped=0 after=0 complete=true since=0 entries=[1,2]", "a read of A from its baseline 0"),
                ("a5", "dropped=0 after=3 complete=true since=0 entries=[4,5]", "a read of A after two more moves"),
                ("b1", "dropped=0 after=3 complete=true since=0 entries=[]", "a read of B"),
                ("a6", "dropped=0 after=0 complete=true since=0 entries=[1,2,4,5]", "a peek of A from sequence 0: every entry held"),
                ("a7", "dropped=0 after=5 complete=false since=1 entries=[]", "a peek of A after a recorder failure"),
            }),
        })
        {
            if (!top.TryGetProperty(run, out var result) || result.ValueKind != JsonValueKind.Object) { Check(false, run + " scenario ran", Head(output)); continue; }
            string R(string name) => result.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "<missing>";
            replies[run] = cases.ToDictionary(c => c.Name, c => R(c.Name));
            Check(R("capacity") == capacity.ToString(CultureInfo.InvariantCulture), run + ": the fake's journal capacity is " + capacity + (run == "default" ? " (MirrorJournal.DefaultCapacity)" : " (the lever)"), R("capacity"));
            Check(R("late").StartsWith("refused: ", StringComparison.Ordinal), run + ": Set-FakeJournalCapacity is refused once the journal holds an entry", R("late"));
            MirrorSnapshot s0, s1, s2, s3;
            try { (s0, s1, s2, s3) = (Snap(R("s0")), Snap(R("s1")), Snap(R("s2")), Snap(R("s3"))); }
            catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, run + ": the fake's query.snapshot readbacks parse as snapshots", error.Message); continue; }
            var wall = R("wall");

            // The production side: one Core journal with the same capacity and tracker, the same commits and reads.
            var tick = 0;
            var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick), capacity);
            var tracker = new MirrorJournalTracker(journal);
            void Record(string path, string name, string[] modified, bool addIn = false) =>
                journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { name }, addIn ? new[] { "x" } : Array.Empty<string>(), modified, Array.Empty<long>(), addIn) { DocumentPath = path });
            string Read(MirrorSnapshot snapshot, string path, bool peek = false, long? after = null) => RevitJournalQuery.RawRead(tracker, true, snapshot, path, new MirrorJournalReadOptions(after, peek));
            var production = new Dictionary<string, string>();
            Record(a, "BIM Agentic OS wall.create", Array.Empty<string>(), addIn: true);
            production["a0"] = Read(s0, a);
            Record(a, "Move", new[] { wall });
            Record(a, "Move", new[] { wall });
            production["a1"] = Read(s1, a, peek: true);
            Record(b, "Move", new[] { wall });
            production["a2"] = Read(s2, a, peek: true);
            production["a3"] = Read(s2, a, peek: true, after: 1);
            production["b0"] = Read(s2, b);
            production["a4"] = Read(s2, a);
            Record(a, "Move", new[] { wall });
            Record(a, "Move", new[] { wall });
            production["a5"] = Read(s3, a);
            production["b1"] = Read(s3, b);
            production["a6"] = Read(s3, a, peek: true, after: 0);
            journal.RecordFailure();
            production["a7"] = Read(s3, a, peek: true);
            Check(journal.Capacity == capacity && journal.Dropped == (run == "capped" ? 3 : 0), run + ": the Core journal holds the same capacity and dropped count", journal.Capacity + " " + journal.Dropped);

            foreach (var (name, shape, what) in cases)
            {
                var reply = R(name);
                Check(Shape(reply) == shape, run + " reply " + name + ": " + what + " (" + shape + ")", Shape(reply));
                Check(Uniform(reply) == Uniform(production[name]), run + " reply " + name + " equals RevitJournalQuery.RawRead over a Core tracker with capacity " + capacity + " and the same commits, member for member (diff included)", Uniform(reply) + " vs " + Uniform(production[name]));
            }
        }
        if (replies.TryGetValue("capped", out var capped) && replies.TryGetValue("default", out var unbounded))
        {
            // Before its first drop the capped fake answers byte for byte as the default one.
            foreach (var name in new[] { "a0", "a1" }) Check(capped[name] == unbounded[name], "capped reply " + name + " (nothing dropped yet) is byte-equal to the default fake's", capped[name] + " vs " + unbounded[name]);
            // The drift loop's reader sees the dropped entries as an incomplete journal.
            bool? Complete(string reply) { try { return MirrorDrift.ParseJournal(reply).Complete; } catch (ArgumentException) { return null; } }
            Check(Complete(capped["a2"]) == false && Complete(capped["a5"]) == true && Complete(unbounded["a2"]) == true, "MirrorDrift.ParseJournal reads the capped fake's dropped entries as an incomplete journal (a2) and a later baseline as complete (a5)",
                Complete(capped["a2"]) + " " + Complete(capped["a5"]) + " " + Complete(unbounded["a2"]));
        }

        // Each top-level reply's retention shape, and equality with the Core RawRead reply of the same name.
        void Compare(string label, int coreCapacity, Dictionary<string, string> production, (string Name, string Shape, string What)[] cases)
        {
            foreach (var (name, shape, what) in cases)
            {
                var reply = T(name);
                Check(Shape(reply) == shape, label + " reply " + name + ": " + what + " (" + shape + ")", Shape(reply));
                Check(Uniform(reply) == Uniform(production[name]), label + " reply " + name + " equals RevitJournalQuery.RawRead over a Core tracker with capacity " + coreCapacity + " and the same records, member for member (diff included)",
                    Uniform(reply) + " vs " + Uniform(production[name]));
            }
        }

        // The default capacity enforced: a default fake fed 1026 entries keeps the last 1024 (sequences 3..1026) and
        // counts 2 dropped, as a Core MirrorJournal at DefaultCapacity fed 1026 records.
        void BulkDefaultCapacity()
        {
            MirrorSnapshot snapshot;
            try { snapshot = Snap(T("bulk_snapshot")); }
            catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, "bulk: the default fake's query.snapshot readback parses as a snapshot", error.Message); return; }
            Check(T("bulk_capacity") == "1024" && T("bulk_retained") == "1024" && T("bulk_first") == "3" && T("bulk_dropped") == "2",
                "bulk: a default fake fed 1026 entries holds 1024 (MirrorJournal.DefaultCapacity), the first retained is sequence 3, and 2 are dropped",
                T("bulk_capacity") + " " + T("bulk_retained") + " " + T("bulk_first") + " " + T("bulk_dropped"));
            var tick = 0;
            var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
            var tracker = new MirrorJournalTracker(journal);
            for (var n = 0; n < 1026; n++)
                journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Move" }, Array.Empty<string>(), new[] { "u-bulk" }, Array.Empty<long>()) { DocumentPath = a });
            string Read(long? after, bool peek) => RevitJournalQuery.RawRead(tracker, true, snapshot, a, new MirrorJournalReadOptions(after, peek, 1));
            var production = new Dictionary<string, string>();
            production["bulk_r0"] = Read(0, true);
            production["bulk_r2"] = Read(2, true);
            production["bulk_read"] = Read(null, false);
            Check(journal.Capacity == MirrorJournal.DefaultCapacity && journal.Dropped == 2 && journal.FirstRetainedSequence == 3 && journal.Entries().Count == 1024,
                "bulk: the Core journal at DefaultCapacity fed the same 1026 records holds 1024 from sequence 3 and dropped 2",
                journal.Capacity + " " + journal.Dropped + " " + journal.FirstRetainedSequence + " " + journal.Entries().Count);
            Compare("bulk", MirrorJournal.DefaultCapacity, production, new[]
            {
                ("bulk_r0", "dropped=2 after=0 complete=false since=0 entries=[3]", "a peek after sequence 0: sequences 1 and 2 dropped, FirstRetainedSequence 3 > after + 1, incomplete"),
                ("bulk_r2", "dropped=2 after=2 complete=true since=0 entries=[3]", "a peek after sequence 2: FirstRetainedSequence 3 = after + 1, complete"),
                ("bulk_read", "dropped=2 after=0 complete=false since=0 entries=[3]", "the first read (no baseline, after 0): incomplete"),
            });
        }

        // The smallest capacity: capacity 1 and two external moves keep only sequence 2 and count 1 dropped.
        void CapacityOne()
        {
            MirrorSnapshot snapshot;
            try { snapshot = Snap(T("one_snapshot")); }
            catch (Exception error) when (error is JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException) { Check(false, "capacity 1: the fake's query.snapshot readback parses as a snapshot", error.Message); return; }
            Check(T("one_capacity") == "1" && T("one_retained") == "1" && T("one_dropped") == "1", "capacity 1: two external moves leave one entry held and one dropped",
                T("one_capacity") + " " + T("one_retained") + " " + T("one_dropped"));
            var tick = 0;
            var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick), 1);
            var tracker = new MirrorJournalTracker(journal);
            void Record(string name, string[] modified, bool addIn = false) =>
                journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { name }, addIn ? new[] { "x" } : Array.Empty<string>(), modified, Array.Empty<long>(), addIn) { DocumentPath = a });
            Record("BIM Agentic OS wall.create", Array.Empty<string>(), addIn: true);
            Record("Move", new[] { T("one_wall") });
            Record("Move", new[] { T("one_wall") });
            string Read(long? after, bool peek) => RevitJournalQuery.RawRead(tracker, true, snapshot, a, new MirrorJournalReadOptions(after, peek));
            var production = new Dictionary<string, string>();
            production["one_r1"] = Read(1, true);
            production["one_r0"] = Read(0, true);
            production["one_read"] = Read(null, false);
            Check(journal.Capacity == 1 && journal.Dropped == 1 && journal.FirstRetainedSequence == 2 && journal.Entries().Count == 1,
                "capacity 1: the Core journal with capacity 1 fed the same moves holds sequence 2 and dropped 1",
                journal.Capacity + " " + journal.Dropped + " " + journal.FirstRetainedSequence + " " + journal.Entries().Count);
            Compare("capacity 1", 1, production, new[]
            {
                ("one_r1", "dropped=1 after=1 complete=true since=0 entries=[2]", "a peek after sequence 1: FirstRetainedSequence 2 = after + 1, complete"),
                ("one_r0", "dropped=1 after=0 complete=false since=0 entries=[2]", "a peek after sequence 0: sequence 1 dropped, incomplete"),
                ("one_read", "dropped=1 after=0 complete=false since=0 entries=[2]", "the first read (no baseline, after 0): incomplete"),
            });
        }

        BulkDefaultCapacity();
        CapacityOne();
    }

    // Snapshot pairs (MirrorSnapshot.ToJson shape) for the differ's field branches the fake's own snapshots never reach.
    static (string Name, string Before, string After)[] DiffCases()
    {
        const string l1 = "11111111-1111-4111-8111-111111111111", l2 = "22222222-2222-4222-8222-222222222222";
        const string e1 = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", e2 = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", e3 = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
        static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string E(string id, string kind, double[] geometry, params string[] dependencies) => "{\"Id\":\"" + id + "\",\"Kind\":\"" + kind + "\",\"GeometryMetres\":[" + string.Join(",", geometry.Select(N))
            + "],\"Dependencies\":[" + string.Join(",", dependencies.Select(dependency => "\"" + dependency + "\"")) + "],\"Provenance\":{\"Source\":\"revit-snapshot\",\"Approval\":\"observed\"}}";
        static string S(string id, string uid, string category) => "{\"id\":\"" + id + "\",\"unique_id\":\"" + uid + "\",\"category\":\"" + category + "\"}";
        static string U(string category, string uid, string reason, string detail) => "{\"category\":\"" + category + "\",\"unique_id\":\"" + uid + "\",\"reason\":\"" + reason + "\",\"detail\":\"" + detail + "\"}";
        static string Snap(string[] elements, string[] sources, params string[] unmapped) => "{\"snapshot_version\":1,\"document_unique_id\":\"" + DocumentUid + "\",\"units\":\"m\",\"elements\":[" + string.Join(",", elements)
            + "],\"sources\":[" + string.Join(",", sources) + "],\"unmapped\":[" + string.Join(",", unmapped) + "]}";
        double[] wall = { 0, 0, 5, 0, 3, 0.2 };
        var level = E(l1, "level", new double[] { 0 });
        var levelSource = S(l1, "u-level", "level");
        return new[]
        {
            ("mapped_to_unmapped", Snap(new[] { level, E(e1, "wall", wall, l1) }, new[] { levelSource, S(e1, "u-wall", "wall") }),
                Snap(new[] { level }, new[] { levelSource }, U("wall", "u-wall", "curved_wall_unsupported", "curved wall"))),
            ("kind_change", Snap(new[] { E(e1, "wall", wall, l1) }, new[] { S(e1, "u-1", "wall") }), Snap(new[] { E(e1, "door", new double[] { 1, 0, 2, 0, 0, 2.1 }, l1, e2) }, new[] { S(e1, "u-1", "door") })),
            ("kind_case", Snap(new[] { E(e1, "Wall", wall, l1) }, new[] { S(e1, "u-1", "wall") }), Snap(new[] { E(e1, "Wall", new double[] { 0.5, 0, 5, 0, 3, 0.2 }, l1) }, new[] { S(e1, "u-1", "wall") })),
            ("geometry_length", Snap(new[] { E(e1, "wall", wall, l1) }, new[] { S(e1, "u-1", "wall") }), Snap(new[] { E(e1, "wall", new double[] { 0, 0, 5, 0, 3 }, l1) }, new[] { S(e1, "u-1", "wall") })),
            ("unnamed_geometry", Snap(new[] { E(e1, "beam", new double[] { 1, 2 }), E(e2, "wall", new double[] { 0, 0, 5, 0, 3 }) }, new[] { S(e1, "u-1", "beam"), S(e2, "u-2", "wall") }),
                Snap(new[] { E(e1, "beam", new double[] { 1, 3 }), E(e2, "wall", new double[] { 0, 1, 5, 0, 3 }) }, new[] { S(e1, "u-1", "beam"), S(e2, "u-2", "wall") })),
            ("dependencies", Snap(new[] { E(e1, "wall", wall, l1), E(e2, "wall", wall, l1) }, new[] { S(e1, "u-1", "wall"), S(e2, "u-2", "wall") }),
                Snap(new[] { E(e1, "wall", wall, l2, l1), E(e2, "wall", wall, l1.ToUpperInvariant()) }, new[] { S(e1, "u-1", "wall"), S(e2, "u-2", "wall") })),
            ("reason_detail", Snap(Array.Empty<string>(), Array.Empty<string>(), U("wall", "u-5", "curved_wall_unsupported", "curved wall"), U("floor", "u-6", "no_bim_ir_kind", "first")),
                Snap(Array.Empty<string>(), Array.Empty<string>(), U("wall", "u-5", "curtain_wall_unsupported", "curved wall"), U("floor", "u-6", "no_bim_ir_kind", "second"))),
            ("category", Snap(new[] { E(e1, "wall", wall, l1) }, new[] { S(e1, "u-1", "wall") }), Snap(new[] { E(e1, "wall", wall, l1) }, new[] { S(e1, "u-1", "generic") })),
            ("uid_case_duplicates_blank", Snap(new[] { E(e1, "wall", wall, l1), E(e2, "wall", wall, l1) }, new[] { S(e1, "U-B", "wall"), S(e2, "a-1", "wall") }, U("wall", "", "missing_data", "no id")),
                Snap(new[] { E(e1, "wall", new double[] { 0, 0, 6, 0, 3, 0.2 }, l1), E(e3, "wall", wall, l1) }, new[] { S(e1, "u-b", "wall"), S(e3, "A-0", "wall") },
                    U("wall", "u-b", "curved_wall_unsupported", "duplicate of a mapped UniqueId"), U("wall", " ", "missing_data", "blank id"))),
            ("tolerance_edge", Snap(new[] { E(e1, "wall", new double[] { 1, 2, 0, 0, 3, 0.2 }, l1) }, new[] { S(e1, "u-1", "wall") }),
                Snap(new[] { E(e1, "wall", new double[] { 1.000001, 2.000002, 0.000001, 0.0000011, 3.0000009, 0.2000011 }, l1) }, new[] { S(e1, "u-1", "wall") })),
        };
    }

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The journal.read reply the production code path (JournalRecorder.Read -> RevitJournalQuery.RawRead) gives for the
    // same session: the CLI's two wall.create writes inside the add-in dispatch (skipped), then the three external
    // commits move B, create C, delete A, recorded at the fake's deterministic clock; peek read after sequence 0.
    static string ProductionJournal(string documentPath, string snapshotResponse, string uidB, string uidC, long deletedA)
    {
        var tick = 0;
        var journal = new MirrorJournal(() => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(++tick));
        foreach (var _ in new[] { 1, 2 }) journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "BIM Agentic OS wall.create" }, new[] { "x" }, Array.Empty<string>(), Array.Empty<long>(), true) { DocumentPath = documentPath });
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Move" }, Array.Empty<string>(), new[] { uidB }, Array.Empty<long>()) { DocumentPath = documentPath });
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Wall" }, new[] { uidC }, Array.Empty<string>(), Array.Empty<long>()) { DocumentPath = documentPath });
        journal.Record(new MirrorJournalChange(DocumentUid, "TransactionCommitted", new[] { "Delete" }, Array.Empty<string>(), Array.Empty<string>(), new[] { deletedA }) { DocumentPath = documentPath });
        using var parsed = JsonDocument.Parse(snapshotResponse);
        var snapshot = MirrorDrift.ParseSnapshot(parsed.RootElement.GetProperty("readback").GetRawText());
        return RevitJournalQuery.RawRead(new MirrorJournalTracker(journal), true, snapshot, documentPath, new MirrorJournalReadOptions(0, true));
    }

    // A reply re-serialized uniformly (member order kept) with report.diff removed.
    static string WithoutDiff(string reply) => Uniform(reply, dropDiff: true);

    // A reply re-serialized uniformly (member order kept, one escaping), optionally without report.diff.
    static string Uniform(string reply, bool dropDiff = false)
    {
        try
        {
            using var parsed = JsonDocument.Parse(reply);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                void Copy(JsonElement element, bool inReport)
                {
                    if (element.ValueKind != JsonValueKind.Object) { element.WriteTo(writer); return; }
                    writer.WriteStartObject();
                    foreach (var member in element.EnumerateObject())
                    {
                        if (dropDiff && inReport && member.Name == "diff") continue;
                        writer.WritePropertyName(member.Name);
                        Copy(member.Value, member.Name == "report");
                    }
                    writer.WriteEndObject();
                }
                Copy(parsed.RootElement, false);
            }
            return Utf8.GetString(buffer.ToArray());
        }
        catch (JsonException) { return "<not JSON> " + reply; }
    }

    static long? Member(string reply, string member)
    {
        try { using var parsed = JsonDocument.Parse(reply); return parsed.RootElement.TryGetProperty("report", out var report) && report.TryGetProperty(member, out var value) && value.TryGetInt64(out var number) ? number : null; }
        catch (JsonException) { return null; }
    }

    static bool? Flag(string reply, string member)
    {
        try { using var parsed = JsonDocument.Parse(reply); return parsed.RootElement.TryGetProperty("report", out var report) && report.TryGetProperty(member, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null; }
        catch (JsonException) { return null; }
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
