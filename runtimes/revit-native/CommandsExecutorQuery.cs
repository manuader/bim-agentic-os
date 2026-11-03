using Autodesk.Revit.DB;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// P1-02 query.elements and query.inspect Revit collector. Read-only: no transaction and no receipt (queries never
// touch the receipt store, see CommandExecutor.Execute). It only reads Revit facts into the neutral records of core
// RevitCommandsQuery.cs; RevitElementQueryRules.Page and RevitElementQueryJson (unit-tested) decide ordering, bounds,
// truncation and the reply. Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here
// has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   FilteredElementCollector.WhereElementIsNotElementType / OfCategory(BuiltInCategory), Element.Category
//     (Category.Name, Category.BuiltInCategory), Element.Id (ElementId.Value), Element.LevelId, Element.GetTypeId,
//     ElementType.FamilyName, Document.GetElement(ElementId / string UniqueId)
//   Element.Parameters, Element.GetParameters(string), Element.get_Parameter(BuiltInParameter)
//   Parameter.Definition (Name, GetDataType; InternalDefinition.BuiltInParameter), Parameter.Id, StorageType,
//     HasValue, IsReadOnly, IsShared, AsString, AsInteger, AsDouble, AsElementId, AsValueString, GetUnitTypeId
//   UnitUtils.ConvertFromInternalUnits, UnitUtils.IsMeasurableSpec, SpecTypeId.Length / Area / Volume / Angle,
//     UnitTypeId.Millimeters / SquareMeters / CubicMeters / Degrees
// An element's level is Element.LevelId, else its FAMILY_LEVEL_PARAM, SCHEDULE_LEVEL_PARAM or
// INSTANCE_REFERENCE_LEVEL_PARAM. Elements without a category are skipped. Only the selected (at most limit) elements
// are read beyond their id and level.
// Unverified in Revit (owner session required): which elements report a level through LevelId versus a level
// parameter, and the parameter set and display texts of the fixture's families.
internal static partial class CommandExecutor
{
    static string QueryElements(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        ElementQueryArgs query => ElementsQuery(d, query),
        ElementInspectArgs inspect => InspectQuery(d, inspect),
        _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", envelope.Command),
    };

    static readonly BuiltInParameter[] LevelParameters = { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM };

    static string ElementsQuery(Document d, ElementQueryArgs args)
    {
        var collector = new FilteredElementCollector(d).WhereElementIsNotElementType();
        if (args.BuiltInCategory is { } name)
        {
            // The schema allows only OST_ followed by letters, digits and underscores, so no numeric or comma form parses.
            if (!Enum.TryParse<BuiltInCategory>(name, false, out var category) || !Enum.IsDefined(category) || category == BuiltInCategory.INVALID)
                throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.category", "Revit has no BuiltInCategory named " + name);
            collector = collector.OfCategory(category);
        }
        var level = args.Level is null ? null : ResolveLevel(d, args.Level, "args.level");
        var matched = new List<long>();
        foreach (var element in collector)
        {
            if (element.Category is null) continue;
            if (level is not null && ElementLevelId(element) != level.Id) continue;
            matched.Add(element.Id.Value);
        }
        var (selected, _) = RevitElementQueryRules.Page(matched, args.Limit);
        var rows = new List<ObservedQueryElement>();
        foreach (var id in selected)
        {
            if (d.GetElement(new ElementId(id)) is not { } element) continue;
            var type = TypeOf(d, element);
            var (parameters, missing) = RequestedParameters(d, element, type, args.Parameters);
            rows.Add(ObserveQueryElement(d, element, type) with { Parameters = parameters, Missing = missing });
        }
        var levelReference = level is null ? null : new ObservedReference(level.Name, level.UniqueId);
        return RevitElementQueryJson.Elements(args, levelReference, matched.Distinct().Count(), rows);
    }

    static string InspectQuery(Document d, ElementInspectArgs args)
    {
        var element = d.GetElement(args.UniqueId) ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.unique_id", "no element with this UniqueId in the open document");
        var type = TypeOf(d, element);
        var parameters = new List<ObservedParameter>();
        foreach (Parameter parameter in element.Parameters)
            if (parameter.Definition is not null) parameters.Add(ObserveParameter(d, parameter, RevitParameterSources.Instance));
        if (type is not null)
            foreach (Parameter parameter in type.Parameters)
                if (parameter.Definition is not null) parameters.Add(ObserveParameter(d, parameter, RevitParameterSources.Type));
        return RevitElementQueryJson.Inspect(ObserveQueryElement(d, element, type), parameters);
    }

    static ElementType? TypeOf(Document d, RevitElement element)
    {
        var typeId = element.GetTypeId();
        return typeId == ElementId.InvalidElementId ? null : d.GetElement(typeId) as ElementType;
    }

    static ElementId ElementLevelId(RevitElement element)
    {
        var levelId = element.LevelId;
        if (levelId is not null && levelId != ElementId.InvalidElementId) return levelId;
        foreach (var builtIn in LevelParameters)
        {
            if (element.get_Parameter(builtIn) is { HasValue: true, StorageType: StorageType.ElementId } parameter && parameter.AsElementId() is { } id && id != ElementId.InvalidElementId)
                return id;
        }
        return ElementId.InvalidElementId;
    }

    static ObservedQueryElement ObserveQueryElement(Document d, RevitElement element, ElementType? type)
    {
        var levelId = ElementLevelId(element);
        var level = levelId == ElementId.InvalidElementId ? null : d.GetElement(levelId) as Level;
        var category = element.Category;
        return new ObservedQueryElement(element.Id.Value, element.UniqueId, category?.Name, category is null ? null : category.BuiltInCategory.ToString())
        {
            Level = level is null ? null : new ObservedReference(level.Name, level.UniqueId),
            FamilyName = type?.FamilyName,
            TypeName = type?.Name,
            TypeUid = type?.UniqueId,
        };
    }

    // Named parameters: on the element first, then on its type; several of one name: the lowest parameter id.
    // No names: the default built-in parameters on the element, skipping any it does not have.
    static (IReadOnlyList<ObservedParameter> Parameters, IReadOnlyList<string> Missing) RequestedParameters(Document d, RevitElement element, ElementType? type, IReadOnlyList<string> names)
    {
        var found = new List<ObservedParameter>();
        var missing = new List<string>();
        if (names.Count == 0)
        {
            foreach (var name in RevitElementQueryRules.DefaultParameters)
            {
                if (Enum.TryParse<BuiltInParameter>(name, false, out var builtIn) && element.get_Parameter(builtIn) is { Definition: not null } parameter)
                    found.Add(ObserveParameter(d, parameter, RevitParameterSources.Instance));
            }
            return (found, missing);
        }
        foreach (var name in names)
        {
            if (Named(element, name) is { } instance) found.Add(ObserveParameter(d, instance, RevitParameterSources.Instance));
            else if (type is not null && Named(type, name) is { } typed) found.Add(ObserveParameter(d, typed, RevitParameterSources.Type));
            else missing.Add(name);
        }
        return (found, missing);
    }

    static Parameter? Named(RevitElement element, string name) =>
        element.GetParameters(name).Where(parameter => parameter.Definition is not null).OrderBy(parameter => parameter.Id.Value).FirstOrDefault();

    static ObservedParameter ObserveParameter(Document d, Parameter parameter, string source)
    {
        var definition = parameter.Definition;
        var builtIn = definition is InternalDefinition internalDefinition && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID ? internalDefinition.BuiltInParameter.ToString() : null;
        var spec = definition.GetDataType();
        var specText = spec is null || string.IsNullOrEmpty(spec.TypeId) ? null : spec.TypeId;
        var storage = parameter.StorageType switch
        {
            StorageType.String => RevitParameterStorage.String,
            StorageType.Integer => RevitParameterStorage.Integer,
            StorageType.Double => RevitParameterStorage.Double,
            StorageType.ElementId => RevitParameterStorage.ElementId,
            _ => RevitParameterStorage.None,
        };
        var observed = new ObservedParameter(definition.Name, parameter.Id.Value, builtIn, source, storage, specText, parameter.HasValue, parameter.IsReadOnly, parameter.IsShared);
        if (parameter.HasValue)
        {
            switch (parameter.StorageType)
            {
                case StorageType.String: observed = observed with { Text = parameter.AsString() }; break;
                case StorageType.Integer: observed = observed with { Integer = parameter.AsInteger() }; break;
                case StorageType.Double:
                    var (number, unit) = TypedNumber(parameter, spec);
                    observed = observed with { Number = number, Unit = unit };
                    break;
                case StorageType.ElementId:
                    var id = parameter.AsElementId();
                    if (id is not null)
                        observed = observed with { ElementIdValue = id.Value, ElementUid = id == ElementId.InvalidElementId ? null : d.GetElement(id)?.UniqueId };
                    break;
            }
        }
        string? display = null;
        try { display = parameter.AsValueString(); }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { display = null; }
        return observed with { Display = display };
    }

    // Lengths in mm, areas in m2, volumes in m3, angles in degrees; any other measurable value in the document's
    // display unit for it (named by its unit type id); anything else as Revit's raw number without a unit.
    static (double Number, string? Unit) TypedNumber(Parameter parameter, ForgeTypeId? spec)
    {
        var raw = parameter.AsDouble();
        if (spec is null || string.IsNullOrEmpty(spec.TypeId)) return (raw, null);
        if (spec.Equals(SpecTypeId.Length)) return (Mm(raw), RevitParameterUnits.Millimetres);
        if (spec.Equals(SpecTypeId.Area)) return (UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.SquareMeters), RevitParameterUnits.SquareMetres);
        if (spec.Equals(SpecTypeId.Volume)) return (UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.CubicMeters), RevitParameterUnits.CubicMetres);
        if (spec.Equals(SpecTypeId.Angle)) return (UnitUtils.ConvertFromInternalUnits(raw, UnitTypeId.Degrees), RevitParameterUnits.Degrees);
        try
        {
            if (UnitUtils.IsMeasurableSpec(spec))
            {
                var unit = parameter.GetUnitTypeId();
                return (UnitUtils.ConvertFromInternalUnits(raw, unit), unit.TypeId);
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        return (raw, null);
    }
}
