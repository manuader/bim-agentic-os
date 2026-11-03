using Autodesk.Revit.DB;
using Bim.Core;
namespace Bim.Revit;

// Materials and layered roof types (chat-roof-gable) Revit executor: query.materials and roof_type.create. Same
// admission, receipts, transaction group, failure handling and readback-before-receipt as v1 (CommandsExecutor.cs);
// every decision that needs no Revit object is in CommandsMaterials.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   FilteredElementCollector.OfClass(typeof(Material)), Material.MaterialClass / MaterialCategory
//   ElementType.Duplicate(string) on a RoofType, HostObjAttributes.GetCompoundStructure / SetCompoundStructure
//   CompoundStructure.CreateSimpleCompoundStructure(IList<CompoundStructureLayer>) (layers exterior to interior),
//   new CompoundStructureLayer(double width, MaterialFunctionAssignment, ElementId material),
//   CompoundStructure.IsValid(Document, out errors, out two-layer errors), GetLayers, GetWidth
// Unverified in Revit (owner session required): whether CreateSimpleCompoundStructure's core boundaries suit every
// roof build-up (IsValid is checked first and a refusal is transaction_failed), and that a type duplicated from a
// roof type with a variable-thickness layer keeps no variable layer once the new structure is set.
internal static partial class CommandExecutor
{
    static Material[] Materials(Document d) => new FilteredElementCollector(d).OfClass(typeof(Material)).Cast<Material>().ToArray();

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    static string QueryMaterials(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        EmptyQueryArgs when envelope.Command == "query.materials" =>
            CommandReadbackMaterials.MaterialsJson(Materials(d).Select(material => new MaterialRow(material.Name, material.UniqueId, Blank(material.MaterialClass), Blank(material.MaterialCategory)))),
        _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", envelope.Command),
    };

    static ObservedRoofType ObserveRoofType(Document d, RoofType type)
    {
        var structure = type.GetCompoundStructure();
        var layers = structure?.GetLayers().Select(layer =>
        {
            var material = d.GetElement(layer.MaterialId) as Material;
            return new ObservedRoofLayer(CommandRulesMaterials.FunctionName(layer.Function.ToString()), material?.UniqueId, material?.Name, Mm(layer.Width));
        }).ToArray();
        return new ObservedRoofType(type.UniqueId, type.Name, layers, structure is null ? null : Mm(structure.GetWidth()));
    }

    static WritePlan RoofTypeWrite(Document d, RoofTypeCreateArgs args)
    {
        var roofTypes = RoofTypes(d);
        var baseType = Resolve(roofTypes, roofType => new CommandCandidate(roofType.UniqueId, roofType.Name), args.BaseType, "args.base_type", "roof type");
        if (baseType.GetCompoundStructure() is null)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.base_type", "roof type '" + baseType.Name + "' has no layers (for example sloped glazing); pick a basic roof type from revit_query_roof_types");
        var materials = Materials(d);
        var resolved = args.Layers.Select((layer, i) => Resolve(materials, material => new CommandCandidate(material.UniqueId, material.Name), layer.Material, "args.layers[" + i + "].material", "material")).ToArray();
        var expected = args.Layers.Select((layer, i) => new ExpectedRoofLayer(layer.Function, resolved[i].UniqueId, layer.Thickness.Millimetres)).ToArray();

        // A roof type of that name (Revit type names ignore case): returned unchanged when identical, else refused.
        if (roofTypes.FirstOrDefault(roofType => string.Equals(roofType.Name, args.Name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            if (CommandReadbackMaterials.RoofType(args.Name, expected, ObserveRoofType(d, existing)) is { } difference)
                throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.name", "a roof type named '" + existing.Name + "' already exists and differs (" + difference + "); choose another name");
            return new WritePlan(
                () => { },
                () => existing.IsValidObject ? CommandReadbackMaterials.RoofType(args.Name, expected, ObserveRoofType(d, existing)) : "the existing roof type no longer exists",
                () => CommandReadbackMaterials.RoofTypeJson(ObserveRoofType(d, existing), existing: true),
                Array.Empty<string>,
                Array.Empty<string>);
        }

        RoofType? created = null;
        return new WritePlan(
            () =>
            {
                var duplicate = baseType.Duplicate(args.Name) as RoofType ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.base_type", "Revit did not duplicate the roof type");
                created = duplicate;
                var layers = new List<CompoundStructureLayer>();
                for (var i = 0; i < args.Layers.Count; i++)
                {
                    var function = Enum.Parse<MaterialFunctionAssignment>(CommandRulesMaterials.RevitFunction(args.Layers[i].Function));
                    layers.Add(new CompoundStructureLayer(U(args.Layers[i].Thickness.Millimetres), function, resolved[i].Id));
                }
                var structure = CompoundStructure.CreateSimpleCompoundStructure(layers);
                // CreateSimpleCompoundStructure puts every layer in the core, and Revit refuses a core holding a membrane
                // (CoreTooThin): the core is the run of structure layers, everything outside it is shell.
                var firstStructure = layers.FindIndex(layer => layer.Function == MaterialFunctionAssignment.Structure);
                var lastStructure = layers.FindLastIndex(layer => layer.Function == MaterialFunctionAssignment.Structure);
                if (firstStructure < 0) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.layers", "a layered roof type needs at least one structure layer");
                for (var i = firstStructure; i <= lastStructure; i++)
                    if (layers[i].Function == MaterialFunctionAssignment.Membrane) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.layers[" + i + "]", "a membrane cannot lie between structure layers");
                structure.SetNumberOfShellLayers(ShellLayerType.Exterior, firstStructure);
                structure.SetNumberOfShellLayers(ShellLayerType.Interior, layers.Count - 1 - lastStructure);
                structure.StructuralMaterialIndex = firstStructure;
                if (!structure.IsValid(d, out var errors, out var pairErrors))
                {
                    var details = errors.Select(error => "layer " + error.Key + ": " + error.Value).Concat(pairErrors.Select(pair => "layers " + pair.Key + " and " + pair.Value)).ToArray();
                    throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.layers", "Revit refused the layer build-up" + (details.Length > 0 ? ": " + string.Join("; ", details) : ""));
                }
                duplicate.SetCompoundStructure(structure);
            },
            () => created is { IsValidObject: true } type ? CommandReadbackMaterials.RoofType(args.Name, expected, ObserveRoofType(d, type)) : "the new roof type no longer exists",
            () => CommandReadbackMaterials.RoofTypeJson(ObserveRoofType(d, created!), existing: false),
            () => new[] { created!.UniqueId },
            Array.Empty<string>);
    }
}
