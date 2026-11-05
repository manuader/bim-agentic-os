using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P1-09 element.move and element.copy: translate 1 to 100 existing elements (by UniqueId) by one [dx, dy, dz]
// millimetre vector in project coordinates, moving them or copying them. Same envelope (protocol_version 1), schema
// subset, unit rules, copies-only guard and operation_id receipts as the earlier catalogue parts, which are unchanged.
// Both are command.batch steps. Vendor-free like every RevitCommands*.cs file; the add-in executor
// (CommandsExecutorTransform.cs) consumes these types and refuses, with the typed codes below, elements that are
// pinned, view-specific, group members, hosted without their host, or without a location point, curve or box.

// A translation in millimetres; validated finite, at least 1.2 mm and at most 1 km long.
public sealed record TranslationVector(Length Dx, Length Dy, Length Dz)
{
    public double MagnitudeMm => Math.Sqrt(Dx.Millimetres * Dx.Millimetres + Dy.Millimetres * Dy.Millimetres + Dz.Millimetres * Dz.Millimetres);
}

public sealed record ElementMoveArgs(IReadOnlyList<string> ElementUids, TranslationVector Vector) : RevitCommandArgs;
public sealed record ElementCopyArgs(IReadOnlyList<string> ElementUids, TranslationVector Vector) : RevitCommandArgs;

public static class RevitTransformRules
{
    public const string Move = "element.move";
    public const string Copy = "element.copy";
    public const int MinTargets = 1;
    public const int MaxTargets = 100;
    // Revit's short-curve tolerance: a shorter move could not be told apart from no move by a 1 mm readback.
    public const double MinMagnitudeMm = RevitCommandGeometry.MinSegmentMm;
    // One kilometre: anything longer is treated as a units mistake (a building move, not a site relocation).
    public const double MaxMagnitudeMm = 1_000_000;
}

// Refusals the add-in reports for a target it will not transform (Field is args.element_uids[i]; Detail names the
// UniqueId). Executor-produced: the client cannot see these Revit facts.
public static class RevitTransformCodes
{
    public const string ElementPinned = "element_pinned";
    public const string ElementViewSpecific = "element_view_specific";
    public const string ElementInGroup = "element_in_group";
    public const string HostMissing = "host_missing";
    public const string LocationUnsupported = "location_unsupported";
    public static IReadOnlyList<string> All { get; } = new[] { ElementPinned, ElementViewSpecific, ElementInGroup, HostMissing, LocationUnsupported };
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> TransformSpecs = new(BuildTransform);

    public static IReadOnlyList<RevitCommandSpec> Transform => TransformSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildTransform()
    {
        string Schema(string verb) => Fill("""{"type":"object","properties":{"element_uids":{"type":"array","description":"Elements to @0@, by UniqueId (from revit_query_elements), distinct, @1@ to @2@","items":@3@,"minItems":@1@,"maxItems":@2@},"vector_mm":{"type":"array","description":"Translation [dx, dy, dz] in millimetres in project coordinates (x, y in plan, z up); at least 1.2 mm and at most 1000000 mm long","items":{"type":"number"},"minItems":3,"maxItems":3}},"required":["element_uids","vector_mm"],"additionalProperties":false}""",
            verb, RevitTransformRules.MinTargets.ToString(CultureInfo.InvariantCulture), RevitTransformRules.MaxTargets.ToString(CultureInfo.InvariantCulture), UniqueIdSchema);
        const string Refusals = "pinned, view-specific (annotation, detail) and group member elements, hosted elements whose host is missing and elements without a location are refused with typed codes";
        return new[]
        {
            new RevitCommandSpec(RevitTransformRules.Move, true,
                "Move 1 to 100 elements (by UniqueId) by one [dx, dy, dz] vector in millimetres, like Revit's Move; each element's location point, curve or bounding box is read back and must have shifted by the vector within 1 mm, else everything is rolled back. "
                + "Hosted elements (doors, windows) move with their host and only along it. Example: move a wall 1 m east is element_uids [wall], vector_mm [1000, 0, 0]. "
                + "The " + Refusals + ". Allowed only on enabled test copies.",
                Schema("move"), BindMove),
            new RevitCommandSpec(RevitTransformRules.Copy, true,
                "Copy 1 to 100 elements (by UniqueId) by one [dx, dy, dz] vector in millimetres, like Revit's Copy; the reply maps each source UniqueId to its new copy (readback copies[].source_unique_id / copy_unique_id) and lists any extra element Revit copied with them (hosted doors and windows) in also_created; each copy must sit at its source's location plus the vector within 1 mm and the sources must not move. "
                + "Example: a second storey of columns is element_uids [columns], vector_mm [0, 0, 3000]. "
                + "The " + Refusals + ". Allowed only on enabled test copies.",
                Schema("copy"), BindCopy),
        };
    }

    static RevitCommandArgs BindMove(JsonElement args)
    {
        var (uids, vector) = BindTransform(args);
        return new ElementMoveArgs(uids, vector);
    }

    static RevitCommandArgs BindCopy(JsonElement args)
    {
        var (uids, vector) = BindTransform(args);
        return new ElementCopyArgs(uids, vector);
    }

    // Distinct UniqueIds (case-insensitive, as Document.GetElement(string)) and a finite vector from 1.2 mm to 1 km.
    static (string[] Uids, TranslationVector Vector) BindTransform(JsonElement args)
    {
        var uids = DistinctUniqueIds(args.GetProperty("element_uids"), "args.element_uids", null);
        var element = args.GetProperty("vector_mm");
        var components = new double[3];
        for (var i = 0; i < 3; i++)
        {
            var field = "args.vector_mm[" + i.ToString(CultureInfo.InvariantCulture) + "]";
            components[i] = Number(element[i]);
            RevitUnits.Millimetres(components[i], field, allowNegative: true, allowZero: true);
            if (Math.Abs(components[i]) > RevitTransformRules.MaxMagnitudeMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, field, "component beyond 1000000 mm (1 km)");
        }
        // Magnitude from the request's own numbers, not the Length round trip.
        var magnitude = Math.Sqrt(components[0] * components[0] + components[1] * components[1] + components[2] * components[2]);
        if (magnitude == 0) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.vector_mm", "zero vector: nothing to move or copy");
        if (!(magnitude >= RevitTransformRules.MinMagnitudeMm)) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.vector_mm", "vector shorter than 1.2 mm (" + magnitude.ToString("0.###", CultureInfo.InvariantCulture) + " mm)");
        if (magnitude > RevitTransformRules.MaxMagnitudeMm) throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.vector_mm", "vector longer than 1000000 mm (1 km): " + magnitude.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
        var vector = new TranslationVector(Length.FromMillimetres(components[0]), Length.FromMillimetres(components[1]), Length.FromMillimetres(components[2]));
        return (uids, vector);
    }
}
