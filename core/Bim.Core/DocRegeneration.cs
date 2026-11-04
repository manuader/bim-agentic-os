using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Bim.Core;

// P4-03 affected-only documentation regeneration. A documentation set names views, tags, dimensions, schedules,
// sheets and sheet placements by stable keys and binds them to BIM-IR element ids. Full regeneration turns every item
// into one catalogue documentation command (P4-01/P4-02) computed from the project; affected-only regeneration, given
// a Compiler.Diff, emits only the items whose referenced elements are in plan.DirtyIds (created, updated or deleted,
// or transitively depending on one that is) or whose view, schedule or sheet is regenerated with them, each step
// byte-identical to the same item's step in full regeneration of after. Vendor-free and BCL only; no BIM-IR or
// persisted format change. Contract: docs/contracts/module-contracts.md, "Affected-only documentation regeneration".

// Kind: floor_plan or ceiling_plan (Elements: exactly the BIM-IR level), section (Elements: exactly the BIM-IR wall
// it cuts along; DepthMetres: far clip depth) or three_d (Elements: walls framed by its section box; none for an
// unbounded view). Name and Scale pass through to the view command.
public sealed record DocView(string Key, string Kind, IReadOnlyList<Guid> Elements, string? Name = null, int? Scale = null, double DepthMetres = 0);
// A tag of one BIM-IR element in a plan view of the set; its head is the element's plan centre plus the offset.
public sealed record DocTag(string Key, string View, Guid Element, double OffsetXMetres = 0, double OffsetYMetres = 0, bool Leader = false, string Orientation = "horizontal");
// A linear dimension in a plan view of the set between two parallel BIM-IR walls; the line starts at the first wall's
// axis midpoint moved OffsetMetres along that axis and runs perpendicular onto the second wall's axis.
public sealed record DocDimension(string Key, string View, Guid First, Guid Second, double OffsetMetres = 0, string WallFace = "exterior");
// A schedule of every BIM-IR element of ElementKind, as the Revit category Category (a catalogue category name).
public sealed record DocSchedule(string Key, string ElementKind, string Category, IReadOnlyList<string> Fields, string? SortBy = null, string? Name = null);
public sealed record DocSheet(string Key, string Number, string Name, string? TitleBlock = null);
// Places the view or schedule with key View on the sheet with key Sheet, centred at sheet millimetres.
public sealed record DocPlacement(string Key, string Sheet, string View, double CenterXMm, double CenterYMm);
public sealed record DocumentationSet(IReadOnlyList<DocView> Views, IReadOnlyList<DocTag> Tags, IReadOnlyList<DocDimension> Dimensions,
    IReadOnlyList<DocSchedule> Schedules, IReadOnlyList<DocSheet> Sheets, IReadOnlyList<DocPlacement> Placements);

// Field ("args.<member>") takes the Revit UniqueId of Source: "doc:<key>" (the realized view, schedule or sheet of that
// item) or "element:<BIM-IR id D>" (the realized element). Filled by DocRegeneration.Envelope.
public sealed record DocStepInput(string Field, string Source);
// ArgsJson is canonical (RevitCommandProtocol.Canonical) with a placeholder UniqueId in every input field. Elements:
// the BIM-IR elements the command was computed from (for a schedule, the scheduled elements of after).
public sealed record DocCommandStep(string Key, string ItemKind, string OperationId, string Command, string ArgsJson, IReadOnlyList<DocStepInput> Inputs, IReadOnlyList<Guid> Elements);
public sealed record DocUnsupported(string Key, string ItemKind, string Code, string Detail);
// Regenerated: every item key this plan regenerates (sorted ordinally); each has exactly one step or one unsupported
// entry. The caller retires the prior realization of every regenerated key (re-tag, re-dimension, schedule refresh)
// and runs the steps in order. TotalItems counts the whole set.
public sealed record DocRegenerationPlan(int Version, Guid ProjectId, int Revision, string AfterHash, RevitDocumentRef Document,
    IReadOnlyList<string> Regenerated, IReadOnlyList<DocCommandStep> Steps, IReadOnlyList<DocUnsupported> Unsupported, int TotalItems)
{
    public int RegeneratedCount => Regenerated.Count;
}

public static class DocRegenerationCodes
{
    public const string ElementMissing = "element_missing";
    public const string KindMismatch = "kind_mismatch";
    public const string GeometryInvalid = "geometry_invalid";
    public const string LevelUnbound = "level_unbound";
    public const string ReferencesNotParallel = "references_not_parallel";
    public const string DependencyUnsupported = "dependency_unsupported";
    public const string CommandInvalid = "command_invalid";
}

// INVARIANT (soundness of affected-only regeneration): an item's result may read only its referenced elements (view
// Elements, tag Element, dimension First/Second, for a schedule every element of its kind), their prerequisites (a
// wall's level elevation), the bindings, and the status of its own view, schedule or sheet; never another element.
// A change to any of those is a plan operation the referenced element is or depends on, so it is in plan.DirtyIds;
// schedule membership changes only by a create, delete or kind change, which are operations too. Hence
// Merge(Full(before), Affected(before, after, plan)) has the same content as Full(after) (Agrees).
public static class DocRegeneration
{
    public const int Version = 1;
    public const string View = "view";
    public const string Tag = "tag";
    public const string Dimension = "dimension";
    public const string Schedule = "schedule";
    public const string Sheet = "sheet";
    public const string Placement = "placement";
    // Step order: what later steps reference comes first.
    static readonly string[] KindOrder = { View, Schedule, Sheet, Tag, Dimension, Placement };
    // Cross product of the two unit wall directions above which walls are not parallel.
    const double ParallelTolerance = 1e-9;
    const string PendingOperationId = "op.pending";

    public static string ElementSource(Guid id) => "element:" + id.ToString("D");
    public static string ItemSource(string key) => "doc:" + key;
    // Distinct per input field (dimension.create_linear refuses equal references); Revit never issues an all-zero
    // episode, so a placeholder sent by mistake fails with reference_not_found.
    public static string Placeholder(int index) => "00000000-0000-0000-0000-000000000000-" + (index + 1).ToString("x8", CultureInfo.InvariantCulture);

    // "doc." + the first 16 bytes (lowercase hex) of SHA-256 over the UTF-8 text "doc-regeneration/1\n<projectId:D>\n
    // <revision>\n<afterHash>\n<key>\n<command>\n<document unique_id>\n<document path>\n<canonical args>\n<sources
    // joined by ','>". A new model state or revision gives a new id, so a regenerated item is never answered from the
    // receipt of an earlier realization; the same input gives the same id (a retry replays).
    public static string OperationId(Guid projectId, int revision, string afterHash, string key, string command, RevitDocumentRef document, string canonicalArgs, IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sources);
        var text = "doc-regeneration/" + Version.ToString(CultureInfo.InvariantCulture) + "\n" + projectId.ToString("D") + "\n" + revision.ToString(CultureInfo.InvariantCulture) + "\n"
            + afterHash + "\n" + key + "\n" + command + "\n" + document.UniqueId + "\n" + document.Path + "\n" + canonicalArgs + "\n" + string.Join(",", sources);
        return "doc." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    // Every item of the set, computed from a Compiler.Validate-clean project.
    public static DocRegenerationPlan Full(Project project, DocumentationSet set, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(project);
        throw new NotImplementedException("P4-03 red: full regeneration not implemented");
    }

    // Only the affected items, computed from after. The plan must be Compiler.Diff(before, after) (plan_mismatch).
    public static DocRegenerationPlan Affected(Project before, Project after, ExecutionPlan plan, DocumentationSet set, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(plan);
        throw new NotImplementedException("P4-03 red: affected regeneration not implemented");
    }

    // previous's results for keys update does not regenerate, plus update's, under update's header. previous should be
    // a full regeneration (or an earlier merge) of the before that update came from; that is not checked here.
    public static DocRegenerationPlan Merge(DocRegenerationPlan previous, DocRegenerationPlan update)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(update);
        if (previous.ProjectId != update.ProjectId) throw new ArgumentException("project_mismatch");
        var replaced = update.Regenerated.ToHashSet(StringComparer.Ordinal);
        return update with
        {
            Regenerated = previous.Regenerated.Union(update.Regenerated, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            Steps = Order(previous.Steps.Where(step => !replaced.Contains(step.Key)).Concat(update.Steps)),
            Unsupported = previous.Unsupported.Where(item => !replaced.Contains(item.Key)).Concat(update.Unsupported).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
        };
    }

    // Same header, keys, commands, args, inputs, elements and unsupported entries; operation ids are not compared
    // (a merged plan keeps the ids its unaffected steps were realized with).
    public static bool SameContent(DocRegenerationPlan a, DocRegenerationPlan b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        static string Text(DocRegenerationPlan plan) => JsonSerializer.Serialize(plan with { Steps = plan.Steps.Select(step => step with { OperationId = "" }).ToArray() });
        return Text(a) == Text(b);
    }

    // Proof helper: the prior full regeneration with the affected items replaced equals full regeneration of after.
    public static bool Agrees(Project before, Project after, ExecutionPlan plan, DocumentationSet set, RevitBindings bindings) =>
        SameContent(Merge(Full(before, set, bindings), Affected(before, after, plan, set, bindings)), Full(after, set, bindings));

    // The protocol envelope of one step with its inputs filled (source -> realized Revit UniqueId), parsed and validated
    // again by RevitCommandProtocol.Parse.
    public static RevitCommandEnvelope Envelope(DocRegenerationPlan plan, DocCommandStep step, IReadOnlyDictionary<string, string> uniqueIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(uniqueIds);
        var args = JsonNode.Parse(step.ArgsJson) as JsonObject ?? throw new ArgumentException("args_not_object");
        foreach (var input in step.Inputs)
        {
            if (!input.Field.StartsWith("args.", StringComparison.Ordinal) || input.Field.Length == "args.".Length || input.Field.IndexOf('.', "args.".Length) >= 0)
                throw new ArgumentException("input_field_unsupported");
            if (!uniqueIds.TryGetValue(input.Source, out var uniqueId)) throw new ArgumentException("input_unresolved");
            if (!RevitCommandProtocol.IsUniqueId(uniqueId)) throw new ArgumentException("input_invalid");
            args[input.Field["args.".Length..]] = uniqueId;
        }
        return Parse(plan.Document, step.OperationId, step.Command, args.ToJsonString());
    }

    sealed record Item(string Key, string Kind, object Spec, IReadOnlyList<string> Parents);

    sealed class Refusal : Exception
    {
        public Refusal(string code, string detail) : base(detail) => Code = code;
        public string Code { get; }
    }

    static void CheckBindings(RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(bindings.Document);
        ArgumentNullException.ThrowIfNull(bindings.Levels);
        if (!RevitCopiesGuard.IsAbsoluteNormalized(bindings.Document.Path)) throw new RevitCommandException(RevitCommandCodes.DocumentPathInvalid, "document.path", "absolute saved path without . or .. segments");
        if (!RevitCommandProtocol.IsUniqueId(bindings.Document.UniqueId)) throw new RevitCommandException(RevitCommandCodes.DocumentIdentityInvalid, "document.unique_id", "ProjectInformation UniqueId required");
    }

    static void Finite(params double[] values)
    {
        if (!values.All(double.IsFinite)) throw new ArgumentException("doc_number_invalid");
    }

    // Static validation of the set: keys, kinds, element counts, references between items, finite numbers.
    static Dictionary<string, Item> Index(DocumentationSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(set.Views);
        ArgumentNullException.ThrowIfNull(set.Tags);
        ArgumentNullException.ThrowIfNull(set.Dimensions);
        ArgumentNullException.ThrowIfNull(set.Schedules);
        ArgumentNullException.ThrowIfNull(set.Sheets);
        ArgumentNullException.ThrowIfNull(set.Placements);
        var items = new Dictionary<string, Item>(StringComparer.Ordinal);
        void Add(string key, string kind, object spec, params string[] parents)
        {
            if (string.IsNullOrEmpty(key) || key.Trim().Length != key.Length || key.Any(char.IsControl)) throw new ArgumentException("doc_key_invalid");
            if (!items.TryAdd(key, new Item(key, kind, spec, parents))) throw new ArgumentException("doc_key_duplicate");
        }
        foreach (var view in set.Views)
        {
            ArgumentNullException.ThrowIfNull(view);
            ArgumentNullException.ThrowIfNull(view.Elements);
            if (view.Kind is not ("floor_plan" or "ceiling_plan" or "section" or "three_d")) throw new ArgumentException("doc_view_kind_invalid");
            if ((view.Kind != "three_d" && view.Elements.Count != 1) || view.Elements.Distinct().Count() != view.Elements.Count) throw new ArgumentException("doc_view_elements_invalid");
            Finite(view.DepthMetres);
            Add(view.Key, View, view);
        }
        foreach (var schedule in set.Schedules)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            ArgumentNullException.ThrowIfNull(schedule.Fields);
            Add(schedule.Key, Schedule, schedule);
        }
        foreach (var sheet in set.Sheets)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            Add(sheet.Key, Sheet, sheet);
        }
        foreach (var tag in set.Tags)
        {
            ArgumentNullException.ThrowIfNull(tag);
            Finite(tag.OffsetXMetres, tag.OffsetYMetres);
            Add(tag.Key, Tag, tag, tag.View);
        }
        foreach (var dimension in set.Dimensions)
        {
            ArgumentNullException.ThrowIfNull(dimension);
            Finite(dimension.OffsetMetres);
            if (dimension.First == dimension.Second) throw new ArgumentException("doc_dimension_references_invalid");
            Add(dimension.Key, Dimension, dimension, dimension.View);
        }
        foreach (var placement in set.Placements)
        {
            ArgumentNullException.ThrowIfNull(placement);
            Finite(placement.CenterXMm, placement.CenterYMm);
            Add(placement.Key, Placement, placement, placement.Sheet, placement.View);
        }
        foreach (var item in items.Values)
        {
            switch (item.Spec)
            {
                case DocTag or DocDimension:
                    if (!items.TryGetValue(item.Parents[0], out var view) || view.Spec is not DocView viewSpec) throw new ArgumentException("doc_view_unknown");
                    if (viewSpec.Kind is not ("floor_plan" or "ceiling_plan")) throw new ArgumentException("doc_view_not_plan");
                    break;
                case DocPlacement:
                    if (!items.TryGetValue(item.Parents[0], out var sheet) || sheet.Spec is not DocSheet) throw new ArgumentException("doc_sheet_unknown");
                    if (!items.TryGetValue(item.Parents[1], out var placed) || placed.Spec is not (DocView or DocSchedule)) throw new ArgumentException("doc_view_unknown");
                    break;
            }
        }
        return items;
    }

    static IEnumerable<Guid> References(Item item, IReadOnlyList<Element> elements) => item.Spec switch
    {
        DocView view => view.Elements,
        DocTag tag => new[] { tag.Element },
        DocDimension dimension => new[] { dimension.First, dimension.Second },
        DocSchedule schedule => elements.Where(element => element.Kind == schedule.ElementKind).Select(element => element.Id),
        _ => Array.Empty<Guid>(),
    };

    static DocCommandStep[] Order(IEnumerable<DocCommandStep> steps) =>
        steps.OrderBy(step => Array.IndexOf(KindOrder, step.ItemKind)).ThenBy(step => step.Key, StringComparer.Ordinal).ToArray();

    static DocRegenerationPlan Generate(Project project, Dictionary<string, Item> items, HashSet<string> keys, RevitBindings bindings)
    {
        var elements = project.Elements.ToDictionary(element => element.Id);
        var hash = Compiler.Hash(project);
        var results = new Dictionary<string, (DocCommandStep? Step, DocUnsupported? Unsupported)>(StringComparer.Ordinal);

        Element Need(Guid id) => elements.TryGetValue(id, out var element) ? element : throw new Refusal(DocRegenerationCodes.ElementMissing, "element " + id.ToString("D") + " is not in the project");
        Element NeedKind(Guid id, string kind)
        {
            var element = Need(id);
            if (element.Kind != kind) throw new Refusal(DocRegenerationCodes.KindMismatch, "element " + id.ToString("D") + " is a " + element.Kind + ", not a " + kind);
            return element;
        }
        double[] Wall(Guid id)
        {
            var g = NeedKind(id, "wall").GeometryMetres;
            if (!g.All(double.IsFinite) || !GeometryCodec.IsValidWall(g)) throw new Refusal(DocRegenerationCodes.GeometryInvalid, "wall " + id.ToString("D") + " geometry must be [ax,ay,bx,by,height,thickness]");
            return g;
        }
        // A wall's base: the elevation of its first level prerequisite, 0 when it has none.
        double Elevation(Guid wall)
        {
            foreach (var dependency in elements[wall].Dependencies)
                if (elements.TryGetValue(dependency, out var level) && level.Kind == "level" && level.GeometryMetres.Length == 1 && double.IsFinite(level.GeometryMetres[0]))
                    return level.GeometryMetres[0];
            return 0;
        }
        void Parent(string key)
        {
            var parent = items[key];
            if (Result(parent).Unsupported is not null) throw new Refusal(DocRegenerationCodes.DependencyUnsupported, parent.Kind + " " + key + " is unsupported");
        }

        (DocCommandStep? Step, DocUnsupported? Unsupported) Result(Item item)
        {
            if (results.TryGetValue(item.Key, out var known)) return known;
            (DocCommandStep?, DocUnsupported?) result;
            try
            {
                var (command, args, inputs, referenced) = Build(item);
                var canonical = CanonicalArgs(Parse(bindings.Document, PendingOperationId, command, args));
                var operationId = OperationId(project.Id, project.Revision, hash, item.Key, command, bindings.Document, canonical, inputs.Select(input => input.Source).ToArray());
                result = (new DocCommandStep(item.Key, item.Kind, operationId, command, canonical, inputs, referenced), null);
            }
            catch (Refusal refusal) { result = (null, new DocUnsupported(item.Key, item.Kind, refusal.Code, refusal.Message)); }
            catch (RevitCommandException error) { result = (null, new DocUnsupported(item.Key, item.Kind, DocRegenerationCodes.CommandInvalid, error.Code + "@" + (error.Field ?? "") + ": " + (error.Detail ?? ""))); }
            // A value too large to write as JSON (a millimetre overflow); the runtime message is not used, it may vary.
            catch (ArgumentException) { result = (null, new DocUnsupported(item.Key, item.Kind, DocRegenerationCodes.GeometryInvalid, "a coordinate is out of range")); }
            results[item.Key] = result;
            return result;
        }

        (string Command, string Args, DocStepInput[] Inputs, Guid[] Elements) Build(Item item)
        {
            switch (item.Spec)
            {
                case DocView view:
                {
                    void Options(Utf8JsonWriter writer)
                    {
                        if (view.Name is not null) writer.WriteString("name", view.Name);
                        if (view.Scale is int scale) writer.WriteNumber("scale", scale);
                    }
                    if (view.Kind is "floor_plan" or "ceiling_plan")
                    {
                        var level = NeedKind(view.Elements[0], "level");
                        if (!bindings.Levels.TryGetValue(level.Id, out var reference)) throw new Refusal(DocRegenerationCodes.LevelUnbound, "level " + level.Id.ToString("D") + " has no Revit level binding");
                        return ("view.create_plan", Json(writer =>
                        {
                            writer.WriteString("kind", view.Kind);
                            writer.WriteString("level", reference);
                            Options(writer);
                        }), Array.Empty<DocStepInput>(), view.Elements.ToArray());
                    }
                    if (view.Kind == "section")
                    {
                        var g = Wall(view.Elements[0]);
                        var z = Elevation(view.Elements[0]);
                        return ("view.create_section", Json(writer =>
                        {
                            writer.WriteStartObject("line");
                            Point(writer, "start", g[0], g[1]);
                            Point(writer, "end", g[2], g[3]);
                            writer.WriteEndObject();
                            writer.WriteNumber("depth_mm", Mm(view.DepthMetres));
                            writer.WriteNumber("bottom_elevation_mm", Mm(z));
                            writer.WriteNumber("top_elevation_mm", Mm(z + g[4]));
                            Options(writer);
                        }), Array.Empty<DocStepInput>(), view.Elements.ToArray());
                    }
                    double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
                    double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
                    foreach (var id in view.Elements)
                    {
                        var g = Wall(id);
                        var z = Elevation(id);
                        var half = g[5] / 2;
                        minX = Math.Min(minX, Math.Min(g[0], g[2]) - half);
                        maxX = Math.Max(maxX, Math.Max(g[0], g[2]) + half);
                        minY = Math.Min(minY, Math.Min(g[1], g[3]) - half);
                        maxY = Math.Max(maxY, Math.Max(g[1], g[3]) + half);
                        minZ = Math.Min(minZ, z);
                        maxZ = Math.Max(maxZ, z + g[4]);
                    }
                    return ("view.create_3d", Json(writer =>
                    {
                        if (view.Elements.Count > 0)
                        {
                            writer.WriteStartObject("section_box");
                            writer.WriteStartArray("min");
                            writer.WriteNumberValue(Mm(minX));
                            writer.WriteNumberValue(Mm(minY));
                            writer.WriteNumberValue(Mm(minZ));
                            writer.WriteEndArray();
                            writer.WriteStartArray("max");
                            writer.WriteNumberValue(Mm(maxX));
                            writer.WriteNumberValue(Mm(maxY));
                            writer.WriteNumberValue(Mm(maxZ));
                            writer.WriteEndArray();
                            writer.WriteEndObject();
                        }
                        Options(writer);
                    }), Array.Empty<DocStepInput>(), view.Elements.ToArray());
                }
                case DocTag tag:
                {
                    Parent(tag.View);
                    var centre = Centre(Need(tag.Element));
                    return ("tag.create", Json(writer =>
                    {
                        writer.WriteString("view_uid", Placeholder(0));
                        writer.WriteString("element_uid", Placeholder(1));
                        if (centre is { } c) Point(writer, "head", c.X + tag.OffsetXMetres, c.Y + tag.OffsetYMetres);
                        writer.WriteBoolean("leader", tag.Leader);
                        writer.WriteString("orientation", tag.Orientation);
                    }), new[] { new DocStepInput("args.view_uid", ItemSource(tag.View)), new DocStepInput("args.element_uid", ElementSource(tag.Element)) }, new[] { tag.Element });
                }
                case DocDimension dimension:
                {
                    Parent(dimension.View);
                    var a = Wall(dimension.First);
                    var b = Wall(dimension.Second);
                    double ax = a[2] - a[0], ay = a[3] - a[1], la = Math.Sqrt(ax * ax + ay * ay);
                    double bx = b[2] - b[0], by = b[3] - b[1], lb = Math.Sqrt(bx * bx + by * by);
                    double ux = ax / la, uy = ay / la;
                    if (Math.Abs(ux * (by / lb) - uy * (bx / lb)) > ParallelTolerance)
                        throw new Refusal(DocRegenerationCodes.ReferencesNotParallel, "walls " + dimension.First.ToString("D") + " and " + dimension.Second.ToString("D") + " are not parallel");
                    double sx = (a[0] + a[2]) / 2 + ux * dimension.OffsetMetres, sy = (a[1] + a[3]) / 2 + uy * dimension.OffsetMetres;
                    double nx = -uy, ny = ux;
                    var distance = (b[0] - sx) * nx + (b[1] - sy) * ny;
                    return ("dimension.create_linear", Json(writer =>
                    {
                        writer.WriteString("view_uid", Placeholder(0));
                        writer.WriteString("first_uid", Placeholder(1));
                        writer.WriteString("second_uid", Placeholder(2));
                        writer.WriteStartObject("line");
                        Point(writer, "start", sx, sy);
                        Point(writer, "end", sx + nx * distance, sy + ny * distance);
                        writer.WriteEndObject();
                        writer.WriteString("wall_face", dimension.WallFace);
                    }), new[] { new DocStepInput("args.view_uid", ItemSource(dimension.View)), new DocStepInput("args.first_uid", ElementSource(dimension.First)), new DocStepInput("args.second_uid", ElementSource(dimension.Second)) },
                    new[] { dimension.First, dimension.Second });
                }
                case DocSchedule schedule:
                    return ("schedule.create", Json(writer =>
                    {
                        writer.WriteString("category", schedule.Category);
                        writer.WriteStartArray("fields");
                        foreach (var field in schedule.Fields) writer.WriteStringValue(field);
                        writer.WriteEndArray();
                        if (schedule.SortBy is not null) writer.WriteString("sort_by", schedule.SortBy);
                        if (schedule.Name is not null) writer.WriteString("name", schedule.Name);
                    }), Array.Empty<DocStepInput>(), project.Elements.Where(element => element.Kind == schedule.ElementKind).Select(element => element.Id).Order().ToArray());
                case DocSheet sheet:
                    return ("sheet.create", Json(writer =>
                    {
                        writer.WriteString("number", sheet.Number);
                        writer.WriteString("name", sheet.Name);
                        if (sheet.TitleBlock is not null) writer.WriteString("title_block", sheet.TitleBlock);
                    }), Array.Empty<DocStepInput>(), Array.Empty<Guid>());
                case DocPlacement placement:
                    Parent(placement.Sheet);
                    Parent(placement.View);
                    return ("sheet.place_view", Json(writer =>
                    {
                        writer.WriteString("sheet_uid", Placeholder(0));
                        writer.WriteString("view_uid", Placeholder(1));
                        writer.WriteStartArray("center");
                        writer.WriteNumberValue(placement.CenterXMm);
                        writer.WriteNumberValue(placement.CenterYMm);
                        writer.WriteEndArray();
                    }), new[] { new DocStepInput("args.sheet_uid", ItemSource(placement.Sheet)), new DocStepInput("args.view_uid", ItemSource(placement.View)) }, Array.Empty<Guid>());
                default:
                    throw new InvalidOperationException("doc_item_unknown");
            }
        }

        var steps = new List<DocCommandStep>();
        var unsupported = new List<DocUnsupported>();
        var ordered = keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        foreach (var key in ordered)
        {
            var (step, refused) = Result(items[key]);
            if (step is not null) steps.Add(step);
            if (refused is not null) unsupported.Add(refused);
        }
        return new DocRegenerationPlan(Version, project.Id, project.Revision, hash, bindings.Document, ordered,
            Order(steps), unsupported.ToArray(), items.Count);
    }

    // Plan centre of an element: wall axis midpoint, space rectangle centre, opening axis midpoint (absolute
    // [ax, ay, bx, by, sill, height]); null otherwise (the tag then sits at Revit's element centre).
    static (double X, double Y)? Centre(Element element)
    {
        var g = element.GeometryMetres;
        if (!g.All(double.IsFinite)) return null;
        return element.Kind switch
        {
            "wall" when GeometryCodec.IsValidWall(g) => ((g[0] + g[2]) / 2, (g[1] + g[3]) / 2),
            "space" when g.Length == 4 => (g[0] + g[2] / 2, g[1] + g[3] / 2),
            "door" or "window" or "opening" when g.Length == 6 => ((g[0] + g[2]) / 2, (g[1] + g[3]) / 2),
            _ => null,
        };
    }

    static RevitCommandEnvelope Parse(RevitDocumentRef document, string operationId, string command, string args) => RevitCommandProtocol.Parse(Json(writer =>
    {
        writer.WriteNumber("protocol_version", RevitCommandProtocol.Version);
        writer.WriteString("operation_id", operationId);
        writer.WriteStartObject("document");
        writer.WriteString("path", document.Path);
        writer.WriteString("unique_id", document.UniqueId);
        writer.WriteEndObject();
        writer.WriteString("command", command);
        writer.WritePropertyName("args");
        writer.WriteRawValue(args);
    }));

    static string CanonicalArgs(RevitCommandEnvelope envelope)
    {
        using var canonical = JsonDocument.Parse(envelope.CanonicalJson);
        return canonical.RootElement.GetProperty("args").GetRawText();
    }

    static string Json(Action<Utf8JsonWriter> members)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            members(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    static void Point(Utf8JsonWriter writer, string name, double xMetres, double yMetres)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(Mm(xMetres));
        writer.WriteNumberValue(Mm(yMetres));
        writer.WriteEndArray();
    }

    // Metres to millimetres, rounded to 1e-6 mm so binary noise does not reach the command; -0 is 0.
    static double Mm(double metres)
    {
        var millimetres = Math.Round(metres * Length.MillimetresPerMetre, 6, MidpointRounding.ToEven);
        return millimetres == 0 ? 0.0 : millimetres;
    }
}
