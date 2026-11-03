using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Bim.Core;
namespace Bim.Revit;

// Family commands Revit executor: family.load and family_instance.place. Same admission, receipts, transaction group,
// failure handling and readback-before-receipt as catalogue v1 (CommandsExecutor.cs); every decision that needs no
// Revit object is in CommandsFamilies.cs (unit-tested). Evidence level: compiled against the Revit 2025 reference
// assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Document.LoadFamily(string, IFamilyLoadOptions, out Family) inside the write transaction (NativeRoomFixture.cs
//     loads library families the same way, without options); IFamilyLoadOptions.OnFamilyFound /
//     OnSharedFamilyFound, FamilySource
//   Family.Name / FamilyCategory (Category.Name, Category.BuiltInCategory) / FamilyPlacementType /
//     GetFamilySymbolIds(); FamilySymbol.IsActive / Activate
//   Document.Create.NewFamilyInstance(XYZ, FamilySymbol, Level, StructuralType.NonStructural);
//     BuiltInParameter.INSTANCE_ELEVATION_PARAM (Elevation from Level); LocationPoint.Point / Rotation /
//     Rotate(Line, double); FamilyInstance.LevelId / Host / Symbol
// Unverified in Revit (owner session required): that LoadFamily names the family after the file (checked, rolls back
// otherwise); what LoadFamily returns for an unchanged family reloaded with overwrite (reported as unchanged); that a
// manufacturer family saved in an older Revit version loads (Revit upgrades it in memory; a newer one fails); that
// NewFamilyInstance with a level keeps the requested plan point at the level's project elevation and that
// INSTANCE_ELEVATION_PARAM then moves it up by offset_mm (both read back; without the parameter the location
// elevation is compared instead); and that LocationPoint.Rotation reads back the requested angle after Rotate.
internal static partial class CommandExecutor
{
    static Family[] FamiliesNamed(Document d, string name) => new FilteredElementCollector(d).OfClass(typeof(Family)).Cast<Family>()
        .Where(family => string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();

    static ObservedFamily ObserveFamily(Document d, Family family)
    {
        var symbols = family.GetFamilySymbolIds().Select(id => d.GetElement(id)).OfType<FamilySymbol>()
            .Select(symbol => new FamilySymbolRow(symbol.Name, symbol.UniqueId, symbol.IsActive)).ToArray();
        var category = family.FamilyCategory;
        return new ObservedFamily(family.UniqueId, family.Name, category?.Name, category?.BuiltInCategory.ToString(), family.FamilyPlacementType.ToString(), symbols);
    }

    static long? FileLength(string path)
    {
        try { var info = new FileInfo(path); return info.Exists ? info.Length : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    static WritePlan FamilyLoadWrite(Document d, FamilyLoadArgs args)
    {
        // The client checked the source; the add-in checks again on its own repository policy and reads the file facts.
        CommandRulesFamilies.CheckSource(args.Path, Policy().FamilySourceRoots, RevitFileSystemProbe.Real, FileLength(args.Path), FullPath(args.Path));
        var existing = FamiliesNamed(d, args.FamilyName);
        if (existing.Length > 1) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.path", existing.Length + " families named '" + args.FamilyName + "' (ignoring case) are loaded");
        var existed = existing.Length == 1;
        Family? family = existed ? existing[0] : null;
        var outcome = CommandRulesFamilies.AlreadyLoaded;
        return new WritePlan(
            () =>
            {
                if (!CommandRulesFamilies.CallsLoad(existed, args.Overwrite)) return;
                var returned = d.LoadFamily(args.Path, new CommandFamilyLoadOptions(args.Overwrite), out var loaded);
                family = loaded ?? (existed ? family : FamiliesNamed(d, args.FamilyName).SingleOrDefault());
                outcome = CommandRulesFamilies.LoadOutcome(existed, args.Overwrite, returned, family is { IsValidObject: true });
            },
            () => family is { IsValidObject: true } observed ? CommandReadbackFamilies.Family(args.FamilyName, ObserveFamily(d, observed)) : "the family is not in the document",
            () => CommandReadbackFamilies.FamilyJson(outcome, args.Path, ObserveFamily(d, family!)),
            () => outcome == CommandRulesFamilies.Loaded ? new[] { family!.UniqueId }.Concat(ObserveFamily(d, family!).Symbols.Select(symbol => symbol.UniqueId)).ToArray() : Array.Empty<string>(),
            () => outcome == CommandRulesFamilies.Reloaded ? new[] { family!.UniqueId } : Array.Empty<string>());
    }

    static WritePlan FamilyInstanceWrite(Document d, FamilyInstancePlaceArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var symbol = Resolve(Symbols(d, args.Category), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name, CommandReferences.Qualified(candidate.Family.Name, candidate.Name)),
            args.Symbol, "args.symbol", args.Category.Replace('_', ' ') + " family type");
        CommandRulesFamilies.CheckPlacement(symbol.Family.FamilyPlacementType.ToString());
        var location = At(args.Point, level.ProjectElevation);
        var expectedZ = Mm(level.ProjectElevation) + args.Offset.Millimetres;
        FamilyInstance? instance = null;
        var activated = false;
        ObservedFamilyInstance Observe(FamilyInstance created)
        {
            var point = created.Location as LocationPoint;
            var levelUid = created.LevelId != ElementId.InvalidElementId && d.GetElement(created.LevelId) is Level own ? own.UniqueId : LevelParameterUid(d, created, BuiltInParameter.FAMILY_LEVEL_PARAM);
            return new ObservedFamilyInstance(created.UniqueId, created.Symbol.UniqueId, created.Symbol.Family.Name, created.Symbol.Name, created.Category?.BuiltInCategory.ToString(),
                levelUid, created.Host?.UniqueId, point is null ? null : Plan(point.Point), point is null ? null : Mm(point.Point.Z),
                LengthParameter(created, BuiltInParameter.INSTANCE_ELEVATION_PARAM), point is null ? null : CommandRulesFamilies.NormalizeDegrees(point.Rotation * 180 / Math.PI));
        }
        return new WritePlan(
            () =>
            {
                activated = EnsureActive(d, symbol);
                var created = d.Create.NewFamilyInstance(location, symbol, level, StructuralType.NonStructural);
                instance = created;
                var elevation = created.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                if (elevation is { IsReadOnly: false, StorageType: StorageType.Double })
                {
                    if (!elevation.Set(U(args.Offset.Millimetres))) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.offset_mm", "the elevation from level of this instance cannot be set");
                }
                else if (args.Offset.Millimetres != 0) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.offset_mm", "this family has no writable Elevation from Level; place it with offset_mm 0");
                if (args.Rotation.Radians != 0)
                {
                    var point = created.Location as LocationPoint ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.rotation_deg", "the instance has no location point to rotate");
                    if (!point.Rotate(Line.CreateBound(location, location + XYZ.BasisZ), args.Rotation.Radians))
                        throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.rotation_deg", "Revit did not rotate the instance");
                }
            },
            () => instance is { IsValidObject: true } created
                ? CommandReadbackFamilies.Instance(symbol.UniqueId, level.UniqueId, Requested(args.Point), args.Offset.Millimetres, expectedZ, args.Rotation.Degrees, Observe(created))
                : "the family instance no longer exists",
            () => CommandReadbackFamilies.InstanceJson(Observe(instance!)),
            () => new[] { instance!.UniqueId },
            () => activated ? new[] { symbol.UniqueId } : Array.Empty<string>());
    }
}

// Loads a new family as is; an existing family (or shared nested family) is replaced, with its parameter values,
// only when overwrite was requested. Decisions in CommandRulesFamilies.
sealed class CommandFamilyLoadOptions : IFamilyLoadOptions
{
    readonly bool overwrite;

    public CommandFamilyLoadOptions(bool overwrite) => this.overwrite = overwrite;

    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    {
        var (proceed, values) = CommandRulesFamilies.OnFamilyFound(overwrite);
        overwriteParameterValues = values;
        return proceed;
    }

    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
    {
        var (proceed, useFamily, values) = CommandRulesFamilies.OnSharedFamilyFound(overwrite);
        source = useFamily ? FamilySource.Family : FamilySource.Project;
        overwriteParameterValues = values;
        return proceed;
    }
}
