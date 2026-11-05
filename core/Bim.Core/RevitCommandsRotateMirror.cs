using System.Globalization;
using System.Text.Json;
namespace Bim.Core;

// P1-10 element.rotate and element.mirror: turn 1 to 100 existing elements (by UniqueId) about one vertical axis through
// a plan point by an angle in degrees (counter-clockwise seen from above is positive), or mirror them in one vertical
// plane through a plan line, keeping the originals (mirrored copies, Revit's Mirror with Copy) or not. Same envelope
// (protocol_version 1), schema subset, unit rules, copies-only guard and operation_id receipts as the earlier catalogue
// parts, which are unchanged. Both are command.batch steps. Vendor-free like every RevitCommands*.cs file; the add-in
// executor (CommandsExecutorRotateMirror.cs) consumes these types and refuses, with the move/copy codes
// (RevitTransformCodes: pinned, view-specific, group member, host missing) and the codes below, elements it cannot turn
// or mirror and read back.

// AngleDeg: the requested angle_deg exactly as sent (echoed in the readback); Angle is the same turn as a Core Angle.
public sealed record ElementRotateArgs(IReadOnlyList<string> ElementUids, Point2 Center, Angle Angle, double AngleDeg) : RevitCommandArgs;
public sealed record ElementMirrorArgs(IReadOnlyList<string> ElementUids, Point2 AxisStart, Point2 AxisEnd, bool KeepOriginal) : RevitCommandArgs;

public static class RevitRotateMirrorRules
{
    public const string Rotate = "element.rotate";
    public const string Mirror = "element.mirror";
    public const int MinTargets = 1;
    public const int MaxTargets = 100;
    // A smaller turn is treated as a mistake (it moves a point 10 m from the axis by under 2 mm).
    public const double MinAngleDeg = 0.01;
    // Exclusive: a full turn (or more) is no rotation, or a units mistake.
    public const double MaxAngleDeg = 360;
}

// Refusals the add-in reports for a target it will not rotate or mirror (Field is args.element_uids[i]; Detail names
// the UniqueId). Executor-produced: the client cannot see these Revit facts.
public static class RevitRotateMirrorCodes
{
    // No location point, curve or sketch profile to turn and read back, or a hosted element whose host is not a target.
    public const string ElementNotRotatable = "element_not_rotatable";
    // As not rotatable, or Revit's ElementTransformUtils.CanMirrorElements refuses it.
    public const string ElementNotMirrorable = "element_not_mirrorable";
    // The turn moves every point of the target by at most 1 mm and the target has no readable rotation: the 1 mm
    // readback could not tell the rotation from no rotation.
    public const string AngleTooSmallForTarget = "angle_too_small_for_target";
    // keep_original false and the target is already its own mirror image (on or symmetric about the line) with no
    // readable FamilyInstance.Mirrored flag: the readback could not tell the mirror from no mirror.
    public const string MirrorImageUnobservable = "mirror_image_unobservable";
    public static IReadOnlyList<string> All { get; } = new[] { ElementNotRotatable, ElementNotMirrorable, AngleTooSmallForTarget, MirrorImageUnobservable };
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> RotateMirrorSpecs = new(BuildRotateMirror);

    public static IReadOnlyList<RevitCommandSpec> RotateMirror => RotateMirrorSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildRotateMirror()
    {
        string Targets(string verb) => Fill("""{"type":"array","description":"Elements to @0@, by UniqueId (from revit_query_elements), distinct, @1@ to @2@","items":@3@,"minItems":@1@,"maxItems":@2@}""",
            verb, RevitRotateMirrorRules.MinTargets.ToString(CultureInfo.InvariantCulture), RevitRotateMirrorRules.MaxTargets.ToString(CultureInfo.InvariantCulture), UniqueIdSchema);
        var rotateSchema = Fill("""{"type":"object","properties":{"element_uids":@0@,"center":{"type":"array","description":"Plan point [x, y] in millimetres (project coordinates) the vertical rotation axis passes through","items":{"type":"number"},"minItems":2,"maxItems":2},"angle_deg":{"type":"number","description":"Rotation in degrees, counter-clockwise seen from above (from +x toward +y) when positive, clockwise when negative; at least 0.01 and under 360 either way"}},"required":["element_uids","center","angle_deg"],"additionalProperties":false}""",
            Targets("rotate"));
        var mirrorSchema = Fill("""{"type":"object","properties":{"element_uids":@0@,"axis":{"type":"object","description":"The mirror line in plan, in millimetres (at least 1.2 mm long): elements are mirrored in the vertical plane through it","properties":{"start":@1@,"end":@1@},"required":["start","end"],"additionalProperties":false},"keep_original":{"type":"boolean","description":"true keeps the originals and adds mirrored copies (Revit's Mirror with Copy checked); false mirrors the originals themselves"}},"required":["element_uids","axis","keep_original"],"additionalProperties":false}""",
            Targets("mirror"), Point);
        const string Refusals = "pinned (element_pinned), view-specific and group member elements and hosted elements whose host is missing are refused with typed codes";
        return new[]
        {
            new RevitCommandSpec(RevitRotateMirrorRules.Rotate, true,
                "Rotate 1 to 100 elements (by UniqueId) about one vertical axis through a plan point [x, y] in millimetres by angle_deg degrees, counter-clockwise seen from above when positive, like Revit's Rotate; "
                + "each element's location point (and its rotation), location curve or sketch profile is read back and must sit where the rotation puts it within 1 mm, else everything is rolled back. "
                + "Hosted elements (doors, windows) turn only with their host: include the host. Example: turn a wall 90 degrees about its start (0, 0) is element_uids [wall], center [0, 0], angle_deg 90. "
                + "The " + Refusals + "; elements without a location point, curve or sketch profile, and hosted elements without their host, are element_not_rotatable; "
                + "an angle too small to move any point of a target by more than 1 mm (for a target without a readable rotation) is angle_too_small_for_target. Allowed only on enabled test copies.",
                rotateSchema, BindRotate),
            new RevitCommandSpec(RevitRotateMirrorRules.Mirror, true,
                "Mirror 1 to 100 elements (by UniqueId) in the vertical plane through a plan line axis.start -> axis.end in millimetres, like Revit's Mirror - Draw Axis; with keep_original true the originals stay and the reply maps each to its mirrored copy, "
                + "with keep_original false the originals themselves are mirrored (a replacement Revit makes is mapped to its original); each result must sit at the mirror image of its original within 1 mm (curves either way round, sketch profiles in any order), else everything is rolled back. "
                + "Example: a mirrored wing across the line x = 5000 is element_uids [walls], axis {start: [5000, 0], end: [5000, 1000]}, keep_original true. "
                + "The " + Refusals + " (pinned elements are refused even with keep_original true: unpin them first); elements Revit cannot mirror (asked once for the whole set), elements without a location point, curve or sketch profile, and hosted elements without their host are element_not_mirrorable; "
                + "with keep_original false a target that is already its own mirror image (on or symmetric about the line) and has no readable mirrored flag is mirror_image_unobservable. "
                + "Originals Revit replaces are listed as deleted. Allowed only on enabled test copies.",
                mirrorSchema, BindMirror),
        };
    }

    // Distinct UniqueIds (case-insensitive, as Document.GetElement(string)), a plan centre and a turn from 0.01 to under
    // 360 degrees either way.
    static RevitCommandArgs BindRotate(JsonElement args)
    {
        var uids = DistinctUniqueIds(args.GetProperty("element_uids"), "args.element_uids", null);
        var center = Coordinates(args, "center", "args.center");
        const string field = "args.angle_deg";
        var degrees = Number(args.GetProperty("angle_deg"));
        var angle = RevitUnits.Degrees(degrees, field);
        if (degrees == 0) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, field, "zero angle: nothing to rotate");
        if (Math.Abs(degrees) >= RevitRotateMirrorRules.MaxAngleDeg) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, field, "a full turn or more (" + degrees.ToString("0.###", CultureInfo.InvariantCulture) + " degrees); use an angle under 360 either way");
        if (Math.Abs(degrees) < RevitRotateMirrorRules.MinAngleDeg) throw new RevitCommandException(RevitCommandCodes.AngleInvalid, field, "angle under 0.01 degrees (" + degrees.ToString("0.#####", CultureInfo.InvariantCulture) + ")");
        return new ElementRotateArgs(uids, ToPoint(center), angle, degrees);
    }

    // Distinct UniqueIds, an axis line at least 1.2 mm long and keep_original.
    static RevitCommandArgs BindMirror(JsonElement args)
    {
        var uids = DistinctUniqueIds(args.GetProperty("element_uids"), "args.element_uids", null);
        var axis = args.GetProperty("axis");
        var start = Coordinates(axis, "start", "args.axis.start");
        var end = Coordinates(axis, "end", "args.axis.end");
        RevitCommandGeometry.CheckLine(start.X, start.Y, end.X, end.Y, "args.axis");
        return new ElementMirrorArgs(uids, ToPoint(start), ToPoint(end), args.GetProperty("keep_original").GetBoolean());
    }
}
