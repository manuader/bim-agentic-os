using System.Globalization;
namespace Bim.Core;

// P4-04 default documentation set derived from a BIM-IR project, input to DocRegeneration (P4-03). Vendor-free, BCL only,
// no BIM-IR or persisted format change. Contract: docs/contracts/module-contracts.md, "P4-04".
//
// Keys come from BIM-IR ids only (never from geometry, names or element order), so edits that keep the set of levels,
// spaces and doors keep every key: "plan-<level:N>", "sheet-<level:N>", "place-plan-<level:N>", "tag-room-<space:N>",
// "tag-door-<door:N>", "schedule-doors", "schedule-rooms", "sheet-schedules", "place-schedule-doors",
// "place-schedule-rooms". Every list is sorted by key (ordinal); the same project always gives the same set.
public static class DocDefaultsCodes
{
    // All three are InvalidOperationException, as DocRegeneration.Full's invalid_ir. Null elements, element, kind,
    // geometry or dependencies, or a project Compiler.Validate rejects.
    public const string InvalidIr = "invalid_ir";
    // A level whose geometry is not exactly [elevation].
    public const string LevelInvalid = "doc_defaults_level_invalid";
    public const string NoLevel = "doc_defaults_no_level";
}

public static class DocDefaults
{
    public const int PlanScale = 100;
    public const string DoorSchedule = "schedule-doors";
    public const string RoomSchedule = "schedule-rooms";
    public const string SchedulesSheet = "sheet-schedules";

    static string K(Guid id) => id.ToString("N");
    static string D(Guid id) => id.ToString("D");

    // Levels are numbered 1..n by (elevation, id): plan "Level i Plan", sheet "A1" + i (two digits at least) "Level i";
    // inserting or raising a level past another renumbers names and sheet numbers (keys stay).
    // A space or absolute door is tagged in the plan of the level it stands on: its first dependency that is a level,
    // else the level of its first wall dependency that has one. A hosted door [offset, width] is tagged only when it has
    // exactly one wall dependency (its host; DocRegeneration refuses none or several), in the plan of that wall's level,
    // else its own first level dependency. An element on no level, or a hosted door without one host, is not tagged (it
    // is still scheduled). Bound (when present, the first by id, ordinal over the "D" form): a plan view to a BIM-IR
    // "plan" element whose first level dependency is its level; a level sheet, levels taken by id, to a BIM-IR "sheet"
    // element depending on that plan element and not bound to an earlier level; the room (door) schedule to a BIM-IR
    // "schedule" element whose dependencies are all spaces (doors), at least one.
    public static DocumentationSet From(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Elements is null || project.Elements.Any(element => element is null || element.GeometryMetres is null || element.Dependencies is null || element.Kind is null))
            throw new InvalidOperationException(DocDefaultsCodes.InvalidIr);
        if (Compiler.Validate(project).Length > 0) throw new InvalidOperationException(DocDefaultsCodes.InvalidIr);
        var elements = project.Elements.ToDictionary(element => element.Id);
        var byId = project.Elements.OrderBy(element => D(element.Id), StringComparer.Ordinal).ToArray();
        var levels = byId.Where(element => element.Kind == "level").ToArray();
        if (levels.Any(level => level.GeometryMetres.Length != 1)) throw new InvalidOperationException(DocDefaultsCodes.LevelInvalid);
        if (levels.Length == 0) throw new InvalidOperationException(DocDefaultsCodes.NoLevel);
        var ordered = levels.OrderBy(level => level.GeometryMetres[0]).ThenBy(level => D(level.Id), StringComparer.Ordinal).ToArray();

        Guid? DirectLevel(Element element)
        {
            foreach (var dependency in element.Dependencies)
                if (elements.TryGetValue(dependency, out var level) && level.Kind == "level") return level.Id;
            return null;
        }
        Guid? LevelOf(Element element)
        {
            if (element.Kind == "door" && element.GeometryMetres.Length == 2)
            {
                var hosts = element.Dependencies.Distinct().Where(dependency => elements.TryGetValue(dependency, out var wall) && wall.Kind == "wall").ToArray();
                if (hosts.Length != 1) return null;
                return DirectLevel(elements[hosts[0]]) ?? DirectLevel(element);
            }
            if (DirectLevel(element) is Guid direct) return direct;
            foreach (var dependency in element.Dependencies)
                if (elements.TryGetValue(dependency, out var wall) && wall.Kind == "wall" && DirectLevel(wall) is Guid level) return level;
            return null;
        }
        bool Schedules(Element schedule, string kind) => schedule.Dependencies.Length > 0
            && schedule.Dependencies.All(dependency => elements.TryGetValue(dependency, out var target) && target.Kind == kind);

        // Bindings by level id, so a BIM-IR sheet depending on several plans is bound once and elevation edits keep them.
        var planBound = new Dictionary<Guid, Guid?>();
        var sheetBound = new Dictionary<Guid, Guid?>();
        var usedSheets = new HashSet<Guid>();
        foreach (var level in levels)
        {
            var planElement = byId.FirstOrDefault(element => element.Kind == "plan" && DirectLevel(element) == level.Id);
            var sheetElement = planElement is null ? null : byId.FirstOrDefault(element => element.Kind == "sheet" && !usedSheets.Contains(element.Id) && element.Dependencies.Contains(planElement.Id));
            if (sheetElement is not null) usedSheets.Add(sheetElement.Id);
            planBound[level.Id] = planElement?.Id;
            sheetBound[level.Id] = sheetElement?.Id;
        }

        var views = new List<DocView>();
        var sheets = new List<DocSheet>();
        var placements = new List<DocPlacement>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var level = ordered[i];
            var number = (i + 1).ToString("00", CultureInfo.InvariantCulture);
            var label = "Level " + (i + 1).ToString(CultureInfo.InvariantCulture);
            views.Add(new DocView("plan-" + K(level.Id), "floor_plan", new[] { level.Id }, label + " Plan", PlanScale, 0, planBound[level.Id]));
            sheets.Add(new DocSheet("sheet-" + K(level.Id), "A1" + number, label, null, sheetBound[level.Id]));
            placements.Add(new DocPlacement("place-plan-" + K(level.Id), "sheet-" + K(level.Id), "plan-" + K(level.Id), 400, 300));
        }

        var tags = new List<DocTag>();
        foreach (var element in byId)
        {
            var prefix = element.Kind switch { "space" => "tag-room-", "door" => "tag-door-", _ => null };
            if (prefix is null || LevelOf(element) is not Guid level) continue;
            tags.Add(new DocTag(prefix + K(element.Id), "plan-" + K(level), element.Id));
        }

        var doorBound = byId.FirstOrDefault(element => element.Kind == "schedule" && Schedules(element, "door"))?.Id;
        var roomBound = byId.FirstOrDefault(element => element.Kind == "schedule" && Schedules(element, "space"))?.Id;
        var schedules = new List<DocSchedule>
        {
            new(DoorSchedule, "door", "door", new[] { "Mark", "Width" }, "Mark", "Door Schedule", doorBound),
            new(RoomSchedule, "space", "room", new[] { "Number", "Name", "Area" }, "Number", "Room Schedule", roomBound),
        };
        sheets.Add(new DocSheet(SchedulesSheet, "A900", "Schedules"));
        placements.Add(new DocPlacement("place-schedule-doors", SchedulesSheet, DoorSchedule, 200, 400));
        placements.Add(new DocPlacement("place-schedule-rooms", SchedulesSheet, RoomSchedule, 600, 400));

        return new DocumentationSet(
            views.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            tags.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            Array.Empty<DocDimension>(),
            schedules.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            sheets.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            placements.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
    }
}
