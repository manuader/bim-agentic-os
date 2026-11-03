using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// Family commands contract oracle: family.load (a .rfa from the repository's .bim/families or the Autodesk content
// library, links refused, size capped, existing families untouched unless overwrite), family_instance.place (a
// non-hosted level-based family instance at a plan point with an elevation offset and a rotation), the generalised
// query.family_symbols categories, and wood (any loaded family) columns and beams. Covers the schemas, the typed
// validators, the guarded client path (fake transport) and the Autodesk-free executor rules and readback checks in
// runtimes/revit-native/CommandsFamilies.cs. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorFamilies.cs is compiled, never run.
static class CatalogueFamiliesChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string SymbolUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c01";
    const string OtherSymbolUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000c02";
    const string FamilyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000f01";
    const string InstanceUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000d01";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string Library = @"C:\ProgramData\Autodesk\RVT 2025\Libraries";
    const string TimberColumn = Library + @"\English\Structural Columns\Wood\M_Timber-Column.rfa";
    const string RepoStove = @"C:\Repo\.bim\families\Focus Gyrofocus.rfa";
    static readonly string[] FamilyCommands = { "family.load", "family_instance.place" };
    static readonly string[] SymbolCategories = { "door", "window", "structural_column", "structural_framing", "generic_model", "specialty_equipment", "furniture", "lighting_fixture", "plumbing_fixture", "mechanical_equipment", "casework", "planting", "site", "entourage" };
    static readonly string[] Placeable = { "generic_model", "specialty_equipment", "furniture", "lighting_fixture", "plumbing_fixture", "mechanical_equipment", "casework", "planting", "site", "entourage" };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-families",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Pair(double x, double y) => new(x, y);
    static JsonObject Load(string path = TimberColumn) => new() { ["path"] = path };
    static JsonObject Place() => new() { ["category"] = "generic_model", ["symbol"] = "Focus Gyrofocus: Suspended", ["level"] = "Level 1", ["point"] = Pair(4000, 15000), ["offset_mm"] = 1200 };
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
    static PlanPoint P(double x, double y) => new(x, y);

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static void RejectsText(string name, string json, string code, string? field = null)
    {
        var error = Refusal(json);
        Check(error is not null && error.Code == code && (field is null || error.Field == field), name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
    static void Rejects(string name, JsonNode envelope, string code, string? field = null) => RejectsText(name, envelope.ToJsonString(), code, field);
    static RevitCommandEnvelope? Accepts(string name, JsonNode envelope)
    {
        try { var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString()); Check(true, name); return parsed; }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        FamilySymbolCategories();
        LoadBinding();
        PlaceBinding();
        WoodStructure();
        SourceGuard();
        SourceGuardOnDisk();
        ClientPath();
        ExecutorRules();
        Readbacks();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.Families.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(FamilyCommands), "family catalogue lists family.load then family_instance.place", string.Join(",", names));
        Check(RevitCommandCatalogue.Families.All(spec => spec.Writes && RevitCommandCatalogue.All.Contains(spec)), "both family commands are writes in the catalogue enumeration");
        Check(RevitCommandCatalogue.Families.Select(spec => spec.ToolName).SequenceEqual(new[] { "revit_family_load", "revit_family_instance_place" }), "family tool names are revit_family_load and revit_family_instance_place");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the family commands");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.Families.Single(spec => spec.Name == name)), "Find resolves family command " + name);
        Check(RevitCommandCatalogue.Families.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal)), "family commands have plain-English descriptions stating the copies-only rule");
        Check(RevitCommandCatalogue.Find("family.load")!.Description.Contains(".bim/families", StringComparison.Ordinal) && RevitCommandCatalogue.Find("family.load")!.Description.Contains("config/revit-content-libraries.txt", StringComparison.Ordinal) && RevitCommandCatalogue.Find("family.load")!.Description.Contains("overwrite", StringComparison.Ordinal),
            "family.load description names both allowed sources and the overwrite flag");
        Check(RevitCommandCatalogue.Find("family_instance.place")!.Description.Contains("offset_mm", StringComparison.Ordinal) && RevitCommandCatalogue.Find("family_instance.place")!.Description.Contains("host", StringComparison.Ordinal), "family_instance.place description names the offset and the hosted-family refusal");
        Check(RevitCommandCatalogue.Families.All(spec => !RevitCommandCatalogue.IsDestructive(spec.Name)), "family commands are additive, not destructive");
        foreach (var spec in RevitCommandCatalogue.Families)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.Families)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == PermissionLevel.WriteRealProject,
                "permission level of " + spec.Name + " is write-copies on a copy and write-real-project elsewhere");
        Check(RevitCommandCodes.ClientSide.Contains(RevitCommandCodes.FamilySourceRefused) && RevitCommandCodes.FamilySourceRefused == "family_source_refused", "family_source_refused is a client-side code");
    }

    static void FamilySymbolCategories()
    {
        Check(RevitFamilyRules.SymbolCategories.SequenceEqual(SymbolCategories), "query.family_symbols categories: door, window, structural column and framing, generic model, specialty equipment, furniture, lighting, plumbing, mechanical equipment, casework", string.Join(",", RevitFamilyRules.SymbolCategories));
        Check(RevitFamilyRules.PlaceableCategories.SequenceEqual(Placeable), "family_instance.place categories are the non-hosted model categories", string.Join(",", RevitFamilyRules.PlaceableCategories));
        foreach (var category in SymbolCategories)
        {
            Check(Accepts("query.family_symbols " + category + " accepted", Envelope("query.family_symbols", new JsonObject { ["category"] = category }))?.Args is FamilySymbolQueryArgs { Category: { } bound } && bound == category, "query.family_symbols binds " + category);
            var builtIn = RevitFamilyRules.BuiltInCategory(category);
            Check(RevitElementQueryRules.Categories.Contains((category, builtIn)), category + " uses the same BuiltInCategory as the element query vocabulary (" + builtIn + ")");
        }
        Check(RevitFamilyRules.BuiltInCategory("door") == "OST_Doors" && RevitFamilyRules.BuiltInCategory("window") == "OST_Windows" && RevitFamilyRules.BuiltInCategory("specialty_equipment") == "OST_SpecialityEquipment"
            && RevitFamilyRules.BuiltInCategory("mechanical_equipment") == "OST_MechanicalEquipment" && RevitFamilyRules.BuiltInCategory("generic_model") == "OST_GenericModel" && RevitFamilyRules.BuiltInCategory("structural_column") == "OST_StructuralColumns",
            "door, window, specialty (Revit spells it Speciality), mechanical, generic model and structural column categories map to their BuiltInCategory");
        Check(RevitFamilyRules.BuiltInCategory("planting") == "OST_Planting" && RevitFamilyRules.BuiltInCategory("site") == "OST_Site" && RevitFamilyRules.BuiltInCategory("entourage") == "OST_Entourage",
            "site model categories: planting (RPC trees and shrubs), site and entourage map to OST_Planting, OST_Site and OST_Entourage");
        Check(Thrown(() => RevitFamilyRules.BuiltInCategory("wall"))?.Code == RevitCommandCodes.ArgsInvalid, "a category that is not a family category is args_invalid");
        Check(RevitFamilyRules.FriendlyCategory("OST_SpecialityEquipment") == "specialty_equipment" && RevitFamilyRules.FriendlyCategory("OST_Doors") == "door" && RevitFamilyRules.FriendlyCategory("OST_Walls") is null && RevitFamilyRules.FriendlyCategory(null) is null,
            "FriendlyCategory names a family category from its BuiltInCategory, null otherwise");
        Rejects("query.family_symbols unknown category", Envelope("query.family_symbols", new JsonObject { ["category"] = "wall" }), RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.family_symbols OST_ name is not offered", Envelope("query.family_symbols", new JsonObject { ["category"] = "OST_GenericModel" }), RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("opening.place still takes only door or window", Envelope("opening.place", new JsonObject { ["category"] = "generic_model", ["symbol"] = "A: B", ["host_wall_uid"] = InstanceUid, ["position_along_mm"] = 100 }), RevitCommandCodes.ArgsInvalid, "args.category");
    }

    static void LoadBinding()
    {
        var load = Accepts("family.load from the Autodesk library accepted", Envelope("family.load", Load()));
        Check(load?.Args is FamilyLoadArgs { Path: TimberColumn, FamilyName: "M_Timber-Column", Overwrite: false }, "family.load binds the path, the family name from the file name and overwrite false by default");
        Check(Accepts("family.load from the repository accepted", Envelope("family.load", With(Load(RepoStove), "overwrite", true)))?.Args is FamilyLoadArgs { FamilyName: "Focus Gyrofocus", Overwrite: true }, "family.load binds overwrite true and a name with spaces");
        Check(Accepts("family.load with upper-case extension accepted", Envelope("family.load", Load(@"C:\Repo\.bim\families\Stove.RFA")))?.Args is FamilyLoadArgs { FamilyName: "Stove" }, "the .rfa extension is case-insensitive");
        Check(Accepts("family.load with forward slashes accepted", Envelope("family.load", Load("C:/Repo/.bim/families/sub/Stove.rfa")))?.Args is FamilyLoadArgs { FamilyName: "Stove" }, "a path with forward slashes names its last segment");
        Rejects("family.load without path", Envelope("family.load", new JsonObject()), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load relative path", Envelope("family.load", Load(@"families\Stove.rfa")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load path with ..", Envelope("family.load", Load(@"C:\Repo\.bim\families\..\..\Secret\Stove.rfa")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load of a project file", Envelope("family.load", Load(@"C:\Repo\.bim\families\Model.rvt")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load of a family template", Envelope("family.load", Load(@"C:\Repo\.bim\families\Generic Model.rft")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load of a bare extension", Envelope("family.load", Load(@"C:\Repo\.bim\families\.rfa")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load of an 8.3 short name", Envelope("family.load", Load(@"C:\PROGRA~3\Autodesk\RVT 2025\Libraries\Stove.rfa")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load alternate data stream", Envelope("family.load", Load(@"C:\Repo\.bim\families\Stove.rfa:hidden")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load device path", Envelope("family.load", Load(@"\\?\C:\Repo\.bim\families\Stove.rfa")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load path with a control character", Envelope("family.load", Load("C:\\Repo\\.bim\\families\\St\u0001ove.rfa")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load path over 1024 characters", Envelope("family.load", Load(@"C:\Repo\.bim\families\" + new string('a', 1010) + ".rfa")), RevitCommandCodes.ArgsInvalid, "args.path");
        Rejects("family.load overwrite as text", Envelope("family.load", With(Load(), "overwrite", "yes")), RevitCommandCodes.ArgsInvalid, "args.overwrite");
        Rejects("family.load with an unknown member", Envelope("family.load", With(Load(), "category", "door")), RevitCommandCodes.ArgsInvalid, "args.category");
        Check(RevitFamilySourceGuard.FamilyName(TimberColumn) == "M_Timber-Column" && RevitFamilySourceGuard.FamilyName("/srv/families/a.b.rfa") == "a.b", "FamilyName is the last path segment without .rfa");
    }

    static void PlaceBinding()
    {
        var place = Accepts("family_instance.place accepted", Envelope("family_instance.place", Place()));
        Check(place?.Args is FamilyInstancePlaceArgs { Category: "generic_model", Symbol: "Focus Gyrofocus: Suspended", Level: "Level 1" } p && Near(p.Point.X.Millimetres, 4000) && Near(p.Point.Y.Millimetres, 15000) && Near(p.Offset.Millimetres, 1200) && p.Rotation.Radians == 0,
            "family_instance.place binds category, symbol, level, the plan point and offset in millimetres, rotation 0 by default");
        var defaults = Accepts("family_instance.place without offset accepted", Envelope("family_instance.place", With(Place(), "offset_mm", null)));
        Check(defaults?.Args is FamilyInstancePlaceArgs d && d.Offset.Millimetres == 0, "offset_mm defaults to 0");
        var rotated = Accepts("family_instance.place with rotation accepted", Envelope("family_instance.place", With(With(Place(), "rotation_deg", -90), "offset_mm", -150)));
        Check(rotated?.Args is FamilyInstancePlaceArgs r && Near(r.Rotation.Degrees, -90) && Near(r.Offset.Millimetres, -150), "rotation in degrees and a negative offset bind");
        foreach (var category in Placeable)
            Check(Accepts("family_instance.place " + category + " accepted", Envelope("family_instance.place", With(Place(), "category", category))) is not null, "family_instance.place takes category " + category);
        foreach (var category in new[] { "door", "window", "structural_column", "structural_framing", "wall" })
            Rejects("family_instance.place category " + category + " is refused", Envelope("family_instance.place", With(Place(), "category", category)), RevitCommandCodes.ArgsInvalid, "args.category");
        Check(Accepts("family_instance.place symbol by UniqueId accepted", Envelope("family_instance.place", With(Place(), "symbol", SymbolUid)))?.Args is FamilyInstancePlaceArgs { Symbol: SymbolUid }, "symbol may be a UniqueId");
        Rejects("family_instance.place offset beyond the Revit limit", Envelope("family_instance.place", With(Place(), "offset_mm", 9_144_001)), RevitCommandCodes.LengthInvalid, "args.offset_mm");
        RejectsText("family_instance.place offset overflowing to infinity", Envelope("family_instance.place", Place()).ToJsonString().Replace("\"offset_mm\":1200", "\"offset_mm\":1e400"), RevitCommandCodes.LengthInvalid, "args.offset_mm");
        Rejects("family_instance.place rotation beyond one turn", Envelope("family_instance.place", With(Place(), "rotation_deg", 361)), RevitCommandCodes.AngleInvalid, "args.rotation_deg");
        Rejects("family_instance.place rotation beyond one turn negative", Envelope("family_instance.place", With(Place(), "rotation_deg", -720)), RevitCommandCodes.AngleInvalid, "args.rotation_deg");
        Rejects("family_instance.place point beyond 1e8 mm", Envelope("family_instance.place", With(Place(), "point", Pair(2e8, 0))), RevitCommandCodes.LengthInvalid, "args.point[0]");
        Rejects("family_instance.place point with three coordinates", Envelope("family_instance.place", With(Place(), "point", new JsonArray(0, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.point");
        foreach (var required in new[] { "category", "symbol", "level", "point" })
            Rejects("family_instance.place without " + required, Envelope("family_instance.place", With(Place(), required, null)), RevitCommandCodes.ArgsInvalid, "args." + required);
        Rejects("family_instance.place with a host", Envelope("family_instance.place", With(Place(), "host_uid", InstanceUid)), RevitCommandCodes.ArgsInvalid, "args.host_uid");
        // A tree on sloping terrain: the caller gives the terrain height above the level as offset_mm.
        Check(Accepts("planting on terrain accepted", Envelope("family_instance.place", new JsonObject { ["category"] = "planting", ["symbol"] = "RPC Tree - Deciduous: Red Maple - 9 Meters", ["level"] = "Level 1", ["point"] = Pair(-6000, 22000), ["offset_mm"] = 850, ["rotation_deg"] = 30 }))?.Args is FamilyInstancePlaceArgs { Category: "planting" } tree && Near(tree.Offset.Millimetres, 850),
            "a planting instance takes the terrain height above the level as offset_mm");
        Rejects("family_instance.place has no toposolid host member", Envelope("family_instance.place", With(Place(), "host_toposolid_uid", InstanceUid)), RevitCommandCodes.ArgsInvalid, "args.host_toposolid_uid");
        Rejects("family_instance.place empty symbol", Envelope("family_instance.place", With(Place(), "symbol", "")), RevitCommandCodes.ArgsInvalid, "args.symbol");
    }

    // Item 4: columns and beams take any loaded family of their category (wood families from the Revit library once
    // loaded): the binder keeps the type text as given and the executor resolves it among every symbol of the category.
    static void WoodStructure()
    {
        var column = Accepts("timber column accepted", Envelope("structural_column.create", new JsonObject { ["type"] = "M_Timber-Column: 140x140mm", ["point"] = Pair(0, 0), ["base_level"] = "Level 1", ["top_level"] = "Level 2" }));
        Check(column?.Args is StructuralColumnCreateArgs { Type: "M_Timber-Column: 140x140mm" }, "structural_column.create keeps a wood family type reference as given");
        var beam = Accepts("timber beam accepted", Envelope("structural_beam.create", new JsonObject { ["level"] = "Level 2", ["type"] = "M_Timber: 80x200mm", ["line"] = new JsonObject { ["start"] = Pair(0, 0), ["end"] = Pair(4000, 0) } }));
        Check(beam?.Args is StructuralBeamCreateArgs { Type: "M_Timber: 80x200mm" }, "structural_beam.create keeps a wood family type reference as given");
        var columns = new[]
        {
            new CommandCandidate(OtherSymbolUid, "W10x49", CommandReferences.Qualified("W-Wide Flange-Column", "W10x49")),
            new CommandCandidate(SymbolUid, "140x140mm", CommandReferences.Qualified("M_Timber-Column", "140x140mm")),
        };
        Check(CommandReferences.Resolve("M_Timber-Column: 140x140mm", columns, "args.type", "structural column type") == 1 && CommandReferences.Resolve("140x140mm", columns, "args.type", "structural column type") == 1 && CommandReferences.Resolve(SymbolUid, columns, "args.type", "structural column type") == 1,
            "a timber column type resolves by Family: Type, type name or UniqueId among steel and wood column families");
        Check(CommandRulesStructure.BuiltInCategory("column") == RevitFamilyRules.BuiltInCategory("structural_column") && CommandRulesStructure.BuiltInCategory("framing") == RevitFamilyRules.BuiltInCategory("structural_framing"),
            "structural column and framing types are listed by the same category whichever query is used");
    }

    static void SourceGuard()
    {
        var repo = RevitFamilySourceGuard.Roots(@"C:\Repo", new[] { Library });
        Check(repo.SequenceEqual(new[] { Path.Combine(@"C:\Repo", ".bim", "families"), Library }), "with a repository the sources are <repo>/.bim/families and the listed content libraries", string.Join(";", repo));
        Check(RevitFamilySourceGuard.Roots(null, new[] { Library }).SequenceEqual(new[] { Library }) && RevitFamilySourceGuard.Roots(null, Array.Empty<string>()).Count == 0, "without a repository only the listed content libraries are sources");
        Check(RevitFamilySourceGuard.ContentLibrariesFile == "config/revit-content-libraries.txt" && RevitFamilySourceGuard.RepositoryFamiliesDirectory == ".bim/families" && RevitFamilySourceGuard.MaxFamilyBytes == 100L * 1024 * 1024, "source constants: .bim/families, config/revit-content-libraries.txt and a 100 MiB cap");
        Check(RevitCommandPolicy.FromAllowlist(null, @"C:\Repo\.bim\native-runs").FamilySourceRoots.Count == 0 && (new RevitCommandPolicy(Array.Empty<string>()) with { RepositoryDirectory = @"C:\Repo", ContentLibraryRoots = new[] { Library } }).FamilySourceRoots.SequenceEqual(repo),
            "the policy's family sources follow its repository and content libraries; a bare policy has none");
        var repository = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory)!;
        var repositoryPolicy = RevitCommandPolicy.ForRepository(AppContext.BaseDirectory);
        Check(repositoryPolicy.RepositoryDirectory == repository && repositoryPolicy.ContentLibraryRoots.SequenceEqual(new[] { Library }), "ForRepository records the repository and the shipped config/revit-content-libraries.txt lists the Revit 2025 library", string.Join(";", repositoryPolicy.ContentLibraryRoots));
        var roots = repo;
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        Check(RevitFamilySourceGuard.Check(TimberColumn, roots, noLinks) is null, "a family in a listed content library is an allowed source");
        Check(RevitFamilySourceGuard.Check(RepoStove, roots, noLinks) is null && RevitFamilySourceGuard.Check(@"c:\repo\.BIM\Families\sub\Stove.rfa", roots, noLinks) is null, "a family under .bim/families (any depth, Windows case rules) is an allowed source");
        Check(RevitFamilySourceGuard.Check(@"C:\Users\owner\Downloads\Stove.rfa", roots, noLinks) is { } outside && outside.Contains(".bim/families", StringComparison.Ordinal), "a family outside both sources is refused, naming where to put it");
        Check(RevitFamilySourceGuard.Check(@"C:\Repo\.bim\families-old\Stove.rfa", roots, noLinks) is not null, "a sibling directory with the same prefix is not a source (whole segments)");
        Check(RevitFamilySourceGuard.Check(@"C:\ProgramData\Autodesk\RVT 2024\Libraries\Stove.rfa", roots, noLinks) is not null, "another Revit version's library is not a source");
        Check(RevitFamilySourceGuard.Check(@"C:\Repo\.bim\families", roots, noLinks) is not null, "the source directory itself is not a family");
        Check(RevitFamilySourceGuard.Check(@"C:\Repo\.bim\families\Stove.rvt", roots, noLinks) is not null, "a non-.rfa file under a source is refused");
        Check(RevitFamilySourceGuard.Check(RepoStove, roots, new RevitFileSystemProbe(path => path.EndsWith("families", StringComparison.OrdinalIgnoreCase), _ => null)) is { } linkedRoot && linkedRoot.Contains("link", StringComparison.Ordinal), "a junction or symlink at the source root is refused");
        Check(RevitFamilySourceGuard.Check(@"C:\Repo\.bim\families\sub\Stove.rfa", roots, new RevitFileSystemProbe(path => path.EndsWith("sub", StringComparison.Ordinal), _ => null)) is not null, "a junction or symlink below the source root is refused");
        Check(RevitFamilySourceGuard.Check(RepoStove, roots, new RevitFileSystemProbe(path => path.EndsWith(".rfa", StringComparison.Ordinal), _ => null)) is not null, "a symlinked family file is refused");
        Check(RevitFamilySourceGuard.Check(RepoStove, roots, new RevitFileSystemProbe(_ => false, _ => 2)) is not null && RevitFamilySourceGuard.Check(RepoStove, roots, new RevitFileSystemProbe(_ => false, _ => -1)) is not null, "a hard-linked or unreadable family file is refused");
        Check(RevitFamilySourceGuard.Check(RepoStove, roots, new RevitFileSystemProbe(_ => false, _ => 1)) is null, "a family file with one link is allowed");
        Check(RevitFamilySourceGuard.SizeProblem(1) is null && RevitFamilySourceGuard.SizeProblem(RevitFamilySourceGuard.MaxFamilyBytes) is null, "a family up to 100 MiB is within the cap");
        Check(RevitFamilySourceGuard.SizeProblem(RevitFamilySourceGuard.MaxFamilyBytes + 1) is { } big && big.Contains("100 MiB", StringComparison.Ordinal), "a family over 100 MiB is refused");
        Check(RevitFamilySourceGuard.SizeProblem(null) is { } missing && missing.Contains("not found", StringComparison.Ordinal) && RevitFamilySourceGuard.SizeProblem(0) is not null, "a missing or empty family file is refused");
    }

    // The real file-system probe on this machine: a real file passes; a symlinked source directory or file is refused.
    static void SourceGuardOnDisk()
    {
        var work = Path.Combine(Path.GetTempPath(), "family-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = Path.Combine(work, "repo");
            var families = Path.Combine(repo, ".bim", "families");
            Directory.CreateDirectory(families);
            var stove = Path.Combine(families, "Stove.rfa");
            File.WriteAllBytes(stove, new byte[] { 1, 2, 3 });
            var roots = RevitFamilySourceGuard.Roots(repo, Array.Empty<string>());
            Check(RevitFamilySourceGuard.Check(stove, roots, RevitFileSystemProbe.Real) is null && RevitFamilySourceGuard.SizeProblem(new FileInfo(stove).Length) is null, "a real .rfa under .bim/families passes the real probe");
            var outside = Path.Combine(work, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "Secret.rfa"), new byte[] { 1 });
            var linkedDirectory = Path.Combine(families, "linked");
            var linkedFile = Path.Combine(families, "Linked.rfa");
            var canLink = true;
            try { Directory.CreateSymbolicLink(linkedDirectory, outside); File.CreateSymbolicLink(linkedFile, Path.Combine(outside, "Secret.rfa")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { canLink = false; }
            if (canLink)
            {
                Check(RevitFamilySourceGuard.Check(Path.Combine(linkedDirectory, "Secret.rfa"), roots, RevitFileSystemProbe.Real) is not null, "a family reached through a symlinked directory is refused by the real probe");
                Check(RevitFamilySourceGuard.Check(linkedFile, roots, RevitFileSystemProbe.Real) is not null, "a symlinked .rfa is refused by the real probe");
            }
            else Console.WriteLine("SKIP symlink creation not permitted here; the injected probe cases cover links");
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        // The content libraries file of a repository: entries follow the allowlist rules; a malformed or linked file fails closed.
        var repos = Path.Combine(Path.GetTempPath(), "family-libraries-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Repo(string name, string? libraries)
            {
                var root = Path.Combine(repos, name);
                Directory.CreateDirectory(Path.Combine(root, "config"));
                File.WriteAllText(Path.Combine(root, "global.json"), "{}");
                if (libraries is not null) File.WriteAllText(Path.Combine(root, "config", "revit-content-libraries.txt"), libraries);
                return root;
            }
            var listed = RevitCommandPolicy.ForRepository(Repo("listed", "# libraries\n/srv/revit/Libraries\n\nD:\\Content\\Revit\n"));
            Check(listed.ContentLibraryRoots.SequenceEqual(new[] { "/srv/revit/Libraries", @"D:\Content\Revit" }) && listed.FamilySourceRoots.Count == 3, "content library entries are read in order, comments and blank lines ignored");
            Check(RevitCommandPolicy.ForRepository(Repo("none", null)).ContentLibraryRoots.Count == 0, "without the file no content library is a source");
            string? Code(Action action) { try { action(); return null; } catch (RevitCommandException error) { return error.Code; } }
            Check(Code(() => RevitCommandPolicy.ForRepository(Repo("relative", "Libraries\n"))) == RevitCommandCodes.AllowlistInvalid && Code(() => RevitCommandPolicy.ForRepository(Repo("broad", "C:\\\n"))) == RevitCommandCodes.AllowlistInvalid,
                "a relative or too-broad content library entry is allowlist_invalid");
            Check(Code(() => RevitCommandPolicy.ForRepository(Repo("huge", new string('#', 70000)))) == RevitCommandCodes.AllowlistInvalid, "an oversized content libraries file is allowlist_invalid");
        }
        finally { try { Directory.Delete(repos, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" }) with { RepositoryDirectory = @"C:\Repo", ContentLibraryRoots = new[] { Library } };
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var (command, args) in new (string, JsonObject)[] { ("family.load", Load()), ("family.load", Load(RepoStove)), ("family_instance.place", Place()) })
        {
            var before = forwarded.Count;
            var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
            Check(!ok.IsError && forwarded.Count == before + 1, command + " on an enabled copy is forwarded", ok.Line);
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
        }
        var before2 = forwarded.Count;
        var outside = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("family.load", Load(@"C:\Users\owner\Downloads\Stove.rfa")).ToJsonString(), policy, Transport, noLinks);
        Check(outside.IsError && !outside.Forwarded && JsonNode.Parse(outside.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.FamilySourceRefused && JsonNode.Parse(outside.Line)!["field"]!.GetValue<string>() == "args.path" && forwarded.Count == before2,
            "family.load from outside the sources is family_source_refused at args.path before the pipe", outside.Line);
        var linked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("family.load", Load(RepoStove)).ToJsonString(), policy, Transport, new RevitFileSystemProbe(path => path.EndsWith("families", StringComparison.Ordinal), _ => null));
        Check(linked.IsError && !linked.Forwarded && JsonNode.Parse(linked.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.FamilySourceRefused && forwarded.Count == before2, "family.load through a linked source directory is family_source_refused before the pipe", linked.Line);
        var noRepo = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("family.load", Load(RepoStove)).ToJsonString(), new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" }) with { ContentLibraryRoots = new[] { Library } }, Transport, noLinks);
        Check(noRepo.IsError && JsonNode.Parse(noRepo.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.FamilySourceRefused, "without a known repository .bim/families is not a source", noRepo.Line);
    }

    static string Answer(string wireLine)
    {
        using var wire = JsonDocument.Parse(wireLine);
        var commandText = wire.RootElement.GetProperty("Command").GetRawText();
        using var command = JsonDocument.Parse(commandText);
        var receipt = new RevitCommandReceipt(command.RootElement.GetProperty("operation_id").GetString()!, command.RootElement.GetProperty("command").GetString()!,
            command.RootElement.GetProperty("document").GetProperty("unique_id").GetString()!, CommandWire.Sha256(commandText), false);
        return RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>()).ToJson();
    }

    static void ExecutorRules()
    {
        Check(CommandRulesFamilies.CallsLoad(existed: false, overwrite: false) && CommandRulesFamilies.CallsLoad(existed: true, overwrite: true) && !CommandRulesFamilies.CallsLoad(existed: true, overwrite: false),
            "LoadFamily is called for a new family or an explicit overwrite, never to touch an existing family otherwise");
        Check(CommandRulesFamilies.LoadOutcome(false, false, true, true) == CommandRulesFamilies.Loaded && CommandRulesFamilies.LoadOutcome(true, false, false, true) == CommandRulesFamilies.AlreadyLoaded
            && CommandRulesFamilies.LoadOutcome(true, true, true, true) == CommandRulesFamilies.Reloaded && CommandRulesFamilies.LoadOutcome(true, true, false, true) == CommandRulesFamilies.Unchanged,
            "load outcomes: loaded, already_loaded, reloaded, unchanged");
        Check(Thrown(() => CommandRulesFamilies.LoadOutcome(false, false, false, false)) is { Code: RevitCommandCodes.TransactionFailed } && Thrown(() => CommandRulesFamilies.LoadOutcome(false, true, true, false)) is { Code: RevitCommandCodes.TransactionFailed }
            && Thrown(() => CommandRulesFamilies.LoadOutcome(true, true, true, false)) is { Code: RevitCommandCodes.TransactionFailed }, "a load that leaves no family is transaction_failed");
        Check(CommandRulesFamilies.OnFamilyFound(false) == (false, false) && CommandRulesFamilies.OnFamilyFound(true) == (true, true), "an existing family is reloaded, with its parameter values overwritten, only when overwrite is set");
        Check(CommandRulesFamilies.OnSharedFamilyFound(false) == (true, false, false) && CommandRulesFamilies.OnSharedFamilyFound(true) == (true, true, true), "a shared nested family keeps the project version and values unless overwrite is set");
        var roots = RevitFamilySourceGuard.Roots(@"C:\Repo", new[] { Library });
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        Check(Thrown(() => CommandRulesFamilies.CheckSource(RepoStove, roots, noLinks, 4096, RepoStove)) is null, "the executor admits a family under a source whose full path resolves to itself");
        Check(Thrown(() => CommandRulesFamilies.CheckSource(@"C:\Elsewhere\Stove.rfa", roots, noLinks, 4096, @"C:\Elsewhere\Stove.rfa")) is { Code: RevitCommandCodes.FamilySourceRefused, Field: "args.path" }, "the executor repeats the source check");
        Check(Thrown(() => CommandRulesFamilies.CheckSource(RepoStove, roots, new RevitFileSystemProbe(_ => true, _ => null), 4096, RepoStove)) is { Code: RevitCommandCodes.FamilySourceRefused }, "the executor repeats the link check");
        Check(Thrown(() => CommandRulesFamilies.CheckSource(RepoStove, roots, noLinks, null, RepoStove)) is { Code: RevitCommandCodes.FamilySourceRefused } && Thrown(() => CommandRulesFamilies.CheckSource(RepoStove, roots, noLinks, RevitFamilySourceGuard.MaxFamilyBytes + 1, RepoStove)) is { Code: RevitCommandCodes.FamilySourceRefused },
            "the executor refuses a missing or oversized family file");
        Check(Thrown(() => CommandRulesFamilies.CheckSource(RepoStove, roots, noLinks, 4096, @"C:\Elsewhere\Stove.rfa")) is { Code: RevitCommandCodes.FamilySourceRefused } && Thrown(() => CommandRulesFamilies.CheckSource(RepoStove, roots, noLinks, 4096, null)) is { Code: RevitCommandCodes.FamilySourceRefused },
            "a path whose full resolution differs, or cannot be resolved, is refused");
        Check(Thrown(() => CommandRulesFamilies.CheckPlacement("OneLevelBased")) is null, "a one-level-based family can be placed");
        foreach (var (placement, word) in new[] { ("OneLevelBasedHosted", "host"), ("WorkPlaneBased", "face"), ("TwoLevelsBased", "TwoLevelsBased"), ("CurveBased", "CurveBased"), ("ViewBased", "ViewBased"), ("Adaptive", "Adaptive") })
            Check(Thrown(() => CommandRulesFamilies.CheckPlacement(placement)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.symbol" } refused && refused.Detail!.Contains(word, StringComparison.Ordinal), "a " + placement + " family is refused args_invalid naming why (" + word + ")");
        Check(Near(CommandRulesFamilies.NormalizeDegrees(-90), 270) && Near(CommandRulesFamilies.NormalizeDegrees(360), 0) && Near(CommandRulesFamilies.NormalizeDegrees(450), 90) && Near(CommandRulesFamilies.NormalizeDegrees(0), 0), "rotations normalise to [0, 360)");
        Check(CommandRulesFamilies.SameRotation(-90, 270) && CommandRulesFamilies.SameRotation(0, 359.9999999) && CommandRulesFamilies.SameRotation(360, 0) && !CommandRulesFamilies.SameRotation(90, 91) && !CommandRulesFamilies.SameRotation(0, 180), "rotations compare modulo one turn within tolerance");
    }

    static ObservedFamily Family(params FamilySymbolRow[] symbols) => new(FamilyUid, "M_Timber-Column", "Structural Columns", "OST_StructuralColumns", "TwoLevelsBased", symbols);
    static ObservedFamilyInstance Instance() => new(InstanceUid, SymbolUid, "Focus Gyrofocus", "Suspended", "OST_GenericModel", LevelUid, null, P(4000, 15000), 1200, 1200, 270);

    static void Readbacks()
    {
        var family = Family(new FamilySymbolRow("140x140mm", SymbolUid, false), new FamilySymbolRow("100x100mm", OtherSymbolUid, true));
        Check(CommandReadbackFamilies.Family("M_Timber-Column", family) is null && CommandReadbackFamilies.Family("m_timber-column", family) is null, "a family named as expected (ignoring case) with types matches");
        Check(CommandReadbackFamilies.Family("M_Timber", family) is { } renamed && renamed.Contains("M_Timber-Column", StringComparison.Ordinal), "a family with another name is a mismatch");
        Check(CommandReadbackFamilies.Family("M_Timber-Column", Family()) is { } empty && empty.Contains("no types", StringComparison.Ordinal), "a family without types is a mismatch");
        var json = JsonNode.Parse(CommandReadbackFamilies.FamilyJson(CommandRulesFamilies.Loaded, TimberColumn, family))!["family"]!;
        Check((string?)json["name"] == "M_Timber-Column" && (string?)json["unique_id"] == FamilyUid && (string?)json["outcome"] == "loaded" && (string?)json["source_path"] == TimberColumn
            && (string?)json["category"] == "structural_column" && (string?)json["category_name"] == "Structural Columns" && (string?)json["built_in_category"] == "OST_StructuralColumns" && (string?)json["placement_type"] == "TwoLevelsBased",
            "family readback JSON: name, unique_id, outcome, source, friendly and Revit category, placement type", json.ToJsonString());
        var symbols = json["symbols"]!.AsArray();
        Check(symbols.Count == 2 && (string?)symbols[0]!["type"] == "100x100mm" && (string?)symbols[0]!["reference"] == "M_Timber-Column: 100x100mm" && (string?)symbols[0]!["unique_id"] == OtherSymbolUid && (bool?)symbols[0]!["active"] == true && (string?)symbols[1]!["type"] == "140x140mm",
            "family readback lists its types ordered by name with the Family: Type reference, UniqueId and active flag", symbols.ToJsonString());
        var unmapped = JsonNode.Parse(CommandReadbackFamilies.FamilyJson(CommandRulesFamilies.AlreadyLoaded, RepoStove, family with { BuiltInCategory = "OST_Columns", CategoryName = null }))!["family"]!;
        Check(unmapped["category"] is null && unmapped["category_name"] is null && (string?)unmapped["outcome"] == "already_loaded", "a category outside the family vocabulary reads back as null with its BuiltInCategory");

        var instance = Instance();
        Check(CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1200, 1200, -90, instance) is null, "family instance readback matches (rotation -90 reads as 270)");
        Check(CommandReadbackFamilies.Instance(OtherSymbolUid, LevelUid, P(4000, 15000), 1200, 1200, -90, instance) is not null, "family instance type mismatch");
        Check(CommandReadbackFamilies.Instance(SymbolUid, OtherSymbolUid, P(4000, 15000), 1200, 1200, -90, instance) is not null, "family instance level mismatch");
        Check(CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000.01), 1200, 1200, -90, instance) is not null && CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1200, 1200, -90, instance with { Point = null }) is not null, "family instance point mismatch or unreadable");
        Check(CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1000, 1000, -90, instance) is { } offset && offset.Contains("offset", StringComparison.Ordinal), "family instance offset mismatch");
        Check(CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1200, 1200, 0, instance) is { } rotation && rotation.Contains("rotation", StringComparison.Ordinal) && CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1200, 1200, -90, instance with { RotationDeg = null }) is not null, "family instance rotation mismatch or unreadable");
        Check(CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1200, 1200, -90, instance with { OffsetMm = null }) is null && CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1200, 1200, -90, instance with { OffsetMm = null, ZMm = 1100 }) is not null
            && CommandReadbackFamilies.Instance(SymbolUid, LevelUid, P(4000, 15000), 1200, 1200, -90, instance with { OffsetMm = null, ZMm = null }) is not null,
            "without an elevation parameter the offset is read from the location elevation above the level");
        var instanceJson = JsonNode.Parse(CommandReadbackFamilies.InstanceJson(instance))!["family_instance"]!;
        Check((string?)instanceJson["unique_id"] == InstanceUid && (string?)instanceJson["symbol_unique_id"] == SymbolUid && (string?)instanceJson["reference"] == "Focus Gyrofocus: Suspended" && (string?)instanceJson["category"] == "generic_model"
            && (string?)instanceJson["level_unique_id"] == LevelUid && (double?)instanceJson["point"]![0] == 4000 && (double?)instanceJson["point"]![1] == 15000 && (double?)instanceJson["offset_mm"] == 1200 && (double?)instanceJson["z_mm"] == 1200
            && (double?)instanceJson["rotation_deg"] == 270 && instanceJson["host_unique_id"] is null && (string?)instanceJson["built_in_category"] == "OST_GenericModel",
            "family instance readback JSON: ids, Family: Type, category, level, point, offset, elevation, rotation, host", instanceJson.ToJsonString());
        var symbolsJson = JsonNode.Parse(CommandReadback.SymbolsJson("generic_model", new[] { new SymbolRow("Focus Gyrofocus", "Suspended", SymbolUid, false, false, "OneLevelBased") }))!;
        Check((string?)symbolsJson["category"] == "generic_model" && (string?)symbolsJson["family_symbols"]![0]!["placement_type"] == "OneLevelBased" && (bool?)symbolsJson["family_symbols"]![0]!["wall_hosted"] == false && (string?)symbolsJson["family_symbols"]![0]!["reference"] == "Focus Gyrofocus: Suspended",
            "query.family_symbols readback carries the placement type for any category");
    }
}
