using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// P4-06 oracle: DocDefaults.From adds a tag per window (in the plan of the level it stands on, as doors) and, on every
// level plan that has a tagged window, one linear dimension chain per exterior wall line: the collinear walls of a line
// with every other wall endpoint of the level on one side, measured between the two perpendicular walls meeting its
// ends (wall-end references), its dimension line 1 m outside. Keys "tag-window-<window:N>" and
// "dim-wall-<first wall of the line by id:N>". Full regeneration yields only catalogue-valid dimension.create_linear
// and tag.create steps; projects without windows stay byte-identical to the P4-04 defaults; deterministic.
static class DefaultsWindowsTests
{
    static readonly Provenance provenance = new("doc-defaults-windows-tests", "unit");
    static readonly RevitDocumentRef document = new(@"C:\Repo\.bim\native-runs\p4-06\copy.rvt", "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0d");
    static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("doc-defaults-windows:" + name)).Take(16).ToArray());
    static readonly Guid projectId = Id("project");
    static Guid RoomOf(int room) => Id("room" + room.ToString(CultureInfo.InvariantCulture));
    static Guid Role(int room, string role) => RoomCompiler.Id(RoomOf(room), role);
    static Guid Door(int room) => Id("door" + room.ToString(CultureInfo.InvariantCulture));
    static Guid Hosted(int room) => Id("hosted" + room.ToString(CultureInfo.InvariantCulture));
    static readonly Guid hostedWindow = Id("window-hosted");
    static readonly Guid axisWindow = Id("window-axis");
    static string K(Guid id) => id.ToString("N");
    static string Json(object value) => JsonSerializer.Serialize(value);

    static string Fake(string source)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        return hex[..8] + "-" + hex[8..12] + "-" + hex[12..16] + "-" + hex[16..20] + "-" + hex[20..32] + "-" + hex[32..40];
    }

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (ArgumentNullException) { return "null"; }
        catch (ArgumentException error) { return "argument:" + error.Message; }
        catch (InvalidOperationException error) { return "state:" + error.Message; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    // Rooms from RoomCompiler (4 x 3 m clear, 0.2 m walls, room i at x = 10 i on its own level), each with an absolute
    // door on the south wall and a hosted door in the north wall (as the P4-04 oracle). With windows: room 0 gets a
    // hosted window [offset, width] in its east wall, room 1 an absolute window on its north wall axis; room 2 none.
    static Project Build(bool windows, params double[] elevations)
    {
        var elements = new List<Element>();
        for (var room = 0; room < elevations.Length; room++)
        {
            foreach (var element in RoomCompiler.Compile(projectId, 0, new RoomIntent(RoomOf(room), 4, 3, 2.8, 0.2, room * 10, 0, "m", provenance)).Elements)
                elements.Add(element.Kind == "level" ? element with { GeometryMetres = new[] { elevations[room] } } : element);
            var x = room * 10 + 0.1;
            elements.Add(new(Door(room), "door", new[] { x + 1, 0.1, x + 1.9, 0.1, 0, 2.1 }, new[] { Role(room, "south"), Role(room, "level") }, provenance));
            elements.Add(new(Hosted(room), "door", new[] { 1.0, 0.9 }, new[] { Role(room, "north"), Role(room, "level") }, provenance));
        }
        if (windows)
        {
            elements.Add(new(hostedWindow, "window", new[] { 1.0, 1.2 }, new[] { Role(0, "east"), Role(0, "level") }, provenance));
            elements.Add(new(axisWindow, "window", new[] { 11.0, 3.3, 12.2, 3.3, 0.9, 1.2 }, new[] { Role(1, "north"), Role(1, "level") }, provenance));
        }
        return new(projectId, 0, elements.ToArray());
    }
    static RevitBindings Bindings(Project project) => new(document,
        project.Elements.Where(element => element.Kind == "level").ToDictionary(element => element.Id, element => "Level " + element.Id.ToString("N")[..8]),
        Array.Empty<RevitWallTypeBinding>(), null, null);
    static Project Replace(Project project, Guid id, Func<Element, Element> change) => project with { Elements = project.Elements.Select(element => element.Id == id ? change(element) : element).ToArray() };
    static Project Add(Project project, params Element[] added) => project with { Elements = project.Elements.Concat(added).ToArray() };
    static Element Wall(Guid id, Guid level, double ax, double ay, double bx, double by) => new(id, "wall", new[] { ax, ay, bx, by, 3.0, 0.2 }, new[] { level }, provenance);
    static Element Level(Guid id, double elevation) => new(id, "level", new[] { elevation }, Array.Empty<Guid>(), provenance);
    // Moves walls (axis) and spaces (origin) of a project by (dx, dy); hosted openings follow their host.
    static Project Translate(Project project, IEnumerable<Guid> ids, double dx, double dy)
    {
        var moved = ids.ToHashSet();
        return project with
        {
            Elements = project.Elements.Select(element =>
            {
                if (!moved.Contains(element.Id)) return element;
                var g = element.GeometryMetres;
                return element.Kind switch
                {
                    "wall" => element with { GeometryMetres = new[] { g[0] + dx, g[1] + dy, g[2] + dx, g[3] + dy, g[4], g[5] } },
                    "space" => element with { GeometryMetres = new[] { g[0] + dx, g[1] + dy, g[2], g[3] } },
                    _ => element,
                };
            }).ToArray(),
        };
    }
    static IEnumerable<string> Keys(DocumentationSet set) =>
        set.Views.Select(item => item.Key).Concat(set.Tags.Select(item => item.Key)).Concat(set.Dimensions.Select(item => item.Key))
            .Concat(set.Schedules.Select(item => item.Key)).Concat(set.Sheets.Select(item => item.Key)).Concat(set.Placements.Select(item => item.Key));
    static string Pair(Guid a, Guid b) => string.Join("+", new[] { K(a), K(b) }.OrderBy(key => key, StringComparer.Ordinal));
    static string DimensionKey(params Guid[] lineWalls) => "dim-wall-" + K(lineWalls.OrderBy(id => id.ToString("D"), StringComparer.Ordinal).First());

    // Each step, its inputs filled, validated against its catalogue entry's args schema and re-parsed by the protocol.
    static string CatalogueProblem(DocRegenerationPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            var spec = RevitCommandCatalogue.Find(step.Command);
            if (spec is null || !spec.Writes) return step.Key + ": " + step.Command + " is not a catalogue write";
            var ids = step.Inputs.ToDictionary(input => input.Source, input => Fake(input.Source), StringComparer.Ordinal);
            var args = (JsonObject)JsonNode.Parse(step.ArgsJson)!;
            foreach (var input in step.Inputs) args[input.Field["args.".Length..]] = ids[input.Source];
            using var value = JsonDocument.Parse(args.ToJsonString());
            using var schema = JsonDocument.Parse(spec.ArgsSchemaJson);
            if (RevitCommandSchema.Validate(value.RootElement, schema.RootElement, "args") is { } failure) return step.Key + ": schema " + failure.Field + " " + failure.Detail;
            var error = Code(() =>
            {
                var envelope = DocRegeneration.Envelope(plan, step, ids);
                if (envelope.Command != step.Command || envelope.OperationId != step.OperationId) throw new InvalidOperationException("envelope_mismatch");
            });
            if (error is not null) return step.Key + ": " + error;
        }
        return "";
    }

    // The dimension line of a dimension.create_linear step as "x1,y1;x2,y2" in mm, endpoints sorted, or "" when absent.
    static string Line(DocCommandStep step)
    {
        var args = JsonNode.Parse(step.ArgsJson)!;
        if (args["line"] is not JsonObject line) return "";
        string P(string name) => string.Join(",", line[name]!.AsArray().Select(value => Math.Round(value!.GetValue<double>(), 3).ToString("0.###", CultureInfo.InvariantCulture)));
        return string.Join(";", new[] { P("start"), P("end") }.OrderBy(point => point, StringComparer.Ordinal));
    }
    static string Segment(double x1, double y1, double x2, double y2)
    {
        string P(double x, double y) => x.ToString("0.###", CultureInfo.InvariantCulture) + "," + y.ToString("0.###", CultureInfo.InvariantCulture);
        return string.Join(";", new[] { P(x1, y1), P(x2, y2) }.OrderBy(point => point, StringComparer.Ordinal));
    }

    static bool Valid(Project project, DocumentationSet set, out string detail)
    {
        var full = DocRegeneration.Full(project, set, Bindings(project));
        detail = Json(full.Unsupported) + " " + CatalogueProblem(full);
        return full.Unsupported.Count == 0 && full.Steps.Count == full.TotalItems && CatalogueProblem(full).Length == 0;
    }

    public static void Run(Checker check)
    {
        // ---- Regression pin: without windows the set is byte-identical to the frozen P4-04 defaults ----
        var plain = Build(false, 0, 3, 6);
        var looseWindow = new Element(Id("window-loose"), "window", new[] { 1.0, 1.2 }, new[] { Role(0, "level") }, provenance);
        var pinned = new (string Name, Project Project)[]
        {
            ("three rooms with doors", plain),
            ("reversed element order", plain with { Elements = Enumerable.Reverse(plain.Elements).ToArray() }),
            ("a moved wall", Translate(plain, new[] { Role(1, "south") }, 0, -0.5)),
            ("a raised level", Replace(plain, Role(0, "level"), level => level with { GeometryMetres = new[] { 9.0 } })),
            ("levels at equal elevation", Build(false, 0, 0, 6)),
            ("a bare level", new Project(projectId, 0, new[] { Level(Id("level-bare"), 0) })),
            ("a hosted opening", Add(plain, new Element(Id("opening"), "opening", new[] { 1.0, 0.8 }, new[] { Role(2, "east"), Role(2, "level") }, provenance))),
            ("a door on no level", Add(plain, new Element(Id("door-loose"), "door", new[] { 30.0, 0.1, 30.9, 0.1, 0, 2.1 }, Array.Empty<Guid>(), provenance))),
        };
        foreach (var (name, project) in pinned)
        {
            var current = DocDefaults.From(project);
            var frozen = DocDefaultsP404Reference.From(project);
            check(Json(current) == Json(frozen) && Json(DocRegeneration.Full(project, current, Bindings(project))) == Json(DocRegeneration.Full(project, frozen, Bindings(project))),
                "windows: without windows the defaults and their full regeneration are byte-identical to P4-04 (" + name + ")", Json(current.Dimensions) + " " + Json(current.Tags.Count));
        }
        check(DocDefaults.From(plain).Dimensions.Count == 0, "windows: a project without windows has no dimensions");

        // ---- Window tags ----
        var basis = Build(true, 0, 3, 6);
        var set = DocDefaults.From(basis);
        var levels = new[] { 0, 1, 2 }.Select(room => Role(room, "level")).ToArray();
        var windowTags = set.Tags.Where(tag => tag.Key.StartsWith("tag-window-", StringComparison.Ordinal)).Select(tag => tag.Key + "|" + tag.View + "|" + K(tag.Element)).ToArray();
        var expectedWindowTags = new[]
        {
            "tag-window-" + K(hostedWindow) + "|plan-" + K(levels[0]) + "|" + K(hostedWindow),
            "tag-window-" + K(axisWindow) + "|plan-" + K(levels[1]) + "|" + K(axisWindow),
        }.OrderBy(tag => tag, StringComparer.Ordinal).ToArray();
        check(windowTags.SequenceEqual(expectedWindowTags) && set.Tags.All(tag => tag.OffsetXMetres == 0 && tag.OffsetYMetres == 0 && !tag.Leader && tag.Orientation == "horizontal"),
            "windows: a tag per window (hosted and absolute) in the plan of the level it stands on", Json(set.Tags));
        var plainSet = DocDefaults.From(plain);
        check(Json(plainSet.Tags) == Json(set.Tags.Where(tag => !tag.Key.StartsWith("tag-window-", StringComparison.Ordinal)).ToArray())
            && Json(plainSet.Views) == Json(set.Views) && Json(plainSet.Schedules) == Json(set.Schedules) && Json(plainSet.Sheets) == Json(set.Sheets) && Json(plainSet.Placements) == Json(set.Placements),
            "windows: every P4-04 item is unchanged when windows are added");
        check(set.Tags.Select(tag => tag.Key).SequenceEqual(set.Tags.Select(tag => tag.Key).OrderBy(key => key, StringComparer.Ordinal))
            && set.Dimensions.Select(item => item.Key).SequenceEqual(set.Dimensions.Select(item => item.Key).OrderBy(key => key, StringComparer.Ordinal)),
            "windows: tags and dimensions are sorted by key");

        // ---- Dimension chains: one per exterior wall line on each plan with a window ----
        var expectedDimensions = new[] { 0, 1 }.SelectMany(room => new[]
        {
            DimensionKey(Role(room, "south")) + "|plan-" + K(levels[room]) + "|" + Pair(Role(room, "west"), Role(room, "east")) + "|exterior",
            DimensionKey(Role(room, "north")) + "|plan-" + K(levels[room]) + "|" + Pair(Role(room, "west"), Role(room, "east")) + "|exterior",
            DimensionKey(Role(room, "east")) + "|plan-" + K(levels[room]) + "|" + Pair(Role(room, "south"), Role(room, "north")) + "|exterior",
            DimensionKey(Role(room, "west")) + "|plan-" + K(levels[room]) + "|" + Pair(Role(room, "south"), Role(room, "north")) + "|exterior",
        }).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var actualDimensions = set.Dimensions.Select(item => item.Key + "|" + item.View + "|" + Pair(item.First, item.Second) + "|" + item.WallFace).ToArray();
        check(actualDimensions.SequenceEqual(expectedDimensions),
            "dimensions: one chain per exterior wall line, referencing the two walls at its ends, on plans with a window only", Json(set.Dimensions));
        check(!set.Dimensions.Any(item => item.View == "plan-" + K(levels[2])), "dimensions: a level without windows gets no dimensions");

        // ---- Full regeneration: only catalogue-valid steps, dimension lines 1 m outside each wall line ----
        var bindings = Bindings(basis);
        var full = DocRegeneration.Full(basis, set, bindings);
        check(full.Unsupported.Count == 0 && full.Steps.Count == full.TotalItems && full.TotalItems == Keys(set).Count(),
            "full: one step per item and nothing unsupported", Json(full.Unsupported));
        var problem = CatalogueProblem(full);
        check(problem.Length == 0, "full: every step validates against its catalogue args schema and parses as an envelope", problem);
        check(set.Dimensions.All(item => full.Steps.Single(step => step.Key == item.Key).Command == "dimension.create_linear")
            && windowTags.Length > 0 && set.Tags.Where(tag => tag.Key.StartsWith("tag-window-", StringComparison.Ordinal)).All(tag => full.Steps.Single(step => step.Key == tag.Key).Command == "tag.create"),
            "full: dimensions become dimension.create_linear and window tags tag.create");
        var lines = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DimensionKey(Role(0, "south"))] = Segment(100, -900, 4300, -900),
            [DimensionKey(Role(0, "north"))] = Segment(100, 4300, 4300, 4300),
            [DimensionKey(Role(0, "east"))] = Segment(5300, 100, 5300, 3300),
            [DimensionKey(Role(0, "west"))] = Segment(-900, 100, -900, 3300),
            [DimensionKey(Role(1, "south"))] = Segment(10100, -900, 14300, -900),
            [DimensionKey(Role(1, "west"))] = Segment(9100, 100, 9100, 3300),
        };
        var actualLines = lines.Keys.Select(key => full.Steps.SingleOrDefault(step => step.Key == key) is { } step ? Line(step) : "missing").ToArray();
        check(actualLines.SequenceEqual(lines.Values), "full: each dimension line runs between the end walls' axes 1 m outside its wall line",
            string.Join(" | ", actualLines) + " expected " + string.Join(" | ", lines.Values));

        // ---- Determinism ----
        check(Json(DocDefaults.From(Build(true, 0, 3, 6))) == Json(set), "determinism: same input twice gives byte-identical JSON");
        check(Json(DocDefaults.From(basis with { Elements = Enumerable.Reverse(basis.Elements).ToArray() })) == Json(set), "determinism: element order does not change the set");
        check(Json(DocRegeneration.Full(basis, DocDefaults.From(basis), bindings)) == Json(full), "determinism: full regeneration is byte-identical for the same input");

        // ---- Stable keys ----
        var room0 = new[] { "south", "east", "north", "west", "space" }.Select(role => Role(0, role)).ToArray();
        var shifted = Translate(basis, room0, 2.5, -1.25);
        var shiftedSet = DocDefaults.From(shifted);
        check(Json(shiftedSet) == Json(set), "stable: translating a room with its hosted window leaves the set byte-identical", Json(shiftedSet.Dimensions));
        check(DocRegeneration.Agrees(basis, shifted, Compiler.Diff(basis, shifted), set, bindings), "stable: affected-only regeneration of a room translation merges to full regeneration");
        var widened = Replace(basis, Role(0, "space"), space => { var g = space.GeometryMetres; return space with { GeometryMetres = new[] { g[0], g[1], g[2] + 0.5, g[3] } }; });
        check(Json(DocDefaults.From(widened)) == Json(set), "stable: resizing a space leaves the set byte-identical");
        var raised = DocDefaults.From(Replace(basis, Role(0, "level"), level => level with { GeometryMetres = new[] { 9.0 } }));
        check(Keys(raised).OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(Keys(set).OrderBy(key => key, StringComparer.Ordinal)) && Json(raised.Dimensions) == Json(set.Dimensions),
            "stable: reordering levels by elevation keeps every key and dimension");
        var second = new Element(Id("window-second"), "window", new[] { 0.5, 1.0 }, new[] { Role(0, "west"), Role(0, "level") }, provenance);
        var withSecond = DocDefaults.From(Add(basis, second));
        check(Keys(withSecond).Except(Keys(set)).SequenceEqual(new[] { "tag-window-" + K(second.Id) }) && !Keys(set).Except(Keys(withSecond)).Any() && Json(withSecond.Dimensions) == Json(set.Dimensions),
            "stable: a second window on a dimensioned level adds exactly its tag key", string.Join(",", Keys(withSecond).Except(Keys(set))));
        var first2 = new Element(Id("window-level2"), "window", new[] { 1.0, 1.2 }, new[] { Role(2, "south"), Role(2, "level") }, provenance);
        var withFirst2 = DocDefaults.From(Add(basis, first2));
        var added = Keys(withFirst2).Except(Keys(set)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var expectedAdded = new[] { "tag-window-" + K(first2.Id) }.Concat(new[] { "south", "east", "north", "west" }.Select(role => DimensionKey(Role(2, role)))).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        check(added.SequenceEqual(expectedAdded) && !Keys(set).Except(Keys(withFirst2)).Any(),
            "stable: the first window on a level adds its tag and that level's four dimension chains, nothing else", string.Join(",", added));

        // ---- Edge cases ----
        var looseProject = Add(plain, looseWindow);
        var looseSet = DocDefaults.From(looseProject);
        check(!looseSet.Tags.Any(tag => tag.Element == looseWindow.Id) && looseSet.Dimensions.Count == 0 && Json(looseSet) == Json(DocDefaultsP404Reference.From(looseProject)) && Valid(looseProject, looseSet, out _),
            "edge: a hosted window without a host wall is not tagged and adds no dimensions", Json(looseSet.Dimensions));

        // Two collinear south walls, an interior partition: one chain per line, the partition is not dimensioned.
        var hall = Id("hall-level");
        var s1 = Wall(Id("hall-s1"), hall, 0, 0, 4, 0);
        var s2 = Wall(Id("hall-s2"), hall, 4, 0, 8, 0);
        var e = Wall(Id("hall-e"), hall, 8, 0, 8, 4);
        var n = Wall(Id("hall-n"), hall, 8, 4, 0, 4);
        var w = Wall(Id("hall-w"), hall, 0, 4, 0, 0);
        var partition = Wall(Id("hall-p"), hall, 4, 0, 4, 4);
        var hallWindow = new Element(Id("hall-window"), "window", new[] { 1.0, 1.2 }, new[] { n.Id, hall }, provenance);
        var hallProject = new Project(projectId, 0, new[] { Level(hall, 0), s1, s2, e, n, w, partition, hallWindow });
        var hallSet = DocDefaults.From(hallProject);
        var hallExpected = new[]
        {
            DimensionKey(s1.Id, s2.Id) + "|" + Pair(w.Id, e.Id),
            DimensionKey(n.Id) + "|" + Pair(w.Id, e.Id),
            DimensionKey(e.Id) + "|" + Pair(s2.Id, n.Id),
            DimensionKey(w.Id) + "|" + Pair(s1.Id, n.Id),
        }.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        check(hallSet.Dimensions.Select(item => item.Key + "|" + Pair(item.First, item.Second)).SequenceEqual(hallExpected) && Valid(hallProject, hallSet, out _),
            "edge: collinear walls form one chain between the outer end walls and an interior partition is not dimensioned", Json(hallSet.Dimensions));
        var hallFull = DocRegeneration.Full(hallProject, hallSet, Bindings(hallProject));
        check(Line(hallFull.Steps.Single(step => step.Key == DimensionKey(s1.Id, s2.Id))) == Segment(0, -1000, 8000, -1000), "edge: a chain of collinear walls spans the whole line",
            Line(hallFull.Steps.Single(step => step.Key == DimensionKey(s1.Id, s2.Id))));

        // A triangle: no line has perpendicular walls at both ends, so nothing is dimensioned; the window is still tagged.
        var tri = Id("tri-level");
        var ta = Wall(Id("tri-a"), tri, 0, 0, 4, 0);
        var tb = Wall(Id("tri-b"), tri, 4, 0, 0, 3);
        var tc = Wall(Id("tri-c"), tri, 0, 3, 0, 0);
        var triWindow = new Element(Id("tri-window"), "window", new[] { 1.0, 1.0 }, new[] { ta.Id, tri }, provenance);
        var triProject = new Project(projectId, 0, new[] { Level(tri, 0), ta, tb, tc, triWindow });
        var triSet = DocDefaults.From(triProject);
        var triValid = Valid(triProject, triSet, out var triDetail);
        check(triSet.Dimensions.Count == 0 && triSet.Tags.Any(tag => tag.Element == triWindow.Id) && triValid,
            "edge: a wall line without perpendicular walls at both ends is not dimensioned", Json(triSet.Dimensions) + " " + triDetail);

        // Two disjoint rooms on one level: a wall line with another room's walls on both sides is not exterior.
        var pair = Id("pair-level");
        var pa = new[] { Wall(Id("pair-a-s"), pair, 0, 0, 4, 0), Wall(Id("pair-a-e"), pair, 4, 0, 4, 3), Wall(Id("pair-a-n"), pair, 4, 3, 0, 3), Wall(Id("pair-a-w"), pair, 0, 3, 0, 0) };
        var pb = new[] { Wall(Id("pair-b-s"), pair, 6, 0, 10, 0), Wall(Id("pair-b-e"), pair, 10, 0, 10, 3), Wall(Id("pair-b-n"), pair, 10, 3, 6, 3), Wall(Id("pair-b-w"), pair, 6, 3, 6, 0) };
        var pairWindow = new Element(Id("pair-window"), "window", new[] { 1.0, 1.0 }, new[] { pa[0].Id, pair }, provenance);
        var pairProject = new Project(projectId, 0, new[] { Level(pair, 0) }.Concat(pa).Concat(pb).Append(pairWindow).ToArray());
        var pairSet = DocDefaults.From(pairProject);
        var pairValid = Valid(pairProject, pairSet, out var pairDetail);
        var pairExpected = new[]
        {
            DimensionKey(pa[0].Id, pb[0].Id) + "|" + Pair(pa[3].Id, pb[1].Id),
            DimensionKey(pa[2].Id, pb[2].Id) + "|" + Pair(pa[3].Id, pb[1].Id),
            DimensionKey(pa[3].Id) + "|" + Pair(pa[0].Id, pa[2].Id),
            DimensionKey(pb[1].Id) + "|" + Pair(pb[0].Id, pb[2].Id),
        }.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        check(pairSet.Dimensions.Select(item => item.Key + "|" + Pair(item.First, item.Second)).SequenceEqual(pairExpected) && pairValid,
            "edge: on a level with two rooms only the outer lines are dimensioned, collinear walls of both rooms in one chain", Json(pairSet.Dimensions) + " " + pairDetail);
    }
}
