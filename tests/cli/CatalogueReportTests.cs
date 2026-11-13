using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Cli;

// P1-12 `catalogue-report --run .bim/native-runs/<run>`: the owner -Catalogue run summary, read-only.
// 1. In process (CatalogueReportCommand.Execute with a temporary repository root per case): the fixture records
//    tests/cli/catalogue-report/<case>/catalogue-run1.json (pass, fail, blocked, v1only; "__RUN__" is the run folder's
//    name) laid out as scripts/owner-native-session.ps1 -Catalogue lays them out: the record in
//    .bim/native-sessions/<stamp>/ next to summary.json (runs[].index, run_directory), the run folder
//    .bim/native-runs/<guid> holding the copy the copia group saved (written here: *.rvt is never committed).
// 2. End to end: tests/cli/catalogue-report-record.ps1 writes records with the real owner code (Invoke-CatalogueSequence
//    against the fake executor, the session script's own record writer) and the verb reads them.
// 3. A real-process smoke inside this repository's .bim (run and session folders named crtest-*, removed before and
//    after), plus static checks of the source and its registration.
internal static class CatalogueReportTests
{
    const string Stamp = "20261105T120000Z";
    const string CopyName = "BIM-Agentic-Test-c04-20261105T120000Z-r1-copia.rvt";
    const string CopySha = "3202cfd5c94a2241834e957759aa5fb26a21bed8e485646de3289cfca9aae862";
    static readonly byte[] CopyBytes = Encoding.ASCII.GetBytes("BIM-Agentic-Test copy fixture: not a real Revit file\n");
    const int MaxRecordBytes = 32 * 1024 * 1024;
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    sealed record Case(string Root, string Run, string Name, string Session);

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        var scratch = Path.Combine(work, "catalogue-report");
        Directory.CreateDirectory(scratch);
        void Check(bool condition, string name, string detail) => check(condition, "catalogue-report: " + name, detail);
        void Skip(string name, string why) => skip("catalogue-report: " + name, why);
        InProcess(root, scratch, Check, Skip);
        EndToEnd(root, scratch, dotnet, cliDll, Check, Skip);
        Smoke(root, scratch, dotnet, cliDll, Check);
    }

    // ---- shared helpers ----

    static string D((int Exit, string Out) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " output " + (r.Out.Length > 700 ? r.Out[..700] + "..." : r.Out);
    static string Failure(string code, string? detail) => detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
    static JsonElement? Json(string text)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }
    static string Raw(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value.GetRawText() : "<missing " + name + ">";
    static JsonElement[] Items(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
    static string? Str(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static string Short(string text) => text.Length > 900 ? text[..900] + "..." : text;
    static string Counts(string? key, int total, int passed, int failed, int blocked, int notSent) =>
        (key is null ? "{" : "{\"key\":\"" + key + "\",") + string.Format(CultureInfo.InvariantCulture, "\"total\":{0},\"passed\":{1},\"failed\":{2},\"blocked\":{3},\"not_sent\":{4}}}", total, passed, failed, blocked, notSent);
    static string Copy(string state, string? file, int? saveStep, bool? savePassed, bool? checkOk, string? recordedSha, long? recordedBytes, string? diskSha, long? diskBytes)
    {
        static string S(string? v) => v is null ? "null" : "\"" + v + "\"";
        static string B(bool? v) => v is null ? "null" : v.Value ? "true" : "false";
        static string N(long? v) => v is null ? "null" : v.Value.ToString(CultureInfo.InvariantCulture);
        return "{\"state\":" + S(state) + ",\"file\":" + S(file) + ",\"save_step\":" + N(saveStep) + ",\"save_passed\":" + B(savePassed) + ",\"recorded_check_ok\":" + B(checkOk)
            + ",\"recorded_sha256\":" + S(recordedSha) + ",\"recorded_bytes\":" + N(recordedBytes) + ",\"disk_sha256\":" + S(diskSha) + ",\"disk_bytes\":" + N(diskBytes) + "}";
    }
    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string Fixture(string root, string name) => File.ReadAllText(Path.Combine(root, "tests", "cli", "catalogue-report", name, "catalogue-run1.json"));
    static string WindowsRun(string name) => "C:\\Repo\\.bim\\native-runs\\" + name;
    static string Summary(string mode, params string[] runDirectories)
    {
        var entries = new JsonArray();
        for (var i = 0; i < runDirectories.Length; i++)
            entries.Add(new JsonObject
            {
                ["index"] = i + 1, ["run_directory"] = runDirectories[i], ["child_pid"] = 4242, ["bootstrap"] = "succeeded", ["outcome"] = "fixture",
                ["catalogue"] = new JsonObject { ["file"] = "C:\\Repo\\.bim\\native-sessions\\" + Stamp + "\\catalogue-run" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".json" },
            });
        return new JsonObject
        {
            ["schema_version"] = 1, ["mode"] = mode, ["catalogue_scope"] = "one_run_all_groups", ["started_utc"] = "2026-11-05T12:00:00.0000000Z",
            ["session"] = "C:\\Repo\\.bim\\native-sessions\\" + Stamp, ["head"] = "308c756", ["protected_revit_pids"] = new JsonArray(), ["source_check"] = "passed",
            ["builds"] = new JsonObject(), ["runs"] = entries, ["finished_utc"] = null, ["outcome"] = "fixture",
        }.ToJsonString(Indented);
    }
    static string Mutate(string template, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(template)!.AsObject();
        change(node);
        return node.ToJsonString(Indented);
    }
    static JsonObject StepAt(JsonObject record, int index) => record["steps"]![index]!.AsObject();
    // Recomputes groups, overall, counts and all_passed from the steps, as Invoke-CatalogueSequence does.
    static void Recount(JsonObject record)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var overall = new int[4];
        foreach (var node in record["steps"]!.AsArray())
        {
            var step = node!.AsObject();
            var group = step["group"]!.GetValue<string>();
            if (!counts.ContainsKey(group)) { order.Add(group); counts[group] = new int[4]; }
            foreach (var bucket in new[] { counts[group], overall })
            {
                bucket[0]++;
                if (step["passed"]!.GetValue<bool>()) bucket[1]++;
                if (step["blocked"]!.GetValue<bool>()) bucket[2]++;
                if (step["request"] is null) bucket[3]++;
            }
        }
        static JsonObject Tally(string? key, int[] c)
        {
            var o = new JsonObject();
            if (key is not null) { o["key"] = key; o["description"] = key; }
            o["total"] = c[0]; o["passed"] = c[1]; o["failed"] = c[0] - c[1] - c[2]; o["blocked"] = c[2]; o["not_sent"] = c[3];
            return o;
        }
        var groups = new JsonArray();
        foreach (var key in order) groups.Add(Tally(key, counts[key]));
        record["groups"] = groups;
        record["overall"] = Tally(null, overall);
        record["counts"] = Tally(null, counts.TryGetValue("v1", out var v1) ? v1 : new int[4]);
        record["all_passed"] = overall[0] - overall[1] - overall[2] == 0 && overall[2] == 0;
    }
    static string Recounted(string template, Action<JsonObject> change) => Mutate(template, r => { change(r); Recount(r); });

    // ---- 1. in process, one temporary repository root per case ----

    static void InProcess(string root, string scratch, Action<bool, string, string> check, Action<string, string> skip)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        var cases = 0;
        string NewRoot()
        {
            var caseRoot = Path.Combine(scratch, "r" + (++cases).ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.Combine(caseRoot, ".bim", "native-runs"));
            Directory.CreateDirectory(Path.Combine(caseRoot, ".bim", "native-sessions"));
            return caseRoot;
        }
        // A run folder (fixture, catalogue-requests/, the copy when given) and a session <Stamp> whose summary names it.
        Case Make(string? record, byte[]? copy = null, Func<string, string>? summaryRun = null, Func<string, string>? finish = null, string mode = "catalogue")
        {
            var caseRoot = NewRoot();
            var name = Guid.NewGuid().ToString();
            var run = Path.Combine(caseRoot, ".bim", "native-runs", name);
            Directory.CreateDirectory(Path.Combine(run, "catalogue-requests"));
            File.WriteAllText(Path.Combine(run, "BIM-Agentic-Test.rvt"), "fixture");
            File.WriteAllText(Path.Combine(run, "catalogue-requests", "01-q-levels.json"), "{}");
            if (copy is not null) File.WriteAllBytes(Path.Combine(run, CopyName), copy);
            if (record is null) return new Case(caseRoot, run, name, "");
            var session = Path.Combine(caseRoot, ".bim", "native-sessions", Stamp);
            Directory.CreateDirectory(session);
            File.WriteAllText(Path.Combine(session, "summary.json"), Summary(mode, summaryRun is null ? WindowsRun(name) : summaryRun(name)));
            var text = record.Replace("__RUN__", name, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(session, "catalogue-run1.json"), finish is null ? text : finish(text));
            return new Case(caseRoot, run, name, Stamp);
        }
        (int Exit, string Out) Report(Case c, string? run = null, long? maxCopy = null) =>
            maxCopy is null ? CatalogueReportCommand.Execute(["catalogue-report", "--run", run ?? c.Run], c.Root) : CatalogueReportCommand.Execute(["catalogue-report", "--run", run ?? c.Run], c.Root, maxCopy.Value);
        void Refused(string name, string code, string? detail, Case c, string? run = null)
        {
            var result = Report(c, run);
            Check(result.Exit == 1 && result.Out == Failure(code, detail), "refuses " + name + " (" + code + (detail is null ? "" : "/" + detail) + ")", D(result));
        }
        bool Link(string path, string target, bool directory, string name)
        {
            try { if (directory) Directory.CreateSymbolicLink(path, target); else File.CreateSymbolicLink(path, target); return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { skip(name, "symbolic links unavailable: " + e.GetType().Name); return false; }
        }
        static string Tree(Case c) => string.Join("\n", Directory.EnumerateFileSystemEntries(Path.Combine(c.Root, ".bim"), "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? p + "|" + new FileInfo(p).Length.ToString(CultureInfo.InvariantCulture) + "|" + new FileInfo(p).LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Sha(File.ReadAllBytes(p)) : p + "|dir"));

        var passTemplate = Fixture(root, "pass");
        var failTemplate = Fixture(root, "fail");
        Check(Sha(CopyBytes) == CopySha && passTemplate.Contains(CopySha, StringComparison.Ordinal), "fixture copy bytes hash to the sha256 the pass record holds");
        Check(passTemplate.Contains("\"code\": \"ok\"", StringComparison.Ordinal) && passTemplate.Contains("\"requested\": 3000.0", StringComparison.Ordinal), "fixtures follow the real writer (success code ok, doubles as 3000.0)");
        var passCopy = Copy("match", CopyName, 6, true, true, CopySha, 53, CopySha, 53);

        // 1a. Everything passed: counts per group, nothing failing or blocked, the copy hash matches; read-only, deterministic.
        var pass = Make(passTemplate, CopyBytes);
        var junk = Path.Combine(pass.Root, ".bim", "native-sessions", "20261105T110000Z");
        Directory.CreateDirectory(junk);
        File.WriteAllText(Path.Combine(junk, "summary.json"), "{");
        var before = Tree(pass);
        var first = Report(pass);
        var second = Report(pass);
        var report = Json(first.Out);
        Check(first.Exit == 0 && Raw(report, "status") == "\"succeeded\"", "a passing run is reported (exit 0); a session whose summary is not JSON is skipped", D(first));
        Check(first.Exit == 0 && first.Out == second.Out, "the report is deterministic (byte-identical twice)", D(second));
        Check(first.Out.All(c => c < 128), "the report is ASCII (non-ASCII escaped)", D(first));
        Check(Raw(report, "run") == "\".bim/native-runs/" + pass.Name + "\"" && Raw(report, "record") == "\".bim/native-sessions/" + Stamp + "/catalogue-run1.json\"", "the report names the run folder and the record it read", D(first));
        Check(Raw(report, "scope") == "\"one_run_all_groups\"" && Raw(report, "head") == "\"308c756\"" && Raw(report, "aborted") == "null" && Raw(report, "all_passed") == "true", "scope, head, aborted and all_passed of a passing run", D(first));
        Check(Raw(report, "overall") == Counts(null, 10, 10, 0, 0, 0), "overall counts of a passing run", Raw(report, "overall"));
        Check(Raw(report, "groups") == "[" + string.Join(",", Counts("v1", 2, 2, 0, 0, 0), Counts("estructura", 2, 2, 0, 0, 0), Counts("copia", 5, 5, 0, 0, 0), Counts("modificar", 1, 1, 0, 0, 0)) + "]", "per-group passed/failed/blocked counts in step order", Raw(report, "groups"));
        Check(Raw(report, "failing") == "[]" && Raw(report, "blocked") == "[]", "a passing run lists no failing or blocked step", D(first));
        Check(Raw(report, "copy") == passCopy, "the copy on disk matches the sha256 and size c-save recorded", Raw(report, "copy"));
        Check(Tree(pass) == before, "read-only: nothing under .bim changes");
        // P4-18: a record without the doc-up-elements step keeps today's bytes: the copy is the last member, no realized.
        Check(first.Out.EndsWith(",\"copy\":" + passCopy + "}", StringComparison.Ordinal) && Raw(report, "realized") == "<missing realized>", "a run without the doc-realized step prints no realized member (copy stays last)", D(first));
        Check(Report(pass, pass.Run + Path.DirectorySeparatorChar).Out == first.Out, "a trailing separator gives the same report");
        if (OperatingSystem.IsWindows()) Check(Report(pass, Path.Combine(Path.GetDirectoryName(pass.Run)!, pass.Name.ToUpperInvariant())).Out == first.Out, "the run name is echoed as on disk whatever the argument's case (Windows)");
        var forward = Make(passTemplate, CopyBytes, name => Path.Combine(Path.GetTempPath(), "x", ".bim", "native-runs", name).Replace('\\', '/'));
        Check(Report(forward).Exit == 0, "a summary run_directory with '/' separators is matched too");

        // 1b. The copy now: changed after the run, gone, outside the run, unsaved, without a recorded hash, too large, not a file.
        var tampered = (byte[])CopyBytes.Clone();
        tampered[0] ^= 1;
        var changed = Json(Report(Make(passTemplate, tampered)).Out);
        Check(Raw(changed, "copy") == Copy("mismatch", CopyName, 6, true, true, CopySha, 53, Sha(tampered), 53), "a same-size copy with other bytes is a sha256 mismatch", Raw(changed, "copy"));
        Check(Raw(changed, "all_passed") == "true", "a copy mismatch does not rewrite the run's own verdict");
        Check(Raw(Json(Report(Make(passTemplate, [.. CopyBytes, (byte)'x'])).Out), "copy").StartsWith("{\"state\":\"mismatch\"", StringComparison.Ordinal), "a longer copy is a mismatch");
        var goneReport = Json(Report(Make(passTemplate)).Out);
        Check(Raw(goneReport, "copy") == Copy("missing_file", CopyName, 6, true, true, CopySha, 53, null, null), "a missing copy is missing_file", Raw(goneReport, "copy"));
        foreach (var (label, path) in new[] { ("in another folder", "C:\\Repo\\.bim\\" + CopyName), ("not named as a catalogue copy", WindowsRun("__RUN__") + "\\summary.json") })
        {
            var outside = Json(Report(Make(Mutate(passTemplate, r => r["copy_path"] = path), CopyBytes)).Out);
            Check(Raw(outside, "status") == "\"succeeded\"" && Raw(outside, "copy") == Copy("outside_run", null, 6, true, true, null, null, null, null) && Raw(outside, "overall") == Counts(null, 10, 10, 0, 0, 0),
                "a copy path " + label + " is reported as outside_run and the rest of the report is kept", Raw(outside, "copy"));
        }
        var unsaved = Json(Report(Make(Recounted(passTemplate, r =>
        {
            var save = StepAt(r, 5);
            save["request"] = null; save["response"] = null; save["passed"] = false; save["exit_code"] = null; save["comparisons"] = new JsonArray();
            save["reason"] = "no enviado: falta copy_path de un paso anterior";
            r["copy_path"] = null;
        }), CopyBytes)).Out);
        Check(Raw(unsaved, "copy") == Copy("not_saved", null, 6, false, null, null, null, null, null), "c-save not sent: not_saved", Raw(unsaved, "copy"));
        var noSha = Json(Report(Make(Mutate(passTemplate, r => StepAt(r, 5)["response"]!["readback"]!["copy"]!["sha256"] = null), CopyBytes)).Out);
        Check(Raw(noSha, "copy") == Copy("no_recorded_sha256", CopyName, 6, true, true, null, 53, CopySha, 53), "no sha256 read back: no_recorded_sha256 (the file is still hashed)", Raw(noSha, "copy"));
        var v1Only = Json(Report(Make(Fixture(root, "v1only"))).Out);
        Check(Raw(v1Only, "scope") == "\"v1_only\"" && Raw(v1Only, "overall") == Counts(null, 2, 2, 0, 0, 0) && Raw(v1Only, "copy") == Copy("not_in_run", null, null, null, null, null, null, null, null),
            "a v1-only run has no copy step: not_in_run", Raw(v1Only, "copy"));
        var capped = Make(passTemplate, CopyBytes);
        var atCopyCap = Json(Report(capped, maxCopy: 53).Out);
        var overCopyCap = Json(Report(capped, maxCopy: 52).Out);
        Check(Raw(atCopyCap, "copy") == passCopy && Raw(overCopyCap, "copy") == Copy("too_large", CopyName, 6, true, true, CopySha, 53, null, 53),
            "a copy of exactly the hashing cap is hashed; one byte over is too_large", Raw(overCopyCap, "copy"));
        var folderCopy = Make(passTemplate);
        Directory.CreateDirectory(Path.Combine(folderCopy.Run, CopyName));
        Refused("a folder in place of the copy", "catalogue_report_copy_not_regular_file", null, folderCopy);
        if (OperatingSystem.IsLinux())
        {
            var fifoCase = Make(passTemplate);
            var fifo = Path.Combine(fifoCase.Run, CopyName);
            using (var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo }, UseShellExecute = false }))
            {
                mkfifo?.WaitForExit(10000);
            }
            if (!File.Exists(fifo)) skip("refuses a FIFO in place of the copy", "mkfifo did not create the FIFO");
            else
            {
                var task = Task.Run(() => Report(fifoCase));
                if (!task.Wait(30000))
                {
                    Check(false, "refuses a FIFO in place of the copy without opening it", "the report blocked on the FIFO");
                    try { using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); } catch (IOException) { }
                }
                else Check(task.Result.Exit == 1 && task.Result.Out == Failure("catalogue_report_copy_not_regular_file", null), "refuses a FIFO in place of the copy without opening it", D(task.Result));
            }
        }
        else skip("refuses a FIFO in place of the copy", "mkfifo is used on Linux only");

        // 1c. Failures: requested vs observed rows, a refused response, the failed save, a step not sent.
        var failRun = Report(Make(failTemplate, CopyBytes));
        var failReport = Json(failRun.Out);
        Check(failRun.Exit == 0 && Raw(failReport, "all_passed") == "false" && Raw(failReport, "overall") == Counts(null, 10, 6, 4, 0, 1), "overall counts of a failing run", D(failRun));
        Check(Raw(failReport, "groups") == "[" + string.Join(",", Counts("v1", 2, 1, 1, 0, 0), Counts("estructura", 2, 1, 1, 0, 0), Counts("copia", 5, 3, 2, 0, 1), Counts("modificar", 1, 1, 0, 0, 0)) + "]", "per-group counts of a failing run", Raw(failReport, "groups"));
        var failing = Items(failReport, "failing");
        Check(failing.Length == 4 && string.Join(",", failing.Select(f => Raw(f, "index") + ":" + Raw(f, "key"))) == "2:\"w-south\",4:\"s-column\",6:\"c-save\",7:\"c-save-again\"", "the failing steps, in order", Raw(failReport, "failing"));
        if (failing.Length == 4)
        {
            Check(Raw(failing[0], "mismatches") == "[{\"check\":\"altura (mm)\",\"requested\":3000.0,\"observed\":2950}]" && Str(failing[0], "reason") == "altura (mm): se pidió 3000.0, se observó 2950"
                && Raw(failing[0], "sent") == "true" && Raw(failing[0], "observed_status") == "\"succeeded\"" && Raw(failing[0], "observed_code") == "\"ok\"" && Raw(failing[0], "command") == "\"wall.create\"" && Raw(failing[0], "mismatches_omitted") == "0" && Raw(failing[0], "raw_response") == "null",
                "a failing check is printed requested vs observed (numbers as the writer wrote them)", failing[0].GetRawText());
            Check(Raw(failing[1], "mismatches") == "[]" && Raw(failing[1], "observed_status") == "\"failed\"" && Raw(failing[1], "observed_code") == "\"type_not_found\"" && Raw(failing[1], "expected") == "{\"Status\":\"succeeded\",\"Created\":1}",
                "a refused response is printed with the expected status and the observed status and code", failing[1].GetRawText());
            var shaRows = Items(failing[2], "mismatches");
            Check(shaRows.Length == 1 && Str(shaRows[0], "check") == "sha256 leído = sha256 del archivo en disco" && Raw(shaRows[0], "requested") == "\"" + CopySha + "\"" && Raw(shaRows[0], "observed") == "\"" + Sha(Encoding.ASCII.GetBytes("other bytes\n")) + "\"",
                "the failed run-time sha256 check of c-save is printed requested vs observed", failing[2].GetRawText());
            Check(Raw(failing[3], "sent") == "false" && Raw(failing[3], "observed_status") == "null" && Str(failing[3], "reason") == "no enviado: falta copy_path de un paso anterior", "a step not sent (and not blocked) is failing with its reason", failing[3].GetRawText());
        }
        Check(Raw(failReport, "copy") == Copy("match", CopyName, 6, false, false, CopySha, 53, CopySha, 53), "without copy_path the copy is taken from c-save's readback; the run-time check and the check now are both reported", Raw(failReport, "copy"));

        // 1d. Blocked steps (missing fixture preconditions), one never sent: blocked, not failed.
        var blockedReport = Json(Report(Make(Fixture(root, "blocked"), CopyBytes)).Out);
        Check(Raw(blockedReport, "overall") == Counts(null, 10, 8, 0, 2, 1) && Raw(blockedReport, "all_passed") == "false" && Raw(blockedReport, "failing") == "[]", "blocked steps are counted apart from failures", Raw(blockedReport, "overall"));
        var blocked = Items(blockedReport, "blocked");
        Check(blocked.Length == 2 && Raw(blocked[0], "index") == "3" && Raw(blocked[0], "key") == "\"s-grid\"" && Raw(blocked[0], "sent") == "false" && Str(blocked[0], "reason") == "no enviado, bloqueado: falta en el modelo de prueba: segundo nivel"
            && Raw(blocked[1], "index") == "4" && Raw(blocked[1], "sent") == "true", "the blocked steps are listed with their reason", Raw(blockedReport, "blocked"));

        // 1e. An aborted run (lost pipe): the reason and the steps never sent.
        const string abortedText = "se perdió la conexión con el Revit de prueba en el paso 8 (pipe_io_error)";
        var aborted = Json(Report(Make(Recounted(passTemplate, r =>
        {
            r["aborted"] = abortedText;
            for (var i = 7; i < 10; i++) { var s = StepAt(r, i); s["request"] = null; s["response"] = null; s["passed"] = false; s["exit_code"] = null; s["comparisons"] = new JsonArray(); s["reason"] = "no enviado: " + abortedText; }
        }), CopyBytes)).Out);
        Check(Str(aborted, "aborted") == abortedText && Raw(aborted, "overall") == Counts(null, 10, 7, 3, 0, 3) && string.Join(",", Items(aborted, "failing").Select(f => Raw(f, "index") + Raw(f, "sent"))) == "8false,9false,10false",
            "an aborted run reports why it stopped and the steps never sent", Short(Raw(aborted, "failing")));

        // 1f. Bounded output: long texts cut, large values replaced by their size, at most 1000 mismatch rows in all;
        // a non-JSON response is shown (bounded), a reason that is not text is echoed as JSON, a null command is kept.
        var longReport = Json(Report(Make(Mutate(failTemplate, r => { StepAt(r, 1)["reason"] = new string('x', 5000); StepAt(r, 1)["comparisons"]![0]!["requested"] = new string('a', 3000); }), CopyBytes)).Out);
        var longFirst = Items(longReport, "failing").FirstOrDefault();
        Check(Str(longFirst, "reason") == new string('x', 2000) + " ...(+3000 chars)" && Raw(longFirst, "mismatches") == "[{\"check\":\"altura (mm)\",\"requested\":{\"truncated_chars\":3002},\"observed\":2950}]",
            "texts over 2000 chars are cut and JSON values over 1024 chars are replaced by their size", Short(Raw(longReport, "failing")));
        var budget = Json(Report(Make(Recounted(passTemplate, r =>
        {
            for (var i = 0; i < 5; i++)
            {
                var rows = new JsonArray();
                for (var j = 0; j < 256; j++) rows.Add(new JsonObject { ["check"] = "c" + j.ToString(CultureInfo.InvariantCulture), ["requested"] = 1, ["observed"] = 2, ["ok"] = false });
                var s = StepAt(r, i); s["passed"] = false; s["comparisons"] = rows; s["reason"] = "many";
            }
        }), CopyBytes)).Out);
        var budgetText = string.Join(",", Items(budget, "failing").Select(f => Items(f, "mismatches").Length.ToString(CultureInfo.InvariantCulture) + "/" + Raw(f, "mismatches_omitted")));
        Check(budgetText == "256/0,256/0,256/0,232/24,0/256", "at most 1000 requested-vs-observed rows are printed in all; the rest are counted per step", budgetText);
        var oddReport = Json(Report(Make(Mutate(failTemplate, r =>
        {
            var s = StepAt(r, 3); s["response"] = null; s["raw_response"] = "<html>" + new string('y', 2500); s["reason"] = "response is not JSON";
            StepAt(r, 1)["reason"] = new JsonArray("a", "b");
            StepAt(r, 0)["command"] = null;
        }), CopyBytes)).Out);
        var odd = Items(oddReport, "failing");
        Check(odd.Length == 4 && Str(odd[1], "raw_response") == "<html>" + new string('y', 1994) + " ...(+506 chars)" && Raw(odd[1], "observed_status") == "null" && Raw(odd[0], "reason") == "[\"a\",\"b\"]" && Raw(oddReport, "status") == "\"succeeded\"",
            "a non-JSON response is shown cut at 2000 chars, a non-text reason is echoed as JSON, a step without command is accepted", Short(Raw(oddReport, "failing")));

        // 1g. Malformed or inconsistent records: typed refusals.
        void Bad(string name, string code, string? detail, string text) => Refused("a record " + name, code, detail, Make(text, CopyBytes));
        void BadMutation(string name, string code, string? detail, Action<JsonObject> change) => Bad(name, code, detail, Mutate(passTemplate, change));
        Bad("that is not JSON", "catalogue_report_record_invalid", "json", "{");
        Bad("that is empty", "catalogue_report_record_invalid", "json", "");
        Bad("that is an array", "catalogue_report_record_invalid", "root", "[]");
        Bad("with a duplicate key", "catalogue_report_record_invalid", "duplicate_key", passTemplate.Replace("\"head\": \"308c756\",", "\"head\": \"308c756\", \"head\": \"308c756\",", StringComparison.Ordinal));
        BadMutation("of another schema version", "catalogue_report_record_invalid", "schema_version", r => r["schema_version"] = 2);
        BadMutation("of another run index", "catalogue_report_record_mismatch", "run", r => r["run"] = 2);
        BadMutation("about another run folder", "catalogue_report_record_mismatch", "document_path", r => r["document_path"] = "C:\\Repo\\.bim\\native-runs\\other\\BIM-Agentic-Test.rvt");
        BadMutation("of another session", "catalogue_report_record_mismatch", "session_stamp", r => r["session_stamp"] = "20261105T130000Z");
        BadMutation("whose session_stamp is not text", "catalogue_report_record_invalid", "session_stamp", r => r["session_stamp"] = 1);
        BadMutation("without steps", "catalogue_report_record_invalid", "steps", r => r.Remove("steps"));
        BadMutation("with no step", "catalogue_report_record_invalid", "steps", r => r["steps"] = new JsonArray());
        BadMutation("with a step without passed", "catalogue_report_record_invalid", "steps[1].passed", r => StepAt(r, 1).Remove("passed"));
        BadMutation("with a string passed", "catalogue_report_record_invalid", "steps[1].passed", r => StepAt(r, 1)["passed"] = "yes");
        BadMutation("with steps out of order", "catalogue_report_record_invalid", "steps[2].index", r => StepAt(r, 2)["index"] = 7);
        BadMutation("with a step both passed and blocked", "catalogue_report_record_invalid", "steps[0].blocked", r => StepAt(r, 0)["blocked"] = true);
        BadMutation("with a passed step never sent", "catalogue_report_record_invalid", "steps[0].request", r => StepAt(r, 0)["request"] = null);
        BadMutation("with a command that is not text", "catalogue_report_record_invalid", "steps[0].command", r => StepAt(r, 0)["command"] = 5);
        BadMutation("with comparisons that are not a list", "catalogue_report_record_invalid", "steps[1].comparisons", r => StepAt(r, 1)["comparisons"] = "none");
        BadMutation("with a comparison without ok", "catalogue_report_record_invalid", "steps[1].comparisons[0].ok", r => StepAt(r, 1)["comparisons"]![0]!.AsObject().Remove("ok"));
        BadMutation("with a group key that is not a key", "catalogue_report_record_invalid", "steps[0].group", r => StepAt(r, 0)["group"] = "v 1");
        BadMutation("whose group counts differ from its steps", "catalogue_report_record_inconsistent", "groups", r => r["groups"]![0]!["passed"] = 1);
        BadMutation("whose groups are reordered", "catalogue_report_record_inconsistent", "groups", r => { var g = r["groups"]!.AsArray(); var x = g[0]!; g.RemoveAt(0); g.Add(x); });
        BadMutation("whose overall counts differ from its steps", "catalogue_report_record_inconsistent", "overall", r => r["overall"]!["failed"] = 1);
        BadMutation("whose v1 counts differ from its steps", "catalogue_report_record_inconsistent", "counts", r => r["counts"]!["total"] = 17);
        BadMutation("whose all_passed differs from its steps", "catalogue_report_record_inconsistent", "all_passed", r => r["all_passed"] = false);
        BadMutation("whose aborted is not text", "catalogue_report_record_invalid", "aborted", r => r["aborted"] = 3);
        BadMutation("whose copy_path is not text", "catalogue_report_record_invalid", "copy_path", r => r["copy_path"] = 3);

        // 1h. Bounds: the record's size (exactly the cap is read), steps, comparisons, session summaries.
        static string Pad(string text, int bytes) => text + new string(' ', bytes - Encoding.UTF8.GetByteCount(text));
        var atCap = Report(Make(passTemplate, CopyBytes, finish: text => Pad(text, MaxRecordBytes)));
        Check(atCap.Exit == 0 && Raw(Json(atCap.Out), "overall") == Counts(null, 10, 10, 0, 0, 0), "a record of exactly 32 MiB is read", D(atCap));
        Refused("a record of 32 MiB + 1 byte", "input_too_large", "record", Make(passTemplate, CopyBytes, finish: text => Pad(text, MaxRecordBytes + 1)));
        BadMutation("with 1001 steps", "catalogue_report_bound_exceeded", "steps", r =>
        {
            var steps = new JsonArray();
            for (var i = 0; i < 1001; i++) { var step = StepAt(r, 0).DeepClone().AsObject(); step["index"] = i + 1; steps.Add(step); }
            r["steps"] = steps;
        });
        BadMutation("with 257 comparisons in a step", "catalogue_report_bound_exceeded", "comparisons", r =>
        {
            var rows = new JsonArray();
            for (var i = 0; i < 257; i++) rows.Add(new JsonObject { ["check"] = "c", ["requested"] = 1, ["observed"] = 1, ["ok"] = true });
            StepAt(r, 1)["comparisons"] = rows;
        });
        var crowded = Make(passTemplate, CopyBytes);
        string Crowd(int i) => Path.Combine(crowded.Root, ".bim", "native-sessions", "20251105T" + i.ToString("D6", CultureInfo.InvariantCulture) + "Z");
        for (var i = 0; i < 1001; i++)
        {
            Directory.CreateDirectory(Crowd(i));
            File.WriteAllText(Path.Combine(Crowd(i), "summary.json"), Summary("room_ab", WindowsRun("unrelated")));
        }
        var crowdedRun = Report(crowded);
        Check(crowdedRun.Exit == 0, "summaries of another mode do not count toward the 1000-session bound", D(crowdedRun));
        for (var i = 0; i < 1000; i++) File.WriteAllText(Path.Combine(Crowd(i), "summary.json"), Summary("catalogue", WindowsRun("unrelated")));
        Refused("more than 1000 catalogue session summaries", "catalogue_report_bound_exceeded", "sessions", crowded);

        // 1i. Finding the record through the session summaries.
        Refused("a run folder no session names", "catalogue_report_record_not_found", null, Make(null, CopyBytes));
        Refused("a run named only by a summary of another mode", "catalogue_report_record_not_found", null, Make(passTemplate, CopyBytes, mode: "room_ab"));
        var twice = Make(passTemplate, CopyBytes);
        var twin = Path.Combine(twice.Root, ".bim", "native-sessions", "20261105T130000Z");
        Directory.CreateDirectory(twin);
        File.WriteAllText(Path.Combine(twin, "summary.json"), Summary("catalogue", WindowsRun(twice.Name)));
        Refused("a run two sessions name", "catalogue_report_record_ambiguous", Stamp + "/catalogue-run1.json,20261105T130000Z/catalogue-run1.json", twice);
        var noRecord = Make(passTemplate, CopyBytes);
        File.Delete(Path.Combine(noRecord.Root, ".bim", "native-sessions", Stamp, "catalogue-run1.json"));
        Refused("a run whose session has no record file", "catalogue_report_record_not_found", Stamp + "/catalogue-run1.json", noRecord);
        var secondRun = Make(null, CopyBytes);
        var secondSession = Path.Combine(secondRun.Root, ".bim", "native-sessions", Stamp);
        Directory.CreateDirectory(secondSession);
        File.WriteAllText(Path.Combine(secondSession, "summary.json"), Summary("catalogue", WindowsRun("another-run"), WindowsRun(secondRun.Name)));
        File.WriteAllText(Path.Combine(secondSession, "catalogue-run2.json"), passTemplate.Replace("__RUN__", secondRun.Name, StringComparison.Ordinal));
        Refused("a record whose run differs from the summary index", "catalogue_report_record_mismatch", "run", secondRun);
        File.WriteAllText(Path.Combine(secondSession, "catalogue-run2.json"), Mutate(passTemplate, r => r["run"] = 2).Replace("__RUN__", secondRun.Name, StringComparison.Ordinal));
        var secondReport = Report(secondRun);
        Check(Raw(Json(secondReport.Out), "record") == "\".bim/native-sessions/" + Stamp + "/catalogue-run2.json\"", "the record of the second run of a session is catalogue-run2.json", D(secondReport));

        // 1j. The run folder: only exactly one level below <root>/.bim/native-runs, after normalisation.
        var runs = Path.Combine(pass.Root, ".bim", "native-runs");
        foreach (var bad in new[] { new[] { "catalogue-report" }, new[] { "catalogue-report", "--run" }, new[] { "catalogue-report", "--run", "" }, new[] { "catalogue-report", "--run", "--x" },
            new[] { "catalogue-report", "--dir", pass.Run }, new[] { "catalogue-report", "--run", pass.Run, "extra" }, new[] { "catalogue-report", pass.Run, "--run" } })
        {
            var result = CatalogueReportCommand.Execute(bad, pass.Root);
            Check(result.Exit == 1 && result.Out == Failure("catalogue_report_usage", null), "refuses " + bad.Length.ToString(CultureInfo.InvariantCulture) + " arguments " + string.Join(" ", bad.Where(a => a.StartsWith("--", StringComparison.Ordinal))) + " (catalogue_report_usage)", D(result));
        }
        var outsideCopy = Path.Combine(scratch, pass.Name);
        Directory.CreateDirectory(outsideCopy);
        Refused("a run folder outside the repository", "catalogue_report_run_outside", null, pass, outsideCopy);
        Refused("the repository root", "catalogue_report_run_outside", null, pass, pass.Root);
        Refused(".bim itself", "catalogue_report_run_outside", null, pass, Path.Combine(pass.Root, ".bim"));
        Refused(".bim/native-runs itself", "catalogue_report_run_outside", null, pass, runs);
        Refused("a folder inside a run folder", "catalogue_report_run_outside", null, pass, Path.Combine(pass.Run, "catalogue-requests"));
        Refused("a sibling of native-runs", "catalogue_report_run_outside", null, pass, Path.Combine(pass.Root, ".bim", "native-runs-x", pass.Name));
        Refused("a session folder", "catalogue_report_run_outside", null, pass, Path.Combine(pass.Root, ".bim", "native-sessions", Stamp));
        Refused("a run of another repository", "catalogue_report_run_outside", null, pass, twice.Run);
        Refused("a '..' that comes back into native-runs", "catalogue_report_run_traversal", null, pass, pass.Run + "/../" + pass.Name);
        Refused("a '..' through native-runs", "catalogue_report_run_traversal", null, pass, Path.Combine(runs, "..", "native-runs", pass.Name));
        Refused("a relative '..'", "catalogue_report_run_traversal", null, pass, "../" + pass.Name);
        Refused("a control character", "catalogue_report_run_invalid", null, pass, pass.Run + "\t");
        Refused("a wildcard in the run name", "catalogue_report_run_invalid", null, pass, Path.Combine(runs, "a*b"));
        Refused("a run folder that does not exist", "catalogue_report_run_not_found", "run", pass, Path.Combine(runs, Guid.NewGuid().ToString()));
        var file = Path.Combine(runs, Guid.NewGuid().ToString());
        File.WriteAllText(file, "not a folder");
        Refused("a file in native-runs", "catalogue_report_run_not_found", "run", pass, file);

        // 1l. P4-18: the doc-up-elements step (doc-realized) and what it left in the run folder,
        // catalogue-requests/NN-doc-up-elements.doc-realized/{realized.json,report.json}.
        const string realizedFolder = "11-doc-up-elements.doc-realized";
        var realizedBytes = Encoding.UTF8.GetBytes("{\"element:door-1\":\"uid-door\",\"element:wall-1\":\"uid-wall\"}");
        var realizedSha = Sha(realizedBytes);
        var realizedUpper = realizedSha.ToUpperInvariant();
        long realizedLength = realizedBytes.Length;
        const string duplicate = "realized_unique_id_duplicate";
        JsonObject Succeeded(string sha, long bytes, int entries) => new() { ["status"] = "succeeded", ["output"] = "C:\\Repo\\.bim\\native-runs\\run\\catalogue-requests\\" + realizedFolder + "\\realized.json", ["entries"] = entries, ["bytes"] = bytes, ["sha256"] = sha };
        static JsonObject Refusal(string code) => new() { ["status"] = "failed", ["code"] = code, ["detail"] = "element:door-1" };
        // The pass record with the doc-realized step as step 11 (documentacion group), as Invoke-CatalogueSequence records it.
        string WithRealized(JsonNode? response, bool passed, bool sent = true, string command = "doc-realized", string? raw = null) => Recounted(passTemplate, r =>
            r["steps"]!.AsArray().Add(new JsonObject
            {
                ["index"] = 11, ["group"] = "documentacion", ["key"] = "doc-up-elements", ["description"] = "Actualización: doc-realized", ["command"] = command, ["operation_id"] = null,
                ["document_path"] = WindowsRun("__RUN__") + "\\BIM-Agentic-Test.rvt", ["expected"] = new JsonObject { ["Status"] = "succeeded" },
                ["request"] = sent ? new JsonObject { ["record_steps"] = 10, ["record_document_unique_id"] = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b", ["fixture"] = new JsonObject(), ["assignments"] = new JsonObject { ["element:wall-1"] = "doc-up-wall" }, ["document"] = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b", ["record_file"] = "C:\\Repo\\record.json" } : null,
                ["response"] = response, ["raw_response"] = raw, ["exit_code"] = sent ? (passed ? 0 : 1) : null, ["timed_out"] = false, ["elapsed_ms"] = sent ? 41.5 : null,
                ["passed"] = passed, ["blocked"] = false, ["reason"] = passed ? null : "doc-realized: rechazado", ["comparisons"] = new JsonArray(),
            }));
        // realized.json / report.json in the step's folder (null: not written); folder false: no folder at all. By default
        // (AutoReport) a succeeded reply is kept as report.json exactly as the owner invoker keeps the CLI line;
        // NoReport writes none.
        const string AutoReport = "\u0001auto", NoReport = "\u0001none";
        Case MakeRealized(string record, byte[]? realized, string? reportLine = AutoReport, bool folder = true)
        {
            var c = Make(record, CopyBytes);
            var directory = Path.Combine(c.Run, "catalogue-requests", realizedFolder);
            if (!folder) return c;
            Directory.CreateDirectory(directory);
            if (realized is not null) File.WriteAllBytes(Path.Combine(directory, "realized.json"), realized);
            if (reportLine == AutoReport)
            {
                var reply = JsonNode.Parse(record)!["steps"]!.AsArray()[^1]!["response"];
                reportLine = reply is JsonObject o && (string?)o["status"] == "succeeded" ? o.ToJsonString() : null;
            }
            if (reportLine is not null && reportLine != NoReport) File.WriteAllText(Path.Combine(directory, "report.json"), reportLine);
            return c;
        }
        static string Realized(string state, string? detail, int? entries, string? sha, long? bytes, string? code, string? replyStatus, string? replyCode, int? replyEntries, string? replySha, long? replyBytes, string? diskSha, long? diskBytes, bool passed = true)
        {
            static string S(string? v) => v is null ? "null" : "\"" + v + "\"";
            static string N(long? v) => v is null ? "null" : v.Value.ToString(CultureInfo.InvariantCulture);
            return "{\"step\":11,\"passed\":" + (passed ? "true" : "false") + ",\"state\":" + S(state) + ",\"detail\":" + S(detail) + ",\"folder\":\"catalogue-requests/" + realizedFolder + "\",\"entries\":" + N(entries) + ",\"sha256\":" + S(sha) + ",\"bytes\":" + N(bytes)
                + ",\"code\":" + S(code) + ",\"reply_status\":" + S(replyStatus) + ",\"reply_code\":" + S(replyCode) + ",\"reply_entries\":" + N(replyEntries) + ",\"reply_sha256\":" + S(replySha?.ToLowerInvariant()) + ",\"reply_bytes\":" + N(replyBytes)
                + ",\"disk_sha256\":" + S(diskSha) + ",\"disk_bytes\":" + N(diskBytes) + "}";
        }
        string RealizedOf(Case c) { var r = Report(c); return r.Exit == 0 ? Raw(Json(r.Out), "realized") : D(r); }
        var okRecord = WithRealized(Succeeded(realizedUpper, realizedLength, 2), passed: true);
        var matchCase = MakeRealized(okRecord, realizedBytes);
        var matchBefore = Tree(matchCase);
        var matchRun = Report(matchCase);
        var matchExpected = Realized("match", null, 2, realizedSha, realizedLength, null, "succeeded", null, 2, realizedUpper, realizedLength, realizedSha, realizedLength);
        Check(matchRun.Exit == 0 && matchRun.Out.EndsWith(",\"copy\":" + passCopy + ",\"realized\":" + matchExpected + "}", StringComparison.Ordinal) && Raw(Json(matchRun.Out), "overall") == Counts(null, 11, 11, 0, 0, 0),
            "the doc-realized step is listed after the copy with the entry count and sha256 of its realized.json (match)", D(matchRun));
        Check(Report(matchCase).Out == matchRun.Out && Tree(matchCase) == matchBefore, "the realized summary is deterministic and read-only");
        var tamperedMap = (byte[])realizedBytes.Clone();
        tamperedMap[^3] ^= 1;
        var tamperedRun = Report(MakeRealized(okRecord, tamperedMap));
        Check(Raw(Json(tamperedRun.Out), "realized") == Realized("mismatch", "sha256", null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength, Sha(tamperedMap), realizedLength) && Raw(Json(tamperedRun.Out), "all_passed") == "true",
            "a realized.json whose sha256 differs from the step reply is a typed mismatch: no entry count or sha256 is trusted, the run's verdict stands", D(tamperedRun));
        Check(RealizedOf(MakeRealized(WithRealized(Succeeded(realizedUpper, realizedLength + 1, 2), passed: true), realizedBytes)) == Realized("mismatch", "bytes", null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength + 1, realizedSha, realizedLength),
            "a realized.json whose size differs from the step reply is a mismatch (bytes)");
        Check(RealizedOf(MakeRealized(WithRealized(Succeeded("not-a-sha", realizedLength, 2), passed: true), realizedBytes)) == Realized("mismatch", "sha256", null, null, null, null, "succeeded", null, 2, null, realizedLength, realizedSha, realizedLength),
            "a step reply without a 64-hex sha256 trusts nothing (mismatch)");
        Check(RealizedOf(MakeRealized(okRecord, null)) == Realized("missing_file", null, null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength, null, null), "a succeeded step without realized.json is missing_file");
        Check(RealizedOf(MakeRealized(okRecord, null, folder: false)) == Realized("missing_folder", null, null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength, null, null), "a step whose folder is not in the run is missing_folder");
        var folderMap = MakeRealized(okRecord, null);
        Directory.CreateDirectory(Path.Combine(folderMap.Run, "catalogue-requests", realizedFolder, "realized.json"));
        Check(RealizedOf(folderMap) == Realized("unreadable", "input_not_regular_file", null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength, null, null), "a folder in place of realized.json is unreadable (input_not_regular_file)");
        var hugeMap = new byte[8 * 1024 * 1024 + 1];
        Check(RealizedOf(MakeRealized(WithRealized(Succeeded(Sha(hugeMap), hugeMap.Length, 0), passed: true), hugeMap)) == Realized("unreadable", "input_too_large", null, null, null, null, "succeeded", null, 0, Sha(hugeMap), hugeMap.Length, null, null),
            "a realized.json over the doc-update --realized cap (8 MiB) is not read (unreadable, input_too_large)");
        foreach (var (label, text, entries, detail) in new[] { ("an array", "[1]", 2, "json"), ("a duplicate key", "{\"a\":\"x\",\"a\":\"y\"}", 2, "json"), ("a non-text value", "{\"a\":1}", 1, "json"), ("not JSON", "{", 2, "json"), ("another entry count", "{\"a\":\"x\"}", 2, "entries") })
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Check(RealizedOf(MakeRealized(WithRealized(Succeeded(Sha(bytes), bytes.Length, entries), passed: true), bytes)) == Realized("invalid", detail, null, null, null, null, "succeeded", null, entries, Sha(bytes), bytes.Length, Sha(bytes), bytes.Length),
                "a realized.json that hashes as replied but holds " + label + " is invalid (" + detail + ")");
        }
        var refusedLine = Refusal(duplicate).ToJsonString();
        var refusedRecord = WithRealized(Refusal(duplicate), passed: false);
        var refusedRun = Report(MakeRealized(refusedRecord, null, refusedLine));
        Check(Raw(Json(refusedRun.Out), "realized") == Realized("refused", null, null, null, null, duplicate, "failed", duplicate, null, null, null, null, null, passed: false)
            && Items(Json(refusedRun.Out), "failing").Any(f => Raw(f, "index") == "11" && Str(f, "observed_code") == duplicate), "a realized_* refusal is listed with the code its report.json holds (and the step is failing)", D(refusedRun));
        Check(RealizedOf(MakeRealized(refusedRecord, null, Refusal("realized_step_unknown").ToJsonString())) == Realized("report_mismatch", "report_differs", null, null, null, null, "failed", duplicate, null, null, null, null, null, passed: false), "a report.json whose code differs from the step reply is report_mismatch");
        Check(RealizedOf(MakeRealized(refusedRecord, null)) == Realized("report_mismatch", "report_missing", null, null, null, null, "failed", duplicate, null, null, null, null, null, passed: false), "a refusal without report.json is report_mismatch (report_missing)");
        Check(RealizedOf(MakeRealized(refusedRecord, null, "not json")) == Realized("report_mismatch", "report_invalid", null, null, null, null, "failed", duplicate, null, null, null, null, null, passed: false), "a report.json that is not a failure envelope is report_mismatch (report_invalid)");
        Check(RealizedOf(MakeRealized(refusedRecord, realizedBytes, refusedLine)) == Realized("report_mismatch", "output_present", null, null, null, null, "failed", duplicate, null, null, null, null, null, passed: false), "a refusal next to a realized.json is report_mismatch (output_present)");
        var otherRefusal = WithRealized(Refusal("input_too_large"), passed: false);
        Check(RealizedOf(MakeRealized(otherRefusal, null, Refusal("input_too_large").ToJsonString())) == Realized("refused", null, null, null, null, "input_too_large", "failed", "input_too_large", null, null, null, null, null, passed: false), "another doc-realized refusal is listed with its code too");
        Check(RealizedOf(MakeRealized(WithRealized(null, passed: false, sent: false), null, folder: false)) == Realized("not_sent", null, null, null, null, null, null, null, null, null, null, null, null, passed: false), "a doc-realized step never sent is not_sent");
        Check(RealizedOf(MakeRealized(WithRealized(null, passed: false, raw: "garbage"), null)) == Realized("no_reply", null, null, null, null, null, null, null, null, null, null, null, null, passed: false), "a doc-realized step without a JSON reply is no_reply");
        Check(RealizedOf(MakeRealized(WithRealized(new JsonObject { ["status"] = "partial" }, passed: false), null)) == Realized("no_reply", "status", null, null, null, null, "partial", null, null, null, null, null, null, passed: false), "a reply that is neither succeeded nor failed is no_reply (status)");
        var otherCommand = Report(MakeRealized(WithRealized(Succeeded(realizedUpper, realizedLength, 2), passed: true, command: "doc-update"), realizedBytes));
        Check(otherCommand.Exit == 0 && otherCommand.Out.EndsWith(",\"copy\":" + passCopy + "}", StringComparison.Ordinal), "a doc-up-elements step that is not doc-realized is not summarized", D(otherCommand));
        // Review: on a succeeded reply the kept line (report.json) must say what the reply says; else nothing is trusted.
        string NoDisk(string state, string? detail) => Realized(state, detail, null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength, null, null);
        Check(RealizedOf(MakeRealized(okRecord, realizedBytes, NoReport)) == NoDisk("report_mismatch", "report_missing"), "a succeeded step without report.json is report_mismatch (report_missing)");
        foreach (var (label, line) in new[] { ("not JSON", "{"), ("an array", "[1]"), ("without status", "{\"sha256\":\"" + realizedUpper + "\"}"), ("with a duplicate key", "{\"status\":\"succeeded\",\"status\":\"succeeded\"}") })
            Check(RealizedOf(MakeRealized(okRecord, realizedBytes, line)) == NoDisk("report_mismatch", "report_invalid"), "a succeeded step whose report.json is " + label + " is report_mismatch (report_invalid)");
        foreach (var (label, line) in new[] { ("status", Refusal(duplicate).ToJsonString()), ("sha256", Succeeded(new string('A', 64), realizedLength, 2).ToJsonString()), ("entries", Succeeded(realizedUpper, realizedLength, 3).ToJsonString()), ("bytes", Succeeded(realizedUpper, realizedLength + 1, 2).ToJsonString()), ("missing entries", new JsonObject { ["status"] = "succeeded", ["sha256"] = realizedUpper, ["bytes"] = realizedLength }.ToJsonString()) })
            Check(RealizedOf(MakeRealized(okRecord, realizedBytes, line)) == NoDisk("report_mismatch", "report_differs"), "a succeeded step whose report.json differs in " + label + " is report_mismatch (report_differs)");
        Check(RealizedOf(MakeRealized(okRecord, realizedBytes, Succeeded(realizedSha, realizedLength, 2).ToJsonString())) == matchExpected, "report.json's sha256 is compared ignoring case");
        Check(RealizedOf(MakeRealized(okRecord, realizedBytes, new string(' ', 1024 * 1024 + 1))) == NoDisk("unreadable", "input_too_large"), "a report.json over 1 MiB is not read (unreadable, input_too_large)");
        var lowerRecord = WithRealized(Succeeded(realizedSha, realizedLength, 2), passed: true);
        Check(RealizedOf(MakeRealized(lowerRecord, realizedBytes)) == matchExpected, "a lower-case reply sha256 matches too; reply_sha256 is printed lower-case");
        var noEntries = new JsonObject { ["status"] = "succeeded", ["sha256"] = realizedUpper, ["bytes"] = realizedLength };
        Check(RealizedOf(MakeRealized(WithRealized(noEntries, passed: true), realizedBytes)) == Realized("invalid", "entries", null, null, null, null, "succeeded", null, null, realizedUpper, realizedLength, realizedSha, realizedLength),
            "a succeeded reply without entries trusts nothing (invalid, entries)");
        var noBytes = new JsonObject { ["status"] = "succeeded", ["sha256"] = realizedUpper, ["entries"] = 2 };
        Check(RealizedOf(MakeRealized(WithRealized(noBytes, passed: true), realizedBytes)) == Realized("mismatch", "bytes", null, null, null, null, "succeeded", null, 2, realizedUpper, null, realizedSha, realizedLength),
            "a succeeded reply without bytes trusts nothing (mismatch, bytes)");
        string Refused1(string state, string? detail) => Realized(state, detail, null, null, null, null, "failed", duplicate, null, null, null, null, null, passed: false);
        Check(RealizedOf(MakeRealized(refusedRecord, null, new string(' ', 1024 * 1024 + 1))) == Refused1("unreadable", "input_too_large"), "a refusal whose report.json is over 1 MiB is unreadable (input_too_large)");
        var reportFolder = MakeRealized(refusedRecord, null);
        Directory.CreateDirectory(Path.Combine(reportFolder.Run, "catalogue-requests", realizedFolder, "report.json"));
        Check(RealizedOf(reportFolder) == Refused1("unreadable", "input_not_regular_file"), "a refusal with a folder in place of report.json is unreadable (input_not_regular_file)");
        var linkedMap = MakeRealized(okRecord, null);
        var mapTarget = Path.Combine(scratch, "realized-map-target.json");
        File.WriteAllBytes(mapTarget, realizedBytes);
        if (Link(Path.Combine(linkedMap.Run, "catalogue-requests", realizedFolder, "realized.json"), mapTarget, false, "a linked realized.json is not read"))
            Check(RealizedOf(linkedMap) == NoDisk("unreadable", "input_link_rejected"), "a linked realized.json is not read (unreadable, input_link_rejected)");
        var linkedFolder = MakeRealized(okRecord, null, folder: false);
        var folderTarget = Path.Combine(scratch, "realized-folder-target");
        Directory.CreateDirectory(folderTarget);
        File.WriteAllBytes(Path.Combine(folderTarget, "realized.json"), realizedBytes);
        Directory.CreateDirectory(Path.Combine(linkedFolder.Run, "catalogue-requests"));
        if (Link(Path.Combine(linkedFolder.Run, "catalogue-requests", realizedFolder), folderTarget, true, "a linked doc-realized folder is not read"))
            Check(RealizedOf(linkedFolder) == Realized("link_refused", "folder", null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength, null, null), "a linked doc-realized folder is not read (link_refused)");
        var linkedRequests = MakeRealized(okRecord, null, folder: false);
        var requestsTarget = Path.Combine(scratch, "realized-requests-target");
        Directory.CreateDirectory(Path.Combine(requestsTarget, realizedFolder));
        File.WriteAllBytes(Path.Combine(requestsTarget, realizedFolder, "realized.json"), realizedBytes);
        Directory.Delete(Path.Combine(linkedRequests.Run, "catalogue-requests"), true);
        if (Link(Path.Combine(linkedRequests.Run, "catalogue-requests"), requestsTarget, true, "a linked catalogue-requests folder is not read"))
            Check(RealizedOf(linkedRequests) == Realized("link_refused", "catalogue-requests", null, null, null, null, "succeeded", null, 2, realizedUpper, realizedLength, null, null), "a linked catalogue-requests folder is not read (link_refused)");

        // 1k. Links are never followed: run folder, copy, record, summary, session folder.
        var linkedRun = Path.Combine(runs, Guid.NewGuid().ToString());
        if (Link(linkedRun, pass.Run, true, "refuses a linked run folder")) Refused("a linked run folder", "catalogue_report_link_refused", "run", pass, linkedRun);
        var linkedCopy = Make(passTemplate);
        var copyTarget = Path.Combine(scratch, "copy-target.rvt");
        File.WriteAllBytes(copyTarget, CopyBytes);
        if (Link(Path.Combine(linkedCopy.Run, CopyName), copyTarget, false, "refuses a linked copy")) Refused("a linked copy", "catalogue_report_link_refused", "copy", linkedCopy);
        var linkedRecord = Make(passTemplate, CopyBytes);
        var recordPath = Path.Combine(linkedRecord.Root, ".bim", "native-sessions", Stamp, "catalogue-run1.json");
        var recordTarget = Path.Combine(scratch, "record-target.json");
        File.Move(recordPath, recordTarget);
        if (Link(recordPath, recordTarget, false, "refuses a linked record")) Refused("a linked record", "input_link_rejected", "record", linkedRecord);
        var linkedSummary = Make(passTemplate, CopyBytes);
        var summaryPath = Path.Combine(linkedSummary.Root, ".bim", "native-sessions", Stamp, "summary.json");
        var summaryTarget = Path.Combine(scratch, "summary-target.json");
        File.Move(summaryPath, summaryTarget);
        if (Link(summaryPath, summaryTarget, false, "skips a linked summary")) Refused("a run named only by a linked summary", "catalogue_report_record_not_found", null, linkedSummary);
        var hidden = Make(null, CopyBytes);
        var outsideSession = Path.Combine(scratch, "outside-session");
        Directory.CreateDirectory(outsideSession);
        File.WriteAllText(Path.Combine(outsideSession, "summary.json"), Summary("catalogue", WindowsRun(hidden.Name)));
        File.WriteAllText(Path.Combine(outsideSession, "catalogue-run1.json"), passTemplate.Replace("__RUN__", hidden.Name, StringComparison.Ordinal));
        if (Link(Path.Combine(hidden.Root, ".bim", "native-sessions", Stamp), outsideSession, true, "skips a linked session folder"))
            Refused("a run named only through a linked session folder", "catalogue_report_record_not_found", null, hidden);
    }

    // ---- 2. records written by the real owner code ----

    static void EndToEnd(string root, string scratch, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        var e2e = Path.Combine(scratch, "e2e");
        Directory.CreateDirectory(e2e);
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "tests", "cli", "catalogue-report-record.ps1"), "-Root", e2e, "-Dotnet", dotnet, "-CliDll", cliDll }) start.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception e)
        {
            // Every CI runner has pwsh: there the end-to-end check must run.
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") Check(false, "end to end: pwsh available on CI", e.Message);
            else skip("end to end with records from the real owner code", "pwsh not found: " + e.Message);
            return;
        }
        if (process is null) { Check(false, "end to end: pwsh started"); return; }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(600000)) { process.Kill(true); Check(false, "end to end: the record generator finished in 10 minutes"); return; }
            process.WaitForExit();
            Check(process.ExitCode == 0, "end to end: the real owner code wrote the records (tests/cli/catalogue-report-record.ps1)", "exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture) + " " + Short(stderr.Result.ReplaceLineEndings(" ")) + " " + stdout.Result);
            if (process.ExitCode != 0) return;
        }
        (JsonElement? Report, string Run, JsonElement? Record, (int Exit, string Out) Result) Read(string scenario)
        {
            var caseRoot = Path.Combine(e2e, scenario);
            var run = Directory.EnumerateDirectories(Path.Combine(caseRoot, ".bim", "native-runs")).Single();
            var result = CatalogueReportCommand.Execute(["catalogue-report", "--run", run], caseRoot);
            var record = Json(File.ReadAllText(Path.Combine(caseRoot, ".bim", "native-sessions", Stamp, "catalogue-run1.json")));
            return (Json(result.Out), run, record, result);
        }
        static string GroupKeys(JsonElement? report) => string.Join(",", Items(report, "groups").Select(g => Str(g, "key")));
        // The verb's group counts equal the ones the real writer recorded (key, total, passed, failed, blocked, not_sent).
        static bool SameGroups(JsonElement? report, JsonElement? record) =>
            Items(report, "groups").Length > 0 && Items(report, "groups").Select(g => g.GetRawText()).SequenceEqual(Items(record, "groups").Select(g => "{\"key\":" + Raw(g, "key") + string.Concat(new[] { "total", "passed", "failed", "blocked", "not_sent" }.Select(n => ",\"" + n + "\":" + Raw(g, n))) + "}"));
        static JsonElement? Member(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value : null;

        var clean = Read("clean");
        var steps = Items(clean.Record, "steps").Length;
        Check(clean.Result.Exit == 0 && Raw(clean.Report, "all_passed") == "true" && Raw(clean.Report, "overall") == Counts(null, steps, steps, 0, 0, 0) && steps > 90,
            "end to end (clean): every step of the real one-run sequence passed", D(clean.Result));
        Check(GroupKeys(clean.Report) == "v1,v2,vistas,snapshot,consultas,estructura,documentacion,diario,formas,extrusion,transformar,copia,modificar" && SameGroups(clean.Report, clean.Record),
            "end to end (clean): the groups and their counts equal the real writer's", GroupKeys(clean.Report));
        var cleanCopy = Member(clean.Report, "copy");
        var cleanFile = Str(cleanCopy, "file");
        Check(Str(cleanCopy, "state") == "match" && Raw(cleanCopy, "save_passed") == "true" && Raw(cleanCopy, "recorded_check_ok") == "true" && cleanFile is not null
            && Str(cleanCopy, "disk_sha256") == Sha(File.ReadAllBytes(Path.Combine(clean.Run, cleanFile))), "end to end (clean): the copy the fake saved matches its recorded sha256", Raw(clean.Report, "copy"));

        var mixed = Read("mixed");
        Check(mixed.Result.Exit == 0 && Raw(mixed.Report, "all_passed") == "false" && SameGroups(mixed.Report, mixed.Record), "end to end (mixed): failures and blocked steps are counted as the real writer counted them", D(mixed.Result));
        Check(string.Join(",", Items(mixed.Report, "blocked").Select(b => Str(b, "key"))) == "s-types-column,s-column", "end to end (mixed): the missing column type blocks two steps", Raw(mixed.Report, "blocked"));
        var mixedFailing = Items(mixed.Report, "failing");
        Check(mixedFailing.Any(f => Str(f, "group") == "v2" && Str(f, "observed_code") == "transaction_failed"), "end to end (mixed): the failing v2 command is listed with its observed code", Short(Raw(mixed.Report, "failing")));
        Check(mixedFailing.Any(f => Str(f, "key") == "c-save" && Items(f, "mismatches").Any(m => Str(m, "check") == "sha256 leído = sha256 del archivo en disco" && Raw(m, "requested") == "\"" + new string('0', 64) + "\"")),
            "end to end (mixed): the wrong sha256 read back is a failing c-save check, requested vs observed", Short(Raw(mixed.Report, "failing")));
        var mixedCopy = Member(mixed.Report, "copy");
        Check(Str(mixedCopy, "state") == "mismatch" && Str(mixedCopy, "recorded_sha256") == new string('0', 64) && Raw(mixedCopy, "save_passed") == "false" && Raw(mixedCopy, "recorded_check_ok") == "false",
            "end to end (mixed): the copy on disk differs from the sha256 read back", Raw(mixed.Report, "copy"));

        var nocopy = Read("nocopy");
        Check(nocopy.Result.Exit == 0 && SameGroups(nocopy.Report, nocopy.Record) && Raw(nocopy.Report, "copy").StartsWith("{\"state\":\"not_saved\",\"file\":null", StringComparison.Ordinal),
            "end to end (nocopy): a failed document.save_copy leaves no copy: not_saved", Raw(nocopy.Report, "copy") + " " + D(nocopy.Result));

        foreach (var (scenario, result) in new[] { ("mixed", mixed), ("nocopy", nocopy) })
        {
            var stepOf = Items(result.Record, "steps").FirstOrDefault(s => Str(s, "key") == "doc-up-elements");
            var map = Path.Combine(result.Run, "catalogue-requests", Raw(stepOf, "index").PadLeft(2, '0') + "-doc-up-elements.doc-realized", "realized.json");
            var realized = Member(result.Report, "realized");
            Check(Str(realized, "state") == "match" && Raw(realized, "entries") == "2" && File.Exists(map) && Str(realized, "sha256") == Sha(File.ReadAllBytes(map)) && Str(realized, "reply_sha256") == Str(realized, "sha256"),
                "end to end (" + scenario + "): the doc-realized step is listed with its realized.json (match, 2 entries)", Raw(result.Report, "realized"));
        }

        var v1 = Read("v1only");
        Check(v1.Result.Exit == 0 && Raw(v1.Report, "scope") == "\"v1_only\"" && Raw(v1.Report, "overall") == Counts(null, 17, 17, 0, 0, 0) && Raw(v1.Report, "copy").StartsWith("{\"state\":\"not_in_run\"", StringComparison.Ordinal),
            "end to end (v1only): the 17 v1 steps, no copy step: not_in_run", D(v1.Result));
        Check(Raw(v1.Report, "realized") == "<missing realized>" && v1.Result.Out.EndsWith("," + "\"copy\":" + Raw(v1.Report, "copy") + "}", StringComparison.Ordinal), "end to end (v1only): no doc-realized step, no realized member", D(v1.Result));

        // P4-18: the doc-up-elements step ran the real doc-realized verb (scripts/owner-catalogue-steps.ps1
        // Invoke-CatalogueDocUpdate -> Invoke-CatalogueDocRealized) into the run's catalogue-requests folder.
        static JsonElement? StepOf(JsonElement? record, string key) => Items(record, "steps").Where(s => Str(s, "key") == key).Select(s => (JsonElement?)s).FirstOrDefault();
        string FolderOf(JsonElement? step) => Raw(step, "index").PadLeft(2, '0') + "-doc-up-elements.doc-realized";
        var cleanElements = StepOf(clean.Record, "doc-up-elements");
        var cleanFolder = Path.Combine(clean.Run, "catalogue-requests", FolderOf(cleanElements));
        var cleanRealizedPath = Path.Combine(cleanFolder, "realized.json");
        var cleanRealized = Member(clean.Report, "realized");
        var cleanMap = File.Exists(cleanRealizedPath) ? File.ReadAllBytes(cleanRealizedPath) : Array.Empty<byte>();
        Check(cleanElements is not null && Str(cleanElements, "command") == "doc-realized" && new[] { "record.json", "fixture.json", "assignments.json", "report.json", "stderr.txt" }.All(f => File.Exists(Path.Combine(cleanFolder, f))) && cleanMap.Length > 0,
            "end to end (clean): doc-up-elements ran the real doc-realized CLI into the run folder", cleanFolder);
        Check(Str(cleanRealized, "state") == "match" && Raw(cleanRealized, "step") == Raw(cleanElements, "index") && Str(cleanRealized, "folder") == "catalogue-requests/" + FolderOf(cleanElements)
            && Raw(cleanRealized, "entries") == "2" && Str(cleanRealized, "sha256") == Sha(cleanMap) && Raw(cleanRealized, "bytes") == cleanMap.Length.ToString(CultureInfo.InvariantCulture)
            && string.Equals(Str(cleanRealized, "reply_sha256"), Sha(cleanMap), StringComparison.OrdinalIgnoreCase) && Raw(cleanRealized, "passed") == "true",
            "end to end (clean): catalogue-report lists doc-up-elements with the entry count and sha256 of its realized.json", Raw(clean.Report, "realized"));
        if (cleanMap.Length > 0)
        {
            // The run folder changed after the run: its realized.json no longer hashes to the step reply.
            var changedMap = (byte[])cleanMap.Clone();
            changedMap[^2] ^= 1;
            File.WriteAllBytes(cleanRealizedPath, changedMap);
            var after = Read("clean");
            var afterRealized = Member(after.Report, "realized");
            Check(Str(afterRealized, "state") == "mismatch" && Str(afterRealized, "detail") == "sha256" && Raw(afterRealized, "entries") == "null" && Raw(afterRealized, "sha256") == "null" && Str(afterRealized, "disk_sha256") == Sha(changedMap) && Raw(after.Report, "all_passed") == "true",
                "end to end (clean): a realized.json changed after the run is reported as a sha256 mismatch, not trusted", Raw(after.Report, "realized"));
            File.WriteAllBytes(cleanRealizedPath, cleanMap);
        }

        // P4-18: a run whose record realizes the door from the wall's step: the real doc-realized refuses it
        // (realized_unique_id_duplicate), the run stops, and the code comes from the folder's report.json.
        var refused = Read("realized");
        var refusedElements = StepOf(refused.Record, "doc-up-elements");
        var refusedFolder = Path.Combine(refused.Run, "catalogue-requests", FolderOf(refusedElements));
        var refusedRealized = Member(refused.Report, "realized");
        Check(refused.Result.Exit == 0 && (Str(refused.Report, "aborted") ?? "").Contains("realized_unique_id_duplicate", StringComparison.Ordinal) && File.Exists(Path.Combine(refusedFolder, "report.json")) && !File.Exists(Path.Combine(refusedFolder, "realized.json")),
            "end to end (realized): the real doc-realized refusal stops the run and is kept in the run folder", D(refused.Result));
        Check(Str(refusedRealized, "state") == "refused" && Str(refusedRealized, "code") == "realized_unique_id_duplicate" && Str(refusedRealized, "reply_code") == "realized_unique_id_duplicate" && Raw(refusedRealized, "entries") == "null" && Raw(refusedRealized, "passed") == "false",
            "end to end (realized): catalogue-report lists the realized_* refusal code read from report.json", Raw(refused.Report, "realized"));
    }

    // ---- 3. the real CLI process in this repository's .bim, and static checks ----

    static void Smoke(string root, string scratch, string dotnet, string cliDll, Action<bool, string, string> check)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, name, detail);
        (int Exit, string Out) Cli(string? cwd, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (cwd is not null) start.WorkingDirectory = cwd;
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); return (-1, "<timed out>"); }
            process.WaitForExit();
            var text = stdout.Result.TrimEnd('\r', '\n');
            return (process.ExitCode, text + (stderr.Result.Length > 0 ? " stderr " + stderr.Result.ReplaceLineEndings(" ") : ""));
        }
        var bim = Path.Combine(root, ".bim");
        var runs = Path.Combine(bim, "native-runs");
        var sessions = Path.Combine(bim, "native-sessions");
        var preexisting = new[] { bim, runs, sessions }.Where(Directory.Exists).ToList();
        // Only crtest-* folders are this test's: leftovers of an interrupted run are removed first, everything after.
        void Clean()
        {
            foreach (var parent in new[] { runs, sessions })
            {
                if (!Directory.Exists(parent)) continue;
                foreach (var directory in Directory.EnumerateDirectories(parent, "crtest-*"))
                {
                    try { Directory.Delete(directory, true); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine("catalogue-report cleanup could not remove " + directory + ": " + e.Message); }
                }
            }
            foreach (var directory in new[] { sessions, runs, bim })
                if (!preexisting.Contains(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        Clean();
        try
        {
            var name = "crtest-" + Guid.NewGuid().ToString("N");
            var session = "crtest-" + Guid.NewGuid().ToString("N");
            var run = Path.Combine(runs, name);
            Directory.CreateDirectory(run);
            File.WriteAllBytes(Path.Combine(run, CopyName), CopyBytes);
            Directory.CreateDirectory(Path.Combine(sessions, session));
            File.WriteAllText(Path.Combine(sessions, session, "summary.json"), Summary("catalogue", WindowsRun(name)));
            File.WriteAllText(Path.Combine(sessions, session, "catalogue-run1.json"), Fixture(root, "pass").Replace("__RUN__", name, StringComparison.Ordinal)
                .Replace("\"session_stamp\": \"" + Stamp + "\"", "\"session_stamp\": \"" + session + "\"", StringComparison.Ordinal));
            var listing = string.Join("\n", Directory.EnumerateFileSystemEntries(runs).Concat(Directory.EnumerateFileSystemEntries(sessions)).OrderBy(p => p, StringComparer.Ordinal));
            var first = Cli(null, "catalogue-report", "--run", run);
            var report = Json(first.Out);
            Check(first.Exit == 0 && Raw(report, "run") == "\".bim/native-runs/" + name + "\"" && Raw(report, "copy") == Copy("match", CopyName, 6, true, true, CopySha, 53, CopySha, 53),
                "real process: the CLI reports a run in this repository's .bim/native-runs", D(first));
            var relative = Cli(root, "catalogue-report", "--run", ".bim/native-runs/" + name);
            Check(relative.Exit == 0 && relative.Out == first.Out, "real process: a path relative to the working directory gives the same report", D(relative));
            var outside = Cli(null, "catalogue-report", "--run", scratch);
            Check(outside.Exit == 1 && outside.Out == Failure("catalogue_report_run_outside", null), "real process: a folder outside .bim/native-runs is refused", D(outside));
            Check(string.Join("\n", Directory.EnumerateFileSystemEntries(runs).Concat(Directory.EnumerateFileSystemEntries(sessions)).OrderBy(p => p, StringComparer.Ordinal)) == listing, "real process: nothing is added to .bim/native-runs or native-sessions");
            Check(Cli(null, "no-such-verb").Out.Contains("catalogue-report --run", StringComparison.Ordinal), "default usage lists catalogue-report");
        }
        finally { Clean(); }

        var source = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "CatalogueReportCommand.cs"));
        var writes = new[] { "File.Write", "File.Create", "File.Delete", "File.Move", "File.Copy", "File.Append", "File.Replace", "Directory.Create", "Directory.Delete", "Directory.Move", "FileMode.Create", "FileMode.Append", "FileMode.OpenOrCreate", "FileMode.Truncate", "FileAccess.Write", "FileAccess.ReadWrite", "WriteNew", "NamedPipe", "Process." };
        Check(writes.All(api => !source.Contains(api, StringComparison.Ordinal)), "the verb uses no write, pipe or process API", string.Join(",", writes.Where(api => source.Contains(api, StringComparison.Ordinal))));
        Check(source.Contains("BoundedInput.ReadBytes(", StringComparison.Ordinal) && source.Contains("RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory)", StringComparison.Ordinal), "the verb reads through BoundedInput and resolves the repository like the other verbs");
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"catalogue-report\": CatalogueReportCommand.Run(args); break;", StringComparison.Ordinal), "Program.cs registers catalogue-report");
    }
}
