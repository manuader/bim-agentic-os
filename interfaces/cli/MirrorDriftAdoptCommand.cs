#nullable enable
using Bim.Core;
using Bim.Mcp;

namespace Bim.Cli;

// P2-13 `mirror-drift-adopt store-directory selection.json`: commits the adopt operations of a P2-10 selection file
// (what mirror-drift-select writes: the canonical MirrorDriftSelection.ToJson()) into the Core store. P2-15: the engine
// (strict canonical selection parse, store checks, MirrorDriftAdoptCommit.Commit, envelopes) is
// interfaces/mcp/MirrorDriftAdoptTool.cs, shared with the confirm-gated MCP tool mirror_drift_adopt; this verb always
// confirms. This file only reads the selection file and prints the engine's text.
//
// Selection file: read through BoundedInput (8 MiB, regular file, no link; detail "selection"; an empty path is
// input_path_invalid), decoded as strict UTF-8 (an optional UTF-8 BOM is dropped; UTF-16/UTF-32 or invalid UTF-8 is
// drift_adopt_selection_invalid), then MirrorDriftAdoptTool.Run(store, text, confirm: true).
// Success prints {"status":"succeeded","outcome":"committed"|"replayed"|"nothing_to_adopt","revision":n,"key":k|null}
// and exits 0. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope and exits 1.
internal static class MirrorDriftAdoptCommand
{
    public const string Usage = "usage: mirror-drift-adopt store-directory selection.json";
    static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    public static void Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException(Usage);
        if (!ReadSelection(args[2], out var text)) return;
        var outcome = MirrorDriftAdoptTool.Run(args[1], text, confirm: true);
        Console.WriteLine(outcome.Text);
        if (!outcome.Succeeded) Environment.ExitCode = 1;
    }

    // Bounded bytes, then strict UTF-8 (BoundedInput.ReadText would also accept UTF-16/UTF-32 with a BOM).
    static bool ReadSelection(string path, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) { Fail(MirrorDriftTool.Failure(MirrorDriftCommand.InputPathInvalid, "selection")); return false; }
        byte[] bytes;
        try { bytes = BoundedInput.ReadBytes(path, BoundedInput.MaxBytes); }
        catch (InputRejectedException rejected) { Fail(MirrorDriftTool.Failure(rejected.Message, "selection")); return false; }
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { text = StrictUtf8.GetString(bytes, start, bytes.Length - start); return true; }
        catch (System.Text.DecoderFallbackException) { Fail(MirrorDriftTool.Failure(MirrorDriftAdoptCodes.SelectionInvalid, "not UTF-8 (only UTF-8, with or without its BOM, is a selection file)")); return false; }
    }

    static void Fail(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}
