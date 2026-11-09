#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Gltf;
using Bim.Mcp;

namespace Bim.Cli;

// P2-10 `mirror-drift-select store-directory snapshot.json mirror.json choices.json output.json`: the P2-09 selection
// (MirrorDriftSelect.Select over the P2-07 resolution of the P2-04 drift report) of a query.snapshot against the Core
// store, for the reviewer's choices, written to a new JSON file. The engine and the input formats are
// MirrorDriftSelectTool's (interfaces/mcp/MirrorDriftSelectTool.cs), shared with the MCP tool mirror_drift_select, whose
// text equals this file byte for byte; the file is the canonical MirrorDriftSelection.ToJson(). Nothing is applied: the
// store is only read (applied false, requires approval).
//
// Same output and input rules as mirror-drift (MirrorDriftCommand): output checked first (GltfExportCommand.OutputProblem,
// .json, never an existing file, directory or link), never in or below the store (output_in_store), created exclusively;
// the three input files through BoundedInput (detail "snapshot", "mirror" or "choices"; an empty path is
// input_path_invalid). Every refusal prints one {"status":"failed","code":..} envelope, exits 1 and writes nothing.
// Success prints {"status":"succeeded","output","bytes","sha256","operations","restore_steps","applied":false}.
internal static class MirrorDriftSelectCommand
{
    public const string Usage = "usage: mirror-drift-select store-directory snapshot.json mirror.json choices.json output.json";

    public static void Run(string[] args)
    {
        if (args.Length != 6) throw new ArgumentException(Usage);
        var output = args[5];
        if (GltfExportCommand.OutputProblem(output, [".json"], out _) is string refused) { Fail(MirrorDriftTool.Failure(refused, null)); return; }
        if (MirrorDriftCommand.InsideStore(args[1], output)) { Fail(MirrorDriftTool.Failure(MirrorDriftCommand.OutputInStore, null)); return; }
        if (!Read(args[2], "snapshot", out var snapshot) || !Read(args[3], "mirror", out var mirror) || !Read(args[4], "choices", out var choices)) return;
        var outcome = MirrorDriftSelectTool.Run(args[1], snapshot, mirror, choices);
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
            operations = outcome.Operations,
            restore_steps = outcome.RestoreSteps,
            applied = false,
        }));
    }

    static bool Read(string path, string name, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) { Fail(MirrorDriftTool.Failure(MirrorDriftCommand.InputPathInvalid, name)); return false; }
        try { text = BoundedInput.ReadText(path); return true; }
        catch (InputRejectedException e) { Fail(MirrorDriftTool.Failure(e.Message, name)); return false; }
    }

    static void Fail(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}
