#nullable enable
using Bim.Core;

namespace Bim.Mcp;

// P2-10 (red stub): the engine behind the CLI verb mirror-drift-select and the MCP tool mirror_drift_select.
internal static class MirrorDriftSelectTool
{
    public const string RequestInvalid = "drift_select_request_invalid";
    public const string ChoicesTooLarge = "drift_select_choices_too_large";
    public const int MaxChoices = MirrorDriftOptions.MaxFindingsLimit;

    public sealed record Outcome(bool Succeeded, string Text, int Operations, int RestoreSteps);

    public static Outcome Run(string storeDirectory, string snapshotJson, string mirrorJson, string choicesJson) =>
        new(false, MirrorDriftTool.Failure("drift_select_not_implemented", null), 0, 0);
}
