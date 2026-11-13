#nullable enable
using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bim.Core;
using Microsoft.Win32.SafeHandles;

namespace Bim.Cli;

// P1-12 `catalogue-report --run <dir>`: the result summary of one owner -Catalogue run (scripts/owner-native-session.ps1
// -Catalogue with scripts/owner-catalogue-steps.ps1) as deterministic JSON. Read-only: it never writes, creates, moves or
// deletes anything, and never talks to Revit.
//
// What the owner session actually writes, and what this verb reads:
//   <dir> = <repository>/.bim/native-runs/<run>   the run folder (fixture, catalogue-requests/, and the saved copy
//                                                 BIM-Agentic-Test-c04-<utc>-r<n>-copia.rvt of the copia group).
//   <repository>/.bim/native-sessions/<utc>/summary.json   mode "catalogue"; runs[] with index and run_directory.
//   <repository>/.bim/native-sessions/<utc>/catalogue-run<index>.json   the run record: schema_version 1, run,
//       document_path, copy_path, groups[], overall, all_passed, aborted, steps[] (index, group, key, command, request,
//       response, passed, blocked, reason, expected, comparisons[] of {check, requested, observed, ok}).
// The run record lives in the session folder, not in the run folder, so the record is found through the session
// summaries: exactly one summary run entry whose run_directory's last segment is <run> (summaries that are links, over
// MaxSummaryBytes, not JSON or not mode "catalogue" are skipped; at most MaxSessionFolders session folders are listed and
// at most MaxSessions catalogue summaries read). The record's session_stamp, when present, must be the session folder's
// name (the session writes both from one stamp).
//
// Path rule: the repository root is the one every verb uses (RevitCommandPolicy.RepositoryRoot from the binary's
// location). <dir> must not contain a ".." segment (catalogue_report_run_traversal) and, after Path.GetFullPath, must be
// exactly one level below <repository>/.bim/native-runs (catalogue_report_run_outside; ordinal comparison, ignoring
// case on Windows). .bim, .bim/native-runs, the run folder, .bim/native-sessions and the copy file may not be links or
// junctions (catalogue_report_link_refused, detail naming which); a link session folder is skipped. A run name with a
// wildcard, ':' or a control character is catalogue_report_run_invalid. The run name is echoed as it is on disk.
//
// The record is checked before anything is reported: duplicate keys, missing or mistyped members, step indexes not
// 1..n in order, a step both passed and blocked (catalogue_report_record_invalid, detail = the member); groups, overall,
// counts (v1) and all_passed must equal what the steps say (catalogue_report_record_inconsistent); the record must be
// about this run (run = the summary index, document_path in <run>, session_stamp = the session folder:
// catalogue_report_record_mismatch). A step's command may be null and its reason any JSON value (echoed as JSON when it
// is not text), as the writer may record them.
// Bounds: record MaxRecordBytes (input_too_large, detail "record", like every BoundedInput refusal), MaxSteps steps,
// MaxComparisons comparisons per step, MaxGroups groups (catalogue_report_bound_exceeded, detail "steps", "comparisons",
// "groups" or "sessions"); echoed texts are cut at MaxTextChars, echoed JSON values over MaxValueChars become
// {"truncated_chars":n}, and at most MaxMismatchRows requested-vs-observed rows are printed in all (the rest counted in
// each step's mismatches_omitted). The copy is hashed only up to MaxCopyBytes: its length is taken after opening it and
// reading stops one byte past the cap (state "too_large"). A copy that is not a regular file (a folder, a FIFO, a device)
// is refused before it is opened (catalogue_report_copy_not_regular_file; lstat on Unix, then the opened handle's type).
//
// Success (exit 0) prints one compact JSON object (non-ASCII escaped, so the bytes do not depend on the console):
// {"status":"succeeded","run":".bim/native-runs/<run>","record":".bim/native-sessions/<s>/catalogue-run<i>.json",
//  "scope","head","aborted","all_passed","overall":{total,passed,failed,blocked,not_sent},"groups":[{key,...}],
//  "failing":[{index,group,key,command,sent,reason,expected,observed_status,observed_code,raw_response,mismatches:[{check,
//  requested,observed}],mismatches_omitted}],"blocked":[{index,group,key,sent,reason}],
//  "copy":{state,file,save_step,save_passed,recorded_check_ok,recorded_sha256,recorded_bytes,disk_sha256,disk_bytes}}.
// failed = total - passed - blocked (the session's own rule: a step not sent and not blocked is failed). copy.state:
// match | mismatch (the file on disk now differs from the sha256/bytes recorded by c-save) | missing_file | not_saved
// (c-save not sent or no copy path recorded) | not_in_run (no c-save step, e.g. -CatalogueV1Only) | outside_run (the
// recorded copy path is not a BIM-Agentic-Test-c04-*-copia.rvt directly in <run>: nothing is read, the rest of the report
// stands) | no_recorded_sha256 | too_large. recorded_check_ok is the run-time "sha256 leído = sha256 del archivo en disco" row.
// P4-18: a record with the doc-up-elements step (command doc-realized, P4-16) gets one more member after copy,
// "realized":{step,passed,state,detail,folder,entries,sha256,bytes,code,reply_status,reply_code,reply_entries,
// reply_sha256,reply_bytes,disk_sha256,disk_bytes}, read from <run>/catalogue-requests/NN-doc-up-elements.doc-realized/
// (realized.json; report.json for a refusal): see WriteRealized. A record without that step prints exactly what it did
// before.
// Every refusal prints one {"status":"failed","code"[,"detail"]} envelope and exits 1. catalogue_report_internal_error is a
// defect (type and message on stderr).
internal static class CatalogueReportCommand
{
    public const string Usage = "usage: catalogue-report --run .bim/native-runs/<run>";
    public const string UsageCode = "catalogue_report_usage";
    public const string RepositoryNotFound = "catalogue_report_repository_not_found";
    public const string RunInvalid = "catalogue_report_run_invalid";
    public const string RunTraversal = "catalogue_report_run_traversal";
    public const string RunOutside = "catalogue_report_run_outside";
    public const string RunNotFound = "catalogue_report_run_not_found";
    public const string LinkRefused = "catalogue_report_link_refused";
    public const string RecordNotFound = "catalogue_report_record_not_found";
    public const string RecordAmbiguous = "catalogue_report_record_ambiguous";
    public const string RecordInvalid = "catalogue_report_record_invalid";
    public const string RecordInconsistent = "catalogue_report_record_inconsistent";
    public const string RecordMismatch = "catalogue_report_record_mismatch";
    public const string CopyNotRegular = "catalogue_report_copy_not_regular_file";
    public const string BoundExceeded = "catalogue_report_bound_exceeded";
    public const string IoError = "catalogue_report_io_error";
    public const string InternalError = "catalogue_report_internal_error";

    public const int MaxRecordBytes = 32 * 1024 * 1024;
    public const int MaxSummaryBytes = 1024 * 1024;
    public const int MaxSessions = 1000;
    public const int MaxSessionFolders = 10_000;
    public const int MaxRunIndex = 10;
    public const int MaxSteps = 1000;
    public const int MaxComparisons = 256;
    public const int MaxGroups = 64;
    public const int MaxTextChars = 2000;
    public const int MaxValueChars = 1024;
    public const int MaxMismatchRows = 1000;
    public const long MaxCopyBytes = 8L * 1024 * 1024 * 1024;

    const string SaveKey = "c-save";
    const string SaveCommand = "document.save_copy";
    const string ShaCheck = "sha256 leído = sha256 del archivo en disco";
    static readonly Regex CopyName = new(@"\ABIM-Agentic-Test-c04-[A-Za-z0-9._-]+-copia\.rvt\z", RegexOptions.CultureInvariant);
    static readonly Regex DocumentName = new(@"\ABIM-Agentic-Test[A-Za-z0-9._-]*\.[rR][vV][tT]\z", RegexOptions.CultureInvariant);
    static readonly Regex GroupKey = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
    static readonly Regex Sha256 = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    // doc-realized prints its sha256 in upper case (as doc-update does).
    static readonly Regex Sha256Any = new(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
    const string RealizedKey = "doc-up-elements";
    const string RealizedCommand = "doc-realized";
    static readonly string[] CountNames = { "total", "passed", "failed", "blocked", "not_sent" };

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    sealed record Counts(int Total, int Passed, int Blocked, int NotSent)
    {
        public int Failed => Total - Passed - Blocked;
        public int Get(string name) => name switch { "total" => Total, "passed" => Passed, "failed" => Failed, "blocked" => Blocked, _ => NotSent };
    }

    sealed record Step(JsonElement Element, int Index, string Group, string Key, string? Command, bool Passed, bool Blocked, bool Sent);

    public static void Run(string[] args)
    {
        var repository = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        var (exit, output) = repository is null ? (1, Failure(RepositoryNotFound, null)) : Execute(args, repository);
        Console.WriteLine(output);
        if (exit != 0) Environment.ExitCode = exit;
    }

    // The verb with the repository root given (Run passes the one every verb uses; tests pass a temporary one) and the
    // copy hashing cap (tests pass a small one). Returns the exit code and the one line Run prints.
    internal static (int Exit, string Out) Execute(string[] args, string repositoryRoot, long maxCopyBytes = MaxCopyBytes)
    {
        try { return (0, Report(args, repositoryRoot, maxCopyBytes)); }
        catch (Refusal refusal) { return (1, Failure(refusal.Code, refusal.Detail)); }
        catch (Exception error)
        {
            Console.Error.WriteLine("catalogue-report internal error: " + error.GetType().FullName + ": " + error.Message);
            return (1, Failure(InternalError, null));
        }
    }

    static string Report(string[] args, string repository, long maxCopyBytes)
    {
        if (args.Length != 3 || args[1] != "--run" || args[2].Length == 0 || args[2].StartsWith("--", StringComparison.Ordinal)) throw new Refusal(UsageCode);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        var (runDirectory, runName) = ResolveRun(root, args[2]);
        var (sessionName, recordIndex, recordPath) = FindRecord(root, runName);
        byte[] bytes;
        try { bytes = BoundedInput.ReadBytes(recordPath, MaxRecordBytes); }
        catch (InputRejectedException error) { throw new Refusal(error.Message, "record"); }
        JsonDocument document;
        try { document = JsonDocument.Parse(Decode(bytes)); }
        catch (JsonException) { throw new Refusal(RecordInvalid, "json"); }
        using (document)
        {
            var record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object) throw new Refusal(RecordInvalid, "root");
            if (HasDuplicateKey(record)) throw new Refusal(RecordInvalid, "duplicate_key");
            if (Int(record, "schema_version", "schema_version") != 1) throw new Refusal(RecordInvalid, "schema_version");
            if (Int(record, "run", "run") != recordIndex) throw new Refusal(RecordMismatch, "run");
            var documentPath = Text(record, "document_path", "document_path", nullable: false)!;
            if (!InRun(documentPath, runName, DocumentName)) throw new Refusal(RecordMismatch, "document_path");
            if (Optional(record, "session_stamp", "session_stamp") is { } stamp && stamp != sessionName) throw new Refusal(RecordMismatch, "session_stamp");
            var steps = Steps(record);
            var groups = GroupCounts(steps);
            var overall = Count(steps);
            CheckConsistency(record, groups, overall);
            var aborted = Text(record, "aborted", "aborted", nullable: true);
            var scope = Optional(record, "scope", "scope");
            var head = Optional(record, "head", "head");
            var copyPath = Optional(record, "copy_path", "copy_path");

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("status", "succeeded");
                writer.WriteString("run", ".bim/native-runs/" + runName);
                writer.WriteString("record", ".bim/native-sessions/" + sessionName + "/" + RecordFile(recordIndex));
                WriteText(writer, "scope", scope);
                WriteText(writer, "head", head);
                WriteText(writer, "aborted", aborted);
                writer.WriteBoolean("all_passed", overall.Failed == 0 && overall.Blocked == 0);
                writer.WritePropertyName("overall");
                WriteCounts(writer, null, overall);
                writer.WriteStartArray("groups");
                foreach (var (key, counts) in groups) WriteCounts(writer, key, counts);
                writer.WriteEndArray();
                WriteFailing(writer, steps);
                writer.WriteStartArray("blocked");
                foreach (var step in steps.Where(s => s.Blocked))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("index", step.Index);
                    writer.WriteString("group", step.Group);
                    writer.WriteString("key", step.Key);
                    writer.WriteBoolean("sent", step.Sent);
                    WriteReason(writer, step.Element);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                WriteCopy(writer, steps, copyPath, runDirectory, runName, maxCopyBytes);
                WriteRealized(writer, steps, runDirectory);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
    }

    // ---- paths ----

    static (string Directory, string Name) ResolveRun(string root, string raw)
    {
        if (raw.Any(char.IsControl)) throw new Refusal(RunInvalid);
        if (raw.Split('/', '\\').Any(segment => segment == "..")) throw new Refusal(RunTraversal);
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException) { throw new Refusal(RunInvalid); }
        var bim = Path.Combine(root, ".bim");
        var runs = Path.Combine(bim, "native-runs");
        var parent = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (parent is null || name.Length == 0 || !SamePath(parent, runs)) throw new Refusal(RunOutside);
        if (name.IndexOfAny(new[] { ':', '/', '\\', '*', '?', '<', '>', '"', '|' }) >= 0) throw new Refusal(RunInvalid);
        RequireDirectory(bim, ".bim", RunNotFound);
        RequireDirectory(runs, "native-runs", RunNotFound);
        RequireDirectory(full, "run", RunNotFound);
        // The name as it is on disk (a differently cased argument on Windows still echoes and matches the same name).
        string onDisk;
        try { onDisk = new DirectoryInfo(runs).EnumerateDirectories(name).Select(d => d.Name).Where(n => string.Equals(n, name, PathComparison)).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault() ?? name; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal(IoError, "native-runs"); }
        return (Path.Combine(runs, onDisk), onDisk);
    }

    static void RequireDirectory(string path, string label, string missing)
    {
        var info = new DirectoryInfo(path);
        if (IsLink(info)) throw new Refusal(LinkRefused, label);
        if (!info.Exists) throw new Refusal(missing, label);
    }

    // Symlinks and junctions have a LinkTarget; an unreadable one is refused as a link (fail closed).
    static bool IsLink(FileSystemInfo info)
    {
        try { return info.LinkTarget is not null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), PathComparison);

    static string[] Segments(string path) => path.Split('/', '\\').Where(segment => segment.Length > 0).ToArray();

    // The recorded path (written on Windows, split on both separators) names a file directly in this run's folder.
    static bool InRun(string path, string runName, Regex fileName)
    {
        var segments = Segments(path);
        return segments.Length >= 2 && fileName.IsMatch(segments[^1]) && string.Equals(segments[^2], runName, PathComparison);
    }

    static string RecordFile(int index) => "catalogue-run" + index.ToString(CultureInfo.InvariantCulture) + ".json";

    static (string Session, int Index, string Path) FindRecord(string root, string runName)
    {
        var sessions = Path.Combine(root, ".bim", "native-sessions");
        RequireDirectory(sessions, "native-sessions", RecordNotFound);
        var directories = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(sessions))
            {
                if (directories.Count == MaxSessionFolders) throw new Refusal(BoundExceeded, "sessions");
                directories.Add(directory);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal(IoError, "native-sessions"); }
        directories.Sort(StringComparer.Ordinal);
        var found = new List<(string Session, int Index, string Path)>();
        var catalogueSummaries = 0;
        foreach (var directory in directories)
        {
            if (IsLink(new DirectoryInfo(directory))) continue;
            // Only catalogue summaries count toward MaxSessions (A/B sessions and unreadable summaries are skipped).
            if (MatchingRuns(Path.Combine(directory, "summary.json"), runName) is not { } indexes) continue;
            if (++catalogueSummaries > MaxSessions) throw new Refusal(BoundExceeded, "sessions");
            foreach (var index in indexes)
                found.Add((Path.GetFileName(directory), index, Path.Combine(directory, RecordFile(index))));
        }
        if (found.Count == 0) throw new Refusal(RecordNotFound);
        if (found.Count > 1) throw new Refusal(RecordAmbiguous, string.Join(",", found.Select(f => f.Session + "/" + RecordFile(f.Index))));
        var record = found[0];
        if (!File.Exists(record.Path) && !IsLink(new FileInfo(record.Path))) throw new Refusal(RecordNotFound, record.Session + "/" + RecordFile(record.Index));
        return record;
    }

    // The run indexes of a catalogue summary that name runName; null when it is not a readable catalogue summary.
    static List<int>? MatchingRuns(string summaryPath, string runName)
    {
        var result = new List<int>();
        if (!File.Exists(summaryPath)) return null;
        byte[] bytes;
        try { bytes = BoundedInput.ReadBytes(summaryPath, MaxSummaryBytes); }
        catch (InputRejectedException) { return null; }
        try
        {
            using var document = JsonDocument.Parse(Decode(bytes));
            var summary = document.RootElement;
            if (summary.ValueKind != JsonValueKind.Object) return null;
            if (!summary.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "catalogue") return null;
            if (!summary.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return result;
            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind != JsonValueKind.Object) continue;
                if (!run.TryGetProperty("run_directory", out var directory) || directory.ValueKind != JsonValueKind.String) continue;
                if (!run.TryGetProperty("index", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var index) || index < 1 || index > MaxRunIndex) continue;
                var segments = Segments(directory.GetString()!);
                if (segments.Length > 0 && string.Equals(segments[^1], runName, PathComparison) && !result.Contains(index)) result.Add(index);
            }
        }
        catch (JsonException) { return null; }
        return result;
    }

    // Exactly as BoundedInput.ReadText decodes: UTF-8 by default, byte order marks detected and stripped.
    static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    // ---- record ----

    static Step[] Steps(JsonElement record)
    {
        if (!record.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) throw new Refusal(RecordInvalid, "steps");
        var length = steps.GetArrayLength();
        if (length == 0) throw new Refusal(RecordInvalid, "steps");
        if (length > MaxSteps) throw new Refusal(BoundExceeded, "steps");
        var result = new List<Step>(length);
        var position = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var at = $"steps[{position.ToString(CultureInfo.InvariantCulture)}]";
            if (step.ValueKind != JsonValueKind.Object) throw new Refusal(RecordInvalid, at);
            if (Int(step, "index", at + ".index") != position + 1) throw new Refusal(RecordInvalid, at + ".index");
            var group = Text(step, "group", at + ".group", nullable: false)!;
            if (!GroupKey.IsMatch(group)) throw new Refusal(RecordInvalid, at + ".group");
            var key = Text(step, "key", at + ".key", nullable: false)!;
            if (key.Length == 0 || key.Length > 128) throw new Refusal(RecordInvalid, at + ".key");
            var command = Text(step, "command", at + ".command", nullable: true);
            var passed = Bool(step, "passed", at + ".passed");
            var blocked = Bool(step, "blocked", at + ".blocked");
            if (passed && blocked) throw new Refusal(RecordInvalid, at + ".blocked");
            if (!step.TryGetProperty("reason", out _)) throw new Refusal(RecordInvalid, at + ".reason");
            if (!step.TryGetProperty("request", out var request)) throw new Refusal(RecordInvalid, at + ".request");
            var sent = request.ValueKind != JsonValueKind.Null;
            if (sent && request.ValueKind != JsonValueKind.Object) throw new Refusal(RecordInvalid, at + ".request");
            if (passed && !sent) throw new Refusal(RecordInvalid, at + ".request");
            if (!step.TryGetProperty("comparisons", out var comparisons) || comparisons.ValueKind != JsonValueKind.Array) throw new Refusal(RecordInvalid, at + ".comparisons");
            if (comparisons.GetArrayLength() > MaxComparisons) throw new Refusal(BoundExceeded, "comparisons");
            var row = 0;
            foreach (var comparison in comparisons.EnumerateArray())
            {
                var where = $"{at}.comparisons[{row.ToString(CultureInfo.InvariantCulture)}]";
                if (comparison.ValueKind != JsonValueKind.Object) throw new Refusal(RecordInvalid, where);
                Text(comparison, "check", where + ".check", nullable: false);
                Bool(comparison, "ok", where + ".ok");
                if (!comparison.TryGetProperty("requested", out _)) throw new Refusal(RecordInvalid, where + ".requested");
                if (!comparison.TryGetProperty("observed", out _)) throw new Refusal(RecordInvalid, where + ".observed");
                row++;
            }
            result.Add(new Step(step, position + 1, group, key, command, passed, blocked, sent));
            position++;
        }
        return result.ToArray();
    }

    static Counts Count(IEnumerable<Step> steps)
    {
        int total = 0, passed = 0, blocked = 0, notSent = 0;
        foreach (var step in steps)
        {
            total++;
            if (step.Passed) passed++;
            if (step.Blocked) blocked++;
            if (!step.Sent) notSent++;
        }
        return new Counts(total, passed, blocked, notSent);
    }

    // Groups in order of their first step.
    static List<(string Key, Counts Counts)> GroupCounts(Step[] steps)
    {
        var order = new List<string>();
        foreach (var step in steps) if (!order.Contains(step.Group, StringComparer.Ordinal)) order.Add(step.Group);
        if (order.Count > MaxGroups) throw new Refusal(BoundExceeded, "groups");
        return order.Select(key => (Key: key, Counts: Count(steps.Where(s => s.Group == key)))).ToList();
    }

    static void CheckConsistency(JsonElement record, List<(string Key, Counts Counts)> groups, Counts overall)
    {
        if (!record.TryGetProperty("groups", out var recorded) || recorded.ValueKind != JsonValueKind.Array) throw new Refusal(RecordInvalid, "groups");
        if (recorded.GetArrayLength() > MaxGroups) throw new Refusal(BoundExceeded, "groups");
        if (recorded.GetArrayLength() != groups.Count) throw new Refusal(RecordInconsistent, "groups");
        var i = 0;
        foreach (var group in recorded.EnumerateArray())
        {
            var at = $"groups[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (group.ValueKind != JsonValueKind.Object) throw new Refusal(RecordInvalid, at);
            if (Text(group, "key", at + ".key", nullable: false) != groups[i].Key || !SameCounts(group, groups[i].Counts, at)) throw new Refusal(RecordInconsistent, "groups");
            i++;
        }
        if (!record.TryGetProperty("overall", out var total) || total.ValueKind != JsonValueKind.Object) throw new Refusal(RecordInvalid, "overall");
        if (!SameCounts(total, overall, "overall")) throw new Refusal(RecordInconsistent, "overall");
        // counts: the v1 group's counts (zero when the run has no v1 step); older records may lack it.
        if (record.TryGetProperty("counts", out var v1))
        {
            if (v1.ValueKind != JsonValueKind.Object) throw new Refusal(RecordInvalid, "counts");
            var expected = groups.Where(g => g.Key == "v1").Select(g => g.Counts).FirstOrDefault() ?? new Counts(0, 0, 0, 0);
            if (!SameCounts(v1, expected, "counts")) throw new Refusal(RecordInconsistent, "counts");
        }
        if (Bool(record, "all_passed", "all_passed") != (overall.Failed == 0 && overall.Blocked == 0)) throw new Refusal(RecordInconsistent, "all_passed");
    }

    static bool SameCounts(JsonElement element, Counts counts, string at)
    {
        foreach (var name in CountNames) if (Int(element, name, at + "." + name) != counts.Get(name)) return false;
        return true;
    }

    static int Int(JsonElement element, string name, string at)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)) throw new Refusal(RecordInvalid, at);
        return number;
    }

    static bool Bool(JsonElement element, string name, string at)
    {
        if (!element.TryGetProperty(name, out var value) || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)) throw new Refusal(RecordInvalid, at);
        return value.GetBoolean();
    }

    // A required member: a string, or null when nullable.
    static string? Text(JsonElement element, string name, string at, bool nullable)
    {
        if (!element.TryGetProperty(name, out var value)) throw new Refusal(RecordInvalid, at);
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        throw new Refusal(RecordInvalid, at);
    }

    // A member that older records may lack: absent or null is null; anything but a string is refused.
    static string? Optional(JsonElement element, string name, string at) =>
        element.TryGetProperty(name, out _) ? Text(element, name, at, nullable: true) : null;

    static JsonElement? Member(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out var next)) return null;
            current = next;
        }
        return current;
    }

    static string? StringMember(JsonElement element, params string[] path) =>
        Member(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    static bool HasDuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    if (!names.Add(property.Name) || HasDuplicateKey(property.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) if (HasDuplicateKey(item)) return true;
                return false;
            default:
                return false;
        }
    }

    // ---- output ----

    static void WriteCounts(Utf8JsonWriter writer, string? key, Counts counts)
    {
        writer.WriteStartObject();
        if (key is not null) writer.WriteString("key", key);
        foreach (var name in CountNames) writer.WriteNumber(name, counts.Get(name));
        writer.WriteEndObject();
    }

    static void WriteFailing(Utf8JsonWriter writer, Step[] steps)
    {
        var budget = MaxMismatchRows;
        writer.WriteStartArray("failing");
        foreach (var step in steps.Where(s => !s.Passed && !s.Blocked))
        {
            var element = step.Element;
            writer.WriteStartObject();
            writer.WriteNumber("index", step.Index);
            writer.WriteString("group", step.Group);
            writer.WriteString("key", step.Key);
            WriteText(writer, "command", step.Command);
            writer.WriteBoolean("sent", step.Sent);
            WriteReason(writer, element);
            if (element.TryGetProperty("expected", out var expected)) WriteValue(writer, "expected", expected);
            else writer.WriteNull("expected");
            WriteText(writer, "observed_status", StringMember(element, "response", "status"));
            WriteText(writer, "observed_code", StringMember(element, "response", "code"));
            // A response that was not JSON is kept by the session as raw_response (cut like every echoed text).
            WriteText(writer, "raw_response", StringMember(element, "raw_response"));
            var omitted = 0;
            writer.WriteStartArray("mismatches");
            foreach (var comparison in element.GetProperty("comparisons").EnumerateArray())
            {
                if (comparison.GetProperty("ok").GetBoolean()) continue;
                if (budget == 0) { omitted++; continue; }
                budget--;
                writer.WriteStartObject();
                WriteText(writer, "check", comparison.GetProperty("check").GetString());
                WriteValue(writer, "requested", comparison.GetProperty("requested"));
                WriteValue(writer, "observed", comparison.GetProperty("observed"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("mismatches_omitted", omitted);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static void WriteCopy(Utf8JsonWriter writer, Step[] steps, string? copyPath, string runDirectory, string runName, long maxCopyBytes)
    {
        var save = steps.FirstOrDefault(s => s.Key == SaveKey && s.Command == SaveCommand);
        string state;
        string? file = null, recordedSha = null, diskSha = null;
        long? recordedBytes = null, diskBytes = null;
        bool? checkOk = null;
        if (save is null) state = "not_in_run";
        else
        {
            var readback = Member(save.Element, "response", "readback", "copy");
            foreach (var comparison in save.Element.GetProperty("comparisons").EnumerateArray())
                if (comparison.GetProperty("check").GetString() == ShaCheck) checkOk = comparison.GetProperty("ok").GetBoolean();
            var path = copyPath ?? (readback.HasValue ? StringMember(readback.Value, "path") : null);
            if (!save.Sent || path is null) state = "not_saved";
            // A path that is not a catalogue copy directly in this run is never opened; the rest of the report stands.
            else if (!InRun(path, runName, CopyName)) state = "outside_run";
            else
            {
                file = Segments(path)[^1];
                if (readback is { } copy)
                {
                    // The values c-save read back (Revit's sha256 and size of the copy right after the save).
                    var sha = StringMember(copy, "sha256");
                    if (sha is not null && Sha256.IsMatch(sha)) recordedSha = sha;
                    if (Member(copy, "bytes") is { ValueKind: JsonValueKind.Number } length && length.TryGetInt64(out var count)) recordedBytes = count;
                }
                var full = Path.Combine(runDirectory, file);
                var info = new FileInfo(full);
                if (IsLink(info)) throw new Refusal(LinkRefused, "copy");
                if (Directory.Exists(full)) throw new Refusal(CopyNotRegular);
                if (!info.Exists) state = "missing_file";
                else
                {
                    var (sha, bytes) = Hash(full, maxCopyBytes);
                    diskSha = sha;
                    diskBytes = bytes;
                    state = sha is null ? "too_large"
                        : recordedSha is null ? "no_recorded_sha256"
                        : recordedSha == sha && (recordedBytes is null || recordedBytes == bytes) ? "match" : "mismatch";
                }
            }
        }
        writer.WriteStartObject("copy");
        writer.WriteString("state", state);
        WriteText(writer, "file", file);
        if (save is null) { writer.WriteNull("save_step"); writer.WriteNull("save_passed"); }
        else { writer.WriteNumber("save_step", save.Index); writer.WriteBoolean("save_passed", save.Passed); }
        if (checkOk is { } ok) writer.WriteBoolean("recorded_check_ok", ok); else writer.WriteNull("recorded_check_ok");
        WriteText(writer, "recorded_sha256", recordedSha);
        if (recordedBytes is { } recorded) writer.WriteNumber("recorded_bytes", recorded); else writer.WriteNull("recorded_bytes");
        WriteText(writer, "disk_sha256", diskSha);
        if (diskBytes is { } disk) writer.WriteNumber("disk_bytes", disk); else writer.WriteNull("disk_bytes");
        writer.WriteEndObject();
    }

    // P4-18: the doc-up-elements step (command doc-realized) and its folder <run>/catalogue-requests/<NN>-doc-up-elements.doc-realized
    // (NN = the step index, two digits at least, as Invoke-CatalogueDocRealized names it). Written only when the record
    // has that step, so every other record keeps its bytes. Nothing recorded is trusted for the map: entries, sha256 and
    // bytes are the file's, given only when it hashes to the step reply (state match). States, in order: not_sent, no_reply
    // (no JSON object reply, or detail "status": neither succeeded nor failed), link_refused (detail catalogue-requests
    // or folder: never read), missing_folder; for a succeeded reply first report.json (the kept CLI line): report_mismatch
    // (detail report_missing; report_invalid: not a JSON object with a text status; report_differs: its status, sha256
    // [ignoring case], entries or bytes differ from the reply) or unreadable, then realized.json: missing_file, unreadable (detail the input_* code;
    // read up to DocRealized.MaxCanonicalBytes, the doc-update --realized cap), mismatch (detail sha256 or bytes: the file
    // differs from the reply), invalid (detail json: not an object of texts without duplicate keys; entries: another
    // count than replied), match; for a failed reply refused (code = report.json's, equal to the reply's) or
    // report_mismatch (detail output_present, report_missing, report_invalid or report_differs), unreadable for an
    // unreadable report.json (read up to MaxSummaryBytes). reply_sha256 is printed lower-cased. A folder or file swapped
    // between the link checks and the reads is accepted: it needs write access to the run folder, and BoundedInput still
    // refuses a link or non-regular file at read time.
    static void WriteRealized(Utf8JsonWriter writer, Step[] steps, string runDirectory)
    {
        var step = steps.FirstOrDefault(s => s.Key == RealizedKey && s.Command == RealizedCommand);
        if (step is null) return;
        var folderName = step.Index.ToString("D2", CultureInfo.InvariantCulture) + "-" + RealizedKey + ".doc-realized";
        var reply = Member(step.Element, "response") is { ValueKind: JsonValueKind.Object } r ? r : (JsonElement?)null;
        string? replyStatus = null, replyCode = null, replySha = null;
        int? replyEntries = null;
        long? replyBytes = null;
        if (reply is { } line)
        {
            replyStatus = StringMember(line, "status");
            replyCode = StringMember(line, "code");
            (replySha, replyEntries, replyBytes) = LineFields(line);
        }
        string state;
        string? detail = null, code = null, sha256 = null, diskSha = null;
        int? entries = null;
        long? bytes = null, diskBytes = null;
        var requests = Path.Combine(runDirectory, "catalogue-requests");
        var folder = Path.Combine(requests, folderName);
        if (!step.Sent) state = "not_sent";
        else if (reply is null) state = "no_reply";
        else if (replyStatus is not ("succeeded" or "failed")) { state = "no_reply"; detail = "status"; }
        else if (IsLink(new DirectoryInfo(requests))) { state = "link_refused"; detail = "catalogue-requests"; }
        else if (IsLink(new DirectoryInfo(folder))) { state = "link_refused"; detail = "folder"; }
        else if (!Directory.Exists(folder)) state = "missing_folder";
        else if (replyStatus == "succeeded")
        {
            // The line the invoker kept (report.json) must say what the record's reply says before the map is read.
            var (kept, keptProblem) = ReadRun(Path.Combine(folder, "report.json"), MaxSummaryBytes);
            var (content, problem) = kept is null ? (null, null) : ReadRun(Path.Combine(folder, "realized.json"), DocRealized.MaxCanonicalBytes);
            if (kept is null) { state = keptProblem == BoundedInput.NotFound ? "report_mismatch" : "unreadable"; detail = keptProblem == BoundedInput.NotFound ? "report_missing" : keptProblem; }
            else if (KeptFields(kept) is not { } keptLine) { state = "report_mismatch"; detail = "report_invalid"; }
            else if (keptLine != ("succeeded", replySha, replyEntries, replyBytes)) { state = "report_mismatch"; detail = "report_differs"; }
            else if (content is null) { state = problem == BoundedInput.NotFound ? "missing_file" : "unreadable"; if (state == "unreadable") detail = problem; }
            else
            {
                diskSha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                diskBytes = content.LongLength;
                if (replySha is null || !string.Equals(replySha, diskSha, StringComparison.OrdinalIgnoreCase)) { state = "mismatch"; detail = "sha256"; }
                else if (replyBytes != diskBytes) { state = "mismatch"; detail = "bytes"; }
                else if (MapEntries(content) is not { } count) { state = "invalid"; detail = "json"; }
                else if (count != replyEntries) { state = "invalid"; detail = "entries"; }
                else { state = "match"; entries = count; sha256 = diskSha; bytes = diskBytes; }
            }
        }
        else
        {
            var output = Path.Combine(folder, "realized.json");
            if (File.Exists(output) || Directory.Exists(output) || IsLink(new FileInfo(output))) { state = "report_mismatch"; detail = "output_present"; }
            else
            {
                var (content, problem) = ReadRun(Path.Combine(folder, "report.json"), MaxSummaryBytes);
                if (content is null) { state = problem == BoundedInput.NotFound ? "report_mismatch" : "unreadable"; detail = problem == BoundedInput.NotFound ? "report_missing" : problem; }
                else if (FailureCode(content) is not { } kept) { state = "report_mismatch"; detail = "report_invalid"; }
                else if (kept != replyCode) { state = "report_mismatch"; detail = "report_differs"; }
                else { state = "refused"; code = kept; }
            }
        }
        writer.WriteStartObject("realized");
        writer.WriteNumber("step", step.Index);
        writer.WriteBoolean("passed", step.Passed);
        writer.WriteString("state", state);
        WriteText(writer, "detail", detail);
        writer.WriteString("folder", "catalogue-requests/" + folderName);
        if (entries is { } x) writer.WriteNumber("entries", x); else writer.WriteNull("entries");
        WriteText(writer, "sha256", sha256);
        if (bytes is { } y) writer.WriteNumber("bytes", y); else writer.WriteNull("bytes");
        WriteText(writer, "code", code);
        WriteText(writer, "reply_status", replyStatus);
        WriteText(writer, "reply_code", replyCode);
        if (replyEntries is { } z) writer.WriteNumber("reply_entries", z); else writer.WriteNull("reply_entries");
        WriteText(writer, "reply_sha256", replySha);
        if (replyBytes is { } w) writer.WriteNumber("reply_bytes", w); else writer.WriteNull("reply_bytes");
        WriteText(writer, "disk_sha256", diskSha);
        if (diskBytes is { } v) writer.WriteNumber("disk_bytes", v); else writer.WriteNull("disk_bytes");
        writer.WriteEndObject();
    }

    // sha256 (64 hex, lower-cased), entries and bytes of a doc-realized line; each null when absent or malformed.
    static (string? Sha, int? Entries, long? Bytes) LineFields(JsonElement line)
    {
        string? sha = StringMember(line, "sha256") is { } text && Sha256Any.IsMatch(text) ? text.ToLowerInvariant() : null;
        int? entries = Member(line, "entries") is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var n) && n >= 0 ? n : null;
        long? bytes = Member(line, "bytes") is { ValueKind: JsonValueKind.Number } b && b.TryGetInt64(out var m) && m >= 0 ? m : null;
        return (sha, entries, bytes);
    }

    // status, sha256, entries and bytes of a kept line (a JSON object with a text status); null when it is not one.
    static (string Status, string? Sha, int? Entries, long? Bytes)? KeptFields(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(Decode(content));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateKey(root) || StringMember(root, "status") is not { } status) return null;
            var (sha, entries, bytes) = LineFields(root);
            return (status, sha, entries, bytes);
        }
        catch (JsonException) { return null; }
    }

    // A file of the run folder through BoundedInput (regular files only, links refused, bounded): its bytes, or the
    // input_* code that refused it.
    static (byte[]? Content, string? Problem) ReadRun(string path, int maxBytes)
    {
        try { return (BoundedInput.ReadBytes(path, maxBytes), null); }
        catch (InputRejectedException error) { return (null, error.Message); }
    }

    // The entry count of a realized map: a JSON object of texts with no duplicate key; null otherwise.
    static int? MapEntries(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(Decode(content));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateKey(root)) return null;
            var count = 0;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) return null;
                count++;
            }
            return count;
        }
        catch (JsonException) { return null; }
    }

    // The code of a kept refusal envelope ({"status":"failed","code":...}); null when it is not one.
    static string? FailureCode(byte[] content)
    {
        try
        {
            using var document = JsonDocument.Parse(Decode(content));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateKey(root) || StringMember(root, "status") != "failed") return null;
            return StringMember(root, "code");
        }
        catch (JsonException) { return null; }
    }

    // Lowercase hex SHA-256 and length of the copy, or a null hash when it is longer than maxCopyBytes. Revit may keep the
    // copy open, so it is opened shared for write and delete (read only). Never opens anything but a regular file: on Unix
    // lstat first (opening a FIFO would block), then the opened handle's type on every system; the length is the opened
    // file's, and reading stops one byte past the cap whatever the length said.
    static (string? Sha, long Bytes) Hash(string path, long maxCopyBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            var pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            if ((UnixMode(status => NativeLStat(pathBytes, status)) & TypeMask) != RegularType) throw new Refusal(CopyNotRegular);
        }
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal(IoError, "copy"); }
        using (stream)
        {
            try
            {
                if (OperatingSystem.IsWindows()) { if (GetFileType(stream.SafeFileHandle) != FileTypeDisk) throw new Refusal(CopyNotRegular); }
                else if ((UnixMode(status => NativeFStat(stream.SafeFileHandle, status)) & TypeMask) != RegularType) throw new Refusal(CopyNotRegular);
                var length = stream.Length;
                if (length > maxCopyBytes) return (null, length);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxCopyBytes) return (null, length);
                    hash.AppendData(buffer, 0, read);
                }
                return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal(IoError, "copy"); }
        }
    }

    // File type checks as in BoundedInput: GetFileType on Windows; lstat/fstat through the runtime's own native shim on
    // Unix (FileStatus: int32 Flags then int32 Mode, whose S_IFMT bits are the type). A missing shim fails closed.
    const int FileTypeDisk = 1, TypeMask = 0xF000, RegularType = 0x8000, StatusBytes = 512;
    [DllImport("kernel32.dll")]
    private static extern int GetFileType(SafeFileHandle handle);
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat")]
    private static extern int NativeLStat(byte[] path, [Out] byte[] status);
    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat")]
    private static extern int NativeFStat(SafeFileHandle fd, [Out] byte[] status);

    static int UnixMode(Func<byte[], int> call)
    {
        var status = new byte[StatusBytes];
        int result;
        try { result = call(status); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { throw new Refusal(IoError, "copy_type_unverifiable"); }
        if (result != 0) throw new Refusal(IoError, "copy");
        return BitConverter.ToInt32(status, 4);
    }

    static void WriteReason(Utf8JsonWriter writer, JsonElement step)
    {
        var reason = step.GetProperty("reason");
        if (reason.ValueKind == JsonValueKind.String) WriteText(writer, "reason", reason.GetString());
        else if (reason.ValueKind == JsonValueKind.Null) writer.WriteNull("reason");
        else WriteValue(writer, "reason", reason);
    }

    static void WriteText(Utf8JsonWriter writer, string name, string? text)
    {
        if (text is null) { writer.WriteNull(name); return; }
        if (text.Length <= MaxTextChars) { writer.WriteString(name, text); return; }
        var cut = MaxTextChars;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        writer.WriteString(name, text[..cut] + " ...(+" + (text.Length - cut).ToString(CultureInfo.InvariantCulture) + " chars)");
    }

    static void WriteValue(Utf8JsonWriter writer, string name, JsonElement value)
    {
        var raw = value.GetRawText();
        if (raw.Length > MaxValueChars)
        {
            writer.WriteStartObject(name);
            writer.WriteNumber("truncated_chars", raw.Length);
            writer.WriteEndObject();
            return;
        }
        writer.WritePropertyName(name);
        value.WriteTo(writer);
    }

    static string Failure(string code, string? detail) =>
        detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
}
