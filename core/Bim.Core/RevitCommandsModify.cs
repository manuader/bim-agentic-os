using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P1-05 modify and delete commands: element.set_parameter and element.delete. Same envelope (protocol_version 1),
// schema subset, copies-only guard and operation_id receipts as the earlier catalogue parts, which are unchanged.
// Both take UniqueId targets and an optional dry_run: the add-in then runs the same transaction, reads back and
// reports the change, and rolls the transaction group back (nothing written, no receipt stored). Vendor-free like
// every RevitCommands*.cs file; the add-in executor (CommandsExecutorModify.cs) consumes these types.

// Which parameter of the target element: its definition name (exactly one match on the element), a Revit
// BuiltInParameter name, or a shared parameter GUID.
public abstract record RevitParameterRef;
public sealed record ParameterByName(string Name) : RevitParameterRef;
public sealed record ParameterByBuiltIn(string BuiltIn) : RevitParameterRef;
public sealed record ParameterByGuid(Guid Guid) : RevitParameterRef;

// A typed parameter value. Lengths cross the boundary in millimetres, areas in square metres, angles in degrees.
public static class RevitParameterValueKinds
{
    public const string Length = "length";
    public const string Area = "area";
    public const string Angle = "angle";
    public const string Text = "text";
    public const string Integer = "integer";
    public const string YesNo = "yes_no";
    public const string Element = "element";
    public static IReadOnlyList<string> All { get; } = new[] { Length, Area, Angle, Text, Integer, YesNo, Element };
}

public abstract record RevitParameterValue
{
    public abstract string Kind { get; }
}
public sealed record LengthValueArg(Length Value) : RevitParameterValue { public override string Kind => RevitParameterValueKinds.Length; }
public sealed record AreaValueArg(Area Value) : RevitParameterValue { public override string Kind => RevitParameterValueKinds.Area; }
public sealed record AngleValueArg(Angle Value) : RevitParameterValue { public override string Kind => RevitParameterValueKinds.Angle; }
public sealed record TextValueArg(string Value) : RevitParameterValue { public override string Kind => RevitParameterValueKinds.Text; }
public sealed record IntegerValueArg(int Value) : RevitParameterValue { public override string Kind => RevitParameterValueKinds.Integer; }
public sealed record YesNoValueArg(bool Value) : RevitParameterValue { public override string Kind => RevitParameterValueKinds.YesNo; }
// An element id parameter set to the element with this UniqueId.
public sealed record ElementValueArg(string UniqueId) : RevitParameterValue { public override string Kind => RevitParameterValueKinds.Element; }

// Sets one parameter of one element (instance or type, by its own UniqueId). ConfirmDeletions: the UniqueIds of the
// elements Revit deletes because of the change (for example an internal join helper), as a dry run reported them; a
// real change that deletes anything is refused unless this set equals what Revit would delete.
public sealed record ElementSetParameterArgs(string ElementUid, RevitParameterRef Parameter, RevitParameterValue Value, bool DryRun, IReadOnlyList<string> ConfirmDeletions) : RevitCommandArgs;
// Deletes the target elements. ConfirmDependents: the UniqueIds of every other element Revit deletes with them, as a
// dry run reported; a real delete is refused unless this set equals what Revit would delete.
public sealed record ElementDeleteArgs(IReadOnlyList<string> ElementUids, IReadOnlyList<string> ConfirmDependents, bool DryRun) : RevitCommandArgs;

public static class RevitModifyRules
{
    public const int MaxDeleteTargets = 100;
    public const int MaxConfirmedDependents = 1000;
    public const int MaxTextLength = 4096;
    // Revit areas beyond (30000 ft)^2 are meaningless; anything above 1e8 m2 is treated as a units mistake.
    public const double MaxAreaSquareMetres = 1e8;
    // Angle parameters (rotations, slopes) are within one turn either way; more is treated as a units mistake.
    public const double MaxAngleDegrees = 360;
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> ModifySpecs = new(BuildModify);

    public static IReadOnlyList<RevitCommandSpec> Modify => ModifySpecs.Value;

    // Commands that change or remove existing elements (MCP destructiveHint): the modify commands, wall.set_profile
    // (RevitCommandsWallProfile.cs), material.set / element.change_type (RevitCommandsMaterialTypes.cs) and
    // document.save, which overwrites the saved file (RevitCommandsExportSave.cs), and command.batch, whose steps may be
    // any of these (RevitCommandsBatch.cs), and element.move (RevitCommandsTransform.cs); every other write only adds elements.
    public static bool IsDestructive(string? command) => command is not null && (Modify.Any(spec => spec.Name == command) || WallProfile.Any(spec => spec.Name == command)
        || (MaterialTypeDestructive(command) && MaterialTypes.Any(spec => spec.Name == command))
        || (ExportSaveDestructive(command) && ExportSave.Any(spec => spec.Name == command))
        || command == RevitCommandBatch.Command || command == RevitTransformRules.Move);

    const string DryRunSchema = """{"type":"boolean","description":"Run the change in a transaction, read it back, report it and roll it back; nothing is written and no receipt is stored; default false"}""";

    static IReadOnlyList<RevitCommandSpec> BuildModify()
    {
        var parameter = Fill("""{"description":"Which parameter of the element: by name (must match exactly one parameter of the element), by Revit BuiltInParameter name, or by shared parameter GUID","oneOf":[{"type":"object","properties":{"name":@0@},"required":["name"],"additionalProperties":false},{"type":"object","properties":{"built_in":{"type":"string","description":"Revit BuiltInParameter name, for example ALL_MODEL_INSTANCE_COMMENTS","minLength":1,"maxLength":128,"pattern":"^[A-Z][A-Z0-9_]*$"}},"required":["built_in"],"additionalProperties":false},{"type":"object","properties":{"guid":{"type":"string","description":"Shared parameter GUID","minLength":36,"maxLength":36,"pattern":"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"}},"required":["guid"],"additionalProperties":false}]}""",
            Text("Parameter name as shown in Revit's Properties palette", 256));

        static string Branch(string kind, string member, string valueSchema, string description) =>
            Fill("""{"type":"object","description":"@0@","properties":{"kind":{"type":"string","enum":["@1@"]},"@2@":@3@},"required":["kind","@2@"],"additionalProperties":false}""", description, kind, member, valueSchema);
        var value = Fill("""{"description":"Typed value; its kind must match the parameter's storage and data type","oneOf":[@0@,@1@,@2@,@3@,@4@,@5@,@6@]}""",
            Branch(RevitParameterValueKinds.Length, "mm", """{"type":"number","description":"Millimetres; may be negative; at most 9144000 either way (Revit limit)"}""", "A length parameter, in millimetres"),
            Branch(RevitParameterValueKinds.Area, "m2", """{"type":"number","description":"Square metres, 0 or more, at most 1e8"}""", "An area parameter, in square metres"),
            Branch(RevitParameterValueKinds.Angle, "deg", """{"type":"number","description":"Degrees, from -360 to 360"}""", "An angle parameter, in degrees"),
            Branch(RevitParameterValueKinds.Text, "text", """{"type":"string","description":"Text, up to 4096 characters, no NUL; empty clears the text","maxLength":4096,"pattern":"^[^\\u0000]*$"}""", "A text parameter"),
            Branch(RevitParameterValueKinds.Integer, "integer", """{"type":"integer","description":"A 32-bit integer"}""", "An integer parameter (not an enumeration)"),
            Branch(RevitParameterValueKinds.YesNo, "yes_no", """{"type":"boolean","description":"Yes (true) or No (false)"}""", "A Yes/No parameter"),
            Branch(RevitParameterValueKinds.Element, "uid", UniqueIdSchema, "An element id parameter, set to the element with this UniqueId"));

        var setParameter = Fill("""{"type":"object","properties":{"element_uid":@0@,"parameter":@1@,"value":@2@,"confirm_deletions":{"type":"array","description":"UniqueIds of the elements Revit deletes because of the change (for example an internal join helper), exactly as a dry run reported them in deleted; a change whose deletions differ is refused; default none","items":@0@,"maxItems":@4@},"dry_run":@3@},"required":["element_uid","parameter","value"],"additionalProperties":false}""",
            UniqueIdSchema, parameter, value, DryRunSchema, RevitModifyRules.MaxConfirmedDependents.ToString(CultureInfo.InvariantCulture));

        var delete = Fill("""{"type":"object","properties":{"element_uids":{"type":"array","description":"Elements to delete, by UniqueId, distinct, 1 to 100","items":@0@,"minItems":1,"maxItems":@1@},"confirm_dependents":{"type":"array","description":"UniqueIds of every other element Revit deletes with the targets, exactly as a dry run reported them; a delete whose dependents differ is refused; default none","items":@0@,"maxItems":@2@},"dry_run":@3@},"required":["element_uids"],"additionalProperties":false}""",
            UniqueIdSchema, RevitModifyRules.MaxDeleteTargets.ToString(CultureInfo.InvariantCulture), RevitModifyRules.MaxConfirmedDependents.ToString(CultureInfo.InvariantCulture), DryRunSchema);

        return new[]
        {
            new RevitCommandSpec("element.set_parameter", true, "Set one writable parameter of an element (by UniqueId) to a typed value: length in millimetres, area in square metres, angle in degrees, text, integer, yes/no or element; read-only parameters are refused; the before and after values are read back; dry_run reports the change, including any element Revit deletes because of it, and rolls it back; a change that deletes elements is refused unless confirm_deletions names exactly those UniqueIds. Allowed only on enabled test copies.", setParameter, BindSetParameter),
            new RevitCommandSpec("element.delete", true, "Delete elements by UniqueId; every other element Revit would delete with them is reported by a dry run and must be confirmed in confirm_dependents, else the delete is refused; pinned, grouped and protected elements are refused. Allowed only on enabled test copies.", delete, BindDelete),
        };
    }

    static bool DryRun(JsonElement args) => args.TryGetProperty("dry_run", out var dryRun) && dryRun.GetBoolean();

    static RevitCommandArgs BindSetParameter(JsonElement args)
    {
        var parameterElement = args.GetProperty("parameter");
        RevitParameterRef parameter = parameterElement.TryGetProperty("name", out var name) ? new ParameterByName(name.GetString()!)
            : parameterElement.TryGetProperty("built_in", out var builtIn) ? new ParameterByBuiltIn(builtIn.GetString()!)
            : new ParameterByGuid(Guid.ParseExact(parameterElement.GetProperty("guid").GetString()!, "D"));
        var elementUid = args.GetProperty("element_uid").GetString()!;
        var deletions = args.TryGetProperty("confirm_deletions", out var confirm)
            ? DistinctUniqueIds(confirm, "args.confirm_deletions", new[] { elementUid }, "is the element being changed, not a deletion")
            : Array.Empty<string>();
        return new ElementSetParameterArgs(elementUid, parameter, BindValue(args.GetProperty("value")), DryRun(args), deletions);
    }

    static RevitParameterValue BindValue(JsonElement value)
    {
        switch (value.GetProperty("kind").GetString())
        {
            case RevitParameterValueKinds.Length:
                var length = RevitUnits.Millimetres(Number(value.GetProperty("mm")), "args.value.mm", allowNegative: true, allowZero: true);
                if (Math.Abs(length.Millimetres) > RevitCommandGeometry.MaxRevitLengthMm)
                    throw new RevitCommandException(RevitCommandCodes.LengthInvalid, "args.value.mm", "length beyond 9144000 mm (Revit's 30000 ft limit)");
                return new LengthValueArg(length);
            case RevitParameterValueKinds.Area:
                var area = Number(value.GetProperty("m2"));
                if (!double.IsFinite(area) || area < 0 || area > RevitModifyRules.MaxAreaSquareMetres)
                    throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.value.m2", "finite square metres from 0 to 1e8 required");
                return new AreaValueArg(Area.FromSquareMetres(area));
            case RevitParameterValueKinds.Angle:
                var degrees = Number(value.GetProperty("deg"));
                var angle = RevitUnits.Degrees(degrees, "args.value.deg");
                if (Math.Abs(degrees) > RevitModifyRules.MaxAngleDegrees) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, "args.value.deg", "angle beyond 360 degrees either way");
                return new AngleValueArg(angle);
            case RevitParameterValueKinds.Text:
                return new TextValueArg(value.GetProperty("text").GetString()!);
            case RevitParameterValueKinds.Integer:
                if (!value.GetProperty("integer").TryGetInt32(out var integer))
                    throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.value.integer", "a 32-bit integer required");
                return new IntegerValueArg(integer);
            case RevitParameterValueKinds.YesNo:
                return new YesNoValueArg(value.GetProperty("yes_no").GetBoolean());
            default:
                return new ElementValueArg(value.GetProperty("uid").GetString()!);
        }
    }

    static RevitCommandArgs BindDelete(JsonElement args)
    {
        var targets = DistinctUniqueIds(args.GetProperty("element_uids"), "args.element_uids", null);
        var confirmed = args.TryGetProperty("confirm_dependents", out var dependents) ? DistinctUniqueIds(dependents, "args.confirm_dependents", targets) : Array.Empty<string>();
        return new ElementDeleteArgs(targets, confirmed, DryRun(args));
    }

    // UniqueIds compare hex case-insensitively, as Document.GetElement(string) does.
    static string[] DistinctUniqueIds(JsonElement array, string field, IReadOnlyList<string>? targets, string targetDetail = "is a delete target, not a dependent")
    {
        var values = new List<string>();
        var index = 0;
        foreach (var uid in array.EnumerateArray().Select(item => item.GetString()!))
        {
            if (values.Contains(uid, StringComparer.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + index + "]", uid + " is listed twice");
            if (targets is not null && targets.Contains(uid, StringComparer.OrdinalIgnoreCase)) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, field + "[" + index + "]", uid + " " + targetDetail);
            values.Add(uid);
            index++;
        }
        return values.ToArray();
    }
}
