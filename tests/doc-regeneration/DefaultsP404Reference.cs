using Bim.Core;
using System.Globalization;

// P4-06 regression pin: a frozen copy of the P4-04 DocDefaults.From (origin/main b0bcd22, before window tags and
// dimension chains). For a project without windows the current DocDefaults.From must stay byte-identical to it.
// Never edit this copy to follow the implementation.
static class DocDefaultsP404Reference
{
    static string K(Guid id) => id.ToString("N");
    static string D(Guid id) => id.ToString("D");

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
            views.Add(new DocView("plan-" + K(level.Id), "floor_plan", new[] { level.Id }, label + " Plan", 100, 0, planBound[level.Id]));
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
            new("schedule-doors", "door", "door", new[] { "Mark", "Width" }, "Mark", "Door Schedule", doorBound),
            new("schedule-rooms", "space", "room", new[] { "Number", "Name", "Area" }, "Number", "Room Schedule", roomBound),
        };
        sheets.Add(new DocSheet("sheet-schedules", "A900", "Schedules"));
        placements.Add(new DocPlacement("place-schedule-doors", "sheet-schedules", "schedule-doors", 200, 400));
        placements.Add(new DocPlacement("place-schedule-rooms", "sheet-schedules", "schedule-rooms", 600, 400));

        return new DocumentationSet(
            views.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            tags.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            Array.Empty<DocDimension>(),
            schedules.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            sheets.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            placements.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
    }
}
