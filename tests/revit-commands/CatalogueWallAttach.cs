using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// wall.attach contract oracle: attach a wall's top (or base) to a roof, floor, ceiling or toposolid (gable end walls
// reaching a sloped roof). Covers the schema, the typed binder (default location, wall and target distinct), the
// guarded client path (fake transport) and the Autodesk-free executor rules and readback in
// runtimes/revit-native/CommandsWallAttach.cs. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorWallAttach.cs is compiled, never run.
static class CatalogueWallAttachChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a001";
    const string RoofUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002b001";
    const string OtherUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002b002";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-attach",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = "wall.attach",
        ["args"] = args.DeepClone(),
    };
    static JsonObject Gable() => new() { ["wall_uid"] = WallUid, ["target_uid"] = RoofUid, ["location"] = "top" };
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static void Rejects(string name, JsonNode envelope, string code, string? field = null)
    {
        var error = Refusal(envelope.ToJsonString());
        Check(error is not null && error.Code == code && (field is null || error.Field == field), name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
    static RevitCommandEnvelope? Accepts(string name, JsonNode envelope)
    {
        try { var parsed = RevitCommandProtocol.Parse(envelope.ToJsonString()); Check(true, name); return parsed; }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        ClientPath();
        ExecutorRules();
        Readbacks();
        ReadbackJson();
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.WallAttach.Select(spec => spec.Name).SequenceEqual(new[] { "wall.attach" }), "wall attach catalogue lists wall.attach");
        var spec = RevitCommandCatalogue.Find("wall.attach");
        Check(spec is not null && ReferenceEquals(spec, RevitCommandCatalogue.WallAttach[0]) && RevitCommandCatalogue.All.Contains(spec), "wall.attach is in the catalogue enumeration and Find resolves it");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with wall.attach");
        Check(spec?.ToolName == "revit_wall_attach", "wall.attach tool name is revit_wall_attach");
        Check(spec is { Writes: true } && !RevitCommandCatalogue.IsDestructive("wall.attach"), "wall.attach is a write, not destructive (it changes a wall's constraint, deletes nothing)");
        Check(spec is not null && spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal) && spec.Description.Contains("roof", StringComparison.Ordinal), "wall.attach describes roof attachment and the copies-only rule");
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec!.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException) { problem = error.Message; }
        Check(problem is null, "wall.attach args schema is valid JSON using only enforced keywords", problem ?? "");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("wall.attach", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("wall.attach", RealPath, copies) == PermissionLevel.WriteRealProject, "wall.attach requires write-copies on a copy and write-real-project on a real project");
        Check(RevitWallAttachRules.Locations.SequenceEqual(new[] { "top", "base" }) && RevitWallAttachRules.Top == "top" && RevitWallAttachRules.Base == "base", "attachment locations are top and base");
    }

    static void Binding()
    {
        Check(Accepts("gable wall top to roof accepted", Envelope(Gable()))?.Args is WallAttachArgs { WallUid: WallUid, TargetUid: RoofUid, Location: "top" }, "wall.attach binds wall, target and location");
        Check(Accepts("location omitted accepted", Envelope(With(Gable(), "location", null)))?.Args is WallAttachArgs { Location: "top" }, "location defaults to top");
        Check(Accepts("base location accepted", Envelope(With(Gable(), "location", "base")))?.Args is WallAttachArgs { Location: "base" }, "location base binds");
        Rejects("unknown location", Envelope(With(Gable(), "location", "middle")), RevitCommandCodes.ArgsInvalid, "args.location");
        Rejects("upper-case location", Envelope(With(Gable(), "location", "Top")), RevitCommandCodes.ArgsInvalid, "args.location");
        Rejects("no wall", Envelope(With(Gable(), "wall_uid", null)), RevitCommandCodes.ArgsInvalid, "args.wall_uid");
        Rejects("no target", Envelope(With(Gable(), "target_uid", null)), RevitCommandCodes.ArgsInvalid, "args.target_uid");
        Rejects("wall not a UniqueId", Envelope(With(Gable(), "wall_uid", "Wall 1")), RevitCommandCodes.ArgsInvalid, "args.wall_uid");
        Rejects("target not a UniqueId", Envelope(With(Gable(), "target_uid", "Roof")), RevitCommandCodes.ArgsInvalid, "args.target_uid");
        Rejects("unexpected argument", Envelope(With(Gable(), "offset_mm", 0)), RevitCommandCodes.ArgsInvalid, "args.offset_mm");
        Rejects("wall attached to itself", Envelope(With(Gable(), "target_uid", WallUid)), RevitCommandCodes.ArgsInvalid, "args.target_uid");
        Rejects("wall attached to itself, hex case differs", Envelope(With(Gable(), "target_uid", WallUid.ToUpperInvariant())), RevitCommandCodes.ArgsInvalid, "args.target_uid");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Gable()).ToJsonString(), policy, Transport, noLinks);
        Check(!ok.IsError && forwarded.Count == 1 && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(Gable()).ToJsonString())), "wall.attach on an enabled copy is forwarded as the canonical wire line", ok.Line);
        var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(Gable(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, "wall.attach on a real project is document_not_enabled and never forwarded", refused.Line);
        var self = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(With(Gable(), "target_uid", WallUid)).ToJsonString(), policy, Transport, noLinks);
        Check(self.IsError && !self.Forwarded && forwarded.Count == 1, "a wall attached to itself is refused before the pipe", self.Line);
        var a = RevitCommandProtocol.Parse(Envelope(Gable()).ToJsonString());
        var b = RevitCommandProtocol.Parse(new JsonObject { ["args"] = new JsonObject { ["location"] = "top", ["target_uid"] = RoofUid, ["wall_uid"] = WallUid }, ["command"] = "wall.attach", ["operation_id"] = "op-attach", ["protocol_version"] = 1, ["document"] = new JsonObject { ["unique_id"] = Uid, ["path"] = FixturePath } }.ToJsonString());
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "wall.attach requests use the same canonical form and hash as v1 (receipts replay by it)");
    }

    static string Answer(string wireLine)
    {
        using var wire = JsonDocument.Parse(wireLine);
        var commandText = wire.RootElement.GetProperty("Command").GetRawText();
        using var command = JsonDocument.Parse(commandText);
        var receipt = new RevitCommandReceipt(command.RootElement.GetProperty("operation_id").GetString()!, command.RootElement.GetProperty("command").GetString()!,
            command.RootElement.GetProperty("document").GetProperty("unique_id").GetString()!, CommandWire.Sha256(commandText), false);
        return RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, Array.Empty<string>()).ToJson();
    }

    static void ExecutorRules()
    {
        Check(CommandRulesWallAttach.TargetKind("OST_Roofs") == "roof" && CommandRulesWallAttach.TargetKind("OST_Floors") == "floor" && CommandRulesWallAttach.TargetKind("OST_Ceilings") == "ceiling" && CommandRulesWallAttach.TargetKind("OST_Toposolid") == "toposolid",
            "roofs, floors, ceilings and toposolids are attachment targets");
        Check(CommandRulesWallAttach.TargetKind("OST_Walls") is null && CommandRulesWallAttach.TargetKind("OST_Doors") is null && CommandRulesWallAttach.TargetKind(null) is null, "walls, doors and uncategorized elements are not targets");
        Check(Thrown(() => CommandRulesWallAttach.CheckTarget("OST_Roofs")) is null && CommandRulesWallAttach.CheckTarget("OST_Roofs") == "roof", "CheckTarget returns the kind of a roof");
        Check(Thrown(() => CommandRulesWallAttach.CheckTarget("OST_Doors")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.target_uid" } door && door.Detail!.Contains("roof", StringComparison.Ordinal), "a door target is args_invalid naming the accepted kinds");
        Check(Thrown(() => CommandRulesWallAttach.CheckTarget(null)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.target_uid" }, "a target without category is args_invalid");
        Check(Thrown(() => CommandRulesWallAttach.CheckApi(true)) is null, "an available attachment API passes");
        Check(Thrown(() => CommandRulesWallAttach.CheckApi(false)) is { Code: RevitCommandCodes.TransactionFailed } api && api.Detail!.Contains("2025.3", StringComparison.Ordinal), "a Revit without Wall.AddAttachment fails closed naming Revit 2025.3");
        Check(Thrown(() => CommandRulesWallAttach.CheckValidTarget(null)) is null && Thrown(() => CommandRulesWallAttach.CheckValidTarget(true)) is null, "a valid (or unchecked) target passes");
        Check(Thrown(() => CommandRulesWallAttach.CheckValidTarget(false)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.target_uid" }, "a target Revit's IsValidTargetAttachment refuses is args_invalid");
        Check(CommandRulesWallAttach.AttachedParameter("top") == "WALL_TOP_IS_ATTACHED" && CommandRulesWallAttach.AttachedParameter("base") == "WALL_BOTTOM_IS_ATTACHED", "attachment flags are WALL_TOP_IS_ATTACHED and WALL_BOTTOM_IS_ATTACHED");
        Check(CommandRulesWallAttach.ApiLocation("top") == "Top" && CommandRulesWallAttach.ApiLocation("base") == "Base", "locations map to AttachmentLocation.Top and AttachmentLocation.Base");
        Check(!CommandRulesWallAttach.AlreadyAttached(Array.Empty<string>(), RoofUid, "top"), "a wall with no attachment at the location is attached");
        Check(CommandRulesWallAttach.AlreadyAttached(new[] { RoofUid.ToUpperInvariant() }, RoofUid, "top"), "a wall already attached to the target is a no-op (UniqueIds ignore hex case)");
        Check(Thrown(() => CommandRulesWallAttach.AlreadyAttached(new[] { OtherUid }, RoofUid, "top")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.target_uid" } other && other.Detail!.Contains(OtherUid, StringComparison.Ordinal) && other.Detail!.Contains("top", StringComparison.Ordinal),
            "a wall whose top is attached to another element is refused naming it (Revit allows one attachment per location)");
    }

    static void Readbacks()
    {
        var good = new ObservedWallAttach(WallUid, new[] { RoofUid }, true, 3000, 5200);
        Check(CommandReadbackWallAttach.Attach(RoofUid, good) is null, "a wall listing the roof with the attached flag set matches");
        Check(CommandReadbackWallAttach.Attach(RoofUid.ToUpperInvariant(), good) is null, "attachment UniqueIds compare ignoring hex case");
        Check(CommandReadbackWallAttach.Attach(RoofUid, good with { AttachedFlag = null }) is null, "an unreadable attached flag is not a mismatch when the attachment list names the target");
        Check(CommandReadbackWallAttach.Attach(RoofUid, good with { AttachedUids = Array.Empty<string>() }) is { } missing && missing.Contains(RoofUid, StringComparison.Ordinal), "a wall that does not list the target is a mismatch");
        Check(CommandReadbackWallAttach.Attach(RoofUid, good with { AttachedUids = new[] { OtherUid } }) is not null, "a wall attached to another element is a mismatch");
        Check(CommandReadbackWallAttach.Attach(RoofUid, good with { AttachedFlag = false }) is { } flag && flag.Contains("attached", StringComparison.Ordinal), "a cleared attached flag is a mismatch");
        Check(CommandReadbackWallAttach.Attach(RoofUid, good with { BoxMaxZMm = null }) is { } box && box.Contains("bounding box", StringComparison.Ordinal), "a wall without bounding box is a mismatch");
    }

    static void ReadbackJson()
    {
        var json = JsonNode.Parse(CommandReadbackWallAttach.Json(new ObservedWallAttach(WallUid, new[] { RoofUid }, true, 3000, 5200), RoofUid, "roof", "top", false, 3000))!["wall_attach"]!;
        Check(json["wall_unique_id"]!.GetValue<string>() == WallUid && json["target_unique_id"]!.GetValue<string>() == RoofUid && json["target_kind"]!.GetValue<string>() == "roof" && json["location"]!.GetValue<string>() == "top"
            && !json["already_attached"]!.GetValue<bool>() && json["attached_unique_ids"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { RoofUid }) && json["attached_flag"]!.GetValue<bool>()
            && json["unconnected_height_mm"]!.GetValue<double>() == 3000 && json["bounding_box_max_z_mm"]!.GetValue<double>() == 5200 && json["bounding_box_max_z_before_mm"]!.GetValue<double>() == 3000,
            "wall.attach readback reports wall, target, kind, location, already_attached, attachments, flag, unconnected height and bounding box top before and after in millimetres", json.ToJsonString());
        var empty = JsonNode.Parse(CommandReadbackWallAttach.Json(new ObservedWallAttach(WallUid, new[] { RoofUid }, null, null, null), RoofUid, "roof", "base", true, null))!["wall_attach"]!;
        Check(empty["attached_flag"] is null && empty["unconnected_height_mm"] is null && empty["bounding_box_max_z_mm"] is null && empty["bounding_box_max_z_before_mm"] is null && empty["already_attached"]!.GetValue<bool>() && empty["location"]!.GetValue<string>() == "base",
            "unreadable wall.attach values are null", empty.ToJsonString());
    }
}
