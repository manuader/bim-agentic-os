#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Gltf;
using Bim.Mcp;

namespace Bim.Cli;

// P2-08 `mirror-drift store-directory snapshot.json mirror.json output.json`: the P2-04 drift report and the P2-07
// resolution (proposals + restore plan) of a query.snapshot against the Core store, written to a new JSON file. The
// engine and both input formats are MirrorDriftTool's (interfaces/mcp/MirrorDriftTool.cs), shared with the MCP tool
// mirror_drift, whose text equals this file byte for byte. Nothing is applied: the store is only read.
//
// The output path follows GltfExportCommand.OutputProblem with extension .json (never an existing file, directory or
// link) and is checked before any input is read; it is created exclusively (GltfExporter.WriteNew). Both input files go
// through BoundedInput (8 MiB, regular files only; refusals carry detail "snapshot" or "mirror"). Every refusal prints
// one {"status":"failed","code":..[,"detail":..]} envelope, exits 1 and writes nothing. Success prints
// {"status":"succeeded","output","bytes","sha256","drift","proposals","applied":false}.
internal static class MirrorDriftCommand
{
    public const string Usage = "usage: mirror-drift store-directory snapshot.json mirror.json output.json";

    public static void Run(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException(Usage);
        var output = args[4];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string refused) { Fail(MirrorDriftTool.Failure(refused, null)); return; }
        string snapshot, mirror;
        try { snapshot = BoundedInput.ReadText(args[2]); }
        catch (InputRejectedException e) { Fail(MirrorDriftTool.Failure(e.Message, "snapshot")); return; }
        try { mirror = BoundedInput.ReadText(args[3]); }
        catch (InputRejectedException e) { Fail(MirrorDriftTool.Failure(e.Message, "mirror")); return; }
        var outcome = MirrorDriftTool.Run(args[1], snapshot, mirror);
        if (!outcome.Succeeded) { Fail(outcome.Text); return; }
        var bytes = new UTF8Encoding(false).GetBytes(outcome.Text);
        try { GltfExporter.WriteNew(bytes, output); }
        catch (GltfExportException e) { Fail(MirrorDriftTool.Failure(e.Code, null)); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(MirrorDriftTool.Failure("output_io_error", e.GetType().Name)); return; }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            output,
            bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            drift = outcome.Drift,
            proposals = outcome.Proposals,
            applied = false,
        }));
    }

    static void Fail(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}
