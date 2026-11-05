using Bim.Core;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// P4-06 oracle: DocDefaults.From adds a tag per window (in the plan of the level it stands on, as doors) and, on every
// level plan that has a tagged window, one linear dimension chain per exterior wall line: a run of touching collinear
// walls whose outward rays (from each wall's midpoint) cross no wall of its connected component on exactly one side,
// measured between the two antiparallel perpendicular walls meeting its ends (wall-end references) on their outer faces
// (Revit exterior is left of start -> end), its dimension line 1 m outside. Keys "tag-window-<window:N>" and
// "dim-wall-<first wall of the run by id:N>". Full regeneration yields only catalogue-valid dimension.create_linear
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
    // Walls along a polyline of points on a level (closed back to the first point), named prefix0, prefix1, ...
    static Element[] Loop(Guid level, string prefix, params (double X, double Y)[] points) => points.Select((point, index) =>
    {
        var next = points[(index + 1) % points.Length];
        return Wall(Id(prefix + index.ToString(CultureInfo.InvariantCulture)), level, point.X, point.Y, next.X, next.Y);
    }).ToArray();
    static Element Window(string name, Guid level, Element host) => new(Id(name), "window", new[] { 0.5, 1.0 }, new[] { host.Id, level }, provenance);
    static string Ordered(Guid first, Guid second) => K(first) + ">" + K(second);
    // Each dimension of a set as "key|first>second|face|line" (line from its full regeneration step), in set order.
    static string[] Described(Project project, DocumentationSet set)
    {
        var full = DocRegeneration.Full(project, set, Bindings(project));
        return set.Dimensions.Select(item => item.Key + "|" + Ordered(item.First, item.Second) + "|" + item.WallFace + "|"
            + (full.Steps.SingleOrDefault(step => step.Key == item.Key) is { } step ? Line(step) : "missing")).ToArray();
    }
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
            DimensionKey(Role(room, "south")) + "|plan-" + K(levels[room]) + "|" + Ordered(Role(room, "west"), Role(room, "east")) + "|interior",
            DimensionKey(Role(room, "north")) + "|plan-" + K(levels[room]) + "|" + Ordered(Role(room, "east"), Role(room, "west")) + "|interior",
            DimensionKey(Role(room, "east")) + "|plan-" + K(levels[room]) + "|" + Ordered(Role(room, "south"), Role(room, "north")) + "|interior",
            DimensionKey(Role(room, "west")) + "|plan-" + K(levels[room]) + "|" + Ordered(Role(room, "north"), Role(room, "south")) + "|interior",
        }).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var actualDimensions = set.Dimensions.Select(item => item.Key + "|" + item.View + "|" + Ordered(item.First, item.Second) + "|" + item.WallFace).ToArray();
        check(actualDimensions.SequenceEqual(expectedDimensions),
            "dimensions: one chain per exterior wall line, First at the start end of the line's first wall, on plans with a window only; a counter-clockwise room (Revit exterior inside) measures outer faces with interior", Json(set.Dimensions));
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
            [DimensionKey(Role(1, "north"))] = Segment(10100, 4300, 14300, 4300),
            [DimensionKey(Role(1, "east"))] = Segment(15300, 100, 15300, 3300),
            [DimensionKey(Role(1, "west"))] = Segment(9100, 100, 9100, 3300),
        };
        var actualLines = lines.Keys.Select(key => full.Steps.SingleOrDefault(step => step.Key == key) is { } step ? Line(step) : "missing").ToArray();
        check(lines.Count == set.Dimensions.Count && actualLines.SequenceEqual(lines.Values), "full: each of the eight dimension lines runs between the end walls' axes 1 m outside its wall line",
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

        // Two separate buildings on one level: each gets its own four outer chains (collinear walls of different buildings
        // are different runs; the other building does not hide a line).
        var pair = Id("pair-level");
        var pa = new[] { Wall(Id("pair-a-s"), pair, 0, 0, 4, 0), Wall(Id("pair-a-e"), pair, 4, 0, 4, 3), Wall(Id("pair-a-n"), pair, 4, 3, 0, 3), Wall(Id("pair-a-w"), pair, 0, 3, 0, 0) };
        var pb = new[] { Wall(Id("pair-b-s"), pair, 6, 0, 10, 0), Wall(Id("pair-b-e"), pair, 10, 0, 10, 3), Wall(Id("pair-b-n"), pair, 10, 3, 6, 3), Wall(Id("pair-b-w"), pair, 6, 3, 6, 0) };
        var pairWindow = new Element(Id("pair-window"), "window", new[] { 1.0, 1.0 }, new[] { pa[0].Id, pair }, provenance);
        var pairProject = new Project(projectId, 0, new[] { Level(pair, 0) }.Concat(pa).Concat(pb).Append(pairWindow).ToArray());
        var pairSet = DocDefaults.From(pairProject);
        var pairValid = Valid(pairProject, pairSet, out var pairDetail);
        var pairExpected = new[] { pa, pb }.SelectMany(building => new[]
        {
            DimensionKey(building[0].Id) + "|" + Pair(building[3].Id, building[1].Id),
            DimensionKey(building[2].Id) + "|" + Pair(building[3].Id, building[1].Id),
            DimensionKey(building[1].Id) + "|" + Pair(building[0].Id, building[2].Id),
            DimensionKey(building[3].Id) + "|" + Pair(building[0].Id, building[2].Id),
        }).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        check(pairSet.Dimensions.Select(item => item.Key + "|" + Pair(item.First, item.Second)).SequenceEqual(pairExpected) && pairValid,
            "edge: two separate buildings on one level each get their four outer chains", Json(pairSet.Dimensions) + " " + pairDetail);
        // L-shape: the reentrant lines (y = 4 and x = 4) are exterior too; six chains.
        var ell = Id("ell-level");
        var lw = Loop(ell, "ell-", (0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10));
        var ellProject = new Project(projectId, 0, new[] { Level(ell, 0) }.Concat(lw).Append(Window("ell-window", ell, lw[0])).ToArray());
        var ellSet = DocDefaults.From(ellProject);
        var ellValid = Valid(ellProject, ellSet, out var ellDetail);
        var ellExpected = Enumerable.Range(0, 6).Select(i => DimensionKey(lw[i].Id) + "|" + Ordered(lw[(i + 5) % 6].Id, lw[(i + 1) % 6].Id) + "|interior")
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        check(ellSet.Dimensions.Select(item => item.Key + "|" + Ordered(item.First, item.Second) + "|" + item.WallFace).SequenceEqual(ellExpected) && ellValid,
            "exterior: an L-shape gets six chains, its reentrant lines included", Json(ellSet.Dimensions) + " " + ellDetail);
        var ellLines = Described(ellProject, ellSet).ToDictionary(item => item.Split('|')[0], item => item.Split('|')[3], StringComparer.Ordinal);
        check(ellLines.GetValueOrDefault(DimensionKey(lw[2].Id)) == Segment(10000, 5000, 4000, 5000) && ellLines.GetValueOrDefault(DimensionKey(lw[3].Id)) == Segment(5000, 4000, 5000, 10000),
            "exterior: the reentrant chains run 1 m outside their lines", string.Join(" ; ", ellLines));

        // A free-standing site wall in front of a room (its own component) removes no chain and gets none.
        var site = Id("site-level");
        var sw = Loop(site, "site-", (0, 0), (8, 0), (8, 4), (0, 4));
        var siteAlone = new Project(projectId, 0, new[] { Level(site, 0) }.Concat(sw).Append(Window("site-window", site, sw[0])).ToArray());
        var siteProject = Add(siteAlone, Wall(Id("site-fence"), site, -2, -5, 10, -5));
        var siteSet = DocDefaults.From(siteProject);
        var siteValid = Valid(siteProject, siteSet, out var siteDetail);
        check(siteSet.Dimensions.Count == 4 && Json(siteSet.Dimensions) == Json(DocDefaults.From(siteAlone).Dimensions) && siteValid,
            "exterior: a free-standing wall outside a room removes no chain and is not dimensioned", Json(siteSet.Dimensions) + " " + siteDetail);

        // Clockwise rectangle (Revit exterior outside); its south line is three collinear walls, the middle one drawn in the
        // opposite direction.
        var cw = Id("cw-level");
        var c0 = Wall(Id("cw-west"), cw, 0, 0, 0, 4);
        var c1 = Wall(Id("cw-north"), cw, 0, 4, 8, 4);
        var c2 = Wall(Id("cw-east"), cw, 8, 4, 8, 0);
        var sa = Wall(Id("cw-south-a"), cw, 8, 0, 6, 0);
        var sb = Wall(Id("cw-south-b"), cw, 2, 0, 6, 0);
        var sc = Wall(Id("cw-south-c"), cw, 2, 0, 0, 0);
        var cwProject = new Project(projectId, 0, new[] { Level(cw, 0), c0, c1, c2, sa, sb, sc, Window("cw-window", cw, c1) });
        var cwSet = DocDefaults.From(cwProject);
        var cwValid = Valid(cwProject, cwSet, out var cwDetail);
        var cwFull = DocRegeneration.Full(cwProject, cwSet, Bindings(cwProject));
        var cwExpected = new[]
        {
            DimensionKey(sa.Id, sb.Id, sc.Id) + "|" + Pair(c0.Id, c2.Id) + "|exterior|" + Segment(0, -1000, 8000, -1000),
            DimensionKey(c1.Id) + "|" + Pair(c0.Id, c2.Id) + "|exterior|" + Segment(0, 5000, 8000, 5000),
            DimensionKey(c0.Id) + "|" + Pair(sc.Id, c1.Id) + "|exterior|" + Segment(-1000, 0, -1000, 4000),
            DimensionKey(c2.Id) + "|" + Pair(c1.Id, sa.Id) + "|exterior|" + Segment(9000, 0, 9000, 4000),
        }.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var cwActual = cwSet.Dimensions.Select(item => item.Key + "|" + Pair(item.First, item.Second) + "|" + item.WallFace + "|"
            + (cwFull.Steps.SingleOrDefault(step => step.Key == item.Key) is { } step ? Line(step) : "missing")).ToArray();
        check(cwActual.SequenceEqual(cwExpected) && cwValid,
            "orientation: a clockwise room measures outer faces with exterior, lines at y = -1, y = 5, x = -1, x = 9; collinear walls in opposite directions make one chain",
            string.Join(" ; ", cwActual) + " " + cwDetail);

        // End walls drawn in the same direction at two convex ends: no single wall_face measures both outer faces, so that
        // line is skipped (at a reentrant end the same-direction pair is consistent; see the L-shape).
        var same = Id("same-level");
        var m0 = Wall(Id("same-south"), same, 0, 0, 8, 0);
        var m1 = Wall(Id("same-east"), same, 8, 0, 8, 4);
        var m2 = Wall(Id("same-north"), same, 8, 4, 0, 4);
        var m3 = Wall(Id("same-west"), same, 0, 0, 0, 4);
        var sameProject = new Project(projectId, 0, new[] { Level(same, 0), m0, m1, m2, m3, Window("same-window", same, m0) });
        var sameSet = DocDefaults.From(sameProject);
        var sameValid = Valid(sameProject, sameSet, out var sameDetail);
        var sameExpected = new[]
        {
            DimensionKey(m1.Id) + "|" + Ordered(m0.Id, m2.Id) + "|interior",
            DimensionKey(m3.Id) + "|" + Ordered(m0.Id, m2.Id) + "|interior",
        }.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        check(sameSet.Dimensions.Select(item => item.Key + "|" + Ordered(item.First, item.Second) + "|" + item.WallFace).SequenceEqual(sameExpected) && sameValid,
            "orientation: a line whose end walls need different faces (both convex ends, walls drawn the same way) is skipped; the others measure their outer faces", Json(sameSet.Dimensions) + " " + sameDetail);

        // A rotated 3-4-5 rectangle: four chains (perpendicular within 1e-6, end walls antiparallel within 1e-9).
        var rot = Id("rot-level");
        var rw = Loop(rot, "rot-", (0, 0), (4, 3), (1, 7), (-3, 4));
        var rotProject = new Project(projectId, 0, new[] { Level(rot, 0) }.Concat(rw).Append(Window("rot-window", rot, rw[0])).ToArray());
        var rotSet = DocDefaults.From(rotProject);
        var rotValid = Valid(rotProject, rotSet, out var rotDetail);
        check(rotSet.Dimensions.Count == 4 && rotValid, "orientation: a rotated rectangle gets four chains", Json(rotSet.Dimensions) + " " + rotDetail);
    }
}
