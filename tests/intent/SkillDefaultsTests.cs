using Bim.Core;
using System.Globalization;
using System.Text.Json;

// P5-04 oracle: resolved style skill defaults (skills/bim.style.*) fill the unspecified fields of a layout intent draft
// before the P3-01 compiler runs, with per-field provenance (fill rule and skill default traces); explicit values win and
// are reported as explicit; one draft under two styles compiles to two different, byte-stable BIM-IR projects; a missing
// default for an open field is a typed default_missing naming the field; invalid or self-inconsistent defaults are refused
// with typed codes. Unit-tested only.
static class SkillDefaultsTests
{
    const string Compact = "bim.style.compact-modern";
    const string Warm = "bim.style.warm-masonry";
    static readonly Provenance Origin = new("skill-defaults-tests", "unit");
    static readonly Guid ProjectId = Guid.Parse("5b1f2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d");

    static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) return directory.FullName;
        throw new DirectoryNotFoundException("repository root with AGENTS.md not found above " + AppContext.BaseDirectory);
    }

    // Level l1 (storey 0) and l2 (storey 1) with open elevations; exterior type "ext" fully open, interior "int" named.
    // l1: a (4x3) and b (3x3) share x=4; l2: u (4x3) with interior walls. w1 is an open window on b south, d1 a door on
    // the shared wall with an explicit height and an open sill.
    static IntentDraft Draft(IntentDraftLevel[]? levels = null, IntentDraftWallType[]? types = null, IntentDraftOpening[]? openings = null) => new(
        LayoutIntent.CurrentVersion, Origin,
        levels ?? new[] { new IntentDraftLevel("l1", "Ground", null, 0), new IntentDraftLevel("l2", "First", null, 1) },
        types ?? new[] { new IntentDraftWallType("ext", null, IntentWallRole.Exterior), new IntentDraftWallType("int", "Partition", IntentWallRole.Interior) },
        new[]
        {
            new IntentRoom("a", "Room a", "l1", "ext", 0, 0, 4, 3),
            new IntentRoom("b", "Room b", "l1", "ext", 4, 0, 3, 3),
            new IntentRoom("u", "Room u", "l2", "int", 0, 0, 4, 3),
        },
        openings ?? new[]
        {
            new IntentDraftOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("b", IntentSide.South), 0.5, 1.2),
            new IntentDraftOpening("d1", IntentOpeningKind.Door, new IntentSharedWall("a", "b"), 1, 0.9, 2.1),
        });

    static SkillManifest Style(string id, IEnumerable<KeyValuePair<string, SkillValue>> defaults) =>
        new(SkillManifest.CurrentVersion, id, new SkillVersion(1, 0, 0), SkillKind.Style, "test style", 30,
            Array.Empty<SkillRequirement>(), Array.Empty<string>(), Array.Empty<SkillParameter>(),
            defaults.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new SkillDefault(pair.Key, pair.Value)).ToArray(), Array.Empty<SkillRule>());

    static SkillResolution Resolve(SkillCatalog catalog, params string[] ids) => SkillResolver.Resolve(catalog, ids.Select(id => new SkillRequest(id)).ToArray());

    static string Issues(IntentDefaultsResult result) => string.Join(";", result.Issues.Select(issue => issue.Code + "@" + issue.Key + "(" + issue.Detail + ")"));

    static string ShowSource(IntentFieldSource? source) => source is null ? "missing"
        : source.Field + "=" + source.Value + " " + source.Origin + "/" + source.Rule + " [" + string.Join(",", source.Defaults.Select(trace => trace.ToString())) + "]";

    static double[]? Geometry(Project project, string key) => project.Elements.FirstOrDefault(element => element.Id == IntentCompiler.Id(ProjectId, key))?.GeometryMetres;

    static bool Near(double[]? actual, int index, double expected) => actual is not null && actual.Length > index && Math.Abs(actual[index] - expected) <= 1e-9;

    static string Show(double[]? values) => values is null ? "missing" : "[" + string.Join(",", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";

    public static void Run(Action<bool, string, string> sink)
    {
        void check(bool condition, string name, string detail = "") => sink(condition, "skill-defaults: " + name, detail);
        SkillCatalog catalog;
        try { catalog = SkillLoader.LoadDirectory(Path.Combine(RepoRoot(), "skills")); }
        catch (SkillException error) { check(false, "skills/ loads", error.Code + " " + error.File + " at " + error.Field); return; }
        var compactManifest = catalog.Find(Compact);
        var warmManifest = catalog.Find(Warm);
        check(compactManifest is not null && warmManifest is not null, "both sample style packs load");
        if (compactManifest is null || warmManifest is null) return;
        var compact = Resolve(catalog, Compact);
        var warm = Resolve(catalog, Warm);
        check(compact.Succeeded && warm.Succeeded, "each style resolves on its own");

        IntentDefaultsResult Fill(IntentDraft draft, SkillResolution resolution)
        {
            try { return IntentSkillDefaults.Fill(draft, resolution); }
            catch (Exception error)
            {
                return new IntentDefaultsResult(null, Array.Empty<IntentFieldSource>(), new[] { new IntentIssue("unexpected " + error.GetType().Name, "", error.Message) });
            }
        }
        string? CompileCode(IntentDraft draft, SkillResolution resolution)
        {
            try { IntentSkillDefaults.Compile(ProjectId, 1, draft, resolution); return null; }
            catch (IntentException error) { return error.Code + "@" + error.Field; }
            catch (Exception error) { return "unexpected " + error.GetType().Name + ": " + error.Message; }
        }
        IntentDefaultsCompilation? Compiled(IntentDraft draft, SkillResolution resolution, string name)
        {
            try { return IntentSkillDefaults.Compile(ProjectId, 1, draft, resolution); }
            catch (IntentException error) { check(false, name + " compiles", error.Code + "@" + error.Field + " " + string.Join(";", error.Issues.Select(issue => issue.Code + "@" + issue.Key))); }
            catch (Exception error) { check(false, name + " compiles", "unexpected " + error.GetType().Name + ": " + error.Message); }
            return null;
        }

        // ---- Fill under the compact style ----
        var draft = Draft();
        var filled = Fill(draft, compact);
        check(filled.Succeeded, "compact style fills the open draft", Issues(filled));
        var intent = filled.Intent;
        if (intent is not null)
        {
            var ext = intent.WallTypes.FirstOrDefault(type => type.Key == "ext");
            var interior = intent.WallTypes.FirstOrDefault(type => type.Key == "int");
            check(ext == new IntentWallType("ext", "reinforced-concrete-150-etics-50", 0.2, 2.9), "exterior wall type takes the style's type name, thickness and level height", ext?.ToString() ?? "missing");
            check(interior == new IntentWallType("int", "Partition", 0.1, 2.9), "interior wall type keeps its explicit name and takes the interior thickness", interior?.ToString() ?? "missing");
            var levels = intent.Levels.ToDictionary(level => level.Key, StringComparer.Ordinal);
            check(levels.GetValueOrDefault("l1")?.ElevationMetres == 0 && levels.GetValueOrDefault("l2")?.ElevationMetres == 2.9,
                "ground storey sits at 0 and storey 1 at one level height", string.Join(",", intent.Levels.Select(level => level.Key + "=" + level.ElevationMetres.ToString("R", CultureInfo.InvariantCulture))));
            var w1 = intent.Openings.FirstOrDefault(opening => opening.Key == "w1");
            var d1 = intent.Openings.FirstOrDefault(opening => opening.Key == "d1");
            check(w1 is not null && w1.SillMetres == 0.3 && w1.HeightMetres == 2.2, "window sill from the style and height = head - sill", w1?.ToString() ?? "missing");
            check(d1 is not null && d1.SillMetres == 0 && d1.HeightMetres == 2.1, "door sill is 0 on its level and its explicit height is kept", d1?.ToString() ?? "missing");
            check(intent.Rooms.SequenceEqual(draft.Rooms) && intent.Provenance == Origin && intent.Version == LayoutIntent.CurrentVersion, "rooms, provenance and version pass through unchanged");
        }

        // ---- Per-field provenance ----
        var expectedFields = new[]
        {
            "levels[l1].elevation_m", "levels[l2].elevation_m",
            "openings[d1].height_m", "openings[d1].sill_m", "openings[w1].height_m", "openings[w1].sill_m",
            "wall_types[ext].height_m", "wall_types[ext].name", "wall_types[ext].thickness_m",
            "wall_types[int].height_m", "wall_types[int].name", "wall_types[int].thickness_m",
        };
        check(filled.Sources.Select(source => source.Field).SequenceEqual(expectedFields), "every defaultable field is reported once, in ordinal field order",
            string.Join(",", filled.Sources.Select(source => source.Field)));
        SkillTrace T(string skill, string key) => new(skill, SkillTrace.DefaultOrigin, key);
        bool Is(IntentDefaultsResult result, string field, string value, string origin, string rule, params SkillTrace[] defaults) =>
            result.Source(field) is { } source && source.Value == value && source.Origin == origin && source.Rule == rule && source.Defaults.SequenceEqual(defaults);
        check(Is(filled, "wall_types[ext].thickness_m", "0.2", IntentFieldSource.DefaultOrigin, "wall-thickness-from-role", T(Compact, IntentSkillDefaults.ExteriorThickness)),
            "exterior thickness traces to the compact exterior thickness default", ShowSource(filled.Source("wall_types[ext].thickness_m")));
        check(Is(filled, "wall_types[int].thickness_m", "0.1", IntentFieldSource.DefaultOrigin, "wall-thickness-from-role", T(Compact, IntentSkillDefaults.InteriorThickness)),
            "interior thickness traces to the interior thickness default", ShowSource(filled.Source("wall_types[int].thickness_m")));
        check(Is(filled, "wall_types[ext].name", "\"reinforced-concrete-150-etics-50\"", IntentFieldSource.DefaultOrigin, "wall-type-name-from-role", T(Compact, IntentSkillDefaults.ExteriorType)),
            "exterior type name traces to the wall type default", ShowSource(filled.Source("wall_types[ext].name")));
        check(Is(filled, "wall_types[int].name", "\"Partition\"", IntentFieldSource.ExplicitOrigin, IntentFieldSource.ExplicitRule),
            "an explicit name is reported as explicit with no default", ShowSource(filled.Source("wall_types[int].name")));
        check(Is(filled, "wall_types[ext].height_m", "2.9", IntentFieldSource.DefaultOrigin, "wall-height-from-level", T(Compact, IntentSkillDefaults.LevelHeight)),
            "wall height traces to the level height default", ShowSource(filled.Source("wall_types[ext].height_m")));
        check(Is(filled, "levels[l1].elevation_m", "0", IntentFieldSource.RuleOrigin, "level-elevation-ground"),
            "the ground storey elevation is a rule fill that reads no default", ShowSource(filled.Source("levels[l1].elevation_m")));
        check(Is(filled, "levels[l2].elevation_m", "2.9", IntentFieldSource.DerivedOrigin, "level-elevation-from-storey", T(Compact, IntentSkillDefaults.LevelHeight)),
            "an upper storey elevation is derived from the level height default", ShowSource(filled.Source("levels[l2].elevation_m")));
        check(Is(filled, "openings[w1].sill_m", "0.3", IntentFieldSource.DefaultOrigin, "window-sill-from-style", T(Compact, IntentSkillDefaults.WindowSill)),
            "window sill traces to the sill default", ShowSource(filled.Source("openings[w1].sill_m")));
        check(Is(filled, "openings[w1].height_m", "2.2", IntentFieldSource.DerivedOrigin, "window-height-from-head", T(Compact, IntentSkillDefaults.WindowHead), T(Compact, IntentSkillDefaults.WindowSill)),
            "window height is derived from the head and sill defaults", ShowSource(filled.Source("openings[w1].height_m")));
        check(Is(filled, "openings[d1].sill_m", "0", IntentFieldSource.RuleOrigin, "door-sill-on-level"), "door sill is the on-level rule", ShowSource(filled.Source("openings[d1].sill_m")));
        check(Is(filled, "openings[d1].height_m", "2.1", IntentFieldSource.ExplicitOrigin, IntentFieldSource.ExplicitRule), "explicit door height is reported as explicit", ShowSource(filled.Source("openings[d1].height_m")));
        check(filled.Source("wall_types[ext].thickness_m")?.Defaults.FirstOrDefault()?.ToString() == Compact + ":default:" + IntentSkillDefaults.ExteriorThickness,
            "default traces print as skill:default:key", filled.Source("wall_types[ext].thickness_m")?.Defaults.FirstOrDefault()?.ToString() ?? "none");

        // ---- Two styles, two deterministic BIM-IR outputs ----
        var compactBuilt = Compiled(draft, compact, "compact draft");
        var warmBuilt = Compiled(draft, warm, "warm draft");
        if (compactBuilt is not null && warmBuilt is not null)
        {
            check(Compiler.Validate(compactBuilt.Project).Length == 0 && Compiler.Validate(warmBuilt.Project).Length == 0, "both compiled projects pass Compiler.Validate");
            var wall = Geometry(compactBuilt.Project, "wall:a|b");
            check(Near(wall, 4, 2.9) && Near(wall, 5, 0.2), "compact shared wall is 2.9 m high and 0.2 m thick", Show(wall));
            var warmWall = Geometry(warmBuilt.Project, "wall:a|b");
            check(Near(warmWall, 4, 3.3) && Near(warmWall, 5, 0.45), "warm shared wall is 3.3 m high and 0.45 m thick", Show(warmWall));
            var interiorWall = Geometry(warmBuilt.Project, "wall:u:south");
            check(Near(interiorWall, 4, 3.3) && Near(interiorWall, 5, 0.15), "warm interior wall is 0.15 m thick", Show(interiorWall));
            check(Near(Geometry(compactBuilt.Project, "level:l2"), 0, 2.9) && Near(Geometry(warmBuilt.Project, "level:l2"), 0, 3.3), "upper level elevation follows each style's level height",
                Show(Geometry(compactBuilt.Project, "level:l2")) + " " + Show(Geometry(warmBuilt.Project, "level:l2")));
            var compactWindow = Geometry(compactBuilt.Project, "opening:w1");
            var warmWindow = Geometry(warmBuilt.Project, "opening:w1");
            check(Near(compactWindow, 4, 0.3) && Near(compactWindow, 5, 2.2) && Near(warmWindow, 4, 0.9) && Near(warmWindow, 5, 1.5),
                "window sill/height: compact 0.3/2.2, warm 0.9/1.5", Show(compactWindow) + " " + Show(warmWindow));
            var compactJson = JsonSerializer.Serialize(compactBuilt.Project);
            var warmJson = JsonSerializer.Serialize(warmBuilt.Project);
            check(compactJson != warmJson, "same draft, two styles: two different BIM-IR projects");
            var again = Compiled(draft, compact, "compact repeat");
            var warmAgain = Compiled(draft, warm, "warm repeat");
            check(again is not null && JsonSerializer.Serialize(again.Project) == compactJson && warmAgain is not null && JsonSerializer.Serialize(warmAgain.Project) == warmJson,
                "each style's project JSON is byte-stable on repeat");
            check(again is not null && JsonSerializer.Serialize(again.Sources) == JsonSerializer.Serialize(compactBuilt.Sources), "field sources are byte-stable on repeat");
            var shuffled = draft with { Levels = draft.Levels.Reverse().ToArray(), WallTypes = draft.WallTypes.Reverse().ToArray(), Rooms = draft.Rooms.Reverse().ToArray(), Openings = draft.Openings.Reverse().ToArray() };
            var shuffledBuilt = Compiled(shuffled, compact, "shuffled draft");
            check(shuffledBuilt is not null && JsonSerializer.Serialize(shuffledBuilt.Project) == compactJson && JsonSerializer.Serialize(shuffledBuilt.Sources) == JsonSerializer.Serialize(compactBuilt.Sources),
                "draft list order changes neither the project nor the sources");
            check(JsonSerializer.Serialize(IntentCompiler.Compile(ProjectId, 1, compactBuilt.Intent)) == compactJson, "the wrapper's project is exactly IntentCompiler.Compile of the filled intent");
        }

        // ---- Explicit values win ----
        var explicitDraft = Draft(
            new[] { new IntentDraftLevel("l1", "Ground", 0), new IntentDraftLevel("l2", "First", 3.1, 1) },
            new[] { new IntentDraftWallType("ext", "Block", IntentWallRole.Exterior, 0.3, 3.0), new IntentDraftWallType("int", "Partition", IntentWallRole.Interior, 0.12, 3.0) },
            new[]
            {
                new IntentDraftOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("b", IntentSide.South), 0.5, 1.2, 1.1, 1.0),
                new IntentDraftOpening("d1", IntentOpeningKind.Door, new IntentSharedWall("a", "b"), 1, 0.9, 2.1, 0),
            });
        var explicitCompact = Compiled(explicitDraft, compact, "fully explicit draft (compact)");
        var explicitWarm = Compiled(explicitDraft, warm, "fully explicit draft (warm)");
        if (explicitCompact is not null && explicitWarm is not null)
        {
            check(JsonSerializer.Serialize(explicitCompact.Project) == JsonSerializer.Serialize(explicitWarm.Project), "a fully explicit draft compiles identically under both styles");
            check(explicitCompact.Sources.Count == 12 && explicitCompact.Sources.All(source => source.Origin == IntentFieldSource.ExplicitOrigin && source.Rule == IntentFieldSource.ExplicitRule && source.Defaults.Count == 0),
                "every field of a fully explicit draft is reported explicit with no default", string.Join(",", explicitCompact.Sources.Where(source => source.Origin != IntentFieldSource.ExplicitOrigin).Select(ShowSource)));
            var wall = Geometry(explicitWarm.Project, "wall:a|b");
            check(Near(wall, 4, 3.0) && Near(wall, 5, 0.3) && Near(Geometry(explicitWarm.Project, "level:l2"), 0, 3.1), "explicit thickness, height and elevation reach BIM-IR", Show(wall));
        }
        var partial = Draft(types: new[] { new IntentDraftWallType("ext", null, IntentWallRole.Exterior, 0.3), new IntentDraftWallType("int", "Partition", IntentWallRole.Interior) },
            openings: new[]
            {
                new IntentDraftOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("b", IntentSide.South), 0.5, 1.2, null, 1.0),
                new IntentDraftOpening("d1", IntentOpeningKind.Door, new IntentSharedWall("a", "b"), 1, 0.9, 2.1),
            });
        var partialCompact = Fill(partial, compact);
        var partialWarm = Fill(partial, warm);
        check(partialCompact.Succeeded && partialWarm.Succeeded, "a partly explicit draft fills under both styles", Issues(partialCompact) + Issues(partialWarm));
        check(partialCompact.Intent?.WallTypes.First(type => type.Key == "ext").ThicknessMetres == 0.3 && partialWarm.Intent?.WallTypes.First(type => type.Key == "ext").ThicknessMetres == 0.3
            && Is(partialWarm, "wall_types[ext].thickness_m", "0.3", IntentFieldSource.ExplicitOrigin, IntentFieldSource.ExplicitRule),
            "an explicit thickness beats both styles' defaults and is reported explicit", ShowSource(partialWarm.Source("wall_types[ext].thickness_m")));
        check(Is(partialCompact, "openings[w1].sill_m", "1", IntentFieldSource.ExplicitOrigin, IntentFieldSource.ExplicitRule)
            && Is(partialCompact, "openings[w1].height_m", "1.5", IntentFieldSource.DerivedOrigin, "window-height-from-head", T(Compact, IntentSkillDefaults.WindowHead))
            && Is(partialWarm, "openings[w1].height_m", "1.4", IntentFieldSource.DerivedOrigin, "window-height-from-head", T(Warm, IntentSkillDefaults.WindowHead)),
            "an explicit sill is kept and the open height runs from it to the style head", ShowSource(partialCompact.Source("openings[w1].height_m")) + " | " + ShowSource(partialWarm.Source("openings[w1].height_m")));

        // ---- Missing defaults are typed refusals naming the field ----
        var openDoor = Draft(openings: new[] { new IntentDraftOpening("d1", IntentOpeningKind.Door, new IntentSharedWall("a", "b"), 1, 0.9) });
        var doorMissing = Fill(openDoor, compact);
        check(!doorMissing.Succeeded && doorMissing.Intent is null && doorMissing.Issues.Count == 1 && doorMissing.Issues[0].Code == IntentDefaultCodes.DefaultMissing
            && doorMissing.Issues[0].Key == "openings[d1].height_m" && doorMissing.Issues[0].Detail.Contains(IntentSkillDefaults.DoorHeight, StringComparison.Ordinal),
            "an open door height without a door.height_m default is default_missing naming the field and key", Issues(doorMissing));
        check(CompileCode(openDoor, compact) == IntentDefaultCodes.DefaultMissing + "@openings[d1].height_m", "Compile throws the typed refusal with the field", CompileCode(openDoor, compact) ?? "compiled");
        var unnamedInterior = Draft(types: new[] { new IntentDraftWallType("ext", null, IntentWallRole.Exterior), new IntentDraftWallType("int", null, IntentWallRole.Interior) });
        var nameMissing = Fill(unnamedInterior, warm);
        check(nameMissing.Issues.Count == 1 && nameMissing.Issues[0].Code == IntentDefaultCodes.DefaultMissing && nameMissing.Issues[0].Key == "wall_types[int].name"
            && nameMissing.Issues[0].Detail.Contains(IntentSkillDefaults.InteriorType, StringComparison.Ordinal),
            "an open interior wall type name without wall.interior.type is default_missing", Issues(nameMissing));
        var compactDefaults = compactManifest.Defaults.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        SkillResolution Custom(Action<Dictionary<string, SkillValue>> change)
        {
            var defaults = new Dictionary<string, SkillValue>(compactDefaults, StringComparer.Ordinal);
            change(defaults);
            return Resolve(new SkillCatalog(new[] { Style("bim.style.test", defaults) }), "bim.style.test");
        }
        var noSill = Fill(draft, Custom(defaults => defaults.Remove(IntentSkillDefaults.WindowSill)));
        check(noSill.Intent is null && noSill.Issues.Count == 1 && noSill.Issues[0].Code == IntentDefaultCodes.DefaultMissing && noSill.Issues[0].Key == "openings[w1].sill_m"
              && noSill.Issues[0].Detail.Contains(IntentSkillDefaults.WindowSill, StringComparison.Ordinal),
            "an open window sill without a sill default is default_missing (no silent 0); the open height still needs the sill", Issues(noSill));
        var noLevel = Fill(draft, Custom(defaults => defaults.Remove(IntentSkillDefaults.LevelHeight)));
        check(noLevel.Intent is null && noLevel.Issues.Select(issue => issue.Code + "@" + issue.Key).SequenceEqual(new[]
            {
                IntentDefaultCodes.DefaultMissing + "@levels[l2].elevation_m",
                IntentDefaultCodes.DefaultMissing + "@wall_types[ext].height_m",
                IntentDefaultCodes.DefaultMissing + "@wall_types[int].height_m",
            }), "without level.height_m every field that needs it is refused (ground storey still fills), sorted", Issues(noLevel));
        var explicitWithoutDefaults = Fill(explicitDraft, Resolve(new SkillCatalog(new[] { Style("bim.style.empty", Array.Empty<KeyValuePair<string, SkillValue>>()) }), "bim.style.empty"));
        check(explicitWithoutDefaults.Succeeded, "a fully explicit draft needs no defaults", Issues(explicitWithoutDefaults));

        // ---- Invalid and inconsistent defaults are refused with typed codes ----
        void Refused(string name, Action<Dictionary<string, SkillValue>> change, string code, string key)
        {
            var result = Fill(draft, Custom(change));
            check(result.Intent is null && result.Issues.Any(issue => issue.Code == code && issue.Key == key), name, Issues(result));
        }
        Refused("sill equal to head is default_inconsistent", defaults => defaults[IntentSkillDefaults.WindowSill] = SkillValue.Of(2.5), IntentDefaultCodes.DefaultInconsistent, IntentSkillDefaults.WindowSill);
        Refused("sill above head is default_inconsistent", defaults => defaults[IntentSkillDefaults.WindowSill] = SkillValue.Of(2.55), IntentDefaultCodes.DefaultInconsistent, IntentSkillDefaults.WindowSill);
        Refused("zero thickness is default_value_invalid", defaults => defaults[IntentSkillDefaults.ExteriorThickness] = SkillValue.Of(0), IntentDefaultCodes.DefaultValueInvalid, IntentSkillDefaults.ExteriorThickness);
        Refused("negative interior thickness is default_value_invalid", defaults => defaults[IntentSkillDefaults.InteriorThickness] = SkillValue.Of(-0.1), IntentDefaultCodes.DefaultValueInvalid, IntentSkillDefaults.InteriorThickness);
        Refused("negative sill is default_value_invalid", defaults => defaults[IntentSkillDefaults.WindowSill] = SkillValue.Of(-0.1), IntentDefaultCodes.DefaultValueInvalid, IntentSkillDefaults.WindowSill);
        Refused("non-positive level height is default_value_invalid", defaults => defaults[IntentSkillDefaults.LevelHeight] = SkillValue.Of(0), IntentDefaultCodes.DefaultValueInvalid, IntentSkillDefaults.LevelHeight);
        Refused("text thickness is default_type_invalid", defaults => defaults[IntentSkillDefaults.ExteriorThickness] = SkillValue.Of("thick"), IntentDefaultCodes.DefaultTypeInvalid, IntentSkillDefaults.ExteriorThickness);
        Refused("boolean head is default_type_invalid", defaults => defaults[IntentSkillDefaults.WindowHead] = SkillValue.Of(true), IntentDefaultCodes.DefaultTypeInvalid, IntentSkillDefaults.WindowHead);
        Refused("numeric wall type name is default_type_invalid", defaults => defaults[IntentSkillDefaults.ExteriorType] = SkillValue.Of(5), IntentDefaultCodes.DefaultTypeInvalid, IntentSkillDefaults.ExteriorType);
        Refused("blank wall type name is default_value_invalid", defaults => defaults[IntentSkillDefaults.ExteriorType] = SkillValue.Of("  "), IntentDefaultCodes.DefaultValueInvalid, IntentSkillDefaults.ExteriorType);
        Refused("clear height above level height is default_inconsistent", defaults => defaults[IntentSkillDefaults.ClearHeight] = SkillValue.Of(3.0), IntentDefaultCodes.DefaultInconsistent, IntentSkillDefaults.ClearHeight);
        Refused("window head above clear height is default_inconsistent", defaults => defaults[IntentSkillDefaults.WindowHead] = SkillValue.Of(2.7), IntentDefaultCodes.DefaultInconsistent, IntentSkillDefaults.WindowHead);
        var badThickness = Custom(defaults => defaults[IntentSkillDefaults.ExteriorThickness] = SkillValue.Of(-1));
        check(CompileCode(draft, badThickness) == IntentDefaultCodes.DefaultValueInvalid + "@" + IntentSkillDefaults.ExteriorThickness, "Compile refuses an invalid default before compiling",
            CompileCode(draft, badThickness) ?? "compiled");
        var derived = Fill(Draft(openings: new[] { new IntentDraftOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("b", IntentSide.South), 0.5, 1.2, null, 2.5) }), compact);
        check(derived.Intent is null && derived.Issues.Count == 1 && derived.Issues[0].Code == IntentDefaultCodes.DerivedValueInvalid && derived.Issues[0].Key == "openings[w1].height_m",
            "an explicit sill at the default head leaves no window height: derived_value_invalid", Issues(derived));
        var both = Resolve(catalog, Compact, Warm);
        var conflicted = Fill(draft, both);
        check(!both.Succeeded && conflicted.Intent is null && conflicted.Issues.Count == 1 && conflicted.Issues[0].Code == IntentDefaultCodes.ResolutionFailed
            && conflicted.Issues[0].Detail.Contains(SkillCodes.SkillConflict, StringComparison.Ordinal),
            "a failed resolution (conflicting styles) is defaults_resolution_failed, not a partial fill", Issues(conflicted));

        // ---- Review fixes: storeys, duplicates, explicit height with open sill, strict validation, rounding ----
        IntentDraftLevel L(string key, double? elevation, int? storey) => new(key, "Level " + key, elevation, storey);
        string Keys(IntentDefaultsResult result) => string.Join(";", result.Issues.Select(issue => issue.Code + "@" + issue.Key));
        var noStorey = Fill(Draft(levels: new[] { L("l1", null, null), L("l2", null, null) }), compact);
        check(noStorey.Intent is null && Keys(noStorey) == IntentDefaultCodes.DefaultMissing + "@levels[l1].elevation_m;" + IntentDefaultCodes.DefaultMissing + "@levels[l2].elevation_m"
            && noStorey.Issues.All(issue => issue.Detail.Contains("storey", StringComparison.Ordinal)),
            "open elevations without a storey are default_missing naming each field (no silent 0 m)", Issues(noStorey));
        var sameStorey = Fill(Draft(levels: new[] { L("l1", null, 0), L("l2", null, 1), L("l3", null, 1) }), compact);
        check(sameStorey.Intent is null && Keys(sameStorey) == IntentDefaultCodes.StoreyDuplicate + "@levels[l2].storey" && sameStorey.Issues[0].Detail.Contains("l3", StringComparison.Ordinal),
            "two levels on one storey are storey_duplicate on the smaller key, naming the other", Issues(sameStorey));
        var negative = Fill(Draft(levels: new[] { L("b1", null, -1), L("l1", null, 0), L("l2", null, 1) }), compact);
        check(negative.Intent is null && Keys(negative) == IntentDefaultCodes.StoreyInvalid + "@levels[b1].storey", "a negative storey is storey_invalid (basements take an explicit elevation without a storey)", Issues(negative));
        var basement = Fill(Draft(levels: new[] { L("b1", -3, null), L("l1", null, 0), L("l2", null, 1) }), compact);
        check(basement.Succeeded && Is(basement, "levels[b1].elevation_m", "-3", IntentFieldSource.ExplicitOrigin, IntentFieldSource.ExplicitRule),
            "an explicit elevation needs no storey", Issues(basement));
        var stacked = Fill(Draft(levels: new[] { L("l1", null, 0), L("l2", 3.1, 1), L("l3", null, 2), L("l4", null, 3) }), compact);
        check(stacked.Succeeded && Is(stacked, "levels[l3].elevation_m", "6", IntentFieldSource.DerivedOrigin, "level-elevation-above-explicit", T(Compact, IntentSkillDefaults.LevelHeight))
            && Is(stacked, "levels[l4].elevation_m", "8.9", IntentFieldSource.DerivedOrigin, "level-elevation-above-explicit", T(Compact, IntentSkillDefaults.LevelHeight)),
            "open storeys above an explicit level stack from it: 3.1 + 2.9 = 6, 3.1 + 2 x 2.9 = 8.9", Issues(stacked) + " " + ShowSource(stacked.Source("levels[l3].elevation_m")) + " " + ShowSource(stacked.Source("levels[l4].elevation_m")));
        var tall = Fill(Draft(levels: new[] { L("l1", null, 0), L("l2", null, 1), L("l4", null, 3) }), compact);
        check(Is(tall, "levels[l4].elevation_m", "8.7", IntentFieldSource.DerivedOrigin, "level-elevation-from-storey", T(Compact, IntentSkillDefaults.LevelHeight)),
            "storey 3 x 2.9 is exactly 8.7 (rounded to 1e-9)", ShowSource(tall.Source("levels[l4].elevation_m")));
        var inverted = Fill(Draft(levels: new[] { L("l1", null, 0), L("l2", null, 1), L("l3", 2.5, 2) }), compact);
        check(inverted.Intent is null && Keys(inverted) == IntentDefaultCodes.StoreyOrderInvalid + "@levels[l3].elevation_m",
            "an explicit elevation below a lower storey's elevation is storey_order_invalid", Issues(inverted));
        var explicitInverted = Fill(Draft(levels: new[] { L("l1", 4, 0), L("l2", 3, 1) }), compact);
        check(Keys(explicitInverted) == IntentDefaultCodes.StoreyOrderInvalid + "@levels[l2].elevation_m", "explicit elevations must rise with their storeys", Issues(explicitInverted));
        check(Fill(Draft(levels: new[] { L("l1", 0, 0), L("l2", 3.1, 1) }), compact).Succeeded, "explicit elevations rising with their storeys fill");
        var warmFilled = Fill(draft, warm);
        check(Is(warmFilled, "openings[w1].height_m", "1.5", IntentFieldSource.DerivedOrigin, "window-height-from-head", T(Warm, IntentSkillDefaults.WindowHead), T(Warm, IntentSkillDefaults.WindowSill))
            && Is(warmFilled, "levels[l2].elevation_m", "3.3", IntentFieldSource.DerivedOrigin, "level-elevation-from-storey", T(Warm, IntentSkillDefaults.LevelHeight)),
            "warm head 2.4 - sill 0.9 is exactly 1.5", ShowSource(warmFilled.Source("openings[w1].height_m")));
        var dupTypes = Fill(Draft(types: new[] { new IntentDraftWallType("ext", null, IntentWallRole.Exterior), new IntentDraftWallType("ext", "Other", IntentWallRole.Interior), new IntentDraftWallType("int", "Partition", IntentWallRole.Interior) }), compact);
        check(dupTypes.Intent is null && Keys(dupTypes) == IntentDefaultCodes.DraftKeyDuplicate + "@wall_types[ext]", "a duplicate wall type key is draft_key_duplicate", Issues(dupTypes));
        var dupLevels = Fill(Draft(levels: new[] { L("l1", null, 0), L("l1", null, 1) }), compact);
        check(Keys(dupLevels) == IntentDefaultCodes.DraftKeyDuplicate + "@levels[l1]", "a duplicate level key is draft_key_duplicate", Issues(dupLevels));
        var dupOpenings = Fill(Draft(openings: new[]
        {
            new IntentDraftOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("b", IntentSide.South), 0.5, 1.2),
            new IntentDraftOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("a", IntentSide.South), 0.5, 1.2),
        }), compact);
        check(Keys(dupOpenings) == IntentDefaultCodes.DraftKeyDuplicate + "@openings[w1]", "a duplicate opening key is draft_key_duplicate", Issues(dupOpenings));
        IntentDraft WindowHeight(double height) => Draft(openings: new[] { new IntentDraftOpening("w1", IntentOpeningKind.Window, new IntentRoomSide("b", IntentSide.South), 0.5, 1.2, height) });
        var fits = Fill(WindowHeight(2.0), compact);
        check(fits.Succeeded && Is(fits, "openings[w1].sill_m", "0.3", IntentFieldSource.DefaultOrigin, "window-sill-from-style", T(Compact, IntentSkillDefaults.WindowSill))
            && Is(fits, "openings[w1].height_m", "2", IntentFieldSource.ExplicitOrigin, IntentFieldSource.ExplicitRule),
            "an explicit window height with an open sill takes the style sill", ShowSource(fits.Source("openings[w1].sill_m")));
        check(Fill(WindowHeight(2.2), compact).Succeeded, "style sill + explicit height exactly at the style head fills");
        var overHead = Fill(WindowHeight(2.3), compact);
        check(overHead.Intent is null && Keys(overHead) == IntentDefaultCodes.DerivedValueInvalid + "@openings[w1].sill_m"
            && overHead.Issues[0].Detail.Contains(IntentSkillDefaults.WindowHead, StringComparison.Ordinal),
            "style sill + explicit height above the style head is derived_value_invalid on the sill", Issues(overHead));
        var strict = Fill(explicitDraft, Custom(defaults => defaults[IntentSkillDefaults.ExteriorThickness] = SkillValue.Of(0)));
        check(strict.Intent is null && Keys(strict) == IntentDefaultCodes.DefaultValueInvalid + "@" + IntentSkillDefaults.ExteriorThickness,
            "strict: an invalid recognised default refuses even a fully explicit draft that would not read it", Issues(strict));
        string? failedField = "unset";
        try { IntentSkillDefaults.Compile(ProjectId, 1, draft, both); }
        catch (IntentException error) { failedField = error.Field; }
        check(conflicted.Issues.Count == 1 && conflicted.Issues[0].Key == "" && failedField is null,
            "defaults_resolution_failed has key \"\" and Compile throws it with a null Field", failedField ?? "null");

        // ---- Invariant culture ----
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = Fill(draft, compact);
            check(german.Succeeded && JsonSerializer.Serialize(german.Sources) == JsonSerializer.Serialize(filled.Sources) && german.Source("openings[w1].height_m")?.Value == "2.2",
                "values and sources are invariant under de-DE");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
