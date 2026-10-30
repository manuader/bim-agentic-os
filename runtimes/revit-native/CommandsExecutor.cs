using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// C03 (ADR0009) Revit executor for catalogue v1. Only invoked by App.Execute on Revit's serialized ExternalEvent
// thread, before any transaction exists. Every decision that needs no Revit object lives in CommandsContract.cs and
// CommandsReadback.cs (unit-tested); this file only reads Revit facts, calls the Revit API and converts units.
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit (C04).
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   Wall.Create(Document, Curve, ElementId, ElementId, double, double, bool, bool)  0ce4c555-4cee-f5fd-2e84-43cacf34ac5c
//   Arc.Create(XYZ center, double radius, double startAngle, double endAngle, XYZ xAxis, XYZ yAxis)  bb8e759b-a0a6-ce55-59df-529f228d5c06
//     (angles in radians, start < end, radius at most 30000 ft)
//   Arc.Create(XYZ end0, XYZ end1, XYZ pointOnArc), Line.CreateBound(XYZ, XYZ)
//   Floor.Create(Document, IList<CurveLoop>, ElementId, ElementId)  a9c74a9f-46eb-a1b7-608e-2039f06be579 (2022+)
//   BoundaryValidation.IsValidHorizontalBoundary(IList<CurveLoop>)  4bd740a2-fa9e-24c5-eb53-c0dac642f1e9
//   Level.ProjectElevation (elevation relative to the project origin, whatever Elevation Base says)
//   Document.Create.NewFamilyInstance(XYZ, FamilySymbol, Element host, Level, StructuralType) and
//   Document.Create.NewRoom(Level, UV): same calls as NativeRoomFixture.cs, which ran in Revit 2025.
//   FailureHandlingOptions.SetFailuresPreprocessor / SetClearAfterRollback, FailuresAccessor.GetFailureMessages /
//   DeleteWarning (FailuresAccessor dea68b06-a061-fc05-d814-db741f2e7f14).
internal static class CommandExecutor
{
    public static string Run(UIApplication app, Request request)
    {
        try { return Execute(app, request); }
        catch (RevitCommandException error) { return CommandResponses.Failure(error); }
        catch (Exception error) { return CommandResponses.Failure(error); }
    }

    static string Execute(UIApplication app, Request r)
    {
        // Raw text of the Command member as received: JsonElement keeps the original bytes.
        var request = CommandWire.Bind(r.DocumentPath, r.UniqueId, r.Metres, r.Batch is not null || r.Checkpoint is not null, r.Command?.GetRawText());
        var envelope = request.Envelope;
        var d = app.ActiveUIDocument?.Document ?? throw new RevitCommandException(RevitCommandCodes.DocumentConflict, "document.path", "no active document");
        CommandAdmission.Check(envelope, Facts(d), Policy, RevitFileSystemProbe.Real);
        // Queries never read or write the receipt store.
        if (!envelope.Spec.Writes) return CommandResponses.Success(request, Array.Empty<string>(), Array.Empty<string>(), Query(d, envelope), Array.Empty<string>()).ToJson();
        var store = ReadReceipts(d);
        switch (CommandReceiptStore.Decide(store.Receipts, envelope.OperationId, request.RequestSha256))
        {
            case CommandReceiptAction.Replay: return CommandReceiptStore.ReplayJson(store.Receipts[envelope.OperationId]);
            case CommandReceiptAction.Conflict: throw CommandReceiptStore.Conflict(envelope.OperationId);
        }
        CommandReceiptStore.EnsureRoom(store.Receipts);
        var plan = envelope.Args switch
        {
            WallCreateArgs wall => WallPlan(d, wall),
            OpeningPlaceArgs opening => OpeningPlan(d, opening),
            FloorCreateArgs floor => FloorPlan(d, floor),
            RoomCreateArgs room => RoomPlan(d, room),
            _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", envelope.Command),
        };
        return Write(d, request, store, plan);
    }

    // The repository allowlist found from the add-in's own location (staged under .bim/native-runs/<run>/addin).
    internal static CommandDocumentFacts Facts(Document d) =>
        new(d.PathName, d.ProjectInformation?.UniqueId, d.IsReadOnly, d.IsFamilyDocument, d.IsWorkshared, d.IsModifiable);

    internal static RevitCommandPolicy Policy()
    {
        var directory = Path.GetDirectoryName(typeof(CommandExecutor).Assembly.Location);
        return string.IsNullOrEmpty(directory) ? RevitCommandPolicy.None : RevitCommandPolicy.ForRepository(directory);
    }

    // ---- Units: millimetres at the boundary, Revit internal units inside, always through UnitUtils ----
    static double U(double millimetres) => UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters);
    static double Mm(double internalLength) => LengthUnits.NormalizeMillimetres(UnitUtils.ConvertFromInternalUnits(internalLength, UnitTypeId.Millimeters));
    static double Mm2(double internalArea) => UnitUtils.ConvertFromInternalUnits(internalArea, UnitTypeId.SquareMeters) * 1e6;
    static PlanPoint Plan(XYZ point) => new(Mm(point.X), Mm(point.Y));
    static XYZ At(Point2 point, double z) => new(U(point.X.Millimetres), U(point.Y.Millimetres), z);
    static double? LengthParameter(RevitElement element, BuiltInParameter parameter) =>
        element.get_Parameter(parameter) is { HasValue: true, StorageType: StorageType.Double } value ? Mm(value.AsDouble()) : null;
    static string UniqueIdOf(Document d, ElementId id) => d.GetElement(id)?.UniqueId ?? "";

    // ---- Collections and reference resolution (rules in CommandReferences) ----
    static Level[] Levels(Document d) => new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>().ToArray();
    static WallType[] WallTypes(Document d) => new FilteredElementCollector(d).OfClass(typeof(WallType)).Cast<WallType>().ToArray();
    static FloorType[] FloorTypes(Document d) => new FilteredElementCollector(d).OfClass(typeof(FloorType)).Cast<FloorType>().Where(type => CommandRules.OffersFloorType(type.IsFoundationSlab)).ToArray();
    static FamilySymbol[] Symbols(Document d, string category) => new FilteredElementCollector(d).OfClass(typeof(FamilySymbol))
        .OfCategory(category == "door" ? BuiltInCategory.OST_Doors : BuiltInCategory.OST_Windows).Cast<FamilySymbol>().ToArray();
    static bool WallHosted(FamilySymbol symbol) => symbol.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted;

    static T Resolve<T>(T[] items, Func<T, CommandCandidate> candidate, string reference, string field, string kind) =>
        items[CommandReferences.Resolve(reference, items.Select(candidate).ToArray(), field, kind)];
    static Level ResolveLevel(Document d, string reference, string field) => Resolve(Levels(d), level => new CommandCandidate(level.UniqueId, level.Name), reference, field, "level");

    // ---- Queries: no transaction, no write ----
    static string Query(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        EmptyQueryArgs when envelope.Command == "query.levels" =>
            CommandReadback.LevelsJson(Levels(d).Select(level => new LevelRow(level.Name, level.UniqueId, Mm(level.Elevation), Mm(level.ProjectElevation)))),
        EmptyQueryArgs when envelope.Command == "query.wall_types" =>
            CommandReadback.WallTypesJson(WallTypes(d).Select(type => new WallTypeRow(type.Name, type.UniqueId, type.Kind.ToString(), WallTypeWidth(type)))),
        EmptyQueryArgs when envelope.Command == "query.floor_types" =>
            CommandReadback.FloorTypesJson(FloorTypes(d).Select(type => new FloorTypeRow(type.Name, type.UniqueId, type.GetCompoundStructure() is { } layers ? Mm(layers.GetWidth()) : null))),
        FamilySymbolQueryArgs symbols =>
            CommandReadback.SymbolsJson(symbols.Category, Symbols(d, symbols.Category).Select(symbol => new SymbolRow(symbol.Family.Name, symbol.Name, symbol.UniqueId, symbol.IsActive, WallHosted(symbol)))),
        _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", envelope.Command),
    };

    static double? WallTypeWidth(WallType type) => CommandRules.ReportsWallTypeWidth(type.Kind.ToString()) ? Mm(type.Width) : null;

    // ---- Writes ----
    // Apply runs inside the one write Transaction; Check returns a readback mismatch (or null) and runs before the
    // transaction commits and again after Revit's failure processing; Readback is the reply's readback JSON.
    sealed record WritePlan(Action Apply, Func<string?> Check, Func<string> Readback, Func<IReadOnlyList<string>> Created, Func<IReadOnlyList<string>> Modified);

    static string Write(Document d, CommandRequest request, ReceiptStore store, WritePlan plan)
    {
        var envelope = request.Envelope;
        var failures = new CommandFailures();
        using var group = new TransactionGroup(d, "BIM Agentic OS " + envelope.Command + " " + envelope.OperationId);
        if (group.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction group did not start");
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
                    plan.Apply();
                    d.Regenerate();
                    if (plan.Check() is { } before) throw Mismatch(before);
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
            // Failure processing may delete or change elements: read back again before the receipt is persisted.
            if (plan.Check() is { } after) throw Mismatch(after);
            var response = CommandResponses.Success(request, plan.Created(), plan.Modified(), plan.Readback(), failures.Warnings.ToArray());
            store.Receipts[envelope.OperationId] = CommandReceiptStore.Record(request, response);
            using (var receiptTx = new Transaction(d, "BIM Agentic OS command receipt"))
            {
                if (receiptTx.Start() != TransactionStatus.Started) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "receipt transaction did not start");
                try
                {
                    var storage = store.Storage ?? DataStorage.Create(d);
                    var entity = new Entity(ReceiptSchema());
                    entity.Set(CommandReceiptStore.PayloadField, CommandReceiptStore.SerializeBounded(store.Receipts));
                    storage.SetEntity(entity);
                    if (receiptTx.Commit() != TransactionStatus.Committed) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "receipt transaction did not commit");
                }
                catch
                {
                    if (receiptTx.GetStatus() == TransactionStatus.Started) receiptTx.RollBack();
                    throw;
                }
            }
            if (group.Assimilate() != TransactionStatus.Committed) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "transaction group did not commit");
            return response.ToJson();
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
    }

    static RevitCommandException Mismatch(string detail) => new(RevitCommandCodes.TransactionFailed, null, "readback mismatch, rolled back: " + detail);

    static WritePlan WallPlan(Document d, WallCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var type = Resolve(WallTypes(d), wallType => new CommandCandidate(wallType.UniqueId, wallType.Name), args.Type, "args.type", "wall type");
        CommandRules.CheckWallTypeForCreate(type.Kind.ToString());
        var expected = CommandGeometry.Wall(args.Curve);
        Wall? wall = null;
        ObservedWall Observe(Wall created)
        {
            var curve = (created.Location as LocationCurve)?.Curve ?? throw Mismatch("wall has no location curve");
            var arc = curve as Arc;
            return new ObservedWall(created.UniqueId, UniqueIdOf(d, created.LevelId), created.WallType.UniqueId, arc is not null,
                Plan(curve.GetEndPoint(0)), Plan(curve.GetEndPoint(1)), Mm(curve.Length), arc is null ? null : Mm(arc.Radius), arc is null ? null : Plan(arc.Center),
                LengthParameter(created, BuiltInParameter.WALL_USER_HEIGHT_PARAM), LengthParameter(created, BuiltInParameter.WALL_BASE_OFFSET),
                created.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT) is { HasValue: true, StorageType: StorageType.Integer } flag ? flag.AsInteger() != 0 : null);
        }
        return new WritePlan(
            () =>
            {
                // The base line lies in the level's plane; Base Offset is the separate offset argument.
                var z = level.ProjectElevation;
                Curve curve = args.Curve switch
                {
                    LineWallCurve line => Line.CreateBound(At(line.Start, z), At(line.End, z)),
                    ArcThroughWallCurve arc => Arc.Create(At(arc.Start, z), At(arc.End, z), At(arc.Through, z)),
                    ArcCenterWallCurve arc => Arc.Create(At(arc.Center, z), U(arc.Radius.Millimetres), arc.StartAngle.Radians, arc.EndAngle.Radians, XYZ.BasisX, XYZ.BasisY),
                    _ => throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.curve", "unsupported curve"),
                };
                wall = Wall.Create(d, curve, type.Id, level.Id, U(args.Height.Millimetres), U(args.BaseOffset.Millimetres), false, args.Structural);
            },
            () => wall is { IsValidObject: true } created
                ? CommandReadback.Wall(expected, level.UniqueId, type.UniqueId, args.Height.Millimetres, args.BaseOffset.Millimetres, args.Structural, Observe(created))
                : "the wall no longer exists",
            () => CommandReadback.WallJson(Observe(wall!)),
            () => new[] { wall!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan OpeningPlan(Document d, OpeningPlaceArgs args)
    {
        var host = d.GetElement(args.HostWallUid) as Wall ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.host_wall_uid", "no wall with UniqueId " + args.HostWallUid);
        var hostCurve = (host.Location as LocationCurve)?.Curve ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.host_wall_uid", "the host wall has no location curve");
        // WALL_KEY_REF_PARAM holds the WallLocationLine; only Wall Centerline puts the location curve in the wall's middle.
        var centerline = host.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM) is { HasValue: true, StorageType: StorageType.Integer } keyRef && keyRef.AsInteger() == (int)WallLocationLine.WallCenterline;
        CommandRules.CheckOpeningHost(host.WallType.Kind.ToString(), centerline);
        var level = d.GetElement(host.LevelId) as Level ?? throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the host wall has no base level");
        var symbol = Resolve(Symbols(d, args.Category), candidate => new CommandCandidate(candidate.UniqueId, candidate.Name, CommandReferences.Qualified(candidate.Family.Name, candidate.Name)), args.Symbol, "args.symbol", args.Category + " family type");
        if (!WallHosted(symbol)) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.symbol", "family type is not wall-hosted");
        // position_along_mm from the host curve start -> normalized parameter -> Curve.Evaluate(parameter, normalized: true).
        var parameter = CommandGeometry.OpeningParameter(Mm(hostCurve.Length), args.PositionAlong.Millimetres, "args.position_along_mm");
        var onCurve = hostCurve.Evaluate(parameter, true);
        var expectedPoint = Plan(onCurve);
        var sill = args.Category == "window" ? args.Sill : null;
        FamilyInstance? instance = null;
        var activated = false;
        ObservedOpening Observe(FamilyInstance created) => new(created.UniqueId, created.Host?.UniqueId, created.Symbol.UniqueId,
            created.Location is LocationPoint point ? Plan(point.Point) : null, LengthParameter(created, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM));
        return new WritePlan(
            () =>
            {
                if (!symbol.IsActive) { symbol.Activate(); activated = true; d.Regenerate(); }
                var location = new XYZ(onCurve.X, onCurve.Y, level.ProjectElevation + (sill is { } height ? U(height.Millimetres) : 0));
                instance = d.Create.NewFamilyInstance(location, symbol, host, level, StructuralType.NonStructural);
                if (sill is { } requested)
                {
                    var sillParameter = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                    if (sillParameter is null || sillParameter.IsReadOnly || !sillParameter.Set(U(requested.Millimetres)))
                        throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.sill_mm", "the sill height of this window cannot be set");
                }
            },
            () => instance is { IsValidObject: true } created
                ? CommandReadback.Opening(host.UniqueId, symbol.UniqueId, expectedPoint, sill?.Millimetres, Observe(created))
                : "the " + args.Category + " no longer exists",
            () => CommandReadback.OpeningJson(args.Category, Observe(instance!)),
            () => new[] { instance!.UniqueId },
            () => activated ? new[] { host.UniqueId, symbol.UniqueId } : new[] { host.UniqueId });
    }

    static WritePlan FloorPlan(Document d, FloorCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        var type = Resolve(FloorTypes(d), floorType => new CommandCandidate(floorType.UniqueId, floorType.Name), args.Type, "args.type", "floor type");
        // Floor.Create ignores the loop elevation; the loop is drawn in the level plane anyway.
        var z = level.ProjectElevation;
        var points = args.Boundary.Select(point => At(point, z)).ToArray();
        var loop = new CurveLoop();
        for (var i = 0; i < points.Length; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Length]));
        var loops = new List<CurveLoop> { loop };
        if (!BoundaryValidation.IsValidHorizontalBoundary(loops))
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.boundary", "Revit BoundaryValidation.IsValidHorizontalBoundary refused the outline");
        var expectedArea = CommandGeometry.PolygonAreaMm2(args.Boundary);
        Floor? floor = null;
        ObservedFloor Observe(Floor created) => new(created.UniqueId, UniqueIdOf(d, created.LevelId), created.FloorType.UniqueId,
            created.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED) is { HasValue: true, StorageType: StorageType.Double } area ? Mm2(area.AsDouble()) : null);
        return new WritePlan(
            () => floor = Floor.Create(d, loops, type.Id, level.Id),
            () => floor is { IsValidObject: true } created ? CommandReadback.Floor(level.UniqueId, type.UniqueId, expectedArea, Observe(created)) : "the floor no longer exists",
            () => CommandReadback.FloorJson(Observe(floor!)),
            () => new[] { floor!.UniqueId },
            Array.Empty<string>);
    }

    static WritePlan RoomPlan(Document d, RoomCreateArgs args)
    {
        var level = ResolveLevel(d, args.Level, "args.level");
        Room? room = null;
        // Room.Name may read back as "name number"; ROOM_NAME holds the name alone.
        ObservedRoom Observe(Room created) => new(created.UniqueId, UniqueIdOf(d, created.LevelId), created.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(), created.Number, Mm2(created.Area));
        return new WritePlan(
            () =>
            {
                room = d.Create.NewRoom(level, new UV(U(args.Point.X.Millimetres), U(args.Point.Y.Millimetres)));
                if (args.Name is { } name) room.Name = name;
                if (args.Number is { } number) room.Number = number;
            },
            () => room is { IsValidObject: true } created ? CommandReadback.Room(level.UniqueId, args.Name, args.Number, Observe(created)) : "the room no longer exists",
            () => CommandReadback.RoomJson(Observe(room!)),
            () => new[] { room!.UniqueId },
            Array.Empty<string>);
    }

    // ---- Receipt store: one DataStorage with its own schema (CommandReceiptStore.SchemaGuid) ----
    sealed record ReceiptStore(DataStorage? Storage, Dictionary<string, StoredCommandReceipt> Receipts);

    static ReceiptStore ReadReceipts(Document d)
    {
        var schema = Schema.Lookup(CommandReceiptStore.SchemaGuid);
        if (schema is null) return new ReceiptStore(null, CommandReceiptStore.Parse(null));
        var stores = new FilteredElementCollector(d).OfClass(typeof(DataStorage)).Cast<DataStorage>().Where(storage => storage.GetEntity(schema).IsValid()).ToArray();
        if (stores.Length > 1) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "receipt store ambiguous: " + stores.Length + " DataStorage elements");
        if (stores.Length == 0) return new ReceiptStore(null, CommandReceiptStore.Parse(null));
        return new ReceiptStore(stores[0], CommandReceiptStore.Parse(stores[0].GetEntity(schema).Get<string>(CommandReceiptStore.PayloadField)));
    }

    static Schema ReceiptSchema()
    {
        var existing = Schema.Lookup(CommandReceiptStore.SchemaGuid);
        if (existing != null) return existing;
        var builder = new SchemaBuilder(CommandReceiptStore.SchemaGuid);
        builder.SetSchemaName(CommandReceiptStore.SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.AddSimpleField(CommandReceiptStore.PayloadField, typeof(string));
        return builder.Finish();
    }
}

// Collects Revit's failure messages for the write transaction: warnings are reported in the reply and removed so no
// modal dialog blocks the ExternalEvent; any error rolls the transaction back and becomes transaction_failed.
sealed class CommandFailures : IFailuresPreprocessor
{
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        var warnings = new List<FailureMessageAccessor>();
        var errors = false;
        foreach (var failure in failuresAccessor.GetFailureMessages())
        {
            var text = failure.GetDescriptionText();
            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                Warnings.Add(text);
                warnings.Add(failure);
            }
            else
            {
                Errors.Add(text);
                errors = true;
            }
        }
        if (errors) return FailureProcessingResult.ProceedWithRollBack;
        foreach (var warning in warnings) failuresAccessor.DeleteWarning(warning);
        return FailureProcessingResult.Continue;
    }
}
