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
//   Wall.Location (LocationCurve: Line or other Curve), Wall.Width, Wall.IsStackedWallMember, WALL_USER_HEIGHT_PARAM
//   FamilyInstance.Host / Location (LocationPoint) / Symbol, FAMILY_WIDTH_PARAM / DOOR_WIDTH / WINDOW_WIDTH,
//     FAMILY_HEIGHT_PARAM / DOOR_HEIGHT / WINDOW_HEIGHT, INSTANCE_SILL_HEIGHT_PARAM
//   Room.Area, SpatialElement.GetBoundarySegments(SpatialElementBoundaryOptions{Finish}), BoundarySegment.GetCurve /
//     ElementId, Level.ProjectElevation, ProjectInformation.UniqueId
// Unverified in Revit (owner session required): that a door's LocationPoint lies on (or projects onto) its host wall's
// location line, that WALL_USER_HEIGHT_PARAM reads the actual height of top-constrained walls, and which width/height
// parameter the loaded door and window families expose.
internal static partial class CommandExecutor
{
    static string QueryMirror(Document d, RevitCommandEnvelope envelope) => envelope.Args switch
    {
        EmptyQueryArgs when envelope.Command == "query.snapshot" => MirrorSnapshotMapper.Map(MirrorObserve(d, envelope)).ToJson(),
        _ => throw new RevitCommandException(RevitCommandCodes.CommandUnknown, "command", envelope.Command),
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

    static MirrorObservation MirrorObserve(Document d, RevitCommandEnvelope envelope)
    {
        var levels = Levels(d).Select(level => new MirrorObservedLevel(level.UniqueId, level.Name, Mm(level.ProjectElevation))).ToArray();
        var elements = new List<MirrorObservedElement>();

        foreach (var wall in new FilteredElementCollector(d).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
        {
            // A stacked wall's members are walls of their own; the stack itself is reported, its members are not.
            if (wall.IsStackedWallMember) continue;
            var observed = new MirrorObservedElement(MirrorCategories.Wall, wall.UniqueId)
            {
                LevelUid = MirrorUid(d, wall.LevelId),
                HeightMm = LengthParameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM),
                ThicknessMm = Mm(wall.Width),
            };
            if (wall.Location is LocationCurve { Curve: { } curve })
                observed = curve is Line line
                    ? observed with { Start = MirrorAt(line.GetEndPoint(0)), End = MirrorAt(line.GetEndPoint(1)) }
                    : observed with { Curved = true };
            elements.Add(observed);
        }

        foreach (var (category, builtIn, width, height) in new[]
        {
            (MirrorCategories.Door, BuiltInCategory.OST_Doors, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.DOOR_HEIGHT),
            (MirrorCategories.Window, BuiltInCategory.OST_Windows, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.WINDOW_HEIGHT),
        })
        {
            foreach (var instance in new FilteredElementCollector(d).OfClass(typeof(FamilyInstance)).OfCategory(builtIn).Cast<FamilyInstance>())
            {
                elements.Add(new MirrorObservedElement(category, instance.UniqueId)
                {
                    LevelUid = MirrorUid(d, instance.LevelId),
                    HostUid = instance.Host?.UniqueId,
                    Location = instance.Location is LocationPoint point ? MirrorAt(point.Point) : null,
                    WidthMm = MirrorLength(instance, BuiltInParameter.FAMILY_WIDTH_PARAM, width),
                    HeightMm = MirrorLength(instance, BuiltInParameter.FAMILY_HEIGHT_PARAM, height),
                    SillMm = LengthParameter(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM),
                });
            }
        }

        foreach (var floor in new FilteredElementCollector(d).OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>())
            elements.Add(new MirrorObservedElement(MirrorCategories.Floor, floor.UniqueId) { LevelUid = MirrorUid(d, floor.LevelId) });

        var finish = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
        foreach (var room in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
        {
            IReadOnlyList<MirrorLoop>? loops = null;
            if (room.Area > 0 && room.GetBoundarySegments(finish) is { Count: > 0 } boundary)
            {
                loops = boundary.Select(segments => new MirrorLoop(
                    segments.Select(segment => MirrorAt(segment.GetCurve().GetEndPoint(0))).ToArray(),
                    segments.Any(segment => segment.GetCurve() is not Line),
                    segments.Select(segment => MirrorUid(d, segment.ElementId) ?? "").ToArray())).ToArray();
            }
            elements.Add(new MirrorObservedElement(MirrorCategories.Room, room.UniqueId) { LevelUid = MirrorUid(d, room.LevelId), Loops = loops });
        }

        // Admission already checked that the active document's ProjectInformation UniqueId equals the envelope's.
        return new MirrorObservation(d.ProjectInformation?.UniqueId ?? envelope.Document.UniqueId, levels, elements);
    }
}
