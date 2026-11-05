using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// element.rotate / element.mirror contract oracle (P1-10): rotate 1 to 100 elements about a vertical axis through a
// plan point by an angle in degrees, or mirror them in a vertical plane through a plan line, keeping the originals
// (mirrored copies) or not. Covers the schema, the typed binder (count, distinct ids, angle range, axis length,
// keep_original), the guarded client path (fake transport), command.batch steps and the Autodesk-free executor rules
// and readback in runtimes/revit-native/CommandsRotateMirror.cs (typed refusals for pinned, not rotatable and not
// mirrorable elements; rotated and mirrored locations within 1 mm; mirrored copies and replacements mapped to their
// sources). Expected coordinates are computed by hand in the comments, not with the implementation. No Revit is
// involved: a passing run is unit-tested evidence only; CommandsExecutorRotateMirror.cs is compiled, never run.
static class CatalogueRotateMirrorChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004a001";
    const string ColumnUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004a002";
    const string FloorUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004a003";
    const string DoorUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004a004";
    const string WallCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004b001";
    const string ColumnCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004b002";
    const string FloorCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004b003";
    const string DoorCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0004b004";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    const string WallKey = "Wall|-2000011|type-200";
    const string ColumnKey = "FamilyInstance|-2001330|type-col";
    const string DoorKey = "FamilyInstance|-2000023|type-door";
    const string FloorKey = "Floor|-2000032|type-floor";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-rotate-mirror",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Uids(params string[] uids) => new(uids.Select(uid => (JsonNode?)JsonValue.Create(uid)).ToArray());
    static JsonArray Xy(double x, double y) => new(x, y);
    static JsonObject RotateArgs(JsonArray uids, JsonNode center, JsonNode angle) => new() { ["element_uids"] = uids, ["center"] = center, ["angle_deg"] = angle };
    static JsonObject MirrorArgs(JsonArray uids, JsonNode start, JsonNode end, JsonNode? keep) =>
        keep is null
            ? new JsonObject { ["element_uids"] = uids, ["axis"] = new JsonObject { ["start"] = start, ["end"] = end } }
            : new JsonObject { ["element_uids"] = uids, ["axis"] = new JsonObject { ["start"] = start, ["end"] = end }, ["keep_original"] = keep };
    static JsonObject SimpleRotate() => RotateArgs(Uids(WallUid), Xy(0, 0), 90);
    static JsonObject SimpleMirror() => MirrorArgs(Uids(WallUid), Xy(0, 2000), Xy(1000, 2000), true);
    static string Numbered(int i) => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;
    static bool NearAll(double[] actual, params double[] expected) => actual.Length == expected.Length && actual.Zip(expected).All(pair => Near(pair.First, pair.Second, 1e-6));

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
    static RevitCommandArgs? Accepts(string name, JsonNode envelope)
    {
        try { return RevitCommandProtocol.Parse(envelope.ToJsonString()).Args; }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
        catch (Exception error) { Check(false, name, error.GetType().Name + ": " + error.Message); return null; }
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    // Runs a rule that returns a value; an unexpected exception is a failed check, not a crash of the whole run.
    static T? Safe<T>(string name, Func<T> call) where T : class
    {
        try { return call(); }
        catch (Exception error) { Check(false, name, error.GetType().Name + ": " + error.Message); return null; }
    }

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        RotateBinding();
        MirrorBinding();
        ClientPath();
        Batch();
        Targets();
        RotateGeometry();
        MirrorGeometry();
        RotateReadback();
        MirrorCopies();
        MirrorInPlace();
        MirrorProfiles();
        Json();
        SmallTurns();
        MirrorFlags();
        Replacements();
        CanMirrorOnce();
        AxisAndPlane();
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.RotateMirror.Select(spec => spec.Name).SequenceEqual(new[] { "element.rotate", "element.mirror" }), "rotate/mirror catalogue lists element.rotate, element.mirror");
        foreach (var spec in RevitCommandCatalogue.RotateMirror)
        {
            Check(RevitCommandCatalogue.All.Contains(spec) && ReferenceEquals(RevitCommandCatalogue.Find(spec.Name), spec), spec.Name + " is in the catalogue enumeration and Find resolves it");
            Check(spec.Writes && RevitCommandCatalogue.BatchStepCommands.Contains(spec), spec.Name + " is a write and can be a command.batch step");
            Check(spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal) && spec.Description.Contains("pinned", StringComparison.Ordinal)
                && spec.Description.Contains("1 to 100", StringComparison.Ordinal) && spec.Description.Contains("millimetres", StringComparison.Ordinal),
                spec.Name + " description states the copies-only rule, the 1 to 100 limit, millimetres and the pinned refusal", spec.Description);
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
            var copies = new[] { @"C:\Repo\.bim\native-runs" };
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == PermissionLevel.WriteRealProject,
                spec.Name + " is write-copies on a copy and write-real-project elsewhere");
            // Both change existing elements (a mirror without keep_original turns the originals themselves).
            Check(RevitCommandCatalogue.IsDestructive(spec.Name), spec.Name + " is destructive (MCP destructiveHint)");
        }
        Check(RevitCommandCatalogue.Find("element.rotate")?.ToolName == "revit_element_rotate" && RevitCommandCatalogue.Find("element.mirror")?.ToolName == "revit_element_mirror", "tool names follow revit_<command>");
        Check(RevitCommandCatalogue.Find("element.rotate")?.Description.Contains("counter-clockwise", StringComparison.Ordinal) == true && RevitCommandCatalogue.Find("element.rotate")?.Description.Contains("degrees", StringComparison.Ordinal) == true,
            "element.rotate states the sign convention (counter-clockwise seen from above) and degrees");
        Check(RevitCommandCatalogue.Find("element.mirror")?.Description.Contains("keep_original", StringComparison.Ordinal) == true, "element.mirror explains keep_original");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the rotate and mirror commands");
        Check(RevitRotateMirrorRules.Rotate == "element.rotate" && RevitRotateMirrorRules.Mirror == "element.mirror" && RevitRotateMirrorRules.MinTargets == 1 && RevitRotateMirrorRules.MaxTargets == 100
            && RevitRotateMirrorRules.MinAngleDeg == 0.01 && RevitRotateMirrorRules.MaxAngleDeg == 360, "1 to 100 targets, angle from 0.01 to under 360 degrees either way");
        var codes = new[] { RevitRotateMirrorCodes.ElementNotRotatable, RevitRotateMirrorCodes.ElementNotMirrorable, RevitRotateMirrorCodes.AngleTooSmallForTarget, RevitRotateMirrorCodes.MirrorImageUnobservable };
        Check(codes.SequenceEqual(new[] { "element_not_rotatable", "element_not_mirrorable", "angle_too_small_for_target", "mirror_image_unobservable" }) && RevitRotateMirrorCodes.All.SequenceEqual(codes),
            "typed refusal codes element_not_rotatable, element_not_mirrorable, angle_too_small_for_target, mirror_image_unobservable");
        // Review NIT7: the pinned refusal holds with keep_original true too, and the description says so.
        Check(RevitCommandCatalogue.Find("element.mirror")?.Description.Contains("even with keep_original true", StringComparison.Ordinal) == true
            && RevitCommandCatalogue.Find("element.mirror")?.Description.Contains("deleted", StringComparison.Ordinal) == true
            && RevitCommandCatalogue.Find("element.rotate")?.Description.Contains("angle_too_small_for_target", StringComparison.Ordinal) == true,
            "element.mirror documents the pinned refusal with keep_original true and the deleted originals; element.rotate documents angle_too_small_for_target");
        Check(codes.All(code => !RevitCommandCodes.ClientSide.Contains(code) && !RevitCommandCodes.ExecutorReserved.Contains(code) && !RevitTransformCodes.All.Contains(code) && System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[a-z]+(_[a-z0-9]+)*\z")),
            "rotate/mirror refusal codes are snake_case and distinct from every protocol and move/copy code");
        // The catalogue keeps its order: the rotate and mirror commands come right after move and copy (P1-09), before command.batch.
        var all = RevitCommandCatalogue.All.ToList();
        var copyAt = all.FindIndex(spec => spec.Name == "element.copy");
        Check(copyAt >= 0 && all.Skip(copyAt + 1).Take(2).SequenceEqual(RevitCommandCatalogue.RotateMirror) && all.Skip(copyAt + 3).SequenceEqual(RevitCommandCatalogue.Batch),
            "All lists element.rotate and element.mirror right after element.copy and before command.batch");
    }

    static void RotateBinding()
    {
        // 90 degrees = pi / 2 rad.
        var rotate = Accepts("a one-element rotation is accepted", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(1000, -500), 90)));
        Check(rotate is ElementRotateArgs { ElementUids: [WallUid] } r && Near(r.Center.X.Millimetres, 1000) && Near(r.Center.Y.Millimetres, -500) && Near(r.Angle.Radians, Math.PI / 2, 1e-12),
            "element.rotate binds the UniqueId, the centre in millimetres and the angle", rotate?.ToString() ?? "");
        var clockwise = Accepts("a negative angle is accepted", Envelope("element.rotate", RotateArgs(Uids(WallUid, ColumnUid), Xy(0, 0), -45)));
        Check(clockwise is ElementRotateArgs { ElementUids: [WallUid, ColumnUid] } cw && Near(cw.Angle.Degrees, -45, 1e-9), "a clockwise (negative) angle binds with its sign; UniqueIds keep their order", clockwise?.ToString() ?? "");
        var hundred = Enumerable.Range(1, 100).Select(Numbered).ToArray();
        Check(Accepts("100 elements are accepted", Envelope("element.rotate", RotateArgs(Uids(hundred), Xy(0, 0), 30))) is ElementRotateArgs { ElementUids.Count: 100 }, "100 distinct elements bind");
        Rejects("101 elements are args_invalid", Envelope("element.rotate", RotateArgs(Uids(Enumerable.Range(1, 101).Select(Numbered).ToArray()), Xy(0, 0), 30)), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Rejects("no element is args_invalid", Envelope("element.rotate", RotateArgs(Uids(), Xy(0, 0), 30)), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Rejects("the same UniqueId twice (other case) is args_invalid at the repeat", Envelope("element.rotate", RotateArgs(Uids(WallUid, WallUid.ToUpperInvariant()), Xy(0, 0), 30)), RevitCommandCodes.ArgsInvalid, "args.element_uids[1]");
        Rejects("missing center is args_invalid", Envelope("element.rotate", new JsonObject { ["element_uids"] = Uids(WallUid), ["angle_deg"] = 90 }), RevitCommandCodes.ArgsInvalid, "args.center");
        Rejects("missing angle_deg is args_invalid", Envelope("element.rotate", new JsonObject { ["element_uids"] = Uids(WallUid), ["center"] = Xy(0, 0) }), RevitCommandCodes.ArgsInvalid, "args.angle_deg");
        Rejects("a three-component center is args_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), new JsonArray(0, 0, 0), 90)), RevitCommandCodes.ArgsInvalid, "args.center");
        Rejects("a text angle is args_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), "90")), RevitCommandCodes.ArgsInvalid, "args.angle_deg");
        var extra = SimpleRotate(); extra["copy"] = true;
        Rejects("an unknown member is args_invalid", Envelope("element.rotate", extra), RevitCommandCodes.ArgsInvalid, "args.copy");
        // Angle range: a non-zero turn under a full turn, at least 0.01 degrees.
        Rejects("a zero angle is angle_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), 0)), RevitCommandCodes.AngleInvalid, "args.angle_deg");
        Rejects("a negative-zero angle is angle_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), JsonNode.Parse("-0.0")!)), RevitCommandCodes.AngleInvalid, "args.angle_deg");
        Rejects("a 0.005 degree angle is under 0.01 degrees", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), 0.005)), RevitCommandCodes.AngleInvalid, "args.angle_deg");
        Check(Accepts("a 0.01 degree angle is accepted", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), 0.01))) is ElementRotateArgs, "0.01 degrees binds");
        Check(Accepts("a 359.99 degree angle is accepted", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), 359.99))) is ElementRotateArgs, "359.99 degrees binds");
        Check(Accepts("a -359.99 degree angle is accepted", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), -359.99))) is ElementRotateArgs, "-359.99 degrees binds");
        Rejects("a full turn is angle_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), 360)), RevitCommandCodes.AngleInvalid, "args.angle_deg");
        Rejects("a full clockwise turn is angle_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), -360)), RevitCommandCodes.AngleInvalid, "args.angle_deg");
        Rejects("450 degrees is angle_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), 450)), RevitCommandCodes.AngleInvalid, "args.angle_deg");
        Rejects("an overflowing angle is angle_invalid", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), JsonNode.Parse("1e400")!)), RevitCommandCodes.AngleInvalid, "args.angle_deg");
        Rejects("an overflowing center is length_invalid at it", Envelope("element.rotate", RotateArgs(Uids(WallUid), new JsonArray(JsonNode.Parse("1e400"), 0), 90)), RevitCommandCodes.LengthInvalid, "args.center[0]");
        Rejects("a center beyond 100 km is length_invalid at it", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 2e8), 90)), RevitCommandCodes.LengthInvalid, "args.center[1]");
        if (rotate is ElementRotateArgs bound)
        {
            var plan = Safe("plan rotation of a bound element.rotate", () => CommandRulesRotateMirror.RotationOf(bound));
            Check(plan is not null && Near(plan.CenterX, 1000) && Near(plan.CenterY, -500) && Near(plan.AngleRad, Math.PI / 2, 1e-12), "the executor's plan rotation is the centre in millimetres and the angle in radians", plan?.ToString() ?? "");
        }
        // Review NIT8: the requested angle is echoed exactly as sent, not through radians.
        if (Accepts("a 33.3 degree turn is accepted", Envelope("element.rotate", RotateArgs(Uids(WallUid), Xy(0, 0), 33.3))) is ElementRotateArgs third)
        {
            var echoed = JsonNode.Parse(Safe("rotate json echo", () => CommandReadbackRotateMirror.RotateJson(new[] { WallUid }, new[] { Curve(1000, 0, 0, 6000, 0, 0) }, new TransformLocation?[] { null }, CommandRulesRotateMirror.RotationOf(third))) ?? "{}");
            Check(third.AngleDeg == 33.3 && echoed?["angle_deg"]?.GetValue<double>() == 33.3, "the readback echoes angle_deg 33.3 exactly as requested", echoed?.ToJsonString() ?? "");
        }
    }

    static void MirrorBinding()
    {
        var keep = Accepts("a mirror keeping the original is accepted", Envelope("element.mirror", SimpleMirror()));
        Check(keep is ElementMirrorArgs { ElementUids: [WallUid], KeepOriginal: true } k && Near(k.AxisStart.X.Millimetres, 0) && Near(k.AxisStart.Y.Millimetres, 2000) && Near(k.AxisEnd.X.Millimetres, 1000) && Near(k.AxisEnd.Y.Millimetres, 2000),
            "element.mirror binds the UniqueId, the axis line in millimetres and keep_original", keep?.ToString() ?? "");
        var move = Accepts("a mirror without the original is accepted", Envelope("element.mirror", MirrorArgs(Uids(WallUid, ColumnUid, FloorUid), Xy(-1000, 0), Xy(-1000, 5000), false)));
        Check(move is ElementMirrorArgs { ElementUids: [WallUid, ColumnUid, FloorUid], KeepOriginal: false }, "keep_original false binds; UniqueIds keep their order", move?.ToString() ?? "");
        Check(Accepts("100 elements are accepted", Envelope("element.mirror", MirrorArgs(Uids(Enumerable.Range(1, 100).Select(Numbered).ToArray()), Xy(0, 0), Xy(0, 1000), true))) is ElementMirrorArgs { ElementUids.Count: 100 }, "100 distinct elements bind");
        Rejects("101 elements are args_invalid", Envelope("element.mirror", MirrorArgs(Uids(Enumerable.Range(1, 101).Select(Numbered).ToArray()), Xy(0, 0), Xy(0, 1000), true)), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Rejects("a malformed UniqueId is args_invalid", Envelope("element.mirror", MirrorArgs(Uids("not-a-unique-id"), Xy(0, 0), Xy(0, 1000), true)), RevitCommandCodes.ArgsInvalid, "args.element_uids[0]");
        Rejects("missing keep_original is args_invalid", Envelope("element.mirror", MirrorArgs(Uids(WallUid), Xy(0, 0), Xy(0, 1000), null)), RevitCommandCodes.ArgsInvalid, "args.keep_original");
        Rejects("a text keep_original is args_invalid", Envelope("element.mirror", MirrorArgs(Uids(WallUid), Xy(0, 0), Xy(0, 1000), "yes")), RevitCommandCodes.ArgsInvalid, "args.keep_original");
        Rejects("missing axis is args_invalid", Envelope("element.mirror", new JsonObject { ["element_uids"] = Uids(WallUid), ["keep_original"] = true }), RevitCommandCodes.ArgsInvalid, "args.axis");
        Rejects("an axis without end is args_invalid", Envelope("element.mirror", new JsonObject { ["element_uids"] = Uids(WallUid), ["axis"] = new JsonObject { ["start"] = Xy(0, 0) }, ["keep_original"] = true }), RevitCommandCodes.ArgsInvalid);
        var extra = SimpleMirror(); extra["angle_deg"] = 90;
        Rejects("an unknown member is args_invalid", Envelope("element.mirror", extra), RevitCommandCodes.ArgsInvalid, "args.angle_deg");
        // |(1, 0)| = 1 mm < 1.2 mm; |(0.9, 0.9)| = 1.27 mm >= 1.2 mm.
        Rejects("a 1 mm axis is curve_too_short", Envelope("element.mirror", MirrorArgs(Uids(WallUid), Xy(0, 0), Xy(1, 0), true)), RevitCommandCodes.CurveTooShort, "args.axis");
        Rejects("an axis whose ends coincide is curve_too_short", Envelope("element.mirror", MirrorArgs(Uids(WallUid), Xy(500, 500), Xy(500, 500), false)), RevitCommandCodes.CurveTooShort, "args.axis");
        Check(Accepts("a 1.27 mm diagonal axis is accepted", Envelope("element.mirror", MirrorArgs(Uids(WallUid), Xy(0, 0), Xy(0.9, 0.9), true))) is ElementMirrorArgs, "an axis just over 1.2 mm binds");
        Rejects("an overflowing axis end is length_invalid at it", Envelope("element.mirror", MirrorArgs(Uids(WallUid), Xy(0, 0), new JsonArray(0, JsonNode.Parse("1e400")), true)), RevitCommandCodes.LengthInvalid, "args.axis.end[1]");
        if (keep is ElementMirrorArgs bound)
        {
            var plan = Safe("plan mirror of a bound element.mirror", () => CommandRulesRotateMirror.MirrorOf(bound));
            Check(plan is not null && Near(plan.StartX, 0) && Near(plan.StartY, 2000) && Near(plan.EndX, 1000) && Near(plan.EndY, 2000), "the executor's plan mirror line is the axis in millimetres", plan?.ToString() ?? "");
        }
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var (command, args, invalid, code) in new[] { ("element.rotate", SimpleRotate(), RotateArgs(Uids(WallUid), Xy(0, 0), 360), RevitCommandCodes.AngleInvalid), ("element.mirror", SimpleMirror(), MirrorArgs(Uids(WallUid), Xy(0, 0), Xy(0, 0), true), RevitCommandCodes.CurveTooShort) })
        {
            var before = forwarded.Count;
            var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
            Check(forwarded.Count == before + 1 && ok.Forwarded && JsonNode.Parse(forwarded[^1])?["Command"]?["command"]?.GetValue<string>() == command, command + " on an enabled copy is forwarded", ok.Line);
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
            var bad = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, invalid).ToJsonString(), policy, Transport, noLinks);
            Check(bad.IsError && !bad.Forwarded && JsonNode.Parse(bad.Line)!["code"]!.GetValue<string>() == code && forwarded.Count == before + 1, command + ": " + code + " is refused before the pipe", bad.Line);
        }
        try
        {
            var a = RevitCommandProtocol.Parse(Envelope("element.rotate", RotateArgs(Uids(WallUid, ColumnUid), Xy(0, 0), 90)).ToJsonString());
            var b = RevitCommandProtocol.Parse(Envelope("element.rotate", new JsonObject { ["angle_deg"] = 90, ["center"] = Xy(0, 0), ["element_uids"] = Uids(WallUid, ColumnUid) }).ToJsonString());
            var other = RevitCommandProtocol.Parse(Envelope("element.rotate", RotateArgs(Uids(WallUid, ColumnUid), Xy(0, 0), -90)).ToJsonString());
            Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256 && a.RequestSha256 != other.RequestSha256,
                "element.rotate requests use the v1 canonical form and hash (member order ignored, the angle's sign kept)");
        }
        catch (RevitCommandException error) { Check(false, "element.rotate canonical form", error.Code + " " + error.Detail); }
    }

    static JsonObject Step(string id, string command, JsonNode args) => new() { ["operation_id"] = id, ["command"] = command, ["args"] = args.DeepClone() };

    static void Batch()
    {
        var batch = new JsonObject { ["steps"] = new JsonArray(Step("s-0", "element.rotate", SimpleRotate()), Step("s-1", "element.mirror", MirrorArgs(Uids(ColumnUid), Xy(0, 0), Xy(0, 1000), false))) };
        var parsed = Accepts("a batch rotating then mirroring is accepted", Envelope("command.batch", batch));
        Check(parsed is CommandBatchArgs { Steps: [{ Args: ElementRotateArgs { ElementUids: [WallUid] } }, { Args: ElementMirrorArgs { ElementUids: [ColumnUid], KeepOriginal: false } }] },
            "batch steps bind to ElementRotateArgs and ElementMirrorArgs in order", parsed?.ToString() ?? "");
        var zero = new JsonObject { ["steps"] = new JsonArray(Step("s-0", "element.rotate", RotateArgs(Uids(ColumnUid), Xy(0, 0), 0))) };
        Rejects("a batch step with a zero angle is angle_invalid under that step", Envelope("command.batch", zero), RevitCommandCodes.AngleInvalid, "args.steps[0].args.angle_deg");
        var shortAxis = new JsonObject { ["steps"] = new JsonArray(Step("s-0", "element.move", new JsonObject { ["element_uids"] = Uids(WallUid), ["vector_mm"] = new JsonArray(1000, 0, 0) }), Step("s-1", "element.mirror", MirrorArgs(Uids(ColumnUid), Xy(0, 0), Xy(0, 1), true))) };
        Rejects("a batch step with a 1 mm axis is curve_too_short under that step", Envelope("command.batch", shortAxis), RevitCommandCodes.CurveTooShort, "args.steps[1].args.axis");
    }

    static TransformLocation Point(double x, double y, double z, double? rotation = 0) => new(TransformLocationKinds.Point, new[] { x, y, z }, null, rotation);
    static TransformLocation Curve(double x0, double y0, double z0, double x1, double y1, double z1) => new(TransformLocationKinds.Curve, new[] { x0, y0, z0 }, new[] { x1, y1, z1 });
    static TransformLocation Box(double x0, double y0, double z0, double x1, double y1, double z1) => new(TransformLocationKinds.Box, new[] { x0, y0, z0 }, new[] { x1, y1, z1 });
    static TransformLocation Profile(params double[][] points) => new(TransformLocationKinds.Profile, points[0], null, null, points);
    static TransformTargetFacts Facts(TransformLocation? location) => new(WallUid, false, false, false, false, false, location);

    static void Targets()
    {
        var wall = Facts(Curve(0, 0, 0, 6000, 0, 0));
        var only = new[] { WallUid };
        // A quarter turn about the origin and a mirror line far to the west move every target here by metres.
        string? Rotate(TransformTargetFacts facts, IReadOnlyCollection<string> targets, string field = "args.element_uids[0]") =>
            Thrown(() => CommandRulesRotateMirror.CheckRotateTarget(facts, targets, new PlanRotation(0, 0, Math.PI / 2), field)) is { } error ? error.Code + "@" + error.Field + "|" + error.Detail : null;
        string? Mirror(TransformTargetFacts facts, bool mirrorable, IReadOnlyCollection<string> targets, string field = "args.element_uids[0]") =>
            Thrown(() => CommandRulesRotateMirror.CheckMirrorTarget(facts, mirrorable, targets, new PlanMirror(-10000, 0, -10000, 1), false, field)) is { } error ? error.Code + "@" + error.Field + "|" + error.Detail : null;

        Check(Rotate(wall, only) is null && Mirror(wall, true, only) is null, "an unpinned model element with a location curve can be rotated and mirrored");
        Check(Rotate(Facts(Point(0, 0, 0)), only) is null && Mirror(Facts(Point(0, 0, 0)), true, only) is null, "an element with a location point can be rotated and mirrored");
        var floor = Facts(Profile(new double[] { 0, 0, 0 }, new double[] { 6000, 0, 0 }, new double[] { 6000, 4000, 0 }, new double[] { 0, 4000, 0 }));
        Check(Rotate(floor, only) is null && Mirror(floor, true, only) is null, "an element read back by its sketch profile (a floor) can be rotated and mirrored");
        // Typed refusals at the target's index, naming it.
        Check(Rotate(wall with { Pinned = true }, only, "args.element_uids[3]") is { } pinned && pinned.StartsWith("element_pinned@args.element_uids[3]|", StringComparison.Ordinal) && pinned.Contains(WallUid, StringComparison.Ordinal),
            "a pinned element is element_pinned at its index, naming it (rotate)");
        Check(Mirror(wall with { Pinned = true }, true, only, "args.element_uids[2]") is { } mpinned && mpinned.StartsWith("element_pinned@args.element_uids[2]|", StringComparison.Ordinal), "a pinned element is element_pinned (mirror)");
        Check(Rotate(wall with { ViewSpecific = true }, only)?.StartsWith("element_view_specific@", StringComparison.Ordinal) == true && Mirror(wall with { ViewSpecific = true }, true, only)?.StartsWith("element_view_specific@", StringComparison.Ordinal) == true,
            "a view-specific element is element_view_specific");
        Check(Rotate(wall with { Grouped = true }, only)?.StartsWith("element_in_group@", StringComparison.Ordinal) == true && Mirror(wall with { Grouped = true }, true, only)?.StartsWith("element_in_group@", StringComparison.Ordinal) == true,
            "a group member is element_in_group");
        var door = new TransformTargetFacts(DoorUid, false, false, false, true, true, Point(1500, 0, 0), WallUid);
        Check(Rotate(door with { HostPresent = false, HostUid = null }, new[] { DoorUid })?.StartsWith("host_missing@", StringComparison.Ordinal) == true
            && Mirror(door with { HostPresent = false, HostUid = null }, true, new[] { DoorUid })?.StartsWith("host_missing@", StringComparison.Ordinal) == true, "a hosted element whose host is missing is host_missing");
        // A door turns with its wall: alone it is not rotatable nor mirrorable; with its host among the targets it is.
        Check(Rotate(door, new[] { DoorUid }, "args.element_uids[1]") is { } lone && lone.StartsWith("element_not_rotatable@args.element_uids[1]|", StringComparison.Ordinal) && lone.Contains("host", StringComparison.Ordinal),
            "a hosted door whose host is not a target is element_not_rotatable, naming its host");
        Check(Mirror(door, true, new[] { DoorUid })?.StartsWith("element_not_mirrorable@", StringComparison.Ordinal) == true, "a hosted door whose host is not a target is element_not_mirrorable");
        Check(Rotate(door with { HostUid = WallUid.ToUpperInvariant() }, new[] { WallUid, DoorUid }) is null && Mirror(door, true, new[] { DoorUid, WallUid }) is null, "a hosted door with its host among the targets (any case, any order) can be rotated and mirrored");
        Check(Rotate(Facts(null), only)?.StartsWith("element_not_rotatable@", StringComparison.Ordinal) == true && Mirror(Facts(null), true, only)?.StartsWith("element_not_mirrorable@", StringComparison.Ordinal) == true,
            "an element without a location is element_not_rotatable / element_not_mirrorable");
        // A bounding box does not turn with the element: a box-only element cannot be read back after a rotation or a mirror.
        Check(Rotate(Facts(Box(0, 0, 0, 6000, 4000, 200)), only) is { } box && box.StartsWith("element_not_rotatable@", StringComparison.Ordinal) && box.Contains("bounding box", StringComparison.Ordinal)
            && Mirror(Facts(Box(0, 0, 0, 6000, 4000, 200)), true, only)?.StartsWith("element_not_mirrorable@", StringComparison.Ordinal) == true, "an element read back only by its bounding box is not rotatable nor mirrorable");
        Check(Mirror(wall, false, only, "args.element_uids[4]") is { } revit && revit.StartsWith("element_not_mirrorable@args.element_uids[4]|", StringComparison.Ordinal) && revit.Contains(WallUid, StringComparison.Ordinal),
            "an element Revit cannot mirror (CanMirrorElements false) is element_not_mirrorable at its index");
        // Order: pinned, view-specific, group, host missing, then the rotate / mirror specific refusals.
        Check(Rotate(new TransformTargetFacts(WallUid, true, true, true, true, false, null), only)?.StartsWith("element_pinned@", StringComparison.Ordinal) == true
            && Mirror(new TransformTargetFacts(WallUid, true, true, true, true, false, null), false, only)?.StartsWith("element_pinned@", StringComparison.Ordinal) == true, "pinned is reported before every other refusal");
        Check(Mirror(new TransformTargetFacts(WallUid, false, false, true, false, false, Curve(0, 0, 0, 1, 0, 0)), false, only)?.StartsWith("element_in_group@", StringComparison.Ordinal) == true, "a group member is reported before not mirrorable");
        Check(Rotate(door with { HostPresent = false }, new[] { DoorUid })?.StartsWith("host_missing@", StringComparison.Ordinal) == true, "host missing is reported before not rotatable");
        // The host carries its door: P1-09's id rule keeps the door out of the ids passed to Revit.
        var ids = CommandRulesTransform.TransformIds(new[] { new TransformTargetFacts(WallUid, false, false, false, false, false, Curve(0, 0, 0, 6000, 0, 0)), door });
        Check(ids.Transformed.SequenceEqual(new[] { WallUid }) && ids.ReadBack.SequenceEqual(new[] { WallUid, DoorUid }), "[wall, door hosted by the wall]: only the wall is rotated or mirrored by Revit, both are read back");
    }

    static void RotateGeometry()
    {
        double[] Turn(double[] p, double cx, double cy, double deg) => CommandRulesRotateMirror.RotatePoint(p, new PlanRotation(cx, cy, deg * Math.PI / 180));
        // (1000, 0, 0) about (0, 0) by 90 degrees counter-clockwise: (0, 1000, 0).
        Check(NearAll(Turn(new double[] { 1000, 0, 0 }, 0, 0, 90), 0, 1000, 0), "(1000, 0) turned 90 degrees about the origin is (0, 1000)");
        // (1000, 0, 0) about (0, 0) by -90 degrees: (0, -1000, 0).
        Check(NearAll(Turn(new double[] { 1000, 0, 0 }, 0, 0, -90), 0, -1000, 0), "(1000, 0) turned -90 degrees is (0, -1000): negative is clockwise");
        // (3000, 1000, 500) about (1000, 1000) by 90: offset (2000, 0) -> (0, 2000); (1000, 3000, 500); z unchanged.
        Check(NearAll(Turn(new double[] { 3000, 1000, 500 }, 1000, 1000, 90), 1000, 3000, 500), "(3000, 1000, 500) turned 90 degrees about (1000, 1000) is (1000, 3000, 500)");
        // (2000, 0) about (0, 0) by 30: (2000 cos 30, 2000 sin 30) = (1732.0508..., 1000).
        Check(NearAll(Turn(new double[] { 2000, 0, 0 }, 0, 0, 30), 2000 * Math.Sqrt(3) / 2, 1000, 0), "(2000, 0) turned 30 degrees is (1732.05, 1000)");
        // A wall (0, 0)-(6000, 0) half a turn about (3000, 2000): start offset (-3000, -2000) -> (3000, 2000) = (6000, 4000);
        // end offset (3000, -2000) -> (-3000, 2000) = (0, 4000).
        var wall = CommandRulesRotateMirror.Rotate(Curve(0, 0, 0, 6000, 0, 0), new PlanRotation(3000, 2000, Math.PI));
        Check(wall.Kind == TransformLocationKinds.Curve && NearAll(wall.A, 6000, 4000, 0) && wall.B is { } end && NearAll(end, 0, 4000, 0), "a wall turned 180 degrees about (3000, 2000) runs from (6000, 4000) to (0, 4000)");
        // A point's own rotation grows by the angle: 0.5 rad + pi / 2.
        var column = CommandRulesRotateMirror.Rotate(Point(1000, 0, 0, 0.5), new PlanRotation(0, 0, Math.PI / 2));
        Check(NearAll(column.A, 0, 1000, 0) && column.RotationRad is { } turned && Near(turned, 0.5 + Math.PI / 2, 1e-12), "a column's location point turns and its rotation grows by the angle");
        Check(CommandRulesRotateMirror.Rotate(Point(1000, 0, 0, null), new PlanRotation(0, 0, Math.PI / 2)).RotationRad is null, "a point without a readable rotation keeps none");
        // A floor rectangle about the origin by 90: (0,0) (6000,0) (6000,4000) (0,4000) -> (0,0) (0,6000) (-4000,6000) (-4000,0).
        var floor = CommandRulesRotateMirror.Rotate(Profile(new double[] { 0, 0, 0 }, new double[] { 6000, 0, 0 }, new double[] { 6000, 4000, 0 }, new double[] { 0, 4000, 0 }), new PlanRotation(0, 0, Math.PI / 2));
        Check(floor.Points is { Count: 4 } p && NearAll(p[0], 0, 0, 0) && NearAll(p[1], 0, 6000, 0) && NearAll(p[2], -4000, 6000, 0) && NearAll(p[3], -4000, 0, 0) && NearAll(floor.A, 0, 0, 0),
            "every sketch profile point turns, in order");
    }

    static void MirrorGeometry()
    {
        double[] Reflect(double[] p, double ax, double ay, double bx, double by) => CommandRulesRotateMirror.MirrorPoint(p, new PlanMirror(ax, ay, bx, by));
        // The plane through x = 0: (1000, 500, 300) -> (-1000, 500, 300).
        Check(NearAll(Reflect(new double[] { 1000, 500, 300 }, 0, 0, 0, 1000), -1000, 500, 300), "(1000, 500, 300) mirrored in x = 0 is (-1000, 500, 300)");
        // The plane through y = 2000: (1500, 500, 0) -> (1500, 3500, 0).
        Check(NearAll(Reflect(new double[] { 1500, 500, 0 }, 0, 2000, 5000, 2000), 1500, 3500, 0), "(1500, 500) mirrored in y = 2000 is (1500, 3500)");
        // The diagonal y = x: (3000, 1000) -> (1000, 3000); the line direction does not matter.
        Check(NearAll(Reflect(new double[] { 3000, 1000, 0 }, 0, 0, 1000, 1000), 1000, 3000, 0) && NearAll(Reflect(new double[] { 3000, 1000, 0 }, 1000, 1000, 0, 0), 1000, 3000, 0),
            "(3000, 1000) mirrored in y = x is (1000, 3000), whichever way the axis is drawn");
        // Axis (0,0)->(3000,4000), unit (0.6, 0.8); P = (5000, 0): projection 3000, foot (1800, 2400); P' = 2 foot - P = (-1400, 4800).
        Check(NearAll(Reflect(new double[] { 5000, 0, 0 }, 0, 0, 3000, 4000), -1400, 4800, 0), "(5000, 0) mirrored in the line through (0, 0) and (3000, 4000) is (-1400, 4800)");
        // A point on the axis stays.
        Check(NearAll(Reflect(new double[] { 1800, 2400, 700 }, 0, 0, 3000, 4000), 1800, 2400, 700), "a point on the mirror line stays where it is");
        // A wall (0,0)-(6000,0) in x = 1000: start (2000, 0), end (-4000, 0).
        var wall = CommandRulesRotateMirror.Mirror(Curve(0, 0, 0, 6000, 0, 0), new PlanMirror(1000, 0, 1000, 10));
        Check(wall.Kind == TransformLocationKinds.Curve && NearAll(wall.A, 2000, 0, 0) && wall.B is { } b && NearAll(b, -4000, 0, 0), "a wall mirrored in x = 1000 runs from (2000, 0) to (-4000, 0)");
        // A mirrored instance is a reflection: its rotation is not predicted (reported, not checked).
        Check(CommandRulesRotateMirror.Mirror(Point(1000, 0, 0, 0.5), new PlanMirror(0, 0, 0, 1)) is { RotationRad: null } col && NearAll(col.A, -1000, 0, 0), "a mirrored point has no expected rotation");
    }

    static void RotateReadback()
    {
        var quarter = new PlanRotation(0, 0, Math.PI / 2);
        string? Rotated(TransformLocation before, TransformLocation? after, PlanRotation? rotation = null) =>
            Safe("rotate readback", () => CommandReadbackRotateMirror.Rotated(new[] { WallUid }, new[] { before }, new[] { after }, rotation ?? quarter) ?? "") is { } result && result.Length == 0 ? null : "mismatch";
        // A wall (1000,0)-(6000,0) about the origin by 90: (0, 1000)-(0, 6000).
        Check(Rotated(Curve(1000, 0, 0, 6000, 0, 0), Curve(0, 1000, 0, 0, 6000, 0)) is null, "a wall turned 90 degrees about the origin matches");
        // Off by (0.5, 0.5, 0.5): 0.87 mm; off by (0.8, 0.8, 0): 1.13 mm.
        Check(Rotated(Curve(1000, 0, 0, 6000, 0, 0), Curve(0.5, 1000.5, 0.5, 0, 6000, 0)) is null, "a curve end within 0.87 mm matches");
        Check(Rotated(Curve(1000, 0, 0, 6000, 0, 0), Curve(0.8, 1000.8, 0, 0, 6000, 0)) is not null, "a curve end 1.13 mm off is a mismatch");
        Check(Rotated(Curve(1000, 0, 0, 6000, 0, 0), Curve(1000, 0, 0, 6000, 0, 0)) is not null, "a wall that did not turn is a mismatch");
        Check(Rotated(Curve(1000, 0, 0, 6000, 0, 0), Curve(0, -1000, 0, 0, -6000, 0)) is not null, "a wall turned the other way (clockwise) is a mismatch");
        Check(Rotated(Curve(1000, 0, 0, 6000, 0, 0), Curve(0, 6000, 0, 0, 1000, 0)) is not null, "a rotated wall whose curve is reversed is a mismatch (a rotation is rigid)");
        Check(Rotated(Point(1000, 0, 0, 0.5), Point(0, 1000, 0, 0.5 + Math.PI / 2)) is null, "a column at its turned point with its rotation grown by 90 degrees matches");
        Check(Rotated(Point(1000, 0, 0, 0.5), Point(0, 1000, 0, 0.5)) is not null, "a column moved to its turned point but not turned itself is a mismatch");
        Check(Rotated(Point(1000, 0, 0, 0.5), Point(0, 1000, 0, 0.5 + Math.PI / 2 - 2 * Math.PI)) is null, "rotations compare modulo a full turn");
        Check(Rotated(Point(1000, 0, 0, 0.5), Point(0, 1000, 0, null)) is not null, "a rotation no longer readable is a mismatch");
        Check(Rotated(Point(1000, 0, 0, 0.5), null) is not null, "an element without a readable location afterwards is a mismatch");
        Check(Rotated(Point(1000, 0, 0, 0.5), Curve(0, 1000, 0, 0, 2000, 0)) is not null, "a location of another kind is a mismatch");
        var floor = Profile(new double[] { 0, 0, 0 }, new double[] { 6000, 0, 0 }, new double[] { 6000, 4000, 0 }, new double[] { 0, 4000, 0 });
        Check(Rotated(floor, Profile(new double[] { 0, 0, 0 }, new double[] { 0, 6000, 0 }, new double[] { -4000, 6000, 0 }, new double[] { -4000, 0, 0 })) is null, "a floor profile turned 90 degrees matches");
        Check(Rotated(floor, Profile(new double[] { 0, 0, 0 }, new double[] { 0, 6000, 0 }, new double[] { -4002, 6000, 0 }, new double[] { -4000, 0, 0 })) is not null, "one turned profile corner 2 mm off is a mismatch");
        var second = Safe("rotate readback of two", () => CommandReadbackRotateMirror.Rotated(new[] { WallUid, ColumnUid }, new[] { Point(1000, 0, 0), Point(0, 2000, 0) }, new TransformLocation?[] { Point(0, 1000, 0, Math.PI / 2), Point(0, 2000, 0) }, quarter) ?? "");
        Check(second is { Length: > 0 } named && named.Contains(ColumnUid, StringComparison.Ordinal), "every target is checked: the second one not turning is a mismatch naming it", second ?? "");
    }

    // Axis y = 2000 drawn west to east: (x, y) -> (x, 4000 - y).
    static readonly PlanMirror North = new(0, 2000, 1000, 2000);

    static void MirrorCopies()
    {
        var sources = new[] { new TransformCandidate(WallUid, WallKey, Curve(0, 0, 0, 6000, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(6000, 500, 0, 0.3)) };
        var unchanged = sources.Select(source => (TransformCandidate?)source).ToArray();
        // Expected: wall (0, 4000)-(6000, 4000) (Revit may report it either way round), column (6000, 3500). Revit lists the
        // new ids in its own order, with a mirrored copy of the wall's door the request did not name.
        var created = new[] { new TransformCandidate(DoorCopyUid, DoorKey, Point(1500, 4000, 0)), new TransformCandidate(ColumnCopyUid, ColumnKey, Point(6000, 3500, 0, 2.1)), new TransformCandidate(WallCopyUid, WallKey, Curve(6000, 4000, 0, 0, 4000, 0)) };
        MirrorMapping? Map(IReadOnlyList<TransformCandidate?> after, IReadOnlyList<TransformCandidate> news, IReadOnlyList<TransformCandidate>? from = null) =>
            Safe("mirror mapping", () => CommandRulesRotateMirror.MapMirrored(from ?? sources, after, news, North, true));
        var mapping = Map(unchanged, created);
        Check(mapping is { Mismatch: null } && mapping.Results.SequenceEqual(new[] { new MirrorResult(WallUid, WallCopyUid, true), new MirrorResult(ColumnUid, ColumnCopyUid, true) }) && mapping.AlsoCreated.SequenceEqual(new[] { DoorCopyUid }),
            "mirrored copies map to their sources in request order (a reversed wall curve included); the extra door copy is also_created", mapping?.ToString() ?? "");
        Check(mapping is not null && CommandRulesRotateMirror.MirrorCreated(mapping).SequenceEqual(new[] { WallCopyUid, ColumnCopyUid, DoorCopyUid }) && CommandRulesRotateMirror.MirrorModified(mapping).Count == 0,
            "created lists the copies in request order then the extra copies; nothing is modified when the originals are kept");
        var moved = Map(new TransformCandidate?[] { unchanged[0]! with { Location = Curve(0, 4000, 0, 6000, 4000, 0) }, unchanged[1] }, created);
        Check(moved?.Mismatch is { } source && source.Contains(WallUid, StringComparison.Ordinal), "a kept original that moved is a mismatch naming it", moved?.ToString() ?? "");
        Check(Map(new TransformCandidate?[] { null, unchanged[1] }, created)?.Mismatch is { } gone && gone.Contains(WallUid, StringComparison.Ordinal), "a kept original that is gone is a mismatch");
        var off = Map(unchanged, new[] { created[1], created[2] with { Location = Curve(6000, 4002, 0, 0, 4002, 0) } });
        Check(off?.Mismatch is { } offWall && offWall.Contains(WallUid, StringComparison.Ordinal), "a copy 2 mm off its mirrored location maps to no source: a mismatch naming the source", off?.ToString() ?? "");
        // A copy left in place (not mirrored) at (6000, 500) is not the column's mirror image.
        Check(Map(unchanged, new[] { created[2], created[1] with { Location = Point(6000, 500, 0) } })?.Mismatch is { } same && same.Contains(ColumnUid, StringComparison.Ordinal), "a copy that was not mirrored is a mismatch");
        Check(Map(unchanged, new[] { created[1], created[2] with { Key = "Wall|-2000011|type-300" } })?.Mismatch is not null, "a copy of another type maps to no source");
        Check(Map(unchanged, new[] { created[1] })?.Mismatch is { } absent && absent.Contains(WallUid, StringComparison.Ordinal), "a source without a copy is a mismatch");
        // Revit returning a source's own id among the new ones does not make it its own copy.
        Check(Map(unchanged, new[] { created[1], new TransformCandidate(WallUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)) })?.Mismatch is not null, "a source's own id is never taken as its copy");
        // Two identical columns at the same place get two distinct mirrored copies.
        var twins = new[] { new TransformCandidate(WallUid, ColumnKey, Point(0, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(0, 0, 0)) };
        var twinMapping = Map(twins.Select(twin => (TransformCandidate?)twin).ToArray(), new[] { new TransformCandidate(WallCopyUid, ColumnKey, Point(0, 4000, 0)), new TransformCandidate(ColumnCopyUid, ColumnKey, Point(0, 4000, 0)) }, twins);
        Check(twinMapping is { Mismatch: null } && twinMapping.Results.Select(result => result.UniqueId).Distinct().Count() == 2 && twinMapping.AlsoCreated.Count == 0, "identical sources map to distinct copies", twinMapping?.ToString() ?? "");
        // Close sources (0.9 mm apart) whose copies come back in another order still pair exactly (nearest first, then
        // augmenting paths, as element.copy). Mirrored in y = 2000: (x, 0) -> (x, 4000).
        var close = new[] { new TransformCandidate(WallUid, ColumnKey, Point(0, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(0.9, 0, 0)), new TransformCandidate(FloorUid, ColumnKey, Point(1.8, 0, 0)) };
        var closeCopies = new[] { new TransformCandidate(ColumnCopyUid, ColumnKey, Point(0.9, 4000, 0)), new TransformCandidate(FloorCopyUid, ColumnKey, Point(1.8, 4000, 0)), new TransformCandidate(WallCopyUid, ColumnKey, Point(0, 4000, 0)) };
        var closeMapping = Map(close.Select(c => (TransformCandidate?)c).ToArray(), closeCopies, close);
        Check(closeMapping is { Mismatch: null } && closeMapping.Results.Select(result => result.UniqueId).SequenceEqual(new[] { WallCopyUid, ColumnCopyUid, FloorCopyUid }), "close sources map nearest first to their exact mirrored copies", closeMapping?.ToString() ?? "");
        Check(Safe("mirror mapping without a source location", () => CommandRulesRotateMirror.MapMirrored(new[] { sources[0] with { Location = null } }, new TransformCandidate?[] { unchanged[0] }, created, North, true))?.Mismatch is not null, "a source without a location cannot be mapped");
    }

    static void MirrorInPlace()
    {
        var sources = new[] { new TransformCandidate(WallUid, WallKey, Curve(0, 0, 0, 6000, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(6000, 500, 0, 0.3)) };
        MirrorMapping? Map(IReadOnlyList<TransformCandidate?> after, IReadOnlyList<TransformCandidate> news) => Safe("in-place mirror mapping", () => CommandRulesRotateMirror.MapMirrored(sources, after, news, North, false));
        // Revit kept both elements and mirrored them: wall (0, 4000)-(6000, 4000), column (6000, 3500).
        var inPlace = Map(new TransformCandidate?[] { sources[0] with { Location = Curve(0, 4000, 0, 6000, 4000, 0) }, sources[1] with { Location = Point(6000, 3500, 0, -0.3) } }, Array.Empty<TransformCandidate>());
        Check(inPlace is { Mismatch: null } && inPlace.Results.SequenceEqual(new[] { new MirrorResult(WallUid, WallUid, false), new MirrorResult(ColumnUid, ColumnUid, false) }) && inPlace.AlsoCreated.Count == 0,
            "originals mirrored in place map to themselves", inPlace?.ToString() ?? "");
        Check(inPlace is not null && CommandRulesRotateMirror.MirrorModified(inPlace).SequenceEqual(new[] { WallUid, ColumnUid }) && CommandRulesRotateMirror.MirrorCreated(inPlace).Count == 0, "originals mirrored in place are modified, nothing is created");
        // Revit returning the originals' own ids does not create anything.
        var echoed = Map(new TransformCandidate?[] { sources[0] with { Location = Curve(6000, 4000, 0, 0, 4000, 0) }, sources[1] with { Location = Point(6000, 3500, 0) } }, new[] { new TransformCandidate(WallUid.ToUpperInvariant(), WallKey, Curve(6000, 4000, 0, 0, 4000, 0)) });
        Check(echoed is { Mismatch: null } && echoed.AlsoCreated.Count == 0 && echoed.Results.All(result => !result.IsNew), "an original's own id returned by Revit is not a new element", echoed?.ToString() ?? "");
        Check(Map(new TransformCandidate?[] { sources[0], sources[1] with { Location = Point(6000, 3500, 0) } }, Array.Empty<TransformCandidate>())?.Mismatch is { } still && still.Contains(WallUid, StringComparison.Ordinal),
            "an original left where it was is a mismatch naming it");
        // Revit replaced the wall by a mirrored new element and deleted the original: the replacement is mapped.
        var replaced = Map(new TransformCandidate?[] { null, sources[1] with { Location = Point(6000, 3500, 0) } }, new[] { new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)), new TransformCandidate(DoorCopyUid, DoorKey, Point(1500, 4000, 0)) });
        Check(replaced is { Mismatch: null } && replaced.Results.SequenceEqual(new[] { new MirrorResult(WallUid, WallCopyUid, true), new MirrorResult(ColumnUid, ColumnUid, false) }) && replaced.AlsoCreated.SequenceEqual(new[] { DoorCopyUid }),
            "an original Revit replaced maps to its mirrored replacement; other new elements are also_created", replaced?.ToString() ?? "");
        Check(replaced is not null && CommandRulesRotateMirror.MirrorCreated(replaced).SequenceEqual(new[] { WallCopyUid, DoorCopyUid }) && CommandRulesRotateMirror.MirrorModified(replaced).SequenceEqual(new[] { ColumnUid }),
            "created lists replacements then extras; modified lists the originals mirrored in place");
        Check(Map(new TransformCandidate?[] { null, sources[1] with { Location = Point(6000, 3500, 0) } }, Array.Empty<TransformCandidate>())?.Mismatch is { } lost && lost.Contains(WallUid, StringComparison.Ordinal), "an original that is gone without a mirrored replacement is a mismatch");
        Check(Map(new TransformCandidate?[] { sources[0] with { Location = null }, sources[1] with { Location = Point(6000, 3500, 0) } }, Array.Empty<TransformCandidate>())?.Mismatch is { } unreadable && unreadable.Contains(WallUid, StringComparison.Ordinal),
            "an original that still exists without a readable location is a mismatch");
    }

    static void MirrorProfiles()
    {
        // A floor rectangle mirrored in x = -1000 (x -> -2000 - x): (0,0) (6000,0) (6000,4000) (0,4000) ->
        // (-2000,0) (-8000,0) (-8000,4000) (-2000,4000). A mirrored sketch may come back in another order.
        var floor = Profile(new double[] { 0, 0, 0 }, new double[] { 6000, 0, 0 }, new double[] { 6000, 4000, 0 }, new double[] { 0, 4000, 0 });
        var axis = new PlanMirror(-1000, 0, -1000, 5000);
        var expected = CommandRulesRotateMirror.Mirror(floor, axis);
        Check(expected.Points is { Count: 4 } p && NearAll(p[0], -2000, 0, 0) && NearAll(p[1], -8000, 0, 0) && NearAll(p[2], -8000, 4000, 0) && NearAll(p[3], -2000, 4000, 0), "every profile point is mirrored");
        string? Compare(TransformLocation? observed) => CommandRulesRotateMirror.CompareMirrored(expected, observed, "floor " + FloorUid);
        Check(Compare(Profile(new double[] { -2000, 0, 0 }, new double[] { -8000, 0, 0 }, new double[] { -8000, 4000, 0 }, new double[] { -2000, 4000, 0 })) is null, "the mirrored profile in order matches");
        Check(Compare(Profile(new double[] { -2000, 4000, 0 }, new double[] { -8000, 4000, 0 }, new double[] { -8000, 0, 0 }, new double[] { -2000, 0, 0 })) is null, "the mirrored profile in another order matches");
        Check(Compare(Profile(new double[] { -2000, 0, 0 }, new double[] { -8000, 0, 0 }, new double[] { -8000, 4002, 0 }, new double[] { -2000, 4000, 0 })) is { } corner && corner.Contains(FloorUid, StringComparison.Ordinal), "one mirrored corner 2 mm off is a mismatch naming the element");
        Check(Compare(Profile(new double[] { -2000, 0, 0 }, new double[] { -8000, 0, 0 }, new double[] { -8000, 4000, 0 })) is not null, "a mirrored profile with another number of points is a mismatch");
        Check(Compare(floor) is not null, "a profile left where it was is a mismatch");
        Check(Compare(Box(-8000, 0, 0, -2000, 4000, 200)) is not null, "a box where a profile was read is a mismatch");
        Check(Compare(null) is { } none && none.Contains("no longer", StringComparison.Ordinal), "no readable location is a mismatch");
        // Curves: either orientation of the mirrored ends; points: position only.
        var wall = CommandRulesRotateMirror.Mirror(Curve(0, 0, 0, 6000, 0, 0), North);
        Check(CommandRulesRotateMirror.CompareMirrored(wall, Curve(0, 4000, 0, 6000, 4000, 0), "wall") is null && CommandRulesRotateMirror.CompareMirrored(wall, Curve(6000, 4000, 0, 0, 4000, 0), "wall") is null,
            "a mirrored wall matches in either direction");
        Check(CommandRulesRotateMirror.CompareMirrored(wall, Curve(0, 4000, 0, 6000, 4001.5, 0), "wall") is not null, "a mirrored wall end 1.5 mm off is a mismatch");
        Check(CommandRulesRotateMirror.CompareMirrored(CommandRulesRotateMirror.Mirror(Point(6000, 500, 0, 0.3), North), Point(6000, 3500, 0.5, 2.9), "column") is null, "a mirrored column matches by position within 1 mm, whatever rotation Revit reports");
    }

    static void Json()
    {
        var quarter = new PlanRotation(1000, -500, Math.PI / 2);
        var rotated = JsonNode.Parse(Safe("rotate json", () => CommandReadbackRotateMirror.RotateJson(new[] { ColumnUid }, new[] { Point(2000, -500, 0, 0) }, new TransformLocation?[] { Point(1000, 500, 0, Math.PI / 2) }, quarter)) ?? "{}");
        var first = rotated?["rotated"]?[0];
        Check(rotated?["center_mm"]?.AsArray().Select(n => n?.GetValue<double>() ?? double.NaN).SequenceEqual(new double[] { 1000, -500 }) == true && Near(rotated?["angle_deg"]?.GetValue<double>() ?? double.NaN, 90, 1e-9)
            && first?["unique_id"]?.GetValue<string>() == ColumnUid && first?["before"]?["point"]?[0]?.GetValue<double>() == 2000 && first?["after"]?["point"]?[1]?.GetValue<double>() == 500
            && Near(first?["after"]?["rotation_deg"]?.GetValue<double>() ?? double.NaN, 90, 1e-9),
            "rotate readback JSON lists the centre, the angle in degrees and each element's location before and after", rotated?.ToJsonString() ?? "");
        var wallJson = JsonNode.Parse(Safe("rotate json curve", () => CommandReadbackRotateMirror.RotateJson(new[] { WallUid }, new[] { Curve(1000, 0, 0, 6000, 0, 0) }, new TransformLocation?[] { Curve(0, 1000, 0, 0, 6000, 0) }, new PlanRotation(0, 0, Math.PI / 2))) ?? "{}");
        Check(wallJson?["rotated"]?[0]?["after"]?["kind"]?.GetValue<string>() == "curve" && wallJson?["rotated"]?[0]?["after"]?["end"]?[1]?.GetValue<double>() == 6000, "a curve location is written as start and end", wallJson?.ToJsonString() ?? "");

        var sources = new[] { new TransformCandidate(WallUid, WallKey, Curve(0, 0, 0, 6000, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(6000, 500, 0, 0.3)) };
        var after = new TransformCandidate?[] { null, sources[1] with { Location = Point(6000, 3500, 0, -0.3) } };
        var created = new[] { new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)), new TransformCandidate(DoorCopyUid, DoorKey, Point(1500, 4000, 0)) };
        var mapping = new MirrorMapping(new[] { new MirrorResult(WallUid, WallCopyUid, true), new MirrorResult(ColumnUid, ColumnUid, false) }, new[] { DoorCopyUid }, null);
        var mirrored = JsonNode.Parse(Safe("mirror json", () => CommandReadbackRotateMirror.MirrorJson(mapping, sources, after, created, North, false)) ?? "{}");
        var wall = mirrored?["mirrored"]?[0];
        var column = mirrored?["mirrored"]?[1];
        Check(mirrored?["axis"]?["start"]?.AsArray().Select(n => n?.GetValue<double>() ?? double.NaN).SequenceEqual(new double[] { 0, 2000 }) == true && mirrored?["axis"]?["end"]?[0]?.GetValue<double>() == 1000
            && mirrored?["keep_original"]?.GetValue<bool>() == false
            && wall?["source_unique_id"]?.GetValue<string>() == WallUid && wall?["unique_id"]?.GetValue<string>() == WallCopyUid && wall?["new"]?.GetValue<bool>() == true
            && wall?["before"]?["start"]?[1]?.GetValue<double>() == 0 && wall?["after"]?["start"]?[1]?.GetValue<double>() == 4000
            && column?["unique_id"]?.GetValue<string>() == ColumnUid && column?["new"]?.GetValue<bool>() == false && column?["after"]?["point"]?[1]?.GetValue<double>() == 3500
            && mirrored?["also_created"]?.AsArray().Select(n => n?.GetValue<string>()).SequenceEqual(new[] { DoorCopyUid }) == true,
            "mirror readback JSON lists the axis, keep_original, each source with its result (new or the original), locations before and after, and also_created", mirrored?.ToJsonString() ?? "");
    }

    // Review SF1: a turn that moves a target by at most 1 mm and changes no readable rotation could pass unturned.
    static void SmallTurns()
    {
        var tiny = new PlanRotation(0, 0, 0.01 * Math.PI / 180, 0.01);
        var wall = Curve(-3000, 0, 0, 3000, 0, 0);
        // Each end moves 2 * 3000 * sin(0.005 degrees) = 0.5236 mm <= 1 mm: an unturned wall would pass a 1 mm readback.
        var unturned = Safe("small-turn readback", () => CommandReadbackRotateMirror.Rotated(new[] { WallUid }, new[] { wall }, new TransformLocation?[] { wall }, tiny) ?? "");
        Check(unturned is { Length: > 0 } why && why.Contains(WallUid, StringComparison.Ordinal), "a 0.01 degree turn of a 6 m wall about its middle, read back unturned, does not pass", unturned ?? "");
        string? Refuse(TransformLocation location, PlanRotation rotation) =>
            Thrown(() => CommandRulesRotateMirror.CheckRotateTarget(Facts(location), new[] { WallUid }, rotation, "args.element_uids[2]")) is { } error ? error.Code + "@" + error.Field + "|" + error.Detail : null;
        Check(Refuse(wall, tiny) is { } small && small.StartsWith("angle_too_small_for_target@args.element_uids[2]|", StringComparison.Ordinal) && small.Contains("0.524", StringComparison.Ordinal),
            "the same target is refused angle_too_small_for_target before any write, stating the 0.524 mm largest move", Refuse(wall, tiny) ?? "");
        // 0.1 degrees: 2 * 3000 * sin(0.05 degrees) = 5.236 mm > 1 mm.
        Check(Refuse(wall, new PlanRotation(0, 0, 0.1 * Math.PI / 180, 0.1)) is null, "a 0.1 degree turn of the same wall moves its ends 5.2 mm and is accepted");
        // 0.01 degrees about a centre 10 km away: 2 * 1e7 * sin(0.005 degrees) = 1745 mm.
        Check(Refuse(wall, new PlanRotation(0, 10_000_000, 0.01 * Math.PI / 180, 0.01)) is null, "a 0.01 degree turn about a far centre is accepted");
        // A column on the axis does not move, but its readable rotation changes by the angle; without one it is refused.
        Check(Refuse(Point(0, 0, 0, 0.3), tiny) is null, "a column on the axis with a readable rotation can be turned (its rotation is read back)");
        Check(Refuse(Point(0, 0, 0, null), new PlanRotation(0, 0, Math.PI / 2, 90))?.StartsWith("angle_too_small_for_target@", StringComparison.Ordinal) == true, "a point on the axis without a readable rotation is refused even by 90 degrees");
        var sets = Thrown(() => CommandRulesRotateMirror.CheckRotateTargets(new[] { Facts(Curve(0, 0, 0, 6000, 0, 0)), new TransformTargetFacts(ColumnUid, false, false, false, false, false, Curve(-3000, 0, 0, 3000, 0, 0)) }, tiny));
        Check(sets is { Code: RevitRotateMirrorCodes.AngleTooSmallForTarget, Field: "args.element_uids[1]" }, "every target is checked in request order: the second one, moving 0.52 mm, is refused at its index", sets?.Code + " " + sets?.Field);
        Check(Thrown(() => CommandRulesRotateMirror.CheckRotateTargets(new[] { Facts(Curve(0, 0, 0, 6000, 0, 0)) }, tiny)) is null, "a wall turned 0.01 degrees about its end (the far end moves 1.05 mm) is accepted");
    }

    static TransformLocation Flagged(double x, double y, double z, bool mirrored) => new(TransformLocationKinds.Point, new[] { x, y, z }, null, 0.3, null, mirrored);

    // Review SF2: a point-located instance on the mirror line sits at its own mirror image; only its orientation
    // (FamilyInstance.Mirrored) shows whether it was mirrored.
    static void MirrorFlags()
    {
        var onAxis = new PlanMirror(0, 0, 0, 1);
        var sources = new[] { new TransformCandidate(ColumnUid, ColumnKey, Point(0, 1000, 0, 0.3)) };
        var same = Safe("unflagged on-axis mirror", () => CommandRulesRotateMirror.MapMirrored(sources, new TransformCandidate?[] { sources[0] }, Array.Empty<TransformCandidate>(), onAxis, false));
        Check(same?.Mismatch is { } unseen && unseen.Contains(ColumnUid, StringComparison.Ordinal), "a column on the mirror line read back unchanged, with no mirrored flag, is a mismatch", same?.ToString() ?? "");
        var flagged = new[] { new TransformCandidate(ColumnUid, ColumnKey, Flagged(0, 1000, 0, false)) };
        var toggled = Safe("toggled on-axis mirror", () => CommandRulesRotateMirror.MapMirrored(flagged, new TransformCandidate?[] { flagged[0] with { Location = Flagged(0, 1000, 0, true) } }, Array.Empty<TransformCandidate>(), onAxis, false));
        Check(toggled is { Mismatch: null } && toggled.Results.SequenceEqual(new[] { new MirrorResult(ColumnUid, ColumnUid, false) }), "a column on the line whose mirrored flag toggled matches", toggled?.ToString() ?? "");
        var stuck = Safe("untoggled mirror", () => CommandRulesRotateMirror.MapMirrored(flagged, new TransformCandidate?[] { flagged[0] }, Array.Empty<TransformCandidate>(), onAxis, false));
        Check(stuck?.Mismatch is { } flag && flag.Contains("mirrored flag", StringComparison.Ordinal), "a column on the line whose mirrored flag did not toggle is a mismatch", stuck?.ToString() ?? "");
        // Off the line: (2000, 0) -> (-2000, 0); right place but flag not toggled is still a mismatch.
        var off = new[] { new TransformCandidate(ColumnUid, ColumnKey, Flagged(2000, 0, 0, true)) };
        Check(Safe("off-axis untoggled", () => CommandRulesRotateMirror.MapMirrored(off, new TransformCandidate?[] { off[0] with { Location = Flagged(-2000, 0, 0, true) } }, Array.Empty<TransformCandidate>(), onAxis, false))?.Mismatch is not null,
            "a column at its mirror image whose flag stayed true is a mismatch");
        Check(Safe("off-axis toggled", () => CommandRulesRotateMirror.MapMirrored(off, new TransformCandidate?[] { off[0] with { Location = Flagged(-2000, 0, 0, false) } }, Array.Empty<TransformCandidate>(), onAxis, false))?.Mismatch is null,
            "a column at its mirror image whose flag toggled to false matches");
        Check(Safe("flag unreadable after", () => CommandRulesRotateMirror.MapMirrored(off, new TransformCandidate?[] { off[0] with { Location = Point(-2000, 0, 0) } }, Array.Empty<TransformCandidate>(), onAxis, false))?.Mismatch is not null,
            "a mirrored flag read before but unreadable after is a mismatch");
        // Copies: a copy whose flag equals its source's is not its mirror image.
        var keep = Safe("copy flag", () => CommandRulesRotateMirror.MapMirrored(flagged, new TransformCandidate?[] { flagged[0] }, new[] { new TransformCandidate(ColumnCopyUid, ColumnKey, Flagged(0, 1000, 0, false)) }, onAxis, true));
        Check(keep?.Mismatch is not null, "a copy on the line with its source's mirrored flag maps to no source", keep?.ToString() ?? "");
        var copy = Safe("copy flag toggled", () => CommandRulesRotateMirror.MapMirrored(flagged, new TransformCandidate?[] { flagged[0] }, new[] { new TransformCandidate(ColumnCopyUid, ColumnKey, Flagged(0, 1000, 0, true)) }, onAxis, true));
        Check(copy is { Mismatch: null } && copy.Results.SequenceEqual(new[] { new MirrorResult(ColumnUid, ColumnCopyUid, true) }), "a copy on the line with the toggled flag is the source's mirrored copy", copy?.ToString() ?? "");
        Check(CommandRulesRotateMirror.Mirror(Flagged(2000, 0, 0, true), onAxis).Mirrored == false && CommandRulesRotateMirror.Mirror(Point(2000, 0, 0), onAxis).Mirrored is null, "the expected mirror image toggles a read flag and expects none when none was read");
        // Refused before any write: keep_original false on the line without a flag; a flag, keep_original true or another line are fine.
        string? Refuse(TransformLocation location, bool keepOriginal, PlanMirror mirror) =>
            Thrown(() => CommandRulesRotateMirror.CheckMirrorTarget(new TransformTargetFacts(ColumnUid, false, false, false, false, false, location), true, new[] { ColumnUid }, mirror, keepOriginal, "args.element_uids[1]")) is { } error ? error.Code + "@" + error.Field : null;
        Check(Refuse(Point(0, 1000, 0, 0.3), false, onAxis) == "mirror_image_unobservable@args.element_uids[1]", "a column on the line without a mirrored flag is mirror_image_unobservable with keep_original false");
        Check(Refuse(Flagged(0, 1000, 0, false), false, onAxis) is null && Refuse(Point(0, 1000, 0, 0.3), true, onAxis) is null && Refuse(Point(0, 1000, 0, 0.3), false, new PlanMirror(5000, 0, 5000, 1)) is null,
            "with a flag, with keep_original true, or mirrored in another line, it is accepted");
        // A floor symmetric about the line x = 3000 is its own mirror image as a set of points.
        var symmetric = Profile(new double[] { 0, 0, 0 }, new double[] { 6000, 0, 0 }, new double[] { 6000, 4000, 0 }, new double[] { 0, 4000, 0 });
        Check(Refuse(symmetric, false, new PlanMirror(3000, 0, 3000, 1)) == "mirror_image_unobservable@args.element_uids[1]", "a floor symmetric about the mirror line is mirror_image_unobservable with keep_original false");
        Check(Refuse(Curve(0, 2000, 0, 6000, 2000, 0), false, new PlanMirror(0, 2000, 1000, 2000)) == "mirror_image_unobservable@args.element_uids[1]", "a wall lying on the mirror line is mirror_image_unobservable with keep_original false");
        var json = JsonNode.Parse(Safe("mirror json flag", () => CommandReadbackRotateMirror.MirrorJson(toggled ?? new MirrorMapping(Array.Empty<MirrorResult>(), Array.Empty<string>(), null), flagged, new TransformCandidate?[] { flagged[0] with { Location = Flagged(0, 1000, 0, true) } }, Array.Empty<TransformCandidate>(), onAxis, false)) ?? "{}");
        Check(json?["mirrored"]?[0]?["before"]?["mirrored"]?.GetValue<bool>() == false && json?["mirrored"]?[0]?["after"]?["mirrored"]?.GetValue<bool>() == true, "the readback reports the mirrored flag before and after", json?.ToJsonString() ?? "");
    }

    // Review SF3: originals Revit replaced are reported deleted, so a batch does not blame the step that created them.
    static void Replacements()
    {
        var sources = new[] { new TransformCandidate(WallUid, WallKey, Curve(0, 0, 0, 6000, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(6000, 500, 0, 0.3)) };
        var replaced = Safe("replaced mapping", () => CommandRulesRotateMirror.MapMirrored(sources, new TransformCandidate?[] { null, sources[1] with { Location = Point(6000, 3500, 0) } }, new[] { new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)) }, North, false));
        Check(replaced is { Mismatch: null } && CommandRulesRotateMirror.MirrorDeleted(replaced).SequenceEqual(new[] { WallUid }), "an original Revit replaced is deleted", replaced?.ToString() ?? "");
        var copies = Safe("copy mapping deleted", () => CommandRulesRotateMirror.MapMirrored(sources, sources.Select(source => (TransformCandidate?)source).ToArray(), new[] { new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)), new TransformCandidate(ColumnCopyUid, ColumnKey, Point(6000, 3500, 0)) }, North, true));
        Check(copies is { Mismatch: null } && CommandRulesRotateMirror.MirrorDeleted(copies).Count == 0, "mirrored copies delete nothing", copies?.ToString() ?? "");
        if (replaced is not null)
        {
            var json = JsonNode.Parse(Safe("mirror json deleted", () => CommandReadbackRotateMirror.MirrorJson(replaced, sources, new TransformCandidate?[] { null, sources[1] with { Location = Point(6000, 3500, 0) } }, new[] { new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)) }, North, false)) ?? "{}");
            Check(json?["deleted"]?.AsArray().Select(n => n?.GetValue<string>()).SequenceEqual(new[] { WallUid }) == true, "the mirror readback lists the deleted originals", json?.ToJsonString() ?? "");
        }
        // The reply carries them, like element.delete.
        try
        {
            var envelope = RevitCommandProtocol.Parse(Envelope("element.mirror", MirrorArgs(Uids(WallUid), Xy(0, 2000), Xy(1000, 2000), false)).ToJsonString());
            var reply = CommandResponses.Success(new CommandRequest(envelope, envelope.RequestSha256), new[] { WallCopyUid }, Array.Empty<string>(), "{}", Array.Empty<string>(), replaced is null ? Array.Empty<string>() : CommandRulesRotateMirror.MirrorDeleted(replaced));
            var parsed = RevitCommandResponse.Parse(reply.ToJson());
            Check(parsed.Deleted.SequenceEqual(new[] { WallUid }) && parsed.Created.SequenceEqual(new[] { WallCopyUid }), "the element.mirror reply lists the replacement as created and the original as deleted", reply.ToJson());
            Check(CommandResponses.Success(new CommandRequest(envelope, envelope.RequestSha256), Array.Empty<string>(), Array.Empty<string>(), "{}", Array.Empty<string>()).Deleted.Count == 0, "other writes still delete nothing by default");
        }
        catch (Exception error) { Check(false, "element.mirror reply with deleted", error.GetType().Name + ": " + error.Message); }
        // A batch: step 0 copies a wall (creating X), step 1 mirrors X without keeping it and Revit replaces it by Y.
        string X = Numbered(0x7001), Y = Numbered(0x7002);
        var batch = RevitCommandProtocol.Parse(Envelope("command.batch", new JsonObject { ["steps"] = new JsonArray(
            Step("s-0", "element.copy", new JsonObject { ["element_uids"] = Uids(WallUid), ["vector_mm"] = new JsonArray(0, 5000, 0) }),
            Step("s-1", "element.mirror", MirrorArgs(Uids(X), Xy(0, 2000), Xy(1000, 2000), false))) }).ToJsonString());
        var steps = ((CommandBatchArgs)batch.Args).Steps.Select(step => RevitCommandBatch.StepEnvelope(batch, step.Index)).ToArray();
        RevitCommandResponse Reply(RevitCommandEnvelope step, string[] created, string[] deleted) =>
            RevitCommandResponse.Success(new RevitCommandReceipt(step.OperationId, step.Command, step.Document.UniqueId, step.RequestSha256, false), created, Array.Empty<string>(), deleted, "{}", Array.Empty<string>());
        var mirrorDeleted = Safe("batch mirror deleted", () => CommandRulesRotateMirror.MirrorDeleted(new MirrorMapping(new[] { new MirrorResult(X, Y, true) }, Array.Empty<string>(), null, new[] { X }))) ?? Array.Empty<string>();
        RevitCommandResponse? persisted = null;
        var line = RevitCommandBatch.Run(batch, batch.RequestSha256, _ => false, 0, 2000,
            step => step.OperationId == "s-0" ? Reply(step, new[] { X }, Array.Empty<string>()) : Reply(step, new[] { Y }, mirrorDeleted.ToArray()),
            uid => !string.Equals(uid, X, StringComparison.OrdinalIgnoreCase), () => { }, response => { persisted = response; return "persisted"; });
        Check(line == "persisted" && persisted is { } done && done.Created.SequenceEqual(new[] { Y }) && done.Deleted.Count == 0,
            "a batch whose mirror step replaced an element an earlier step created persists, with only the replacement created", line);
        // Without the deleted list the survivor check blamed the copy step (the defect the review found).
        var blamed = RevitCommandBatch.Run(batch, batch.RequestSha256, _ => false, 0, 2000,
            step => step.OperationId == "s-0" ? Reply(step, new[] { X }, Array.Empty<string>()) : Reply(step, new[] { Y }, Array.Empty<string>()),
            uid => !string.Equals(uid, X, StringComparison.OrdinalIgnoreCase), () => { }, _ => "persisted");
        Check(blamed != "persisted" && blamed.Contains("args.steps[0]", StringComparison.Ordinal), "without the deleted list the batch would refuse, blaming the copy step", blamed);
    }

    // Review SF4: Revit is asked once for the ids actually mirrored; per id only to name a culprit, never for a carried door.
    static void CanMirrorOnce()
    {
        var far = new PlanMirror(-10000, 0, -10000, 1);
        var wall = new TransformTargetFacts(WallUid, false, false, false, false, false, Curve(0, 0, 0, 6000, 0, 0));
        var door = new TransformTargetFacts(DoorUid, false, false, false, true, true, Point(1500, 0, 0), WallUid);
        var column = new TransformTargetFacts(ColumnUid, false, false, false, false, false, Point(8000, 0, 0));
        var asked = new List<string>();
        RevitCommandException? Ask(IReadOnlyList<TransformTargetFacts> facts, Func<IReadOnlyList<string>, bool> answer)
        {
            asked.Clear();
            return Thrown(() => CommandRulesRotateMirror.CheckMirrorTargets(facts, far, true, ids => { asked.Add(string.Join(",", ids)); return answer(ids); }));
        }
        Check(Ask(new[] { wall, door }, _ => true) is null && asked.SequenceEqual(new[] { WallUid }), "[wall, its door]: CanMirrorElements is asked once, for the wall only", string.Join(" / ", asked));
        var culprit = Ask(new[] { door, wall, column }, ids => !ids.Contains(ColumnUid));
        Check(culprit is { Code: RevitRotateMirrorCodes.ElementNotMirrorable, Field: "args.element_uids[2]" } && asked.SequenceEqual(new[] { WallUid + "," + ColumnUid, WallUid, ColumnUid }),
            "a refused set is asked id by id in request order, skipping the carried door, and the column is refused at its index", culprit?.Field + " | " + string.Join(" / ", asked));
        var together = Ask(new[] { wall, column }, ids => ids.Count == 1);
        Check(together is { Code: RevitRotateMirrorCodes.ElementNotMirrorable, Field: "args.element_uids" }, "a set refused although each alone is accepted is refused at args.element_uids", together?.Field ?? "");
        Check(Ask(new[] { wall, column with { Pinned = true } }, _ => true) is { Code: RevitTransformCodes.ElementPinned, Field: "args.element_uids[1]" } && asked.Count == 0, "per-target refusals come before Revit is asked at all");
    }

    // Review NIT9: the rotation axis and the mirror plane the executor hands to Revit.
    static void AxisAndPlane()
    {
        var axis = CommandRulesRotateMirror.RotationAxis(new PlanRotation(1000, -500, Math.PI / 2));
        Check(NearAll(axis.BottomMm, 1000, -500, 0) && NearAll(axis.Direction, 0, 0, 1), "the rotation axis is vertical (+z) through the centre at z = 0");
        // Line (0,0)->(3000,4000): unit (0.6, 0.8), left normal (-0.8, 0.6, 0), origin the start.
        var plane = CommandRulesRotateMirror.MirrorPlane(new PlanMirror(0, 0, 3000, 4000));
        Check(NearAll(plane.Normal, -0.8, 0.6, 0) && NearAll(plane.OriginMm, 0, 0, 0), "the mirror plane's normal is the line's left unit perpendicular and its origin the line's start");
        // Reflecting (5000, 0, 0) in that plane: p - 2 ((p - o) . n) n = (5000, 0) - 2 (-4000) (-0.8, 0.6) = (-1400, 4800), as MirrorPoint.
        var p = new double[] { 5000, 0, 0 };
        var dot = (p[0] - plane.OriginMm[0]) * plane.Normal[0] + (p[1] - plane.OriginMm[1]) * plane.Normal[1] + (p[2] - plane.OriginMm[2]) * plane.Normal[2];
        Check(NearAll(new[] { p[0] - 2 * dot * plane.Normal[0], p[1] - 2 * dot * plane.Normal[1], p[2] - 2 * dot * plane.Normal[2] }, -1400, 4800, 0), "reflecting in the executor's plane gives the hand-computed (-1400, 4800)");
        var offset = CommandRulesRotateMirror.MirrorPlane(new PlanMirror(5000, 100, 5000, 2100));
        Check(NearAll(offset.Normal, -1, 0, 0) && NearAll(offset.OriginMm, 5000, 100, 0), "a north-running line x = 5000 has normal (-1, 0, 0) through (5000, 100, 0)");
    }
}
