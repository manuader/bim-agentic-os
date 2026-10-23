using Bim.Core;
using Bim.Ifc;
using System.Reflection;
using System.Text.Json;

namespace Bim.TypedGeometry.Tests;

// ADR0008 option A, steps 2-5: typed units and per-kind geometry views at API boundaries only.
public static class TypedContracts
{
    static readonly Provenance Fixture = new("typed geometry fixture", "specified");

    static Element Make(string kind, double[] geometry) => new(Guid.NewGuid(), kind, geometry, [], Fixture);

    static bool BitEqual(double[] expected, double[] actual) =>
        expected.Length == actual.Length && expected.Zip(actual).All(pair => BitConverter.DoubleToInt64Bits(pair.First) == BitConverter.DoubleToInt64Bits(pair.Second));

    public static void Run(Suite suite, string root)
    {
        Units(suite);
        Codec(suite, root);
        PersistedBoundary(suite);
        Capabilities(suite, root);
        Semantic(suite);
        IfcRectangle(suite);
    }

    static void Units(Suite suite)
    {
        suite.Check(Length.FromMillimetres(400).Metres == 0.4, "400 mm is exactly 0.4 m");
        suite.Check(Length.FromMetres(0.2).Metres == 0.2, "FromMetres keeps the metre value");
        suite.Throws<ArgumentException>("FromMillimetres rejects NaN", () => Length.FromMillimetres(double.NaN));
        suite.Throws<ArgumentException>("FromMetres rejects infinity", () => Length.FromMetres(double.PositiveInfinity));
        suite.Check(LengthUnits.NormalizeMillimetres(Length.FromMillimetres(5400).Millimetres) == 5400, "5400 mm round-trips through metres exactly");
        suite.Check(LengthUnits.NormalizeMillimetres(Length.FromMetres(5.1000000000000005).Millimetres) == 5100, "binary noise of compiled axis normalizes to 5100 mm");
        suite.Check(LengthUnits.NormalizeMillimetres(0.25) == 0.25, "normalization keeps real sub-millimetre values");
        suite.Throws<ArgumentException>("normalization rejects non-finite millimetres", () => LengthUnits.NormalizeMillimetres(double.NaN));
        suite.Check(Math.Abs(Length.FromMetres(1).Feet - 3.280839895) < 1e-8, "metres to feet unchanged");
        suite.Check(LengthUnits.ToLength(400, LengthUnits.Millimetres) == Length.FromMetres(0.4) && LengthUnits.ToLength(5.4, LengthUnits.Metres).Metres == 5.4, "explicit unit tags decode to Length");
        suite.Throws<ArgumentException>("implicit or unknown unit tags rejected", () => LengthUnits.ToLength(1, "ft"), "unsupported_length_unit");
        suite.Check(Area.Of(Length.FromMetres(5), Length.FromMetres(4)).SquareMetres == 20, "area of clear rectangle");
        suite.Throws<ArgumentException>("area rejects non-finite", () => Area.FromSquareMetres(double.NaN));
        suite.Check(Angle.FromDegrees(180).Radians == Math.PI && Angle.FromRadians(Math.PI).Degrees == 180, "angle degrees and radians");
        suite.Throws<ArgumentException>("angle rejects non-finite", () => Angle.FromDegrees(double.NegativeInfinity));
    }

    static void Codec(Suite suite, string root)
    {
        var rejected = new (string Name, Element Element, string Code)[]
        {
            ("wall arity 5", Make("wall", [0, 0, 5, 0, 3]), "wall_geometry"),
            ("wall arity 7", Make("wall", [0, 0, 5, 0, 3, .2, 1]), "wall_geometry"),
            ("wall zero height", Make("wall", [0, 0, 5, 0, 0, .2]), "wall_geometry"),
            ("wall negative thickness", Make("wall", [0, 0, 5, 0, 3, -.2]), "wall_geometry"),
            ("wall degenerate axis", Make("wall", [1, 1, 1, 1, 3, .2]), "wall_geometry"),
            ("wall non-finite", Make("wall", [0, 0, double.NaN, 0, 3, .2]), "geometry_non_finite"),
            ("level arity 0", Make("level", []), "level_geometry"),
            ("level arity 2", Make("level", [0, 1]), "level_geometry"),
            ("level non-finite", Make("level", [double.PositiveInfinity]), "geometry_non_finite"),
            ("space arity 3", Make("space", [0, 0, 5]), "space_geometry"),
            ("space arity 5", Make("space", [0, 0, 5, 4, 1]), "space_geometry"),
            ("plan with geometry", Make("plan", [1]), "documentation_geometry"),
            ("unknown kind", Make("roof", [0]), "geometry_kind_unsupported"),
        };
        foreach (var (name, element, code) in rejected)
        {
            suite.Throws<ArgumentException>("codec rejects " + name + " as " + code, () => GeometryCodec.Decode(element), code);
            suite.Equal("codec reports " + name + " without throwing", code, GeometryCodec.Problem(element) ?? "none");
        }
        suite.Check(GeometryCodec.ExpectedArity("wall") == 6 && GeometryCodec.ExpectedArity("level") == 1 && GeometryCodec.ExpectedArity("space") == 4
            && GeometryCodec.ExpectedArity("plan") == 0 && GeometryCodec.ExpectedArity("schedule") == 0 && GeometryCodec.ExpectedArity("sheet") == 0
            && GeometryCodec.ExpectedArity("roof") is null, "single per-kind arity table");

        // Compiler.Validate keeps its wall rule but takes it from the codec: both must agree.
        var walls = new double[][] { [0, 0, 5, 0, 3, .2], [0, 0, 5, 0, 3], [0, 0, 5, 0, 3, .2, 1], [0, 0, 5, 0, 0, .2], [0, 0, 5, 0, 3, 0], [2, 3, 2, 3, 3, .2], [2, 3, 2, 4, 3, .2] };
        foreach (var geometry in walls)
        {
            var project = new Project(Guid.NewGuid(), 0, [Make("wall", geometry)]);
            var validateRejects = Compiler.Validate(project).Any(issue => issue.Code == "wall_geometry");
            suite.Check(validateRejects == !GeometryCodec.IsValidWall(geometry) && validateRejects == (GeometryCodec.Problem(project.Elements[0]) == "wall_geometry"), "validate and codec agree on wall [" + string.Join(",", geometry) + "]");
        }
        var explicitWalls = new (string Name, double[] Geometry, string[] ValidateCodes, string? CodecCode)[]
        {
            ("NaN end x", new double[] { 0, 0, double.NaN, 0, 3, .2 }, new[] { "units" }, "geometry_non_finite"),
            ("NaN height", new double[] { 0, 0, 5, 0, double.NaN, .2 }, new[] { "units" }, "geometry_non_finite"),
            ("-0.0 start equals 0 end", new double[] { -0.0, 0, 0, 0, 3, .2 }, new[] { "wall_geometry" }, "wall_geometry"),
            ("-0.0 thickness", new double[] { 0, 0, 5, 0, 3, -0.0 }, new[] { "wall_geometry" }, "wall_geometry"),
            ("-0.0 coordinate on a valid axis", new double[] { -0.0, 0, 5, 0, 3, .2 }, Array.Empty<string>(), null),
        };
        foreach (var (name, geometry, validateCodes, codecCode) in explicitWalls)
        {
            var wall = Make("wall", geometry);
            var codes = Compiler.Validate(new Project(Guid.NewGuid(), 0, [wall])).Select(issue => issue.Code).Order(StringComparer.Ordinal).ToArray();
            suite.Check(codes.SequenceEqual(validateCodes), "validate codes for wall " + name + ": " + string.Join(",", codes));
            suite.Equal("codec code for wall " + name, codecCode ?? "none", GeometryCodec.Problem(wall) ?? "none");
        }

        var intent = new RoomIntent(Guid.Parse("a71c442d-5181-4b11-9663-a4257ac1261f"), 5, 4, 3, .2, -.2, -.2, "m", new("IFC evaluation", "fixed evaluation fixture"));
        var projectId = Guid.Parse("66ef39da-f724-4328-b055-c33e78dbb2ed");
        var projects = new[]
        {
            RoomCompiler.Compile(projectId, 0, intent),
            RoomCompiler.Compile(projectId, 0, intent with { WidthMetres = 5.4 }),
            RoomCompiler.Compile(projectId, 7, intent with { WidthMetres = 6.4 }),
            RoomCompiler.Compile(Guid.Parse("00000000-0000-0000-0000-000000000001"), 0, Goldens.RoomIntentExample(root)),
            RoomCompiler.Compile(Guid.NewGuid(), 0, new RoomIntent(Guid.Parse("11111111-1111-1111-1111-111111111111"), 5, 4, 3, .2, 10, 20, "m", new("architect", "approved"))),
            Goldens.Example(root, "before.json"),
            Goldens.Example(root, "after.json"),
            new Project(Guid.NewGuid(), 0, [Make("level", [-0.0]), Make("wall", [-0.0, 0.1 + 0.2, 1e-300, double.Epsilon, 3, .2])]),
        };
        var roundTrips = projects.SelectMany(project => project.Elements).All(element => BitEqual(element.GeometryMetres, GeometryCodec.Encode(GeometryCodec.Decode(element))));
        suite.Check(roundTrips, "Encode(Decode(e)) is bit-identical for every compiled and example element");
        foreach (var project in projects)
        {
            var reencoded = project with { Elements = project.Elements.Select(element => element with { GeometryMetres = GeometryCodec.Encode(GeometryCodec.Decode(element)) }).ToArray() };
            suite.Check(JsonSerializer.Serialize(reencoded) == JsonSerializer.Serialize(project) && Compiler.Hash(reencoded) == Compiler.Hash(project), "re-encoded project serializes byte-identical (" + project.Id + ")");
        }

        var room = projects[0];
        var south = (WallGeometry)GeometryCodec.Decode(room.Elements.Single(e => e.Id == RoomCompiler.Id(intent.RoomId, "south")));
        suite.Check(south.Start == new Point2(Length.FromMetres(-0.1), Length.FromMetres(-0.1)) && south.Height == Length.FromMetres(3) && south.Thickness == Length.FromMillimetres(200), "wall view exposes typed axis points, height and thickness");
        var space = (SpaceGeometry)GeometryCodec.Decode(room.Elements.Single(e => e.Kind == "space"));
        suite.Check(space.InteriorCorner == new Point2(Length.FromMetres(0), Length.FromMetres(0)) && space.Width == Length.FromMetres(5) && space.ClearArea.SquareMetres == 20, "space view exposes interior corner, clear size and area");
        var level = (LevelGeometry)GeometryCodec.Decode(room.Elements.Single(e => e.Kind == "level"));
        suite.Check(level.Elevation == Length.FromMetres(0) && level.Kind == "level", "level view exposes elevation");
        suite.Check(GeometryCodec.Decode(room.Elements.Single(e => e.Kind == "sheet")) is DocumentationGeometry { Kind: "sheet" }, "documentation kinds decode to empty geometry");
        suite.Check(south.Fields().Select(field => field.Name).SequenceEqual(new[] { "start.x", "start.y", "end.x", "end.y", "height", "thickness" }), "wall field names follow the persisted array order");
        suite.Throws<ArgumentException>("encode rejects foreign geometry kinds", () => GeometryCodec.Encode(new ForeignGeometry()), "geometry_kind_unsupported");
    }

    sealed record ForeignGeometry : ElementGeometry
    {
        public override string Kind => "roof";
        public override GeometryField[] Fields() => [];
    }

    static void PersistedBoundary(Suite suite)
    {
        var typed = new[] { typeof(Length), typeof(Area), typeof(Angle), typeof(Point2), typeof(ElementGeometry), typeof(GeometryField), typeof(SemanticDiff), typeof(GeometryChange), typeof(ElementChange), typeof(Capability), typeof(CapabilityRegistry) };
        var persisted = new[] { typeof(Project), typeof(Element), typeof(Operation), typeof(ExecutionPlan), typeof(ProjectStore.State), typeof(Provenance), typeof(RoomIntent), typeof(NativeBatchRequest), typeof(IfcCompiledRequest), typeof(CommandResult), typeof(ValidationIssue) };
        foreach (var type in persisted)
        {
            var reachable = Reachable(type);
            var leaked = reachable.Where(candidate => typed.Any(t => t.IsAssignableFrom(candidate))).Select(candidate => candidate.Name).ToArray();
            suite.Check(leaked.Length == 0, "persisted " + type.Name + " exposes no typed geometry or unit" + (leaked.Length == 0 ? "" : ": " + string.Join(",", leaked)));
        }
        var elementKeys = typeof(Element).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        suite.Check(elementKeys.SequenceEqual(new[] { "Dependencies", "GeometryMetres", "Id", "Kind", "Provenance" }), "Element keeps exactly the keys the IFC adapter accepts");
    }

    static HashSet<Type> Reachable(Type start)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!seen.Add(type)) continue;
            if (type.IsArray && type.GetElementType() is Type element) pending.Push(element);
            foreach (var argument in type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes) pending.Push(argument);
            if (Nullable.GetUnderlyingType(type) is Type underlying) pending.Push(underlying);
            if (type.Namespace is null || !type.Namespace.StartsWith("Bim", StringComparison.Ordinal)) continue;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) pending.Push(property.PropertyType);
        }
        return seen;
    }

    static void Capabilities(Suite suite, string root)
    {
        var parsed = CapabilityRegistry.Parse(File.ReadAllText(Path.Combine(root, "capabilities", "capabilities.json")));
        suite.Check(parsed.Ids.Order(StringComparer.Ordinal).SequenceEqual(CapabilityRegistry.NativeDefault.Ids.Order(StringComparer.Ordinal)), "built-in native registry mirrors capabilities.json ids");
        foreach (var capability in parsed.Capabilities)
        {
            var mirror = CapabilityRegistry.NativeDefault.Capabilities.SingleOrDefault(candidate => candidate.Id == capability.Id);
            suite.Check(mirror is not null && mirror.CoverageStatus == capability.CoverageStatus && mirror.NativeVerified == capability.NativeVerified && mirror.VersionScope.SequenceEqual(capability.VersionScope),
                "built-in mirror matches coverage_status, version_scope and native_verified of " + capability.Id);
        }
        suite.Check(parsed.NativeCreateKinds.Order(StringComparer.Ordinal).SequenceEqual(new[] { "level", "wall" }), "capabilities.json declares native create for level and wall only");
        suite.Check(parsed.Capabilities.All(capability => !capability.NativeVerified && capability.CoverageStatus == "source-implemented" && capability.VersionScope.Length > 0), "capability records carry coverage and unverified native state");
        suite.Check(parsed.SupportsNativeKind("wall") && !parsed.SupportsNativeKind("space") && !parsed.SupportsNativeKind("plan"), "registry answers native kind support");
        suite.Throws<ArgumentException>("malformed capabilities rejected", () => CapabilityRegistry.Parse("{\"capabilities\":[{\"coverage_status\":\"x\",\"native_verified\":false}]}"), "invalid_capabilities");
        suite.Throws<ArgumentException>("non-json capabilities rejected", () => CapabilityRegistry.Parse("not json"), "invalid_capabilities");
        suite.Throws<ArgumentException>("duplicate capability ids rejected", () => CapabilityRegistry.Parse("{\"capabilities\":[{\"id\":\"a\",\"coverage_status\":\"x\",\"native_verified\":false},{\"id\":\"a\",\"coverage_status\":\"x\",\"native_verified\":false}]}"), "duplicate_capability");

        var projectId = Guid.NewGuid();
        var empty = new Project(projectId, 0, []);
        var room = RoomCompiler.Compile(projectId, 0, new RoomIntent(Guid.Parse("11111111-1111-1111-1111-111111111111"), 5, 4, 3, .2, 10, 20, "m", new("architect", "approved")));
        var level = new Element(Guid.NewGuid(), "level", [0], [], Fixture);
        var goodLevel = new Project(projectId, 0, [level]);
        var badLevel = goodLevel with { Elements = [level with { GeometryMetres = [0, 1] }] };
        foreach (var (name, before, after, code) in new[] { ("documentation and space block native room batch", empty, room, "unsupported_native_kind"), ("native preflight validates new state of retained identities", goodLevel, badLevel, "invalid_level") })
        {
            var plan = Compiler.Diff(before, after);
            var builtIn = NativeBatchContract.Preflight(before, after, plan);
            var driven = NativeBatchContract.Preflight(before, after, plan, parsed);
            suite.Check(JsonSerializer.Serialize(builtIn) == JsonSerializer.Serialize(driven) && driven.Any(issue => issue.Code == code), "registry-driven preflight yields identical issues: " + name);
        }
        var wallOnly = new CapabilityRegistry(parsed.Capabilities.Where(capability => capability.Id != "revit.level.create"));
        suite.Check(NativeBatchContract.Preflight(empty, goodLevel, Compiler.Diff(empty, goodLevel), wallOnly).Any(issue => issue.Code == "unsupported_native_kind" && issue.Id == level.Id), "preflight follows the supplied registry");
    }

    static void Semantic(Suite suite)
    {
        var intent = new RoomIntent(Guid.Parse("a71c442d-5181-4b11-9663-a4257ac1261f"), 5, 4, 3, .2, -.2, -.2, "m", new("IFC evaluation", "fixed evaluation fixture"));
        var projectId = Guid.Parse("66ef39da-f724-4328-b055-c33e78dbb2ed");
        var before = RoomCompiler.Compile(projectId, 0, intent);
        var after = RoomCompiler.Compile(projectId, 0, intent with { WidthMetres = 5.4 });
        var plan = Compiler.Diff(before, after);
        var planJson = JsonSerializer.Serialize(plan);
        var diff = SemanticDiff.Compute(before, after, plan);
        Guid Role(string role) => RoomCompiler.Id(intent.RoomId, role);
        var changed = diff.Elements.Select(element => element.Id).ToArray();
        suite.Check(changed.SequenceEqual(new[] { Role("south"), Role("east"), Role("north"), Role("space") }), "semantic diff covers the three moved walls and the space in plan order");
        suite.Check(diff.Elements.All(element => element.Action == "update" && element.Changes.Length > 0 && element.Changes.All(change => change.Describe() == "+0.400 m")), "every reported change is +0.400 m");
        var space = diff.Elements.Single(element => element.Id == Role("space"));
        suite.Check(space.Changes.Length == 1 && space.Changes[0].Field == "width" && space.Changes[0].Before == Length.FromMetres(5) && space.Changes[0].After == Length.FromMetres(5.4), "space reports width 5 m to 5.4 m");
        suite.Check(diff.Elements.Single(element => element.Id == Role("south")).Changes.Select(change => change.Field).SequenceEqual(new[] { "end.x" }), "south wall reports its moved end");
        suite.Check(diff.Elements.Single(element => element.Id == Role("east")).Changes.Select(change => change.Field).SequenceEqual(new[] { "start.x", "end.x" }), "east wall reports both moved points");
        suite.Check(!changed.Contains(Role("west")) && !changed.Contains(Role("level")), "west and level report nothing");
        suite.Check(diff.BeforeHash == plan.BeforeHash && diff.AfterHash == plan.AfterHash && JsonSerializer.Serialize(plan) == planJson && planJson == JsonSerializer.Serialize(Compiler.Diff(before, after)), "plan JSON unchanged beside semantic diff");
        suite.Throws<InvalidOperationException>("semantic diff rejects a tampered plan", () => SemanticDiff.Compute(before, after, plan with { Operations = [] }), "plan_mismatch");
        var created = SemanticDiff.Compute(new Project(projectId, 0, []), before, Compiler.Diff(new Project(projectId, 0, []), before));
        var createdSpace = created.Elements.Single(element => element.Id == Role("space"));
        suite.Check(createdSpace.Action == "create" && createdSpace.Changes.All(change => change.Before is null && change.After is not null) && createdSpace.Changes.Single(change => change.Field == "width").Describe() == "= 5.000 m", "creates report typed initial values");
        suite.Check(SemanticDiff.Compute(before, before, Compiler.Diff(before, before)).Elements.Length == 0, "no-op plan has empty semantic diff");
        suite.Check(diff.Elements.All(element => element.GeometryProblem is null), "decodable changes carry no geometry problem");

        var wallId = Guid.NewGuid();
        var levelId = Guid.NewGuid();
        var wall = new Element(wallId, "wall", [0, 0, 5, 0, 3, .2], [], Fixture);
        var level = new Element(levelId, "level", [0], [], Fixture);
        var start = new Project(projectId, 0, [wall, level]);
        ElementChange Only(Project next)
        {
            var nextPlan = Compiler.Diff(start, next);
            return SemanticDiff.Compute(start, next, nextPlan).Elements.Single();
        }
        var deleted = Only(start with { Elements = [level] });
        suite.Check(deleted.Action == "delete" && deleted.Id == wallId && deleted.GeometryProblem is null && deleted.Changes.Length == 6
            && deleted.Changes.All(change => change.After is null && change.Before is not null) && deleted.Changes.Single(change => change.Field == "end.x").Describe() == "was 5.000 m", "deletes report typed prior values");
        var rekinded = Only(start with { Elements = [new Element(wallId, "space", [0, 0, 5, 4], [], Fixture), level] });
        suite.Check(rekinded.Action == "update" && rekinded.Kind == "space" && rekinded.GeometryProblem is null && rekinded.Changes.Length == 10
            && rekinded.Changes.Take(6).All(change => change.After is null) && rekinded.Changes.Skip(6).All(change => change.Before is null), "kind change reports removed wall fields then created space fields");
        var unknown = Only(start with { Elements = [wall, level, new Element(Guid.NewGuid(), "roof", [1], [], Fixture)] });
        suite.Check(unknown.Action == "create" && unknown.Kind == "roof" && unknown.Changes.Length == 0 && unknown.GeometryProblem == "geometry_kind_unsupported", "validate-accepted unknown kind is reported without typed fields");
        var unknownUpdate = Only(start with { Elements = [new Element(wallId, "beam", [0, 0, 5, 0, 3, .2], [], Fixture), level] });
        suite.Check(unknownUpdate.Action == "update" && unknownUpdate.Changes.Length == 0 && unknownUpdate.GeometryProblem == "geometry_kind_unsupported", "update into an unknown kind is reported without typed fields");
        var twoValueLevel = Only(start with { Elements = [wall, level with { GeometryMetres = [0, 1] }] });
        suite.Check(twoValueLevel.Action == "update" && twoValueLevel.Id == levelId && twoValueLevel.Changes.Length == 0 && twoValueLevel.GeometryProblem == "level_geometry", "level with two values is reported without typed fields");

        var tinyDelta = 1 + 1e-9 - 1;
        suite.Equal("sub-millimetre nonzero delta keeps round-trip precision", "+" + tinyDelta.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " m", new GeometryChange("x", Length.FromMetres(1), Length.FromMetres(1 + 1e-9)).Describe());
        suite.Equal("negative sub-millimetre delta keeps sign and precision", "-" + tinyDelta.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " m", new GeometryChange("x", Length.FromMetres(1 + 1e-9), Length.FromMetres(1)).Describe());
        suite.Equal("zero delta still formats as +0.000 m", "+0.000 m", new GeometryChange("x", Length.FromMetres(1), Length.FromMetres(1)).Describe());
    }

    static void IfcRectangle(Suite suite)
    {
        var request = IfcCompiler.EvaluationRequest();
        var before = JsonSerializer.Deserialize<Project>(request.BeforeJson) ?? throw new InvalidOperationException("missing_before");
        var wide = before with { Elements = before.Elements.Select(element => element.Kind == "space" ? element with { GeometryMetres = [.. element.GeometryMetres, 1] } : element).ToArray() };
        var beamed = before with { Elements = before.Elements.Select(element => element.Id == RoomCompiler.Id(request.RoomId, "west") ? element with { Kind = "beam" } : element).ToArray() };
        suite.Throws<ArgumentException>("IFC rectangle requires the west element to be a wall", () => IfcCompiler.Export(before, beamed, Compiler.Diff(before, beamed), request.RoomId, "beam"), "unsupported_ifc_rectangle");
        suite.Throws<ArgumentException>("IFC rectangle decodes space through the codec", () => IfcCompiler.Export(before, wide, Compiler.Diff(before, wide), request.RoomId, "wide"), "unsupported_ifc_rectangle");
    }
}
