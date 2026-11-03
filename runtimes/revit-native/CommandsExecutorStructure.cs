using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Bim.Core;
namespace Bim.Revit;

// P9-02 structure commands Revit executor: query.grids, query.structural_types, grid.create,
// structural_column.create, structural_beam.create, foundation.create_isolated and foundation.create_wall. Same
// admission, receipts, transaction group, failure handling and readback-before-receipt as catalogue v1
// (CommandsExecutor.cs); every decision that needs no Revit object is in CommandsStructure.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Grid.Create(Document, Line), Grid.Curve / IsCurved / Name
//   Document.Create.NewFamilyInstance(XYZ, FamilySymbol, Level, StructuralType.Column / StructuralType.Footing) and
//     NewFamilyInstance(Curve, FamilySymbol, Level, StructuralType.Beam); FamilyInstance.StructuralType /
//     IsSlantedColumn / Location (LocationPoint, LocationCurve) / Symbol; FamilySymbol.IsActive / Activate
//   BuiltInParameter.FAMILY_BASE_LEVEL_PARAM / FAMILY_TOP_LEVEL_PARAM / FAMILY_BASE_LEVEL_OFFSET_PARAM /
//     FAMILY_TOP_LEVEL_OFFSET_PARAM (columns), INSTANCE_REFERENCE_LEVEL_PARAM and STRUCTURAL_BEAM_END0_ELEVATION /
//     STRUCTURAL_BEAM_END1_ELEVATION (beams), FAMILY_LEVEL_PARAM (footings), Family.FamilyPlacementType,
//     MultiSegmentGrid.Name
//   WallFoundation.Create(Document, ElementId wallFoundationTypeId, ElementId wallId), WallFoundation.WallId,
//     WallFoundationType
// Unverified in Revit (owner session required): that a column placed with the base level keeps the plan point and
// accepts the top level and both offsets as set (each is read back from its parameter and rolls back on a
// difference); that a beam's LocationCurve keeps the requested end points and direction and its reference level is
// INSTANCE_REFERENCE_LEVEL_PARAM with both level offsets 0; that a footing reports its level through LevelId (else
// FAMILY_LEVEL_PARAM) and keeps its LocationPoint at the level's project elevation; and
// whether WallFoundation.Create refuses a wall that already has one (checked before the call). created lists the
// placed element only: joins, cut-backs or analytical elements Revit adds are not diffed.
internal static partial class CommandExecutor
{
    static Grid[] Grids(Document d) => new FilteredElementCollector(d).OfClass(typeof(Grid)).Cast<Grid>().ToArray();

    static FamilySymbol[] StructuralSymbols(Document d, string category) => new FilteredElementCollector(d).OfClass(typeof(FamilySymbol))
        .OfCategory(Enum.Parse<BuiltInCategory>(CommandRulesStructure.BuiltInCategory(category)!)).Cast<FamilySymbol>().ToArray();

    static WallFoundationType[] WallFoundationTypes(Document d) => new FilteredElementCollector(d).OfClass(typeof(WallFoundationType)).Cast<WallFoundationType>().ToArray();

    static FamilySymbol ResolveStructuralSymbol(Document d, string category, string reference, string field, string kind) =>
        Resolve(StructuralSymbols(d, category), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name, CommandReferences.Qualified(candidate.Family.Name, candidate.Name)), reference, field, kind);

    static string QueryStructure(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        EmptyQueryArgs when envelope.Command == "query.grids" =>
            CommandReadbackStructure.GridsJson(Grids(d).Select(grid => { var observed = ObserveGrid(grid); return new GridRow(observed.Name, observed.UniqueId, observed.Curved, observed.Start, observed.End); })),
        StructuralTypeQueryArgs { Category: "wall_foundation" } query =>
            CommandReadbackStructure.StructuralTypesJson(query.Category, WallFoundationTypes(d).Select(type => new StructuralTypeRow(null, type.Name, type.UniqueId, true, null))),
        StructuralTypeQueryArgs query =>
            CommandReadbackStructure.StructuralTypesJson(query.Category, StructuralSymbols(d, query.Category).Select(symbol => new StructuralTypeRow(symbol.Family.Name, symbol.Name, symbol.UniqueId, symbol.IsActive, symbol.Family.FamilyPlacementType.ToString()))),
        _ => QueryDocumentation(d, envelope),
    };

    static ObservedGrid ObserveGrid(Grid grid)
    {
        var curve = grid.Curve;
        return new ObservedGrid(grid.UniqueId, grid.Name, grid.IsCurved, Plan(curve.GetEndPoint(0)), Plan(curve.GetEndPoint(1)));
    }

    static PlanPoint Requested(Point2 point) => new(point.X.Millimetres, point.Y.Millimetres);

    static PlanPoint? LocationPlanPoint(FamilyInstance instance) => instance.Location is LocationPoint point ? Plan(point.Point) : null;

    // Activates an inactive symbol inside the write transaction; returns whether it did.
    static bool EnsureActive(Document d, FamilySymbol symbol)
    {
        if (symbol.IsActive) return false;
        symbol.Activate();
        d.Regenerate();
        return true;
    }

    static WritePlan GridWrite(Document d, GridCreateArgs args)
    {
        CommandRulesStructure.CheckGridNameFree(args.Name, Grids(d).Select(grid => grid.Name),
            new FilteredElementCollector(d).OfClass(typeof(MultiSegmentGrid)).Cast<MultiSegmentGrid>().Select(grid => grid.Name));
        var line = Line.CreateBound(At(args.Start, 0), At(args.End, 0));
        Grid? grid = null;
        return new WritePlan(
            () =>
            {
                grid = Grid.Create(d, line);
                if (args.Name is { } name) grid.Name = name;
            },
            () => grid is { IsValidObject: true } created ? CommandReadbackStructure.Grid(Requested(args.Start), Requested(args.End), args.Name, ObserveGrid(created)) : "the grid no longer exists",
            () => CommandReadbackStructure.GridJson(ObserveGrid(grid!)),
            () => new[] { grid!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan ColumnWrite(Document d, StructuralColumnCreateArgs args)
    {
        var symbol = ResolveStructuralSymbol(d, "column", args.Type, "args.type", "structural column type");
        var baseLevel = ResolveLevel(d, args.BaseLevel, "args.base_level");
        var topLevel = ResolveLevel(d, args.TopLevel, "args.top_level");
        CommandRulesStructure.ColumnHeightMm(baseLevel.UniqueId, Mm(baseLevel.ProjectElevation), args.BaseOffset.Millimetres, topLevel.UniqueId, Mm(topLevel.ProjectElevation), args.TopOffset.Millimetres);
        FamilyInstance? column = null;
        var activated = false;
        ObservedColumn Observe(FamilyInstance created) => new(created.UniqueId, created.Symbol.UniqueId, created.StructuralType.ToString(), created.IsSlantedColumn, LocationPlanPoint(created),
            LevelParameterUid(d, created, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM), LevelParameterUid(d, created, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM),
            LengthParameter(created, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM), LengthParameter(created, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM));
        void Set(FamilyInstance instance, BuiltInParameter parameter, Func<Parameter, bool> set, string field, string what)
        {
            var target = instance.get_Parameter(parameter);
            if (target is null || target.IsReadOnly || !set(target)) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, field, "the " + what + " of this column cannot be set");
        }
        return new WritePlan(
            () =>
            {
                activated = EnsureActive(d, symbol);
                var created = d.Create.NewFamilyInstance(At(args.Point, baseLevel.ProjectElevation), symbol, baseLevel, StructuralType.Column);
                column = created;
                Set(created, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, target => target.Set(topLevel.Id), "args.top_level", "top level");
                Set(created, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, target => target.Set(U(args.BaseOffset.Millimetres)), "args.base_offset_mm", "base offset");
                Set(created, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, target => target.Set(U(args.TopOffset.Millimetres)), "args.top_offset_mm", "top offset");
            },
            () => column is { IsValidObject: true } created
                ? CommandReadbackStructure.Column(symbol.UniqueId, Requested(args.Point), baseLevel.UniqueId, topLevel.UniqueId, args.BaseOffset.Millimetres, args.TopOffset.Millimetres, Observe(created))
                : "the structural column no longer exists",
            () => CommandReadbackStructure.ColumnJson(Observe(column!)),
            () => new[] { column!.UniqueId },
            () => activated ? new[] { symbol.UniqueId } : Array.Empty<string>());
    }

    static WritePlan BeamWrite(Document d, StructuralBeamCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var symbol = ResolveStructuralSymbol(d, "framing", args.Type, "args.type", "structural framing type");
        var line = Line.CreateBound(At(args.Start, level.ProjectElevation), At(args.End, level.ProjectElevation));
        FamilyInstance? beam = null;
        var activated = false;
        ObservedBeam Observe(FamilyInstance created)
        {
            var curve = (created.Location as LocationCurve)?.Curve;
            return new ObservedBeam(created.UniqueId, created.Symbol.UniqueId, created.StructuralType.ToString(), LevelParameterUid(d, created, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM),
                curve is null ? null : Plan(curve.GetEndPoint(0)), curve is null ? null : Plan(curve.GetEndPoint(1)),
                LengthParameter(created, BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION), LengthParameter(created, BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION));
        }
        return new WritePlan(
            () =>
            {
                activated = EnsureActive(d, symbol);
                beam = d.Create.NewFamilyInstance(line, symbol, level, StructuralType.Beam);
            },
            () => beam is { IsValidObject: true } created
                ? CommandReadbackStructure.Beam(symbol.UniqueId, level.UniqueId, Requested(args.Start), Requested(args.End), Observe(created))
                : "the structural beam no longer exists",
            () => CommandReadbackStructure.BeamJson(Observe(beam!)),
            () => new[] { beam!.UniqueId },
            () => activated ? new[] { symbol.UniqueId } : Array.Empty<string>());
    }

    static WritePlan IsolatedFoundationWrite(Document d, IsolatedFoundationCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var symbol = ResolveStructuralSymbol(d, "foundation", args.Type, "args.type", "isolated foundation type");
        CommandRulesStructure.CheckIsolatedFoundationPlacement(symbol.Family.FamilyPlacementType.ToString());
        FamilyInstance? footing = null;
        var activated = false;
        ObservedFooting Observe(FamilyInstance created) => new(created.UniqueId, created.Symbol.UniqueId, created.StructuralType.ToString(),
            created.LevelId != ElementId.InvalidElementId && d.GetElement(created.LevelId) is Level own ? own.UniqueId : LevelParameterUid(d, created, BuiltInParameter.FAMILY_LEVEL_PARAM),
            LocationPlanPoint(created), created.Location is LocationPoint location ? (double?)Mm(location.Point.Z) : null);
        return new WritePlan(
            () =>
            {
                activated = EnsureActive(d, symbol);
                footing = d.Create.NewFamilyInstance(At(args.Point, level.ProjectElevation), symbol, level, StructuralType.Footing);
            },
            () => footing is { IsValidObject: true } created
                ? CommandReadbackStructure.Footing(symbol.UniqueId, level.UniqueId, Requested(args.Point), Mm(level.ProjectElevation), Observe(created))
                : "the foundation no longer exists",
            () => CommandReadbackStructure.FootingJson(Observe(footing!)),
            () => new[] { footing!.UniqueId },
            () => activated ? new[] { symbol.UniqueId } : Array.Empty<string>());
    }

    static WritePlan WallFoundationWrite(Document d, WallFoundationCreateArgs args)
    {
        var host = d.GetElement(args.HostWallUid) as Wall ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.host_wall_uid", "no wall with UniqueId " + args.HostWallUid);
        var existing = new FilteredElementCollector(d).OfClass(typeof(WallFoundation)).Cast<WallFoundation>().Count(foundation => foundation.WallId == host.Id);
        CommandRulesStructure.CheckWallFoundationHost(host.WallType.Kind.ToString(), existing);
        var type = Resolve(WallFoundationTypes(d), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name), args.Type, "args.type", "wall foundation type");
        WallFoundation? foundation = null;
        ObservedWallFoundation Observe(WallFoundation created) => new(created.UniqueId, UniqueIdOf(d, created.GetTypeId()), d.GetElement(created.WallId) is Wall wall ? wall.UniqueId : null);
        return new WritePlan(
            () => foundation = WallFoundation.Create(d, type.Id, host.Id),
            () => foundation is { IsValidObject: true } created ? CommandReadbackStructure.WallFoundation(type.UniqueId, host.UniqueId, Observe(created)) : "the wall foundation no longer exists",
            () => CommandReadbackStructure.WallFoundationJson(Observe(foundation!)),
            () => new[] { foundation!.UniqueId },
            Array.Empty<string>);
    }
}
