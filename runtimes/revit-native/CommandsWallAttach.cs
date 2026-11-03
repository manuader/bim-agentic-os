using Bim.Core;
using System.Text;
using System.Text.Json;
namespace Bim.Revit;

// wall.attach executor decisions, readback check and readback JSON, in millimetres. Autodesk-free on purpose:
// tests/revit-commands source-links this file. CommandsExecutorWallAttach.cs reads the Revit facts (lengths converted
// with UnitUtils) and calls the Revit API.

// What the wall reports after regeneration. AttachedUids: Wall.GetAttachmentIds(location) as UniqueIds; AttachedFlag:
// WALL_TOP_IS_ATTACHED / WALL_BOTTOM_IS_ATTACHED (null when unreadable); UnconnectedHeightMm: WALL_USER_HEIGHT_PARAM;
// BoxMaxZMm: the top of Element.get_BoundingBox(null) (null without a box).
public sealed record ObservedWallAttach(string WallUid, IReadOnlyList<string> AttachedUids, bool? AttachedFlag, double? UnconnectedHeightMm, double? BoxMaxZMm);

public static class CommandRulesWallAttach
{
    // Built-in category name (BuiltInCategory.ToString()) to the target kind reported in the readback.
    static readonly IReadOnlyDictionary<string, string> Targets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OST_Roofs"] = "roof",
        ["OST_Floors"] = "floor",
        ["OST_Ceilings"] = "ceiling",
        ["OST_Toposolid"] = "toposolid",
    };

    public static string? TargetKind(string? builtInCategory) => builtInCategory is not null && Targets.TryGetValue(builtInCategory, out var kind) ? kind : null;

    public static string CheckTarget(string? builtInCategory) => TargetKind(builtInCategory)
        ?? throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.target_uid", "the target is " + (builtInCategory ?? "an element without category") + "; a wall attaches only to a roof, floor, ceiling or toposolid");

    // Wall.AddAttachment and Wall.GetAttachmentIds first appear in Revit 2025.3; older Revit has no API to attach.
    public static void CheckApi(bool available)
    {
        if (!available) throw new RevitCommandException(RevitCommandCodes.TransactionFailed, "command", "this Revit has no Wall.AddAttachment API (added in Revit 2025.3); update Revit, or attach the wall by hand with Modify | Walls > Attach Top/Base");
    }

    // Wall.IsValidTargetAttachment(Document, ElementId); null when this Revit does not offer the check.
    public static void CheckValidTarget(bool? valid)
    {
        if (valid == false) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.target_uid", "Revit does not accept this element as an attachment target for the wall");
    }

    public static string AttachedParameter(string location) => location == RevitWallAttachRules.Base ? "WALL_BOTTOM_IS_ATTACHED" : "WALL_TOP_IS_ATTACHED";

    // AttachmentLocation member name.
    public static string ApiLocation(string location) => location == RevitWallAttachRules.Base ? "Base" : "Top";

    // attached: the UniqueIds the wall is attached to at location before the command. True when the target is already
    // one of them (nothing to do); Revit allows one attachment per location, so any other element is refused.
    public static bool AlreadyAttached(IReadOnlyList<string> attached, string targetUid, string location)
    {
        if (attached.Any(uid => string.Equals(uid, targetUid, StringComparison.OrdinalIgnoreCase))) return true;
        if (attached.Count > 0)
            throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.target_uid", "the wall's " + location + " is already attached to " + string.Join(", ", attached) + "; Revit allows one attachment per location, detach it first");
        return false;
    }
}

// Null when the readback matches, else a plain-English mismatch; the executor then rolls the whole transaction group
// back and answers transaction_failed with that text.
public static class CommandReadbackWallAttach
{
    public static string? Attach(string targetUid, ObservedWallAttach observed)
    {
        if (!observed.AttachedUids.Any(uid => string.Equals(uid, targetUid, StringComparison.OrdinalIgnoreCase)))
            return "the wall's attachments are [" + string.Join(", ", observed.AttachedUids) + "], expected " + targetUid;
        if (observed.AttachedFlag == false) return "the wall's attached flag is not set after the attachment";
        if (observed.BoxMaxZMm is null) return "the wall has no bounding box after the attachment";
        return null;
    }

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number)) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    public static string Json(ObservedWallAttach observed, string targetUid, string targetKind, string location, bool alreadyAttached, double? boxMaxZBeforeMm)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("wall_attach");
            writer.WriteString("wall_unique_id", observed.WallUid);
            writer.WriteString("target_unique_id", targetUid);
            writer.WriteString("target_kind", targetKind);
            writer.WriteString("location", location);
            writer.WriteBoolean("already_attached", alreadyAttached);
            writer.WriteStartArray("attached_unique_ids");
            foreach (var uid in observed.AttachedUids) writer.WriteStringValue(uid);
            writer.WriteEndArray();
            if (observed.AttachedFlag is { } flag) writer.WriteBoolean("attached_flag", flag);
            else writer.WriteNull("attached_flag");
            Number(writer, "unconnected_height_mm", observed.UnconnectedHeightMm);
            Number(writer, "bounding_box_max_z_mm", observed.BoxMaxZMm);
            Number(writer, "bounding_box_max_z_before_mm", boxMaxZBeforeMm);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
