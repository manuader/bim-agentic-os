#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Gltf;

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
// Never-overwrite: the output path follows GltfExportCommand.OutputProblem with extension .json (never an existing file,
// directory or link; checked before any input is read) and is created exclusively (GltfExporter.WriteNew).
// Success prints {"status":"succeeded","output","entries","bytes","sha256"} (sha256 upper-case hex of the file, as
// doc-update prints it) and exits 0. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope, exits 1 and
// writes no file: doc_realized_usage, output_*, input_*, the library's realized_* codes with their detail
// (DocRealizedCodes), and doc_realized_internal_error (a defect: type and message on stderr).
// Precedence: usage, output path, inputs read (record, fixture, assignments), fixture parse, then the library
// (FromRecord: record, document, assignments, fixture and sources in its own order; then Canonical), then the write.
internal static class DocRealizedCommand
{
    public const string Usage = "usage: doc-realized --record run.json --fixture ir.json --assignments assignments.json --document revit-unique-id --output realized.json (--output: a new file, never overwritten)";
    public const string UsageCode = "doc_realized_usage";
    public const string InternalError = "doc_realized_internal_error";

    static readonly string[] Flags = { "--record", "--fixture", "--assignments", "--document", "--output" };

    sealed class Refusal(string code, string? detail = null) : Exception(code)
    {
        public string Code { get; } = code;
        public string? Detail { get; } = detail;
    }

    public static void Run(string[] args)
    {
        try { Execute(args); }
        catch (Refusal refusal) { Fail(refusal.Code, refusal.Detail); }
    }

    static void Execute(string[] args)
    {
        if (!TryParse(args, out var values)) throw new Refusal(UsageCode);
        var output = values["--output"];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string badOutput) throw new Refusal(badOutput);
        var recordText = Read(values["--record"], "record", DocRealized.MaxRecordBytes);
        var fixtureText = Read(values["--fixture"], "fixture", BoundedInput.MaxBytes);
        var assignmentsText = Read(values["--assignments"], "assignments", DocRealized.MaxRecordBytes);
        var fixture = ParseFixture(fixtureText);

        IReadOnlyDictionary<string, string> realized;
        string json;
        try
        {
            realized = DocRealized.FromRecord(fixture, recordText, assignmentsText, values["--document"]);
            json = DocRealized.Canonical(realized);
        }
        catch (DocRealizedException error) { throw new Refusal(error.Code, error.Detail); }
        catch (Exception error)
        {
            Console.Error.WriteLine("doc-realized internal error: " + error.GetType().FullName + ": " + error.Message);
            throw new Refusal(InternalError);
        }

        var bytes = new UTF8Encoding(false).GetBytes(json);
        var line = JsonSerializer.Serialize(new
        {
            status = "succeeded",
            output,
            entries = realized.Count,
            bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        });
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException error) { throw new Refusal(error.Code); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new Refusal("output_io_error", error.GetType().Name); }
        Console.WriteLine(line);
    }

    // The five flags, each exactly once, in any order, each with a non-empty value that is not flag-shaped ("--...").
    static bool TryParse(string[] args, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (args.Length != 1 + 2 * Flags.Length) return false;
        for (var i = 1; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (!Flags.Contains(args[i], StringComparer.Ordinal) || !values.TryAdd(args[i], value)) return false;
        }
        return Flags.All(values.ContainsKey);
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

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
