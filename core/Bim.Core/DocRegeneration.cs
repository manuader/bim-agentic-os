using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Bim.Core;

// P4-03 affected-only documentation regeneration. A documentation set names views, tags, dimensions, schedules,
// sheets and sheet placements by stable keys and binds them to BIM-IR element ids. Full regeneration turns every item
// into one catalogue documentation command (P4-01/P4-02) computed from the project; affected-only regeneration, given
// a Compiler.Diff, emits only the items whose result (command, args, inputs, elements, or unsupported entry) differs
// between before and after, plus the tags, dimensions and placements whose view, schedule or sheet is regenerated with
// them, each step byte-identical to the same item's step in full regeneration of after. The set is caller-owned: the BIM-IR plan, schedule and sheet
// elements (RoomCompiler emits them) are read only by items that name one in Bound. Vendor-free and BCL only; no BIM-IR or
// persisted format change. Contract: docs/contracts/module-contracts.md, "Affected-only documentation regeneration".

// Kind: floor_plan or ceiling_plan (Elements: exactly the BIM-IR level), section (Elements: exactly the BIM-IR wall
// it cuts along; DepthMetres: far clip depth) or three_d (Elements: walls framed by its section box; none for an
// unbounded view). Name and Scale pass through to the view command. Bound: optionally the BIM-IR "plan" element this
// view realizes; the view is unsupported while that element is missing or another kind.
public sealed record DocView(string Key, string Kind, IReadOnlyList<Guid> Elements, string? Name = null, int? Scale = null, double DepthMetres = 0, Guid? Bound = null);
// A tag of one BIM-IR element in a plan view of the set; its head is the element's plan centre plus the offset: wall
// axis midpoint, space centre, the midpoint of an absolute opening [ax, ay, bx, by, sill, height], or for a hosted
// opening [offset, width] the point offset + width / 2 along its host wall's axis (its first wall dependency).
public sealed record DocTag(string Key, string View, Guid Element, double OffsetXMetres = 0, double OffsetYMetres = 0, bool Leader = false, string Orientation = "horizontal");
// A linear dimension in a plan view of the set between two parallel BIM-IR walls; the line starts at the first wall's
// axis midpoint moved OffsetMetres along that axis and runs perpendicular onto the second wall's axis.
public sealed record DocDimension(string Key, string View, Guid First, Guid Second, double OffsetMetres = 0, string WallFace = "exterior");
// A schedule of every BIM-IR element of ElementKind, as the Revit category Category (a catalogue category name).
// Bound: optionally the BIM-IR "schedule" element it realizes (as DocView.Bound).
public sealed record DocSchedule(string Key, string ElementKind, string Category, IReadOnlyList<string> Fields, string? SortBy = null, string? Name = null, Guid? Bound = null);
// Bound: optionally the BIM-IR "sheet" element it realizes (as DocView.Bound).
public sealed record DocSheet(string Key, string Number, string Name, string? TitleBlock = null, Guid? Bound = null);
// Places the view or schedule with key View on the sheet with key Sheet, centred at sheet millimetres.
public sealed record DocPlacement(string Key, string Sheet, string View, double CenterXMm, double CenterYMm);
public sealed record DocumentationSet(IReadOnlyList<DocView> Views, IReadOnlyList<DocTag> Tags, IReadOnlyList<DocDimension> Dimensions,
    IReadOnlyList<DocSchedule> Schedules, IReadOnlyList<DocSheet> Sheets, IReadOnlyList<DocPlacement> Placements);

// Field ("args.<member>") takes the Revit UniqueId of Source: "doc:<key>" (the realized view, schedule or sheet of that
// item) or "element:<BIM-IR id D>" (the realized element). Filled by DocRegeneration.Envelope.
public sealed record DocStepInput(string Field, string Source);
// ArgsJson is canonical (RevitCommandProtocol.Canonical) with a placeholder UniqueId in every input field. Elements:
// the BIM-IR elements the result was computed from, in a fixed order (view elements then the levels their walls stand
// on; tagged element then its host; first and second wall; scheduled elements by id; then Bound). An unsupported
// entry lists the same.
public sealed record DocCommandStep(string Key, string ItemKind, string OperationId, string Command, string ArgsJson, IReadOnlyList<DocStepInput> Inputs, IReadOnlyList<Guid> Elements);
public sealed record DocUnsupported(string Key, string ItemKind, string Code, string Detail, IReadOnlyList<Guid> Elements);
// InputDigest: SHA-256 of the set and bindings; Merge refuses plans with different digests (Affected and Agrees assume
// the prior Full(before) was made with the same set and bindings). Regenerated: every item key this plan regenerates (sorted ordinally); each has exactly one step or one unsupported
// entry. The caller retires the prior realization of every regenerated key (re-tag, re-dimension, schedule refresh)
// and runs the steps in order. TotalItems counts the whole set.
public sealed record DocRegenerationPlan(int Version, Guid ProjectId, int Revision, string AfterHash, RevitDocumentRef Document, string InputDigest,
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
    public const string HostMissing = "host_missing";
    public const string HostAmbiguous = "host_ambiguous";
    public const string DependencyUnsupported = "dependency_unsupported";
    public const string CommandInvalid = "command_invalid";
}

// INVARIANT (soundness and minimality of affected-only regeneration): an item's result is a function of the bindings,
// the status of its own view, schedule or sheet, and the kind, geometry and dependency list of exactly the elements
// Reads(item, project) lists (also its Elements): view elements and the level each wall resolves to (its first
// dependency that is a level with [elevation]), the tagged element and every wall dependency of a hosted opening, the
// two dimensioned walls, every element of a schedule's kind, and Bound. A resolution can only move when the element it
// starts from changed or when an element it lands on in before or after changed, so an item none of whose reads in
// before or after changed has the same result in both. Affected computes the remaining candidates in before and in
// after and keeps those whose content (everything but the operation id) differs, then adds the tags, dimensions and
// placements of a kept view, schedule or sheet. Hence Merge(Full(before), Affected(...)) has the content of
// Full(after) (Agrees), and an item regenerated on its own always has a different full result.
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
    // <revision>\n<afterHash>\n<key>\n<scope>\n<command>\n<document unique_id>\n<document path>\n<canonical args>\n
    // <sources joined by ','>". Scope is a schedule's element kind, empty otherwise. Keys hold no ',' or control
    // character, so the text is unambiguous. A new model state or revision gives a new id, so a regenerated item is never answered from the
    // receipt of an earlier realization; the same input gives the same id (a retry replays).
    public static string OperationId(Guid projectId, int revision, string afterHash, string key, string scope, string command, RevitDocumentRef document, string canonicalArgs, IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sources);
        var text = "doc-regeneration/" + Version.ToString(CultureInfo.InvariantCulture) + "\n" + projectId.ToString("D") + "\n" + revision.ToString(CultureInfo.InvariantCulture) + "\n"
            + afterHash + "\n" + key + "\n" + scope + "\n" + command + "\n" + document.UniqueId + "\n" + document.Path + "\n" + canonicalArgs + "\n" + string.Join(",", sources);
        return "doc." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16).ToLowerInvariant();
    }

    // Every item of the set, computed from a Compiler.Validate-clean project.
    public static DocRegenerationPlan Full(Project project, DocumentationSet set, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(project);
        var items = Index(set);
        CheckBindings(bindings);
        if (Compiler.Validate(project).Length > 0) throw new InvalidOperationException("invalid_ir");
        return Assemble(project, items, items.Keys, bindings, Generator(project, items, bindings));
    }

    // Only the affected items, computed from after. The plan must be Compiler.Diff(before, after) (plan_mismatch).
    public static DocRegenerationPlan Affected(Project before, Project after, ExecutionPlan plan, DocumentationSet set, RevitBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(plan);
        var items = Index(set);
        CheckBindings(bindings);
        if (JsonSerializer.Serialize(Compiler.Diff(before, after)) != JsonSerializer.Serialize(plan)) throw new InvalidOperationException("plan_mismatch");
        var prior = before.Elements.ToDictionary(element => element.Id);
        var next = after.Elements.ToDictionary(element => element.Id);
        // Real changes only: an update of provenance alone is an operation no documentation command can show.
        var changed = plan.Operations.Select(operation => operation.Element.Id).Where(id => !prior.TryGetValue(id, out var a) || !next.TryGetValue(id, out var b)
            || a.Kind != b.Kind || !a.GeometryMetres.SequenceEqual(b.GeometryMetres) || !a.Dependencies.SequenceEqual(b.Dependencies)).ToHashSet();
        // Candidates: an element the item reads in before or after really changed; nothing else can change a result.
        // Each candidate is computed in both states and kept only when its content (all but the operation id) differs.
        var resultBefore = Generator(before, items, bindings);
        var resultAfter = Generator(after, items, bindings);
        var direct = items.Values.Where(item => Reads(item, prior).Concat(Reads(item, next)).Any(changed.Contains))
            .Where(item => Content(resultBefore(item)) != Content(resultAfter(item))).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        // A tag, dimension or placement re-runs with its regenerated view, schedule or sheet (the realized parent is
        // new). Parents have no parents themselves: one pass closes the set.
        var affected = new HashSet<string>(direct, StringComparer.Ordinal);
        foreach (var item in items.Values) if (item.Parents.Any(direct.Contains)) affected.Add(item.Key);
        return Assemble(after, items, affected, bindings, resultAfter);
    }

    // previous's results for keys update does not regenerate, plus update's, under update's header. previous should be
    // a full regeneration (or an earlier merge) of the before that update came from; the model is not checked here, but
    // a different project (project_mismatch) or another set or bindings (input_mismatch, by InputDigest) is refused.
    public static DocRegenerationPlan Merge(DocRegenerationPlan previous, DocRegenerationPlan update)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(update);
        if (previous.ProjectId != update.ProjectId) throw new ArgumentException("project_mismatch");
        if (previous.InputDigest != update.InputDigest) throw new ArgumentException("input_mismatch");
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
            if (input?.Field is null) throw new ArgumentException("input_field_unsupported");
            if (input.Source is null) throw new ArgumentException("input_unresolved");
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

    // Strings must be valid UTF-16 (no lone surrogate), so they can be written as JSON; required ones must not be null.
    static bool ValidText(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { i++; continue; }
            if (char.IsSurrogate(value[i])) return false;
        }
        return true;
    }

    static void Text(string? value, bool required)
    {
        if (value is null ? required : !ValidText(value)) throw new ArgumentException("doc_text_invalid");
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
            if (string.IsNullOrEmpty(key) || key.Trim().Length != key.Length || key.Any(char.IsControl) || key.Contains(',') || !ValidText(key)) throw new ArgumentException("doc_key_invalid");
            if (!items.TryAdd(key, new Item(key, kind, spec, parents))) throw new ArgumentException("doc_key_duplicate");
        }
        foreach (var view in set.Views)
        {
            ArgumentNullException.ThrowIfNull(view);
            ArgumentNullException.ThrowIfNull(view.Elements);
            if (view.Kind is not ("floor_plan" or "ceiling_plan" or "section" or "three_d")) throw new ArgumentException("doc_view_kind_invalid");
            if ((view.Kind != "three_d" && view.Elements.Count != 1) || view.Elements.Distinct().Count() != view.Elements.Count) throw new ArgumentException("doc_view_elements_invalid");
            Finite(view.DepthMetres);
            Text(view.Name, false);
            Add(view.Key, View, view);
        }
        foreach (var schedule in set.Schedules)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            if (schedule.Fields is null) throw new ArgumentException("doc_text_invalid");
            Text(schedule.ElementKind, true);
            Text(schedule.Category, true);
            foreach (var field in schedule.Fields) Text(field, true);
            Text(schedule.SortBy, false);
            Text(schedule.Name, false);
            Add(schedule.Key, Schedule, schedule);
        }
        foreach (var sheet in set.Sheets)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            Text(sheet.Number, true);
            Text(sheet.Name, true);
            Text(sheet.TitleBlock, false);
            Add(sheet.Key, Sheet, sheet);
        }
        foreach (var tag in set.Tags)
        {
            ArgumentNullException.ThrowIfNull(tag);
            Finite(tag.OffsetXMetres, tag.OffsetYMetres);
            Text(tag.Orientation, true);
            Add(tag.Key, Tag, tag, tag.View);
        }
        foreach (var dimension in set.Dimensions)
        {
            ArgumentNullException.ThrowIfNull(dimension);
            Finite(dimension.OffsetMetres);
            Text(dimension.WallFace, true);
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
                    if (item.Parents[0] is not string viewKey || !items.TryGetValue(viewKey, out var view) || view.Spec is not DocView viewSpec) throw new ArgumentException("doc_view_unknown");
                    if (viewSpec.Kind is not ("floor_plan" or "ceiling_plan")) throw new ArgumentException("doc_view_not_plan");
                    break;
                case DocPlacement:
                    if (item.Parents[0] is not string sheetKey || !items.TryGetValue(sheetKey, out var sheet) || sheet.Spec is not DocSheet) throw new ArgumentException("doc_sheet_unknown");
                    if (item.Parents[1] is not string placedKey || !items.TryGetValue(placedKey, out var placed) || placed.Spec is not (DocView or DocSchedule)) throw new ArgumentException("doc_view_unknown");
                    break;
            }
        }
        return items;
    }

    // The level a wall stands on in a project: its first dependency that is a level with [elevation] (null for none).
    static Element? LevelOf(Element wall, IReadOnlyDictionary<Guid, Element> elements)
    {
        foreach (var dependency in wall.Dependencies)
            if (elements.TryGetValue(dependency, out var level) && level.Kind == "level" && level.GeometryMetres.Length == 1 && double.IsFinite(level.GeometryMetres[0])) return level;
        return null;
    }

    static bool IsHosted(Element element) => element.Kind is "door" or "window" or "opening" && element.GeometryMetres.Length == 2;

    // The walls a hosted opening [offset, width] may stand in: its dependencies that are walls, in order. Exactly one
    // is its host (none: host_missing, several: host_ambiguous, as in LocalValidation).
    static Element[] HostsOf(Element opening, IReadOnlyDictionary<Guid, Element> elements) =>
        opening.Dependencies.Distinct().Where(dependency => elements.TryGetValue(dependency, out var wall) && wall.Kind == "wall").Select(dependency => elements[dependency]).ToArray();

    // Every element an item's result reads in this project, in the documented order, without repeats.
    static Guid[] Reads(Item item, IReadOnlyDictionary<Guid, Element> elements)
    {
        var reads = new List<Guid>();
        void Read(Guid id) { if (!reads.Contains(id)) reads.Add(id); }
        void Walls(IEnumerable<Guid> walls)
        {
            var ids = walls.ToArray();
            foreach (var id in ids) Read(id);
            foreach (var id in ids)
                if (elements.TryGetValue(id, out var wall) && wall.Kind == "wall" && LevelOf(wall, elements) is { } level) Read(level.Id);
        }
        switch (item.Spec)
        {
            case DocView view:
                if (view.Kind is "floor_plan" or "ceiling_plan") Read(view.Elements[0]); else Walls(view.Elements);
                if (view.Bound is Guid boundView) Read(boundView);
                break;
            case DocTag tag:
                Read(tag.Element);
                if (elements.TryGetValue(tag.Element, out var tagged) && IsHosted(tagged)) foreach (var host in HostsOf(tagged, elements)) Read(host.Id);
                break;
            case DocDimension dimension:
                Read(dimension.First);
                Read(dimension.Second);
                break;
            case DocSchedule schedule:
                foreach (var id in elements.Values.Where(element => element.Kind == schedule.ElementKind).Select(element => element.Id).Order()) Read(id);
                if (schedule.Bound is Guid boundSchedule) Read(boundSchedule);
                break;
            case DocSheet sheet:
                if (sheet.Bound is Guid boundSheet) Read(boundSheet);
                break;
        }
        return reads.ToArray();
    }

    static DocCommandStep[] Order(IEnumerable<DocCommandStep> steps) =>
        steps.OrderBy(step => Array.IndexOf(KindOrder, step.ItemKind)).ThenBy(step => step.Key, StringComparer.Ordinal).ToArray();

    // Item results for one project, computed on first use and memoized (a child reads its parent's status).
    static Func<Item, (DocCommandStep? Step, DocUnsupported? Unsupported)> Generator(Project project, Dictionary<string, Item> items, RevitBindings bindings)
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
        // A wall's base: the elevation of the level it resolves to, 0 when it has none.
        double Elevation(Guid wall) => LevelOf(elements[wall], elements)?.GeometryMetres[0] ?? 0;
        // The BIM-IR documentation element an item realizes must exist with its kind.
        void Bound(Guid? bound, string kind)
        {
            if (bound is Guid id) NeedKind(id, kind);
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
            var reads = Reads(item, elements);
            try
            {
                var (command, args, inputs) = Build(item);
                var canonical = CanonicalArgs(Parse(bindings.Document, PendingOperationId, command, args));
                var scope = item.Spec is DocSchedule scheduled ? scheduled.ElementKind : "";
                var operationId = OperationId(project.Id, project.Revision, hash, item.Key, scope, command, bindings.Document, canonical, inputs.Select(input => input.Source).ToArray());
                result = (new DocCommandStep(item.Key, item.Kind, operationId, command, canonical, inputs, reads), null);
            }
            catch (Refusal refusal) { result = (null, new DocUnsupported(item.Key, item.Kind, refusal.Code, refusal.Message, reads)); }
            catch (RevitCommandException error) { result = (null, new DocUnsupported(item.Key, item.Kind, DocRegenerationCodes.CommandInvalid, error.Code + "@" + (error.Field ?? "") + ": " + (error.Detail ?? ""), reads)); }
            // A number too large to write as JSON (a millimetre overflow); strings were checked by Index. The runtime
            // message is not used: it may vary.
            catch (ArgumentException) { result = (null, new DocUnsupported(item.Key, item.Kind, DocRegenerationCodes.GeometryInvalid, "a coordinate is out of range", reads)); }
            results[item.Key] = result;
            return result;
        }

        (string Command, string Args, DocStepInput[] Inputs) Build(Item item)
        {
            switch (item.Spec)
            {
                case DocView view:
                {
                    Bound(view.Bound, "plan");
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
                        }), Array.Empty<DocStepInput>());
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
                        }), Array.Empty<DocStepInput>());
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
                    }), Array.Empty<DocStepInput>());
                }
                case DocTag tag:
                {
                    Parent(tag.View);
                    var tagged = Need(tag.Element);
                    var centre = Centre(tagged);
                    if (IsHosted(tagged))
                    {
                        var hosts = HostsOf(tagged, elements);
                        if (hosts.Length == 0) throw new Refusal(DocRegenerationCodes.HostMissing, "hosted " + tagged.Kind + " " + tagged.Id.ToString("D") + " has no host wall");
                        if (hosts.Length > 1) throw new Refusal(DocRegenerationCodes.HostAmbiguous, "hosted " + tagged.Kind + " " + tagged.Id.ToString("D") + " depends on more than one wall");
                        var host = hosts[0];
                        var h = Wall(host.Id);
                        double hx = h[2] - h[0], hy = h[3] - h[1], length = Math.Sqrt(hx * hx + hy * hy);
                        var g = tagged.GeometryMetres;
                        if (!(length > 0) || !g.All(double.IsFinite)) throw new Refusal(DocRegenerationCodes.GeometryInvalid, "hosted " + tagged.Kind + " " + tagged.Id.ToString("D") + " has no position on its host");
                        var along = g[0] + g[1] / 2;
                        centre = (h[0] + hx / length * along, h[1] + hy / length * along);
                    }
                    return ("tag.create", Json(writer =>
                    {
                        writer.WriteString("view_uid", Placeholder(0));
                        writer.WriteString("element_uid", Placeholder(1));
                        if (centre is { } c) Point(writer, "head", c.X + tag.OffsetXMetres, c.Y + tag.OffsetYMetres);
                        writer.WriteBoolean("leader", tag.Leader);
                        writer.WriteString("orientation", tag.Orientation);
                    }), new[] { new DocStepInput("args.view_uid", ItemSource(tag.View)), new DocStepInput("args.element_uid", ElementSource(tag.Element)) });
                }
                case DocDimension dimension:
                {
                    Parent(dimension.View);
                    var a = Wall(dimension.First);
                    var b = Wall(dimension.Second);
                    double ax = a[2] - a[0], ay = a[3] - a[1], la = Math.Sqrt(ax * ax + ay * ay);
                    double bx = b[2] - b[0], by = b[3] - b[1], lb = Math.Sqrt(bx * bx + by * by);
                    if (!(la > 0) || !(lb > 0)) throw new Refusal(DocRegenerationCodes.GeometryInvalid, "a dimensioned wall has no measurable length");
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
                    }), new[] { new DocStepInput("args.view_uid", ItemSource(dimension.View)), new DocStepInput("args.first_uid", ElementSource(dimension.First)), new DocStepInput("args.second_uid", ElementSource(dimension.Second)) });
                }
                case DocSchedule schedule:
                    Bound(schedule.Bound, "schedule");
                    return ("schedule.create", Json(writer =>
                    {
                        writer.WriteString("category", schedule.Category);
                        writer.WriteStartArray("fields");
                        foreach (var field in schedule.Fields) writer.WriteStringValue(field);
                        writer.WriteEndArray();
                        if (schedule.SortBy is not null) writer.WriteString("sort_by", schedule.SortBy);
                        if (schedule.Name is not null) writer.WriteString("name", schedule.Name);
                    }), Array.Empty<DocStepInput>());
                case DocSheet sheet:
                    Bound(sheet.Bound, "sheet");
                    return ("sheet.create", Json(writer =>
                    {
                        writer.WriteString("number", sheet.Number);
                        writer.WriteString("name", sheet.Name);
                        if (sheet.TitleBlock is not null) writer.WriteString("title_block", sheet.TitleBlock);
                    }), Array.Empty<DocStepInput>());
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
                    }), new[] { new DocStepInput("args.sheet_uid", ItemSource(placement.Sheet)), new DocStepInput("args.view_uid", ItemSource(placement.View)) });
                default:
                    throw new InvalidOperationException("doc_item_unknown");
            }
        }

        return Result;
    }

    static DocRegenerationPlan Assemble(Project project, Dictionary<string, Item> items, IEnumerable<string> keys, RevitBindings bindings,
        Func<Item, (DocCommandStep? Step, DocUnsupported? Unsupported)> result)
    {
        var steps = new List<DocCommandStep>();
        var unsupported = new List<DocUnsupported>();
        var ordered = keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        foreach (var key in ordered)
        {
            var (step, refused) = result(items[key]);
            if (step is not null) steps.Add(step);
            if (refused is not null) unsupported.Add(refused);
        }
        return new DocRegenerationPlan(Version, project.Id, project.Revision, Compiler.Hash(project), bindings.Document, Digest(items, bindings), ordered,
            Order(steps), unsupported.ToArray(), items.Count);
    }

    // An item's result without its operation id (which changes with every model state).
    static string Content((DocCommandStep? Step, DocUnsupported? Unsupported) result) =>
        result.Step is { } step ? JsonSerializer.Serialize(step with { OperationId = "" }) : JsonSerializer.Serialize(result.Unsupported);

    // Lowercase hex SHA-256 of the set (items by key, ordinal) and the bindings (levels by id), so a merge can refuse an
    // update computed with another set or other bindings than the prior regeneration.
    static string Digest(Dictionary<string, Item> items, RevitBindings bindings)
    {
        var text = JsonSerializer.Serialize(new
        {
            items = items.Values.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new { kind = item.Kind, spec = JsonSerializer.Serialize(item.Spec, item.Spec.GetType()) }).ToArray(),
            document = bindings.Document,
            levels = bindings.Levels.OrderBy(pair => pair.Key).Select(pair => new { id = pair.Key.ToString("D"), reference = pair.Value }).ToArray(),
            wall_types = bindings.WallTypes?.OrderBy(type => type.ThicknessMetres).ToArray(),
            door = bindings.DoorSymbol,
            window = bindings.WindowSymbol,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    // Plan centre of an element: wall axis midpoint, space rectangle centre, opening axis midpoint (absolute
    // [ax, ay, bx, by, sill, height]); null otherwise (the tag then sits at Revit's element centre). Hosted openings
    // are placed on their host by the tag builder.
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
