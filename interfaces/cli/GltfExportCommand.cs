#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Gltf;

namespace Bim.Cli;

// P8-07 `export-gltf project.json output.glb|output.gltf`: BIM-IR (bounded input, BoundedInput) to a new glTF 2.0
// file. The output path is bounded too: extension .glb or .gltf, an existing parent directory that is not a link, no
// link at the path itself, no Windows device namespace, no ':' after a drive designator (NTFS alternate data streams),
// and never an existing file or directory (the exporter creates
// it exclusively). Every refusal prints one JSON envelope and writes nothing. Contract: adapters/gltf/README.md.
// The output-path rule (OutputProblem) is the other part of this class, in OutputProblem.cs (P4-24): the MCP server
// links that part only, never this verb.
internal static partial class GltfExportCommand
{
    public const string Usage = "usage: export-gltf project.json output.glb|output.gltf";

    public static void Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException(Usage);
        // Output is checked before the input is read, so a bad path costs no parse.
        if (OutputProblem(args[2], out var format) is string refused) { Fail(refused, null); return; }
        var text = BoundedInput.ReadText(args[1]);
        Project? project;
        try { project = JsonSerializer.Deserialize<Project>(text); }
        catch (JsonException) { Fail("bim_ir_json_invalid", null); return; }
        byte[] written;
        try { written = format == "glb" ? GltfExporter.ExportGlb(project!) : new UTF8Encoding(false).GetBytes(GltfExporter.ExportGltf(project!)); }
        catch (GltfExportException e) { Fail(e.Code, e.Detail); return; }
        try { GltfExporter.WriteNew(written, args[2]); }
        catch (GltfExportException e) { Fail(e.Code, null); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail("output_io_error", e.GetType().Name); return; }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            format,
            output = args[2],
            bytes = written.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(written)),
            elements = project!.Elements.Length,
            openings = GltfExporter.OpeningCount(project),
        }));
    }

    static void Fail(string code, string? detail)
    {
        Console.WriteLine(detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail }));
        Environment.ExitCode = 1;
    }
}
