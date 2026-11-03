using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// Catalogue v2 (P9-01) Revit executor: query.roof_types, roof.create_footprint, stair.create, curtain_wall.create and
// floor.create_opening. Same admission, receipts, transaction group, failure handling and readback-before-receipt
// as v1 (CommandsExecutor.cs); every decision that needs no Revit object is in CommandsV2.cs (unit-tested).
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Document.Create.NewFootPrintRoof(CurveArray, Level, RoofType, out ModelCurveArray), FootPrintRoof.DefinesSlope /
//     SlopeAngle per footprint ModelCurve and FootPrintRoof.GetProfiles (the sketch, read back independently of the
//     creation mapping), BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM / ROOF_SLOPE; slopes converted only through
//     UnitUtils with UnitTypeId.SlopeDegrees (Revit's internal slope unit is rise over run)
//   StairsEditScope(Document, string).Start(ElementId baseLevel, ElementId topLevel), Commit(IFailuresPreprocessor);
//     StairsRun.CreateStraightRun(Document, ElementId, Line, StairsRunJustification), StairsRun.ActualRunWidth,
//     StairsRun.GetStairsPath, Stairs.GetStairsRuns / ActualRisersNumber / DesiredRisersNumber /
//     GetAssociatedRailings, STAIRS_BASE_LEVEL_PARAM / STAIRS_TOP_LEVEL_PARAM
//   Wall.Create with a WallKind.Curtain type, Wall.CurtainGrid, CurtainGrid.AddGridLine(bool isUGridLine, XYZ, bool),
//     CurtainGridLine.FullCurve, CurtainGrid.NumULines / NumVLines / GetPanelIds / GetMullionIds
//   Document.Create.NewOpening(Element host, CurveArray profile, bool perpendicularFace), Opening.Host /
//     IsRectBoundary / BoundaryCurves
// Unverified in Revit (owner session required): whether a StairsEditScope may start inside the command's
// TransactionGroup (IsPermitted is checked and a refusal is transaction_failed), the U/V orientation of curtain grid
// lines on a wall (the readback checks orientation and rolls back if it is swapped), whether the stairs path of a
// centre-justified straight run starts at the requested run start, and that ROOF_SLOPE carries the edge slope.
// created lists the primary element and the children these APIs name (runs, railings, grid lines, panels,
// mullions); other elements Revit may add as a side effect (for example joins or tags) are not diffed.
internal static partial class CommandExecutor
{
    static RoofType[] RoofTypes(Document d) => new FilteredElementCollector(d).OfClass(typeof(RoofType)).Cast<RoofType>().ToArray();

    static string QueryV2(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        EmptyQueryArgs when envelope.Command == "query.roof_types" =>
            CommandReadbackV2.RoofTypesJson(RoofTypes(d).Select(type => new RoofTypeRow(type.Name, type.UniqueId, type.GetCompoundStructure() is { } layers ? Mm(layers.GetWidth()) : null))),
        _ => QueryViews(d, envelope),
    };

    // Closed outline (implicit closing edge) as lines in the plane z (internal units).
    static CurveArray Polygon(IReadOnlyList<Point2> points, double z)
    {
        var vertices = points.Select(point => At(point, z)).ToArray();
        var array = new CurveArray();
        for (var i = 0; i < vertices.Length; i++) array.Append(Line.CreateBound(vertices[i], vertices[(i + 1) % vertices.Length]));
        return array;
    }

    static double SlopeDegrees(double internalSlope) => UnitUtils.ConvertFromInternalUnits(internalSlope, UnitTypeId.SlopeDegrees);

    // UniqueIds of the elements that still exist, in the given order.
    static IEnumerable<string> Uids(Document d, IEnumerable<ElementId> ids) => ids.Select(id => d.GetElement(id)?.UniqueId).OfType<string>();

    static XYZ AtPlan(PlanPoint point, double zMillimetres) => new(U(point.X), U(point.Y), U(zMillimetres));

    static string? LevelParameterUid(Document d, RevitElement element, BuiltInParameter parameter) =>
        element.get_Parameter(parameter) is { HasValue: true, StorageType: StorageType.ElementId } value && d.GetElement(value.AsElementId()) is Level level ? level.UniqueId : null;

    static WritePlan RoofPlan(Document d, RoofFootprintCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var type = Resolve(RoofTypes(d), roofType => new CommandCandidate(roofType.UniqueId, roofType.Name), args.Type, "args.type", "roof type");
        var footprint = Polygon(args.Boundary, level.ProjectElevation);
        FootPrintRoof? roof = null;
        var edges = new List<ModelCurve>();
        // Read from the roof's own sketch, not from the creation mapping.
        ObservedRoof Observe(FootPrintRoof created)
        {
            var profiles = created.GetProfiles();
            var sketch = new List<ModelCurve>();
            foreach (ModelCurveArray loop in profiles)
                foreach (ModelCurve curve in loop) sketch.Add(curve);
            var slopeParameter = created.get_Parameter(BuiltInParameter.ROOF_SLOPE) is { HasValue: true, StorageType: StorageType.Double } value ? SlopeDegrees(value.AsDouble()) : (double?)null;
            return new ObservedRoof(created.UniqueId, UniqueIdOf(d, created.LevelId), UniqueIdOf(d, created.GetTypeId()),
                LengthParameter(created, BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM),
                sketch.Select(edge => created.get_DefinesSlope(edge) ? new ObservedRoofEdge(true, SlopeDegrees(created.get_SlopeAngle(edge))) : new ObservedRoofEdge(false, null)).ToArray(),
                slopeParameter, profiles.Size, sketch.Select(edge => Plan(edge.GeometryCurve.GetEndPoint(0))).ToArray());
        }
        return new WritePlan(
            () =>
            {
                // Revit 2025 throws ArgumentNullException ("Value cannot be null") unless the out mapping array is
                // pre-allocated: the native side writes into the caller's instance (observed in owner chat 2026-11-03).
                ModelCurveArray mapping = new ModelCurveArray();
                var created = d.Create.NewFootPrintRoof(footprint, level, type, out mapping);
                roof = created;
                foreach (ModelCurve edge in mapping) edges.Add(edge);
                foreach (var edge in edges)
                {
                    created.set_DefinesSlope(edge, args.Slope is not null);
                    if (args.Slope is { } slope) created.set_SlopeAngle(edge, UnitUtils.ConvertToInternalUnits(slope.Degrees, UnitTypeId.SlopeDegrees));
                }
                if (args.BaseOffset.Millimetres != 0)
                {
                    var offset = created.get_Parameter(BuiltInParameter.ROOF_LEVEL_OFFSET_PARAM);
                    if (offset is null || offset.IsReadOnly || !offset.Set(U(args.BaseOffset.Millimetres)))
                        throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.base_offset_mm", "the base offset of this roof cannot be set");
                }
            },
            () => roof is { IsValidObject: true } created
                ? CommandReadbackV2.Roof(level.UniqueId, type.UniqueId, args.Boundary, args.Slope?.Degrees, args.BaseOffset.Millimetres, Observe(created))
                : "the roof no longer exists",
            () => CommandReadbackV2.RoofJson(Observe(roof!)),
            () => new[] { roof!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan CurtainWallPlan(Document d, CurtainWallCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var type = Resolve(WallTypes(d), wallType => new CommandCandidate(wallType.UniqueId, wallType.Name), args.Type, "args.type", "wall type");
        CommandRulesV2.CheckCurtainWallType(type.Kind.ToString());
        var expected = CommandGeometry.Wall(args.Curve);
        var grid = CommandRulesV2.CurtainGrid(args, Mm(level.ProjectElevation));
        Wall? wall = null;
        var vertical = new List<CurtainGridLine>();
        var horizontal = new List<CurtainGridLine>();
        ObservedGridLine GridLineOf(CurtainGridLine line)
        {
            var curve = line.FullCurve;
            XYZ start = curve.GetEndPoint(0), end = curve.GetEndPoint(1);
            return new ObservedGridLine(line.UniqueId, Plan(start), Mm(start.Z), Plan(end), Mm(end.Z));
        }
        string? Check()
        {
            if (wall is not { IsValidObject: true } created) return "the curtain wall no longer exists";
            if (CommandReadback.Wall(expected, level.UniqueId, type.UniqueId, args.Height.Millimetres, args.BaseOffset.Millimetres, false, ObserveWall(d, created)) is { } mismatch) return mismatch;
            if (vertical.Concat(horizontal).Any(line => !line.IsValidObject)) return "an added curtain grid line no longer exists";
            return CommandReadbackV2.CurtainGrid(grid, vertical.Select(GridLineOf).ToArray(), horizontal.Select(GridLineOf).ToArray());
        }
        string[] Added() => vertical.Concat(horizontal).Select(line => line.UniqueId).ToArray();
        // Panels and mullions are regenerated with the grid; they are read after the transaction commits.
        IReadOnlyList<string> Created() => CommandRulesV2.Created(wall!.UniqueId, Added(),
            wall.CurtainGrid is { } panelGrid ? Uids(d, panelGrid.GetPanelIds()) : Array.Empty<string>(),
            wall.CurtainGrid is { } mullionGrid ? Uids(d, mullionGrid.GetMullionIds()) : Array.Empty<string>());
        return new WritePlan(
            () =>
            {
                var created = Wall.Create(d, WallCurveAt(args.Curve, level.ProjectElevation), type.Id, level.Id, U(args.Height.Millimetres), U(args.BaseOffset.Millimetres), false, false);
                wall = created;
                if (grid.Vertical.Count == 0 && grid.HorizontalZMm.Count == 0) return;
                // The curtain grid exists only after regeneration.
                d.Regenerate();
                var curtainGrid = created.CurtainGrid ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.grid", "the curtain wall has no curtain grid");
                foreach (var point in grid.Vertical) vertical.Add(curtainGrid.AddGridLine(CommandRulesV2.IsUGridLine(vertical: true), AtPlan(point, grid.VerticalZMm), false));
                foreach (var z in grid.HorizontalZMm) horizontal.Add(curtainGrid.AddGridLine(CommandRulesV2.IsUGridLine(vertical: false), AtPlan(grid.HorizontalAt, z), false));
            },
            Check,
            () => CommandReadbackV2.CurtainWallJson(ObserveWall(d, wall!), wall!.CurtainGrid?.NumVLines ?? 0, wall.CurtainGrid?.NumULines ?? 0, Added(),
                wall.CurtainGrid?.GetPanelIds().Count ?? 0, wall.CurtainGrid?.GetMullionIds().Count ?? 0),
            Created,
            Array.Empty<string>);
    }

    static WritePlan FloorOpeningPlan(Document d, FloorOpeningCreateArgs args)
    {
        var host = d.GetElement(args.HostFloorUid) as Floor ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.host_floor_uid", "no floor with UniqueId " + args.HostFloorUid);
        var level = d.GetElement(host.LevelId) as Level ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the host floor has no level");
        var profile = Polygon(args.Profile, level.ProjectElevation);
        Opening? opening = null;
        ObservedFloorOpening Observe(Opening created)
        {
            var vertices = new List<PlanPoint>();
            if (!created.IsRectBoundary)
                foreach (Curve curve in created.BoundaryCurves) vertices.Add(Plan(curve.GetEndPoint(0)));
            return new ObservedFloorOpening(created.UniqueId, created.Host?.UniqueId, created.IsRectBoundary, vertices);
        }
        return new WritePlan(
            () => opening = d.Create.NewOpening(host, profile, true),
            () => opening is { IsValidObject: true } created ? CommandReadbackV2.FloorOpening(host.UniqueId, args.Profile, Observe(created)) : "the floor opening no longer exists",
            () => CommandReadbackV2.FloorOpeningJson(Observe(opening!)),
            () => new[] { opening!.UniqueId },
            () => new[] { host.UniqueId });
    }

    // A stair is built in a StairsEditScope, which opens its own transactions, so it cannot use WritePlan's single
    // transaction. Same envelope: one TransactionGroup, readback after Revit's failure processing, then the receipt.
    static string WriteStair(Document d, CommandRequest request, ReceiptStore store, StairCreateArgs args)
    {
        var envelope = request.Envelope;
        var baseLevel = ResolveLevel(d, args.BaseLevel, "args.base_level");
        var topLevel = ResolveLevel(d, args.TopLevel, "args.top_level");
        CommandRulesV2.CheckStairLevels(baseLevel.UniqueId, Mm(baseLevel.ProjectElevation), topLevel.UniqueId, Mm(topLevel.ProjectElevation));
        var z = baseLevel.ProjectElevation;
        var runLine = Line.CreateBound(At(args.RunStart, z), At(args.RunEnd, z));
        var failures = new CommandFailures();
        Stairs? stairs = null;
        ObservedStairs Observe(Stairs created)
        {
            var runs = created.GetStairsRuns().Select(id => d.GetElement(id) as StairsRun).ToArray();
            PlanPoint? start = null, end = null;
            if (runs is [{ } only])
            {
                var path = only.GetStairsPath().ToList();
                if (path.Count > 0) { start = Plan(path[0].GetEndPoint(0)); end = Plan(path[^1].GetEndPoint(1)); }
            }
            return new ObservedStairs(created.UniqueId,
                LevelParameterUid(d, created, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM), LevelParameterUid(d, created, BuiltInParameter.STAIRS_TOP_LEVEL_PARAM),
                runs.Select(run => run is null ? (double?)null : Mm(run.ActualRunWidth)).ToArray(),
                created.ActualRisersNumber, created.DesiredRisersNumber, start, end);
        }
        var requestedStart = new PlanPoint(args.RunStart.X.Millimetres, args.RunStart.Y.Millimetres);
        var requestedEnd = new PlanPoint(args.RunEnd.X.Millimetres, args.RunEnd.Y.Millimetres);
        using var group = new TransactionGroup(d, "BIM Agentic OS " + envelope.Command + " " + envelope.OperationId);
        if (group.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction group did not start");
        try
        {
            using (var scope = new StairsEditScope(d, "BIM Agentic OS " + envelope.Command))
            {
                if (!scope.IsPermitted) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "Revit does not permit a stairs edit scope in this state");
                var stairsId = scope.Start(baseLevel.Id, topLevel.Id);
                try
                {
                    using (var tx = new Transaction(d, "BIM Agentic OS " + envelope.Command))
                    {
                        if (tx.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction did not start");
                        var options = tx.GetFailureHandlingOptions();
                        options.SetFailuresPreprocessor(failures);
                        options.SetClearAfterRollback(true);
                        tx.SetFailureHandlingOptions(options);
                        try
                        {
                            var run = StairsRun.CreateStraightRun(d, stairsId, runLine, StairsRunJustification.Center);
                            run.ActualRunWidth = U(args.Width.Millimetres);
                            var status = tx.Commit();
                            if (status != TransactionStatus.Committed)
                                throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "Revit did not commit the transaction (" + status + ")");
                        }
                        catch
                        {
                            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                            throw;
                        }
                    }
                    scope.Commit(failures);
                }
                catch
                {
                    if (scope.IsActive) scope.Cancel();
                    throw;
                }
                stairs = d.GetElement(stairsId) as Stairs;
            }
            if (failures.Errors.Count > 0) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, string.Join("; ", failures.Errors));
            if (stairs is not { IsValidObject: true } done) throw Mismatch("the stair no longer exists");
            if (CommandReadbackV2.Stairs(baseLevel.UniqueId, topLevel.UniqueId, args.Width.Millimetres, requestedStart, requestedEnd, Observe(done)) is { } mismatch) throw Mismatch(mismatch);
            var railings = Uids(d, done.GetAssociatedRailings()).ToArray();
            var created = CommandRulesV2.Created(done.UniqueId, Uids(d, done.GetStairsRuns()), railings);
            var response = CommandResponses.Success(request, created, Array.Empty<string>(), CommandReadbackV2.StairsJson(Observe(done), railings), failures.Warnings.ToArray());
            return Persist(d, request, store, group, response);
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }
}
