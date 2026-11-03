using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// wall.set_profile: replace the elevation profile of an existing straight wall (gable end walls under a gable roof;
// the owner's Revit 2025.2 has no Wall.AddAttachment, so attaching the wall top is not an API route). Same envelope
// (protocol_version 1), schema subset, unit rules, copies-only guard and receipts as the earlier catalogue parts,
// which are unchanged. Vendor-free like every RevitCommands*.cs file; the add-in executor
// (CommandsExecutorWallProfile.cs) consumes these types.

// A profile vertex in the wall's vertical plane, in millimetres: U is the distance along the wall from its
// location-line start, Z the height above the wall's base level elevation (not above its base offset).
public readonly record struct ProfileVertex(Length U, Length Z);
// The new closed elevation profile (implicit closing edge, not self-intersecting) of the wall WallUid.
public sealed record WallSetProfileArgs(string WallUid, IReadOnlyList<ProfileVertex> Profile) : RevitCommandArgs;

public static class RevitWallProfileRules
{
    public const int MinVertices = 3;
    public const int MaxVertices = 200;
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> WallProfileSpecs = new(BuildWallProfile);

    public static IReadOnlyList<RevitCommandSpec> WallProfile => WallProfileSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildWallProfile()
    {
        const string Vertex = """{"type":"array","description":"[u, z] in millimetres: u along the wall from its location-line start, z above the wall's base level","items":{"type":"number"},"minItems":2,"maxItems":2}""";
        var schema = Fill("""{"type":"object","properties":{"wall_uid":@0@,"profile":{"type":"array","description":"Closed elevation profile as [u, z] millimetre vertices, @1@ to @2@; the closing edge is implicit; must not self-intersect; u must span exactly 0 to the wall's length (the location line is kept)","items":@3@,"minItems":@1@,"maxItems":@2@}},"required":["wall_uid","profile"],"additionalProperties":false}""",
            UniqueIdSchema, RevitWallProfileRules.MinVertices.ToString(CultureInfo.InvariantCulture), RevitWallProfileRules.MaxVertices.ToString(CultureInfo.InvariantCulture), Vertex);
        return new[]
        {
            new RevitCommandSpec("wall.set_profile", true,
                "Replace the elevation profile of an existing straight wall (for example a gable end wall under a gable roof: 6000 long, eaves 2700, ridge 5200 at the middle is [[0,0],[6000,0],[6000,2700],[3000,5200],[0,2700]]) "
                + "with a closed polygon of 3 to 200 [u, z] millimetre vertices. u is the distance along the wall from its location-line start and must span 0 to the wall's length; "
                + "z is the height above the wall's base level elevation, not above its base offset (a wall with base offset 300 whose bottom should stay put needs its bottom edge at z 300; Revit may then report Base Offset and Unconnected Height from the new profile). "
                + "Only straight basic walls; curved, curtain and stacked walls are refused, and so is a profile that would leave a hosted door, window or opening partly outside it. Allowed only on enabled test copies.",
                schema, BindWallSetProfile),
        };
    }

    static RevitCommandArgs BindWallSetProfile(JsonElement args)
    {
        var vertices = new List<(double X, double Y)>();
        var index = 0;
        foreach (var vertex in args.GetProperty("profile").EnumerateArray())
        {
            var field = "args.profile[" + index + "]";
            var (u, z) = Coordinates(vertex, field);
            if (Math.Abs(u) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field + "[0]", "u beyond 9144000 mm (Revit's 30000 ft limit)");
            if (Math.Abs(z) > RevitCommandGeometry.MaxRevitLengthMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field + "[1]", "z beyond 9144000 mm (Revit's 30000 ft limit)");
            vertices.Add((u, z));
            index++;
        }
        var profile = RevitCommandGeometry.CheckBoundary(vertices, "args.profile");
        return new WallSetProfileArgs(args.GetProperty("wall_uid").GetString()!, profile.Select(v => new ProfileVertex(Length.FromMillimetres(v.X), Length.FromMillimetres(v.Y))).ToArray());
    }
}
