using System.Text.Json;
namespace Bim.Core;

// wall.attach: attach the top (or base) of an existing wall to a roof, floor, ceiling or toposolid, for example the
// gable end walls of a cabin reaching its sloped roof. Same envelope (protocol_version 1), schema subset, copies-only
// guard and operation_id receipts as catalogue v1; earlier commands are unchanged. Vendor-free like every
// RevitCommands*.cs file; the add-in executor (CommandsExecutorWallAttach.cs) consumes these types.

// Attach the wall WallUid at Location (RevitWallAttachRules.Locations) to the element TargetUid.
public sealed record WallAttachArgs(string WallUid, string TargetUid, string Location) : RevitCommandArgs;

public static class RevitWallAttachRules
{
    public const string Top = "top";
    public const string Base = "base";
    public static readonly IReadOnlyList<string> Locations = new[] { Top, Base };
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> WallAttachSpecs = new(BuildWallAttach);

    public static IReadOnlyList<RevitCommandSpec> WallAttach => WallAttachSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildWallAttach()
    {
        var schema = Fill("""{"type":"object","properties":{"wall_uid":@0@,"target_uid":@1@,"location":{"type":"string","description":"Which end of the wall to attach: top (default, for example a gable wall up to a sloped roof) or base (for example down to a floor or toposolid)","enum":["top","base"]}},"required":["wall_uid","target_uid"],"additionalProperties":false}""",
            UniqueIdSchema.Replace("Revit element UniqueId, as returned by a query", "UniqueId of the wall to attach (from revit_query_elements or a wall.create result)", StringComparison.Ordinal),
            UniqueIdSchema.Replace("Revit element UniqueId, as returned by a query", "UniqueId of the roof, floor, ceiling or toposolid to attach to", StringComparison.Ordinal));
        return new[]
        {
            new RevitCommandSpec("wall.attach", true, "Attach the top (default) or base of an existing wall to a roof, floor, ceiling or toposolid so it follows that element, for example the gable end walls of a cabin reaching the sloped roof; a wall already attached there to that element is left unchanged. Needs Revit 2025.3 or later. Allowed only on enabled test copies.", schema, BindWallAttach),
        };
    }

    static RevitCommandArgs BindWallAttach(JsonElement args)
    {
        var wall = args.GetProperty("wall_uid").GetString()!;
        var target = args.GetProperty("target_uid").GetString()!;
        if (string.Equals(wall, target, StringComparison.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.target_uid", "a wall cannot be attached to itself");
        var location = args.TryGetProperty("location", out var value) ? value.GetString()! : RevitWallAttachRules.Top;
        return new WallAttachArgs(wall, target, location);
    }
}
