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

    // P2-33: writeProgressOnly runs only the per-OS write-progress section (the macOS CI step; Program.cs --write-progress-only).
    public static void Run(Action<bool, string, string> checker, bool writeProgressOnly = false)
    {
        check = checker;
        try { Checks(writeProgressOnly); }
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

    static void Checks(bool writeProgressOnly)
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
        if (writeProgressOnly) { RunWriteProgressPerOsLoop(root, dotnet, pwsh, cliDll, runDirectory); return; }
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
        RunBrokenPipeLoop(root, dotnet, pwsh, cliDll, runDirectory);
        RunMidArgsBreakLoop(root, dotnet, pwsh, cliDll, runDirectory);
        RunWriteProgressLoop(root, dotnet, pwsh, cliDll, runDirectory);
        RunWriteProgressPerOsLoop(root, dotnet, pwsh, cliDll, runDirectory);
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
        // P2-33: the fake's pipe name, read-only (the CLI is given it; in-process clients connect to it).
        public string Pipe => pipe;

        public Harness(string root, string dotnet, string pwsh, string cliDll, string runDirectory, string documentName, int maxRequests, string? externalEdits = null, string? recorderFailures = null, string? dropReplyCommand = null, bool noColor = false, string? breakPipeCommand = null, int? breakAfterBytes = null)
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
            // Test-only (P2-30): a request of this command has its connection closed before its line is fully read.
            if (breakPipeCommand is not null) { serverStart.ArgumentList.Add("-BreakPipeCommand"); serverStart.ArgumentList.Add(breakPipeCommand); }
            // Test-only (P2-31): a request line longer than this many bytes has its connection closed after that many.
            if (breakAfterBytes is { } breakAfter) { serverStart.ArgumentList.Add("-BreakAfterBytes"); serverStart.ArgumentList.Add(breakAfter.ToString(CultureInfo.InvariantCulture)); }
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

        // P2-29: three scheduled ids, the middle one (mm-id) matched by a succeeded, non-dry-run query.levels: the gate line
        // names only the two never matched, sorted ordinally (aa-id,zz-id), and never the matched mm-id.
        using var three = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Failure-Three.rvt", 2, "zz-id", "zz-id:1,mm-id:1,aa-id:1");
        if (!three.WaitReady(out var threeNotReady)) { Check(false, "fake add-in pipe ready with -RecorderFailures zz-id:1,mm-id:1,aa-id:1", threeNotReady); return; }
        var matched = three.Command("mm-id", "query.levels", _ => { });
        var threeReply = three.Journal(null);
        var t3 = three.End();
        Check(Succeeded(matched), "with -RecorderFailures zz-id:1,mm-id:1,aa-id:1 the query.levels named mm-id succeeds (not a dry run)", R(matched));
        Check(FailureShape(threeReply) == "failures=1 since=1 complete=false existed=false entries=[]", "with -RecorderFailures zz-id:1,mm-id:1,aa-id:1 the journal.read after mm-id reports exactly its one injected failure", FailureShape(threeReply) + " | " + Head(threeReply));
        Check(three.Received().SequenceEqual(new[] { "mm-id", JournalWire }), "with -RecorderFailures zz-id:1,mm-id:1,aa-id:1 the fake received mm-id then the journal.read", string.Join(",", three.Received()));
        Check(t3.Exited && t3.Exit == 1 && t3.Error == GateLine("zz-id", "aa-id") && !t3.Error.Contains("mm-id", StringComparison.Ordinal),
            "with -RecorderFailures zz-id:1,mm-id:1,aa-id:1 and mm-id matched the fake exits exactly 1 and its whole stderr is the gate line naming aa-id,zz-id only (the matched mm-id excluded)", "exited " + t3.Exited + " exit " + t3.Exit + " " + t3.Error);
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
        // P2-29: the exact code per OS, observed by a deliberate probe red (run 44240630631): the CLI answers no_response
        // (exit 1, field pipe_name, "pipe closed without a response line") on Windows (named pipe) and Linux (Unix socket).
        // Both OSes return the same code, so one constant pins each; pipe_io_error (the P2-28 alternative) is now refused.
        const string droppedCode = "no_response";
        Check(move.Exit == 1 && Status(move.Out) == "failed" && Text(move.Out, "code") == droppedCode && Text(move.Out, "field") == "pipe_name"
            && Text(move.Out, "detail") == "pipe closed without a response line",
            "the external element.move of A named p2-28-move-a gets no reply (its connection is dropped after the write): exit 1, failed with exactly " + droppedCode + " on " + (OperatingSystem.IsWindows() ? "Windows" : "Unix") + ", field pipe_name",
            "observed code " + (Text(move.Out, "code") ?? "<null>") + " | " + R(move));
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

    // P2-30: a pipe broken before the reply is read. With -BreakPipeCommand element.move the fake closes each element.move
    // connection as soon as it has read that envelope's own "command":"element.move","document": member, before the rest of
    // the line (document, operation_id, protocol_version, newline) and without a reply; the request never reaches the
    // executor. Each move is a large request (100 element uids, a 128-character operation id after the command member), so
    // a long tail stays unread. First a command.batch whose one step is an element.move (its step's "command" is followed
    // by "operation_id", not "document") is served normally: logged by its operation id, answered by the fake
    // (command_unknown, the fake has no batch). Then ten moves, the first one's id scheduled with -RecorderFailures <id>:2:
    // each CLI call fails (exit 1) with one code, field and detail; nothing ran, so the id is never matched (the fake's
    // gate line names it, exit 1), the log holds broken:element.move for each, the journal has no entry and no failure,
    // equal to RevitJournalQuery.RawRead over one Core tracker that saw only wall A, and the snapshot after the moves
    // equals the one before.
    const int BrokenMoveCount = 10;
    static readonly string[] BrokenMoves = Enumerable.Range(1, BrokenMoveCount).Select(i => ("p2-30-move-" + i.ToString("D2", CultureInfo.InvariantCulture) + "-").PadRight(128, 'x')).ToArray();
    static readonly string BrokenMoveA = BrokenMoves[0];
    const string BrokenLog = "broken:element.move";
    const string BrokenBatch = "p2-30-batch";
    const string BrokenMarker = "\"command\":\"element.move\",\"document\":";
    static readonly string[] BrokenRequests = new[] { "p2-30-levels", "p2-30-wall-types", "p2-30-wall-a", "p2-30-snapshot-0", JournalWire, BrokenBatch }
        .Concat(Enumerable.Repeat(BrokenLog, BrokenMoveCount)).Concat(new[] { "p2-30-snapshot-1", JournalWire }).ToArray();

    static void BigMoveArgs(Utf8JsonWriter w, string uid)
    {
        w.WriteStartArray("element_uids");
        w.WriteStringValue(uid);
        for (var i = 1; i < RevitTransformRules.MaxTargets; i++) w.WriteStringValue(string.Create(CultureInfo.InvariantCulture, $"e2e2{i:x4}-0000-4000-8000-00000000{i:x4}-{i:x8}"));
        w.WriteEndArray();
        w.WriteStartArray("vector_mm"); w.WriteNumberValue(500); w.WriteNumberValue(0); w.WriteNumberValue(0); w.WriteEndArray();
    }

    static void RunBrokenPipeLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "pipe broken before reply: " + name, detail);
        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Pipe-Broken.rvt", BrokenRequests.Length, string.Join(",", BrokenMoves), BrokenMoveA + ":2", breakPipeCommand: "element.move");
        var document = h.Document;
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready with -BreakPipeCommand element.move", notReady); return; }
        var levels = h.Command("p2-30-levels", "query.levels", _ => { });
        var types = h.Command("p2-30-wall-types", "query.wall_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed", R(levels) + " | " + R(types)); return; }
        var wallA = h.Command("p2-30-wall-a", "wall.create", w => WallArgs(w, Pick(levels.Out, "levels", "name", "Level 1"), Pick(types.Out, "wall_types", "name", WallType), 0, 0, 5000, 0));
        if (!Succeeded(wallA)) { Check(false, "wall.create of A succeeds through the CLI", R(wallA)); return; }
        var uidA = Created(wallA.Out);
        var snapshot0 = h.Command("p2-30-snapshot-0", "query.snapshot", _ => { });
        if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return; }
        var j0 = h.Journal(null, peek: false);

        // A command.batch with one element.move step: its wire line holds "command":"element.move" (the step's) but not the
        // anchored marker, so the fake reads it whole and answers it (the fake has no batch: command_unknown).
        var batch = h.Command(BrokenBatch, "command.batch", w =>
        {
            w.WriteStartArray("steps");
            w.WriteStartObject();
            w.WriteString("operation_id", BrokenBatch + "-step-1");
            w.WriteString("command", "element.move");
            w.WriteStartObject("args"); MoveArgs(w, uidA); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
        });
        var batchFile = Directory.GetFiles(h.Scratch, "*-envelope-command.batch.json").Single();
        var batchWire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(File.ReadAllText(batchFile, Utf8)));
        Check(batchWire.Contains("\"command\":\"element.move\"", StringComparison.Ordinal) && !batchWire.Contains(BrokenMarker, StringComparison.Ordinal),
            "the command.batch wire line holds its step's \"command\":\"element.move\" but not the envelope's own command member marker", Head(batchWire));
        Check(batch.Exit == 1 && Status(batch.Out) == "failed" && Text(batch.Out, "code") == "command_unknown" && Text(batch.Out, "field") == "command" && Text(batch.Out, "detail") == "command.batch",
            "with -BreakPipeCommand element.move the command.batch whose step is an element.move is served normally, not broken (the fake's own answer: failed command_unknown, field command, detail command.batch)", R(batch));

        var moves = BrokenMoves.Select(id => h.Command(id, "element.move", w => BigMoveArgs(w, uidA))).ToArray();
        // The wire line the CLI sent for each move: RevitCommandProtocol.WireLine of the very envelope file it was given.
        var moveFiles = Directory.GetFiles(h.Scratch, "*-envelope-element.move.json").Order(StringComparer.Ordinal).ToArray();
        Check(moveFiles.Length == BrokenMoveCount, "one envelope file per broken move", moveFiles.Length.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < moveFiles.Length; i++)
        {
            var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(File.ReadAllText(moveFiles[i], Utf8)));
            var at = wire.IndexOf(BrokenMarker, StringComparison.Ordinal);
            var tail = at < 0 ? -1 : wire.Length - at - BrokenMarker.Length;
            Check(wire.Length > 4500 && wire.Length <= RevitCommandProtocol.MaxRequestChars && at >= 0 && at == wire.LastIndexOf(BrokenMarker, StringComparison.Ordinal) && tail >= 250
                && wire.Contains("\"operation_id\":\"" + BrokenMoves[i] + "\"", StringComparison.Ordinal),
                "broken move " + (i + 1) + "'s wire line is large (over 4500 characters, within the 65536 cap), names its 128-character operation id, holds the anchored marker once and leaves at least 250 characters after it",
                "length " + wire.Length + " tail " + tail);
        }
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        string Seen((int Exit, string Out, string Err) r) => "exit=" + r.Exit + " status=" + (Status(r.Out) ?? "<null>") + " code=" + (Text(r.Out, "code") ?? "<null>") + " field=" + (Text(r.Out, "field") ?? "<null>") + " detail=" + (Text(r.Out, "detail") ?? "<null>");
        // P2-30: the exact failure per OS, observed by a deliberate probe red (run 44299538961, three moves): the CLI
        // answers pipe_io_error, field pipe_name, detail IOException (the exception type name only), exit 1, on Windows
        // (named pipe) and Linux (Unix socket). Both OSes return the same failure, so one set of constants pins each move.
        const string brokenCode = "pipe_io_error", brokenField = "pipe_name", brokenDetail = "IOException";
        for (var i = 0; i < moves.Length; i++)
            Check(moves[i].Exit == 1 && Status(moves[i].Out) == "failed" && Text(moves[i].Out, "code") == brokenCode && Text(moves[i].Out, "field") == brokenField && Text(moves[i].Out, "detail") == brokenDetail,
                "broken element.move " + (i + 1) + " of " + BrokenMoveCount + " (its connection closed before its line was read): exit 1, failed with exactly " + brokenCode + ", field " + brokenField + ", detail " + brokenDetail + " on " + os,
                Seen(moves[i]) + " | " + R(moves[i]));
        var snapshot1 = h.Command("p2-30-snapshot-1", "query.snapshot", _ => { });
        if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the broken moves succeeds", R(snapshot1)); return; }
        Check(Readback(snapshot1.Out) == Readback(snapshot0.Out), "the query.snapshot after the broken moves equals the one before (no move ran)", Head(Readback(snapshot1.Out)));
        var j1 = h.Journal(0);

        var (exited, exit, error) = h.End();
        Check(exited && exit == 1 && error == GateLine(BrokenMoveA),
            "the fake add-in exits exactly 1 and its whole stderr is the gate line naming the broken move's id (never run, so never matched)", "exited " + exited + " exit " + exit + " " + error);
        var received = h.Received();
        Check(received.SequenceEqual(BrokenRequests), "the fake add-in logged the expected requests in order (broken:element.move for each move, no move operation id)", string.Join(",", received));

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
        var p1 = RevitJournalQuery.RawRead(tracker, true, s1, document, new MirrorJournalReadOptions(0, true));
        Check(FailureShape(j0) == "failures=0 since=0 complete=true existed=false entries=[]", "pipe reply b0: the first read without peek, no failure", FailureShape(j0) + " | " + Head(j0));
        Check(FailureShape(j1) == "failures=0 since=0 complete=true existed=true entries=[]", "pipe reply b1: a peek after sequence 0 once the moves were broken: no failure injected, no entry, complete", FailureShape(j1) + " | " + Head(j1));
        Check(Uniform(j0) == Uniform(p0), "pipe reply b0 equals RevitJournalQuery.RawRead over one Core tracker, member for member (diff included)", Uniform(j0) + " vs " + Uniform(p0));
        Check(Uniform(j1) == Uniform(p1), "pipe reply b1 equals RevitJournalQuery.RawRead over one Core tracker that saw no move and no failure, member for member (diff included)", Uniform(j1) + " vs " + Uniform(p1));
    }

    // P2-31: a pipe broken mid-args. With -BreakAfterBytes 4096 the fake reads at most 4096 bytes of each request and
    // closes a connection whose line is not ended within them, without a reply; the request never reaches the executor.
    // The broken request is a floor.create with a 1000-point boundary (a regular polygon of radius 10 km, shortest
    // round-trip coordinates), a wire line of over 30000 ASCII characters in which byte 4096 lies inside the args member,
    // so the fake stops mid-args with the rest of the line unread or still being written. Measured counts: the fake logs
    // the bytes it read, the InBufferSize its server end reports (on Windows the requested quota, 0, not a measured
    // buffer: diagnostic only) and, 200 ms after it stopped reading, the bytes queued in its server end (PeekNamedPipe /
    // FIONREAD); the test measures the wire bytes the CLI writes (line plus Environment.NewLine). The byte counts differ
    // per OS (CRLF on Windows, and Math.Cos/Math.Sin give coordinates whose shortest round-trip text differs in length);
    // the default send buffer of a Unix socket is reported as a diagnostic only.
    // A control run without the option serves the same floor (succeeded, one created), so the request would run. The
    // broken run: walls A, snapshot 0, a read without peek, the broken floor (its id an external edit with a scheduled
    // recorder failure), snapshot 1 and a peek after sequence 0: the CLI fails with one code, field and detail, exit 1;
    // snapshot 1 equals snapshot 0; the journal has no entry and no failure (RawRead over a tracker that saw only wall A);
    // the floor's id is never matched (the gate line names it, the fake exits 1).
    const int MidArgsBreakBytes = 4096;
    const string MidArgsFloor = "p2-31-floor";
    const string MidArgsLogPrefix = "broken-after:";
    static readonly string[] MidArgsRequestsBefore = { "p2-31-levels", "p2-31-wall-types", "p2-31-floor-types", "p2-31-wall-a", "p2-31-snapshot-0", JournalWire };
    static readonly string[] MidArgsRequestsAfter = { "p2-31-snapshot-1", JournalWire };

    static void BigFloorArgs(Utf8JsonWriter w, string level, string type)
    {
        w.WriteString("level", level);
        w.WriteString("type", type);
        w.WriteStartArray("boundary");
        for (var i = 0; i < RevitCommandCatalogue.MaxBoundaryPoints; i++)
        {
            var angle = 2 * Math.PI * i / RevitCommandCatalogue.MaxBoundaryPoints;
            w.WriteStartArray(); w.WriteNumberValue(1e7 * Math.Cos(angle)); w.WriteNumberValue(1e7 * Math.Sin(angle)); w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    // The default send buffer of a new Unix socket (what the CLI's NamedPipeClientStream gets off Windows); -1 on Windows.
    static int UnixSendBuffer()
    {
        if (OperatingSystem.IsWindows()) return -1;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        return socket.SendBufferSize;
    }

    static void RunMidArgsBreakLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "pipe broken mid-args: " + name, detail);
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

        // Control: the same large floor.create served by a fake without the option runs (succeeded, one element created).
        using (var c = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Pipe-MidArgs-Control.rvt", 3))
        {
            if (!c.WaitReady(out var controlNotReady)) { Check(false, "control fake add-in pipe ready (no option)", controlNotReady); return; }
            var controlLevels = c.Command("p2-31-control-levels", "query.levels", _ => { });
            var controlTypes = c.Command("p2-31-control-floor-types", "query.floor_types", _ => { });
            if (!Succeeded(controlLevels) || !Succeeded(controlTypes)) { Check(false, "control: query.levels and query.floor_types succeed", R(controlLevels) + " | " + R(controlTypes)); return; }
            var controlFloor = c.Command("p2-31-control-floor", "floor.create", w => BigFloorArgs(w, Pick(controlLevels.Out, "levels", "name", "Level 1"), Pick(controlTypes.Out, "floor_types", "name", "Generic 150mm")));
            Check(Succeeded(controlFloor) && Created(controlFloor.Out).Length > 0, "control: without -BreakAfterBytes the large floor.create runs (succeeded, one element created)", R(controlFloor));
            var (controlExited, controlExit, controlError) = c.End();
            Check(controlExited && controlExit == 0 && controlError.Length == 0, "control: the fake add-in exits 0 with an empty stderr", "exited " + controlExited + " exit " + controlExit + " " + controlError);
        }

        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Pipe-MidArgs.rvt", MidArgsRequestsBefore.Length + 1 + MidArgsRequestsAfter.Length, MidArgsFloor, MidArgsFloor + ":1", breakAfterBytes: MidArgsBreakBytes);
        var document = h.Document;
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready with -BreakAfterBytes " + MidArgsBreakBytes, notReady); return; }
        var levels = h.Command("p2-31-levels", "query.levels", _ => { });
        var types = h.Command("p2-31-wall-types", "query.wall_types", _ => { });
        var floorTypes = h.Command("p2-31-floor-types", "query.floor_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(types) || !Succeeded(floorTypes)) { Check(false, "query.levels, query.wall_types and query.floor_types succeed (lines under 4096 bytes are served)", R(levels) + " | " + R(types) + " | " + R(floorTypes)); return; }
        var level = Pick(levels.Out, "levels", "name", "Level 1");
        var wallA = h.Command("p2-31-wall-a", "wall.create", w => WallArgs(w, level, Pick(types.Out, "wall_types", "name", WallType), 0, 0, 5000, 0));
        if (!Succeeded(wallA)) { Check(false, "wall.create of A succeeds through the CLI", R(wallA)); return; }
        var snapshot0 = h.Command("p2-31-snapshot-0", "query.snapshot", _ => { });
        if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return; }
        var j0 = h.Journal(null, peek: false);
        var floor = h.Command(MidArgsFloor, "floor.create", w => BigFloorArgs(w, level, Pick(floorTypes.Out, "floor_types", "name", "Generic 150mm")));
        var snapshot1 = h.Command("p2-31-snapshot-1", "query.snapshot", _ => { });
        if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the broken floor succeeds", R(snapshot1)); return; }
        var j1 = h.Journal(0);
        var (exited, exit, error) = h.End();
        var received = h.Received();

        // Measured counts: the wire line the CLI wrote (WireLine of the very envelope file plus Environment.NewLine), where
        // its args member lies, the bytes the fake read and the buffers.
        var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(File.ReadAllText(Directory.GetFiles(h.Scratch, "*-envelope-floor.create.json").Single(), Utf8)));
        var wireBytes = Utf8.GetByteCount(wire + Environment.NewLine);
        const string argsOpen = "\"Command\":{\"args\":";
        var argsStart = wire.IndexOf(argsOpen, StringComparison.Ordinal) + argsOpen.Length;
        var argsEnd = wire.IndexOf(",\"command\":\"floor.create\",\"document\":", StringComparison.Ordinal);
        var brokenLine = received.Length == MidArgsRequestsBefore.Length + 1 + MidArgsRequestsAfter.Length ? received[MidArgsRequestsBefore.Length] : "";
        var parts = brokenLine.Split(':');
        var read = parts.Length == 4 && parts[0] + ":" == MidArgsLogPrefix && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : -1;
        var queued = parts.Length == 4 && long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var q) ? q : long.MinValue;
        var unread = read < 0 ? -1 : wireBytes - read;
        var measured = "os=" + os + " wire_chars=" + wire.Length + " wire_bytes=" + wireBytes + " args=[" + argsStart + "," + argsEnd + ") read=" + read + " unread=" + unread
            + " queued=" + (parts.Length == 4 ? parts[3] : "<none>") + " unqueued=" + (queued == long.MinValue ? "<none>" : (unread - queued).ToString(CultureInfo.InvariantCulture))
            + " in_buffer_size=" + (parts.Length == 4 ? parts[2] : "<none>") + " unix_send_buffer=" + UnixSendBuffer() + " cap_bytes=" + (RevitCommandProtocol.MaxRequestChars + Environment.NewLine.Length);
        Check(wire.Length > 30000 && wire.Length <= RevitCommandProtocol.MaxRequestChars && wireBytes == wire.Length + Environment.NewLine.Length && argsStart > argsOpen.Length && argsEnd > argsStart
            && argsStart < MidArgsBreakBytes && MidArgsBreakBytes < argsEnd,
            "the broken floor.create's wire line is large (over 30000 ASCII characters, within the 65536 cap) and byte " + MidArgsBreakBytes + " lies inside its args member", measured);
        Check(read == MidArgsBreakBytes, "the fake read exactly " + MidArgsBreakBytes + " bytes of the floor.create line before closing it (measured from its log)", measured + " log " + brokenLine);
        string Seen((int Exit, string Out, string Err) x) => "exit=" + x.Exit + " status=" + (Status(x.Out) ?? "<null>") + " code=" + (Text(x.Out, "code") ?? "<null>") + " field=" + (Text(x.Out, "field") ?? "<null>") + " detail=" + (Text(x.Out, "detail") ?? "<null>");
        // P2-31: measured by a deliberate probe red (run 44424268750): 200 ms after the fake stopped reading, the bytes
        // queued in its server end equal the whole unread remainder on both OSes (Linux 35628 of 35628, FIONREAD on the
        // socket; Windows 35619 of 35619, PeekNamedPipe TotalBytesAvail). On Linux that is the remainder sitting in the
        // socket: the CLI's write completed (its send buffer is larger than any request under the 65536-character cap).
        // On Windows the inbound quota is 0, so those bytes cannot be buffered: they are the CLI's still-pending write,
        // which PeekNamedPipe counts as available. Neither measurement shows bytes the client had not yet handed to the
        // OS, so "unread minus queued" is 0 on both and is pinned as observed. Other OSes have not been probed (macOS does
        // not run mirror-journal) and fail here until one is.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Check(read == MidArgsBreakBytes && unread > 0 && queued == unread,
                os + ": 200 ms after the break the bytes queued in the fake's server end (measured) equal the whole unread remainder of the floor.create line", measured);
        else Check(false, os + ": the bytes queued at a mid-args break have not been observed by a probe on this OS", measured);
        // P2-31: the exact failure, observed by the same probe: the CLI answers pipe_io_error, field pipe_name, detail
        // IOException (the exception type name only), exit 1, on Windows and Linux alike, so one set of constants pins it.
        const string midArgsCode = "pipe_io_error", midArgsField = "pipe_name", midArgsDetail = "IOException";
        Check(floor.Exit == 1 && Status(floor.Out) == "failed" && Text(floor.Out, "code") == midArgsCode && Text(floor.Out, "field") == midArgsField && Text(floor.Out, "detail") == midArgsDetail,
            "the floor.create broken after " + MidArgsBreakBytes + " bytes (mid-args): exit 1, failed with exactly " + midArgsCode + ", field " + midArgsField + ", detail " + midArgsDetail + " on " + os,
            Seen(floor) + " | " + R(floor));

        Check(Readback(snapshot1.Out) == Readback(snapshot0.Out), "the query.snapshot after the broken floor equals the one before (nothing ran)", Head(Readback(snapshot1.Out)));
        Check(exited && exit == 1 && error == GateLine(MidArgsFloor),
            "the fake add-in exits exactly 1 and its whole stderr is the gate line naming the broken floor's id (never run, so never matched)", "exited " + exited + " exit " + exit + " " + error);
        var expected = MidArgsRequestsBefore.Append(brokenLine).Concat(MidArgsRequestsAfter).ToArray();
        Check(brokenLine.StartsWith(MidArgsLogPrefix, StringComparison.Ordinal) && received.SequenceEqual(expected),
            "the fake add-in logged the expected requests in order (one broken-after line for the floor, no floor operation id)", string.Join(",", received));

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
        var p1 = RevitJournalQuery.RawRead(tracker, true, s1, document, new MirrorJournalReadOptions(0, true));
        Check(FailureShape(j0) == "failures=0 since=0 complete=true existed=false entries=[]", "pipe reply m0: the first read without peek, no failure", FailureShape(j0) + " | " + Head(j0));
        Check(FailureShape(j1) == "failures=0 since=0 complete=true existed=true entries=[]", "pipe reply m1: a peek after sequence 0 once the floor was broken: no failure injected, no entry, complete", FailureShape(j1) + " | " + Head(j1));
        Check(Uniform(j0) == Uniform(p0), "pipe reply m0 equals RevitJournalQuery.RawRead over one Core tracker, member for member (diff included)", Uniform(j0) + " vs " + Uniform(p0));
        Check(Uniform(j1) == Uniform(p1), "pipe reply m1 equals RevitJournalQuery.RawRead over one Core tracker that saw no floor and no failure, member for member (diff included)", Uniform(j1) + " vs " + Uniform(p1));
    }

    // P2-32: client-side write progress at the P2-31 mid-args break. The fake's queued count (P2-31) cannot tell on
    // Windows a pending client write from buffered bytes, so this measures the client: Core's test seam
    // RevitCommandService.WriteProgress ([ThreadStatic], null in production) makes the real pipe transport hand the request
    // to the pipe in WriteProgressChunk-byte pieces and report (bytes handed so far, wire bytes) before the first piece and
    // after each piece whose Write returned. Against a fresh -BreakAfterBytes 4096 fake: the CLI sends the same large
    // floor.create as P2-31 (no seam: one Write), then RevitCommandService.Run sends that very envelope in process with the
    // seam set. Both are broken after 4096 bytes; the seam run's line equals the CLI's (same code, field and detail, exit 1
    // for the CLI), the seam's wire bytes equal WireLine plus Environment.NewLine, and the bytes handed when the fake
    // closed are pinned per OS (see the measured values below). Static pin: no production source assigns the seam, and it is null on the test thread
    // before and after the run.
    const string WriteProgressFloor = "p2-32-floor";
    static readonly string[] WriteProgressRequestsBefore = { "p2-32-levels", "p2-32-floor-types" };
    const string WriteProgressService = "core/Bim.Core/RevitCommandsService.cs";

    static void RunWriteProgressLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "client write progress at a mid-args break: " + name, detail);
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

        // Static pin: the seam is named only in RevitCommandsService.cs among the production sources, declared without an
        // initializer and never assigned there.
        var naming = new List<string>();
        foreach (var top in new[] { "core", "interfaces", "runtimes", "adapters", "evaluation" })
        {
            var directory = Path.Combine(root, top);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (File.ReadAllText(file).Contains("WriteProgress", StringComparison.Ordinal)) naming.Add(relative);
            }
        }
        naming.Sort(StringComparer.Ordinal);
        Check(naming.SequenceEqual(new[] { WriteProgressService }), "static pin: among the production sources only " + WriteProgressService + " names the write-progress seam", string.Join(",", naming));
        var service = File.ReadAllText(Path.Combine(root, WriteProgressService));
        // Comments stripped, the code of RevitCommandsService.cs names the seam exactly twice: its declaration and the one
        // read in WriteRequest. Any assignment (=, +=, -=, ??=) or other use is a third occurrence; a planted self-test
        // shows those forms are caught while a mention in a comment is not.
        static string Code(string source) => System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/|//[^\n]*", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        const string seamDeclaration = "[ThreadStatic] internal static Action<int, int>? WriteProgress;";
        const string seamRead = "var progress = WriteProgress;";
        static int Uses(string source) => System.Text.RegularExpressions.Regex.Matches(Code(source), @"\bWriteProgress\b").Count;
        static bool Pinned(string source) => Uses(source) == 2 && Code(source).Contains(seamDeclaration, StringComparison.Ordinal) && Code(source).Contains(seamRead, StringComparison.Ordinal);
        Check(Pinned(service),
            "static pin: with comments stripped the seam occurs exactly twice in " + WriteProgressService + " (its [ThreadStatic] declaration without an initializer and the one read): never assigned in production source (null unless a test sets it)",
            "code occurrences " + Uses(service));
        var planted = new[] { "\nstatic void P() { WriteProgress += (_, _) => { }; }\n", "\nstatic void P(Action<int, int> x) { WriteProgress ??= x; }\n", "\nstatic void P() { WriteProgress -= null; }\n", "\nstatic void P() { WriteProgress = null; }\n" };
        Check(planted.All(plant => !Pinned(service + plant)) && Pinned(service + "\n// WriteProgress = x;\n/* WriteProgress += y; */\n"),
            "static pin self-test: a planted WriteProgress +=, ??=, -= or = is caught, a mention in a comment is not", string.Join(",", planted.Select(plant => Uses(service + plant))));
        Check(RevitCommandService.WriteProgress is null, "the seam is null on the test thread before the run");

        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Pipe-WriteProgress.rvt", WriteProgressRequestsBefore.Length + 2, breakAfterBytes: MidArgsBreakBytes);
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready with -BreakAfterBytes " + MidArgsBreakBytes, notReady); return; }
        var levels = h.Command("p2-32-levels", "query.levels", _ => { });
        var floorTypes = h.Command("p2-32-floor-types", "query.floor_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(floorTypes)) { Check(false, "query.levels and query.floor_types succeed", R(levels) + " | " + R(floorTypes)); return; }
        var cli = h.Command(WriteProgressFloor, "floor.create", w => BigFloorArgs(w, Pick(levels.Out, "levels", "name", "Level 1"), Pick(floorTypes.Out, "floor_types", "name", "Generic 150mm")));
        var envelope = File.ReadAllText(Directory.GetFiles(h.Scratch, "*-envelope-floor.create.json").Single(), Utf8);
        var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(envelope));
        var wireBytes = Utf8.GetByteCount(wire + Environment.NewLine);

        // The fake's pipe name (Harness keeps it private) and, off Windows, the socket directory Core's client resolves.
        var pipe = typeof(Harness).GetField("pipe", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(h) as string;
        if (pipe is null) { Check(false, "the harness pipe name is readable"); return; }
        // scripts/verify.ps1 gives this process a long TMPDIR, while the fake listens under UnixTemp (sockets: 108-byte
        // limit). .NET caches the pipe socket prefix (PipeStream.s_pipePrefix, from Path.GetTempPath) for the process on
        // first use, so TMPDIR is UnixTemp when that cache is made (forced here by reading it by reflection) and while
        // Core's client runs. Only TMPDIR is restored afterwards: the cached prefix stays UnixTemp for the rest of this
        // process. Order dependency: no earlier check of this process may open a pipe client off Windows (none does), or
        // the prefix is already cached elsewhere and the check below fails by name.
        Check(MidArgsBreakBytes % RevitCommandService.WriteProgressChunk == 0,
            "precondition: the break point (" + MidArgsBreakBytes + " bytes) is a whole number of " + RevitCommandService.WriteProgressChunk + "-byte pieces, so a piece ends exactly where the fake stops reading");
        var savedTemp = Environment.GetEnvironmentVariable("TMPDIR");
        var reports = new List<(int Written, int Total, long Ms)>();
        var clock = Stopwatch.StartNew();
        RevitCommandResult? seam = null;
        var thrown = "";
        string? wrongPrefix = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("TMPDIR", UnixTemp);
                var prefixField = typeof(System.IO.Pipes.PipeStream).GetField("s_pipePrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var prefix = prefixField?.GetValue(null) as string;
                if (prefix is null)
                    Console.WriteLine("::warning::drift-loop: client write progress at a mid-args break: PipeStream.s_pipePrefix is not readable on this runtime ("
                        + (prefixField is null ? "no such field" : "not a string") + "); the socket directory Core's client resolves is not confirmed (a wrong one ends in pipe_connect_timeout below)");
                else if (prefix != UnixPipePrefix) wrongPrefix = prefix;
            }
            if (wrongPrefix is null)
            {
                RevitCommandService.WriteProgress = (written, total) => reports.Add((written, total, clock.ElapsedMilliseconds));
                seam = RevitCommandService.Run(pipe, envelope, RevitCommandPolicy.ForRepository(root), RevitCommandService.PipeTransportWithReadTimeout(TimeSpan.FromMilliseconds(Math.Max(1, h.Budget()))));
            }
        }
        catch (Exception failure) { thrown = failure.GetType().Name + ": " + failure.Message; }
        finally
        {
            RevitCommandService.WriteProgress = null;
            if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR", savedTemp);
        }
        if (wrongPrefix is not null) { Check(false, "Core's pipe client resolves sockets under " + UnixTemp + " (PipeStream socket prefix)", wrongPrefix); return; }
        var elapsed = clock.ElapsedMilliseconds;
        Check(RevitCommandService.WriteProgress is null, "the seam is null on the test thread after the run");
        var (exited, exit, error) = h.End();
        var received = h.Received();

        var handed = reports.Count > 0 ? reports[^1].Written : -1;
        var brokenLines = received.Skip(WriteProgressRequestsBefore.Length).ToArray();
        int Read(string line) { var p = line.Split(':'); return p.Length == 4 && p[0] + ":" == MidArgsLogPrefix && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1; }
        var measured = "os=" + os + " wire_bytes=" + wireBytes + " chunk=" + RevitCommandService.WriteProgressChunk + " reports=" + reports.Count + " handed=" + handed
            + " unhanded=" + (handed < 0 ? "<none>" : (wireBytes - handed).ToString(CultureInfo.InvariantCulture))
            + " last_report_ms=" + (reports.Count > 0 ? reports[^1].Ms.ToString(CultureInfo.InvariantCulture) : "<none>") + " elapsed_ms=" + elapsed
            + " fake=" + string.Join(",", brokenLines) + " seam_line=" + (seam?.Line ?? "<none> " + thrown);

        Check(reports.Count > 0 && reports[0].Written == 0 && reports.All(x => x.Total == wireBytes),
            "the seam reported 0 of the wire bytes before the first piece, and its wire bytes are WireLine of the CLI's envelope plus Environment.NewLine", measured);
        Check(reports.Zip(reports.Skip(1)).All(x => x.Second.Written > x.First.Written && x.Second.Written - x.First.Written <= RevitCommandService.WriteProgressChunk),
            "each report after a returned Write grows by one piece of at most " + RevitCommandService.WriteProgressChunk + " bytes", measured);
        Check(brokenLines.Length == 2 && brokenLines.All(x => Read(x) == MidArgsBreakBytes)
            && received.Take(WriteProgressRequestsBefore.Length).SequenceEqual(WriteProgressRequestsBefore),
            "the fake logged the two queries and two broken-after lines, each after reading exactly " + MidArgsBreakBytes + " bytes (CLI, then the seam run)", string.Join(",", received));
        Check(exited && exit == 0 && error.Length == 0, "the fake add-in exits 0 with an empty stderr", "exited " + exited + " exit " + exit + " " + error);
        // The seam changes neither the bytes nor the answer: the CLI (no seam) and the seam run end with the same line,
        // the P2-31 failure (pipe_io_error, field pipe_name, detail IOException), exit 1 for the CLI.
        Check(cli.Exit == 1 && Status(cli.Out) == "failed" && Text(cli.Out, "code") == "pipe_io_error" && Text(cli.Out, "field") == "pipe_name" && Text(cli.Out, "detail") == "IOException",
            "the CLI's floor.create broken after " + MidArgsBreakBytes + " bytes: exit 1, pipe_io_error, field pipe_name, detail IOException on " + os, R(cli));
        Check(seam is { IsError: true, Forwarded: true } && seam.Line == cli.Out, "the seam run's line equals the CLI's line (same failure, nothing changed by the seam)", measured + " | " + R(cli));
        // P2-32: measured by deliberate probe reds (Windows run 44484659892, Linux run 44487717843). Windows (named pipe,
        // inbound quota 0): the seam saw 4096 of 39727 bytes handed, exactly what the fake read; the fifth piece's Write
        // was still pending when the fake closed (it failed about 200 ms after the last report), and the fake's queued
        // count was 1024 for the seam run (that one pending piece) but 35631 for the CLI's single Write (its whole pending
        // remainder): PeekNamedPipe counts a pending client write, so the P2-31 "queued == unread" on Windows was the
        // client still writing. Linux (Unix socket): every byte was handed (39736 of 39736) about 200 ms before the close
        // and both runs left 35640 bytes queued in the fake's socket: the client's write had completed, nothing was still
        // being written. Other OSes have not been probed (macOS does not run mirror-journal) and fail here until one is.
        long Queued(string line) { var p = line.Split(':'); return p.Length == 4 && long.TryParse(p[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var q) ? q : long.MinValue; }
        var cliQueued = brokenLines.Length == 2 ? Queued(brokenLines[0]) : long.MinValue;
        var seamQueued = brokenLines.Length == 2 ? Queued(brokenLines[1]) : long.MinValue;
        if (OperatingSystem.IsWindows())
            Check(handed == MidArgsBreakBytes && handed < wireBytes && seamQueued == RevitCommandService.WriteProgressChunk && cliQueued == wireBytes - MidArgsBreakBytes,
                "windows: when the fake closed the client had handed only the " + MidArgsBreakBytes + " bytes the fake read (written < wire bytes: still writing); the fake's queued count is the pending write (one "
                + RevitCommandService.WriteProgressChunk + "-byte piece with the seam, the whole remainder for the CLI's single Write)", measured);
        else if (OperatingSystem.IsLinux())
            Check(handed == wireBytes && cliQueued == wireBytes - MidArgsBreakBytes && seamQueued == cliQueued,
                "linux: the client had handed every wire byte before the fake closed (its write completed) and the unread remainder sat queued in the fake's socket in both runs", measured);
        else Check(false, os + ": the bytes handed at a mid-args break have not been observed by a probe on this OS", measured);
    }

    // P2-33: the P2-32 write-progress measurement as its own section, run on every OS (macOS through the CI step
    // "Write progress at a mid-args break (macOS)", which runs only this section: Program.cs --write-progress-only).
    // Against a fresh -BreakAfterBytes 4096 fake: the real CLI process sends the large floor.create (no seam, one Write),
    // then RevitCommandService.Run sends that very envelope in process with the seam set, to Harness.Pipe (no reflection).
    // The CLI process's line is the seam run's line byte for byte; the bytes handed and the fake's queued counts are pinned
    // per OS as relations to the wire bytes of that envelope (WireLine plus Environment.NewLine), no literal byte counts.
    const string PerOsFloor = "p2-33-floor";
    static readonly string[] PerOsRequestsBefore = { "p2-33-levels", "p2-33-floor-types" };

    // P2-36/P2-37: what one macOS seam run's lever did: how often PipeConnected ran (once per WriteRequest, so once per
    // request line), the client socket's send buffer read before the set, the value asked for (P2-41), the value read back
    // after it, and any error.
    sealed class LeverRecord
    {
        public int Calls;
        public int Default = -1;
        public int Requested = -1;
        public int Set = -1;
        public string Error = "";
        public override string ToString() => "calls=" + Calls + " default=" + Default + " requested=" + Requested + " set=" + Set + " error=" + (Error.Length == 0 ? "<none>" : Error);
    }

    // P2-40: the lowered lever's ratio (3/4 of the client's default send buffer), declared once: the second macOS seam
    // run's SendBufferLever and the requested value its setup check compares the read-back against both use this pair.
    const int LoweredNumerator = 3;
    const int LoweredDenominator = 4;

    // P2-41: the macOS send-buffer gate as data, so its planted self-tests run the very checks the seam runs feed:
    // what each lever did, the kept/requested lowered value, the fake's read and receive buffer, and the counts.
    sealed record MacSendBufferCounts(bool LeversOk, int SendBuffer, int RaisedDefault, int LoweredDefault, int Raised, int Lowered, int RequestedLowered,
        int InBuffer, int Read, int Chunk, long WireBytes, long SeamQueued, long Handed, long CliQueued, bool LoweredRun, long LoweredQueued, long LoweredHanded);

    // P2-42: the recorded macOS values (P2-37 probe red 44853016956 and P2-38 green: default send buffer 8192, raised
    // 16384, lowered requested and kept 6144, InBufferSize 32768, read 4096, 1024-byte piece, wire 39757, raised queued
    // 16384 / handed 20480, CLI queued 8192; lowered queued 6144 / handed 10240 by the pinned relation): the input of the
    // pure-data self-tests on Linux and Windows (the self-tests rebuild their baseline from the default, read, receive
    // buffer, piece and wire bytes).
    static readonly MacSendBufferCounts MacRecordedCounts = new(true, 8192, 8192, 8192, 16384, 6144, 6144, 32768, 4096, 1024, 39757, 16384, 20480, 8192, true, 6144, 10240);

    const string MacLeversName = "macos setup (P2-41): each lever (raised, then lowered) ran exactly once without error";
    const string MacKeptPrefix = "macos setup (P2-39)";
    const string MacSelfTestPrefix = "macos self-test (P2-39)";
    const string MacLoweredPrefix = "macos (P2-37)";
    const string MacPreconditionPrefix = "macos precondition (P2-42: the P2-41 lever setup check gates it, so no lever clause here): ";

    // P2-42: the gate's check names after the lever setup check, in gate order, from the counts they report; the
    // self-tests compare red and not-run names against these in full.
    sealed record MacCheckNames(string Kept, string Precondition, string SelfTest, string Raised, string Lowered)
    {
        public string[] All => new[] { Kept, Precondition, SelfTest, Raised, Lowered };
    }

    // P2-43: the kept name says the requested value is the declared ratio of the default only when it is; otherwise it
    // shows the requested value alone.
    // P2-44: every number of both branches is formatted with InvariantCulture, so the text a red carries does not depend
    // on the current culture (negative values, which a lever that never ran leaves behind, would show its negative sign);
    // the planted MacRequestedTextCulture pin runs both branches under a culture whose negative sign is '~'.
    static string MacRequestedText(MacSendBufferCounts c) => c.RequestedLowered == (long)c.LoweredDefault * LoweredNumerator / LoweredDenominator
        ? MacNumber(c.RequestedLowered) + " = " + MacNumber(LoweredNumerator) + "/" + MacNumber(LoweredDenominator) + " of default " + MacNumber(c.LoweredDefault)
        : MacNumber(c.RequestedLowered);

    static string MacNumber(long value) => value.ToString(CultureInfo.InvariantCulture);

    static MacCheckNames MacNames(MacSendBufferCounts c) => new(
        MacKeptPrefix + ": the kernel kept the lowered send buffer the lever requested (measured lowered=" + c.Lowered + ", requested=" + MacRequestedText(c) + ")",
        MacPreconditionPrefix + "each lever's client default send buffer == the default unix send buffer; raised > default is a whole multiple of the " + c.Chunk + "-byte piece, as are the " + c.Read + " bytes the fake reads; a kept lowered value (0 < lowered < default; rounding is the P2-39 setup check) differs from them (P2-38: handed == read + lowered is not 2 x read); and the fake's receive buffer (InBufferSize, all broken-after lines equal) is above the raised send buffer",
        MacSelfTestPrefix + ": the lowered relation rejects a planted handed == 2 x read (" + 2L * c.Read + ") with queued == lowered (" + c.Lowered + ")",
        "macos: with the seam client's send buffer raised (and the fake's receive buffer above it), the seam run's queued (FIONREAD) == the raised send buffer and handed == the bytes the fake read plus it < wire bytes (still writing), while the CLI process (default send buffer) queues exactly the default unix send buffer: the writer's send buffer bounds the queue",
        MacLoweredPrefix + ": with the seam client's send buffer lowered below its default, the second seam run prints the CLI's line, queues exactly the lowered send buffer and hands the bytes the fake read plus it, below the default and raised runs: queued and handed follow the send buffer both ways");

    // P2-42: the line printed for a check a red setup check skips.
    static string MacNotRunNotice(string name) => "::notice::drift-loop: write progress per OS (P2-33): " + name + " not run (setup red)";

    // P2-42: what the two levers did, as the gate reads it (LeversOk, defaults, read-back values). The kept check's
    // requested value is the one the lowered lever asked for (LeverRecord.Requested), not one recomputed from its default.
    static MacSendBufferCounts WithLevers(MacSendBufferCounts c, LeverRecord raised, LeverRecord lowered) => c with
    {
        LeversOk = raised.Calls == 1 && raised.Error.Length == 0 && lowered.Calls == 1 && lowered.Error.Length == 0,
        RaisedDefault = raised.Default, LoweredDefault = lowered.Default, Raised = raised.Set, Lowered = lowered.Set,
        RequestedLowered = lowered.Requested,
    };

    // P2-41: the macOS checks in order as (passed, name); checks skipped because a setup check is red are added to notRun
    // (their names), so the caller prints one 'not run (setup red)' line for each.
    static List<(bool Ok, string Name)> MacSendBufferChecks(MacSendBufferCounts c, List<string> notRun)
    {
        var checks = new List<(bool Ok, string Name)>();
        var names = MacNames(c);
        var (kept, preconditionName, selfTest, raisedName, loweredName) = (names.Kept, names.Precondition, names.SelfTest, names.Raised, names.Lowered);
        // P2-41: a failed lever (not exactly one call, or an error) is one named setup red; nothing else runs, each
        // skipped check is a not-run line (before P2-41 it was the rounding setup red plus precondition and relation reds).
        checks.Add((c.LeversOk, MacLeversName));
        if (!c.LeversOk) { notRun.AddRange(names.All); return checks; }
        // P2-39: a kernel that rounds the lowered send buffer (kept != requested) is a named setup red carrying the
        // measured value, not a relation red.
        // P2-39 probe red (run 44927109449) planted lowered - 1: named red "measured lowered=6143, requested=6144".
        // P2-40: this is the only check that names kept != requested: the precondition does not repeat it, and the
        // lowered-run checks below (its self-test and relation, which read the kept value) run only when it holds, so a
        // rounded lowered value is exactly one red, this one.
        // P2-40 probe red (run 45243506492) planted lowered - 1: this setup check was the only macOS red.
        // P2-42: no LeversOk clause here or in the precondition: a failed lever returns above (the P2-41 setup check), so
        // every check below runs only with LeversOk true.
        var loweredKept = c.Lowered == c.RequestedLowered;
        checks.Add((loweredKept, kept));
        // P2-40: no lowered % chunk clause: LoweredCounts requires handed == read + lowered and handed % chunk == 0, so with
        // read % chunk == 0 (checked here) a lowered value that is not a whole number of pieces already fails the lowered
        // relation. The lowered clauses apply to a kept value only (a rounded one is the setup red above).
        var precondition = c.SendBuffer > 0 && c.RaisedDefault == c.SendBuffer && c.LoweredDefault == c.SendBuffer
            && c.Raised > c.SendBuffer && c.Raised % c.Chunk == 0 && c.InBuffer > c.Raised && c.Read % c.Chunk == 0
            && (!loweredKept || (c.Lowered > 0 && c.Lowered < c.SendBuffer && c.Lowered != c.Read));
        checks.Add((precondition, preconditionName));
        // P2-38: the precondition guards every macOS relation once; a relation cannot pass without it.
        void Relation(bool relation, string name) => checks.Add((precondition && relation, name));
        // P2-39: the lowered queued/handed relation, once; its planted self-test proves it rejects handed == 2 x read
        // (the precondition's lowered != read makes read + lowered != 2 x read), so no separate != 2 x read clause.
        bool LoweredCounts(long queued, long handedBytes) => queued == c.Lowered && handedBytes == c.Read + c.Lowered && handedBytes % c.Chunk == 0;
        // P2-39 probe red (run 44927109449) asserted acceptance and failed: the relation rejects handed 8192 == 2 x read.
        if (loweredKept) Relation(!LoweredCounts(c.Lowered, 2L * c.Read), selfTest); else notRun.Add(selfTest);
        Relation(c.SeamQueued == c.Raised && c.Handed == c.Read + c.Raised && c.Handed == c.Read + c.SeamQueued && c.Handed % c.Chunk == 0 && c.Handed < c.WireBytes
            && c.SeamQueued < c.InBuffer && c.CliQueued == c.SendBuffer && c.CliQueued < c.SeamQueued && c.CliQueued < c.WireBytes - c.Read, raisedName);
        if (loweredKept) Relation(c.LoweredRun && LoweredCounts(c.LoweredQueued, c.LoweredHanded) && c.LoweredQueued < c.CliQueued && c.LoweredHanded < c.Handed, loweredName); else notRun.Add(loweredName);
        return checks;
    }

    // P2-41: the planted self-tests of the macOS gate. The baseline is built from the measured default send buffer, the
    // fake's read and receive buffer and the wire bytes by the pinned relations (raised = 2 x default, lowered = the
    // requested ratio of it, queued = the send buffer, handed = read + it), so it is green by construction; each plant
    // changes one input and names the only red(s) and not-run lines it must give.
    static void MacSendBufferSelfTests(MacSendBufferCounts measured, Action<bool, string, string> check)
    {
        var s = measured.SendBuffer;
        var lowered = (int)((long)s * LoweredNumerator / LoweredDenominator);
        var baseline = measured with
        {
            LeversOk = true, RaisedDefault = s, LoweredDefault = s, Raised = 2 * s, Lowered = lowered, RequestedLowered = lowered,
            SeamQueued = 2L * s, Handed = measured.Read + 2L * s, CliQueued = s, LoweredRun = true, LoweredQueued = lowered, LoweredHanded = measured.Read + (long)lowered,
        };
        static LeverRecord Lever(int calls, int @default, int requested, int set, string error = "") => new() { Calls = calls, Default = @default, Requested = requested, Set = set, Error = error };
        (string[] Red, string[] NotRun, string Seen) Outcome(MacSendBufferCounts c)
        {
            var notRun = new List<string>();
            var red = MacSendBufferChecks(c, notRun).Where(x => !x.Ok).Select(x => x.Name).ToArray();
            return (red, notRun.ToArray(), "red=[" + string.Join(" ; ", red) + "] not_run=[" + string.Join(" ; ", notRun) + "] counts=" + c);
        }
        // P2-42: red and not-run names are compared in full and in order (not by prefix or count).
        static bool Same(string[] names, params string[] expected) => names.SequenceEqual(expected, StringComparer.Ordinal);
        var green = Outcome(baseline);
        var fromLevers = WithLevers(baseline with { LeversOk = false, RaisedDefault = -1, LoweredDefault = -1, Raised = -1, Lowered = -1, RequestedLowered = -1 },
            Lever(1, s, 2 * s, 2 * s), Lever(1, s, lowered, lowered));
        check(green.Red.Length == 0 && green.NotRun.Length == 0 && fromLevers == baseline,
            "macos self-test (P2-41): the baseline built from the pinned relations gives no red and no not-run line (P2-42: and is what the gate reads from levers that did exactly that)", green.Seen + " from_levers=" + fromLevers);
        var roundedInput = baseline with { Lowered = baseline.Read };
        var rounded = Outcome(roundedInput);
        var roundedNames = MacNames(roundedInput);
        // P2-42: the rounding red pinned as text: it carries the measured (rounded) and requested values.
        // P2-43: the ratio in the pinned text is the declared pair, not a hand-copied "3/4".
        // P2-45: as keptLiteral, the ratio is the written text "3/4" (independent of the declared pair).
        var roundedKept = "macos setup (P2-39): the kernel kept the lowered send buffer the lever requested (measured lowered=" + baseline.Read + ", requested=" + lowered + " = 3/4 of default " + s + ")";
        check(Same(rounded.Red, roundedKept) && Same(rounded.Red, roundedNames.Kept) && Same(rounded.NotRun, roundedNames.SelfTest, roundedNames.Lowered),
            "macos self-test (P2-41): a planted kernel rounding of the lowered send buffer to exactly the read size (" + baseline.Read + ") gives only the setup red, and the lowered self-test and relation each print a not-run line (P2-42: names compared in full)", rounded.Seen);
        var failedInput = baseline with { LeversOk = false };
        var failedLever = Outcome(failedInput);
        var failedNames = MacNames(failedInput);
        check(Same(failedLever.Red, MacLeversName) && Same(failedLever.NotRun, failedNames.Kept, failedNames.Precondition, failedNames.SelfTest, failedNames.Raised, failedNames.Lowered),
            "macos self-test (P2-41): a planted failed lever (leversOk false) gives one named red, the lever setup check, and every other macOS check prints a not-run line (P2-42: names compared in full)", failedLever.Seen);
        // P2-42: the not-run notice text, not only the names.
        const string notice = "::notice::drift-loop: write progress per OS (P2-33): ";
        var notices = failedLever.NotRun.Select(MacNotRunNotice).ToArray();
        check(Same(notices, failedNames.All.Select(name => notice + name + " not run (setup red)").ToArray()) && Same(rounded.NotRun.Select(MacNotRunNotice).ToArray(), notice + roundedNames.SelfTest + " not run (setup red)", notice + roundedNames.Lowered + " not run (setup red)"),
            "macos self-test (P2-42): each check a red setup check skips prints '::notice::drift-loop: write progress per OS (P2-33): <name> not run (setup red)'", string.Join(" ; ", notices));
        // P2-42: a planted lowered lever whose read-back equals the configured ratio of its default but not the value it
        // asked for: the kept check compares against LeverRecord.Requested, so it is the only red (a requested value
        // recomputed from the default would pass it).
        // P2-42 red (run 45379428651, requested recomputed as 3/4 of the default): red=[] on Linux, Windows and macOS.
        var chunk = baseline.Chunk;
        var mismatchInput = WithLevers(baseline, Lever(1, s, 2 * s, 2 * s), Lever(1, s, lowered + chunk, lowered));
        var mismatch = Outcome(mismatchInput);
        var mismatchNames = MacNames(mismatchInput);
        check(mismatchInput.RequestedLowered == lowered + chunk && Same(mismatch.Red, mismatchNames.Kept) && Same(mismatch.NotRun, mismatchNames.SelfTest, mismatchNames.Lowered),
            "macos self-test (P2-42): a planted lowered lever that requested " + (lowered + chunk) + " but kept " + lowered + " (the ratio of its default " + s + ") gives only the kept setup red: the gate compares against LeverRecord.Requested", mismatch.Seen);
        // P2-43: the mismatch red as text: a requested value that is not the declared ratio of the default shows the
        // requested value only (no ' = 3/4 of default' claim it does not meet).
        var mismatchKept = "macos setup (P2-39): the kernel kept the lowered send buffer the lever requested (measured lowered=" + lowered + ", requested=" + (lowered + chunk) + ")";
        check(Same(mismatch.Red, mismatchKept),
            "macos self-test (P2-43): the planted requested/kept mismatch red names the requested value only (" + (lowered + chunk) + "), not as 3/4 of the default", mismatch.Seen);

        // P2-43: the gate's check names pinned as independently written text (not only through MacNames): the kept name
        // of a requested value that is the declared ratio, the precondition, the lowered self-test and the lowered relation.
        // P2-44: the ratio in this pin is the written text "3/4", not the declared pair: the pin is independent of the
        // constants the name is built from (a changed pair must be stated here and in the row, not followed silently).
        var baselineNames = MacNames(baseline);
        var keptLiteral = "macos setup (P2-39): the kernel kept the lowered send buffer the lever requested (measured lowered=" + lowered + ", requested=" + lowered + " = 3/4 of default " + s + ")";
        var preconditionLiteral = "macos precondition (P2-42: the P2-41 lever setup check gates it, so no lever clause here): each lever's client default send buffer == the default unix send buffer; raised > default is a whole multiple of the "
            + chunk + "-byte piece, as are the " + baseline.Read + " bytes the fake reads; a kept lowered value (0 < lowered < default; rounding is the P2-39 setup check) differs from them (P2-38: handed == read + lowered is not 2 x read); and the fake's receive buffer (InBufferSize, all broken-after lines equal) is above the raised send buffer";
        var selfTestLiteral = "macos self-test (P2-39): the lowered relation rejects a planted handed == 2 x read (" + 2L * baseline.Read + ") with queued == lowered (" + lowered + ")";
        const string loweredLiteral = "macos (P2-37): with the seam client's send buffer lowered below its default, the second seam run prints the CLI's line, queues exactly the lowered send buffer and hands the bytes the fake read plus it, below the default and raised runs: queued and handed follow the send buffer both ways";
        check(baselineNames.Kept == keptLiteral && baselineNames.Precondition == preconditionLiteral && baselineNames.SelfTest == selfTestLiteral && baselineNames.Lowered == loweredLiteral,
            "macos self-test (P2-43): the kept, precondition, lowered self-test and lowered relation names equal their independently written text",
            "kept=" + baselineNames.Kept + " | precondition=" + baselineNames.Precondition + " | self_test=" + baselineNames.SelfTest + " | lowered=" + baselineNames.Lowered);

        // P2-44: the two names P2-43 left unpinned, as independently written text as well, so every name the macOS gate
        // prints is pinned: the raised relation's name, and the lever setup check's name through the red a failed lever
        // gives (not by reading MacLeversName into the expectation).
        const string raisedLiteral = "macos: with the seam client's send buffer raised (and the fake's receive buffer above it), the seam run's queued (FIONREAD) == the raised send buffer and handed == the bytes the fake read plus it < wire bytes (still writing), while the CLI process (default send buffer) queues exactly the default unix send buffer: the writer's send buffer bounds the queue";
        const string leversLiteral = "macos setup (P2-41): each lever (raised, then lowered) ran exactly once without error";
        check(baselineNames.Raised == raisedLiteral && Same(failedLever.Red, leversLiteral),
            "macos self-test (P2-44): the raised relation name and the lever setup red equal their independently written text",
            "raised=" + baselineNames.Raised + " | levers_red=" + string.Join(" ; ", failedLever.Red));

        // P2-43: lever records that did not run exactly once, or recorded an error, planted through WithLevers (values
        // otherwise as the baseline levers): each gives only the P2-41 lever setup red and every other check a not-run line.
        var raisedOk = Lever(1, s, 2 * s, 2 * s);
        var loweredOk = Lever(1, s, lowered, lowered);
        // P2-44: a lever that never ran keeps its record's -1 default, requested and read-back values.
        var neverRan = Lever(0, -1, -1, -1);
        var leverPlants = new (string What, LeverRecord Raised, LeverRecord Lowered)[]
        {
            ("raised calls=0", neverRan, loweredOk),
            ("raised calls=2", Lever(2, s, 2 * s, 2 * s), loweredOk),
            ("lowered calls=0", raisedOk, neverRan),
            ("lowered calls=2", raisedOk, Lever(2, s, lowered, lowered)),
            ("raised error", Lever(1, s, 2 * s, 2 * s, "planted raised error"), loweredOk),
            ("lowered error", raisedOk, Lever(1, s, lowered, lowered, "planted lowered error")),
        };
        // P2-44: what a pair of lever records breaks, read from the records themselves (which lever, and whether it did
        // not run exactly once or recorded an error). This replaces the always-true leverPlants.Length == 6 clause: each
        // plant must break exactly one thing, that one must be what its label says, the levers must be ok exactly when
        // nothing is broken, and the plants together must cover both levers x (calls 0, calls 2, an error).
        static string[] LeverFailures(LeverRecord raised, LeverRecord lowered)
        {
            var failures = new List<string>();
            foreach (var (which, record) in new[] { ("raised", raised), ("lowered", lowered) })
            {
                if (record.Calls != 1) failures.Add(which + " calls=" + record.Calls.ToString(CultureInfo.InvariantCulture));
                if (record.Error.Length != 0) failures.Add(which + " error");
            }
            return failures.ToArray();
        }
        var leverSeen = new List<string>();
        var covered = new SortedSet<string>(StringComparer.Ordinal);
        var leverPlantsOk = LeverFailures(raisedOk, loweredOk).Length == 0 && WithLevers(baseline, raisedOk, loweredOk).LeversOk;
        foreach (var (what, raisedLever, loweredLever) in leverPlants)
        {
            var input = WithLevers(baseline, raisedLever, loweredLever);
            var outcome = Outcome(input);
            var failures = LeverFailures(raisedLever, loweredLever);
            if (failures.Length == 1) covered.Add(failures[0]);
            leverPlantsOk &= failures.Length == 1 && failures[0] == what && !input.LeversOk
                && Same(outcome.Red, MacLeversName) && Same(outcome.NotRun, MacNames(input).All);
            leverSeen.Add(what + ": " + outcome.Seen);
        }
        check(leverPlantsOk && covered.SequenceEqual(new[] { "lowered calls=0", "lowered calls=2", "lowered error", "raised calls=0", "raised calls=2", "raised error" }, StringComparer.Ordinal),
            "macos self-test (P2-43): planted lever records (raised or lowered with calls 0 or 2, or with an error) through WithLevers give only the lever setup red '" + MacLeversName + "' and five not-run lines (P2-44: each plant breaks exactly the one thing its label names, and the six cover both levers x calls 0, calls 2 and an error)",
            string.Join(" || ", leverSeen) + " || covered=[" + string.Join(" ; ", covered) + "]");

        // P2-44: the not-run names of the calls=0 plants as written text. A lowered lever that never ran leaves -1 in
        // the kept check's measured and requested values and in the lowered self-test's queued value (3/4 of a -1
        // default truncates to 0, so the requested value is not the ratio of it and the kept name shows it alone);
        // a raised lever that never ran leaves no -1 anywhere, because no name reads the raised values.
        var zeroLowered = Outcome(WithLevers(baseline, raisedOk, neverRan));
        var zeroRaised = Outcome(WithLevers(baseline, neverRan, loweredOk));
        const string zeroKeptLiteral = "macos setup (P2-39): the kernel kept the lowered send buffer the lever requested (measured lowered=-1, requested=-1)";
        var zeroSelfTestLiteral = "macos self-test (P2-39): the lowered relation rejects a planted handed == 2 x read (" + 2L * baseline.Read + ") with queued == lowered (-1)";
        check(Same(zeroLowered.Red, leversLiteral) && Same(zeroLowered.NotRun, zeroKeptLiteral, preconditionLiteral, zeroSelfTestLiteral, raisedLiteral, loweredLiteral)
            && Same(zeroRaised.Red, leversLiteral) && Same(zeroRaised.NotRun, keptLiteral, preconditionLiteral, selfTestLiteral, raisedLiteral, loweredLiteral),
            "macos self-test (P2-44): a lowered lever that never ran prints its -1 measured, requested and queued values in the not-run names as written text, and a raised lever that never ran prints the baseline names",
            "lowered_zero=[" + string.Join(" ; ", zeroLowered.NotRun) + "] raised_zero=[" + string.Join(" ; ", zeroRaised.NotRun) + "]");
    }

    // P2-44: the macOS gate's names against the recorded macOS numbers (MacRecordedCounts) written as literals: the
    // default send buffer 8192, the 4096 bytes the fake reads, the 1024-byte piece and the kept lowered value 6144. The
    // P2-43 pins rebuild their text from the counts they are handed, so a name that read a neighbouring field (the
    // raised value where the lowered belongs, the read size where the piece belongs) still matches them; these do not.
    // Pure data, so they run on every OS.
    static void MacRecordedNamePins(Action<bool, string, string> check)
    {
        var c = MacRecordedCounts;
        check(c.SendBuffer == 8192 && c.RaisedDefault == 8192 && c.LoweredDefault == 8192 && c.Raised == 16384 && c.Lowered == 6144
            && c.RequestedLowered == 6144 && c.InBuffer == 32768 && c.Read == 4096 && c.Chunk == 1024 && c.WireBytes == 39757,
            "macos pin (P2-44): the recorded macOS counts are the measured values (default send buffer 8192, raised 16384, lowered requested and kept 6144, receive buffer 32768, read 4096, piece 1024, wire 39757)",
            c.ToString());
        var names = MacNames(c);
        const string kept = "macos setup (P2-39): the kernel kept the lowered send buffer the lever requested (measured lowered=6144, requested=6144 = 3/4 of default 8192)";
        const string precondition = "macos precondition (P2-42: the P2-41 lever setup check gates it, so no lever clause here): each lever's client default send buffer == the default unix send buffer; raised > default is a whole multiple of the 1024-byte piece, as are the 4096 bytes the fake reads; a kept lowered value (0 < lowered < default; rounding is the P2-39 setup check) differs from them (P2-38: handed == read + lowered is not 2 x read); and the fake's receive buffer (InBufferSize, all broken-after lines equal) is above the raised send buffer";
        const string selfTest = "macos self-test (P2-39): the lowered relation rejects a planted handed == 2 x read (8192) with queued == lowered (6144)";
        check(names.Kept == kept && names.Precondition == precondition && names.SelfTest == selfTest,
            "macos pin (P2-44): the kept, precondition and lowered self-test names carry the recorded macOS numbers as literals (6144, 8192, 1024, 4096 and 2 x read 8192)",
            "kept=" + names.Kept + " | precondition=" + names.Precondition + " | self_test=" + names.SelfTest);
        // P2-45: the drift list the live macOS gate's recorded-count check reports: none for the recorded counts
        // themselves, exactly the one planted field (name, live and recorded values) otherwise.
        var same = MacCountsDrift(c, c);
        var oneOff = MacCountsDrift(c with { Lowered = 6143 }, c);
        var twoOff = MacCountsDrift(c with { WireBytes = 39758, LoweredRun = false }, c);
        check(same.Length == 0 && oneOff.SequenceEqual(new[] { "Lowered: live=6143 recorded=6144" }, StringComparer.Ordinal)
            && twoOff.SequenceEqual(new[] { "WireBytes: live=39758 recorded=39757", "LoweredRun: live=False recorded=True" }, StringComparer.Ordinal),
            "macos pin self-test (P2-45): the recorded-count drift lists nothing for equal counts and exactly each planted field with its live and recorded values",
            "same=[" + string.Join(" ; ", same) + "] one=[" + string.Join(" ; ", oneOff) + "] two=[" + string.Join(" ; ", twoOff) + "]");
    }

    // P2-45: the fields where the live macOS counts differ from the recorded ones, in declaration order, as
    // 'Field: live=X recorded=Y' with InvariantCulture numbers.
    static string[] MacCountsDrift(MacSendBufferCounts live, MacSendBufferCounts recorded)
    {
        static string Text(object? value) => value switch
        {
            int i => MacNumber(i),
            long l => MacNumber(l),
            bool b => b ? "True" : "False",
            _ => value?.ToString() ?? "<null>",
        };
        var drift = new List<string>();
        foreach (var property in typeof(MacSendBufferCounts).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).OrderBy(p => p.MetadataToken))
        {
            var (a, b) = (Text(property.GetValue(live)), Text(property.GetValue(recorded)));
            if (a != b) drift.Add(property.Name + ": live=" + a + " recorded=" + b);
        }
        return drift.ToArray();
    }

    // P2-44: MacRequestedText formats the numbers of both its branches with InvariantCulture. Under a culture whose
    // negative sign is not '-' a negative requested value shows it: the ratio branch (a negative default whose ratio is
    // the requested value) and the bare branch must print the same text as under the invariant culture. Planted values
    // only - the measured macOS counts are positive, where no culture changes an integer's text.
    static void MacRequestedTextCulture(Action<bool, string, string> check)
    {
        var exotic = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        exotic.NumberFormat.NegativeSign = "~";
        var ratio = MacRecordedCounts with { LoweredDefault = -8192, RequestedLowered = -6144, Lowered = -6144 };
        var bare = MacRecordedCounts with { LoweredDefault = -8192, RequestedLowered = -7168, Lowered = -7168 };
        var saved = CultureInfo.CurrentCulture;
        string ratioText, bareText, positiveText;
        try
        {
            CultureInfo.CurrentCulture = exotic;
            ratioText = MacRequestedText(ratio);
            bareText = MacRequestedText(bare);
            positiveText = MacRequestedText(MacRecordedCounts);
        }
        finally { CultureInfo.CurrentCulture = saved; }
        check(ratioText == "-6144 = 3/4 of default -8192" && bareText == "-7168" && positiveText == "6144 = 3/4 of default 8192",
            "macos pin (P2-44): MacRequestedText writes InvariantCulture numbers in both branches (a current culture whose negative sign is '~' does not reach the ratio branch or the bare one)",
            "ratio=" + ratioText + " bare=" + bareText + " positive=" + positiveText);

        // P2-45: the whole name record, not only MacRequestedText: every number MacNames writes (the measured lowered
        // value, the piece and read sizes, 2 x read and the queued == lowered value) is planted negative and the five
        // names built under the '~' culture must equal independently written text with '-' signs (and the names built
        // under the invariant culture).
        var planted = MacRecordedCounts with { LoweredDefault = -8192, RequestedLowered = -6144, Lowered = -6144, Read = -4096, Chunk = -1024 };
        MacCheckNames exoticNames;
        try
        {
            CultureInfo.CurrentCulture = exotic;
            exoticNames = MacNames(planted);
        }
        finally { CultureInfo.CurrentCulture = saved; }
        MacCheckNames invariantNames;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            invariantNames = MacNames(planted);
        }
        finally { CultureInfo.CurrentCulture = saved; }
        const string keptText = "macos setup (P2-39): the kernel kept the lowered send buffer the lever requested (measured lowered=-6144, requested=-6144 = 3/4 of default -8192)";
        const string preconditionText = "macos precondition (P2-42: the P2-41 lever setup check gates it, so no lever clause here): each lever's client default send buffer == the default unix send buffer; raised > default is a whole multiple of the -1024-byte piece, as are the -4096 bytes the fake reads; a kept lowered value (0 < lowered < default; rounding is the P2-39 setup check) differs from them (P2-38: handed == read + lowered is not 2 x read); and the fake's receive buffer (InBufferSize, all broken-after lines equal) is above the raised send buffer";
        const string selfTestText = "macos self-test (P2-39): the lowered relation rejects a planted handed == 2 x read (-8192) with queued == lowered (-6144)";
        const string raisedText = "macos: with the seam client's send buffer raised (and the fake's receive buffer above it), the seam run's queued (FIONREAD) == the raised send buffer and handed == the bytes the fake read plus it < wire bytes (still writing), while the CLI process (default send buffer) queues exactly the default unix send buffer: the writer's send buffer bounds the queue";
        const string loweredText = "macos (P2-37): with the seam client's send buffer lowered below its default, the second seam run prints the CLI's line, queues exactly the lowered send buffer and hands the bytes the fake read plus it, below the default and raised runs: queued and handed follow the send buffer both ways";
        check(exoticNames.All.SequenceEqual(new[] { keptText, preconditionText, selfTestText, raisedText, loweredText }, StringComparer.Ordinal)
            && exoticNames.All.SequenceEqual(invariantNames.All, StringComparer.Ordinal),
            "macos pin (P2-45): every number in the whole name record (measured lowered, piece and read sizes, 2 x read, queued == lowered) is written with InvariantCulture: planted negative, the names under a '~' negative-sign culture equal the written text and the invariant-culture names",
            "exotic=[" + string.Join(" ; ", exoticNames.All) + "] invariant=[" + string.Join(" ; ", invariantNames.All) + "]");
    }

    // P2-36/P2-37: the test-only lever (macOS): sets the connected client socket's SO_SNDBUF to numerator/denominator of
    // its own default, reading it back; errors are recorded, not thrown.
    static Action<System.IO.Pipes.PipeStream> SendBufferLever(int numerator, int denominator, LeverRecord record) => pipe =>
    {
        record.Calls++;
        try
        {
            var property = typeof(Microsoft.Win32.SafeHandles.SafePipeHandle).GetProperty("PipeSocket", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (property?.GetValue(pipe.SafePipeHandle) is not Socket socket) { record.Error = property is null ? "no SafePipeHandle.PipeSocket" : "PipeSocket is not a Socket"; return; }
            record.Default = socket.SendBufferSize;
            record.Requested = (int)((long)record.Default * numerator / denominator);
            socket.SendBufferSize = record.Requested;
            record.Set = socket.SendBufferSize;
        }
        catch (Exception failure) { record.Error = failure.GetType().Name + ": " + failure.Message; }
    };

    // P2-36: static pin of the connected-pipe seam RevitCommandService.PipeConnected ([ThreadStatic], null in production):
    // named only in RevitCommandsService.cs among the production sources and, comments stripped, exactly twice there (its
    // declaration without an initializer and the one read in WriteRequest); a planted =, +=, -= or ??= is caught.
    static void PipeConnectedPinned(string root, Action<bool, string, string> check)
    {
        var naming = new List<string>();
        foreach (var top in new[] { "core", "interfaces", "runtimes", "adapters", "evaluation" })
        {
            var directory = Path.Combine(root, top);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (File.ReadAllText(file).Contains("PipeConnected", StringComparison.Ordinal)) naming.Add(relative);
            }
        }
        naming.Sort(StringComparer.Ordinal);
        check(naming.SequenceEqual(new[] { WriteProgressService }), "static pin (P2-36): among the production sources only " + WriteProgressService + " names the connected-pipe seam", string.Join(",", naming));
        var service = File.ReadAllText(Path.Combine(root, WriteProgressService));
        static string Code(string source) => System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/|//[^\n]*", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        const string seamDeclaration = "[ThreadStatic] internal static Action<PipeStream>? PipeConnected;";
        const string seamRead = "PipeConnected?.Invoke(pipe);";
        static int Uses(string source) => System.Text.RegularExpressions.Regex.Matches(Code(source), @"\bPipeConnected\b").Count;
        static bool Pinned(string source) => Uses(source) == 2 && Code(source).Contains(seamDeclaration, StringComparison.Ordinal) && Code(source).Contains(seamRead, StringComparison.Ordinal);
        check(Pinned(service),
            "static pin (P2-36): with comments stripped the connected-pipe seam occurs exactly twice in " + WriteProgressService + " (its [ThreadStatic] declaration without an initializer and the one read): never assigned in production source",
            "code occurrences " + Uses(service));
        var planted = new[] { "\nstatic void P() { PipeConnected += _ => { }; }\n", "\nstatic void P(Action<PipeStream> x) { PipeConnected ??= x; }\n", "\nstatic void P() { PipeConnected -= null; }\n", "\nstatic void P() { PipeConnected = null; }\n" };
        check(planted.All(plant => !Pinned(service + plant)) && Pinned(service + "\n// PipeConnected = x;\n/* PipeConnected += y; */\n"),
            "static pin self-test (P2-36): a planted PipeConnected +=, ??=, -= or = is caught, a mention in a comment is not", string.Join(",", planted.Select(plant => Uses(service + plant))));
    }

    static void RunWriteProgressPerOsLoop(string root, string dotnet, string pwsh, string cliDll, string runDirectory)
    {
        void Check(bool condition, string name, string detail = "") => MirrorDriftLoopChecks.Check(condition, "write progress per OS (P2-33): " + name, detail);
        var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        Check(typeof(Harness).GetProperty("Pipe") is { CanRead: true, CanWrite: false, SetMethod: null }, "the harness's Pipe property is read-only (a getter, no setter or init accessor)");
        Check(RevitCommandService.WriteProgress is null, "the seam is null on the test thread before the run");
        PipeConnectedPinned(root, (condition, name, detail) => Check(condition, name, detail));
        Check(RevitCommandService.PipeConnected is null, "the connected-pipe seam (P2-36) is null on the test thread before the run");
        // P2-44: the name pins against the recorded macOS numbers as literals and MacRequestedText's culture-independence
        // read MacRecordedCounts, not this run, so they run on every OS (on macOS beside the live gate's own pins).
        MacRecordedNamePins((condition, name, detail) => Check(condition, name, detail));
        MacRequestedTextCulture((condition, name, detail) => Check(condition, name, detail));
        // P2-42: the macOS gate's planted self-tests are pure data; off macOS they run on the recorded macOS values.
        if (!OperatingSystem.IsMacOS()) MacSendBufferSelfTests(MacRecordedCounts, (condition, name, detail) => Check(condition, name, detail));

        using var h = new Harness(root, dotnet, pwsh, cliDll, runDirectory, "Drift-Pipe-WriteProgress-PerOs.rvt", PerOsRequestsBefore.Length + (OperatingSystem.IsMacOS() ? 3 : 2), breakAfterBytes: MidArgsBreakBytes);
        Check(h.Pipe.StartsWith("bim-agentic-os-2025-", StringComparison.Ordinal) && h.Pipe.Length == "bim-agentic-os-2025-".Length + 6 && h.Pipe[^6..].All(char.IsAsciiDigit),
            "the harness exposes its pipe name: bim-agentic-os-2025- and six digits", h.Pipe);
        if (!h.WaitReady(out var notReady)) { Check(false, "fake add-in pipe ready with -BreakAfterBytes " + MidArgsBreakBytes + " on " + os, notReady); return; }
        var levels = h.Command("p2-33-levels", "query.levels", _ => { });
        var floorTypes = h.Command("p2-33-floor-types", "query.floor_types", _ => { });
        if (!Succeeded(levels) || !Succeeded(floorTypes)) { Check(false, "query.levels and query.floor_types succeed on " + os, R(levels) + " | " + R(floorTypes)); return; }
        var cli = h.Command(PerOsFloor, "floor.create", w => BigFloorArgs(w, Pick(levels.Out, "levels", "name", "Level 1"), Pick(floorTypes.Out, "floor_types", "name", "Generic 150mm")));
        var envelope = File.ReadAllText(Directory.GetFiles(h.Scratch, "*-envelope-floor.create.json").Single(), Utf8);
        var wire = RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(envelope));
        var wireBytes = Utf8.GetByteCount(wire + Environment.NewLine);

        // Off Windows the fake listens under UnixTemp; Core's client resolves the socket directory from TMPDIR when .NET
        // first caches its pipe prefix (in the full suite P2-32 already cached UnixTemp; in a --write-progress-only run
        // nothing opened a pipe client before this). TMPDIR is restored afterwards.
        var savedTemp = Environment.GetEnvironmentVariable("TMPDIR");
        var reports = new List<(int Written, int Total, long Ms)>();
        var clock = Stopwatch.StartNew();
        RevitCommandResult? seam = null;
        var thrown = "";
        string? wrongPrefix = null;
        // P2-36: on macOS only, the test-only lever RevitCommandService.PipeConnected (null in production; called once per
        // WriteRequest, at its top, before that request line's first byte) doubles the seam run's client socket send
        // buffer (SO_SNDBUF); the value read back after the set is what the pin expects queued. P2-37: a second macOS seam
        // run, after the first, lowers it instead (P2-38: to 3/4 of its default, so the lowered value differs from the
        // 4096 bytes the fake reads). The CLI process has no lever (default send buffer).
        var raisedLever = new LeverRecord();
        var loweredLever = new LeverRecord();
        var loweredReports = new List<(int Written, int Total, long Ms)>();
        RevitCommandResult? loweredSeam = null;
        try
        {
            if (OperatingSystem.IsMacOS()) RevitCommandService.PipeConnected = SendBufferLever(2, 1, raisedLever);
            if (!OperatingSystem.IsWindows())
            {
                Environment.SetEnvironmentVariable("TMPDIR", UnixTemp);
                var prefixField = typeof(System.IO.Pipes.PipeStream).GetField("s_pipePrefix", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var prefix = prefixField?.GetValue(null) as string;
                if (prefix is null)
                    Console.WriteLine("::warning::drift-loop: write progress per OS (P2-33): PipeStream.s_pipePrefix is not readable on this runtime ("
                        + (prefixField is null ? "no such field" : "not a string") + "); the socket directory Core's client resolves is not confirmed (a wrong one ends in pipe_connect_timeout below)");
                else if (prefix != UnixPipePrefix) wrongPrefix = prefix;
            }
            if (wrongPrefix is null)
            {
                RevitCommandService.WriteProgress = (written, total) => reports.Add((written, total, clock.ElapsedMilliseconds));
                seam = RevitCommandService.Run(h.Pipe, envelope, RevitCommandPolicy.ForRepository(root), RevitCommandService.PipeTransportWithReadTimeout(TimeSpan.FromMilliseconds(Math.Max(1, h.Budget()))));
                if (OperatingSystem.IsMacOS())
                {
                    RevitCommandService.WriteProgress = (written, total) => loweredReports.Add((written, total, clock.ElapsedMilliseconds));
                    RevitCommandService.PipeConnected = SendBufferLever(LoweredNumerator, LoweredDenominator, loweredLever);
                    loweredSeam = RevitCommandService.Run(h.Pipe, envelope, RevitCommandPolicy.ForRepository(root), RevitCommandService.PipeTransportWithReadTimeout(TimeSpan.FromMilliseconds(Math.Max(1, h.Budget()))));
                }
            }
        }
        catch (Exception failure) { thrown = failure.GetType().Name + ": " + failure.Message; }
        finally
        {
            RevitCommandService.WriteProgress = null;
            RevitCommandService.PipeConnected = null;
            if (!OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable("TMPDIR", savedTemp);
        }
        if (wrongPrefix is not null) { Check(false, "Core's pipe client resolves sockets under " + UnixTemp + " on " + os, wrongPrefix); return; }
        var elapsed = clock.ElapsedMilliseconds;
        Check(RevitCommandService.WriteProgress is null, "the seam is null on the test thread after the run");
        Check(RevitCommandService.PipeConnected is null, "the connected-pipe seam (P2-36) is null on the test thread after the run");
        var (exited, exit, error) = h.End();
        var received = h.Received();

        var handed = reports.Count > 0 ? reports[^1].Written : -1;
        // The CLI process's and the first seam run's broken-after lines; on macOS a third (the lowered seam run) follows.
        var brokenLines = received.Skip(PerOsRequestsBefore.Length).Take(2).ToArray();
        var extraLines = received.Skip(PerOsRequestsBefore.Length + 2).ToArray();
        static string[] Fields(string line) { var p = line.Split(':'); return p.Length == 4 && p[0] + ":" == MidArgsLogPrefix ? p : Array.Empty<string>(); }
        static int Read(string line) => Fields(line) is { Length: 4 } p && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
        static long Queued(string line) => Fields(line) is { Length: 4 } p && long.TryParse(p[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var q) ? q : long.MinValue;
        var cliQueued = brokenLines.Length == 2 ? Queued(brokenLines[0]) : long.MinValue;
        var seamQueued = brokenLines.Length == 2 ? Queued(brokenLines[1]) : long.MinValue;
        string Seen((int Exit, string Out, string Err) x) => "exit=" + x.Exit + " status=" + (Status(x.Out) ?? "<null>") + " code=" + (Text(x.Out, "code") ?? "<null>") + " field=" + (Text(x.Out, "field") ?? "<null>") + " detail=" + (Text(x.Out, "detail") ?? "<null>");
        var measured = "os=" + os + " wire_bytes=" + wireBytes + " chunk=" + RevitCommandService.WriteProgressChunk + " reports=" + reports.Count + " handed=" + handed
            + " unhanded=" + (handed < 0 ? "<none>" : (wireBytes - handed).ToString(CultureInfo.InvariantCulture))
            + " last_report_ms=" + (reports.Count > 0 ? reports[^1].Ms.ToString(CultureInfo.InvariantCulture) : "<none>") + " elapsed_ms=" + elapsed
            + " unix_send_buffer=" + UnixSendBuffer() + " fake=" + string.Join(",", brokenLines) + " cli=" + Seen(cli)
            + " seam_line=" + (seam?.Line is { } seamLine ? (seamLine.Length > 300 ? seamLine[..300] + "..." : seamLine) : "<none> " + thrown);

        Check(reports.Count > 0 && reports[0].Written == 0 && reports.All(x => x.Total == wireBytes),
            "the seam reported 0 of the wire bytes before the first piece, and its wire bytes are WireLine of the CLI's envelope plus Environment.NewLine", measured);
        Check(reports.Zip(reports.Skip(1)).All(x => x.Second.Written > x.First.Written && x.Second.Written - x.First.Written <= RevitCommandService.WriteProgressChunk),
            "each report after a returned Write grows by one piece of at most " + RevitCommandService.WriteProgressChunk + " bytes", measured);
        Check(brokenLines.Length == 2 && brokenLines.All(x => Read(x) == MidArgsBreakBytes) && received.Take(PerOsRequestsBefore.Length).SequenceEqual(PerOsRequestsBefore),
            "the fake logged the two queries and two broken-after lines, each after reading exactly " + MidArgsBreakBytes + " bytes (CLI process, then the seam run)", string.Join(",", received) + " | " + measured);
        Check(exited && exit == 0 && error.Length == 0, "the fake add-in exits 0 with an empty stderr", "exited " + exited + " exit " + exit + " " + error);
        // CLI process bytes unchanged by the seam: the real CLI (no seam) and the seam run print the very same line.
        Check(seam is { IsError: true, Forwarded: true } && seam.Line == cli.Out, "the seam run's line equals the CLI process's stdout line byte for byte on " + os, measured);
        // P2-31/P2-32 facts, and on macOS the P2-33 probe red (run 44582611952): pipe_io_error, field pipe_name, detail
        // IOException, exit 1 for the CLI process on all three OSes.
        Check(cli.Exit == 1 && Status(cli.Out) == "failed" && Text(cli.Out, "code") == "pipe_io_error" && Text(cli.Out, "field") == "pipe_name" && Text(cli.Out, "detail") == "IOException",
            "the CLI process's floor.create broken after " + MidArgsBreakBytes + " bytes: exit 1, pipe_io_error, field pipe_name, detail IOException on " + os, measured);
        Check(extraLines.Length == (OperatingSystem.IsMacOS() ? 1 : 0) && extraLines.All(x => Read(x) == MidArgsBreakBytes), "the fake logged " + (OperatingSystem.IsMacOS() ? "one more broken-after line after reading exactly " + MidArgsBreakBytes + " bytes (the lowered seam run, P2-37)" : "nothing after the two broken-after lines") + " on " + os, string.Join(",", received));
        // The fake's InBufferSize field (on Unix the receive buffer of its socket end).
        var inBuffers = brokenLines.Concat(extraLines).Select(x => Fields(x) is { Length: 4 } p && int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : -1).Distinct().ToArray();
        var inBuffer = inBuffers.Length == 1 ? inBuffers[0] : -1;
        if (OperatingSystem.IsWindows())
            Check(handed == MidArgsBreakBytes && handed < wireBytes && seamQueued == RevitCommandService.WriteProgressChunk && cliQueued == wireBytes - MidArgsBreakBytes,
                "windows: handed == the bytes the fake read < wire bytes (still writing); queued: one piece with the seam, the whole unread remainder for the CLI", measured);
        else if (OperatingSystem.IsLinux())
            Check(handed == wireBytes && cliQueued == wireBytes - MidArgsBreakBytes && seamQueued == cliQueued,
                "linux: every wire byte handed (write completed); queued: the whole unread remainder in both runs", measured);
        else if (OperatingSystem.IsMacOS())
        {
            // P2-33 probe red (run 44582611952): wire 39757 bytes, 13 reports, handed 12288 = the 4096 bytes the fake read
            // plus 8192; the next piece's Write was pending when the fake closed (27469 bytes never handed).
            // P2-34 probe red (run 44671009756): FIONREAD (Socket.Available) found 8192 queued in both runs, equal to both
            // InBufferSize and the default unix send buffer (cause not isolated).
            // P2-35 probe red (run 44748588269): with the fake's receive buffer set to 32768 (InBufferSize 32768 in both
            // broken-after lines), queued stayed 8192 = the client's default unix send buffer and handed stayed 12288 = the
            // bytes read + that send buffer: on XNU AF_UNIX the writer is limited by its own send buffer, not the reader's
            // receive buffer.
            // P2-36: the cause is proven by moving it. The seam run's client send buffer is raised (lever above: 2x its
            // default, read back) while the CLI process keeps the default; the seam run's queued and handed follow the
            // raised value, the CLI's queued stays at the default. Pinned as relations to UnixSendBuffer(), the read-back
            // raised send buffer and InBufferSize (no literal byte counts). UnixSendBuffer() measures a fresh Unix socket's
            // default send buffer (as P2-33 does) and must equal the seam client's own default read before the raise;
            // handed % WriteProgressChunk == 0 assumes the raised send buffer is a multiple of the 1024-byte piece.
            // The fake raises its receive buffer relative to its own defaults (header), so the precondition is checked on
            // its own: a red there is a setup failure (a lever or SetReceiveBuffer sentinel), not a relation failure.
            // P2-37: the raised value must also be a whole number of 1024-byte pieces (an odd kernel rounding is a setup
            // red, not a relation red). A second seam run lowers the send buffer (P2-38: 3/4 of its default; also a multiple of the
            // piece); its queued and handed move down the same way. Every value is logged once in a ::notice:: line.
            // P2-37 probe red (run 44853016956): wire 39757, read 4096, InBufferSize 32768, default send buffer 8192;
            // raised 16384 -> queued 16384, handed 20480; lowered 4096 -> queued 4096, handed 8192; CLI queued 8192.
            // P2-38: lowered 4096 equals the bytes the fake read, so handed 8192 == read + lowered could not be told apart
            // from 2 x read (probe red 44882412787 with the precondition lowered != read). The lowered value is now 3/4 of
            // the default (a multiple of the piece) and the precondition requires it to differ from the read size.
            var sendBuffer = UnixSendBuffer();
            var raised = raisedLever.Set;
            var lowered = loweredLever.Set;
            // P2-39: the value the lever asked for (LoweredNumerator/LoweredDenominator of the client's default, the pair
            // SendBufferLever was given; P2-40); measured green P2-38: default 8192 -> requested and kept 6144.
            // P2-42: read from the gate, which takes it from the lowered lever's record (WithLevers).
            var loweredHanded = loweredReports.Count > 0 ? loweredReports[^1].Written : -1;
            var loweredQueued = extraLines.Length == 1 ? Queued(extraLines[0]) : long.MinValue;
            var chunk = RevitCommandService.WriteProgressChunk;
            var gate = WithLevers(new MacSendBufferCounts(false, sendBuffer, -1, -1, -1, -1, -1, inBuffer, MidArgsBreakBytes, chunk,
                wireBytes, seamQueued, handed, cliQueued,
                loweredSeam is { IsError: true, Forwarded: true } && loweredSeam.Line == cli.Out && loweredReports.Count > 0 && loweredReports[0].Written == 0 && loweredReports.All(x => x.Total == wireBytes),
                loweredQueued, loweredHanded), raisedLever, loweredLever);
            var requestedLowered = gate.RequestedLowered;
            var counts = "macos send buffer counts (P2-37): wire=" + wireBytes + " read=" + MidArgsBreakBytes + " in_buffer=" + inBuffer + " default=" + sendBuffer
                + " raised=" + raised + " raised_queued=" + seamQueued + " raised_handed=" + handed + " lowered=" + lowered + " lowered_requested=" + requestedLowered + " lowered_queued=" + loweredQueued
                + " lowered_handed=" + loweredHanded + " cli_queued=" + cliQueued;
            Console.WriteLine("::notice::drift-loop: " + counts);
            var macDetail = measured + " | " + counts + " | raised_lever " + raisedLever + " | lowered_lever " + loweredLever + " lowered_reports=" + loweredReports.Count
                + " lowered_line=" + (extraLines.Length == 1 ? extraLines[0] : "<none>")
                + " lowered_seam_line=" + (loweredSeam?.Line is { } low ? (low.Length > 200 ? low[..200] + "..." : low) : "<none>");
            // P2-41: the gate (setup checks, precondition, self-test, relations) is MacSendBufferChecks; checks a red setup
            // check skips print a named 'not run (setup red)' notice; the gate's planted self-tests run on every macOS run
            // (P2-42: and on Linux and Windows against the recorded macOS values, MacRecordedCounts).
            var notRun = new List<string>();
            foreach (var (ok, name) in MacSendBufferChecks(gate, notRun)) Check(ok, name, macDetail);
            foreach (var name in notRun) Console.WriteLine(MacNotRunNotice(name));
            // P2-45: the live counts are checked equal to the recorded macOS values (not only printed in the notice above):
            // a new runner value is a named red listing each differing field, and the row records the new value.
            var drift = MacCountsDrift(gate, MacRecordedCounts);
            Check(drift.Length == 0,
                "macos pin (P2-45): the live gate's counts equal the recorded macOS counts (MacRecordedCounts) field by field",
                "drift=[" + string.Join(" ; ", drift) + "] live=" + gate + " recorded=" + MacRecordedCounts);
            MacSendBufferSelfTests(gate, (condition, name, detail) => Check(condition, name, detail));
        }
        else Check(false, os + ": the bytes handed and queued at a mid-args break have not been observed by a probe on this OS", measured);
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
