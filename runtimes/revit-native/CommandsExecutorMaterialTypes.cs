using Autodesk.Revit.DB;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// Materials, layered wall/floor types and type changes (chat-materials-types) Revit executor: query.fill_patterns,
// material.create, material.set, wall_type.create, floor_type.create and element.change_type. Same admission, receipts,
// transaction group, failure handling and readback-before-receipt as v1 (CommandsExecutor.cs, WritePlan); every
// decision that needs no Revit object is in CommandsMaterialTypes.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   FilteredElementCollector.OfClass(typeof(FillPatternElement)), FillPatternElement.GetFillPattern() (FillPattern.Target,
//     IsSolidFill)
//   Material.Create(Document, string) (returns the new ElementId), Material.Duplicate(string) (copies the material;
//     its AppearanceAssetId is kept), Material.Color / Transparency (0-100) / Shininess (0-128) / Smoothness (0-100) /
//     MaterialClass / MaterialCategory / UseRenderAppearanceForShading / SurfaceForegroundPatternId /
//     CutForegroundPatternId / AppearanceAssetId, AppearanceAssetElement (Name, UniqueId)
//   ElementType.Duplicate(string) on a WallType (WallKind.Basic) or FloorType, CompoundStructure.CreateSimpleCompoundStructure,
//     SetNumberOfShellLayers(ShellLayerType, int), GetNumberOfShellLayers, StructuralMaterialIndex,
//     IsValid(Document, out, out), EndCap (EndCapCondition) / OpeningWrapping (OpeningWrappingCondition),
//     HostObjAttributes.SetCompoundStructure
//   Element.GetValidTypes(), Element.ChangeTypeId(ElementId), FamilySymbol.IsActive / Activate()
// Unverified in Revit (owner session required): that Material.Duplicate keeps the base material's render appearance
//   asset (it is reported, not checked) and whether that asset is shared with the base material (editing it would then
//   change both; nothing here edits assets); that setting Material.Color with UseRenderAppearanceForShading false is what
//   shaded views show (Realistic views and renders keep the appearance asset, so a duplicated light "Metal Deck" stays
//   light there); that CreateSimpleCompoundStructure plus the shell counts gives a valid wall and floor structure for
//   every build-up (IsValid is checked first, a refusal is transaction_failed); that ChangeTypeId keeps hosted doors and
//   windows and wall joins when the new wall type is thicker; that GetValidTypes lists every same-category type.
internal static partial class CommandExecutor
{
    static FillPatternElement[] FillPatterns(Document d) => new FilteredElementCollector(d).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().ToArray();

    static string TargetOf(FillPatternElement pattern) => CommandRulesMaterialTypes.PatternTarget(pattern.GetFillPattern().Target.ToString());

    static string QueryFillPatterns(Document d, FillPatternQueryArgs args) =>
        CommandReadbackMaterialTypes.FillPatternsJson(FillPatterns(d).Select(pattern => new FillPatternRow(pattern.Name, pattern.UniqueId, TargetOf(pattern), pattern.GetFillPattern().IsSolidFill)), args.Target);

    static FillPatternElement? ResolvePattern(FillPatternElement[] patterns, string? reference, string field) => reference is null ? null :
        Resolve(patterns, pattern => new CommandCandidate(pattern.UniqueId, pattern.Name, CommandRulesMaterialTypes.PatternName(TargetOf(pattern), pattern.Name)), reference, field, "fill pattern");

    static PatternRef? PatternOf(Document d, ElementId id) =>
        id == ElementId.InvalidElementId || d.GetElement(id) is not FillPatternElement pattern ? null : new PatternRef(pattern.UniqueId, pattern.Name, TargetOf(pattern));

    static ObservedMaterial ObserveMaterial(Document d, Material material)
    {
        var color = material.Color is { IsValid: true } c ? new RgbColor(c.Red, c.Green, c.Blue) : (RgbColor?)null;
        var asset = material.AppearanceAssetId == ElementId.InvalidElementId ? null : d.GetElement(material.AppearanceAssetId) as AppearanceAssetElement;
        return new ObservedMaterial(material.UniqueId, material.Name, color, material.Transparency, material.Shininess, material.Smoothness, Blank(material.MaterialClass), Blank(material.MaterialCategory),
            material.UseRenderAppearanceForShading, PatternOf(d, material.SurfaceForegroundPatternId), PatternOf(d, material.CutForegroundPatternId), asset?.UniqueId, asset?.Name);
    }

    // Sets only the requested members. A color is the shaded color, so shading stops following the render appearance.
    static void ApplyMaterial(Material material, RgbColor? color, int? transparency, int? shininess, int? smoothness, string? materialClass, FillPatternElement? surface, FillPatternElement? cut)
    {
        if (color is { } rgb)
        {
            material.Color = new Color(rgb.Red, rgb.Green, rgb.Blue);
            material.UseRenderAppearanceForShading = false;
        }
        if (transparency is { } t) material.Transparency = t;
        if (shininess is { } s) material.Shininess = s;
        if (smoothness is { } m) material.Smoothness = m;
        if (materialClass is not null) material.MaterialClass = materialClass;
        if (surface is not null) material.SurfaceForegroundPatternId = surface.Id;
        if (cut is not null) material.CutForegroundPatternId = cut.Id;
    }

    static WritePlan MaterialCreateWrite(Document d, MaterialCreateArgs args)
    {
        var materials = Materials(d);
        var baseMaterial = args.BaseMaterial is null ? null : Resolve(materials, material => new CommandCandidate(material.UniqueId, material.Name), args.BaseMaterial, "args.base_material", "material");
        var patterns = FillPatterns(d);
        var surface = ResolvePattern(patterns, args.SurfacePattern, "args.surface_pattern");
        var cut = ResolvePattern(patterns, args.CutPattern, "args.cut_pattern");
        var expected = new ExpectedMaterial(args.Name, args.Color, args.Transparency, args.Shininess, args.Smoothness, args.MaterialClass, surface?.UniqueId, cut?.UniqueId);

        // A material of that name (Revit names ignore case): returned unchanged when it matches, else refused.
        if (materials.FirstOrDefault(material => string.Equals(material.Name, args.Name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            if (CommandReadbackMaterialTypes.Material(expected, ObserveMaterial(d, existing)) is { } difference)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a material named '" + existing.Name + "' already exists and differs (" + difference + "); choose another name or change it with material.set");
            return new WritePlan(
                () => { },
                () => existing.IsValidObject ? CommandReadbackMaterialTypes.Material(expected, ObserveMaterial(d, existing)) : "the existing material no longer exists",
                () => CommandReadbackMaterialTypes.MaterialJson(ObserveMaterial(d, existing), existing: true),
                Array.Empty<string>,
                Array.Empty<string>);
        }

        Material? created = null;
        return new WritePlan(
            () =>
            {
                var material = baseMaterial is not null
                    ? baseMaterial.Duplicate(args.Name)
                    : d.GetElement(Material.Create(d, args.Name)) as Material;
                created = material ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, args.BaseMaterial is null ? "args.name" : "args.base_material", "Revit did not create the material");
                ApplyMaterial(material, args.Color, args.Transparency, args.Shininess, args.Smoothness, args.MaterialClass, surface, cut);
            },
            () => created is { IsValidObject: true } material ? CommandReadbackMaterialTypes.Material(expected, ObserveMaterial(d, material)) : "the new material no longer exists",
            () => CommandReadbackMaterialTypes.MaterialJson(ObserveMaterial(d, created!), existing: false),
            () => new[] { created!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan MaterialSetWrite(Document d, MaterialSetArgs args)
    {
        var material = Resolve(Materials(d), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name), args.Material, "args.material", "material");
        var patterns = FillPatterns(d);
        var surface = ResolvePattern(patterns, args.SurfacePattern, "args.surface_pattern");
        var cut = ResolvePattern(patterns, args.CutPattern, "args.cut_pattern");
        var before = ObserveMaterial(d, material);
        var expected = new ExpectedMaterial(material.Name, args.Color, args.Transparency, args.Shininess, args.Smoothness, null, surface?.UniqueId, cut?.UniqueId);
        return new WritePlan(
            () => ApplyMaterial(material, args.Color, args.Transparency, args.Shininess, args.Smoothness, null, surface, cut),
            () => material.IsValidObject ? CommandReadbackMaterialTypes.Material(expected, ObserveMaterial(d, material)) : "the material no longer exists",
            () => CommandReadbackMaterialTypes.MaterialSetJson(before, ObserveMaterial(d, material)),
            Array.Empty<string>,
            () => new[] { material.UniqueId });
    }

    static ObservedHostType ObserveHostType(Document d, HostObjAttributes type)
    {
        var structure = type.GetCompoundStructure();
        var layers = structure?.GetLayers().Select(layer =>
        {
            var material = d.GetElement(layer.MaterialId) as Material;
            return new ObservedRoofLayer(CommandRulesMaterials.FunctionName(layer.Function.ToString()), material?.UniqueId, material?.Name, Mm(layer.Width));
        }).ToArray();
        return new ObservedHostType(type.UniqueId, type.Name, layers, structure is null ? null : Mm(structure.GetWidth()),
            structure?.GetNumberOfShellLayers(ShellLayerType.Exterior) ?? 0, structure?.GetNumberOfShellLayers(ShellLayerType.Interior) ?? 0);
    }

    // The layered structure in request order (exterior/top first): the core is the run of structure layers. hostKind
    // "wall", "floor" or "roof" picks the end cap and opening wrapping (CommandRulesMaterialTypes.Wrapping) from the
    // base type's own structure; wall_type.create, floor_type.create and roof_type.create all build through here.
    static CompoundStructure HostStructure(Document d, string hostKind, CompoundStructure? baseStructure, IReadOnlyList<HostTypeLayerArg> layers, Material[] materials, (int Exterior, int Interior) shells)
    {
        var built = new List<CompoundStructureLayer>();
        for (var i = 0; i < layers.Count; i++)
        {
            var function = Enum.Parse<MaterialFunctionAssignment>(CommandRulesMaterialTypes.RevitFunction(layers[i].Function));
            built.Add(new CompoundStructureLayer(U(layers[i].Thickness.Millimetres), function, materials[i].Id));
        }
        var structure = CompoundStructure.CreateSimpleCompoundStructure(built);
        structure.SetNumberOfShellLayers(ShellLayerType.Exterior, shells.Exterior);
        structure.SetNumberOfShellLayers(ShellLayerType.Interior, shells.Interior);
        structure.StructuralMaterialIndex = shells.Exterior;
        var wrapping = CommandRulesMaterialTypes.Wrapping(hostKind, baseStructure?.EndCap.ToString(), baseStructure?.OpeningWrapping.ToString());
        structure.EndCap = Enum.Parse<EndCapCondition>(wrapping.EndCap);
        structure.OpeningWrapping = Enum.Parse<OpeningWrappingCondition>(wrapping.OpeningWrapping);
        if (!structure.IsValid(d, out var errors, out var pairErrors))
        {
            var details = errors.Select(error => "layer " + error.Key + ": " + error.Value).Concat(pairErrors.Select(pair => "layers " + pair.Key + " and " + pair.Value)).ToArray();
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.layers", "Revit refused the layer build-up" + (details.Length > 0 ? ": " + string.Join("; ", details) : ""));
        }
        return structure;
    }

    // SetCompoundStructure's ArgumentException (for example a wrong end cap for the element type, which IsValid does not
    // check) becomes a typed refusal instead of an unexpected executor error.
    static void SetStructure(HostObjAttributes type, CompoundStructure structure)
    {
        try { type.SetCompoundStructure(structure); }
        catch (Autodesk.Revit.Exceptions.ArgumentException error)
        {
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.layers", "Revit refused the layer build-up: " + error.Message);
        }
    }

    // hostKind: "wall" / "floor"; kind: "wall type" / "floor type"; member: "wall_type" / "floor_type". existingTypes:
    // every type of that class.
    static WritePlan HostTypeWrite<T>(Document d, string hostKind, string kind, string member, string name, IReadOnlyList<HostTypeLayerArg> layers, T baseType, T[] existingTypes) where T : HostObjAttributes
    {
        var materials = Materials(d);
        var resolved = layers.Select((layer, i) => Resolve(materials, material => new CommandCandidate(material.UniqueId, material.Name), layer.Material, "args.layers[" + i + "].material", "material")).ToArray();
        var expected = CommandRulesMaterialTypes.ExpectedLayers(layers, resolved.Select(material => material.UniqueId).ToArray());
        var shells = RevitMaterialTypeRules.Shells(layers.Select(layer => layer.Function).ToArray(), "args.layers");

        if (existingTypes.FirstOrDefault(type => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            if (CommandReadbackMaterialTypes.HostType(kind, name, expected, shells, ObserveHostType(d, existing)) is { } difference)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a " + kind + " named '" + existing.Name + "' already exists and differs (" + difference + "); choose another name");
            return new WritePlan(
                () => { },
                () => existing.IsValidObject ? CommandReadbackMaterialTypes.HostType(kind, name, expected, shells, ObserveHostType(d, existing)) : "the existing " + kind + " no longer exists",
                () => CommandReadbackMaterialTypes.HostTypeJson(member, ObserveHostType(d, existing), existing: true),
                Array.Empty<string>,
                Array.Empty<string>);
        }

        T? created = null;
        return new WritePlan(
            () =>
            {
                var duplicate = baseType.Duplicate(name) as T ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.base_type", "Revit did not duplicate the " + kind);
                created = duplicate;
                SetStructure(duplicate, HostStructure(d, hostKind, baseType.GetCompoundStructure(), layers, resolved, shells));
            },
            () => created is { IsValidObject: true } type ? CommandReadbackMaterialTypes.HostType(kind, name, expected, shells, ObserveHostType(d, type)) : "the new " + kind + " no longer exists",
            () => CommandReadbackMaterialTypes.HostTypeJson(member, ObserveHostType(d, created!), existing: false),
            () => new[] { created!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan WallTypeWrite(Document d, WallTypeCreateArgs args)
    {
        var wallTypes = WallTypes(d);
        var baseType = Resolve(wallTypes, type => new CommandCandidate(type.UniqueId, type.Name), args.BaseType, "args.base_type", "wall type");
        CommandRulesMaterialTypes.CheckBaseWallKind(baseType.Kind.ToString());
        return HostTypeWrite(d, "wall", "wall type", "wall_type", args.Name, args.Layers, baseType, wallTypes);
    }

    static WritePlan FloorTypeWrite(Document d, FloorTypeCreateArgs args)
    {
        var baseType = Resolve(FloorTypes(d), type => new CommandCandidate(type.UniqueId, type.Name), args.BaseType, "args.base_type", "floor type");
        if (baseType.GetCompoundStructure() is null)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.base_type", "floor type '" + baseType.Name + "' has no layers; pick a layered floor type from revit_query_floor_types");
        var allFloorTypes = new FilteredElementCollector(d).OfClass(typeof(FloorType)).Cast<FloorType>().ToArray();
        return HostTypeWrite(d, "floor", "floor type", "floor_type", args.Name, args.Layers, baseType, allFloorTypes);
    }

    static ObservedTypeRef TypeRefOf(ElementType type) => new(type.UniqueId, type.Name, Blank(type.FamilyName));

    static WritePlan ChangeTypeWrite(Document d, ElementChangeTypeArgs args)
    {
        var element = d.GetElement(args.ElementUid) ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.element_uid", "no element with UniqueId " + args.ElementUid);
        var kind = element switch { Wall => "wall", Floor => "floor", RoofBase => "roof", FamilyInstance => "family_instance", _ => null };
        CommandRulesMaterialTypes.CheckChangeTypeKind(kind, element.GetType().Name);
        if (element.GroupId != ElementId.InvalidElementId) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.element_uid", "the element is a group member; edit the group or ungroup it first");
        var current = d.GetElement(element.GetTypeId()) as ElementType ?? throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.element_uid", "the element has no type");
        var categoryId = element.Category?.Id;
        var valid = element.GetValidTypes().Select(id => d.GetElement(id) as ElementType).OfType<ElementType>()
            .Where(type => categoryId is not null && type.Category is { } typeCategory && typeCategory.Id.Equals(categoryId)).ToList();
        if (!valid.Any(type => type.Id == current.Id)) valid.Add(current);
        var candidates = valid.ToArray();
        ElementType target;
        try
        {
            target = Resolve(candidates, type => new CommandCandidate(type.UniqueId, type.Name, CommandReferences.Qualified(type.FamilyName, type.Name)), args.Type, "args.type", "type valid for this element");
        }
        catch (RevitCommandException error) when (error.Code == RevitCommandCodes.ReferenceNotFound && !RevitCommandProtocol.IsUniqueId(args.Type)
            && new FilteredElementCollector(d).WhereElementIsElementType().Cast<ElementType>().FirstOrDefault(type => type.Name == args.Type || CommandReferences.Qualified(type.FamilyName, type.Name) == args.Type) is { } other
            && !candidates.Any(type => type.Id == other.Id))
        {
            throw CommandRulesMaterialTypes.Incompatible(other.Name, other.Category?.Name, element.Category?.Name);
        }
        catch (RevitCommandException error) when (error.Code == RevitCommandCodes.ReferenceNotFound && RevitCommandProtocol.IsUniqueId(args.Type) && d.GetElement(args.Type) is ElementType other && !candidates.Any(type => type.Id == other.Id))
        {
            throw CommandRulesMaterialTypes.Incompatible(other.Name, other.Category?.Name, element.Category?.Name);
        }
        if (element is Wall wall && target is WallType wallType) CommandRulesMaterialTypes.CheckWallKind(wall.WallType.Kind.ToString(), wallType.Kind.ToString());
        var before = TypeRefOf(current);
        var category = element.Category?.Name;

        RevitElement? Live() => d.GetElement(args.ElementUid);
        string? ObservedTypeUid() => Live() is { } live && d.GetElement(live.GetTypeId()) is ElementType type ? type.UniqueId : null;
        return new WritePlan(
            () =>
            {
                if (target.Id == current.Id) return;
                if (target is FamilySymbol { IsActive: false } symbol) symbol.Activate();
                element.ChangeTypeId(target.Id);
            },
            () => CommandReadbackMaterialTypes.ChangeType(target.UniqueId, ObservedTypeUid()),
            () => CommandReadbackMaterialTypes.ChangeTypeJson(args.ElementUid, category, before, d.GetElement(Live()!.GetTypeId()) is ElementType now ? TypeRefOf(now) : TypeRefOf(target)),
            Array.Empty<string>,
            () => target.Id == current.Id ? Array.Empty<string>() : new[] { args.ElementUid });
    }
}
