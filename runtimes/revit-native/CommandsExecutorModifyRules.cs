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
public sealed record DeleteTargetFacts(string UniqueId, bool CanDelete, bool Pinned, bool InGroup, bool ProjectInformation, bool ReceiptStore, bool ActiveView);
// What a delete removes besides its targets: the dependents' UniqueIds (sorted and distinct, hex case ignored) and how many deleted ids
// had no element to name them.
public sealed record DeleteCascade(IReadOnlyList<string> Dependents, int Unidentified);

public static class CommandRulesModify
{
    public const double LengthToleranceMm = 1e-3;
    public const double AreaToleranceSquareMetres = 1e-6;
    public const double AngleToleranceDegrees = 1e-6;
    // Refusal details name at most this many UniqueIds, then the count of the rest.
    public const int MaxListedInDetail = 20;

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

    public static void CheckDeletable(DeleteTargetFacts target, string field)
    {
        string? reason = target.ProjectInformation ? "is the document's Project Information"
            : target.ReceiptStore ? "is the command receipt store"
            : target.ActiveView ? "is the active view"
            : target.Pinned ? "is pinned; unpin it in Revit first"
            : target.InGroup ? "is a member of a group; edit or ungroup the group in Revit first"
            : !target.CanDelete ? "cannot be deleted (DocumentValidation.CanDeleteElement)"
            : null;
        if (reason is not null) throw ArgsInvalid(field, "element " + target.UniqueId + " " + reason);
    }

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

    // A cascade must be fully named and must not reach a protected element (ProjectInformation, the receipt store).
    public static void CheckCascade(DeleteCascade cascade, IReadOnlyList<string> protectedUids)
    {
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
    public static string ParameterJson(bool dryRun, string elementUid, string kind, ObservedParameter before, ObservedParameter after, bool changed) => Wrapped("set_parameter", writer =>
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

    // {"delete": {dry_run, targets, dependents, deleted_count, confirmed}}: confirmed is whether confirm_dependents
    // named exactly the dependents.
    public static string DeleteJson(bool dryRun, IReadOnlyList<string> targets, IReadOnlyList<string> dependents, bool confirmed) => Wrapped("delete", writer =>
    {
        writer.WriteBoolean("dry_run", dryRun);
        Strings(writer, "targets", targets);
        Strings(writer, "dependents", dependents);
        writer.WriteNumber("deleted_count", targets.Count + dependents.Count);
        writer.WriteBoolean("confirmed", confirmed);
    });

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
