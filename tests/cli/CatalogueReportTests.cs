using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// P1-12 `catalogue-report --run .bim/native-runs/<run>`: the owner -Catalogue run summary, read-only.
// The fixture records (tests/cli/catalogue-report/<case>/catalogue-run1.json: pass, fail, blocked) mirror what
// scripts/owner-native-session.ps1 -Catalogue writes: the record sits in .bim/native-sessions/<utc>/ next to summary.json
// (mode "catalogue", runs[].index and run_directory naming the run folder); the run folder .bim/native-runs/<guid> holds
// the fixture and the copy saved by the copia group (written here: *.rvt is never committed). "__RUN__" in a record is
// the run folder's name. Each case gets a fresh run/session pair inside this repository's .bim (the verb refuses
// anything else); everything this file creates there is removed at the end, and nothing else in .bim is touched.
internal static class CatalogueReportTests
{
    const string CopyName = "BIM-Agentic-Test-c04-20261105T120000Z-r1-copia.rvt";
    const string CopySha = "3202cfd5c94a2241834e957759aa5fb26a21bed8e485646de3289cfca9aae862";
    static readonly byte[] CopyBytes = Encoding.ASCII.GetBytes("BIM-Agentic-Test copy fixture: not a real Revit file\n");
    const int MaxRecordBytes = 32 * 1024 * 1024;

    public static void Run(string root, string work, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        var bim = Path.Combine(root, ".bim");
        var runs = Path.Combine(bim, "native-runs");
        var sessions = Path.Combine(bim, "native-sessions");
        var preexisting = new[] { bim, runs, sessions }.Where(Directory.Exists).ToList();
        var created = new List<string>();
        var scratch = Path.Combine(work, "catalogue-report");
        Directory.CreateDirectory(scratch);
        try
        {
            Directory.CreateDirectory(runs);
            Directory.CreateDirectory(sessions);
            Body(root, scratch, runs, sessions, created, dotnet, cliDll, check, skip);
        }
        finally
        {
            for (var i = created.Count - 1; i >= 0; i--) Remove(created[i]);
            foreach (var directory in new[] { sessions, runs, bim })
                if (!preexisting.Contains(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    static void Remove(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            // A link is removed itself, never its target (a directory link on Windows needs RemoveDirectory, elsewhere unlink).
            if (info.LinkTarget is not null) { if (info is DirectoryInfo && OperatingSystem.IsWindows()) Directory.Delete(path); else File.Delete(path); }
            else if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine("catalogue-report cleanup could not remove " + path + ": " + e.Message); }
    }

    static void Body(string root, string scratch, string runs, string sessions, List<string> created, string dotnet, string cliDll, Action<bool, string, string> check, Action<string, string> skip)
    {
        (int Exit, string Out, string Err) Cli(string? cwd, params string[] arguments)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false) };
            if (cwd is not null) start.WorkingDirectory = cwd;
            start.ArgumentList.Add(cliDll);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("CLI process did not start: " + dotnet);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(true); return (-1, "<timed out>", ""); }
            process.WaitForExit();
            var text = stdout.Result;
            if (text.EndsWith('\n')) text = text[..^1];
            if (text.EndsWith('\r')) text = text[..^1];
            return (process.ExitCode, text, stderr.Result.ReplaceLineEndings(" ").Trim());
        }
        (int Exit, string Out, string Err) Report(string run) => Cli(null, "catalogue-report", "--run", run);
        static string D((int Exit, string Out, string Err) r) => "exit " + r.Exit.ToString(CultureInfo.InvariantCulture) + " stdout " + (r.Out.Length > 700 ? r.Out[..700] + "..." : r.Out) + (r.Err.Length > 0 ? " stderr " + r.Err : "");
        void Check(bool condition, string name, string detail = "") => check(condition, "catalogue-report: " + name, detail);
        void Refused(string name, string code, string? detail, params string[] arguments)
        {
            var r = Cli(null, arguments);
            var expected = detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
            Check(r.Exit == 1 && r.Out == expected, "refuses " + name + " (" + code + (detail is null ? "" : "/" + detail) + ")", D(r));
        }
        void RefusedRun(string name, string code, string? detail, string run) => Refused(name, code, detail, "catalogue-report", "--run", run);
        static JsonElement? Json(string text)
        {
            try { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
            catch (JsonException) { return null; }
        }
        static string Raw(JsonElement? element, string name) => element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value.GetRawText() : "<missing " + name + ">";
        static string Counts(string? key, int total, int passed, int failed, int blocked, int notSent) =>
            (key is null ? "{" : "{\"key\":\"" + key + "\",") + string.Format(CultureInfo.InvariantCulture, "\"total\":{0},\"passed\":{1},\"failed\":{2},\"blocked\":{3},\"not_sent\":{4}}}", total, passed, failed, blocked, notSent);
        static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string Fixture(string name) => File.ReadAllText(Path.Combine(root, "tests", "cli", "catalogue-report", name, "catalogue-run1.json"));
        string Directory_(string parent, string name) { var path = Path.Combine(parent, name); Directory.CreateDirectory(path); created.Add(path); return path; }
        static string Summary(string mode, params string[] runDirectories)
        {
            var entries = new JsonArray();
            for (var i = 0; i < runDirectories.Length; i++)
                entries.Add(new JsonObject
                {
                    ["index"] = i + 1, ["run_directory"] = runDirectories[i], ["child_pid"] = 4242, ["bootstrap"] = "succeeded", ["outcome"] = "fixture",
                    ["catalogue"] = new JsonObject { ["file"] = "C:\\Repo\\.bim\\native-sessions\\20261105T120000Z\\catalogue-run" + (i + 1).ToString(CultureInfo.InvariantCulture) + ".json" },
                });
            return new JsonObject
            {
                ["schema_version"] = 1, ["mode"] = mode, ["catalogue_scope"] = "one_run_all_groups", ["started_utc"] = "2026-11-05T12:00:00.0000000Z",
                ["session"] = "C:\\Repo\\.bim\\native-sessions\\20261105T120000Z", ["head"] = "308c756", ["protected_revit_pids"] = new JsonArray(), ["source_check"] = "passed",
                ["builds"] = new JsonObject(), ["runs"] = entries, ["finished_utc"] = null, ["outcome"] = "fixture",
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        string WindowsRun(string name) => "C:\\Repo\\.bim\\native-runs\\" + name;
        // A run folder (fixture, catalogue-requests/, the copy when given) and a session whose summary names it.
        (string Run, string Name, string Session) Make(string? record, byte[]? copy = null, Func<string, string>? summaryRun = null, Func<string, string>? finish = null, string mode = "catalogue")
        {
            var name = Guid.NewGuid().ToString();
            var run = Directory_(runs, name);
            File.WriteAllText(Path.Combine(run, "BIM-Agentic-Test.rvt"), "fixture");
            Directory.CreateDirectory(Path.Combine(run, "catalogue-requests"));
            File.WriteAllText(Path.Combine(run, "catalogue-requests", "01-q-levels.json"), "{}");
            if (copy is not null) File.WriteAllBytes(Path.Combine(run, CopyName), copy);
            if (record is null) return (run, name, "");
            var session = Directory_(sessions, "20261105T120000Z-crtest-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(Path.Combine(session, "summary.json"), Summary(mode, summaryRun is null ? WindowsRun(name) : summaryRun(name)));
            var text = record.Replace("__RUN__", name, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(session, "catalogue-run1.json"), finish is null ? text : finish(text));
            return (run, name, Path.GetFileName(session));
        }
        string Mutate(string template, Action<JsonObject> change)
        {
            var node = JsonNode.Parse(template)!.AsObject();
            change(node);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        static JsonObject StepAt(JsonObject record, int index) => record["steps"]![index]!.AsObject();
        static string Tree(params string[] directories) => string.Join("\n", directories.SelectMany(d => Directory.EnumerateFileSystemEntries(d, "*", SearchOption.AllDirectories).Prepend(d))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? p + "|" + new FileInfo(p).Length.ToString(CultureInfo.InvariantCulture) + "|" + new FileInfo(p).LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Sha(File.ReadAllBytes(p)) : p + "|dir"));

        var passTemplate = Fixture("pass");
        Check(Sha(CopyBytes) == CopySha && passTemplate.Contains(CopySha, StringComparison.Ordinal), "fixture copy bytes hash to the sha256 the pass record holds");
        // A session that is not JSON and one of another mode sit next to the cases: they are skipped, never fatal.
        var junk = Directory_(sessions, "20261105T110000Z-crtest-junk-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(junk, "summary.json"), "{");

        // 1. A run where everything passed: counts per group, nothing failing or blocked, the copy hash matches.
        var pass = Make(passTemplate, CopyBytes);
        var snapshot = Tree(pass.Run, Path.Combine(sessions, pass.Session));
        var listing = string.Join("\n", Directory.EnumerateFileSystemEntries(runs).Concat(Directory.EnumerateFileSystemEntries(sessions)).OrderBy(p => p, StringComparer.Ordinal));
        var first = Report(pass.Run);
        var second = Report(pass.Run);
        var report = Json(first.Out);
        Check(first.Exit == 0 && Raw(report, "status") == "\"succeeded\"", "a passing run is reported (exit 0)", D(first));
        Check(first.Exit == 0 && first.Out == second.Out, "the report is deterministic (two runs, byte-identical stdout)", D(second));
        Check(first.Out.All(c => c < 128), "the report is ASCII (non-ASCII escaped), whatever the console encoding", D(first));
        Check(Raw(report, "run") == "\".bim/native-runs/" + pass.Name + "\"" && Raw(report, "record") == "\".bim/native-sessions/" + pass.Session + "/catalogue-run1.json\"", "the report names the run folder and the session record it read", D(first));
        Check(Raw(report, "scope") == "\"one_run_all_groups\"" && Raw(report, "head") == "\"308c756\"" && Raw(report, "aborted") == "null" && Raw(report, "all_passed") == "true", "scope, head, aborted and all_passed of a passing run", D(first));
        Check(Raw(report, "overall") == Counts(null, 10, 10, 0, 0, 0), "overall counts of a passing run", Raw(report, "overall"));
        Check(Raw(report, "groups") == "[" + string.Join(",", Counts("v1", 2, 2, 0, 0, 0), Counts("estructura", 2, 2, 0, 0, 0), Counts("copia", 5, 5, 0, 0, 0), Counts("modificar", 1, 1, 0, 0, 0)) + "]", "per-group passed/failed/blocked counts in step order", Raw(report, "groups"));
        Check(Raw(report, "failing") == "[]" && Raw(report, "blocked") == "[]", "a passing run lists no failing or blocked step", D(first));
        Check(Raw(report, "copy") == "{\"state\":\"match\",\"file\":\"" + CopyName + "\",\"save_step\":6,\"save_passed\":true,\"recorded_check_ok\":true,\"recorded_sha256\":\"" + CopySha + "\",\"recorded_bytes\":53,\"disk_sha256\":\"" + CopySha + "\",\"disk_bytes\":53}",
            "the copy on disk matches the sha256 and size c-save recorded", Raw(report, "copy"));
        Check(Tree(pass.Run, Path.Combine(sessions, pass.Session)) == snapshot
            && string.Join("\n", Directory.EnumerateFileSystemEntries(runs).Concat(Directory.EnumerateFileSystemEntries(sessions)).OrderBy(p => p, StringComparer.Ordinal)) == listing,
            "read-only: the run folder, the session folder and .bim/native-runs and native-sessions are unchanged");
        var relative = Cli(root, "catalogue-report", "--run", ".bim/native-runs/" + pass.Name);
        Check(relative.Exit == 0 && relative.Out == first.Out, "a path relative to the working directory gives the same report", D(relative));
        var trailing = Report(pass.Run + Path.DirectorySeparatorChar);
        Check(trailing.Exit == 0 && trailing.Out == first.Out, "a trailing separator gives the same report", D(trailing));
        var forward = Make(passTemplate, CopyBytes, name => Path.Combine(runs, name).Replace('\\', '/'));
        Check(Report(forward.Run).Exit == 0, "a summary run_directory with '/' separators is matched too");

        // 2. The copy changed after the run (owner saved at Revit's prompt), or is gone.
        var tampered = (byte[])CopyBytes.Clone();
        tampered[0] ^= 1;
        var changed = Make(passTemplate, tampered);
        var changedReport = Json(Report(changed.Run).Out);
        Check(Raw(changedReport, "copy") == "{\"state\":\"mismatch\",\"file\":\"" + CopyName + "\",\"save_step\":6,\"save_passed\":true,\"recorded_check_ok\":true,\"recorded_sha256\":\"" + CopySha + "\",\"recorded_bytes\":53,\"disk_sha256\":\"" + Sha(tampered) + "\",\"disk_bytes\":53}",
            "a same-size copy with other bytes is a sha256 mismatch", Raw(changedReport, "copy"));
        Check(Raw(changedReport, "all_passed") == "true" && Raw(changedReport, "overall") == Counts(null, 10, 10, 0, 0, 0), "a copy mismatch does not rewrite the run's own verdict", Raw(changedReport, "overall"));
        var longer = Make(passTemplate, [.. CopyBytes, (byte)'x']);
        Check(Raw(Json(Report(longer.Run).Out), "copy").StartsWith("{\"state\":\"mismatch\"", StringComparison.Ordinal), "a longer copy is a mismatch");
        var gone = Make(passTemplate);
        Check(Raw(Json(Report(gone.Run).Out), "copy") == "{\"state\":\"missing_file\",\"file\":\"" + CopyName + "\",\"save_step\":6,\"save_passed\":true,\"recorded_check_ok\":true,\"recorded_sha256\":\"" + CopySha + "\",\"recorded_bytes\":53,\"disk_sha256\":null,\"disk_bytes\":null}",
            "a missing copy is reported as missing_file", Raw(Json(Report(gone.Run).Out), "copy"));

        // 3. A run with failures: requested vs observed rows, a refused response, the failed save, a step not sent.
        var fail = Make(Fixture("fail"), CopyBytes);
        var failRun = Report(fail.Run);
        var failReport = Json(failRun.Out);
        Check(failRun.Exit == 0 && Raw(failReport, "all_passed") == "false" && Raw(failReport, "overall") == Counts(null, 10, 6, 4, 0, 1), "overall counts of a failing run", D(failRun));
        Check(Raw(failReport, "groups") == "[" + string.Join(",", Counts("v1", 2, 1, 1, 0, 0), Counts("estructura", 2, 1, 1, 0, 0), Counts("copia", 5, 3, 2, 0, 1), Counts("modificar", 1, 1, 0, 0, 0)) + "]", "per-group counts of a failing run", Raw(failReport, "groups"));
        var failing = failReport is { } fr && fr.TryGetProperty("failing", out var failList) && failList.ValueKind == JsonValueKind.Array ? failList.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
        Check(failing.Length == 4 && string.Join(",", failing.Select(f => Raw(f, "index") + ":" + Raw(f, "key"))) == "2:\"w-south\",4:\"s-column\",6:\"c-save\",7:\"c-save-again\"", "the failing steps, in order", Raw(failReport, "failing"));
        if (failing.Length == 4)
        {
            Check(Raw(failing[0], "mismatches") == "[{\"check\":\"altura (mm)\",\"requested\":3000,\"observed\":2950}]" && failing[0].GetProperty("reason").GetString() == "altura (mm): se pidió 3000, se observó 2950"
                && Raw(failing[0], "sent") == "true" && Raw(failing[0], "observed_status") == "\"succeeded\"" && Raw(failing[0], "group") == "\"v1\"" && Raw(failing[0], "command") == "\"wall.create\"" && Raw(failing[0], "mismatches_omitted") == "0",
                "a failing check is printed with requested vs observed (only the rows that did not hold)", failing[0].GetRawText());
            Check(Raw(failing[1], "mismatches") == "[]" && Raw(failing[1], "observed_status") == "\"failed\"" && Raw(failing[1], "observed_code") == "\"type_not_found\"" && Raw(failing[1], "expected") == "{\"Status\":\"succeeded\",\"Created\":1}",
                "a refused response is printed with the expected status and the observed status and code", failing[1].GetRawText());
            var shaRows = failing[2].GetProperty("mismatches");
            Check(shaRows.GetArrayLength() == 1 && shaRows[0].GetProperty("check").GetString() == "sha256 leído = sha256 del archivo en disco"
                && Raw(shaRows[0], "requested") == "\"" + CopySha + "\"" && Raw(shaRows[0], "observed") == "\"" + Sha(Encoding.ASCII.GetBytes("other bytes\n")) + "\"",
                "the failed run-time sha256 check of c-save is printed requested vs observed", failing[2].GetRawText());
            Check(Raw(failing[3], "sent") == "false" && Raw(failing[3], "observed_status") == "null" && Raw(failing[3], "mismatches") == "[]" && failing[3].GetProperty("reason").GetString() == "no enviado: falta copy_path de un paso anterior",
                "a step not sent (and not blocked) is failing with its reason", failing[3].GetRawText());
        }
        Check(Raw(failReport, "blocked") == "[]", "a failing run without blocked steps lists none", Raw(failReport, "blocked"));
        Check(Raw(failReport, "copy") == "{\"state\":\"match\",\"file\":\"" + CopyName + "\",\"save_step\":6,\"save_passed\":false,\"recorded_check_ok\":false,\"recorded_sha256\":\"" + CopySha + "\",\"recorded_bytes\":53,\"disk_sha256\":\"" + CopySha + "\",\"disk_bytes\":53}",
            "without copy_path the copy is taken from c-save's readback; the run-time check and the check now are both reported", Raw(failReport, "copy"));

        // 4. A run with blocked steps (missing fixture preconditions), one never sent: blocked, not failed.
        var blocked = Make(Fixture("blocked"), CopyBytes);
        var blockedReport = Json(Report(blocked.Run).Out);
        Check(Raw(blockedReport, "overall") == Counts(null, 10, 8, 0, 2, 1) && Raw(blockedReport, "all_passed") == "false" && Raw(blockedReport, "failing") == "[]", "blocked steps are counted apart from failures", Raw(blockedReport, "overall"));
        Check(Raw(blockedReport, "groups").Contains(Counts("estructura", 2, 0, 0, 2, 1), StringComparison.Ordinal), "the group of the blocked steps", Raw(blockedReport, "groups"));
        Check(blockedReport is { } br && br.TryGetProperty("blocked", out var blockedList) && blockedList.GetArrayLength() == 2
            && Raw(blockedList[0], "index") == "3" && Raw(blockedList[0], "key") == "\"s-grid\"" && Raw(blockedList[0], "sent") == "false" && blockedList[0].GetProperty("reason").GetString() == "no enviado, bloqueado: falta en el modelo de prueba: segundo nivel"
            && Raw(blockedList[1], "index") == "4" && Raw(blockedList[1], "sent") == "true",
            "the blocked steps are listed with their reason", Raw(blockedReport, "blocked"));

        // 5. Bounded output: a long reason is cut, a large requested value is replaced by its size.
        var long_ = Make(Mutate(Fixture("fail"), r => { StepAt(r, 1)["reason"] = new string('x', 5000); StepAt(r, 1)["comparisons"]![0]!["requested"] = new string('a', 3000); }), CopyBytes);
        var longReport = Json(Report(long_.Run).Out);
        var longFirst = longReport is { } lr && lr.TryGetProperty("failing", out var lf) && lf.GetArrayLength() > 0 ? lf[0] : default;
        Check(longFirst.ValueKind == JsonValueKind.Object && longFirst.GetProperty("reason").GetString() == new string('x', 2000) + " ...(+3000 chars)"
            && Raw(longFirst, "mismatches") == "[{\"check\":\"altura (mm)\",\"requested\":{\"truncated_chars\":3002},\"observed\":2950}]",
            "texts over 2000 chars are cut and JSON values over 1024 chars are replaced by their size", longFirst.ValueKind == JsonValueKind.Object ? longFirst.GetRawText() : D(Report(long_.Run)));

        // 6. Malformed or inconsistent records: typed refusals, nothing printed but the envelope.
        void Bad(string name, string code, string? detail, string text) => RefusedRun("a record " + name, code, detail, Make(text, CopyBytes).Run);
        void BadMutation(string name, string code, string? detail, Action<JsonObject> change) => Bad(name, code, detail, Mutate(passTemplate, change));
        Bad("that is not JSON", "catalogue_report_record_invalid", "json", "{");
        Bad("that is empty", "catalogue_report_record_invalid", "json", "");
        Bad("that is an array", "catalogue_report_record_invalid", "root", "[]");
        Bad("with a duplicate key", "catalogue_report_record_invalid", "duplicate_key", passTemplate.Replace("\"head\": \"308c756\",", "\"head\": \"308c756\", \"head\": \"308c756\",", StringComparison.Ordinal));
        BadMutation("of another schema version", "catalogue_report_record_invalid", "schema_version", r => r["schema_version"] = 2);
        BadMutation("of another run index", "catalogue_report_record_mismatch", "run", r => r["run"] = 2);
        BadMutation("about another run folder", "catalogue_report_record_mismatch", "document_path", r => r["document_path"] = "C:\\Repo\\.bim\\native-runs\\other\\BIM-Agentic-Test.rvt");
        BadMutation("without steps", "catalogue_report_record_invalid", "steps", r => r.Remove("steps"));
        BadMutation("with no step", "catalogue_report_record_invalid", "steps", r => r["steps"] = new JsonArray());
        BadMutation("with a step without passed", "catalogue_report_record_invalid", "steps[1].passed", r => StepAt(r, 1).Remove("passed"));
        BadMutation("with a string passed", "catalogue_report_record_invalid", "steps[1].passed", r => StepAt(r, 1)["passed"] = "yes");
        BadMutation("with steps out of order", "catalogue_report_record_invalid", "steps[2].index", r => StepAt(r, 2)["index"] = 7);
        BadMutation("with a step both passed and blocked", "catalogue_report_record_invalid", "steps[0].blocked", r => StepAt(r, 0)["blocked"] = true);
        BadMutation("with a passed step never sent", "catalogue_report_record_invalid", "steps[0].request", r => StepAt(r, 0)["request"] = null);
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
        BadMutation("whose copy is in another folder", "catalogue_report_copy_outside_run", null, r => r["copy_path"] = "C:\\Repo\\.bim\\BIM-Agentic-Test-c04-20261105T120000Z-r1-copia.rvt");
        BadMutation("whose copy is not a catalogue copy", "catalogue_report_copy_outside_run", null, r => r["copy_path"] = WindowsRun("__RUN__") + "\\summary.json");

        // 7. Bounds: the record's size (exactly the cap is read), steps and comparisons.
        string Pad(string text, int bytes) => text + new string(' ', bytes - Encoding.UTF8.GetByteCount(text));
        var atCap = Make(passTemplate, CopyBytes, finish: text => Pad(text, MaxRecordBytes));
        var atCapRun = Report(atCap.Run);
        Check(atCapRun.Exit == 0 && Raw(Json(atCapRun.Out), "overall") == Counts(null, 10, 10, 0, 0, 0), "a record of exactly 32 MiB is read", D(atCapRun));
        RefusedRun("a record of 32 MiB + 1 byte", "input_too_large", "record", Make(passTemplate, CopyBytes, finish: text => Pad(text, MaxRecordBytes + 1)).Run);
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

        // 8. Finding the record through the session summaries.
        RefusedRun("a run folder no session names", "catalogue_report_record_not_found", null, Make(null, CopyBytes).Run);
        RefusedRun("a run named only by a summary of another mode", "catalogue_report_record_not_found", null, Make(passTemplate, CopyBytes, mode: "room_ab").Run);
        var twice = Make(passTemplate, CopyBytes);
        var other = Directory_(sessions, "20261105T130000Z-crtest-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(other, "summary.json"), Summary("catalogue", WindowsRun(twice.Name)));
        File.WriteAllText(Path.Combine(other, "catalogue-run1.json"), passTemplate.Replace("__RUN__", twice.Name, StringComparison.Ordinal));
        var names = new[] { twice.Session, Path.GetFileName(other) }.OrderBy(n => n, StringComparer.Ordinal).Select(n => n + "/catalogue-run1.json");
        RefusedRun("a run two sessions name", "catalogue_report_record_ambiguous", string.Join(",", names), twice.Run);
        var noRecord = Make(passTemplate, CopyBytes);
        File.Delete(Path.Combine(sessions, noRecord.Session, "catalogue-run1.json"));
        RefusedRun("a run whose session has no record file", "catalogue_report_record_not_found", noRecord.Session + "/catalogue-run1.json", noRecord.Run);
        var second_ = Make(null, CopyBytes);
        var secondSession = Directory_(sessions, "20261105T140000Z-crtest-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(secondSession, "summary.json"), Summary("catalogue", WindowsRun("another-run"), WindowsRun(second_.Name)));
        File.WriteAllText(Path.Combine(secondSession, "catalogue-run2.json"), passTemplate.Replace("__RUN__", second_.Name, StringComparison.Ordinal));
        RefusedRun("a record whose run differs from the summary index", "catalogue_report_record_mismatch", "run", second_.Run);
        File.WriteAllText(Path.Combine(secondSession, "catalogue-run2.json"), Mutate(passTemplate, r => r["run"] = 2).Replace("__RUN__", second_.Name, StringComparison.Ordinal));
        Check(Raw(Json(Report(second_.Run).Out), "record") == "\".bim/native-sessions/" + Path.GetFileName(secondSession) + "/catalogue-run2.json\"", "the record of the second run of a session is catalogue-run2.json", D(Report(second_.Run)));

        // 9. The run folder: only exactly one level below <repository>/.bim/native-runs, after normalisation.
        foreach (var bad in new[] { new[] { "catalogue-report" }, new[] { "catalogue-report", "--run" }, new[] { "catalogue-report", "--run", "" }, new[] { "catalogue-report", "--run", "--x" },
            new[] { "catalogue-report", "--dir", pass.Run }, new[] { "catalogue-report", "--run", pass.Run, "extra" }, new[] { "catalogue-report", pass.Run, "--run" } })
            Refused("arguments " + string.Join(" ", bad.Skip(1).Select(a => a.StartsWith("--", StringComparison.Ordinal) || a.Length == 0 ? "'" + a + "'" : "<path>")), "catalogue_report_usage", null, bad);
        var outsideCopy = Path.Combine(scratch, pass.Name);
        Directory.CreateDirectory(outsideCopy);
        RefusedRun("a run folder outside the repository", "catalogue_report_run_outside", null, outsideCopy);
        RefusedRun("the repository root", "catalogue_report_run_outside", null, root);
        RefusedRun(".bim itself", "catalogue_report_run_outside", null, Path.Combine(root, ".bim"));
        RefusedRun(".bim/native-runs itself", "catalogue_report_run_outside", null, runs);
        RefusedRun("a folder inside a run folder", "catalogue_report_run_outside", null, Path.Combine(pass.Run, "catalogue-requests"));
        RefusedRun("a sibling of native-runs", "catalogue_report_run_outside", null, Path.Combine(root, ".bim", "native-runs-x", pass.Name));
        RefusedRun("a session folder", "catalogue_report_run_outside", null, Path.Combine(sessions, pass.Session));
        RefusedRun("a '..' that comes back into native-runs", "catalogue_report_run_traversal", null, pass.Run + "/../" + pass.Name);
        RefusedRun("a '..' through native-runs", "catalogue_report_run_traversal", null, Path.Combine(runs, "..", "native-runs", pass.Name));
        RefusedRun("a relative '..'", "catalogue_report_run_traversal", null, "../" + pass.Name);
        RefusedRun("a control character", "catalogue_report_run_invalid", null, pass.Run + "\t");
        RefusedRun("a run folder that does not exist", "catalogue_report_run_not_found", "run", Path.Combine(runs, Guid.NewGuid().ToString()));
        var file = Path.Combine(runs, Guid.NewGuid().ToString());
        File.WriteAllText(file, "not a folder");
        created.Add(file);
        RefusedRun("a file in native-runs", "catalogue_report_run_not_found", "run", file);

        // 10. Links are never followed: a linked run folder, a linked copy, a linked session folder.
        bool Link(string path, string target, bool directory, string name)
        {
            try
            {
                if (directory) Directory.CreateSymbolicLink(path, target); else File.CreateSymbolicLink(path, target);
                created.Add(path);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { skip("catalogue-report: " + name, "symbolic links unavailable: " + e.GetType().Name); return false; }
        }
        var linkedRun = Path.Combine(runs, Guid.NewGuid().ToString());
        if (Link(linkedRun, pass.Run, true, "refuses a linked run folder")) RefusedRun("a linked run folder", "catalogue_report_link_refused", "run", linkedRun);
        var linkedCopy = Make(passTemplate);
        var copyTarget = Path.Combine(scratch, "copy-target.rvt");
        File.WriteAllBytes(copyTarget, CopyBytes);
        if (Link(Path.Combine(linkedCopy.Run, CopyName), copyTarget, false, "refuses a linked copy")) RefusedRun("a linked copy", "catalogue_report_link_refused", "copy", linkedCopy.Run);
        var hidden = Make(null, CopyBytes);
        var outsideSession = Path.Combine(scratch, "outside-session");
        Directory.CreateDirectory(outsideSession);
        File.WriteAllText(Path.Combine(outsideSession, "summary.json"), Summary("catalogue", WindowsRun(hidden.Name)));
        File.WriteAllText(Path.Combine(outsideSession, "catalogue-run1.json"), passTemplate.Replace("__RUN__", hidden.Name, StringComparison.Ordinal));
        if (Link(Path.Combine(sessions, "20261105T150000Z-crtest-link-" + Guid.NewGuid().ToString("N")), outsideSession, true, "skips a linked session folder"))
            RefusedRun("a run named only through a linked session folder", "catalogue_report_record_not_found", null, hidden.Run);

        // 11. Static: a read-only verb, registered, with the repository root every verb uses.
        var sourcePath = Path.Combine(root, "interfaces", "cli", "CatalogueReportCommand.cs");
        Check(File.Exists(sourcePath), "interfaces/cli/CatalogueReportCommand.cs exists");
        if (File.Exists(sourcePath))
        {
            var source = File.ReadAllText(sourcePath);
            var writes = new[] { "File.Write", "File.Create", "File.Delete", "File.Move", "File.Copy", "File.Append", "File.Replace", "Directory.Create", "Directory.Delete", "Directory.Move", "FileMode.Create", "FileMode.Append", "FileMode.OpenOrCreate", "FileMode.Truncate", "FileAccess.Write", "FileAccess.ReadWrite", "WriteNew", "NamedPipe", "Process." };
            Check(writes.All(api => !source.Contains(api, StringComparison.Ordinal)), "the verb uses no write, pipe or process API", string.Join(",", writes.Where(api => source.Contains(api, StringComparison.Ordinal))));
            Check(source.Contains("BoundedInput.ReadBytes(", StringComparison.Ordinal) && source.Contains("RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory)", StringComparison.Ordinal), "the verb reads through BoundedInput and resolves the repository like the other verbs");
        }
        var program = File.ReadAllText(Path.Combine(root, "interfaces", "cli", "Program.cs"));
        Check(program.Contains("case \"catalogue-report\": CatalogueReportCommand.Run(args); break;", StringComparison.Ordinal), "Program.cs registers catalogue-report");
        Check(Cli(null, "no-such-verb").Out.Contains("catalogue-report --run", StringComparison.Ordinal), "default usage lists catalogue-report");
    }
}
