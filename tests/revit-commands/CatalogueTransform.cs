using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// element.move / element.copy contract oracle (P1-09): translate 1 to 100 elements by a [dx, dy, dz] millimetre vector,
// moving them or copying them (the reply maps each source UniqueId to its copy). Covers the schema, the typed binder
// (count, distinct ids, finite, non-zero and bounded vector), the guarded client path (fake transport), command.batch
// steps and the Autodesk-free executor rules and readback in runtimes/revit-native/CommandsTransform.cs (typed refusals
// for pinned, view-specific, grouped, orphaned hosted and location-less elements; location shifted within 1 mm; copy
// mapping). Expected values are computed by hand in the comments, not with the implementation. No Revit is involved:
// a passing run is unit-tested evidence only; CommandsExecutorTransform.cs is compiled, never run.
static class CatalogueTransformChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003a001";
    const string ColumnUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003a002";
    const string FloorUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003a003";
    const string WallCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003b001";
    const string ColumnCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003b002";
    const string DoorCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003b003";
    const string FloorCopyUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003b004";
    const string DoorUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003a004";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-transform",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonArray Uids(params string[] uids) => new(uids.Select(uid => (JsonNode?)JsonValue.Create(uid)).ToArray());
    static JsonArray Vec(double dx, double dy, double dz) => new(dx, dy, dz);
    static JsonObject Args(JsonArray uids, JsonNode vector) => new() { ["element_uids"] = uids, ["vector_mm"] = vector };
    static JsonObject Simple() => Args(Uids(WallUid), Vec(1000, 0, 0));
    static string Numbered(int i) => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) <= tolerance;

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

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        Limits();
        ClientPath();
        Batch();
        Targets();
        MoveReadback();
        MappingChecks();
        NearestMapping();
        HostedTargets();
        SketchProfiles();
        Rotation();
        CopyReadback();
        Json();
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.Transform.Select(spec => spec.Name).SequenceEqual(new[] { "element.move", "element.copy" }), "transform catalogue lists element.move, element.copy");
        foreach (var spec in RevitCommandCatalogue.Transform)
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
        }
        Check(RevitCommandCatalogue.Find("element.move")?.ToolName == "revit_element_move" && RevitCommandCatalogue.Find("element.copy")?.ToolName == "revit_element_copy", "tool names follow revit_<command>");
        // A move changes existing elements (MCP destructiveHint); a copy only adds.
        Check(RevitCommandCatalogue.IsDestructive("element.move") && !RevitCommandCatalogue.IsDestructive("element.copy"), "element.move is destructive, element.copy is additive");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the transform commands");
        Check(RevitTransformRules.MinTargets == 1 && RevitTransformRules.MaxTargets == 100 && RevitTransformRules.MinMagnitudeMm == 1.2 && RevitTransformRules.MaxMagnitudeMm == 1_000_000 && CommandRulesTransform.ToleranceMm == 1.0,
            "1 to 100 targets, vector 1.2 mm to 1 km, readback within 1 mm");
        var codes = new[] { RevitTransformCodes.ElementPinned, RevitTransformCodes.ElementViewSpecific, RevitTransformCodes.ElementInGroup, RevitTransformCodes.HostMissing, RevitTransformCodes.LocationUnsupported };
        Check(codes.SequenceEqual(new[] { "element_pinned", "element_view_specific", "element_in_group", "host_missing", "location_unsupported" }) && RevitTransformCodes.All.SequenceEqual(codes),
            "typed refusal codes element_pinned, element_view_specific, element_in_group, host_missing, location_unsupported");
        Check(codes.All(code => !RevitCommandCodes.ClientSide.Contains(code) && !RevitCommandCodes.ExecutorReserved.Contains(code) && System.Text.RegularExpressions.Regex.IsMatch(code, @"\A[a-z]+(_[a-z0-9]+)*\z")),
            "transform refusal codes are snake_case and distinct from every protocol code");
    }

    static void Binding()
    {
        // (1000, 0, 0) mm: one metre east.
        var move = Accepts("a one-element move is accepted", Envelope("element.move", Simple()));
        Check(move is ElementMoveArgs { ElementUids: [WallUid] } m && Near(m.Vector.Dx.Millimetres, 1000) && Near(m.Vector.Dy.Millimetres, 0) && Near(m.Vector.Dz.Millimetres, 0),
            "element.move binds the UniqueId and the vector in millimetres", move?.ToString() ?? "");
        var copy = Accepts("a three-element copy is accepted", Envelope("element.copy", Args(Uids(WallUid, ColumnUid, FloorUid), Vec(-2500, 300.5, 3000))));
        Check(copy is ElementCopyArgs { ElementUids: [WallUid, ColumnUid, FloorUid] } c && Near(c.Vector.Dx.Millimetres, -2500) && Near(c.Vector.Dy.Millimetres, 300.5) && Near(c.Vector.Dz.Millimetres, 3000),
            "element.copy binds the UniqueIds in order and a negative / fractional / vertical vector", copy?.ToString() ?? "");
        // |(-2500, 300.5, 3000)| = sqrt(6250000 + 90300.25 + 9000000) = sqrt(15340300.25) = 3916.67...
        Check(copy is ElementCopyArgs cv && Near(cv.Vector.MagnitudeMm, Math.Sqrt(15340300.25), 1e-6), "the vector magnitude is its Euclidean length");
        var hundred = Enumerable.Range(1, 100).Select(Numbered).ToArray();
        Check(Accepts("100 elements are accepted", Envelope("element.move", Args(Uids(hundred), Vec(0, 0, 3000)))) is ElementMoveArgs { ElementUids.Count: 100 }, "100 distinct elements bind");
        Rejects("101 elements are args_invalid", Envelope("element.copy", Args(Uids(Enumerable.Range(1, 101).Select(Numbered).ToArray()), Vec(0, 0, 3000))), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Rejects("no element is args_invalid", Envelope("element.move", Args(Uids(), Vec(1000, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        // UniqueIds compare hex case-insensitively, as Document.GetElement(string) does.
        Rejects("the same UniqueId twice (other case) is args_invalid at the repeat", Envelope("element.move", Args(Uids(WallUid, ColumnUid, WallUid.ToUpperInvariant()), Vec(1000, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.element_uids[2]");
        Rejects("a malformed UniqueId is args_invalid", Envelope("element.copy", Args(Uids("not-a-unique-id"), Vec(1000, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.element_uids[0]");
        Rejects("missing vector_mm is args_invalid", Envelope("element.move", new JsonObject { ["element_uids"] = Uids(WallUid) }), RevitCommandCodes.ArgsInvalid, "args.vector_mm");
        Rejects("missing element_uids is args_invalid", Envelope("element.move", new JsonObject { ["vector_mm"] = Vec(1, 2, 3) }), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Rejects("a two-component vector is args_invalid", Envelope("element.move", Args(Uids(WallUid), new JsonArray(1000, 0))), RevitCommandCodes.ArgsInvalid, "args.vector_mm");
        Rejects("a four-component vector is args_invalid", Envelope("element.copy", Args(Uids(WallUid), new JsonArray(1000, 0, 0, 0))), RevitCommandCodes.ArgsInvalid, "args.vector_mm");
        Rejects("a text component is args_invalid", Envelope("element.move", Args(Uids(WallUid), new JsonArray(1000, "0", 0))), RevitCommandCodes.ArgsInvalid, "args.vector_mm[1]");
        var extra = Simple(); extra["rotate_deg"] = 90;
        Rejects("an unknown member is args_invalid", Envelope("element.move", extra), RevitCommandCodes.ArgsInvalid, "args.rotate_deg");
        var dryRun = Simple(); dryRun["dry_run"] = true;
        Rejects("dry_run is not offered", Envelope("element.copy", dryRun), RevitCommandCodes.ArgsInvalid, "args.dry_run");
    }

    static void Limits()
    {
        foreach (var command in new[] { "element.move", "element.copy" })
        {
            Rejects(command + ": a zero vector is length_invalid", Envelope(command, Args(Uids(WallUid), Vec(0, 0, 0))), RevitCommandCodes.LengthInvalid, "args.vector_mm");
            Rejects(command + ": a negative-zero vector is length_invalid", Envelope(command, Args(Uids(WallUid), JsonNode.Parse("[-0.0, 0, -0.0]")!)), RevitCommandCodes.LengthInvalid, "args.vector_mm");
            // |(1, 0, 0)| = 1 mm < 1.2 mm; |(0.6, 0.6, 0.6)| = 1.0392 mm < 1.2 mm.
            Rejects(command + ": a 1 mm vector is under 1.2 mm", Envelope(command, Args(Uids(WallUid), Vec(1, 0, 0))), RevitCommandCodes.LengthInvalid, "args.vector_mm");
            Rejects(command + ": a 1.04 mm diagonal vector is under 1.2 mm", Envelope(command, Args(Uids(WallUid), Vec(0.6, 0.6, 0.6))), RevitCommandCodes.LengthInvalid, "args.vector_mm");
            // |(0.7, 0.7, 0.7)| = 1.2124 mm >= 1.2 mm.
            Check(Accepts(command + ": a 1.21 mm diagonal vector is accepted", Envelope(command, Args(Uids(WallUid), Vec(0.7, 0.7, 0.7)))) is not null, command + ": a vector just over 1.2 mm binds");
            Check(Accepts(command + ": a 1 km vector is accepted", Envelope(command, Args(Uids(WallUid), Vec(0, -1_000_000, 0)))) is not null, command + ": a vector of exactly 1000000 mm binds");
            // |(800000, 800000, 0)| = 1131370.8 mm > 1000000 mm although each component is within it.
            Rejects(command + ": a 1131 m diagonal vector is too long", Envelope(command, Args(Uids(WallUid), Vec(800_000, 800_000, 0))), RevitCommandCodes.LengthInvalid, "args.vector_mm");
            Rejects(command + ": one component beyond 1 km is length_invalid at it", Envelope(command, Args(Uids(WallUid), Vec(0, 0, 1_000_001))), RevitCommandCodes.LengthInvalid, "args.vector_mm[2]");
            // 1e400 does not fit a double: it is not finite.
            Rejects(command + ": an overflowing component is length_invalid at it", Envelope(command, Args(Uids(WallUid), JsonNode.Parse("[1e400, 0, 0]")!)), RevitCommandCodes.LengthInvalid, "args.vector_mm[0]");
        }
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var command in new[] { "element.move", "element.copy" })
        {
            var before = forwarded.Count;
            var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, Simple()).ToJsonString(), policy, Transport, noLinks);
            Check(forwarded.Count == before + 1 && ok.Forwarded && JsonNode.Parse(forwarded[^1])?["Command"]?["command"]?.GetValue<string>() == command, command + " on an enabled copy is forwarded", ok.Line);
            var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, Simple(), RealPath).ToJsonString(), policy, Transport, noLinks);
            Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, command + " on a real project is document_not_enabled and never forwarded", refused.Line);
            var zero = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, Args(Uids(WallUid), Vec(0, 0, 0))).ToJsonString(), policy, Transport, noLinks);
            Check(zero.IsError && !zero.Forwarded && JsonNode.Parse(zero.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.LengthInvalid && forwarded.Count == before + 1, command + ": a zero vector is refused before the pipe", zero.Line);
            var twice = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, Args(Uids(WallUid, WallUid), Vec(1000, 0, 0))).ToJsonString(), policy, Transport, noLinks);
            Check(twice.IsError && !twice.Forwarded && JsonNode.Parse(twice.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.ArgsInvalid && forwarded.Count == before + 1, command + ": a repeated UniqueId is refused before the pipe", twice.Line);
        }
        try
        {
            var a = RevitCommandProtocol.Parse(Envelope("element.copy", Args(Uids(WallUid, ColumnUid), Vec(1000, 0, 0))).ToJsonString());
            var b = RevitCommandProtocol.Parse(Envelope("element.copy", new JsonObject { ["vector_mm"] = Vec(1000, 0, 0), ["element_uids"] = Uids(WallUid, ColumnUid) }).ToJsonString());
            var swapped = RevitCommandProtocol.Parse(Envelope("element.copy", Args(Uids(ColumnUid, WallUid), Vec(1000, 0, 0))).ToJsonString());
            Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256 && a.RequestSha256 != swapped.RequestSha256,
                "element.copy requests use the v1 canonical form and hash (member order ignored, element order kept)");
        }
        catch (RevitCommandException error) { Check(false, "element.copy canonical form", error.Code + " " + error.Detail); }
    }

    static JsonObject Step(string id, string command, JsonNode args) => new() { ["operation_id"] = id, ["command"] = command, ["args"] = args.DeepClone() };

    static void Batch()
    {
        var batch = new JsonObject { ["steps"] = new JsonArray(Step("s-0", "element.move", Simple()), Step("s-1", "element.copy", Args(Uids(ColumnUid), Vec(0, 4000, 0)))) };
        var parsed = Accepts("a batch moving then copying is accepted", Envelope("command.batch", batch));
        Check(parsed is CommandBatchArgs { Steps: [{ Args: ElementMoveArgs { ElementUids: [WallUid] } }, { Args: ElementCopyArgs { ElementUids: [ColumnUid] } }] },
            "batch steps bind to ElementMoveArgs and ElementCopyArgs in order", parsed?.ToString() ?? "");
        var zero = new JsonObject { ["steps"] = new JsonArray(Step("s-0", "element.copy", Args(Uids(ColumnUid), Vec(0, 0, 0)))) };
        Rejects("a batch step with a zero vector is length_invalid under that step", Envelope("command.batch", zero), RevitCommandCodes.LengthInvalid, "args.steps[0].args.vector_mm");
    }

    static TransformLocation Point(double x, double y, double z, double rotation = 0) => new(TransformLocationKinds.Point, new[] { x, y, z }, null, rotation);
    static TransformLocation Curve(double x0, double y0, double z0, double x1, double y1, double z1) => new(TransformLocationKinds.Curve, new[] { x0, y0, z0 }, new[] { x1, y1, z1 });
    static TransformLocation Box(double x0, double y0, double z0, double x1, double y1, double z1) => new(TransformLocationKinds.Box, new[] { x0, y0, z0 }, new[] { x1, y1, z1 });
    static TransformTargetFacts Facts(TransformLocation? location) => new(WallUid, false, false, false, false, false, location);

    static void Targets()
    {
        var wall = Facts(Curve(0, 0, 0, 6000, 0, 0));
        Check(Thrown(() => CommandRulesTransform.CheckTarget(wall, "args.element_uids[0]")) is null, "an unpinned model element with a location curve can be transformed");
        Check(Thrown(() => CommandRulesTransform.CheckTarget(Facts(Box(0, 0, 0, 6000, 4000, 200)), "args.element_uids[0]")) is null, "an element with only a bounding box (a floor) can be transformed");
        Check(Thrown(() => CommandRulesTransform.CheckTarget(wall with { Pinned = true }, "args.element_uids[3]")) is { Code: RevitTransformCodes.ElementPinned, Field: "args.element_uids[3]" } pinned && pinned.Detail!.Contains(WallUid, StringComparison.Ordinal),
            "a pinned element is element_pinned at its index, naming it");
        Check(Thrown(() => CommandRulesTransform.CheckTarget(wall with { ViewSpecific = true }, "args.element_uids[1]")) is { Code: RevitTransformCodes.ElementViewSpecific, Field: "args.element_uids[1]" },
            "a view-specific element (annotation, detail item) is element_view_specific");
        Check(Thrown(() => CommandRulesTransform.CheckTarget(wall with { Grouped = true }, "args.element_uids[0]")) is { Code: RevitTransformCodes.ElementInGroup }, "a group member is element_in_group");
        var door = new TransformTargetFacts(ColumnUid, false, false, false, true, true, Point(1500, 0, 0));
        Check(Thrown(() => CommandRulesTransform.CheckTarget(door, "args.element_uids[0]")) is null, "a hosted element whose host exists can be transformed");
        Check(Thrown(() => CommandRulesTransform.CheckTarget(door with { HostPresent = false }, "args.element_uids[2]")) is { Code: RevitTransformCodes.HostMissing, Field: "args.element_uids[2]" } orphan && orphan.Detail!.Contains(ColumnUid, StringComparison.Ordinal),
            "a hosted element whose host is missing is host_missing");
        Check(Thrown(() => CommandRulesTransform.CheckTarget(Facts(null), "args.element_uids[0]")) is { Code: RevitTransformCodes.LocationUnsupported }, "an element with no location point, curve or box is location_unsupported");
        // Several reasons: pinned is reported first, then view-specific, group, host, location.
        Check(Thrown(() => CommandRulesTransform.CheckTarget(new TransformTargetFacts(WallUid, true, true, true, true, false, null), "args.element_uids[0]")) is { Code: RevitTransformCodes.ElementPinned },
            "pinned is reported before every other refusal");
        Check(Thrown(() => CommandRulesTransform.CheckTarget(new TransformTargetFacts(WallUid, false, true, true, true, false, null), "args.element_uids[0]")) is { Code: RevitTransformCodes.ElementViewSpecific },
            "view-specific is reported before group, host and location");
    }

    static readonly double[] East = { 1000, 0, 0 };

    static void MoveReadback()
    {
        // Expected by hand: (1000, 2000, 0) + (500, -250, 300) = (1500, 1750, 300).
        var shifted = CommandRulesTransform.Shift(Point(1000, 2000, 0, 0.5), new double[] { 500, -250, 300 });
        Check(shifted.Kind == TransformLocationKinds.Point && shifted.A.SequenceEqual(new double[] { 1500, 1750, 300 }) && shifted.B is null && shifted.RotationRad == 0.5, "a point shifts by the vector and keeps its rotation");
        var curve = CommandRulesTransform.Shift(Curve(0, 0, 0, 6000, 0, 0), new double[] { 0, 3000, -150 });
        Check(curve.A.SequenceEqual(new double[] { 0, 3000, -150 }) && curve.B!.SequenceEqual(new double[] { 6000, 3000, -150 }), "both curve ends shift by the vector");
        string? Moved(TransformLocation before, TransformLocation? after, double[] vector) => CommandReadbackTransform.Moved(new[] { WallUid }, new[] { before }, new[] { after }, vector);
        Check(Moved(Curve(0, 0, 0, 6000, 0, 0), Curve(1000, 0, 0, 7000, 0, 0), East) is null, "a wall whose location curve moved 1000 mm east matches");
        // Off by (0.5, 0.5, 0.5): 0.866 mm <= 1 mm.
        Check(Moved(Point(0, 0, 0), Point(1000.5, 0.5, 0.5), East) is null, "a point within 0.87 mm of the expected location matches");
        // Off by (0.8, 0.8, 0): 1.13 mm > 1 mm although each component is within 1 mm.
        Check(Moved(Point(0, 0, 0), Point(1000.8, 0.8, 0), East) is { } off && off.Contains(WallUid, StringComparison.Ordinal), "a point 1.13 mm from the expected location is a mismatch naming the element");
        Check(Moved(Point(0, 0, 0), Point(0, 0, 0), East) is not null, "an element that did not move is a mismatch");
        Check(Moved(Curve(0, 0, 0, 6000, 0, 0), Curve(1000, 0, 0, 6000, 0, 0), East) is not null, "a curve stretched instead of moved (only its start shifted) is a mismatch");
        Check(Moved(Curve(0, 0, 0, 6000, 0, 0), Curve(6000 + 1000, 0, 0, 1000, 0, 0), East) is not null, "a reversed curve is a mismatch");
        Check(Moved(Point(0, 0, 0), Curve(1000, 0, 0, 2000, 0, 0), East) is { } kind && kind.Contains("point", StringComparison.Ordinal), "a location of another kind is a mismatch");
        Check(Moved(Point(0, 0, 0), null, East) is { } gone && gone.Contains("no longer", StringComparison.Ordinal), "an element without a readable location afterwards is a mismatch");
        Check(Moved(Point(0, 0, 0, 0), Point(1000, 0, 0, 0.1), East) is { } turned && turned.Contains("rotation", StringComparison.Ordinal), "a point whose rotation changed is a mismatch");
        Check(Moved(Box(0, 0, 0, 6000, 4000, 200), Box(1000, 0, 0, 7000, 4000, 200), East) is null, "a bounding box that moved by the vector matches");
        Check(Moved(Box(0, 0, 0, 6000, 4000, 200), Box(1000, 0, 0, 7000, 4500, 200), East) is not null, "a bounding box that grew is a mismatch");
        Check(CommandReadbackTransform.Moved(new[] { WallUid, ColumnUid }, new[] { Point(0, 0, 0), Point(5000, 0, 0) }, new TransformLocation?[] { Point(1000, 0, 0), Point(5000, 0, 0) }, East) is { } second && second.Contains(ColumnUid, StringComparison.Ordinal),
            "every target is checked: the second one not moving is a mismatch naming it");
    }

    const string WallKey = "Wall|-2000011|type-200";
    const string ColumnKey = "FamilyInstance|-2001330|type-col";
    const string DoorKey = "FamilyInstance|-2000023|type-door";

    static void MappingChecks()
    {
        var sources = new[] { new TransformCandidate(WallUid, WallKey, Curve(0, 0, 0, 6000, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(6000, 0, 0)) };
        // Revit returns the new ids in its own order, with a copy of the wall's hosted door the request did not name.
        var copies = new[] { new TransformCandidate(DoorCopyUid, DoorKey, Point(1500, 4000, 0)), new TransformCandidate(ColumnCopyUid, ColumnKey, Point(6000, 4000, 0)), new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)) };
        var north = new double[] { 0, 4000, 0 };
        var mapping = CommandRulesTransform.MapCopies(sources, copies, north);
        Check(mapping.Mismatch is null && mapping.Pairs.SequenceEqual(new[] { new CopyPair(WallUid, WallCopyUid), new CopyPair(ColumnUid, ColumnCopyUid) }) && mapping.AlsoCreated.SequenceEqual(new[] { DoorCopyUid }),
            "copies map to their sources in request order by kind and shifted location; the extra hosted copy is also_created", mapping.ToString());
        var misplaced = CommandRulesTransform.MapCopies(sources, new[] { copies[1], copies[2] with { Location = Curve(0, 4002, 0, 6000, 4002, 0) } }, north);
        Check(misplaced.Mismatch is { } m && m.Contains(WallUid, StringComparison.Ordinal), "a copy 2 mm off its expected location maps to no source: a mismatch naming the source", misplaced.ToString());
        var otherKind = CommandRulesTransform.MapCopies(sources, new[] { copies[1], copies[2] with { Key = "Wall|-2000011|type-300" } }, north);
        Check(otherKind.Mismatch is not null, "a copy of another type maps to no source");
        var missing = CommandRulesTransform.MapCopies(sources, new[] { copies[1] }, north);
        Check(missing.Mismatch is { } absent && absent.Contains(WallUid, StringComparison.Ordinal), "a source without a copy is a mismatch");
        // Two identical sources at the same place (Revit's duplicate-instance warning) get two distinct copies.
        var twins = new[] { new TransformCandidate(WallUid, ColumnKey, Point(0, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(0, 0, 0)) };
        var twinCopies = new[] { new TransformCandidate(WallCopyUid, ColumnKey, Point(0, 4000, 0)), new TransformCandidate(ColumnCopyUid, ColumnKey, Point(0, 4000, 0)) };
        var twinMapping = CommandRulesTransform.MapCopies(twins, twinCopies, north);
        Check(twinMapping.Mismatch is null && twinMapping.Pairs.Select(pair => pair.CopyUid).Distinct().Count() == 2 && twinMapping.AlsoCreated.Count == 0, "identical sources map to distinct copies", twinMapping.ToString());
        Check(CommandRulesTransform.MapCopies(new[] { sources[0] with { Location = null } }, copies, north).Mismatch is not null, "a source without a location cannot be mapped");
    }

    static void CopyReadback()
    {
        var sources = new[] { new TransformCandidate(WallUid, WallKey, Curve(0, 0, 0, 6000, 0, 0)) };
        var copies = new[] { new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)) };
        var north = new double[] { 0, 4000, 0 };
        Check(CommandReadbackTransform.Copied(sources, new TransformLocation?[] { Curve(0, 0, 0, 6000, 0, 0) }, copies, north) is null, "a copy 4000 mm north of an unchanged source matches");
        Check(CommandReadbackTransform.Copied(sources, new TransformLocation?[] { Curve(0, 0, 0, 6000, 0, 0) }, Array.Empty<TransformCandidate>(), north) is { } none && none.Contains(WallUid, StringComparison.Ordinal), "no copy is a mismatch");
        Check(CommandReadbackTransform.Copied(sources, new TransformLocation?[] { Curve(0, 4000, 0, 6000, 4000, 0) }, copies, north) is { } moved && moved.Contains("source", StringComparison.Ordinal), "a source that moved instead of staying is a mismatch");
        Check(CommandReadbackTransform.Copied(sources, new TransformLocation?[] { null }, copies, north) is not null, "a source that is gone is a mismatch");
    }

    // Review (1): sources 0.9 mm apart whose copies come back in another order. First-fit would give source 0 the copy
    // at 0.9 (0.9 mm off, within 1 mm), source 0.9 the copy at 1.8, and leave source 1.8 with the copy at 0 (1.8 mm
    // off): a false mismatch. Nearest-first takes the three exact pairs.
    static void NearestMapping()
    {
        var north = new double[] { 0, 4000, 0 };
        var sources = new[] { new TransformCandidate(WallUid, ColumnKey, Point(0, 0, 0)), new TransformCandidate(ColumnUid, ColumnKey, Point(0.9, 0, 0)), new TransformCandidate(FloorUid, ColumnKey, Point(1.8, 0, 0)) };
        var copies = new[] { new TransformCandidate(ColumnCopyUid, ColumnKey, Point(0.9, 4000, 0)), new TransformCandidate(FloorCopyUid, ColumnKey, Point(1.8, 4000, 0)), new TransformCandidate(WallCopyUid, ColumnKey, Point(0, 4000, 0)) };
        var mapping = CommandRulesTransform.MapCopies(sources, copies, north);
        Check(mapping.Mismatch is null && mapping.Pairs.SequenceEqual(new[] { new CopyPair(WallUid, WallCopyUid), new CopyPair(ColumnUid, ColumnCopyUid), new CopyPair(FloorUid, FloorCopyUid) }) && mapping.AlsoCreated.Count == 0,
            "close sources map nearest-first to their exact copies whatever order Revit returns them in", mapping.ToString());
        // Each source still within 1 mm of a copy: (0.5, 0, 0) off for all three keeps the same pairs.
        var offset = copies.Select(copy => copy with { Location = CommandRulesTransform.Shift(copy.Location!, new double[] { 0.5, 0, 0 }) }).ToArray();
        var shifted = CommandRulesTransform.MapCopies(sources, offset, north);
        Check(shifted.Mismatch is null && shifted.Pairs.Select(pair => pair.CopyUid).SequenceEqual(new[] { WallCopyUid, ColumnCopyUid, FloorCopyUid }), "copies all 0.5 mm off still pair with their own sources", shifted.ToString());
    }

    // Review (2): a door and its host wall in one request. The wall carries the door, so only the wall is passed to
    // MoveElements / CopyElements; both are read back.
    static void HostedTargets()
    {
        var wall = new TransformTargetFacts(WallUid, false, false, false, false, false, Curve(0, 0, 0, 6000, 0, 0));
        var door = new TransformTargetFacts(DoorUid, false, false, false, true, true, Point(1500, 0, 0), WallUid);
        var both = CommandRulesTransform.TransformIds(new[] { wall, door });
        Check(both.Transformed.SequenceEqual(new[] { WallUid }) && both.ReadBack.SequenceEqual(new[] { WallUid, DoorUid }), "[wall, door hosted by the wall]: only the wall is transformed, both are read back", string.Join(",", both.Transformed) + " / " + string.Join(",", both.ReadBack));
        var upper = CommandRulesTransform.TransformIds(new[] { door with { HostUid = WallUid.ToUpperInvariant() }, wall });
        Check(upper.Transformed.SequenceEqual(new[] { WallUid }) && upper.ReadBack.SequenceEqual(new[] { DoorUid, WallUid }), "the host is matched case-insensitively and in any order");
        var alone = CommandRulesTransform.TransformIds(new[] { door });
        Check(alone.Transformed.SequenceEqual(new[] { DoorUid }) && alone.ReadBack.SequenceEqual(new[] { DoorUid }), "a door whose host is not a target is transformed itself");
        // Moved by the wall, the door is read back shifted like any target.
        Check(CommandReadbackTransform.Moved(both.ReadBack, new[] { wall.Location!, door.Location! }, new TransformLocation?[] { Curve(1000, 0, 0, 7000, 0, 0), Point(2500, 0, 0) }, East) is null
            && CommandReadbackTransform.Moved(both.ReadBack, new[] { wall.Location!, door.Location! }, new TransformLocation?[] { Curve(1000, 0, 0, 7000, 0, 0), Point(3500, 0, 0) }, East) is { } twice && twice.Contains(DoorUid, StringComparison.Ordinal),
            "a door moved once with its wall matches; a door moved twice is a mismatch naming it");
    }

    static TransformLocation Profile(params double[][] points) => new(TransformLocationKinds.Profile, points[0], null, null, points);

    // Review (3): floors and roofs are read back by their sketch profile curve ends, which joins and cuts do not change.
    static void SketchProfiles()
    {
        var floor = Profile(new double[] { 0, 0, 0 }, new double[] { 6000, 0, 0 }, new double[] { 6000, 4000, 0 }, new double[] { 0, 4000, 0 });
        var shifted = CommandRulesTransform.Shift(floor, East);
        Check(shifted.Kind == TransformLocationKinds.Profile && shifted.Points is { Count: 4 } points && points[0].SequenceEqual(new double[] { 1000, 0, 0 }) && points[2].SequenceEqual(new double[] { 7000, 4000, 0 }),
            "every profile point shifts by the vector");
        string? Moved(TransformLocation? after) => CommandReadbackTransform.Moved(new[] { FloorUid }, new[] { floor }, new[] { after }, East);
        Check(Moved(Profile(new double[] { 1000, 0, 0 }, new double[] { 7000, 0, 0 }, new double[] { 7000, 4000, 0 }, new double[] { 1000, 4000, 0 })) is null, "a floor profile moved 1000 mm east matches");
        Check(Moved(Profile(new double[] { 1000, 0, 0 }, new double[] { 7000, 0, 0 }, new double[] { 7000, 4002, 0 }, new double[] { 1000, 4000, 0 })) is { } corner && corner.Contains("profile", StringComparison.Ordinal),
            "one profile corner 2 mm off is a mismatch");
        Check(Moved(Profile(new double[] { 1000, 0, 0 }, new double[] { 7000, 0, 0 }, new double[] { 7000, 4000, 0 })) is not null, "a profile with another number of curve ends is a mismatch");
        Check(Moved(Box(1000, 0, 0, 7000, 4000, 200)) is not null, "a box where a profile was read is a mismatch");
        var json = JsonNode.Parse(CommandReadbackTransform.MoveJson(new[] { FloorUid }, new[] { floor }, new TransformLocation?[] { shifted }, East));
        Check(json?["moved"]?[0]?["after"]?["kind"]?.GetValue<string>() == "profile" && json?["moved"]?[0]?["after"]?["points"]?.AsArray().Count == 4 && json?["moved"]?[0]?["after"]?["points"]?[2]?[0]?.GetValue<double>() == 7000,
            "a profile location is written as its curve end points", json?.ToJsonString() ?? "");
    }

    // Review (4): a rotation that cannot be read after the move is a mismatch; angles compare modulo a full turn.
    static void Rotation()
    {
        string? Moved(TransformLocation before, TransformLocation after) => CommandReadbackTransform.Moved(new[] { ColumnUid }, new[] { before }, new TransformLocation?[] { after }, East);
        Check(Moved(Point(0, 0, 0, 0.1), Point(1000, 0, 0) with { RotationRad = null }) is { } lost && lost.Contains("rotation", StringComparison.Ordinal), "a rotation no longer readable after the move is a mismatch");
        Check(Moved(Point(0, 0, 0, 0), Point(1000, 0, 0, 2 * Math.PI)) is null, "0 and 2 pi are the same rotation");
        Check(Moved(Point(0, 0, 0, Math.PI - 1e-7), Point(1000, 0, 0, -Math.PI + 1e-7)) is null, "pi - 1e-7 and -pi + 1e-7 are 2e-7 rad apart");
        Check(Moved(Point(0, 0, 0, 0.5), Point(1000, 0, 0, 0.5 + 2 * Math.PI + 0.1)) is not null, "a turn of 0.1 rad beyond a full turn is a mismatch");
        Check(Moved(Point(0, 0, 0) with { RotationRad = null }, Point(1000, 0, 0) with { RotationRad = null }) is null, "an element without a rotation before and after matches");
    }

    static void Json()
    {
        var moveJson = JsonNode.Parse(CommandReadbackTransform.MoveJson(new[] { WallUid }, new[] { Curve(0, 0, 0, 6000, 0, 0) }, new TransformLocation?[] { Curve(1000, 0, 0, 7000, 0, 0) }, East));
        var moved = moveJson?["moved"]?[0];
        Check(moveJson?["vector_mm"]?.AsArray().Select(n => n?.GetValue<double>() ?? double.NaN).SequenceEqual(East) == true && moved?["unique_id"]?.GetValue<string>() == WallUid
            && moved?["before"]?["kind"]?.GetValue<string>() == "curve" && moved?["after"]?["start"]?[0]?.GetValue<double>() == 1000 && moved?["after"]?["end"]?[0]?.GetValue<double>() == 7000,
            "move readback JSON lists the vector and each element's location before and after", moveJson?.ToJsonString() ?? "");
        var point = JsonNode.Parse(CommandReadbackTransform.MoveJson(new[] { ColumnUid }, new[] { Point(0, 0, 0, Math.PI / 2) }, new TransformLocation?[] { Point(1000, 0, 0, Math.PI / 2) }, East));
        Check(point?["moved"]?[0]?["after"]?["point"]?[0]?.GetValue<double>() == 1000 && Near(point?["moved"]?[0]?["after"]?["rotation_deg"]?.GetValue<double>() ?? double.NaN, 90, 1e-9),
            "a point location is written with its rotation in degrees", point?.ToJsonString() ?? "");
        var box = JsonNode.Parse(CommandReadbackTransform.MoveJson(new[] { FloorUid }, new[] { Box(0, 0, 0, 6000, 4000, 200) }, new TransformLocation?[] { Box(1000, 0, 0, 7000, 4000, 200) }, East));
        Check(box?["moved"]?[0]?["after"]?["kind"]?.GetValue<string>() == "box" && box?["moved"]?[0]?["after"]?["max"]?[0]?.GetValue<double>() == 7000, "a bounding box location is written as min and max", box?.ToJsonString() ?? "");
        var mapping = new CopyMapping(new[] { new CopyPair(WallUid, WallCopyUid) }, new[] { DoorCopyUid }, null);
        var copyJson = JsonNode.Parse(CommandReadbackTransform.CopyJson(mapping, new[] { new TransformCandidate(WallCopyUid, WallKey, Curve(0, 4000, 0, 6000, 4000, 0)) }, new double[] { 0, 4000, 0 }));
        var pair = copyJson?["copies"]?[0];
        Check(pair?["source_unique_id"]?.GetValue<string>() == WallUid && pair?["copy_unique_id"]?.GetValue<string>() == WallCopyUid && pair?["location"]?["start"]?[1]?.GetValue<double>() == 4000
            && copyJson?["also_created"]?.AsArray().Select(n => n?.GetValue<string>()).SequenceEqual(new[] { DoorCopyUid }) == true && copyJson?["vector_mm"]?.AsArray().Count == 3,
            "copy readback JSON maps each source UniqueId to its copy with the copy's location and lists also_created", copyJson?.ToJsonString() ?? "");
        Check(CommandRulesTransform.Created(mapping).SequenceEqual(new[] { WallCopyUid, DoorCopyUid }), "created lists the mapped copies in request order, then the extra copies");
    }
}
