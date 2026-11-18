#nullable enable
using System.Text;
using System.Text.Json;
using Bim.Core;

namespace Bim.Cli;

// P4-15 `doc-realized --record run.json --fixture ir.json --assignments a.json --document <UniqueId> --output realized.json`:
// the P4-13 realized map (core/Bim.Core/DocRealized.cs) of an owner -Catalogue run record, written to a new file that
// `doc-update --realized` takes. Offline: no transport, no pipe, no Revit, no store.
//   record        the run record (schema_version 1, document_unique_id, steps[]), read by DocRealized.Read.
//   fixture       the BIM-IR Project the catalogue built, read like doc-update's before/after (default System.Text.Json;
//                 not JSON or not a Project is realized_fixture_invalid "json"; null elements, geometry or dependencies
//                 are realized_fixture_invalid "fixture"); DocRealized.Build validates it further (DocDefaults.From).
//   assignments   {"doc:<key>"|"element:<id D>": "<step key>"}, read by DocRealized.Assignments.
//   document      the UniqueId of the Revit document the map is for; must be the record's document_unique_id
//                 (ignoring case), else realized_document_mismatch.
// Output: the file holds exactly UTF-8(DocRealized.Canonical(DocRealized.FromRecord(fixture, record, assignments,
// document))) (compact, sorted by key, no BOM, no trailing newline): byte-identical to the in-process builder.
// Bounded input: every file goes through BoundedInput (regular files only, links refused; input_* codes with detail
// "record", "fixture" or "assignments"). The fixture keeps the CLI cap (BoundedInput.MaxBytes, 8 MiB); record and
// assignments are read up to the library's own bound DocRealized.MaxRecordBytes (32 MiB) and decoded exactly as
// BoundedInput.ReadText decodes (UTF-8, byte order marks detected and stripped), so the CLI accepts every record the
// in-process builder accepts. A file over that bound is refused at read time as input_too_large (detail the input): the
// read cap equals the library cap, which would refuse the same text as realized_bound_exceeded. The library's other
// bounds (MaxSteps, MaxCreated, MaxAssignments, MaxCanonicalBytes) are realized_bound_exceeded.
// Never-overwrite: the output path follows the gltf-export OutputProblem rule with extension .json (never an existing
// file, directory or link; checked before any input is read) and is created exclusively by GltfExporter.WriteNew(bytes,
// output) in interfaces/cli/DocRealizedWrite.cs.
// Success prints {"status":"succeeded","output","entries","bytes","sha256"} (sha256 upper-case hex of the file, as
// doc-update prints it) and exits 0. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope, exits 1 and
// writes no file: doc_realized_usage, output_*, input_*, the library's realized_* codes with their detail
// (DocRealizedCodes), and doc_realized_internal_error (a defect: type and message on stderr).
// Precedence: usage, output path, inputs read (record, fixture, assignments), fixture parse, then the library
// (FromRecord: record, document, assignments, fixture and sources in its own order; then Canonical), then the write.
// P4-21: everything after the output path is Build, shared with DryRealized, the read-only MCP tool doc_realized's entry.
// P4-23: this file is the shared, read-only part (Build, DryRealized, the readers, the refusal envelope); the verb's
// argument parsing, output path, exclusive write and stdout/exit code live in DocRealizedWrite.cs (the other part of this
// partial class), which the CLI compiles and the MCP server does not.
internal static partial class DocRealizedCommand
{
    public const string InternalError = "doc_realized_internal_error";

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    // The verb's work after the output path: the inputs read (record, fixture, assignments), the fixture parsed, then the
    // guarded library build (FromRecord, Canonical); the first failure throws its Refusal. The verb and DryRealized both
    // build through here.
    static (IReadOnlyDictionary<string, string> Realized, string Json) Build(string recordPath, string fixturePath, string assignmentsPath, string document)
    {
        var recordText = Read(recordPath, "record", DocRealized.MaxRecordBytes);
        var fixtureText = Read(fixturePath, "fixture", BoundedInput.MaxBytes);
        var assignmentsText = Read(assignmentsPath, "assignments", DocRealized.MaxRecordBytes);
        var fixture = ParseFixture(fixtureText);
        try
        {
            GuardedWorkProbe?.Invoke();
            var realized = DocRealized.FromRecord(fixture, recordText, assignmentsText, document);
            return (realized, DocRealized.Canonical(realized));
        }
        catch (DocRealizedException error) { throw new Refusal(error.Code, error.Detail); }
        catch (Exception error) { throw InternalRefusal(error); }
    }

    // P4-21: the map of the read-only MCP tool doc_realized (interfaces/mcp/DocRealizedTool.cs), without an output path or
    // any write. Succeeded: Text is DocRealized.Canonical(DocRealized.FromRecord(...)), whose UTF-8 bytes are exactly the
    // file the verb writes for the same inputs. Otherwise Text is the verb's refusal line without its newline (input_*,
    // realized_fixture_invalid, the library's realized_* codes with their detail, doc_realized_internal_error with the
    // IntentPlan diagnostic on stderr), in the verb's precedence after the output path. Writes nothing to Console.Out and
    // does not touch Environment.ExitCode.
    internal static (bool Succeeded, string Text) DryRealized(string recordPath, string fixturePath, string assignmentsPath, string document)
    {
        try { return (true, Build(recordPath, fixturePath, assignmentsPath, document).Json); }
        catch (Refusal refusal) { return (false, Line(refusal.Code, refusal.Detail)); }
    }

    // BoundedInput.ReadBytes with the given cap, decoded exactly as BoundedInput.ReadText (UTF-8, BOMs detected and stripped).
    static string Read(string path, string name, int maxBytes)
    {
        if (path.IndexOf('\0') >= 0) throw new Refusal("input_path_invalid", name);
        byte[] bytes;
        try { bytes = BoundedInput.ReadBytes(path, maxBytes); }
        catch (InputRejectedException e) { throw new Refusal(e.Message, name); }
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    static Project ParseFixture(string text)
    {
        Project? project;
        try { project = JsonSerializer.Deserialize<Project>(text); }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) { throw new Refusal(DocRealizedCodes.FixtureInvalid, "json"); }
        if (project is null) throw new Refusal(DocRealizedCodes.FixtureInvalid, "json");
        if (project.Elements is null || project.Elements.Any(e => e is null || e.GeometryMetres is null || e.Dependencies is null))
            throw new Refusal(DocRealizedCodes.FixtureInvalid, "fixture");
        return project;
    }

    // P3-20 test seam: null in production. tests/cli/InternalErrorDelegationTests.cs sets it (by reflection) to an Action
    // that throws, so the guarded library work below fails inside Run's real catch-all; it is reset after the test.
    internal static Action? GuardedWorkProbe { get; set; }

    // P3-19: the catch-all's internal error: IntentPlan's stderr detail, then the Refusal Run prints as
    // {"status":"failed","code":InternalError}, exit 1 (IntentPlan.Internal's bytes).
    static Refusal InternalRefusal(Exception error)
    {
        Console.Error.WriteLine(IntentPlan.InternalDiagnostic("doc-realized", error));
        return new Refusal(InternalError);
    }

    // The refusal envelope {"status":"failed","code"[,"detail"]} (the verb's Fail prints it; DryRealized returns it).
    static string Line(string code, string? detail) =>
        detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail });
}
