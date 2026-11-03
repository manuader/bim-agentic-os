using Autodesk.Revit.DB;
using Bim.Core;
using System.Reflection;
using System.Runtime.ExceptionServices;
using RevitElement = Autodesk.Revit.DB.Element;
namespace Bim.Revit;

// wall.attach Revit executor. Same admission, receipts, transaction group, failure handling and readback-before-receipt
// as catalogue v1 (CommandsExecutor.cs); every decision that needs no Revit object is in CommandsWallAttach.cs
// (unit-tested). Evidence level: compiled against the Revit 2025 reference assemblies in CI; nothing here has run in
// Revit.
//
// Revit API used (https://www.revitapidocs.com/2026/):
//   Wall.AddAttachment(ElementId targetId, AttachmentLocation attachmentLocation)  bef619ea-dede-2795-d767-04a464a997f0
//     (throws when an attachment already exists at that location), Wall.GetAttachmentIds(AttachmentLocation)
//     3f317dcc-f9ad-3670-c89c-1703c813e8be, static Wall.IsValidTargetAttachment(Document, ElementId)
//     e795b763-9f7e-00a6-5289-f9f4d8bf9da5, enum AttachmentLocation { Base = 0, Top = 1 }.
//   These first appear in Revit 2025.3; the CI reference package (Nice3point.Revit.Api 2025.0.2) and Revit 2025.0-2025.2
//   do not have them, so they are bound by reflection at run time. Without them the command fails closed
//   (transaction_failed, nothing changed); there is no older public API that attaches a wall.
//   BuiltInParameter.WALL_TOP_IS_ATTACHED / WALL_BOTTOM_IS_ATTACHED, WALL_USER_HEIGHT_PARAM, Element.get_BoundingBox(null).
// Unverified in Revit (owner session required): that the reflection binding finds the members on 2025.3+; that
// AddAttachment on a gable end wall below a gable roof makes the wall follow both slopes; that GetAttachmentIds lists
// the roof immediately after Regenerate inside the same transaction; that WALL_TOP_IS_ATTACHED reads 1 afterwards;
// whether Revit raises a warning (kept by failure processing) or an error for a roof that does not cover the wall.
internal static partial class CommandExecutor
{
    sealed record AttachmentApi(MethodInfo Add, MethodInfo GetIds, MethodInfo? IsValidTarget, Type Location);

    static readonly Lazy<AttachmentApi?> WallAttachmentApi = new(() =>
    {
        var location = typeof(Wall).Assembly.GetType("Autodesk.Revit.DB.AttachmentLocation");
        if (location is null || !location.IsEnum) return null;
        var add = typeof(Wall).GetMethod("AddAttachment", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(ElementId), location }, null);
        var ids = typeof(Wall).GetMethod("GetAttachmentIds", BindingFlags.Public | BindingFlags.Instance, null, new[] { location }, null);
        var valid = typeof(Wall).GetMethod("IsValidTargetAttachment", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Document), typeof(ElementId) }, null);
        return add is null || ids is null ? null : new AttachmentApi(add, ids, valid is { ReturnType: var type } && type == typeof(bool) ? valid : null, location);
    });

    // Invoke a reflected Revit member; a Revit argument or state refusal is the request's fault (args_invalid).
    static object? InvokeRevit(MethodInfo method, object? target, object?[] arguments, string field)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is Autodesk.Revit.Exceptions.ArgumentException or Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            throw CommandRulesModify.RevitRefused(field, error.InnerException!.Message);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    static string? CategoryName(RevitElement element) => element.Category is { } category ? ((BuiltInCategory)category.Id.Value).ToString() : null;

    static string[] AttachedUids(Document d, AttachmentApi api, Wall wall, object location) =>
        InvokeRevit(api.GetIds, wall, new object[] { location }, "args.wall_uid") is IEnumerable<ElementId> ids ? ids.Select(id => UniqueIdOf(d, id)).Where(uid => uid.Length > 0).ToArray() : Array.Empty<string>();

    static WritePlan WallAttachWrite(Document d, WallAttachArgs args)
    {
        var wallElement = d.GetElement(args.WallUid) ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.wall_uid", "no element with UniqueId " + args.WallUid);
        var wall = wallElement as Wall ?? throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.wall_uid", "the element " + args.WallUid + " is " + (CategoryName(wallElement) ?? "an element without category") + ", not a wall");
        var target = d.GetElement(args.TargetUid) ?? throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.target_uid", "no element with UniqueId " + args.TargetUid);
        var kind = CommandRulesWallAttach.CheckTarget(CategoryName(target));
        var api = WallAttachmentApi.Value;
        CommandRulesWallAttach.CheckApi(api is not null);
        var location = Enum.Parse(api!.Location, CommandRulesWallAttach.ApiLocation(args.Location));
        if (api.IsValidTarget is { } isValid) CommandRulesWallAttach.CheckValidTarget(InvokeRevit(isValid, null, new object?[] { d, target.Id }, "args.target_uid") as bool?);
        var already = CommandRulesWallAttach.AlreadyAttached(AttachedUids(d, api, wall, location), target.UniqueId, args.Location);
        var flagParameter = Enum.Parse<BuiltInParameter>(CommandRulesWallAttach.AttachedParameter(args.Location));
        var boxBefore = wall.get_BoundingBox(null) is { } before ? Mm(before.Max.Z) : (double?)null;
        ObservedWallAttach Observe() => new(wall.UniqueId, AttachedUids(d, api, wall, location),
            wall.get_Parameter(flagParameter) is { HasValue: true, StorageType: StorageType.Integer } flag ? flag.AsInteger() != 0 : null,
            LengthParameter(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM),
            wall.get_BoundingBox(null) is { } box ? Mm(box.Max.Z) : null);
        return new WritePlan(
            () =>
            {
                if (!already) InvokeRevit(api.Add, wall, new object[] { target.Id, location }, "args.target_uid");
            },
            () => wall.IsValidObject && target.IsValidObject ? CommandReadbackWallAttach.Attach(target.UniqueId, Observe()) : "the wall or its target no longer exists",
            () => CommandReadbackWallAttach.Json(Observe(), target.UniqueId, kind, args.Location, already, boxBefore),
            Array.Empty<string>,
            () => already ? Array.Empty<string>() : new[] { wall.UniqueId });
    }
}
