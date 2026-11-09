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
// through BoundedInput (8 MiB, regular files only; refusals carry detail "snapshot" or "mirror"; an empty path is
// input_path_invalid). The output may not be in or below the store directory (output_in_store). Every refusal prints
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
        if (InsideStore(args[1], output)) { Fail(MirrorDriftTool.Failure(OutputInStore, null)); return; }
        if (!Read(args[2], "snapshot", out var snapshot) || !Read(args[3], "mirror", out var mirror)) return;
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

    // An output in the store directory (or below it) would sit next to project.json, where a later CloudGuard check
    // could take it for a conflict copy (project-drift.json); the store is never written.
    public const string OutputInStore = "output_in_store";
    public const string InputPathInvalid = "input_path_invalid";

    internal static bool InsideStore(string store, string output)
    {
        if (string.IsNullOrWhiteSpace(store) || store.IndexOf('\0') >= 0) return false;
        string storeFull, parent;
        try
        {
            storeFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(store));
            parent = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(output)) ?? "");
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        // Case-insensitive everywhere: refusing a differently cased sibling on a case-sensitive volume is harmless.
        return string.Equals(parent, storeFull, StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith(storeFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || parent.StartsWith(storeFull + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static bool Read(string path, string name, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) { Fail(MirrorDriftTool.Failure(InputPathInvalid, name)); return false; }
        try { text = BoundedInput.ReadText(path); return true; }
        catch (InputRejectedException e) { Fail(MirrorDriftTool.Failure(e.Message, name)); return false; }
    }

    static void Fail(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}
