using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Bim.Core;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// P2-01 query.snapshot Revit collector. Read-only: no transaction, no receipt (queries never touch the receipt store,
// see CommandExecutor.Execute). It only reads Revit facts, converts lengths to millimetres with UnitUtils (Mm) and
// fills the neutral records of core Mirror.cs; MirrorSnapshotMapper (unit-tested) decides every mapping, id and order.
// Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in Revit.
//
// Revit 2025 API used (https://www.revitapidocs.com/2025/):
//   FilteredElementCollector.OfClass(Wall / Floor / FamilyInstance) / OfCategory(OST_Doors / OST_Windows / OST_Rooms)
//   Wall.Location (LocationCurve: Line or other Curve), Wall.Width, Wall.IsStackedWallMember, Wall.WallType.Kind,
//     WALL_USER_HEIGHT_PARAM, WALL_KEY_REF_PARAM (WallLocationLine.WallCenterline), WALL_BASE_OFFSET
//   Element.DesignOption (DesignOption.IsPrimary), Element.DemolishedPhaseId
//   FamilyInstance.Host / Location (LocationPoint) / Symbol, FAMILY_WIDTH_PARAM / DOOR_WIDTH / WINDOW_WIDTH,
//     FAMILY_HEIGHT_PARAM / DOOR_HEIGHT / WINDOW_HEIGHT, INSTANCE_SILL_HEIGHT_PARAM
//   Room.Area, SpatialElement.GetBoundarySegments(SpatialElementBoundaryOptions{Finish}), BoundarySegment.GetCurve /
//     ElementId, Level.Elevation, ProjectInformation.UniqueId
// Elements in a secondary design option are skipped (main model and primary options only); demolished elements are
// observed with Demolished so the mapper lists them as unmapped. The count is checked against
// MirrorSnapshotMapper.MaxElements while collecting, so a huge model fails snapshot_too_large early.
// Unverified in Revit (owner session required): that a door's LocationPoint lies on (or projects onto) its host wall's
// location line, that WALL_USER_HEIGHT_PARAM reads the actual height of top-constrained walls, and which width/height
// parameter the loaded door and window families expose.
internal static partial class CommandExecutor
{
    static string QueryMirror(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        EmptyQueryArgs when envelope.Command == "query.snapshot" => MirrorSnapshotMapper.Map(MirrorObserve(d, envelope)).ToJson(),
        // P1-02 element queries (CommandsExecutorQuery.cs).
        _ => QueryElements(d, envelope),
    };

    static MirrorPoint MirrorAt(XYZ point) => new(Mm(point.X), Mm(point.Y));

    static string? MirrorUid(Document d, ElementId id) =>
        id == ElementId.InvalidElementId ? null : d.GetElement(id)?.UniqueId is { Length: > 0 } uid ? uid : null;

    // First readable length (millimetres) among the parameters, on the instance and then on its type.
    static double? MirrorLength(FamilyInstance instance, params BuiltInParameter[] parameters)
    {
        foreach (var source in new RevitElement?[] { instance, instance.Symbol })
        {
            if (source is null) continue;
            foreach (var parameter in parameters)
                if (LengthParameter(source, parameter) is { } value) return value;
        }
        return null;
    }

    // Main model and primary design options only.
    static bool MirrorVisible(RevitElement element) => element.DesignOption is not { IsPrimary: false };
    static bool MirrorDemolished(RevitElement element) => element.DemolishedPhaseId != ElementId.InvalidElementId;

    static MirrorObservation MirrorObserve(Document d, RevitCommandEnvelope envelope)
    {
        var levels = Levels(d).Select(level => new MirrorObservedLevel(level.UniqueId, level.Name, Mm(level.Elevation))).ToArray();
        MirrorSnapshotMapper.CheckSize(levels.Length);
        var elements = new List<MirrorObservedElement>();
        void Add(MirrorObservedElement element)
        {
            elements.Add(element);
            MirrorSnapshotMapper.CheckSize(levels.Length + elements.Count);
        }

        foreach (var wall in new FilteredElementCollector(d).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
        {
            // A stacked wall's members are walls of their own; the stack itself is reported (unmapped), its members are not.
            if (wall.IsStackedWallMember || !MirrorVisible(wall)) continue;
            var keyRef = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
            var observed = new MirrorObservedElement(MirrorCategories.Wall, wall.UniqueId)
            {
                LevelUid = MirrorUid(d, wall.LevelId),
                HeightMm = LengthParameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM),
                ThicknessMm = Mm(wall.Width),
                WallKind = wall.WallType?.Kind.ToString(),
                LocationLineIsCenterline = keyRef is { HasValue: true, StorageType: StorageType.Integer } ? keyRef.AsInteger() == (int)WallLocationLine.WallCenterline : null,
                BaseOffsetMm = LengthParameter(wall, BuiltInParameter.WALL_BASE_OFFSET),
                Demolished = MirrorDemolished(wall),
            };
            if (wall.Location is LocationCurve { Curve: { } curve })
                observed = curve is Line line
                    ? observed with { Start = MirrorAt(line.GetEndPoint(0)), End = MirrorAt(line.GetEndPoint(1)) }
                    : observed with { Curved = true };
            Add(observed);
        }

        foreach (var (category, builtIn, width, height) in new[]
        {
            (MirrorCategories.Door, BuiltInCategory.OST_Doors, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.DOOR_HEIGHT),
            (MirrorCategories.Window, BuiltInCategory.OST_Windows, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.WINDOW_HEIGHT),
        })
        {
            foreach (var instance in new FilteredElementCollector(d).OfClass(typeof(FamilyInstance)).OfCategory(builtIn).Cast<FamilyInstance>().Where(MirrorVisible))
            {
                Add(new MirrorObservedElement(category, instance.UniqueId)
                {
                    LevelUid = MirrorUid(d, instance.LevelId),
                    HostUid = instance.Host?.UniqueId,
                    Location = instance.Location is LocationPoint point ? MirrorAt(point.Point) : null,
                    WidthMm = MirrorLength(instance, BuiltInParameter.FAMILY_WIDTH_PARAM, width),
                    HeightMm = MirrorLength(instance, BuiltInParameter.FAMILY_HEIGHT_PARAM, height),
                    SillMm = LengthParameter(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM),
                    Demolished = MirrorDemolished(instance),
                });
            }
        }

        foreach (var floor in new FilteredElementCollector(d).OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>().Where(MirrorVisible))
            Add(new MirrorObservedElement(MirrorCategories.Floor, floor.UniqueId) { LevelUid = MirrorUid(d, floor.LevelId), Demolished = MirrorDemolished(floor) });

        var finish = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        foreach (var room in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().Where(MirrorVisible))
        {
            IReadOnlyList<MirrorLoop>? loops = null;
            if (room.Area > 0 && room.GetBoundarySegments(finish) is { Count: > 0 } boundary)
            {
                loops = boundary.Select(segments =>
                {
                    // GetCurve once per segment.
                    var curves = segments.Select(segment => (Curve: segment.GetCurve(), Uid: MirrorUid(d, segment.ElementId) ?? "")).ToArray();
                    return new MirrorLoop(curves.Select(item => MirrorAt(item.Curve.GetEndPoint(0))).ToArray(), curves.Any(item => item.Curve is not Line), curves.Select(item => item.Uid).ToArray());
                }).ToArray();
            }
            Add(new MirrorObservedElement(MirrorCategories.Room, room.UniqueId) { LevelUid = MirrorUid(d, room.LevelId), Loops = loops, Demolished = MirrorDemolished(room) });
        }

        // Admission already checked that the active document's ProjectInformation UniqueId equals the envelope's.
        return new MirrorObservation(d.ProjectInformation?.UniqueId ?? envelope.Document.UniqueId, levels, elements);
    }
}
