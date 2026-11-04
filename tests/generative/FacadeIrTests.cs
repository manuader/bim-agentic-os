using System.Globalization;
using System.Text.Json;
using Bim.Core;
using Bim.Generative;

// P7-06 facade windows into the prototype BIM-IR oracle: a feasible space plan compiled through IntentCompiler (levels,
// axis walls, spaces) with Compiler.Validate clean; the windows of a complete P7-05 layout added as "window" elements
// [ax,ay,bx,by,sill,height] hosted ([level, wall]) on the exterior wall of their room side, with exact hand-computed
// offsets and hosts on crafted plans (a south facade, an upper level over a lower one); an independent IntentCompiler
// oracle that compiles the same windows as intent openings on room sides and must agree on host and geometry; P5-02
// SkillRoomValidator on room facts read back from the BIM-IR (illumination/ventilation passing with windows, failing
// without); the P7-04 entrance door: windows moved clear of it (nearest side, ties to the lower coordinate) or a typed
// facade_ir_entrance_overlap failure; typed failures (no host, outside the wall, overlapping windows), refusals, ranked
// 1/2/3-bedroom prototypes with real circulation models, and determinism across runs, room orders and cultures.
// Unit-tested evidence only.
static class FacadeIrTests
{
    const double Tolerance = 1e-9;
    const string PackId = "ar.caba.codigo-edificacion";
    static readonly Guid ProjectId = Guid.Parse("00000000-0000-4000-8000-0000000007a6");

    public static void Run(Action<bool, string, string> check, string root)
    {
        void Check(bool condition, string name, string detail = "") => check(condition, "facade-ir: " + name, detail);
        var caba = SkillParser.Parse(File.ReadAllText(Path.Combine(root, "skills", PackId, "skill.json")));
        var defaults = new FacadeIrOptions();
        Check(defaults.WallThicknessMetres == 0.15 && defaults.WallHeightMetres == 2.6 && defaults.EntranceClearanceMetres == 0.3 && defaults.Provenance is null
            && FacadeIrOptions.DefaultProvenance.Source.Length > 0 && FacadeIrOptions.DefaultProvenance.Approval.Length > 0,
            "documented defaults: 0.15 m walls 2.60 m high, 0.30 m entrance clearance, default provenance");

        // ---- flat: living and bedroom on the south facade, wet and service rooms behind ----
        var flat = Plan("flat", 1, 10, 6, P("living", "Living", 0, 0, 6, 4), P("bedroom_1", "Bedroom 1", 6, 0, 4, 4), P("bathroom", "Bathroom", 0, 4, 3, 2),
            P("hall", "Hall", 3, 4, 3, 2), P("kitchen", "Kitchen", 6, 4, 4, 2));
        var layout = FacadePlanner.Place(flat, caba);
        Check(layout.IsComplete && layout.Windows.Count == 2, "the flat P7-05 layout is complete with two windows", Text(layout.Windows.Count));
        var prototype = Safe(() => FacadeIrBuilder.Prototype(ProjectId, 0, flat));
        Check(prototype is not null && prototype.Id == ProjectId && prototype.Revision == 0 && Compiler.Validate(prototype).Length == 0
            && prototype.Elements.Count(e => e.Kind == "level") == 1 && prototype.Elements.Count(e => e.Kind == "space") == 5 && prototype.Elements.Count(e => e.Kind == "wall") > 0
            && !prototype.Elements.Any(e => e.Kind is "window" or "door"), "the prototype BIM-IR compiles the plan: one level, five spaces, walls, no openings, Compiler.Validate clean",
            prototype is null ? "threw" : Kinds(prototype));
        Check(prototype is not null && Json(prototype) == Json(IntentCompiler.Compile(ProjectId, 0, FacadeIrBuilder.Intent(flat))),
            "the prototype is IntentCompiler.Compile of FacadeIrBuilder.Intent(plan)");
        var intent = FacadeIrBuilder.Intent(flat);
        Check(intent.Rooms.Count == 5 && intent.Rooms.Select(r => r.Key).SequenceEqual(new[] { "bathroom", "bedroom_1", "hall", "kitchen", "living" })
            && intent.Rooms.Single(r => r.Key == "living") is { XMetres: 0, YMetres: 0, WidthMetres: 6, DepthMetres: 4, Name: "Living" }
            && intent.WallTypes.Count == 1 && intent.WallTypes[0].ThicknessMetres == 0.15 && intent.WallTypes[0].HeightMetres == 2.6
            && intent.Levels.Count == 1 && intent.Levels[0].ElevationMetres == 0 && intent.Openings.Count == 0 && intent.Provenance == FacadeIrOptions.DefaultProvenance,
            "the intent holds the plan rectangles as axis rooms in instance id order, one wall type, one level, no openings");
        Check(FacadeIrBuilder.RoomKey("bedroom_1") == "bedroom_1" && FacadeIrBuilder.RoomKey("bedroom#2") == "bedroom-2"
            && FacadeIrBuilder.LevelId(ProjectId, 0) == IntentCompiler.Id(ProjectId, IntentCompiler.LevelKey("l0"))
            && FacadeIrBuilder.SpaceId(ProjectId, "living") == IntentCompiler.Id(ProjectId, IntentCompiler.SpaceKey("living")),
            "room keys are instance ids ('#' as '-'), level n is intent level l<n>, spaces are the intent space ids");

        var result = prototype is null ? null : FacadeIrBuilder.AddWindows(prototype, layout);
        Check(result is { Status: FacadeIrStatus.Complete, IsComplete: true, Project: not null } && result.Windows.Count == 2 && result.Violations.Count == 0,
            "windows are added to the flat prototype", Describe(result));
        var project = result?.Project;
        Check(project is not null && Compiler.Validate(project).Length == 0, "Compiler.Validate is clean on the result",
            project is null ? "" : string.Join(",", Compiler.Validate(project).Select(i => i.Code)));
        Check(project is not null && prototype is not null && project.Id == prototype.Id && project.Revision == prototype.Revision
            && project.Elements.Length == prototype.Elements.Length + 2 && project.Elements.Take(prototype.Elements.Length).Select(e => Json(e)).SequenceEqual(prototype.Elements.Select(e => Json(e))),
            "the prototype's elements are kept bit for bit and in order, the windows follow");
        var diff = project is null || prototype is null ? null : Safe(() => Compiler.Diff(prototype!, project!));
        Check(diff is not null && diff.Operations.Length == 2 && diff.Operations.All(o => o.Action == "create" && o.Element.Kind == "window"),
            "Compiler.Diff of prototype and result creates exactly the two windows");
        var livingWall = IntentCompiler.Id(ProjectId, IntentCompiler.WallKey("living", IntentSide.South));
        var bedroomWall = IntentCompiler.Id(ProjectId, IntentCompiler.WallKey("bedroom_1", IntentSide.South));
        var livingWindow = layout.Windows.Single(w => w.Instance == "living");
        var bedroomWindow = layout.Windows.Single(w => w.Instance == "bedroom_1");
        var living = result?.Windows.FirstOrDefault(w => w.Id == "window:living/1");
        var livingElement = project?.Elements.FirstOrDefault(e => living is not null && e.Id == living.ElementId);
        Check(living is not null && living.ElementId == IntentCompiler.Id(ProjectId, IntentCompiler.OpeningKey("window:living/1")) && living.HostWallId == livingWall
            && living.Instance == "living" && living.Level == 0 && Near(living.OffsetMetres, 2) && Near(living.WidthMetres, 2) && living.SillHeightMetres == 0.9
            && living.HeadHeightMetres == 2.1 && living.ShiftMetres == 0, "living window: hosted on wall:living:south at offset 2.00 m, width 2.00 m, sill 0.90, head 2.10", Describe(result));
        Check(livingElement is { Kind: "window" } && livingElement.GeometryMetres.Length == 6 && livingElement.GeometryMetres[0] == livingWindow.X0 && livingElement.GeometryMetres[1] == 0
            && livingElement.GeometryMetres[2] == livingWindow.X1 && livingElement.GeometryMetres[3] == 0 && livingElement.GeometryMetres[4] == 0.9
            && livingElement.GeometryMetres[5] == 2.1 - 0.9 && livingElement.Dependencies.SequenceEqual(new[] { FacadeIrBuilder.LevelId(ProjectId, 0), livingWall })
            && livingElement.Provenance == FacadeIrOptions.DefaultProvenance,
            "living window element: [x0,0,x1,0,sill,head-sill] on the host axis in its direction, dependencies [level, host wall]", livingElement is null ? "" : Nums(livingElement.GeometryMetres));
        var bedroom = result?.Windows.FirstOrDefault(w => w.Id == "window:bedroom_1/1");
        Check(bedroom is not null && bedroom.HostWallId == bedroomWall && Near(bedroom.OffsetMetres, bedroomWindow.X0 - 6) && Near(bedroom.WidthMetres, bedroomWindow.WidthMetres)
            && result!.Windows[0].Id == "window:bedroom_1/1", "bedroom window hosted on wall:bedroom_1:south at offset x0 - 6, windows in layout order", Describe(result));
        Check(result is not null && result.Checks.Count(c => c.Code == FacadeIrCodes.Window && c.Hard && c.Satisfied && c.Detail.Length > 0) == 2
            && !result.Checks.Any(c => c.Code == FacadeIrCodes.Moved), "each hosted window has a satisfied facade_ir_window check; none moved", Describe(result));
        Check(project is not null && OracleProblem(flat, layout, project, defaults) is null, "the flat windows agree with the IntentCompiler opening oracle",
            project is null ? "" : OracleProblem(flat, layout, project, defaults) ?? "");

        // P5-02 on the BIM-IR: facts read back from spaces and hosted windows.
        var facts = project is null ? Array.Empty<RoomFacts>() : FacadeIrBuilder.ToRoomFacts(project, flat, layout.Options.OpenableFraction);
        var livingFacts = facts.FirstOrDefault(f => f.Id == "living");
        Check(facts.Count == 5 && facts.Select(f => f.Id).SequenceEqual(new[] { "bathroom", "bedroom_1", "hall", "kitchen", "living" }) && livingFacts is not null
            && livingFacts.Name == "Living" && Near(livingFacts.AreaSquareMetres ?? 0, 5.85 * 3.85) && Near(livingFacts.MinSideMetres ?? 0, 3.85)
            && Near(livingFacts.WindowAreaSquareMetres ?? 0, 2.4) && Near(livingFacts.VentilationAreaSquareMetres ?? 0, 1.2)
            && facts.Single(f => f.Id == "hall").WindowAreaSquareMetres == 0, "room facts from the BIM-IR: clear space area and side, hosted window and openable areas",
            string.Join(" ", facts.Select(f => f.Id + ":" + F(f.AreaSquareMetres ?? -1) + "/" + F(f.WindowAreaSquareMetres ?? -1))));
        var report = SkillRoomValidator.Validate(caba, facts);
        var ratio = report.Findings.Where(f => f.Property is RoomFacts.WindowAreaKey or RoomFacts.VentilationAreaKey).ToArray();
        Check(ratio.Length >= 4 && ratio.All(f => f.Outcome == RoomCheckOutcome.Pass) && new[] { "living", "bedroom_1" }.All(room =>
                ratio.Any(f => f.RoomId == room && f.Property == RoomFacts.WindowAreaKey) && ratio.Any(f => f.RoomId == room && f.Property == RoomFacts.VentilationAreaKey)),
            "P5-02 SkillRoomValidator passes the illumination and ventilation rules of living and bedroom on the result", Findings(ratio));
        var bare = prototype is null ? Array.Empty<RoomFacts>() : FacadeIrBuilder.ToRoomFacts(prototype, flat, layout.Options.OpenableFraction);
        var bareRatio = SkillRoomValidator.Validate(caba, bare).Findings.Where(f => f.Property is RoomFacts.WindowAreaKey or RoomFacts.VentilationAreaKey).ToArray();
        Check(bareRatio.Length >= 4 && bareRatio.All(f => f.Outcome == RoomCheckOutcome.Fail), "without the windows the same rules fail on the prototype", Findings(bareRatio));

        // ---- entrance door (P7-04) ----
        var noMove = prototype is null ? null : FacadeIrBuilder.AddWindows(prototype, layout, Entrance("living", 2.2, 4.5, 2.2 + 0.9, 4.5, vertical: false));
        Check(noMove is { IsComplete: true } && result is not null && Json(noMove.Project!) == Json(result.Project!), "a door off the window line moves nothing", Describe(noMove));
        var moved = prototype is null ? null : FacadeIrBuilder.AddWindows(prototype, layout, Entrance("living", 2.2, 0, 3.1, 0));
        var movedLiving = moved?.Windows.FirstOrDefault(w => w.Id == "window:living/1");
        var movedElement = moved?.Project?.Elements.FirstOrDefault(e => movedLiving is not null && e.Id == movedLiving.ElementId);
        Check(moved is { IsComplete: true } && movedLiving is not null && movedLiving.HostWallId == livingWall && Near(movedLiving.ShiftMetres, 1.4) && Near(movedLiving.OffsetMetres, 3.4)
            && Near(movedLiving.WidthMetres, 2) && movedElement is not null && Near(movedElement.GeometryMetres[0], 3.4) && Near(movedElement.GeometryMetres[2], 5.4)
            && movedElement.GeometryMetres[1] == 0 && movedElement.GeometryMetres[3] == 0 && Compiler.Validate(moved.Project!).Length == 0,
            "a door at 2.20-3.10 m: the living window moves 1.40 m east to 3.40-5.40 m (west does not fit the 0.30 m end clearance)", Describe(moved));
        Check(moved is not null && moved.Checks.Any(c => c.Code == FacadeIrCodes.Moved && c.Subject == "window:living/1" && !c.Hard && c.Satisfied && c.Detail.Length > 0)
            && moved.Windows.Single(w => w.Id == "window:bedroom_1/1").ShiftMetres == 0, "the move is reported as a soft facade_ir_window_moved check; the bedroom window stays", Describe(moved));
        var movedFacts = moved?.Project is null ? Array.Empty<RoomFacts>() : FacadeIrBuilder.ToRoomFacts(moved.Project, flat, layout.Options.OpenableFraction);
        Check(SkillRoomValidator.Validate(caba, movedFacts).Findings.Where(f => f.Property is RoomFacts.WindowAreaKey or RoomFacts.VentilationAreaKey) is var movedRatio
            && movedRatio.Count() >= 4 && movedRatio.All(f => f.Outcome == RoomCheckOutcome.Pass), "a moved window keeps its area: P5-02 still passes");
        var blocked = prototype is null ? null : FacadeIrBuilder.AddWindows(prototype, layout, Entrance("living", 2.55, 0, 3.45, 0));
        Check(blocked is { Status: FacadeIrStatus.Failed, Project: null } && blocked.Checks.Any(c => c.Code == FacadeIrCodes.EntranceOverlap && c.Subject == "window:living/1" && c.Hard
                && !c.Satisfied && c.Detail.Length > 0) && blocked.Checks.Any(c => c.Code == FacadeIrCodes.Window && c.Subject == "window:bedroom_1/1" && c.Satisfied),
            "a door centred on the 6 m living facade leaves no room on either side: typed facade_ir_entrance_overlap failure", Describe(blocked));
        Check(prototype is not null && FacadeIrBuilder.AddWindows(prototype, layout, Entrance("living", 2.55, 0, 3.45, 0), defaults with { EntranceClearanceMetres = 0 }) is var tight
            && tight.IsComplete && tight.Windows.Single(w => w.Id == "window:living/1") is var tightLiving && Near(tightLiving.ShiftMetres, -1.45) && Near(tightLiving.OffsetMetres, 0.55),
            "with zero clearance both sides of the door fit and tie at 1.45 m: the window moves west to 0.55-2.55 m");
        var clear = prototype is null ? null : FacadeIrBuilder.AddWindows(prototype, layout, Entrance("living", 4.3, 0, 5.2, 0));
        Check(clear is { IsComplete: true } && clear.Windows.Single(w => w.Id == "window:living/1").ShiftMetres == 0 && !clear.Checks.Any(c => c.Code == FacadeIrCodes.Moved),
            "a door exactly the 0.30 m clearance past the window end moves nothing", Describe(clear));
        var nudged = prototype is null ? null : FacadeIrBuilder.AddWindows(prototype, layout, Entrance("living", 4.25, 0, 5.15, 0));
        Check(nudged is { IsComplete: true } && nudged.Windows.Single(w => w.Id == "window:living/1") is var nudgedLiving && Near(nudgedLiving.ShiftMetres, -0.05)
            && Near(nudgedLiving.OffsetMetres, 1.95), "a door 0.25 m past the window end: the window moves 0.05 m west to keep the 0.30 m clearance", Describe(nudged));

        // A wider living (8 m facade) with the door centred on the window: equal moves both ways, the lower coordinate wins.
        var wide = Plan("wide", 2, 12, 5, P("living", "Living", 0, 0, 8, 3), P("bathroom", "Bathroom", 0, 3, 4, 2), P("kitchen", "Kitchen", 4, 3, 4, 2),
            P("bedroom_1", "Bedroom 1", 8, 0, 4, 5));
        var wideLayout = FacadePlanner.Place(wide, caba);
        var wideLiving = wideLayout.Windows.FirstOrDefault(w => w.Instance == "living");
        Check(wideLayout.IsComplete && wideLiving is { Side: FacadeSide.South } && Near(wideLiving.X0, 3) && Near(wideLiving.X1, 5), "the wide living window is centred on its 8 m south facade",
            wideLiving is null ? "" : F(wideLiving.X0) + "-" + F(wideLiving.X1));
        var wideProto = Safe(() => FacadeIrBuilder.Prototype(ProjectId, 3, wide));
        var tie = wideProto is null ? null : FacadeIrBuilder.AddWindows(wideProto, wideLayout, Entrance("living", 3.55, 0, 4.45, 0));
        var tieLiving = tie?.Windows.FirstOrDefault(w => w.Id == "window:living/1");
        Check(tie is { IsComplete: true } && tieLiving is not null && Near(tieLiving.ShiftMetres, -1.75) && Near(tieLiving.OffsetMetres, 1.25) && tie.Project!.Revision == 3,
            "a centred door on an 8 m facade: west and east moves tie at 1.75 m, the window moves west to 1.25-3.25 m", Describe(tie));
        var nearEast = wideProto is null ? null : FacadeIrBuilder.AddWindows(wideProto, wideLayout, Entrance("living", 3.3, 0, 4.2, 0));
        Check(nearEast?.Windows.FirstOrDefault(w => w.Id == "window:living/1") is { } east && Near(east.ShiftMetres, 1.5) && Near(east.OffsetMetres, 4.5),
            "a door at 3.30-4.20 m: east (1.50 m) is nearer than west (2.00 m), the window moves east to 4.50-6.50 m", Describe(nearEast));

        // ---- an upper level over a lower one: level filter on collinear walls ----
        var stacked = new SpacePlan(SpacePlanStatus.Feasible, "stacked", 4, 2, new PlanFootprint(6, 4), 0.9,
            new[] { P("living", "Living", 0, 0, 6, 4), P("bedroom_1", "Bedroom 1", 0, 0, 4, 4, level: 1) }, Array.Empty<PlanConstraint>(), 0);
        var stackedLayout = FacadePlanner.Place(stacked, caba);
        var stackedProto = Safe(() => FacadeIrBuilder.Prototype(ProjectId, 0, stacked));
        var stackedResult = stackedProto is null ? null : FacadeIrBuilder.AddWindows(stackedProto, stackedLayout);
        var upper = stackedResult?.Windows.FirstOrDefault(w => w.Instance == "bedroom_1");
        var upperHost = stackedResult?.Project?.Elements.FirstOrDefault(e => upper is not null && e.Id == upper.HostWallId);
        Check(stackedLayout.IsComplete && stackedProto is not null && stackedProto.Elements.SingleOrDefault(e => e.Id == FacadeIrBuilder.LevelId(ProjectId, 1)) is { Kind: "level" } level1
            && level1.GeometryMetres.SequenceEqual(new[] { 2.6 }), "the stacked prototype has level 1 at 2.60 m");
        Check(stackedResult is { IsComplete: true } && upper is { Level: 1 } && upper.HostWallId == IntentCompiler.Id(ProjectId, IntentCompiler.WallKey("bedroom_1", IntentSide.South))
            && upperHost is not null && upperHost.Dependencies.SequenceEqual(new[] { FacadeIrBuilder.LevelId(ProjectId, 1) })
            && stackedResult.Project!.Elements.Single(e => e.Id == upper.ElementId).Dependencies[0] == FacadeIrBuilder.LevelId(ProjectId, 1),
            "the upper bedroom window is hosted on the level-1 wall, not on the collinear level-0 living wall", Describe(stackedResult));
        Check(stackedResult?.Project is not null && OracleProblem(stacked, stackedLayout, stackedResult.Project, defaults) is null, "the stacked windows agree with the IntentCompiler oracle",
            stackedResult?.Project is null ? "" : OracleProblem(stacked, stackedLayout, stackedResult.Project, defaults) ?? "");

        // ---- typed failures ----
        if (prototype is not null)
        {
            var off = layout with { Windows = layout.Windows.Select(w => w.Instance == "living" ? w with { Y0 = 0.5, Y1 = 0.5 } : w).ToArray() };
            var missing = FacadeIrBuilder.AddWindows(prototype, off);
            Check(missing.Status == FacadeIrStatus.Failed && missing.Project is null && missing.Checks.Any(c => c.Code == FacadeIrCodes.HostMissing && c.Subject == "window:living/1" && c.Hard && !c.Satisfied),
                "a window off every wall line has no host: facade_ir_host_missing", Describe(missing));
            var corner = layout with { Windows = layout.Windows.Select(w => w.Instance == "living" ? w with { X0 = 0.05, X1 = 2.05 } : w).ToArray() };
            Check(FacadeIrBuilder.AddWindows(prototype, corner) is { Status: FacadeIrStatus.Failed } c1 && c1.Checks.Any(c => c.Code == FacadeIrCodes.OutsideWall && c.Subject == "window:living/1"),
                "a window reaching into the corner wall's half thickness is outside the clear span: facade_ir_outside_wall");
            var lowProto = FacadeIrBuilder.Prototype(ProjectId, 0, flat, defaults with { WallHeightMetres = 2.0 });
            Check(FacadeIrBuilder.AddWindows(lowProto, layout, null, defaults with { WallHeightMetres = 2.0 }) is { Status: FacadeIrStatus.Failed } low
                && low.Checks.Count(c => c.Code == FacadeIrCodes.OutsideWall) == 2, "a 2.10 m head above 2.00 m walls is outside the wall");
            var twice = layout with { Windows = layout.Windows.Concat(new[] { livingWindow with { Id = "window:living/2", X0 = livingWindow.X0 + 1, X1 = livingWindow.X1 + 1 } }).ToArray() };
            Check(FacadeIrBuilder.AddWindows(prototype, twice) is { Status: FacadeIrStatus.Failed } overlap
                && overlap.Checks.Any(c => c.Code == FacadeIrCodes.WindowOverlap && c.Subject == "window:living/2" && !c.Satisfied),
                "two windows overlapping on one host: facade_ir_window_overlap on the later one");

            // ---- refusals ----
            void Refused(string label, FacadeIrResult refused, string code) =>
                Check(refused.Status == FacadeIrStatus.Refused && refused.Project is null && refused.Windows.Count == 0 && refused.Checks.Count > 0
                    && refused.Checks.All(c => c.Hard && !c.Satisfied) && refused.Checks.Any(c => c.Code == code && c.Detail.Length > 0), "refuses " + label, Describe(refused));
            Refused("an incomplete layout", FacadeIrBuilder.AddWindows(prototype, FacadePlanner.Place(flat, caba, new FacadeOptions(OpeningEdges: Array.Empty<LotEdge>()))), FacadeIrCodes.LayoutInvalid);
            Refused("a refused layout", FacadeIrBuilder.AddWindows(prototype, FacadePlanner.Place(flat, caba, new FacadeOptions(SillHeightMetres: -1))), FacadeIrCodes.LayoutInvalid);
            Refused("an invalid project", FacadeIrBuilder.AddWindows(prototype with { Elements = prototype.Elements.Concat(new[] { prototype.Elements[0] }).ToArray() }, layout), FacadeIrCodes.ProjectInvalid);
            Refused("a negative entrance clearance", FacadeIrBuilder.AddWindows(prototype, layout, null, defaults with { EntranceClearanceMetres = -0.1 }), FacadeIrCodes.OptionsInvalid);
            Refused("a non-finite clearance", FacadeIrBuilder.AddWindows(prototype, layout, null, defaults with { EntranceClearanceMetres = double.NaN }), FacadeIrCodes.OptionsInvalid);
            if (project is not null) Refused("windows added twice", FacadeIrBuilder.AddWindows(project, layout), FacadeIrCodes.ElementExists);
        }
        var nullThrows = false;
        try { FacadeIrBuilder.AddWindows(null!, layout); } catch (ArgumentNullException) { nullThrows = true; }
        Check(nullThrows, "a null project throws ArgumentNullException");
        var infeasibleThrows = false;
        try { FacadeIrBuilder.Prototype(ProjectId, 0, flat with { Status = SpacePlanStatus.Infeasible }); } catch (ArgumentException) { infeasibleThrows = true; }
        Check(infeasibleThrows, "an infeasible plan has no prototype (ArgumentException)");
        var thinThrows = false;
        try { FacadeIrBuilder.Prototype(ProjectId, 0, flat, defaults with { WallThicknessMetres = 0 }); } catch (ArgumentException) { thinThrows = true; }
        Check(thinThrows, "a zero wall thickness is refused (ArgumentException)");

        // ---- determinism ----
        var reversed = flat with { Rooms = Enumerable.Reverse(flat.Rooms).ToArray() };
        var again = FacadeIrBuilder.AddWindows(FacadeIrBuilder.Prototype(ProjectId, 0, reversed), FacadePlanner.Place(reversed, caba), Entrance("living", 2.2, 0, 3.1, 0));
        Check(moved?.Project is not null && again.Project is not null && Json(again.Project) == Json(moved.Project) && Show(again) == Show(moved),
            "the result does not depend on the order of the plan's rooms, nor on the run");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = prototype is null ? null : FacadeIrBuilder.AddWindows(prototype, layout, Entrance("living", 2.2, 0, 3.1, 0));
            Check(german is not null && moved is not null && Show(german) == Show(moved) && Json(german.Project!) == Json(moved.Project!), "the result is culture-invariant (de-DE)");
        }
        finally { CultureInfo.CurrentCulture = culture; }

        // ---- ranked 1/2/3-bedroom prototypes with their real circulation models ----
        int complete = 0, overlaps = 0, incomplete = 0, windows = 0, shifted = 0;
        string? problem = null;
        foreach (var (typology, lot) in new[] { (ApartmentTypology.OneBedroom, new PrototypeLot(8.66, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(8.66, 20)),
            (ApartmentTypology.ThreeBedroom, new PrototypeLot(8, 20)), (ApartmentTypology.TwoBedroom, new PrototypeLot(12, 30)) })
        {
            var generated = PrototypeGenerator.Generate(new PrototypeRequest(typology, lot, 4, Seed: 0));
            if (generated.Brief is null || generated.Alternatives is null) { problem ??= typology + " not generated"; continue; }
            foreach (var alternative in generated.Alternatives.Ranked)
            {
                var label = typology + " " + F(lot.WidthMetres) + "x" + F(lot.DepthMetres) + " alternative " + Text(alternative.Index) + ": ";
                var plan = alternative.Plan;
                var refusal = IntentCompiler.Validate(FacadeIrBuilder.Intent(plan));
                var proto = Safe(() => FacadeIrBuilder.Prototype(ProjectId, 0, plan));
                if (proto is null || Compiler.Validate(proto).Length > 0)
                {
                    problem ??= label + "prototype does not compile clean: " + string.Join("; ", refusal.Select(i => i.Code + "@" + i.Key + " " + i.Detail))
                        + " rooms " + string.Join(" ", plan.Rooms.Select(r => r.InstanceId + "(" + F(r.Rect.X) + "," + F(r.Rect.Y) + "," + F(r.Rect.Width) + "," + F(r.Rect.Depth) + ")"));
                    continue;
                }
                var placed = FacadePlanner.Place(plan, caba);
                if (!placed.IsComplete) { incomplete++; continue; }
                var circulation = CirculationAnalyzer.Analyze(generated.Brief, plan);
                var added = FacadeIrBuilder.AddWindows(proto, placed, circulation);
                if (added.Status == FacadeIrStatus.Failed && added.Violations.All(v => v.Code == FacadeIrCodes.EntranceOverlap)) { overlaps++; continue; }
                if (added.Project is not { } done) { problem ??= label + Describe(added); continue; }
                complete++;
                windows += added.Windows.Count;
                shifted += added.Windows.Count(w => w.ShiftMetres != 0);
                if (Compiler.Validate(done).Length > 0) problem ??= label + "Compiler.Validate not clean";
                if (added.Windows.Count != placed.Windows.Count) problem ??= label + "window count";
                var findings = SkillRoomValidator.Validate(caba, FacadeIrBuilder.ToRoomFacts(done, plan, placed.Options.OpenableFraction)).Findings
                    .Where(f => f.Property is RoomFacts.WindowAreaKey or RoomFacts.VentilationAreaKey).ToArray();
                var compliant = placed.Rooms.Where(r => r.Status == FacadeRoomStatus.Compliant).Select(r => r.Instance).ToArray();
                if (findings.Any(f => f.Outcome != RoomCheckOutcome.Pass) || compliant.Any(room => !findings.Any(f => f.RoomId == room && f.Property == RoomFacts.WindowAreaKey)))
                    problem ??= label + "P5-02 ratio findings " + Findings(findings);
                if (circulation.Entrance is { } door && EntranceClash(door, added, done, defaults.EntranceClearanceMetres) is { } clash) problem ??= label + clash;
                var unmoved = placed with { Windows = placed.Windows.Where(w => added.Windows.Single(a => a.Id == w.Id).ShiftMetres == 0).ToArray() };
                if (OracleProblem(plan, unmoved, done, defaults) is { } oracle) problem ??= label + oracle;
                var reorderedPlan = plan with { Rooms = Enumerable.Reverse(plan.Rooms).ToArray() };
                var reordered = FacadeIrBuilder.AddWindows(FacadeIrBuilder.Prototype(ProjectId, 0, reorderedPlan), FacadePlanner.Place(reorderedPlan, caba), circulation);
                if (reordered.Project is null || Json(reordered.Project) != Json(done) || Show(reordered) != Show(added)) problem ??= label + "not deterministic under room reordering";
            }
        }
        Check(problem is null && complete >= 1 && windows >= 2 * complete, "ranked prototype layouts get hosted windows: Compiler.Validate clean, P5-02 ratios pass, entrance clear, oracle agrees",
            (problem ?? "") + " complete " + Text(complete) + " entrance-blocked " + Text(overlaps) + " incomplete " + Text(incomplete) + " windows " + Text(windows) + " moved " + Text(shifted));
    }

    // Independent oracle: the same windows compiled by IntentCompiler as intent openings on their room sides (offset from
    // the side's lower corner) must land on the same host wall, with the same level and geometry (within 1e-9 m), and the
    // non-opening elements must equal the project's other elements. Windows are matched by element id order of the keys.
    static string? OracleProblem(SpacePlan plan, FacadeLayout layout, Project project, FacadeIrOptions options)
    {
        var rooms = plan.Rooms.ToDictionary(r => r.InstanceId, StringComparer.Ordinal);
        var openings = layout.Windows.Select((w, i) =>
        {
            var rect = rooms[w.Instance].Rect;
            var horizontal = w.Side is FacadeSide.South or FacadeSide.North;
            var side = w.Side switch { FacadeSide.South => IntentSide.South, FacadeSide.East => IntentSide.East, FacadeSide.North => IntentSide.North, _ => IntentSide.West };
            return new IntentOpening("w" + i.ToString("D3", CultureInfo.InvariantCulture), IntentOpeningKind.Window, new IntentRoomSide(FacadeIrBuilder.RoomKey(w.Instance), side),
                horizontal ? w.X0 - rect.X : w.Y0 - rect.Y, horizontal ? w.X1 - w.X0 : w.Y1 - w.Y0, w.HeadHeightMetres - w.SillHeightMetres, w.SillHeightMetres);
        }).ToArray();
        Project compiled;
        try { compiled = IntentCompiler.Compile(project.Id, project.Revision, FacadeIrBuilder.Intent(plan, options) with { Openings = openings }); }
        catch (IntentException error) { return "oracle intent refused: " + string.Join(",", error.Issues.Select(i => i.Code + "@" + i.Key + " " + i.Detail)); }
        var ours = project.Elements.Where(e => e.Kind == "window").ToDictionary(e => e.Id);
        var walls = compiled.Elements.Where(e => e.Kind != "window").Select(e => Json(e)).ToArray();
        var others = project.Elements.Where(e => e.Kind != "window").Select(e => Json(e)).ToArray();
        if (!walls.SequenceEqual(others)) return "oracle non-window elements differ";
        for (var i = 0; i < layout.Windows.Count; i++)
        {
            var expected = compiled.Elements.Single(e => e.Id == IntentCompiler.Id(project.Id, IntentCompiler.OpeningKey(openings[i].Key)));
            if (!ours.TryGetValue(IntentCompiler.Id(project.Id, IntentCompiler.OpeningKey(layout.Windows[i].Id)), out var actual)) return "missing element of " + layout.Windows[i].Id;
            if (!actual.Dependencies.SequenceEqual(expected.Dependencies)) return layout.Windows[i].Id + " host/level differs from IntentCompiler";
            if (actual.GeometryMetres.Length != 6 || actual.GeometryMetres.Zip(expected.GeometryMetres).Any(p => Math.Abs(p.First - p.Second) > Tolerance))
                return layout.Windows[i].Id + " geometry " + Nums(actual.GeometryMetres) + " vs IntentCompiler " + Nums(expected.GeometryMetres);
        }
        return null;
    }

    // A window on the entrance door's level and line must stay at least the clearance away from the door stretch.
    static string? EntranceClash(CirculationDoor door, FacadeIrResult result, Project project, double clearance)
    {
        var horizontal = door.Y0 == door.Y1;
        foreach (var window in result.Windows.Where(w => w.Level == door.Level))
        {
            var g = project.Elements.Single(e => e.Id == window.ElementId).GeometryMetres;
            if (horizontal != (g[1] == g[3])) continue;
            if (Math.Abs((horizontal ? g[1] - door.Y0 : g[0] - door.X0)) > 1e-7) continue;
            double low = horizontal ? Math.Min(g[0], g[2]) : Math.Min(g[1], g[3]), high = horizontal ? Math.Max(g[0], g[2]) : Math.Max(g[1], g[3]);
            double d0 = horizontal ? door.X0 : door.Y0, d1 = horizontal ? door.X1 : door.Y1;
            if (Math.Min(high, d1 + clearance) - Math.Max(low, d0 - clearance) > 1e-7) return window.Id + " within the entrance clearance";
        }
        return null;
    }

    static CirculationModel Entrance(string room, double x0, double y0, double x1, double y1, bool vertical = false) =>
        new(CirculationStatus.Valid, "test", 0, 0.9, new CirculationDoor(CirculationModel.EntranceDoorId, CirculationModel.Exterior, room, 0, x0, y0, x1, y1, true),
            vertical ? LotEdge.West : LotEdge.South, Array.Empty<CirculationDoor>(), Array.Empty<CirculationRoute>(), Array.Empty<PlanConstraint>());

    static PlannedRoom P(string id, string name, double x, double y, double width, double depth, int level = 0) => new(id, id, name, 1, level, new PlanRect(x, y, width, depth));

    static SpacePlan Plan(string name, int seed, double width, double depth, params PlannedRoom[] rooms) =>
        new(SpacePlanStatus.Feasible, name, seed, 1, new PlanFootprint(width, depth), 0.9, rooms, Array.Empty<PlanConstraint>(), 0);

    static T? Safe<T>(Func<T> make) where T : class
    {
        try { return make(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException) { return null; }
    }

    static string Json<T>(T value) => JsonSerializer.Serialize(value);
    static string Show(FacadeIrResult result) => Json(result.Windows) + Json(result.Checks) + result.Status;
    static string Kinds(Project project) => string.Join(",", project.Elements.GroupBy(e => e.Kind).Select(g => g.Key + "=" + Text(g.Count())));
    static string Findings(IEnumerable<RoomFinding> findings) => string.Join(" ", findings.Select(f => f.RoomId + "/" + f.Property + "=" + f.Outcome));
    static string Describe(FacadeIrResult? result) => result is null ? "null" : result.Status + " | " + string.Join(" ", result.Windows.Select(w => w.Id + "@" + F(w.OffsetMetres) + "+" + F(w.WidthMetres)
        + (w.ShiftMetres != 0 ? " shift " + F(w.ShiftMetres) : ""))) + " | " + string.Join("; ", result.Checks.Where(c => !c.Satisfied || c.Code == FacadeIrCodes.Moved).Select(c => c.Code + "@" + c.Subject + " " + c.Detail));
    static string Nums(double[] values) => "[" + string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
    static bool Near(double value, double expected) => Math.Abs(value - expected) <= Tolerance;
    static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
