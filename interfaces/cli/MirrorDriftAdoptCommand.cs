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
// input_path_invalid) and parsed strictly by MirrorDriftAdoptSelectionFile.Parse: it must be exactly the canonical JSON
// of the selection it describes (same members, order, values and number forms; parse then ToJson gives the same text,
// a UTF-8 BOM aside), else drift_adopt_selection_invalid. So the commit key, which hashes the canonical selection,
// hashes the file's own text. The store's project.json is checked through BoundedInput first (detail "store") only when
// the selection has adopt operations: a restore-only or empty selection never opens the store (as Commit).
// Success prints {"status":"succeeded","outcome":"committed"|"replayed"|"nothing_to_adopt","revision":n,"key":k|null}
// and exits 0. Every refusal prints one {"status":"failed","code"[,"detail"]} envelope and exits 1. The restore plan
// is not run and the map changes are not written (P2-12 scope); no provenance (P2-11).
internal static class MirrorDriftAdoptCommand
{
    public const string Usage = "usage: mirror-drift-adopt store-directory selection.json";

    public static void Run(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException(Usage);
        var store = args[1];
        if (string.IsNullOrEmpty(store) || store.IndexOf('\0') >= 0) { Fail(MirrorDriftTool.Failure(MirrorDriftCommand.InputPathInvalid, "store")); return; }
        if (!Read(args[2], "selection", FileShare.Read, out var text)) return;
        MirrorDriftSelection selection;
        try { selection = MirrorDriftAdoptSelectionFile.Parse(text); }
        catch (MirrorDriftAdoptException e) { Fail(MirrorDriftTool.Failure(e.Code, Detail(e))); return; }
        if (selection.Operations.Count > 0 && !Read(Path.Combine(store, "project.json"), "store", FileShare.ReadWrite | FileShare.Delete, out _)) return;
        MirrorDriftAdoptResult result;
        try { result = MirrorDriftAdoptCommit.Commit(store, selection); }
        catch (MirrorDriftAdoptException e) { Fail(MirrorDriftTool.Failure(e.Code, Detail(e))); return; }
        catch (Exception e) { Fail(MirrorDriftTool.Failure(MirrorDriftAdoptCodes.InternalError, e.GetType().Name + ": " + e.Message)); return; }
        Console.WriteLine(JsonSerializer.Serialize(new { status = "succeeded", outcome = result.Outcome, revision = result.Revision, key = result.Key }));
    }

    static string? Detail(MirrorDriftAdoptException e) => e.Message.Length > e.Code.Length + 1 ? e.Message[(e.Code.Length + 1)..] : null;

    static bool Read(string path, string name, FileShare share, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) { Fail(MirrorDriftTool.Failure(MirrorDriftCommand.InputPathInvalid, name)); return false; }
        try { text = BoundedInput.ReadText(path, share); return true; }
        catch (InputRejectedException e) { Fail(MirrorDriftTool.Failure(e.Message, name)); return false; }
    }

    static void Fail(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}

// The strict reader of a P2-10 selection file: the inverse of MirrorDriftSelection.ToJson(), accepting only text that
// ToJson gives back unchanged. Any other input is drift_adopt_selection_invalid.
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
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            throw Invalid("not a selection document (" + e.GetType().Name + ")");
        }
        string canonical;
        try { canonical = selection.ToJson(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException) { throw Invalid("not a selection document (" + e.GetType().Name + ")"); }
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
