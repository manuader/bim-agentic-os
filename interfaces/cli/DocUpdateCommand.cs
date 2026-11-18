#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P4-08 `doc-update --before b.json --after a.json --realized r.json --bindings bindings.json --output plan.json
// [--dependents dependents.json]`: the P4-07 documentation update plan (core/Bim.Core/DocUpdate.cs) of a BIM-IR edit,
// written to a new file. A dry plan: DocUpdate.Plan only, never Run; no transport, no pipe, no Revit, no store.
//   before/after   Projects, read like `diff-edits --project` (default System.Text.Json; null elements, geometry or
//                  dependencies refused as doc_update_project_invalid, detail "before" or "after").
//   realized       a JSON object {"doc:<key>"|"element:<id D>": "<Revit UniqueId>"} of string values (DocReplace's
//                  realized map; its key and UniqueId rules are the library's realized_invalid).
//   bindings       {"document":{"path","unique_id"},"levels":{"<BIM-IR level id>":"<Revit level>"}, optional
//                  "wall_types":[{"thickness_metres","type"}], "door_symbol", "window_symbol"} (the member names of the
//                  mirror-drift request); the after-state RevitBindings DocRegeneration needs (Revit document, levels).
//                  "document" and "levels" are required objects; an optional member given as null is the same as absent.
//                  Two wall types of one thickness are refused (their order would decide the bytes of the plan).
//   dependents     optional {"<regenerated key>":["<UniqueId>",...]}, the dry-run dependents of DocUpdate.Plan.
// Every object of realized, bindings and dependents refuses duplicate keys and unknown members.
// Output: the file holds exactly JsonSerializer.Serialize(DocUpdate.Plan(before, after, bindings, realized, dependents))
// (default options, compact, UTF-8 without BOM, no trailing newline): byte-identical to the library call. Deterministic:
// the plan sorts its maps and keys, so input member order and culture do not change a byte.
// Bounded input: every file goes through BoundedInput.ReadText (8 MiB, regular files only, links refused; input_* codes
// with detail naming the input); at most MaxEntries realized entries, MaxEntries level bindings, MaxEntries wall types
// and MaxEntries dependent UniqueIds in total (doc_update_input_bound_exceeded, detail "realized", "bindings" or
// "dependents").
// Never-overwrite: the output path follows GltfExportCommand.OutputProblem with extension .json (never an existing file,
// directory or link; checked before any input is read) and is created exclusively (GltfExporter.WriteNew).
// Success prints {"status":"succeeded","output","bytes","sha256","regenerated","steps","forced","retired","applied":false}
// and exits 0. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope, exits 1 and writes no file:
// doc_update_usage, output_*, input_*, doc_update_project_invalid, doc_update_realized_invalid,
// doc_update_bindings_invalid, doc_update_dependents_invalid, doc_update_input_bound_exceeded, the library's codes
// (LibraryCodes: the ArgumentException / InvalidOperationException messages of DocDefaults, Compiler.Diff,
// DocRegeneration and DocReplace, such as invalid_ir, revision_conflict, realized_invalid, input_unresolved,
// dependents_unknown_key; a RevitCommandException's Code with detail = its field, such as document_path_invalid)
// and doc_update_internal_error (a defect: type and message on stderr). The summary counts are taken from the plan
// before the file is written, so nothing can fail after the write.
// Precedence: usage, output path, inputs read (before, after, realized, bindings, dependents), parsed in that order, then
// the library; the first failure is the only one reported.
// P4-10 --probe-dependents process-pipe-name [--accept uid]... (instead of --dependents; --accept only with it): the
// dependents come from Revit instead of a file. DocUpdateProbe (interfaces/cli/DocUpdateProbe.cs) runs
// DocUpdateDependents.Plan over the process pipe, which sends only element.delete dry runs, and the file holds exactly
// JsonSerializer.Serialize(result.Plan), the replanned update (the same canonical plan DocUpdate.Plan gives with those
// dependents). accepted is the --accept UniqueIds (untracked dependents a human confirmed; invalid ones are
// accepted_invalid before the policy is read or anything is sent; more than MaxAccepted --accept values are
// doc_update_input_bound_exceeded, field "accepted", before any input is read). Precedence with --probe-dependents:
// usage, output path, pipe name (explicit_process_pipe_required, field pipe_name), the --accept bound, inputs read and
// parsed, accepted_invalid, the first plan (its library refusals), then the enabled-roots policy, read lazily and once,
// just before the first dry run (a plan without deletes never reads it; its RevitCommandException is reported like a
// dry run's refusal, with the key and operation_id of that dry run). In probe mode every refusal prints
// {"status":"failed","code"[,"key"][,"operation_id"][,"field"][,"detail"]}, members present only when set: "field" names
// the field or input concerned, "detail" is the human detail (e.g. an exception type, a UniqueId); a second-plan refusal
// has no key and no operation_id. This covers every DocUpdateDependents failure (dependent_unknown, dependent_kept,
// dependent_planned, dependent_shared, dependents_changed, replan_internal, a second-plan DocReplace code, or a dry run's
// own failure such as document_not_enabled, pipe_connect_timeout, no_response, response_invalid); exit 1, no file. A
// stale target Revit already deleted (reference_not_found on args.element_uids) is a Missing probe, not a refusal.
// Success adds "probed" (dry runs of the first pass) and "dependents" (UniqueIds collected) before "applied":false.
// Without --probe-dependents nothing changes: no policy, no pipe, the P4-08 bytes, summary and refusal envelopes
// ({"status","code"[,"detail"]}, detail = the field or input name as before).
// P4-12 --execute --expected-document <sha256> (both or neither, only with --probe-dependents; --execute takes no value;
// <sha256> is 64 hex digits, any case; anything else is doc_update_usage): after the probe, a plan whose bytes do not hash
// to <sha256> is refused doc_update_plan_changed (field "expected_document", detail the SHA-256 of this plan; no file,
// no write sent); otherwise the plan file is written exactly as without --execute and only then is the plan run by
// DocUpdateExecute (interfaces/cli/DocUpdateExecute.cs) on the channel of the dry runs. Its line and exit code replace
// the dry summary. Without --execute nothing changes: the dry plan, its bytes and its summary are those above.
// P4-22: DocUpdateCommand is split in two parts. This file is the verb (arguments, output path, summary, write, and the
// probe and run hand-off); interfaces/cli/DocUpdateDryPlan.cs holds the input reading and parsing, the library-code filter,
// the refusal envelope and DryPlan, which the read-only MCP server compiles without this file, DocUpdateProbe.cs or
// DocUpdateExecute.cs.
internal static partial class DocUpdateCommand
{
    public const string Usage = "usage: doc-update --before before.json --after after.json --realized realized.json --bindings bindings.json --output plan.json [--dependents dependents.json | --probe-dependents process-pipe-name [--accept uid]... [--execute --expected-document plan-sha256]] (--output: a new file, never overwritten; plan-sha256: the sha256 a dry run printed)";
    public const string UsageCode = "doc_update_usage";
    // At most this many --accept values (P4-10).
    public const int MaxAccepted = 10_000;

    static readonly string[] Required = { "--before", "--after", "--realized", "--bindings", "--output" };

    public static void Run(string[] args)
    {
        if (Execute(args, Console.Out, DocUpdateRevit.Real) != 0) Environment.ExitCode = 1;
    }

    // The verb on an explicit stdout and Revit access (the CLI passes Console.Out and DocUpdateRevit.Real); returns the
    // exit code. revit is used only with --probe-dependents; an internal error's type and message go to Console.Error.
    internal static int Execute(string[] args, TextWriter stdout, DocUpdateRevit revit)
    {
        string? pipe = null;
        // Plain mode keeps the P4-08 envelope {"status","code"[,"detail"]} (detail = field, else detail); probe mode
        // prints field and detail as separate members.
        int Fail(string code, string? field, string? detail, string? key = null, string? operation = null)
        {
            if (pipe is null)
            {
                stdout.WriteLine(PlainFailure(code, field ?? detail));
                return 1;
            }
            var envelope = new JsonObject { ["status"] = "failed", ["code"] = code };
            if (key is not null) envelope["key"] = key;
            if (operation is not null) envelope["operation_id"] = operation;
            if (field is not null) envelope["field"] = field;
            if (detail is not null) envelope["detail"] = detail;
            stdout.WriteLine(envelope.ToJsonString());
            return 1;
        }
        if (!TryParse(args, out var paths, out var accepted, out var execute)) return Fail(UsageCode, null, null);
        var output = paths["--output"];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string refused) return Fail(refused, null, null);
        pipe = paths.TryGetValue("--probe-dependents", out var pipeName) ? pipeName : null;
        if (pipe is not null && DocUpdateProbe.PipeProblem(pipe) is { } pipeRefused) return Fail(pipeRefused.Code, pipeRefused.Field, pipeRefused.Detail);
        if (accepted.Count > MaxAccepted) return Fail(BoundExceeded, "accepted", null);
        // One channel for the dry runs and, with --execute, the run: the policy is read once for both.
        var channel = pipe is null ? null : DocUpdateProbe.Channel(pipe, revit);
        try
        {
            var (before, after, realized, bindings, dependents) = ReadInputs(paths["--before"], paths["--after"], paths["--realized"], paths["--bindings"],
                paths.TryGetValue("--dependents", out var dependentsPath) ? dependentsPath : null);
            DocUpdatePlan plan;
            DocUpdateDependentsResult? probed = null;
            if (channel is not { } probeChannel) plan = Library(() => DocUpdate.Plan(before, after, bindings, realized, dependents));
            else
            {
                probed = Library(() => DocUpdateProbe.Plan(before, after, bindings, realized, accepted, probeChannel));
                if (probed.Failed is { } failed) return Fail(failed.Code, failed.Field, failed.Detail, failed.Key, failed.OperationId);
                if (probed.Plan is null) return Fail(InternalError, null, null);
                plan = probed.Plan;
            }
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(plan));
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            // --execute runs only the plan a human reviewed: its bytes must hash to --expected-document.
            if (execute && !string.Equals(sha, paths["--expected-document"], StringComparison.OrdinalIgnoreCase)) return Fail(DocUpdateExecute.PlanChanged, "expected_document", sha);
            object summary = probed is null
                ? new
                {
                    status = "succeeded",
                    output,
                    bytes = bytes.LongLength,
                    sha256 = sha,
                    regenerated = plan.Regeneration.Regenerated.Count,
                    steps = plan.Replacement.Commands.Steps.Count,
                    forced = plan.Forced.Count,
                    retired = plan.Retired.Count,
                    applied = false,
                }
                : new
                {
                    status = "succeeded",
                    output,
                    bytes = bytes.LongLength,
                    sha256 = sha,
                    regenerated = plan.Regeneration.Regenerated.Count,
                    steps = plan.Replacement.Commands.Steps.Count,
                    forced = plan.Forced.Count,
                    retired = plan.Retired.Count,
                    probed = probed.Probes.Count,
                    dependents = probed.Dependents.Values.Sum(list => list.Count),
                    applied = false,
                };
            var line = JsonSerializer.Serialize(summary);
            try { GltfExporter.WriteNew(bytes, output); }
            catch (GltfExportException e) { return Fail(e.Code, null, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Fail("output_io_error", null, e.GetType().Name); }
            if (execute && channel is not null) return DocUpdateExecute.Run(plan, channel, line, stdout);
            stdout.WriteLine(line);
            return 0;
        }
        // Plain mode: the P4-08 detail is the field (a RevitCommandException's own detail was never printed there).
        catch (Refusal refusal) { return Fail(refusal.Code, refusal.Field, pipe is null ? null : refusal.Detail); }
    }

    // The five required flags and either the optional --dependents or the optional --probe-dependents (never both), each
    // once, plus any number of --accept only with --probe-dependents, and --execute (no value) with --expected-document
    // (64 hex digits) once each, both or neither, only with --probe-dependents; every other flag with a non-empty value
    // that is not flag-shaped ("--..."), in any order; nothing else.
    static bool TryParse(string[] args, out Dictionary<string, string> paths, out List<string> accepted, out bool execute)
    {
        paths = new Dictionary<string, string>(StringComparer.Ordinal);
        accepted = new List<string>();
        execute = false;
        if (args.Length < 11) return false;
        var i = 1;
        while (i < args.Length)
        {
            if (args[i] == "--execute")
            {
                if (execute) return false;
                execute = true;
                i++;
                continue;
            }
            if (i + 1 >= args.Length) return false;
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (args[i] == "--accept") accepted.Add(value);
            else if (!(Required.Contains(args[i], StringComparer.Ordinal) || args[i] is "--dependents" or "--probe-dependents" or "--expected-document") || !paths.TryAdd(args[i], value)) return false;
            i += 2;
        }
        if (paths.ContainsKey("--dependents") && paths.ContainsKey("--probe-dependents")) return false;
        if (accepted.Count > 0 && !paths.ContainsKey("--probe-dependents")) return false;
        if (execute != paths.TryGetValue("--expected-document", out var expected)) return false;
        if (execute && (!paths.ContainsKey("--probe-dependents") || expected is null || expected.Length != 64 || !expected.All(char.IsAsciiHexDigit))) return false;
        return Required.All(paths.ContainsKey);
    }
}
