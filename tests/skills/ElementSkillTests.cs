using Bim.Core;
using System.Globalization;
using System.Text.Json.Nodes;

// P5-05 oracle: the element QA recipe pack (skills/bim.qa.element-basics) and SkillElementValidator over BIM-IR walls,
// doors and windows. Wall thickness bounds, door/window width and height bounds and openings fully inside their host wall
// are evaluated with traceable findings (qualified rule id, element id, measured vs bound in an explicit unit, skill id,
// version and bound source); missing data is a typed not_evaluable finding, never a silent pass; findings are ordered
// deterministically; boundary equality, openings exactly at wall ends, unsupported curved walls, orphan hosts and duplicate
// rule ids (refused at load) are covered. Unit-tested only.
static class ElementSkillTests
{
    public const string Pack = "bim.qa.element-basics";
    const string RoomBasics = "bim.qa.room-basics";
    const string Caba = "ar.caba.codigo-edificacion";
    static readonly Provenance Origin = new("element-skill-tests", "unit");
    static readonly Guid LevelId = Guid.Parse("00000000-0000-4000-8000-000000000001");
    static readonly Guid WallId = Guid.Parse("00000000-0000-4000-8000-000000000010");
    static readonly Guid DoorId = Guid.Parse("00000000-0000-4000-8000-000000000020");
    static readonly Guid WindowId = Guid.Parse("00000000-0000-4000-8000-000000000030");

    static Element Level() => new(LevelId, "level", new[] { 0.0 }, Array.Empty<Guid>(), Origin);
    static Element Wall(Guid id, double ax, double ay, double bx, double by, double height = 2.7, double thickness = 0.2) =>
        new(id, "wall", new[] { ax, ay, bx, by, height, thickness }, new[] { LevelId }, Origin);
    static Element Opening(string kind, Guid id, double ax, double ay, double bx, double by, double sill, double height, params Guid[] dependencies) =>
        new(id, kind, new[] { ax, ay, bx, by, sill, height }, dependencies.Length == 0 ? new[] { LevelId, WallId } : dependencies, Origin);
    static Project Model(params Element[] elements) => new(Guid.Parse("00000000-0000-4000-8000-0000000000ff"), 0, elements);
    static Guid G(int n) => Guid.Parse("00000000-0000-4000-8000-" + n.ToString("D12", CultureInfo.InvariantCulture));

    // A compliant model: a 4 m wall 200 mm thick and 2.7 m high, a 900 x 2100 door at 0.5 m and a 1200 x 1200 window at 2 m
    // with a 900 mm sill.
    static Element[] Good() => new[]
    {
        Level(),
        Wall(WallId, 0, 0, 4, 0),
        Opening("door", DoorId, 0.5, 0, 1.4, 0, 0, 2.1),
        Opening("window", WindowId, 2, 0, 3.2, 0, 0.9, 1.2),
    };

    static string Show(ElementValidationReport report) => string.Join(" | ", report.Findings.Select(finding =>
        finding.ElementId[^2..] + ":" + finding.LocalRuleId + "=" + finding.Outcome + (finding.Reason is null ? "" : "(" + finding.Reason + ")") + " " + finding.Detail))
        + (report.UnusedRuleIds.Count == 0 ? "" : " | unused " + string.Join(",", report.UnusedRuleIds));

    static ElementFinding? Find(ElementValidationReport report, Guid element, string rule) =>
        report.Findings.FirstOrDefault(finding => finding.ElementId == element.ToString("D") && finding.LocalRuleId == rule);

    static string? Code(Action action)
    {
        try { action(); return null; }
        catch (SkillException error) { return error.Code + "@" + error.Field + (error.File is null ? "" : "#" + error.File); }
        catch (ArgumentException error) { return error.GetType().Name; }
        catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
    }

    public static void Run(string root, Action<bool, string, string> sink)
    {
        void check(bool condition, string name, string detail = "") => sink(condition, "element: " + name, detail);
        SkillCatalog catalog;
        try { catalog = SkillLoader.LoadDirectory(Path.Combine(root, "skills")); }
        catch (SkillException error) { check(false, "skills/ loads", error.Code + " " + error.File + " at " + error.Field + " (" + error.Detail + ")"); return; }
        var pack = catalog.Find(Pack);
        check(pack is not null, "the element QA pack loads through SkillLoader", string.Join(",", catalog.Skills.Select(skill => skill.Id)));
        if (pack is null) return;
        var packPath = Path.Combine(root, "skills", Pack, "skill.json");

        // ---- Manifest shape ----
        check(pack.Kind == SkillKind.Qa && pack.Version == new SkillVersion(1, 0, 0) && pack.Priority == 50 && pack.Requires.Count == 0 && pack.Defaults.Count == 0,
            "the pack is a qa skill 1.0.0 with priority 50, no requirements and no defaults", pack.Kind + " " + pack.Version + " " + pack.Priority);
        check(pack.Rules.Count == 20 && pack.Rules.All(rule => rule.Target.ElementKind is "wall" or "door" or "window")
            && pack.Rules.All(rule => rule.Check.Property.EndsWith("_mm", StringComparison.Ordinal) && ElementFacts.IsProperty(rule.Target.ElementKind!, rule.Check.Property)),
            "20 element rules over walls, doors and windows, every property an explicit millimetre quantity of its kind",
            string.Join(",", pack.Rules.Select(rule => rule.Target.ElementKind + "." + rule.Check.Property)));
        var expectedRules = new[] { "wall-thickness-min", "wall-thickness-max" }
            .Concat(new[] { "door", "window" }.SelectMany(kind => new[] { "width-min", "width-max", "height-min", "height-max", "inside-host-start", "inside-host-end", "inside-host-bottom", "inside-host-top", "on-host-axis" }
                .Select(suffix => kind + "-" + suffix))).ToArray();
        check(pack.Rules.Select(rule => rule.Id).SequenceEqual(expectedRules), "the pack covers thickness bounds, size bounds and inside-host checks for doors and windows",
            string.Join(",", pack.Rules.Select(rule => rule.Id)));
        check(pack.Description.Contains("not a building code", StringComparison.OrdinalIgnoreCase) && pack.Description.Contains("millimetres", StringComparison.Ordinal),
            "the pack states it is illustrative and that lengths are millimetres");
        check(pack.Parameters.FirstOrDefault(parameter => parameter.Name == "wall_thickness_min_mm") is { Default.Number: 75 }
            && pack.Parameters.FirstOrDefault(parameter => parameter.Name == "door_width_min_mm") is { Default.Number: 700 }
            && pack.Parameters.FirstOrDefault(parameter => parameter.Name == "opening_axis_tolerance_mm") is { Default.Number: 1 },
            "pack bounds are parameters (wall 75 mm, door 700 mm, axis tolerance 1 mm)");
        var together = SkillResolver.Resolve(catalog, new[] { new SkillRequest(Caba), new SkillRequest(Pack), new SkillRequest(RoomBasics) });
        check(together.Succeeded && together.Rules.Count(rule => rule.SkillId == Pack) == 20, "the pack composes with the CABA and room QA packs without conflicts",
            string.Join("; ", together.Issues.Select(issue => issue.Code + "@" + issue.Subject)));

        // ---- Duplicate rule ids are refused at load ----
        var text = File.ReadAllText(packPath);
        var node = JsonNode.Parse(text)!.AsObject();
        var rules = node["rules"]!.AsArray();
        rules.Add(rules[0]!.DeepClone());
        var duplicated = node.ToJsonString();
        check(Code(() => SkillParser.Parse(duplicated)) == SkillCodes.EntryDuplicate + "@rules[20].id", "a duplicate rule id in the pack is refused by the parser",
            Code(() => SkillParser.Parse(duplicated)) ?? "accepted");
        var temp = Path.Combine(Path.GetTempPath(), "bim-element-skill-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, Pack));
            File.WriteAllText(Path.Combine(temp, Pack, "skill.json"), duplicated);
            check(Code(() => SkillLoader.LoadDirectory(temp)) == SkillCodes.EntryDuplicate + "@rules[20].id#" + Pack + "/skill.json",
                "a duplicate rule id is refused at load with the manifest path", Code(() => SkillLoader.LoadDirectory(temp)) ?? "loaded");
        }
        finally
        {
            try { Directory.Delete(temp, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- A compliant model ----
        var good = SkillElementValidator.Validate(pack, Model(Good()));
        check(good.Findings.Count == 20 && good.Compliant && good.Complete && good.Failures.Count == 0 && good.UnusedRuleIds.Count == 0,
            "a compliant wall, door and window pass every rule", Show(good));
        var orderKeys = good.Findings.Select(finding => (finding.ElementId, finding.RuleId)).ToArray();
        check(orderKeys.SequenceEqual(orderKeys.OrderBy(key => key.ElementId, StringComparer.Ordinal).ThenBy(key => key.RuleId, StringComparer.Ordinal)),
            "findings are ordered by element id then qualified rule id", Show(good));
        check(Find(good, WallId, "wall-thickness-min") is { RuleId: Pack + "/wall-thickness-min", SkillId: Pack, SkillVersion: "1.0.0", ElementKind: "wall", Op: "min",
                Property: "thickness_mm", Unit: "mm", Measured: 200, Bound: 75, BoundSource: Pack + ":parameter:wall_thickness_min_mm", Outcome: ElementCheckOutcome.Pass, Reason: null, HostId: null } wallFinding
            && wallFinding.Detail.Contains("thickness_mm 200 >= 75 mm", StringComparison.Ordinal),
            "a finding traces rule id, skill id and version, element, measured vs bound in millimetres and the parameter the bound came from", Show(good));
        check(Find(good, DoorId, "door-inside-host-start") is { Measured: 500, Bound: 0, BoundSource: Pack + ":rule:door-inside-host-start", HostId: var host } && host == WallId.ToString("D")
            && Find(good, DoorId, "door-inside-host-end") is { Measured: 2600 } && Find(good, WindowId, "window-inside-host-top") is { Measured: 600 }
            && Find(good, DoorId, "door-width-min") is { Measured: 900 } && Find(good, WindowId, "window-on-host-axis") is { Measured: 0, Bound: 1 },
            "opening findings carry the host wall and measured clearances (500, 2600, head 600) and widths in millimetres", Show(good));

        // ---- Boundary equality and openings exactly at wall ends ----
        var edge = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 4, 0, 2.7, 0.075), Wall(G(11), 0, 5, 4, 5, 2.7, 0.6),
            Opening("door", DoorId, 0, 0, 0.7, 0, 0, 2.0), Opening("window", WindowId, 2.8, 0, 4, 0, 1.5, 1.2)));
        check(edge.Compliant && edge.Findings.Count == 22, "walls exactly 75 and 600 mm, a door exactly 700 x 2000 and openings flush with the wall ends and top pass", Show(edge));
        check(Find(edge, WallId, "wall-thickness-min") is { Measured: 75, Bound: 75 } && Find(edge, G(11), "wall-thickness-max") is { Measured: 600, Bound: 600 }
            && Find(edge, DoorId, "door-inside-host-start") is { Measured: 0, Outcome: ElementCheckOutcome.Pass }
            && Find(edge, WindowId, "window-inside-host-end") is { Measured: 0, Outcome: ElementCheckOutcome.Pass }
            && Find(edge, WindowId, "window-inside-host-top") is { Measured: 0, Outcome: ElementCheckOutcome.Pass },
            "boundary values are measured exactly (75, 600 mm; clearances of 0 at both wall ends and at the wall top)", Show(edge));
        var reversedHost = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 4, 0, 0, 0), Opening("door", DoorId, 4, 0, 3.1, 0, 0, 2.1), Opening("window", WindowId, 1.2, 0, 0, 0, 0.9, 1.2)));
        check(reversedHost.Compliant && Find(reversedHost, DoorId, "door-inside-host-start") is { Measured: 0 } && Find(reversedHost, WindowId, "window-inside-host-end") is { Measured: 0 }
            && Find(reversedHost, WindowId, "window-inside-host-start") is { Measured: 2800 },
            "clearances follow the host wall direction (a wall drawn from x=4 to x=0)", Show(reversedHost));
        var diagonal = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 3, 4), Opening("door", DoorId, 0.6, 0.8, 1.2, 1.6, 0, 2.1)));
        check(diagonal.Compliant && Find(diagonal, DoorId, "door-inside-host-start") is { Measured: 1000 } && Find(diagonal, DoorId, "door-inside-host-end") is { Measured: 3000 }
            && Find(diagonal, DoorId, "door-width-min") is { Measured: 1000 } && Find(diagonal, DoorId, "door-on-host-axis") is { Measured: 0 },
            "a door on a diagonal 5 m wall measures 1000 mm wide, 1000 mm from the start and 3000 mm from the end, on the axis", Show(diagonal));

        // ---- Failures just past each bound ----
        var below = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 4, 0, 2.7, 0.0749), Opening("door", DoorId, 0.5, 0, 1.1999, 0, 0, 1.9999)));
        check(Find(below, WallId, "wall-thickness-min") is { Outcome: ElementCheckOutcome.Fail, Measured: 74.9, Bound: 75, Severity: SkillSeverity.Error }
            && Find(below, DoorId, "door-width-min") is { Outcome: ElementCheckOutcome.Fail, Measured: 699.9 }
            && Find(below, DoorId, "door-height-min") is { Outcome: ElementCheckOutcome.Fail, Measured: 1999.9 } && below.Failures.Count == 3 && !below.Compliant && below.Complete,
            "one tenth of a millimetre below a minimum fails with the measured value", Show(below));
        var outside = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 4, 0, 2.7, 0.7),
            Opening("door", DoorId, 3.5, 0, 4.4, 0, 0, 2.1), Opening("window", WindowId, -0.1, 0.1, 1.1, 0.1, 2, 1), Opening("door", G(21), 1, 0, 3.5, 0, -0.05, 3.1)));
        check(Find(outside, WallId, "wall-thickness-max") is { Outcome: ElementCheckOutcome.Fail, Measured: 700, Severity: SkillSeverity.Warning }
            && Find(outside, DoorId, "door-inside-host-end") is { Outcome: ElementCheckOutcome.Fail, Measured: -400 }
            && Find(outside, WindowId, "window-inside-host-start") is { Outcome: ElementCheckOutcome.Fail, Measured: -100 }
            && Find(outside, WindowId, "window-inside-host-top") is { Outcome: ElementCheckOutcome.Fail, Measured: -300 }
            && Find(outside, WindowId, "window-on-host-axis") is { Outcome: ElementCheckOutcome.Fail, Measured: 100, Bound: 1 }
            && Find(outside, G(21), "door-inside-host-bottom") is { Outcome: ElementCheckOutcome.Fail, Measured: -50 }
            && Find(outside, G(21), "door-width-max") is { Outcome: ElementCheckOutcome.Fail, Measured: 2500, Bound: 2400 }
            && Find(outside, G(21), "door-height-max") is { Outcome: ElementCheckOutcome.Fail, Measured: 3100 }
            && Find(outside, G(21), "door-inside-host-top") is { Outcome: ElementCheckOutcome.Fail, Measured: -350 },
            "openings past the wall end, before its start, above its top, below its base or off its axis fail with signed clearances", Show(outside));

        // ---- Missing data is not_evaluable with a typed reason ----
        var orphan = Guid.Parse("00000000-0000-4000-8000-0000000000aa");
        var orphaned = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 4, 0), Opening("door", DoorId, 0.5, 0, 1.4, 0, 0, 2.1, LevelId, orphan)));
        var hostRules = new[] { "door-inside-host-start", "door-inside-host-end", "door-inside-host-top", "door-on-host-axis" };
        check(hostRules.All(rule => Find(orphaned, DoorId, rule) is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.HostOrphan, Measured: null, HostId: null } finding
                && finding.Detail.Contains(orphan.ToString("D"), StringComparison.Ordinal))
            && Find(orphaned, DoorId, "door-width-min") is { Outcome: ElementCheckOutcome.Pass } && Find(orphaned, DoorId, "door-inside-host-bottom") is { Outcome: ElementCheckOutcome.Pass }
            && orphaned.NotEvaluable.Count == 4 && !orphaned.Complete && !orphaned.Compliant && orphaned.Failures.Count == 0,
            "an orphan host id makes the host checks not_evaluable (host_orphan, naming the id) while size checks still run; the report is not compliant", Show(orphaned));
        var noHost = SkillElementValidator.Validate(pack, Model(Level(), Opening("window", WindowId, 0, 0, 1, 0, 1, 1, LevelId)));
        check(Find(noHost, WindowId, "window-inside-host-end") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.HostMissing } && noHost.NotEvaluable.Count == 4,
            "an opening with no wall dependency is host_missing", Show(noHost));
        var twoHosts = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 4, 0), Wall(G(11), 0, 0, 0, 4), Opening("door", DoorId, 0.5, 0, 1.4, 0, 0, 2.1, LevelId, WallId, G(11))));
        check(Find(twoHosts, DoorId, "door-inside-host-start") is { Reason: ElementReasons.HostAmbiguous, Outcome: ElementCheckOutcome.NotEvaluable }, "an opening depending on two walls is host_ambiguous", Show(twoHosts));
        var pointHost = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 1, 1, 1, 1), Opening("door", DoorId, 1, 1, 1, 1, 0, 2.1)));
        check(Find(pointHost, DoorId, "door-inside-host-start") is { Reason: ElementReasons.HostDegenerate, HostId: var pointHostId } && pointHostId == WallId.ToString("D")
            && Find(pointHost, DoorId, "door-width-min") is { Outcome: ElementCheckOutcome.Fail, Measured: 0 },
            "a zero-length host is host_degenerate; a zero-width door fails its width rule", Show(pointHost));
        var broken = SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 4, 0),
            new Element(DoorId, "door", new[] { 0.5, 0, 1.4, 0, 0 }, new[] { LevelId, WallId }, Origin),
            new Element(WindowId, "window", new[] { 2, 0, 3.2, 0, 0.9, double.NaN }, new[] { LevelId, WallId }, Origin)));
        check(broken.Findings.Where(finding => finding.ElementKind is "door" or "window").All(finding => finding is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.GeometryInvalid, Measured: null })
            && broken.NotEvaluable.Count == 18 && !broken.Compliant,
            "a door with five values and a window with a non-finite height are geometry_invalid on every rule", Show(broken));

        // ---- Curved walls are a typed unsupported shape ----
        var arc = new Element(WallId, "wall", new[] { 0, 0, 4, 0, 2.7, 0.2, 0.5 }, new[] { LevelId }, Origin);
        var curved = SkillElementValidator.Validate(pack, Model(Level(), arc, Opening("door", DoorId, 0.5, 0, 1.4, 0, 0, 2.1)));
        check(Find(curved, WallId, "wall-thickness-min") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.ShapeUnsupported } curvedFinding
            && curvedFinding.Detail.Contains("curved", StringComparison.Ordinal)
            && Find(curved, DoorId, "door-inside-host-end") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.HostShapeUnsupported }
            && Find(curved, DoorId, "door-width-min") is { Outcome: ElementCheckOutcome.Pass } && !curved.Compliant,
            "a wall carrying an arc bulge value is shape_unsupported (BIM-IR walls are straight) and its openings' host checks are host_shape_unsupported", Show(curved));
        var nonFiniteWall = new Element(WallId, "wall", new[] { 0, 0, double.PositiveInfinity, 0, 2.7, 0.2 }, new[] { LevelId }, Origin);
        check(Find(SkillElementValidator.Validate(pack, Model(Level(), nonFiniteWall)), WallId, "wall-thickness-max") is { Reason: ElementReasons.GeometryInvalid },
            "a wall with a non-finite coordinate is geometry_invalid");

        // ---- Determinism ----
        var shuffled = SkillElementValidator.Validate(pack, Model(Good().Reverse().ToArray()));
        var recatalogued = SkillElementValidator.Validate(SkillResolver.Resolve(new SkillCatalog(catalog.Skills.Reverse().ToArray()), new[] { new SkillRequest(Pack) }), Model(Good()));
        check(shuffled.Findings.SequenceEqual(good.Findings) && recatalogued.Findings.SequenceEqual(good.Findings) && SkillElementValidator.Validate(pack, Model(Good())).Findings.SequenceEqual(good.Findings),
            "findings do not depend on element order, catalog order or repetition");
        check(Code(() => SkillElementValidator.Validate(pack, Model(Level(), Wall(WallId, 0, 0, 4, 0), Wall(WallId, 0, 0, 0, 4)))) == nameof(ArgumentException),
            "duplicate element ids are refused");

        // ---- Parameters and run-time overrides ----
        var stricter = SkillElementValidator.Validate(pack, Model(Good()), new Dictionary<string, double> { [Pack + "/wall_thickness_min_mm"] = 250 });
        check(Find(stricter, WallId, "wall-thickness-min") is { Outcome: ElementCheckOutcome.Fail, Bound: 250, BoundSource: "override:" + Pack + "/wall_thickness_min_mm" },
            "a run-time override changes the bound and is traced as the bound source", Show(stricter));
        check(Code(() => SkillElementValidator.Validate(pack, Model(Good()), new Dictionary<string, double> { [Pack + "/wall_thickness_min_mm"] = 5 })) == nameof(ArgumentOutOfRangeException)
            && Code(() => SkillElementValidator.Validate(pack, Model(Good()), new Dictionary<string, double> { [Pack + "/nope"] = 5 })) == nameof(ArgumentException),
            "out-of-range and unknown overrides are refused");

        // ---- The room QA sample's element rules are now evaluated (P5-01 gap) ----
        var roomBasics = catalog.Find(RoomBasics)!;
        var basics = SkillElementValidator.Validate(roomBasics, Model(Good()));
        check(Find(basics, WallId, "wall-min-thickness") is { Outcome: ElementCheckOutcome.Pass, Unit: "m", Measured: 0.2, Bound: 0.05, BoundSource: RoomBasics + ":parameter:min_wall_thickness_m" }
            && Find(basics, WallId, "wall-max-height") is { Outcome: ElementCheckOutcome.Pass, Measured: 2.7, Bound: 12 }
            && Find(basics, WallId, "wall-bounded") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.PropertyUnknown }
            && Find(basics, DoorId, "door-operation-known") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.PropertyUnknown }
            && basics.Findings.Count == 4 && basics.UnusedRuleIds.Count == 0 && !basics.Compliant,
            "every element rule of bim.qa.room-basics yields a finding: metre quantities are measured, room_bounding and operation are property_unknown", Show(basics));

        // ---- Units and the remaining ops on a custom manifest ----
        var custom = SkillParser.Parse("""
            {"manifest_version":1,"id":"bim.test.elements","version":"0.1.0","kind":"qa","description":"Element op corpus, not a building code.","priority":1,
             "rules":[
              {"id":"cm","severity":"info","target":{"element":"wall"},"check":{"op":"min","property":"thickness_cm","value":1},"message":"cm is not a unit."},
              {"id":"eq","severity":"info","target":{"element":"door"},"check":{"op":"equals","property":"width_mm","value":900},"message":"Door width."},
              {"id":"eq-text","severity":"info","target":{"element":"door"},"check":{"op":"equals","property":"width_mm","value":"900"},"message":"Door width text."},
              {"id":"in","severity":"info","target":{"element":"door"},"check":{"op":"in","property":"width_mm","values":[800,900]},"message":"Door width set."},
              {"id":"in-miss","severity":"info","target":{"element":"window"},"check":{"op":"in","property":"width_mm","values":[800,900]},"message":"Window width set."},
              {"id":"ratio","severity":"info","target":{"element":"door"},"check":{"op":"ratio_min","property":"height_mm","over":"width_mm","value":2},"message":"Door proportion."},
              {"id":"space","severity":"info","target":{"element":"space"},"check":{"op":"min","property":"width_mm","value":1},"message":"Space."},
              {"id":"stair","severity":"info","target":{"element":"stair"},"check":{"op":"min","property":"width_mm","value":1},"message":"Stair."},
              {"id":"metres","severity":"info","target":{"element":"wall"},"check":{"op":"max","property":"thickness_m","value":0.2},"message":"Metres."}
             ]}
            """);
        var space = new Element(G(40), "space", new[] { 0.1, 0.1, 3.8, 2.8 }, new[] { LevelId }, Origin);
        var ops = SkillElementValidator.Validate(custom, Model(Good().Append(space).ToArray()));
        check(Find(ops, WallId, "cm") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.PropertyUnknown, Unit: "" }
            && Find(ops, WallId, "metres") is { Outcome: ElementCheckOutcome.Pass, Unit: "m", Measured: 0.2 }
            && Find(ops, DoorId, "eq") is { Outcome: ElementCheckOutcome.Pass, Measured: 900 }
            && Find(ops, DoorId, "eq-text") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.ThresholdNotNumber }
            && Find(ops, DoorId, "in") is { Outcome: ElementCheckOutcome.Pass, Bound: null, BoundSource: null }
            && Find(ops, WindowId, "in-miss") is { Outcome: ElementCheckOutcome.Fail, Measured: 1200 }
            && Find(ops, DoorId, "ratio") is { Outcome: ElementCheckOutcome.Pass, Unit: "ratio" } ratio && Math.Abs(ratio.Measured!.Value - 2100.0 / 900) < 1e-12
            && Find(ops, G(40), "space") is { Outcome: ElementCheckOutcome.NotEvaluable, Reason: ElementReasons.PropertyUnknown, ElementKind: "space" }
            && ops.UnusedRuleIds.SequenceEqual(new[] { "bim.test.elements/stair" }),
            "only _mm and _m suffixes are units; equals, in and ratio_min evaluate; a text threshold, a non-element quantity and an unused rule are reported", Show(ops));

        // ---- End to end: a compiled layout intent ----
        var intent = new LayoutIntent(LayoutIntent.CurrentVersion, Origin, new[] { new IntentLevel("l1", "L1", 0) }, new[] { new IntentWallType("std", "Std", 0.2, 2.7) },
            new[] { new IntentRoom("a", "Room a", "l1", "std", 0, 0, 4, 3) },
            new[]
            {
                new IntentOpening("d1", IntentOpeningKind.Door, new IntentRoomSide("a", IntentSide.South), 0.5, 0.9, 2.1, 0),
                new IntentOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("a", IntentSide.North), 1, 1.2, 1.2, 0.9),
            });
        Project? compiled = null;
        try { compiled = IntentCompiler.Compile(Guid.Parse("00000000-0000-4000-8000-0000000000ee"), 0, intent); }
        catch (Exception error) { check(false, "the layout intent compiles", error.GetType().Name + ": " + error.Message); }
        if (compiled is not null)
        {
            var e2e = SkillElementValidator.Validate(pack, compiled);
            var compiledDoor = IntentCompiler.Id(compiled.Id, IntentCompiler.OpeningKey("d1")).ToString("D");
            check(e2e.Compliant && e2e.Findings.Count == 4 * 2 + 9 * 2 && e2e.UnusedRuleIds.Count == 0
                && e2e.Findings.Any(finding => finding.ElementId == compiledDoor && finding.LocalRuleId == "door-inside-host-start" && finding.Measured == 500),
                "a compiled one-room layout with a door and a window passes the pack, measured on the compiled host walls", Show(e2e));
        }
    }
}
