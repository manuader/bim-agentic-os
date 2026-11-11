#nullable enable
using System.Text.Json;
using Bim.Core;
using Bim.Mcp;

namespace Bim.Cli;

// P2-13 `mirror-drift-adopt store-directory selection.json`: commits the adopt operations of a P2-10 selection file
// (what mirror-drift-select writes: the canonical MirrorDriftSelection.ToJson()) into the Core store through the P2-12
// MirrorDriftAdoptCommit.Commit, which owns every store rule (stale revision and hash, project, replay by key, lock,
// validation; nothing written on refusal except drift_adopt_commit_incomplete).
//
// Selection file: read through BoundedInput (8 MiB, regular file, no link; detail "selection"; an empty path is
// input_path_invalid), decoded as strict UTF-8 (an optional UTF-8 BOM is dropped; UTF-16/UTF-32 or invalid UTF-8 is
// drift_adopt_selection_invalid) and parsed strictly by MirrorDriftAdoptSelectionFile.Parse: it must be exactly the
// canonical JSON of the selection it describes (same members, order, values and number forms; parse then ToJson gives
// the same text), else drift_adopt_selection_invalid. So the commit key, which hashes the canonical selection, hashes
// the file's own text. Only when the selection has adopt operations is the store used: a blank store path (empty or
// whitespace) is then input_path_invalid (detail "store") and the store's project.json is checked through BoundedInput
// first (detail "store"). A restore-only or empty selection never opens the store, whatever its path (as Commit), and
// prints nothing_to_adopt.
// Success prints {"status":"succeeded","outcome":"committed"|"replayed"|"nothing_to_adopt","revision":n,"key":k|null}
// and exits 0. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope and exits 1. A typed
// drift_adopt_* refusal other than drift_adopt_commit_incomplete leaves the store unchanged (MirrorDriftAdoptCommit's
// contract). An unexpected exception is drift_adopt_internal_error with only its type name as detail (as MirrorDriftTool);
// one raised by the commit gives NO nothing-written guarantee (it may come after ProjectStore.Commit started writing).
// The restore plan is not run and the map changes are not written (P2-12 scope); no provenance (P2-11).
internal static class MirrorDriftAdoptCommand
{
    public const string Usage = "usage: mirror-drift-adopt store-directory selection.json";
    static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

    public static void Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException(Usage);
        var store = args[1];
        if (!ReadSelection(args[2], out var text)) return;
        MirrorDriftSelection selection;
        try { selection = MirrorDriftAdoptSelectionFile.Parse(text); }
        catch (MirrorDriftAdoptException refusal) { Fail(MirrorDriftTool.Failure(refusal.Code, Detail(refusal))); return; }
        catch (Exception unexpected) { Fail(MirrorDriftTool.Failure(MirrorDriftAdoptCodes.SelectionInvalid, unexpected.GetType().Name)); return; }
        MirrorDriftAdoptResult result;
        try
        {
            if (selection.Operations.Count == 0)
            {
                // Nothing to commit: the store is never used. Key runs the same selection checks Commit does first.
                MirrorDriftAdoptCommit.Key(selection);
                result = new(MirrorDriftAdoptOutcomes.NothingToAdopt, selection.CoreRevision, null, null, null);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(store) || store.IndexOf('\0') >= 0) { Fail(MirrorDriftTool.Failure(MirrorDriftCommand.InputPathInvalid, "store")); return; }
                try { BoundedInput.ReadText(Path.Combine(store, "project.json"), FileShare.ReadWrite | FileShare.Delete); }
                catch (InputRejectedException rejected) { Fail(MirrorDriftTool.Failure(rejected.Message, "store")); return; }
                result = MirrorDriftAdoptCommit.Commit(store, selection);
            }
        }
        catch (MirrorDriftAdoptException refusal) { Fail(MirrorDriftTool.Failure(refusal.Code, Detail(refusal))); return; }
        catch (Exception unexpected) { Fail(MirrorDriftTool.Failure(MirrorDriftAdoptCodes.InternalError, unexpected.GetType().Name)); return; }
        Console.WriteLine(JsonSerializer.Serialize(new { status = "succeeded", outcome = result.Outcome, revision = result.Revision, key = result.Key }));
    }

    static string? Detail(MirrorDriftAdoptException refusal) => refusal.Message.Length > refusal.Code.Length + 1 ? refusal.Message[(refusal.Code.Length + 1)..] : null;

    // Bounded bytes, then strict UTF-8 (BoundedInput.ReadText would also accept UTF-16/UTF-32 with a BOM).
    static bool ReadSelection(string path, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) { Fail(MirrorDriftTool.Failure(MirrorDriftCommand.InputPathInvalid, "selection")); return false; }
        byte[] bytes;
        try { bytes = BoundedInput.ReadBytes(path, BoundedInput.MaxBytes); }
        catch (InputRejectedException rejected) { Fail(MirrorDriftTool.Failure(rejected.Message, "selection")); return false; }
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { text = StrictUtf8.GetString(bytes, start, bytes.Length - start); return true; }
        catch (System.Text.DecoderFallbackException) { Fail(MirrorDriftTool.Failure(MirrorDriftAdoptCodes.SelectionInvalid, "not UTF-8 (only UTF-8, with or without its BOM, is a selection file)")); return false; }
    }

    static void Fail(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
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
