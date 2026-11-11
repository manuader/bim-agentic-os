using Bim.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
// P2-14 drift loop end to end on the fake add-in: real processes only. A fake add-in pipe
// (tests/integration/fake-revit-command-pipe.ps1, the headless stand-in the catalogue session test uses) answers the real
// Release CLI (interfaces/cli/bin/Release/net8.0/Bim.Cli.dll), and the chain
//   revit-command query.snapshot -> mirror-drift -> mirror-drift-select -> mirror-drift-adopt -> revit-command query.snapshot
//   -> mirror-drift
// runs over files exactly as an operator would. Oracles:
//   1. Baseline: walls created through the CLI, a Core store built from that first snapshot (own Core ids, a map
//      Core id -> UniqueId) and mirror-drift reports no drift (the fixture is consistent).
//   2. An external edit (element.move of one wall through the CLI) is reported as one changed finding with an adopt
//      proposal; mirror-drift-select with {adopt} writes one BIM-IR operation; mirror-drift-adopt commits revision 2.
//   3. The store head is the adopted state: the moved wall's Core element has the Revit geometry, every other element
//      is untouched; a replay prints replayed; the pre-adopt mirror is refused stale.
//   4. A fresh query.snapshot (same readback as before the adopt) against the mirror at the new revision reports no drift
//      for the adopted element (no drift at all).
//   5. Deterministic: a second complete run (new fake add-in, new store) produces byte-identical files and outputs.
// Needs pwsh and the built CLI (scripts/verify.ps1 builds interfaces/cli before this project). No Revit is involved: a
// passing run is unit-tested evidence only.
static class MirrorDriftLoopChecks
{
    const string DocumentUid = "5e1f0a2b-3c4d-4e5f-8a9b-0c1d2e3f4a5b-0000e2e1";
    const string WallType = "Generic - 200mm";
    static readonly Guid ProjectId = Guid.Parse("a0000000-0000-4000-8000-0000000000e2");

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
    sealed record Loop(string Snapshot1, string Drift1, string Selection, string Adopt, string ProjectJson, string Drift2);

    static void Checks()
    {
        var root = FindRoot();
        var cliDll = Path.Combine(root, "interfaces", "cli", "bin", "Release", "net8.0", "Bim.Cli.dll");
        if (!File.Exists(cliDll)) { Check(false, "CLI binary present (build interfaces/cli --configuration Release first, as scripts/verify.ps1 does)", cliDll); return; }
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        if (!PwshAvailable(out var why))
        {
            // Every CI runner has pwsh: there the loop must run.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") Check(false, "pwsh available on CI", why);
            else Console.WriteLine("SKIP drift-loop: pwsh not found (" + why + ")");
            return;
        }
        // The document must sit under the repository's .bim/native-runs (the CLI's default enabled root) for writes; both
        // runs name the same document path (it is in every restore envelope), one after the other.
        var runDirectory = Path.Combine(root, ".bim", "native-runs", "mirror-drift-loop-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(runDirectory);
        var first = RunLoop(root, dotnet, cliDll, runDirectory, 1, report: true);
        var second = RunLoop(root, dotnet, cliDll, runDirectory, 2, report: false);
        if (first is null || second is null) { Check(false, "both runs completed", first is null ? "run 1 stopped" : "run 2 stopped"); return; }
        Check(first.Snapshot1 == second.Snapshot1, "deterministic: the edited query.snapshot readback is identical in both runs");
        Check(first.Drift1 == second.Drift1, "deterministic: the mirror-drift file is byte-identical in both runs");
        Check(first.Selection == second.Selection, "deterministic: the mirror-drift-select file is byte-identical in both runs");
        Check(first.Adopt == second.Adopt, "deterministic: mirror-drift-adopt prints the same outcome, revision and key", first.Adopt + " vs " + second.Adopt);
        Check(first.ProjectJson == second.ProjectJson, "deterministic: the adopted store project.json is byte-identical in both runs");
        Check(first.Drift2 == second.Drift2, "deterministic: the second mirror-drift file is byte-identical in both runs");
    }

    static bool PwshAvailable(out string why)
    {
        why = "";
        try
        {
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoProfile", "-Command", "$PSVersionTable.PSVersion.Major" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) { why = "pwsh did not start"; return false; }
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(60000)) { process.Kill(true); why = "pwsh timed out"; return false; }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception error) { why = error.Message; return false; }
    }

    static Loop? RunLoop(string root, string dotnet, string cliDll, string runDirectory, int run, bool report)
    {
        void Check(bool condition, string name, string detail = "") { if (report || !condition) MirrorDriftLoopChecks.Check(condition, "run " + run + ": " + name, detail); }
        var scratch = Path.Combine(Path.GetTempPath(), "bim-drift-loop-" + Guid.NewGuid().ToString("N"));
        Temporary.Add(scratch);
        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(scratch);
        var document = Path.GetFullPath(Path.Combine(runDirectory, "Drift-Loop.rvt"));
        File.WriteAllBytes(document, Array.Empty<byte>());
        var pipe = "bim-agentic-os-2025-" + Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
        var utf8 = new UTF8Encoding(false);
        var n = 0;
        string Path_(string stem) => Path.Combine(scratch, (++n).ToString("D2", CultureInfo.InvariantCulture) + "-" + stem + ".json");
        string File_(string stem, string content) { var path = Path_(stem); File.WriteAllText(path, content, utf8); return path; }

        // .NET pipes on Unix are sockets under the temp directory; both processes use the same short one (108-byte limit).
        void Environment_(ProcessStartInfo start) { if (!OperatingSystem.IsWindows()) start.Environment["TMPDIR"] = "/tmp"; }
        (int Exit, string Out, string Err) Cli(params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = utf8 };
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Environment_(start);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            return (process.ExitCode, stdout.Result.TrimEnd('\r', '\n'), stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        string R((int Exit, string Out, string Err) r) => "exit " + r.Exit + " " + (r.Out.Length > 1500 ? r.Out[..1500] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        string Envelope(string operationId, string command, Action<Utf8JsonWriter> args)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("protocol_version", 1);
                writer.WriteString("operation_id", operationId);
                writer.WriteStartObject("document");
                writer.WriteString("path", document);
                writer.WriteString("unique_id", DocumentUid);
                writer.WriteEndObject();
                writer.WriteString("command", command);
                writer.WriteStartObject("args");
                args(writer);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return utf8.GetString(buffer.ToArray());
        }
        (int Exit, string Out, string Err) Command(string operationId, string command, Action<Utf8JsonWriter> args) =>
            Cli("revit-command", File_("envelope-" + command, Envelope(operationId, command, args)), pipe);
        bool Succeeded((int Exit, string Out, string Err) r) => r.Exit == 0 && Status(r.Out) == "succeeded";

        // ---- The fake add-in behind a real pipe ----
        var ready = Path.Combine(scratch, "ready.txt");
        var log = Path.Combine(scratch, "received.txt");
        var serverStart = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(root, "tests", "integration", "fake-revit-command-pipe.ps1"), "-PipeName", pipe, "-DocumentUid", DocumentUid,
            "-ReadyFile", ready, "-LogFile", log, "-MaxRequests", "40", "-TimeoutSeconds", "600" }) serverStart.ArgumentList.Add(argument);
        Environment_(serverStart);
        using var server = Process.Start(serverStart) ?? throw new InvalidOperationException("fake add-in pipe did not start");
        var serverOut = server.StandardOutput.ReadToEndAsync();
        var serverErr = server.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!File.Exists(ready) && DateTime.UtcNow < deadline && !server.HasExited) Thread.Sleep(100);
            if (!File.Exists(ready)) { Check(false, "fake add-in pipe ready", server.HasExited ? serverErr.Result : "timed out"); return null; }

            // ---- Seed the fake document through the CLI: two walls on Level 1 ----
            var levels = Command("p2-14-levels", "query.levels", _ => { });
            var types = Command("p2-14-wall-types", "query.wall_types", _ => { });
            if (!Succeeded(levels) || !Succeeded(types)) { Check(false, "query.levels and query.wall_types succeed", R(levels) + " | " + R(types)); return null; }
            var level = Pick(levels.Out, "levels", "name", "Level 1");
            var type = Pick(types.Out, "wall_types", "name", WallType);
            void Wall(Utf8JsonWriter w, double ax, double ay, double bx, double by)
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
            var wallA = Command("p2-14-wall-a", "wall.create", w => Wall(w, 0, 0, 5000, 0));
            var wallB = Command("p2-14-wall-b", "wall.create", w => Wall(w, 5000, 0, 5000, 4000));
            if (!Succeeded(wallA) || !Succeeded(wallB)) { Check(false, "two wall.create commands succeed through the CLI", R(wallA) + " | " + R(wallB)); return null; }
            var uidA = Created(wallA.Out);
            var uidB = Created(wallB.Out);

            // ---- Core store at revision 1 = the first snapshot, with its own Core ids and the add-in map ----
            var snapshot0 = Command("p2-14-snapshot-0", "query.snapshot", _ => { });
            if (!Succeeded(snapshot0)) { Check(false, "the first query.snapshot succeeds", R(snapshot0)); return null; }
            var (project, map, levelNames) = CoreFrom(snapshot0.Out, levels.Out);
            var coreB = map.Single(pair => pair.Value == uidB).Key;
            var store = Path.Combine(scratch, "store");
            new ProjectStore(store).Initialize(project);
            string Mirror(int revision) => MirrorJson(document, revision, map, levelNames);
            var mirror1 = File_("mirror-r1", Mirror(1));
            var snapshot0Path = File_("snapshot-0", snapshot0.Out);
            var baseline = Cli("mirror-drift", store, snapshot0Path, mirror1, Path_("drift-baseline"));
            Check(baseline.Exit == 0 && Number(baseline.Out, "drift") == 0 && Number(baseline.Out, "proposals") == 0,
                "baseline: the store built from the first snapshot has no drift", R(baseline));

            // ---- External edit: wall B moved 500 mm east in the (fake) Revit document ----
            var move = Command("p2-14-move-b", "element.move", w =>
            {
                w.WriteStartArray("element_uids"); w.WriteStringValue(uidB); w.WriteEndArray();
                w.WriteStartArray("vector_mm"); w.WriteNumberValue(500); w.WriteNumberValue(0); w.WriteNumberValue(0); w.WriteEndArray();
            });
            if (!Succeeded(move)) { Check(false, "element.move of wall B succeeds through the CLI", R(move)); return null; }
            var snapshot1 = Command("p2-14-snapshot-1", "query.snapshot", _ => { });
            if (!Succeeded(snapshot1)) { Check(false, "the query.snapshot after the edit succeeds", R(snapshot1)); return null; }
            var snapshot1Path = File_("snapshot-1", snapshot1.Out);
            var observedB = SnapshotGeometry(snapshot1.Out, uidB);
            Check(observedB.SequenceEqual(new[] { 5.5, 0, 5.5, 4, 3, 0.2 }), "the edited snapshot reports wall B 500 mm east", string.Join(",", observedB));

            // ---- mirror-drift: one changed finding, an adopt proposal for wall B ----
            var drift1Path = Path_("drift-1");
            var drift1 = Cli("mirror-drift", store, snapshot1Path, mirror1, drift1Path);
            if (drift1.Exit != 0 || !File.Exists(drift1Path)) { Check(false, "mirror-drift after the edit succeeds", R(drift1)); return null; }
            var drift1Text = File.ReadAllText(drift1Path, utf8);
            using (var parsed = JsonDocument.Parse(drift1Text))
            {
                var reportElement = parsed.RootElement.GetProperty("report");
                var findings = reportElement.GetProperty("findings").EnumerateArray().ToArray();
                var proposals = parsed.RootElement.GetProperty("resolution").GetProperty("proposals").EnumerateArray().ToArray();
                Check(reportElement.GetProperty("summary").GetProperty("drift").GetInt32() == 1 && findings.Length == 1
                    && findings[0].GetProperty("kind").GetString() == "changed" && findings[0].GetProperty("unique_id").GetString() == uidB
                    && findings[0].GetProperty("core_id").GetString() == coreB.ToString("D"),
                    "mirror-drift reports exactly one changed finding: wall B, by UniqueId and Core id", drift1Text.Length > 1500 ? drift1Text[..1500] : drift1Text);
                Check(proposals.Length == 1 && proposals[0].GetProperty("unique_id").GetString() == uidB
                    && proposals[0].GetProperty("adopt").GetProperty("status").GetString() == MirrorDriftResolveStatus.Proposed
                    && proposals[0].GetProperty("adopt").GetProperty("action").GetString() == MirrorDriftAdoptActions.Update,
                    "the resolution proposes adopt (update) for wall B");
                Check(parsed.RootElement.GetProperty("applied").GetBoolean() == false && parsed.RootElement.GetProperty("core_store").GetProperty("revision").GetInt32() == 1,
                    "mirror-drift applies nothing and names the store at revision 1");
            }
            Check(new ProjectStore(store).Read().Revision == 1, "mirror-drift leaves the store at revision 1");

            // ---- mirror-drift-select: adopt wall B ----
            var choices = File_("choices", "{\"choices\":[{\"proposal_id\":" + JsonSerializer.Serialize(uidB) + ",\"action\":\"adopt\"}]}");
            var selectionPath = Path_("selection");
            var select = Cli("mirror-drift-select", store, snapshot1Path, mirror1, choices, selectionPath);
            Check(select.Exit == 0 && Number(select.Out, "operations") == 1 && File.Exists(selectionPath), "mirror-drift-select writes a selection with one BIM-IR operation", R(select));
            if (!File.Exists(selectionPath)) return null;
            var selectionText = File.ReadAllText(selectionPath, utf8);
            Check(new ProjectStore(store).Read().Revision == 1, "mirror-drift-select leaves the store at revision 1");

            // ---- mirror-drift-adopt: one Core revision ----
            var adopt = Cli("mirror-drift-adopt", store, selectionPath);
            Check(adopt.Exit == 0 && Status(adopt.Out) == "succeeded" && Text(adopt.Out, "outcome") == "committed" && Number(adopt.Out, "revision") == 2,
                "mirror-drift-adopt commits the selection as revision 2", R(adopt));
            var head = new ProjectStore(store).Read();
            var adoptedB = head.Elements.SingleOrDefault(element => element.Id == coreB);
            Check(head.Revision == 2 && adoptedB is not null && adoptedB.GeometryMetres.SequenceEqual(observedB) && adoptedB.Kind == "wall",
                "the store head holds wall B at the Revit geometry under its Core id", adoptedB is null ? "missing" : string.Join(",", adoptedB.GeometryMetres));
            Check(adoptedB is not null && adoptedB.Provenance == MirrorDriftResolve.AdoptProvenance, "the adopted wall carries the adopt provenance", adoptedB?.Provenance.ToString() ?? "");
            var before = project.Elements.Where(element => element.Id != coreB).ToDictionary(element => element.Id);
            var others = head.Elements.Where(element => element.Id != coreB).ToArray();
            Check(head.Id == ProjectId && others.Length == before.Count && others.All(element => before.TryGetValue(element.Id, out var original)
                    && original.Kind == element.Kind && original.GeometryMetres.SequenceEqual(element.GeometryMetres) && original.Dependencies.SequenceEqual(element.Dependencies) && original.Provenance == element.Provenance),
                "every other Core element is unchanged by the adopt");
            var projectJson = File.ReadAllText(Path.Combine(store, "project.json"), utf8);
            var replay = Cli("mirror-drift-adopt", store, selectionPath);
            Check(replay.Exit == 0 && Text(replay.Out, "outcome") == "replayed" && Number(replay.Out, "revision") == 2 && File.ReadAllText(Path.Combine(store, "project.json"), utf8) == projectJson,
                "a replay of the same selection prints replayed and writes nothing", R(replay));
            var stale = Cli("mirror-drift", store, snapshot1Path, mirror1, Path_("drift-stale"));
            Check(stale.Exit == 1 && Text(stale.Out, "code") == MirrorDriftResolveCodes.ReportStale, "the pre-adopt mirror (revision 1) is refused stale against the new head", R(stale));

            // ---- Second loop pass: a fresh snapshot against the mirror at revision 2 has no drift ----
            var snapshot2 = Command("p2-14-snapshot-2", "query.snapshot", _ => { });
            if (!Succeeded(snapshot2)) { Check(false, "the query.snapshot after the adopt succeeds", R(snapshot2)); return null; }
            Check(Readback(snapshot2.Out) == Readback(snapshot1.Out), "the Revit side is unchanged by the adopt (same snapshot readback)");
            var mirror2 = File_("mirror-r2", Mirror(2));
            var drift2Path = Path_("drift-2");
            var drift2 = Cli("mirror-drift", store, File_("snapshot-2", snapshot2.Out), mirror2, drift2Path);
            if (drift2.Exit != 0 || !File.Exists(drift2Path)) { Check(false, "the second mirror-drift succeeds", R(drift2)); return null; }
            var drift2Text = File.ReadAllText(drift2Path, utf8);
            using (var parsed = JsonDocument.Parse(drift2Text))
            {
                var reportElement = parsed.RootElement.GetProperty("report");
                var summary = reportElement.GetProperty("summary");
                Check(reportElement.GetProperty("findings").EnumerateArray().Any(finding => finding.GetProperty("unique_id").GetString() == uidB),
                    "the second mirror-drift reports no drift for the adopted wall B", drift2Text.Length > 1500 ? drift2Text[..1500] : drift2Text);
                Check(summary.GetProperty("drift").GetInt32() == 0 && summary.GetProperty("unchanged").GetInt32() == project.Elements.Length
                    && parsed.RootElement.GetProperty("resolution").GetProperty("proposals").GetArrayLength() == 0 && parsed.RootElement.GetProperty("core_store").GetProperty("revision").GetInt32() == 2,
                    "the second mirror-drift reports no drift at all: every element unchanged, no proposals, store at revision 2", summary.GetRawText());
            }
            Check(Number(drift2.Out, "drift") == 0 && Number(drift2.Out, "proposals") == 0, "the second mirror-drift prints drift 0 and proposals 0", R(drift2));
            Check(uidA != uidB, "two distinct walls were created");
            return new Loop(Readback(snapshot1.Out), drift1Text, selectionText, adopt.Out, projectJson, drift2Text);
        }
        finally
        {
            if (!server.HasExited) { try { server.Kill(true); } catch (InvalidOperationException) { } }
            server.WaitForExit(10000);
            _ = serverOut.Wait(5000);
            _ = serverErr.Wait(5000);
        }
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

    static string MirrorJson(string documentPath, int revision, SortedDictionary<Guid, string> map, SortedDictionary<Guid, string> levels)
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
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
