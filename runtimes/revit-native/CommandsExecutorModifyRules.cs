using Bim.Core;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// P1-05 element.set_parameter and element.delete executor decisions, readback checks and the change report JSON.
// Autodesk-free on purpose: tests/revit-commands source-links this file. CommandsExecutorModify.cs reads the Revit
// facts (parameter values through CommandExecutor.ObserveParameter: lengths in mm, areas in m2, angles in degrees)
// and calls the Revit API.

// The parameter to set as Revit reports it, plus whether its data type is Yes/No (SpecTypeId.Boolean.YesNo) or a
// plain integer (SpecTypeId.Int.Integer); other integer parameters are enumerations.
public sealed record ParameterTarget(ObservedParameter Parameter, bool YesNo, bool IntegerSpec);
// One delete target: DocumentValidation.CanDeleteElement, Element.Pinned, a group member (GroupId valid), the
// document's ProjectInformation, the command receipt DataStorage, or the document's active view.
// OwnerUid (dependents only): the element that owns this one as a sub-element Revit deletes with it (a curtain grid
// line, panel or mullion of a curtain wall or roof, a sketch of its owner, a sketch's plane or lines, an internal element
// without a category), null for an independent element.
// GridMember (dependents only): a curtain grid line, panel or mullion of a curtain wall or roof (OwnerUid is that host).
public sealed record DeleteTargetFacts(string UniqueId, bool CanDelete, bool Pinned, bool InGroup, bool ProjectInformation, bool ReceiptStore, bool ActiveView, string? OwnerUid = null, bool GridMember = false);
// What a delete removes besides its targets: the dependents' UniqueIds (sorted and distinct, hex case ignored) and how many deleted ids
// had no element to name them.
public sealed record DeleteCascade(IReadOnlyList<string> Dependents, int Unidentified);
// An element a set_parameter change deletes, named after the rollback restored it: UniqueId null when it could not be
// read, Category null for internal elements without one (join or warning helpers), ClassName its Revit class.
public sealed record DeletedElement(string? UniqueId, long Id, string? Category, string ClassName);

public static class CommandRulesModify
{
    public const double LengthToleranceMm = 1e-3;
    public const double AreaToleranceSquareMetres = 1e-6;
    public const double AngleToleranceDegrees = 1e-6;
    // Refusal details name at most this many UniqueIds, then the count of the rest.
    public const int MaxListedInDetail = 20;
    // Replies and receipts list at most this many dependents or other modified elements; beyond it they carry the
    // count (and for dependents a SHA-256), so a large cascade cannot fill the receipt store.
    public const int MaxDependentsListed = 100;

    static RevitCommandException ArgsInvalid(string field, string detail) => new(RevitCommandCodes.ArgsInvalid, field, detail);

    // The value kind a parameter takes, or null when this command does not set it: integers that are neither Yes/No
    // nor plain integers (enumerations), doubles that are not lengths, areas or angles, and parameters without storage.
    public static string? KindOf(ParameterTarget target)
    {
        var parameter = target.Parameter;
        return parameter.StorageType switch
        {
            RevitParameterStorage.String => RevitParameterValueKinds.Text,
            RevitParameterStorage.ElementId => RevitParameterValueKinds.Element,
            RevitParameterStorage.Integer => target.YesNo ? RevitParameterValueKinds.YesNo : target.IntegerSpec ? RevitParameterValueKinds.Integer : null,
            RevitParameterStorage.Double => RevitParameterUnits.ForSpec(parameter.Spec) switch
            {
                RevitParameterUnits.Millimetres => RevitParameterValueKinds.Length,
                RevitParameterUnits.SquareMetres => RevitParameterValueKinds.Area,
                RevitParameterUnits.Degrees => RevitParameterValueKinds.Angle,
                _ => null,
            },
            _ => null,
        };
    }

    // Refuses read-only parameters, parameters this command cannot set and a value of the wrong kind; returns the kind.
    public static string CheckSettable(ParameterTarget target, RevitParameterValue value)
    {
        var parameter = target.Parameter;
        if (parameter.ReadOnly) throw ArgsInvalid("args.parameter", "parameter " + parameter.Name + " is read-only");
        var kind = KindOf(target) ?? throw ArgsInvalid("args.value", "parameter " + parameter.Name + " stores " + parameter.StorageType + (parameter.Spec is { } spec ? " (" + spec + ")" : "")
            + "; this command sets only " + string.Join(", ", RevitParameterValueKinds.All) + " parameters");
        if (kind != value.Kind) throw ArgsInvalid("args.value.kind", "parameter " + parameter.Name + " takes a " + kind + " value, not " + value.Kind);
        return kind;
    }

    // A parameter named by its definition name must be the only one of that name on the element.
    public static void CheckNamedMatches(int count, string name)
    {
        if (count == 0) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.parameter.name", "the element has no parameter named " + name);
        if (count > 1) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.parameter.name", count + " parameters of the element are named " + name + "; pass built_in or guid instead");
    }

    static string? Undeletable(DeleteTargetFacts element) =>
        element.ProjectInformation ? "is the document's Project Information"
        : element.ReceiptStore ? "is the command receipt store"
        : element.ActiveView ? "is the active view"
        : element.Pinned ? "is pinned; unpin it in Revit first"
        : element.InGroup ? "is a member of a group; edit or ungroup the group in Revit first"
        : !element.CanDelete ? "cannot be deleted (DocumentValidation.CanDeleteElement)"
        : null;

    public static void CheckDeletable(DeleteTargetFacts target, string field)
    {
        if (Undeletable(target) is { } reason) throw ArgsInvalid(field, "element " + target.UniqueId + " " + reason);
    }

    // Every element the cascade would remove must be deletable on its own terms: a pinned, grouped or otherwise
    // protected dependent refuses the whole delete. A sub-element whose OwnerUid chain (through other dependents) reaches
    // a target is deleted by Revit with its owner and is refused by CanDeleteElement on its own (owner, 2026-11-04: a
    // curtain wall's grid, a profiled wall's or roof's sketch), so only CanDeleteElement is waived for it.
    public static void CheckDependents(IReadOnlyList<DeleteTargetFacts> dependents, IReadOnlyCollection<string>? targets = null)
    {
        var targetSet = new HashSet<string>(targets ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependent in dependents) owners.TryAdd(dependent.UniqueId, dependent.OwnerUid);
        bool OwnedByTarget(DeleteTargetFacts dependent)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var owner = dependent.OwnerUid;
            while (owner is not null && seen.Add(owner))
            {
                if (targetSet.Contains(owner)) return true;
                owner = owners.TryGetValue(owner, out var next) ? next : null;
            }
            return false;
        }
        foreach (var dependent in dependents)
        {
            var facts = !dependent.CanDelete && OwnedByTarget(dependent) ? dependent with { CanDelete = true } : dependent;
            if (Undeletable(facts) is { } reason) throw ArgsInvalid("args.element_uids", "the delete would also remove dependent element " + dependent.UniqueId + ", which " + reason);
        }
    }

    // set_parameter: Revit refuses parameter changes on group members unless the group is edited.
    public static void CheckParameterElement(string uniqueId, bool inGroup)
    {
        if (inGroup) throw ArgsInvalid("args.element_uid", "element " + uniqueId + " is a member of a group; edit or ungroup the group in Revit first");
    }

    // A Revit ArgumentException or InvalidOperationException from Parameter.Set or Document.Delete is a refusal of the
    // request, not an executor failure.
    public static RevitCommandException RevitRefused(string? field, string message) => new(RevitCommandCodes.ArgsInvalid, field, "Revit refused: " + message);

    // Real runs store a receipt (and need room for one first); dry runs never store anything.
    public static bool StoresReceipt(bool dryRun) => !dryRun;

    // set_parameter side effects (pre/post element id snapshot and the modified ids of the transaction). accepted: the
    // ids of the deletions SetParameterDeletions allowed for this run (a dry run's report or confirm_deletions), named on
    // a first run that was rolled back. Without accepted ids any deleted element refuses the change; with them the run
    // must delete exactly those ids, else it is rolled back (the model changed between the two runs). The other modified
    // ids (not the target, not a deleted one) are returned, distinct and sorted.
    public static IReadOnlyList<long> SetParameterEffects(long targetId, IReadOnlyCollection<long> deleted, IReadOnlyCollection<long> modified, IReadOnlyCollection<long>? accepted = null)
    {
        if (accepted is not { Count: > 0 })
        {
            if (deleted.Count > 0)
                throw ArgsInvalid("args.value", "the change would delete " + deleted.Count + " element(s) (ids " + string.Join(", ", deleted.OrderBy(id => id).Take(MaxListedInDetail)) + "); refused");
        }
        else if (!new HashSet<long>(accepted).SetEquals(deleted))
        {
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.value", "the change deleted " + deleted.Distinct().Count() + " element(s) where " + accepted.Distinct().Count() + " were named and accepted (ids "
                + string.Join(", ", deleted.Distinct().OrderBy(id => id).Take(MaxListedInDetail)) + "); rolled back");
        }
        var gone = new HashSet<long>(deleted);
        return modified.Where(id => id != targetId && !gone.Contains(id)).Distinct().OrderBy(id => id).ToArray();
    }

    // The elements a set_parameter change deletes (owner, Revit 2025.2, 2026-11-04: a floor's height offset deleted an
    // internal element without a category, a join helper with the toposolid), named after the first run was rolled
    // back. Every one must have a UniqueId (transaction_failed), there may be at most MaxConfirmedDependents and none may
    // be protected (Project Information, the receipt store), dry run or not (args_invalid at args.value). A dry run then
    // reports them; a real change needs confirm_deletions to equal them as a set (hex case ignored), else args_invalid
    // at args.confirm_deletions naming the unconfirmed and stale UniqueIds, as element.delete's confirm_dependents.
    // Returns the deletions distinct by UniqueId, sorted.
    public static IReadOnlyList<DeletedElement> SetParameterDeletions(bool dryRun, IReadOnlyList<DeletedElement> deleted, IReadOnlyList<string> confirmed, IReadOnlyList<string> protectedUids)
    {
        var unnamed = deleted.Where(element => element.UniqueId is null).Select(element => element.Id).OrderBy(id => id).ToArray();
        if (unnamed.Length > 0)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.value", "the change would delete " + unnamed.Length + " element(s) that cannot be named by UniqueId (ids " + string.Join(", ", unnamed.Take(MaxListedInDetail)) + "); refused");
        var named = deleted.GroupBy(element => element.UniqueId!, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderBy(element => element.UniqueId, StringComparer.OrdinalIgnoreCase).ToArray();
        if (named.Length > RevitModifyRules.MaxConfirmedDependents)
            throw ArgsInvalid("args.value", "the change would delete " + named.Length + " elements, more than the " + RevitModifyRules.MaxConfirmedDependents + " confirm_deletions can hold; refused");
        var reached = named.Where(element => protectedUids.Contains(element.UniqueId!, StringComparer.OrdinalIgnoreCase)).Select(element => element.UniqueId!).ToArray();
        if (reached.Length > 0) throw ArgsInvalid("args.value", "the change would delete protected element " + string.Join(", ", reached) + "; refused");
        if (dryRun) return named;
        var uids = named.Select(element => element.UniqueId!).ToArray();
        if (Confirmed(uids, confirmed)) return named;
        var unconfirmed = named.Where(element => !confirmed.Contains(element.UniqueId!, StringComparer.OrdinalIgnoreCase)).ToArray();
        var stale = confirmed.Where(uid => !uids.Contains(uid, StringComparer.OrdinalIgnoreCase)).ToArray();
        var detail = new StringBuilder();
        if (unconfirmed.Length > 0)
            detail.Append("the change would delete " + unconfirmed.Length + " unconfirmed element(s): " + string.Join(", ", unconfirmed.Take(MaxListedInDetail).Select(Described))
                + (unconfirmed.Length > MaxListedInDetail ? " and " + (unconfirmed.Length - MaxListedInDetail) + " more" : ""));
        if (stale.Length > 0) detail.Append((detail.Length > 0 ? "; " : "") + stale.Length + " confirmed element(s) would not be deleted: " + Listed(stale));
        detail.Append("; run a dry run and confirm exactly its deleted elements in confirm_deletions");
        throw ArgsInvalid("args.confirm_deletions", detail.ToString());
    }

    static string Described(DeletedElement element) =>
        element.UniqueId + " (id " + element.Id + ", " + (element.Category ?? "no category") + ", " + element.ClassName + ")";

    // deletedUids: the UniqueId of every element id Revit's Document.Delete returned (null when none could be read).
    // Every target must be among them; the rest are the dependents.
    public static DeleteCascade Cascade(IReadOnlyList<string> targets, IReadOnlyList<string?> deletedUids)
    {
        var deleted = new HashSet<string>(deletedUids.Where(uid => uid is not null).Select(uid => uid!), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < targets.Count; i++)
        {
            if (!deleted.Contains(targets[i]))
                throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.element_uids[" + i + "]", "Revit would not delete element " + targets[i]);
        }
        var dependents = deleted.Where(uid => !targets.Contains(uid, StringComparer.OrdinalIgnoreCase)).OrderBy(uid => uid, StringComparer.OrdinalIgnoreCase).ToArray();
        return new DeleteCascade(dependents, deletedUids.Count(uid => uid is null));
    }

    // A cascade must be fully named, small enough to confirm (dry runs included) and must not reach a protected element
    // (ProjectInformation, the receipt store).
    public static void CheckCascade(DeleteCascade cascade, IReadOnlyList<string> protectedUids)
    {
        if (cascade.Dependents.Count > RevitModifyRules.MaxConfirmedDependents)
            throw ArgsInvalid("args.element_uids", "the delete would also remove " + cascade.Dependents.Count + " elements, more than the " + RevitModifyRules.MaxConfirmedDependents + " confirm_dependents can hold; delete fewer elements at a time");
        if (cascade.Unidentified > 0)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "args.element_uids", cascade.Unidentified + " of the elements Revit would delete cannot be named by UniqueId; refused");
        var reached = cascade.Dependents.Where(uid => protectedUids.Contains(uid, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (reached.Length > 0) throw ArgsInvalid("args.element_uids", "the delete would also remove protected element " + string.Join(", ", reached));
    }

    public static bool Confirmed(IReadOnlyList<string> dependents, IReadOnlyList<string> confirmed) =>
        new HashSet<string>(dependents, StringComparer.OrdinalIgnoreCase).SetEquals(confirmed);

    // A real delete runs only when confirm_dependents names exactly the dependents Revit would delete now.
    public static void CheckConfirmed(IReadOnlyList<string> dependents, IReadOnlyList<string> confirmed)
    {
        if (Confirmed(dependents, confirmed)) return;
        var unconfirmed = dependents.Where(uid => !confirmed.Contains(uid, StringComparer.OrdinalIgnoreCase)).ToArray();
        var stale = confirmed.Where(uid => !dependents.Contains(uid, StringComparer.OrdinalIgnoreCase)).ToArray();
        var detail = new StringBuilder();
        if (unconfirmed.Length > 0) detail.Append("Revit would also delete " + unconfirmed.Length + " unconfirmed element(s): " + Listed(unconfirmed));
        if (stale.Length > 0) detail.Append((detail.Length > 0 ? "; " : "") + stale.Length + " confirmed element(s) would not be deleted: " + Listed(stale));
        detail.Append("; run a dry run and confirm exactly its dependents");
        throw ArgsInvalid("args.confirm_dependents", detail.ToString());
    }

    static string Listed(IReadOnlyList<string> uids) =>
        string.Join(", ", uids.Take(MaxListedInDetail)) + (uids.Count > MaxListedInDetail ? " and " + (uids.Count - MaxListedInDetail) + " more" : "");

    // The delete that is kept must remove exactly the element ids its probe removed.
    public static void CheckSameCascade(IReadOnlyCollection<long> probe, IReadOnlyCollection<long> actual)
    {
        if (!new HashSet<long>(probe).SetEquals(actual))
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the delete removed " + actual.Count + " element(s) where its probe removed " + probe.Count + "; rolled back");
    }

    // The kept delete, after commit, may have removed nothing outside the probe's set (pre/post id snapshot).
    public static void CheckNoExtraDeletions(IReadOnlyCollection<long> probe, IReadOnlyCollection<long> removed)
    {
        var expected = new HashSet<long>(probe);
        var extra = removed.Where(id => !expected.Contains(id)).Distinct().OrderBy(id => id).ToArray();
        if (extra.Length > 0)
            throw new RevitCommandException(RevitCommandCodes.TransactionFailed, null, "the delete also removed " + extra.Length + " element(s) its probe did not (ids " + string.Join(", ", extra.Take(MaxListedInDetail)) + "); rolled back");
    }

    // Same stored value, within the readback tolerances for numbers.
    public static bool SameValue(string kind, ObservedParameter before, ObservedParameter after)
    {
        if (before.HasValue != after.HasValue) return kind == RevitParameterValueKinds.Text && (before.Text ?? "") == (after.Text ?? "");
        if (!before.HasValue) return true;
        return kind switch
        {
            RevitParameterValueKinds.Length => Near(before.Number, after.Number, LengthToleranceMm),
            RevitParameterValueKinds.Area => Near(before.Number, after.Number, AreaToleranceSquareMetres),
            RevitParameterValueKinds.Angle => Near(before.Number, after.Number, AngleToleranceDegrees),
            RevitParameterValueKinds.Text => (before.Text ?? "") == (after.Text ?? ""),
            RevitParameterValueKinds.Element => string.Equals(before.ElementUid, after.ElementUid, StringComparison.OrdinalIgnoreCase),
            _ => before.Integer == after.Integer,
        };
    }

    static bool Near(double? a, double? b, double tolerance) => a is { } x && b is { } y && Math.Abs(x - y) <= tolerance;
}

public static class CommandReadbackModify
{
    // Null when the parameter now holds the requested value; otherwise what differs.
    public static string? Parameter(RevitParameterValue requested, ObservedParameter after)
    {
        if (requested is TextValueArg { Value: "" } && !after.HasValue) return null;
        if (!after.HasValue) return "parameter " + after.Name + " has no value after the set";
        string? Number(double expected, string unit, double tolerance, string what) =>
            after.Unit != unit || after.Number is not { } number ? "parameter " + after.Name + " reads back without a " + unit + " value"
            : Math.Abs(number - expected) > tolerance ? "parameter " + after.Name + " reads back " + number.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " " + unit + ", requested " + expected.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " (" + what + ")"
            : null;
        return requested switch
        {
            LengthValueArg length => Number(length.Value.Millimetres, RevitParameterUnits.Millimetres, CommandRulesModify.LengthToleranceMm, "length"),
            AreaValueArg area => Number(area.Value.SquareMetres, RevitParameterUnits.SquareMetres, CommandRulesModify.AreaToleranceSquareMetres, "area"),
            AngleValueArg angle => Number(angle.Value.Degrees, RevitParameterUnits.Degrees, CommandRulesModify.AngleToleranceDegrees, "angle"),
            TextValueArg text => (after.Text ?? "") == text.Value ? null : "parameter " + after.Name + " reads back a different text",
            IntegerValueArg integer => after.Integer == integer.Value ? null : "parameter " + after.Name + " reads back " + after.Integer + ", requested " + integer.Value,
            YesNoValueArg yesNo => after.Integer == (yesNo.Value ? 1 : 0) ? null : "parameter " + after.Name + " reads back " + after.Integer + ", requested " + (yesNo.Value ? "yes" : "no"),
            ElementValueArg element => string.Equals(after.ElementUid, element.UniqueId, StringComparison.OrdinalIgnoreCase) ? null : "parameter " + after.Name + " reads back element " + (after.ElementUid ?? "none") + ", requested " + element.UniqueId,
            _ => "unsupported value kind " + requested.Kind,
        };
    }

    // Null when none of the elements that were to be deleted is still in the document.
    public static string? Deleted(IReadOnlyList<string> stillPresent) =>
        stillPresent.Count == 0 ? null : stillPresent.Count + " element(s) still exist after the delete: " + string.Join(", ", stillPresent.Take(CommandRulesModify.MaxListedInDetail));

    // {"set_parameter": {dry_run, element_unique_id, parameter: {name, id, built_in, storage, spec, shared}, kind,
    // before, after, changed}}; before/after are values in the request shape ({kind, mm|m2|deg|text|integer|yes_no|uid})
    // or null when the parameter has no value.
    // also_modified: other elements the change modified (at most MaxDependentsListed listed), also_modified_count and
    // also_modified_listed.
    public static string ParameterJson(bool dryRun, string elementUid, string kind, ObservedParameter before, ObservedParameter after, bool changed, IReadOnlyList<string> alsoModified,
        IReadOnlyList<DeletedElement>? deleted = null, bool deletionsConfirmed = false) => Wrapped("set_parameter", writer =>
    {
        writer.WriteBoolean("dry_run", dryRun);
        writer.WriteString("element_unique_id", elementUid);
        writer.WriteStartObject("parameter");
        writer.WriteString("name", after.Name);
        writer.WriteNumber("id", after.Id);
        if (after.BuiltIn is { } builtIn) writer.WriteString("built_in", builtIn); else writer.WriteNull("built_in");
        writer.WriteString("storage", after.StorageType);
        if (after.Spec is { } spec) writer.WriteString("spec", spec); else writer.WriteNull("spec");
        writer.WriteBoolean("shared", after.Shared);
        writer.WriteEndObject();
        writer.WriteString("kind", kind);
        Value(writer, "before", kind, before);
        Value(writer, "after", kind, after);
        writer.WriteBoolean("changed", changed);
        var listed = alsoModified.Count <= CommandRulesModify.MaxDependentsListed;
        Strings(writer, "also_modified", alsoModified.Take(CommandRulesModify.MaxDependentsListed));
        writer.WriteNumber("also_modified_count", alsoModified.Count);
        writer.WriteBoolean("also_modified_listed", listed);
        // deleted: the elements Revit deletes because of the change (a dry run lists them for confirm_deletions; a real
        // run only when they were confirmed), at most MaxConfirmedDependents, each {unique_id, id, category, class}; a real
        // run's report (stored as the receipt) lists them only up to MaxDependentsListed, else deleted is null.
        var gone = deleted ?? Array.Empty<DeletedElement>();
        var deletedListed = CommandReadbackModify.DependentsListed(dryRun, gone.Count);
        writer.WriteBoolean("deleted_listed", deletedListed);
        if (!deletedListed) writer.WriteNull("deleted");
        else writer.WriteStartArray("deleted");
        foreach (var element in deletedListed ? gone : Array.Empty<DeletedElement>())
        {
            writer.WriteStartObject();
            if (element.UniqueId is { } uid) writer.WriteString("unique_id", uid); else writer.WriteNull("unique_id");
            writer.WriteNumber("id", element.Id);
            if (element.Category is { } category) writer.WriteString("category", category); else writer.WriteNull("category");
            writer.WriteString("class", element.ClassName);
            writer.WriteEndObject();
        }
        if (deletedListed) writer.WriteEndArray();
        writer.WriteNumber("deleted_count", gone.Count);
        writer.WriteBoolean("deletions_confirmed", deletionsConfirmed);
    });

    static void Value(Utf8JsonWriter writer, string name, string kind, ObservedParameter observed)
    {
        if (!observed.HasValue) { writer.WriteNull(name); return; }
        writer.WriteStartObject(name);
        writer.WriteString("kind", kind);
        void Number(string member)
        {
            if (observed.Number is { } number) writer.WriteNumber(member, number); else writer.WriteNull(member);
        }
        switch (kind)
        {
            case RevitParameterValueKinds.Length: Number("mm"); break;
            case RevitParameterValueKinds.Area: Number("m2"); break;
            case RevitParameterValueKinds.Angle: Number("deg"); break;
            case RevitParameterValueKinds.Text: writer.WriteString("text", observed.Text ?? ""); break;
            case RevitParameterValueKinds.YesNo:
                if (observed.Integer is { } flag) writer.WriteBoolean("yes_no", flag != 0); else writer.WriteNull("yes_no");
                break;
            case RevitParameterValueKinds.Element:
                if (observed.ElementUid is { } uid) writer.WriteString("uid", uid); else writer.WriteNull("uid");
                break;
            default:
                if (observed.Integer is { } integer) writer.WriteNumber("integer", integer); else writer.WriteNull("integer");
                break;
        }
        writer.WriteEndObject();
    }

    // {"delete": {dry_run, targets, dependents, dependents_count, dependents_sha256, dependents_listed, deleted_count,
    // confirmed}}: a dry run lists every dependent; a real run (whose reply is stored as the receipt) lists them only up
    // to MaxDependentsListed, else dependents is null. confirmed: confirm_dependents named exactly the dependents.
    public static string DeleteJson(bool dryRun, IReadOnlyList<string> targets, IReadOnlyList<string> dependents, bool confirmed) => Wrapped("delete", writer =>
    {
        writer.WriteBoolean("dry_run", dryRun);
        Strings(writer, "targets", targets);
        var listed = DependentsListed(dryRun, dependents.Count);
        if (listed) Strings(writer, "dependents", dependents); else writer.WriteNull("dependents");
        writer.WriteNumber("dependents_count", dependents.Count);
        writer.WriteString("dependents_sha256", DependentsSha256(dependents));
        writer.WriteBoolean("dependents_listed", listed);
        writer.WriteNumber("deleted_count", targets.Count + dependents.Count);
        writer.WriteBoolean("confirmed", confirmed);
    });

    public static bool DependentsListed(bool dryRun, int count) => dryRun || count <= CommandRulesModify.MaxDependentsListed;

    // Lower-case hex SHA-256 of the lower-case UniqueIds, sorted ordinally, joined by '\n' (UTF-8).
    public static string DependentsSha256(IEnumerable<string> dependents)
    {
        var text = string.Join("\n", dependents.Select(uid => uid.ToLowerInvariant()).OrderBy(uid => uid, StringComparer.Ordinal));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    static string Wrapped(string name, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject(name);
            body(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

// A modify reply and whether it is persisted as the receipt (real runs) or only returned (dry runs, whose transaction
// group the executor rolls back).
public sealed record ModifyReply(RevitCommandResponse Response, bool Persist);

public static class CommandResponsesModify
{
    static RevitCommandReceipt Receipt(CommandRequest request) =>
        new(request.Envelope.OperationId, request.Envelope.Command, request.Envelope.Document.UniqueId, request.RequestSha256, false);

    // modified: nothing for a dry run; else the target when its value changed, then the other modified elements when
    // there are at most MaxDependentsListed of them (the report counts them all).
    public static ModifyReply SetParameter(CommandRequest request, bool dryRun, string elementUid, bool changed, IReadOnlyList<string> alsoModified, string readbackJson, IReadOnlyList<string> warnings, IReadOnlyList<string>? deleted = null)
    {
        var modified = new List<string>();
        if (!dryRun)
        {
            if (changed) modified.Add(elementUid);
            if (alsoModified.Count <= CommandRulesModify.MaxDependentsListed) modified.AddRange(alsoModified);
        }
        // deleted: the confirmed deletions of a real run, listed when there are at most MaxDependentsListed (the report
        // has them all), so a stored receipt stays small.
        var removed = !dryRun && deleted is { Count: > 0 and <= CommandRulesModify.MaxDependentsListed } ? deleted.ToArray() : Array.Empty<string>();
        return new ModifyReply(RevitCommandResponse.Success(Receipt(request), Array.Empty<string>(), modified, removed, readbackJson, warnings), CommandRulesModify.StoresReceipt(dryRun));
    }

    // deleted: nothing for a dry run; else the targets, then the dependents when they are listed in the report.
    public static ModifyReply Delete(CommandRequest request, bool dryRun, IReadOnlyList<string> targets, IReadOnlyList<string> dependents, IReadOnlyList<string> confirmed, IReadOnlyList<string> warnings)
    {
        var readback = CommandReadbackModify.DeleteJson(dryRun, targets, dependents, CommandRulesModify.Confirmed(dependents, confirmed));
        var deleted = dryRun ? Array.Empty<string>()
            : CommandReadbackModify.DependentsListed(false, dependents.Count) ? targets.Concat(dependents).ToArray()
            : targets.ToArray();
        return new ModifyReply(RevitCommandResponse.Success(Receipt(request), Array.Empty<string>(), Array.Empty<string>(), deleted, readback, warnings), CommandRulesModify.StoresReceipt(dryRun));
    }
}
