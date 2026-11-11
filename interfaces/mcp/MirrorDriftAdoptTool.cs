#nullable enable
using System.Text.Json;
using Bim.Cli;
using Bim.Core;

namespace Bim.Mcp;

// P2-13/P2-15: the one engine behind the CLI verb `mirror-drift-adopt store-directory selection.json`
// (interfaces/cli/MirrorDriftAdoptCommand.cs, which reads the selection file and prints Run's text) and the
// confirm-gated MCP tool mirror_drift_adopt (McpServer, permission label write). It commits the adopt operations of a
// P2-10 selection (the canonical MirrorDriftSelection.ToJson() text that mirror-drift-select writes and
// mirror_drift_select returns) into the Core store through the P2-12 MirrorDriftAdoptCommit.Commit, which owns every
// store rule (stale revision and hash, project, replay by key, lock, validation; nothing written on refusal except
// drift_adopt_commit_incomplete).
//
// Selection text: parsed strictly by MirrorDriftAdoptSelectionFile.Parse: it must be exactly the canonical JSON of the
// selection it describes (parse then ToJson gives the same text), else drift_adopt_selection_invalid. So the commit key,
// which hashes the canonical selection, hashes the text itself. Only when the selection has adopt operations is the
// store used: a blank store path (empty or whitespace) is then input_path_invalid (detail "store") and the store's
// project.json is checked through BoundedInput first (detail "store"). A restore-only or empty selection never opens
// the store, whatever its path (as Commit), and gives nothing_to_adopt.
//
// confirm true (the CLI verb, and the tool with confirm:true): Commit. Success
// {"status":"succeeded","outcome":"committed"|"replayed"|"nothing_to_adopt","revision":n,"key":k|null}.
// confirm false (the tool without confirm:true): the plan only, nothing written (no lock taken, no file created):
// {"status":"planned","applied":false,"outcome":"would_commit"|"would_replay"|"nothing_to_adopt","revision":n,"key":k|null,
// "plan":ExecutionPlan|null}, where revision is what the confirmed call would report (head + 1, the current head, or
// the selection's revision) and plan is MirrorDriftAdoptCommit.Plan's Compiler.Diff plan (would_commit only). The
// store is read once, read-only, as Commit reads it (strict UTF-8 project.json, same drift_adopt_store_* codes), then
// the receipt (would_replay), then MirrorDriftAdoptCommit.Plan (project, revision, hash, operations, validation), in
// Commit's order, so a plan refusal is the text the confirmed call would give on that store. The plan is a snapshot:
// the store may move before a confirmed call, which re-checks everything.
// Every refusal is one {"status":"failed","code"[,"detail"]} envelope (Succeeded false). A typed drift_adopt_* refusal
// other than drift_adopt_commit_incomplete leaves the store unchanged (MirrorDriftAdoptCommit's contract). An
// unexpected exception is drift_adopt_internal_error with only its type name as detail (as MirrorDriftTool); one raised
// by the commit gives NO nothing-written guarantee (it may come after ProjectStore.Commit started writing).
// The restore plan is not run and the map changes are not written (P2-12 scope); no provenance (P2-11).
internal static class MirrorDriftAdoptTool
{
    public const string InputPathInvalid = "input_path_invalid";
    public const string Planned = "planned";
    public const string WouldCommit = "would_commit";
    public const string WouldReplay = "would_replay";
    static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    public sealed record Outcome(bool Succeeded, string Text);

    public static Outcome Run(string storeDirectory, string selectionText, bool confirm)
    {
        MirrorDriftSelection selection;
        try { selection = MirrorDriftAdoptSelectionFile.Parse(selectionText); }
        catch (MirrorDriftAdoptException refusal) { return Refuse(refusal.Code, Detail(refusal)); }
        catch (Exception unexpected) { return Refuse(MirrorDriftAdoptCodes.SelectionInvalid, unexpected.GetType().Name); }
        try
        {
            if (selection.Operations.Count == 0)
            {
                // Nothing to commit: the store is never used. Key runs the same selection checks Commit does first.
                MirrorDriftAdoptCommit.Key(selection);
                return confirm
                    ? Succeed(MirrorDriftAdoptOutcomes.NothingToAdopt, selection.CoreRevision, null)
                    : Plan(MirrorDriftAdoptOutcomes.NothingToAdopt, selection.CoreRevision, null, null);
            }
            if (string.IsNullOrWhiteSpace(storeDirectory) || storeDirectory.IndexOf('\0') >= 0) return Refuse(InputPathInvalid, "store");
            var projectPath = Path.Combine(storeDirectory, "project.json");
            try { BoundedInput.ReadText(projectPath, FileShare.ReadWrite | FileShare.Delete); }
            catch (InputRejectedException rejected) { return Refuse(rejected.Message, "store"); }
            if (!confirm) return Preview(projectPath, selection);
            var result = MirrorDriftAdoptCommit.Commit(storeDirectory, selection);
            return Succeed(result.Outcome, result.Revision, result.Key);
        }
        catch (MirrorDriftAdoptException refusal) { return Refuse(refusal.Code, Detail(refusal)); }
        catch (InputRejectedException rejected) { return Refuse(rejected.Message, "store"); }
        catch (Exception unexpected) { return Refuse(MirrorDriftAdoptCodes.InternalError, unexpected.GetType().Name); }
    }

    // The read-only half of Commit: its store read (same codes and details), receipt check and Plan; nothing written.
    static Outcome Preview(string projectPath, MirrorDriftSelection selection)
    {
        var key = MirrorDriftAdoptCommit.Key(selection);
        var state = ReadState(projectPath);
        if (state.Receipts.ContainsKey(key)) return Plan(WouldReplay, state.Project.Revision, key, null);
        var (_, plan) = MirrorDriftAdoptCommit.Plan(state.Project, selection);
        return Plan(WouldCommit, plan.ExpectedRevision + 1, key, plan);
    }

    // As MirrorDriftAdoptCommit's store read: a StreamReader over strict UTF-8 (byte order marks detected), bounded.
    static ProjectStore.State ReadState(string projectPath)
    {
        var bytes = BoundedInput.ReadBytes(projectPath, BoundedInput.MaxBytes, FileShare.ReadWrite | FileShare.Delete);
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), StrictUtf8);
            text = reader.ReadToEnd();
        }
        catch (System.Text.DecoderFallbackException) { throw new MirrorDriftAdoptException(MirrorDriftAdoptCodes.StoreInvalid, "project.json is not UTF-8"); }
        ProjectStore.State? state;
        try { state = JsonSerializer.Deserialize<ProjectStore.State>(text); }
        catch (JsonException) { throw new MirrorDriftAdoptException(MirrorDriftAdoptCodes.StoreInvalid, "project.json is not a store state"); }
        var project = state?.Project;
        if (state?.Receipts is null || project?.Elements is null || project.Elements.Any(element => element is null || element.GeometryMetres is null || element.Dependencies is null))
            throw new MirrorDriftAdoptException(MirrorDriftAdoptCodes.StoreInvalid, "project.json has a null project, element, geometry, dependencies or receipts");
        return state;
    }

    static Outcome Succeed(string outcome, int revision, string? key) =>
        new(true, JsonSerializer.Serialize(new { status = "succeeded", outcome, revision, key }));

    static Outcome Plan(string outcome, int revision, string? key, ExecutionPlan? plan) =>
        new(true, JsonSerializer.Serialize(new { status = Planned, applied = false, outcome, revision, key, plan }));

    static Outcome Refuse(string code, string? detail) => new(false, MirrorDriftTool.Failure(code, detail));

    static string? Detail(MirrorDriftAdoptException refusal) => refusal.Message.Length > refusal.Code.Length + 1 ? refusal.Message[(refusal.Code.Length + 1)..] : null;
}

// The strict reader of a P2-10 selection file: the inverse of MirrorDriftSelection.ToJson(), accepting only text that
// ToJson gives back unchanged. Any other input is drift_adopt_selection_invalid; no other exception escapes Parse.
internal static class MirrorDriftAdoptSelectionFile
{
    public static MirrorDriftSelection Parse(string text)
    {
        MirrorDriftSelection selection;
        try
        {
            using var document = JsonDocument.Parse(text);
            selection = Selection(document.RootElement);
        }
        catch (Exception unexpected)
        {
            throw Invalid("not a selection document (" + unexpected.GetType().Name + ")");
        }
        string canonical;
        try { canonical = selection.ToJson(); }
        catch (Exception unexpected) { throw Invalid("not a selection document (" + unexpected.GetType().Name + ")"); }
        if (!string.Equals(canonical, text, StringComparison.Ordinal)) throw Invalid("not the canonical selection JSON that mirror-drift-select writes");
        return selection;
    }

    static MirrorDriftAdoptException Invalid(string detail) => new(MirrorDriftAdoptCodes.SelectionInvalid, detail);

    static MirrorDriftSelection Selection(JsonElement root)
    {
        RequireObject(root);
        return new MirrorDriftSelection(
            Text(root, "document_unique_id"),
            Id(root, "core_project_id"),
            root.GetProperty("core_revision").GetInt32(),
            root.GetProperty("evidence_incomplete").GetBoolean(),
            Items(root, "choices", item => new MirrorDriftChoice(Text(item, "proposal_id"), Text(item, "action"))),
            Items(root, "omitted", Text),
            Items(root, "operations", item => new Operation(Text(item, "action"), ReadElement(item.GetProperty("element")))),
            Items(root, "map_adds", Entry),
            Items(root, "map_removals", Entry),
            Plan(root.GetProperty("restore_plan")),
            Items(root, "invalidated_map", item => new MirrorDriftMapInvalidation(Text(item, "key"), Text(item, "unique_id"), Text(item, "proposal_id"), Text(item, "mode"), Text(item, "by_operation"))));
    }

    static Element ReadElement(JsonElement item)
    {
        RequireObject(item);
        var provenance = item.GetProperty("provenance");
        RequireObject(provenance);
        return new Element(Id(item, "id"), Text(item, "kind"), Items(item, "geometry_metres", value => value.GetDouble()).ToArray(),
            Items(item, "dependencies", value => Guid.ParseExact(Text(value), "D")).ToArray(), new Provenance(Text(provenance, "source"), Text(provenance, "approval")));
    }

    static MirrorDriftMapEntry Entry(JsonElement item) => new(Text(item, "key"), Text(item, "unique_id"), Text(item, "proposal_id"));

    static RevitCommandPlan Plan(JsonElement plan)
    {
        RequireObject(plan);
        var document = plan.GetProperty("document");
        RequireObject(document);
        return new RevitCommandPlan(
            plan.GetProperty("bridge_version").GetInt32(),
            Id(plan, "project_id"),
            plan.GetProperty("expected_revision").GetInt32(),
            Text(plan, "after_hash"),
            new RevitDocumentRef(Text(document, "path"), Text(document, "unique_id")),
            Items(plan, "preconditions", item => new RevitLevelPrecondition(Id(item, "element_id"), NullableText(item, "action"), Text(item, "level"),
                item.GetProperty("elevation_mm") is { ValueKind: JsonValueKind.Null } ? (double?)null : item.GetProperty("elevation_mm").GetDouble(), NullableText(item, "check_operation"))),
            Items(plan, "steps", item =>
            {
                RequireObject(item);
                var args = item.GetProperty("args");
                if (args.ValueKind != JsonValueKind.Object) throw new FormatException("args");
                return new RevitCommandStep(Text(item, "operation_id"), Text(item, "command"),
                    item.GetProperty("element_id") is { ValueKind: JsonValueKind.Null } ? (Guid?)null : Id(item, "element_id"), NullableText(item, "element_kind"),
                    Items(item, "depends_on", Text), args.GetRawText(),
                    Items(item, "inputs", input => new RevitStepInput(Text(input, "field"), Text(input, "from_operation"), Text(input, "select"))),
                    Items(item, "unmapped", Text));
            }),
            Items(plan, "unsupported", item => new RevitUnsupportedElement(Id(item, "element_id"), Text(item, "element_kind"), Text(item, "action"), Text(item, "code"), Text(item, "detail"))));
    }

    static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("object expected");
    }

    static T[] Items<T>(JsonElement parent, string name, Func<JsonElement, T> read)
    {
        RequireObject(parent);
        var array = parent.GetProperty(name);
        if (array.ValueKind != JsonValueKind.Array) throw new FormatException(name);
        return array.EnumerateArray().Select(read).ToArray();
    }

    static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("string expected");

    static string Text(JsonElement parent, string name)
    {
        RequireObject(parent);
        return Text(parent.GetProperty(name));
    }

    static string? NullableText(JsonElement parent, string name)
    {
        RequireObject(parent);
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : Text(value);
    }

    static Guid Id(JsonElement parent, string name) => Guid.ParseExact(Text(parent, name), "D");
}
