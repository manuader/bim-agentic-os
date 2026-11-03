using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// Material and layered type creation (P9-06) Revit executor: material.create, wall_type.create and floor_type.create.
// Same admission, receipts, transaction group, failure handling and readback-before-receipt as v1
// (CommandsExecutor.cs); every decision that needs no Revit object is in CommandsTypes.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Material.Create(Document, string), Material.Color / Transparency / Shininess / Smoothness / MaterialClass /
//   MaterialCategory / AppearanceAssetId, Color(byte, byte, byte), Color.IsValid,
//   FilteredElementCollector.OfClass(typeof(AppearanceAssetElement))
//   WallType.Kind (WallKind), ElementType.Duplicate(string) on a WallType / FloorType,
//   HostObjAttributes.GetCompoundStructure / SetCompoundStructure,
//   CompoundStructure.CreateSimpleCompoundStructure(IList<CompoundStructureLayer>) (layers exterior/top first),
//   SetNumberOfShellLayers(ShellLayerType, int), StructuralMaterialIndex, IsValid(Document, out errors, out pairs)
// Unverified in Revit (owner session required): that a wall type duplicated from a basic wall with sweeps, reveals or
// wrapping keeps those once the simple structure is set; that a floor type duplicated from one with a variable-thickness
// layer keeps no variable layer; that an appearance asset shared by two materials renders the same on both; that the
// shell layer counts and StructuralMaterialIndex survive SetCompoundStructure as set (they are read back); that 1.2 mm
// (RevitTypeRules.MinLayerThicknessMm) is accepted for every layer function; whether type names must be unique across
// system families (a wall type named like a floor type) and whether material names are unique ignoring case (Duplicate
// and Material.Create refusals become args_invalid); and how Material.Color shows when the material's
// UseRenderAppearanceForShading is on (the graphics colour is set and read back, the shading shown is not checked).
internal static partial class CommandExecutor
{
    static AppearanceAssetElement[] AppearanceAssets(Document d) => new FilteredElementCollector(d).OfClass(typeof(AppearanceAssetElement)).Cast<AppearanceAssetElement>().ToArray();

    static ObservedMaterial ObserveMaterial(Document d, Material material)
    {
        var color = material.Color;
        var asset = material.AppearanceAssetId is { } assetId && assetId != ElementId.InvalidElementId ? d.GetElement(assetId) : null;
        return new ObservedMaterial(material.UniqueId, material.Name,
            color is { IsValid: true } ? new RgbColor(color.Red, color.Green, color.Blue) : null,
            material.Transparency, material.Shininess, material.Smoothness, Blank(material.MaterialClass), Blank(material.MaterialCategory),
            asset?.UniqueId, asset?.Name);
    }

    static WritePlan MaterialWrite(Document d, MaterialCreateArgs args)
    {
        var asset = args.AppearanceAsset is { } reference
            ? Resolve(AppearanceAssets(d), element => new CommandCandidate(element.UniqueId, element.Name), reference, "args.appearance_asset", "appearance asset")
            : null;
        var assetUid = asset?.UniqueId;

        // A material of that name (exact name first, then ignoring case: CommandRulesTypes.PickExisting): returned
        // unchanged when identical, else refused.
        var materials = Materials(d);
        if (CommandRulesTypes.PickExisting(materials.Select(material => material.Name).ToArray(), args.Name) is { } index)
        {
            var existing = materials[index];
            if (CommandReadbackTypes.Material(args, assetUid, ObserveMaterial(d, existing)) is { } difference)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a material named '" + existing.Name + "' already exists and differs (" + difference + "); choose another name");
            return new WritePlan(
                () => { },
                () => existing.IsValidObject ? CommandReadbackTypes.Material(args, assetUid, ObserveMaterial(d, existing)) : "the existing material no longer exists",
                () => CommandReadbackTypes.MaterialJson(ObserveMaterial(d, existing), existing: true),
                Array.Empty<string>,
                Array.Empty<string>);
        }

        Material? created = null;
        return new WritePlan(
            () =>
            {
                ElementId id;
                try { id = Material.Create(d, args.Name); }
                catch (Autodesk.Revit.Exceptions.ArgumentException error) { throw CommandRulesModify.RevitRefused("args.name", error.Message); }
                var material = d.GetElement(id) as Material ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.name", "Revit did not create the material");
                created = material;
                if (args.Color is { } color) material.Color = new Color((byte)color.Red, (byte)color.Green, (byte)color.Blue);
                if (args.Transparency is { } transparency) material.Transparency = transparency;
                if (args.Shininess is { } shininess) material.Shininess = shininess;
                if (args.Smoothness is { } smoothness) material.Smoothness = smoothness;
                if (args.MaterialClass is { } materialClass) material.MaterialClass = materialClass;
                if (asset is not null) material.AppearanceAssetId = asset.Id;
            },
            () => created is { IsValidObject: true } material ? CommandReadbackTypes.Material(args, assetUid, ObserveMaterial(d, material)) : "the new material no longer exists",
            () => CommandReadbackTypes.MaterialJson(ObserveMaterial(d, created!), existing: false),
            () => new[] { created!.UniqueId },
            Array.Empty<string>);
    }

    static ObservedLayeredType ObserveLayeredType(Document d, HostObjAttributes type)
    {
        var structure = type.GetCompoundStructure();
        var layers = structure?.GetLayers().Select(layer =>
        {
            var material = d.GetElement(layer.MaterialId) as Material;
            return new ObservedTypeLayer(CommandRulesMaterials.FunctionName(layer.Function.ToString()), material?.UniqueId, material?.Name, Mm(layer.Width));
        }).ToArray();
        return structure is null
            ? new ObservedLayeredType(type.UniqueId, type.Name, null, null, null, null, null)
            : new ObservedLayeredType(type.UniqueId, type.Name, layers, Mm(structure.GetWidth()),
                structure.GetNumberOfShellLayers(ShellLayerType.Exterior), structure.GetNumberOfShellLayers(ShellLayerType.Interior), structure.StructuralMaterialIndex);
    }

    static WritePlan WallTypeWrite(Document d, WallTypeCreateArgs args)
    {
        var wallTypes = WallTypes(d);
        var baseType = Resolve(wallTypes, wallType => new CommandCandidate(wallType.UniqueId, wallType.Name), args.BaseType, "args.base_type", "wall type");
        if (CommandRulesTypes.WallKindProblem(baseType.Kind.ToString()) is { } problem)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.base_type", "wall type '" + baseType.Name + "' " + problem + "; pick a basic wall type from revit_query_wall_types");
        var existing = CommandRulesTypes.PickExisting(wallTypes.Select(wallType => wallType.Name).ToArray(), args.Name) is { } index ? wallTypes[index] : null;
        if (existing is not null && CommandRulesTypes.WallKindProblem(existing.Kind.ToString()) is { } existingProblem)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a wall type named '" + existing.Name + "' already exists and " + existingProblem + "; choose another name");
        return LayeredTypeWrite(d, baseType, existing, args.Name, args.Layers, "wall type", "wall_type");
    }

    static WritePlan FloorTypeWrite(Document d, FloorTypeCreateArgs args)
    {
        var floorTypes = FloorTypes(d);
        var baseType = Resolve(floorTypes, floorType => new CommandCandidate(floorType.UniqueId, floorType.Name), args.BaseType, "args.base_type", "floor type");
        var existing = CommandRulesTypes.PickExisting(floorTypes.Select(floorType => floorType.Name).ToArray(), args.Name) is { } index ? floorTypes[index] : null;
        return LayeredTypeWrite(d, baseType, existing, args.Name, args.Layers, "floor type", "floor_type");
    }

    // Duplicates baseType under name and sets a simple compound structure from layers (exterior or top first), the
    // core being the run of structure layers (RevitTypeRules.CoreRange). An existing type of that name is returned
    // unchanged when identical, else refused.
    static WritePlan LayeredTypeWrite(Document d, HostObjAttributes baseType, HostObjAttributes? existing, string name, IReadOnlyList<TypeLayerArg> requested, string what, string member)
    {
        if (baseType.GetCompoundStructure() is null)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.base_type", what + " '" + baseType.Name + "' has no layers; pick a layered " + what);
        var materials = Materials(d);
        var resolved = requested.Select((layer, i) => Resolve(materials, material => new CommandCandidate(material.UniqueId, material.Name), layer.Material, "args.layers[" + i + "].material", "material")).ToArray();
        var expected = requested.Select((layer, i) => new ExpectedTypeLayer(layer.Function, resolved[i].UniqueId, layer.Thickness.Millimetres)).ToArray();
        var (firstCore, lastCore) = RevitTypeRules.CoreRange(requested.Select(layer => layer.Function).ToArray(), "args.layers");

        if (existing is { } found)
        {
            if (CommandReadbackTypes.LayeredType(what, name, expected, ObserveLayeredType(d, found)) is { } difference)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a " + what + " named '" + found.Name + "' already exists and differs (" + difference + "); choose another name");
            return new WritePlan(
                () => { },
                () => found.IsValidObject ? CommandReadbackTypes.LayeredType(what, name, expected, ObserveLayeredType(d, found)) : "the existing " + what + " no longer exists",
                () => CommandReadbackTypes.LayeredTypeJson(member, ObserveLayeredType(d, found), existing: true),
                Array.Empty<string>,
                Array.Empty<string>);
        }

        HostObjAttributes? created = null;
        return new WritePlan(
            () =>
            {
                ElementType copy;
                try { copy = baseType.Duplicate(name); }
                catch (Autodesk.Revit.Exceptions.ArgumentException error) { throw CommandRulesModify.RevitRefused("args.name", error.Message); }
                var duplicate = copy as HostObjAttributes ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.base_type", "Revit did not duplicate the " + what);
                created = duplicate;
                var layers = new List<CompoundStructureLayer>();
                for (var i = 0; i < requested.Count; i++)
                {
                    var function = Enum.Parse<MaterialFunctionAssignment>(CommandRulesMaterials.RevitFunction(requested[i].Function));
                    layers.Add(new CompoundStructureLayer(U(requested[i].Thickness.Millimetres), function, resolved[i].Id));
                }
                var structure = CompoundStructure.CreateSimpleCompoundStructure(layers);
                // CreateSimpleCompoundStructure puts every layer in the core; the core is the run of structure layers,
                // everything outside it is shell (Revit refuses a membrane in the core).
                structure.SetNumberOfShellLayers(ShellLayerType.Exterior, firstCore);
                structure.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - lastCore);
                structure.StructuralMaterialIndex = firstCore;
                if (!structure.IsValid(d, out var errors, out var pairErrors))
                {
                    var details = errors.Select(error => "layer " + error.Key + ": " + error.Value).Concat(pairErrors.Select(pair => "layers " + pair.Key + " and " + pair.Value)).ToArray();
                    throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.layers", "Revit refused the layer build-up" + (details.Length > 0 ? ": " + string.Join("; ", details) : ""));
                }
                duplicate.SetCompoundStructure(structure);
            },
            () => created is { IsValidObject: true } type ? CommandReadbackTypes.LayeredType(what, name, expected, ObserveLayeredType(d, type)) : "the new " + what + " no longer exists",
            () => CommandReadbackTypes.LayeredTypeJson(member, ObserveLayeredType(d, created!), existing: false),
            () => new[] { created!.UniqueId },
            Array.Empty<string>);
    }
}
