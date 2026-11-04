using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// P1-05 modify and delete commands contract oracle: element.set_parameter and element.delete. Covers the schemas, the
// typed validators and units, the guarded client path (fake transport; dry runs are guarded like writes), and the
// Autodesk-free executor rules, cascade confirmation, readback checks and change report JSON in
// runtimes/revit-native/CommandsExecutorModifyRules.cs. No Revit is involved: a passing run is unit-tested evidence
// only; CommandsExecutorModify.cs is compiled, never run.
static class CatalogueModifyChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string WallUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3";
    const string DoorUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1d0";
    const string WindowUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1d1";
    const string LevelUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000311";
    const string StoreUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000a01";
    const string SharedGuid = "1b8fa0f4-2b2e-4b5e-9c55-0a6b7d1e2f30";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";
    static readonly string[] ModifyCommands = { "element.set_parameter", "element.delete" };

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-modify",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonObject Value(string kind, string member, JsonNode? value) => new() { ["kind"] = kind, [member] = value };
    static JsonObject Set(JsonObject value, JsonObject? parameter = null) => new() { ["element_uid"] = WallUid, ["parameter"] = parameter ?? new JsonObject { ["name"] = "Unconnected Height" }, ["value"] = value };
    static JsonObject Delete(params string[] uids) => new() { ["element_uids"] = new JsonArray(uids.Select(uid => (JsonNode?)uid).ToArray()) };
    static JsonObject Sample(string command) => command == "element.set_parameter" ? Set(Value("length", "mm", 3000)) : Delete(WallUid);
    static JsonObject With(JsonObject args, string member, JsonNode? value) { if (value is null) args.Remove(member); else args[member] = value; return args; }
    static string Hex(int i) => "60f91daf-3dd7-4283-a86d-24137b73f3da-" + i.ToString("x8");

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
        SetParameterBinding();
        DeleteBinding();
        ClientPath();
        ParameterRules();
        DeleteRules();
        Readbacks();
        ReadbackJson();
        SideEffects();
        Replies();
    }

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.Modify.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(ModifyCommands), "modify catalogue lists element.set_parameter then element.delete", string.Join(",", names));
        var throughDocumentation = RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror).Concat(RevitCommandCatalogue.Elements)
            .Concat(RevitCommandCatalogue.Structure).Concat(RevitCommandCatalogue.Documentation).ToArray();
        Check(RevitCommandCatalogue.All.Take(throughDocumentation.Length).SequenceEqual(throughDocumentation) && RevitCommandCatalogue.All.Skip(throughDocumentation.Length).Take(RevitCommandCatalogue.Modify.Count).SequenceEqual(RevitCommandCatalogue.Modify),
            "All is v1, v2, views, snapshot, element queries, structure, documentation, then the modify commands (later catalogue parts follow)");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the modify commands");
        Check(RevitCommandCatalogue.Modify.Select(spec => spec.ToolName).SequenceEqual(new[] { "revit_element_set_parameter", "revit_element_delete" }), "modify tool names are revit_element_set_parameter and revit_element_delete");
        Check(RevitCommandCatalogue.Modify.All(spec => spec.Writes), "both modify commands are writes (dry runs included)");
        foreach (var name in names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.Modify.Single(spec => spec.Name == name)), "Find resolves modify command " + name);
        Check(RevitCommandCatalogue.Modify.All(spec => spec.Description.Length > 20 && spec.Description.EndsWith('.') && spec.Description.Contains("enabled test copies", StringComparison.Ordinal) && spec.Description.Contains("UniqueId", StringComparison.Ordinal)),
            "every modify command has a plain-English description naming UniqueId targets and the copies-only rule");
        Check(RevitCommandCatalogue.Find("element.set_parameter")!.Description.Contains("read-only", StringComparison.Ordinal) && RevitCommandCatalogue.Find("element.set_parameter")!.Description.Contains("millimetres", StringComparison.Ordinal)
            && RevitCommandCatalogue.Find("element.delete")!.Description.Contains("dry run", StringComparison.Ordinal), "descriptions state typed units, the read-only refusal and the dry-run cascade report");
        foreach (var spec in RevitCommandCatalogue.Modify)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; if (!schema.RootElement.GetProperty("properties").TryGetProperty("dry_run", out var dryRun) || dryRun.GetProperty("type").GetString() != "boolean") problem = "no boolean dry_run"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords, with a boolean dry_run", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var spec in RevitCommandCatalogue.Modify)
            Check(PermissionPolicy.RequiredLevel(spec.Name, FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel(spec.Name, RealPath, copies) == PermissionLevel.WriteRealProject,
                "permission level of " + spec.Name + " is write-copies on a copy and write-real-project elsewhere");
        var changing = RevitCommandCatalogue.Modify.Concat(RevitCommandCatalogue.WallProfile).Concat(RevitCommandCatalogue.MaterialTypes.Where(spec => spec.Name is "material.set" or "element.change_type"))
            .Concat(RevitCommandCatalogue.ExportSave.Where(spec => spec.Name == "document.save")).ToArray();
        Check(changing.All(spec => RevitCommandCatalogue.IsDestructive(spec.Name)) && RevitCommandCatalogue.All.Except(changing).All(spec => !RevitCommandCatalogue.IsDestructive(spec.Name))
            && !RevitCommandCatalogue.IsDestructive(null) && !RevitCommandCatalogue.IsDestructive("element.purge"), "exactly the modify commands, wall.set_profile, material.set, element.change_type and document.save are destructive; additive writes and queries are not");
    }

    static void SetParameterBinding()
    {
        var length = Accepts("set_parameter length accepted", Envelope("element.set_parameter", Set(Value("length", "mm", 3200.5))));
        Check(length?.Args is ElementSetParameterArgs { ElementUid: WallUid, Parameter: ParameterByName { Name: "Unconnected Height" }, Value: LengthValueArg { Value: var mm }, DryRun: false } && Math.Abs(mm.Millimetres - 3200.5) < 1e-9 && Math.Abs(mm.Metres - 3.2005) < 1e-12,
            "a length value binds to a typed Length in millimetres (metres inside), parameter by name, dry_run false by default");
        Check(Accepts("negative length accepted", Envelope("element.set_parameter", Set(Value("length", "mm", -450))))?.Args is ElementSetParameterArgs { Value: LengthValueArg { Value: var negative } } && Math.Abs(negative.Millimetres + 450) < 1e-9, "lengths may be negative (offsets)");
        var area = Accepts("set_parameter area accepted", Envelope("element.set_parameter", Set(Value("area", "m2", 12.5))));
        Check(area?.Args is ElementSetParameterArgs { Value: AreaValueArg { Value.SquareMetres: 12.5, Kind: "area" } }, "an area value binds to square metres");
        var angle = Accepts("set_parameter angle accepted", Envelope("element.set_parameter", Set(Value("angle", "deg", 90))));
        Check(angle?.Args is ElementSetParameterArgs { Value: AngleValueArg { Value: var a, Kind: "angle" } } && Math.Abs(a.Radians - Math.PI / 2) < 1e-12, "an angle value binds to degrees (radians inside)");
        Check(Accepts("set_parameter text accepted", Envelope("element.set_parameter", Set(Value("text", "text", "Fire rated"), new JsonObject { ["built_in"] = "ALL_MODEL_INSTANCE_COMMENTS" })))?.Args
            is ElementSetParameterArgs { Parameter: ParameterByBuiltIn { BuiltIn: "ALL_MODEL_INSTANCE_COMMENTS" }, Value: TextValueArg { Value: "Fire rated", Kind: "text" } }, "a text value and a built-in parameter bind");
        Check(Accepts("set_parameter empty text accepted", Envelope("element.set_parameter", Set(Value("text", "text", ""))))?.Args is ElementSetParameterArgs { Value: TextValueArg { Value: "" } }, "an empty text clears the parameter");
        Check(Accepts("set_parameter multi-line text accepted", Envelope("element.set_parameter", Set(Value("text", "text", "line 1\nline 2"))))?.Args is ElementSetParameterArgs { Value: TextValueArg { Value: "line 1\nline 2" } }, "multi-line text is allowed");
        Check(Accepts("set_parameter integer accepted", Envelope("element.set_parameter", Set(Value("integer", "integer", -7), new JsonObject { ["guid"] = SharedGuid.ToUpperInvariant() })))?.Args
            is ElementSetParameterArgs { Parameter: ParameterByGuid { Guid: var guid }, Value: IntegerValueArg { Value: -7, Kind: "integer" } } && guid == Guid.Parse(SharedGuid), "an integer value and a shared parameter GUID (any hex case) bind");
        Check(Accepts("set_parameter yes/no accepted", Envelope("element.set_parameter", With(Set(Value("yes_no", "yes_no", true)), "dry_run", true)))?.Args is ElementSetParameterArgs { Value: YesNoValueArg { Value: true, Kind: "yes_no" }, DryRun: true }, "a yes/no value and dry_run bind");
        Check(Accepts("set_parameter element accepted", Envelope("element.set_parameter", Set(Value("element", "uid", LevelUid))))?.Args is ElementSetParameterArgs { Value: ElementValueArg { UniqueId: LevelUid, Kind: "element" } }, "an element value binds to a UniqueId");
        Check(RevitParameterValueKinds.All.SequenceEqual(new[] { "length", "area", "angle", "text", "integer", "yes_no", "element" }), "value kinds are length, area, angle, text, integer, yes_no and element");

        Rejects("set_parameter without value", Envelope("element.set_parameter", With(Set(Value("length", "mm", 1)), "value", null)), RevitCommandCodes.ArgsInvalid, "args.value");
        Rejects("set_parameter without parameter", Envelope("element.set_parameter", With(Set(Value("length", "mm", 1)), "parameter", null)), RevitCommandCodes.ArgsInvalid, "args.parameter");
        Rejects("set_parameter element that is not a UniqueId", Envelope("element.set_parameter", With(Set(Value("length", "mm", 1)), "element_uid", "Basic Wall")), RevitCommandCodes.ArgsInvalid, "args.element_uid");
        Rejects("set_parameter unknown value kind", Envelope("element.set_parameter", Set(Value("volume", "m3", 1))), RevitCommandCodes.ArgsInvalid);
        Rejects("set_parameter length given in metres", Envelope("element.set_parameter", Set(Value("length", "m", 3))), RevitCommandCodes.ArgsInvalid);
        Rejects("set_parameter length as a string", Envelope("element.set_parameter", Set(Value("length", "mm", "3000"))), RevitCommandCodes.ArgsInvalid, "args.value.mm");
        Rejects("set_parameter length beyond the Revit limit", Envelope("element.set_parameter", Set(Value("length", "mm", 9_144_001))), RevitCommandCodes.LengthInvalid, "args.value.mm");
        Rejects("set_parameter length beyond 1e8 mm", Envelope("element.set_parameter", Set(Value("length", "mm", -2e8))), RevitCommandCodes.LengthInvalid, "args.value.mm");
        Rejects("set_parameter overflowing length", Envelope("element.set_parameter", Set(Value("length", "mm", JsonNode.Parse("1e400")))), RevitCommandCodes.LengthInvalid, "args.value.mm");
        Rejects("set_parameter negative area", Envelope("element.set_parameter", Set(Value("area", "m2", -1))), RevitCommandCodes.ArgsInvalid, "args.value.m2");
        Rejects("set_parameter area beyond 1e8 m2", Envelope("element.set_parameter", Set(Value("area", "m2", 2e8))), RevitCommandCodes.ArgsInvalid, "args.value.m2");
        Rejects("set_parameter angle beyond 360 degrees", Envelope("element.set_parameter", Set(Value("angle", "deg", 360.5))), RevitCommandCodes.AngleInvalid, "args.value.deg");
        Check(Accepts("set_parameter angle of -360 degrees accepted", Envelope("element.set_parameter", Set(Value("angle", "deg", -360))))?.Args is ElementSetParameterArgs { Value: AngleValueArg }, "angles from -360 to 360 degrees are accepted");
        Rejects("set_parameter angle beyond 1e6 degrees", Envelope("element.set_parameter", Set(Value("angle", "deg", 2e6))), RevitCommandCodes.AngleInvalid, "args.value.deg");
        Rejects("set_parameter fractional integer", Envelope("element.set_parameter", Set(Value("integer", "integer", 1.5))), RevitCommandCodes.ArgsInvalid, "args.value.integer");
        Rejects("set_parameter integer beyond 32 bits", Envelope("element.set_parameter", Set(Value("integer", "integer", 3_000_000_000L))), RevitCommandCodes.ArgsInvalid, "args.value.integer");
        Rejects("set_parameter yes/no as text", Envelope("element.set_parameter", Set(Value("yes_no", "yes_no", "yes"))), RevitCommandCodes.ArgsInvalid, "args.value.yes_no");
        Rejects("set_parameter text with NUL", Envelope("element.set_parameter", Set(Value("text", "text", "a\u0000b"))), RevitCommandCodes.ArgsInvalid, "args.value.text");
        Rejects("set_parameter text over 4096 characters", Envelope("element.set_parameter", Set(Value("text", "text", new string('x', 4097)))), RevitCommandCodes.ArgsInvalid, "args.value.text");
        Rejects("set_parameter element value that is not a UniqueId", Envelope("element.set_parameter", Set(Value("element", "uid", "Level 1"))), RevitCommandCodes.ArgsInvalid, "args.value.uid");
        Rejects("set_parameter value with an extra member", Envelope("element.set_parameter", Set(With(Value("length", "mm", 1), "unit", "mm"))), RevitCommandCodes.ArgsInvalid, "args.value.unit");
        Rejects("set_parameter lower-case built-in name", Envelope("element.set_parameter", Set(Value("text", "text", "x"), new JsonObject { ["built_in"] = "all_model_mark" })), RevitCommandCodes.ArgsInvalid, "args.parameter.built_in");
        Rejects("set_parameter malformed GUID", Envelope("element.set_parameter", Set(Value("text", "text", "x"), new JsonObject { ["guid"] = "not-a-guid" })), RevitCommandCodes.ArgsInvalid, "args.parameter.guid");
        Rejects("set_parameter parameter by both name and built-in", Envelope("element.set_parameter", Set(Value("text", "text", "x"), new JsonObject { ["name"] = "Mark", ["built_in"] = "ALL_MODEL_MARK" })), RevitCommandCodes.ArgsInvalid);
        Rejects("set_parameter blank parameter name", Envelope("element.set_parameter", Set(Value("text", "text", "x"), new JsonObject { ["name"] = "" })), RevitCommandCodes.ArgsInvalid, "args.parameter.name");
        Rejects("set_parameter dry_run not a boolean", Envelope("element.set_parameter", With(Set(Value("length", "mm", 1)), "dry_run", "yes")), RevitCommandCodes.ArgsInvalid, "args.dry_run");
        Rejects("set_parameter with an extra member", Envelope("element.set_parameter", With(Set(Value("length", "mm", 1)), "force", true)), RevitCommandCodes.ArgsInvalid, "args.force");
    }

    static void DeleteBinding()
    {
        var single = Accepts("delete accepted", Envelope("element.delete", Delete(WallUid)));
        Check(single?.Args is ElementDeleteArgs { DryRun: false, ElementUids: [WallUid], ConfirmDependents.Count: 0 }, "delete binds the targets, no confirmed dependents and dry_run false by default");
        var confirmed = Accepts("delete with confirmed dependents accepted", Envelope("element.delete", With(With(Delete(WallUid, LevelUid), "confirm_dependents", new JsonArray(DoorUid, WindowUid)), "dry_run", true)));
        Check(confirmed?.Args is ElementDeleteArgs { DryRun: true, ElementUids: [WallUid, LevelUid], ConfirmDependents: [DoorUid, WindowUid] }, "delete binds targets in order, the confirmed dependents and dry_run");
        Rejects("delete without targets", Envelope("element.delete", Delete()), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Rejects("delete without element_uids", Envelope("element.delete", new JsonObject()), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Rejects("delete over 100 targets", Envelope("element.delete", Delete(Enumerable.Range(1, 101).Select(Hex).ToArray())), RevitCommandCodes.ArgsInvalid, "args.element_uids");
        Check(Accepts("delete of 100 targets accepted", Envelope("element.delete", Delete(Enumerable.Range(1, 100).Select(Hex).ToArray())))?.Args is ElementDeleteArgs { ElementUids.Count: 100 }, "100 targets are allowed");
        Rejects("delete target listed twice (hex case ignored)", Envelope("element.delete", Delete(WallUid, DoorUid, WallUid.ToUpperInvariant())), RevitCommandCodes.ArgsInvalid, "args.element_uids[2]");
        Rejects("delete target that is not a UniqueId", Envelope("element.delete", Delete("Basic Wall")), RevitCommandCodes.ArgsInvalid, "args.element_uids[0]");
        Rejects("delete dependent listed twice", Envelope("element.delete", With(Delete(WallUid), "confirm_dependents", new JsonArray(DoorUid, DoorUid.ToUpperInvariant()))), RevitCommandCodes.ArgsInvalid, "args.confirm_dependents[1]");
        Rejects("delete dependent that is also a target", Envelope("element.delete", With(Delete(WallUid), "confirm_dependents", new JsonArray(WallUid.ToUpperInvariant()))), RevitCommandCodes.ArgsInvalid, "args.confirm_dependents[0]");
        Rejects("delete over 1000 confirmed dependents", Envelope("element.delete", With(Delete(WallUid), "confirm_dependents", new JsonArray(Enumerable.Range(1, 1001).Select(i => (JsonNode?)Hex(i)).ToArray()))), RevitCommandCodes.ArgsInvalid, "args.confirm_dependents");
        Rejects("delete with an extra member", Envelope("element.delete", With(Delete(WallUid), "cascade", true)), RevitCommandCodes.ArgsInvalid, "args.cascade");
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return Answer(line); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        foreach (var command in ModifyCommands)
        {
            foreach (var dryRun in new[] { false, true })
            {
                var args = With(Sample(command), "dry_run", dryRun);
                var label = command + (dryRun ? " dry run" : "");
                var before = forwarded.Count;
                var ok = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, noLinks);
                Check(!ok.IsError && forwarded.Count == before + 1 && forwarded[^1] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString())), label + " on an enabled copy is forwarded as the canonical wire line", ok.Line);
                var refused = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args, RealPath).ToJsonString(), policy, Transport, noLinks);
                Check(refused.IsError && !refused.Forwarded && JsonNode.Parse(refused.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == before + 1, label + " on a real project is document_not_enabled and never forwarded", refused.Line);
                var linked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope(command, args).ToJsonString(), policy, Transport, new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null));
                Check(linked.IsError && JsonNode.Parse(linked.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == before + 1, label + " through a linked directory is document_link_refused");
            }
        }
        var invalid = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("element.set_parameter", Set(Value("length", "mm", 9_144_001))).ToJsonString(), policy, Transport, noLinks);
        Check(invalid.IsError && !invalid.Forwarded && JsonNode.Parse(invalid.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.LengthInvalid && JsonNode.Parse(invalid.Line)!["field"]!.GetValue<string>() == "args.value.mm", "an invalid set_parameter returns the typed failure before the pipe", invalid.Line);
        var real = RevitCommandProtocol.Parse(Envelope("element.delete", Delete(WallUid)).ToJsonString());
        var dry = RevitCommandProtocol.Parse(Envelope("element.delete", With(Delete(WallUid), "dry_run", true)).ToJsonString());
        Check(real.RequestSha256 != dry.RequestSha256, "a dry run and the real request hash differently, so a dry run never answers for the real delete");
        var a = RevitCommandProtocol.Parse("""{"protocol_version":1,"operation_id":"op-m","document":{"path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt","unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b"},"command":"element.set_parameter","args":{"element_uid":"60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3","parameter":{"name":"Unconnected Height"},"value":{"kind":"length","mm":3000}}}""");
        var b = RevitCommandProtocol.Parse("""{"args":{"value":{"mm":3.0e3,"kind":"length"},"parameter":{"name":"Unconnected Height"},"element_uid":"60f91daf-3dd7-4283-a86d-24137b73f3da-0002a1c3"},"command":"element.set_parameter","document":{"unique_id":"60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b","path":"C:\\Repo\\.bim\\native-runs\\r\\a.rvt"},"operation_id":"op-m","protocol_version":1}""");
        Check(a.CanonicalJson == b.CanonicalJson && a.RequestSha256 == b.RequestSha256, "modify requests use the same canonical form and hash as v1 (operation_id replay key)", a.CanonicalJson + " vs " + b.CanonicalJson);
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

    static ObservedParameter Param(string storage, string? spec, bool readOnly = false, string name = "P") => new(name, -1001, null, RevitParameterSources.Instance, storage, spec, true, readOnly, false);
    const string LengthSpec = "autodesk.spec.aec:length-2.0.0";
    const string AreaSpec = "autodesk.spec.aec:area-2.0.0";
    const string AngleSpec = "autodesk.spec.aec:angle-2.0.0";
    const string VolumeSpec = "autodesk.spec.aec:volume-2.0.0";
    const string YesNoSpec = "autodesk.spec:spec.bool-1.0.0";
    const string IntegerSpec = "autodesk.spec:spec.int64-2.0.0";

    static void ParameterRules()
    {
        Check(CommandRulesModify.KindOf(new ParameterTarget(Param("double", LengthSpec), false, false)) == "length"
            && CommandRulesModify.KindOf(new ParameterTarget(Param("double", AreaSpec), false, false)) == "area"
            && CommandRulesModify.KindOf(new ParameterTarget(Param("double", AngleSpec), false, false)) == "angle", "double parameters of length, area and angle specs take those kinds");
        Check(CommandRulesModify.KindOf(new ParameterTarget(Param("double", VolumeSpec), false, false)) is null && CommandRulesModify.KindOf(new ParameterTarget(Param("double", "autodesk.spec.aec.structural:force-2.0.0"), false, false)) is null
            && CommandRulesModify.KindOf(new ParameterTarget(Param("double", null), false, false)) is null, "volumes, other measurable specs and unitless doubles are not settable");
        Check(CommandRulesModify.KindOf(new ParameterTarget(Param("string", "autodesk.spec:spec.string-2.0.0"), false, false)) == "text" && CommandRulesModify.KindOf(new ParameterTarget(Param("element_id", null), false, false)) == "element",
            "string parameters take text and element id parameters take an element");
        Check(CommandRulesModify.KindOf(new ParameterTarget(Param("integer", YesNoSpec), true, false)) == "yes_no" && CommandRulesModify.KindOf(new ParameterTarget(Param("integer", IntegerSpec), false, true)) == "integer"
            && CommandRulesModify.KindOf(new ParameterTarget(Param("integer", null), false, false)) is null && CommandRulesModify.KindOf(new ParameterTarget(Param("none", null), false, false)) is null,
            "integer parameters take yes_no or integer by data type; enumerations and parameters without storage are not settable");

        var lengthTarget = new ParameterTarget(Param("double", LengthSpec, name: "Unconnected Height"), false, false);
        Check(Thrown(() => CommandRulesModify.CheckSettable(lengthTarget, new LengthValueArg(Length.FromMillimetres(3000)))) is null && CommandRulesModify.CheckSettable(lengthTarget, new LengthValueArg(Length.FromMillimetres(3000))) == "length", "a writable length parameter takes a length value");
        var readOnly = Thrown(() => CommandRulesModify.CheckSettable(new ParameterTarget(Param("double", LengthSpec, readOnly: true, name: "Length"), false, false), new LengthValueArg(Length.FromMillimetres(1))));
        Check(readOnly is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.parameter" } && readOnly.Detail!.Contains("read-only", StringComparison.Ordinal) && readOnly.Detail.Contains("Length", StringComparison.Ordinal), "a read-only parameter is refused at args.parameter, named", readOnly?.Detail ?? "");
        var readOnlyText = Thrown(() => CommandRulesModify.CheckSettable(new ParameterTarget(Param("string", null, readOnly: true), false, false), new TextValueArg("x")));
        Check(readOnlyText is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.parameter" }, "read-only is refused before the value kind is considered");
        var mismatch = Thrown(() => CommandRulesModify.CheckSettable(lengthTarget, new AreaValueArg(Area.FromSquareMetres(2))));
        Check(mismatch is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.value.kind" } && mismatch.Detail!.Contains("length", StringComparison.Ordinal), "a value of the wrong kind is refused at args.value.kind naming the expected kind", mismatch?.Detail ?? "");
        Check(Thrown(() => CommandRulesModify.CheckSettable(new ParameterTarget(Param("integer", YesNoSpec), true, false), new IntegerValueArg(1))) is { Field: "args.value.kind" }, "an integer value is refused for a yes/no parameter");
        var enumeration = Thrown(() => CommandRulesModify.CheckSettable(new ParameterTarget(Param("integer", null, name: "Location Line"), false, false), new IntegerValueArg(2)));
        Check(enumeration is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.value" } && enumeration.Detail!.Contains("Location Line", StringComparison.Ordinal), "an enumerated integer parameter is refused as not settable", enumeration?.Detail ?? "");
        Check(Thrown(() => CommandRulesModify.CheckNamedMatches(1, "Comments")) is null, "a name matching exactly one parameter resolves");
        Check(Thrown(() => CommandRulesModify.CheckNamedMatches(0, "Comments")) is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.parameter.name" }, "a name matching no parameter is reference_not_found");
        var ambiguous = Thrown(() => CommandRulesModify.CheckNamedMatches(2, "Comments"));
        Check(ambiguous is { Code: RevitCommandCodes.ReferenceNotFound, Field: "args.parameter.name" } && ambiguous.Detail!.Contains("built_in", StringComparison.Ordinal), "an ambiguous name asks for built_in or guid", ambiguous?.Detail ?? "");

        var before = Param("double", LengthSpec) with { Number = 3000, Unit = "mm" };
        Check(CommandRulesModify.SameValue("length", before, before with { Number = 3000.0004 }) && !CommandRulesModify.SameValue("length", before, before with { Number = 3000.01 }), "same length within 0.001 mm");
        Check(CommandRulesModify.SameValue("text", Param("string", null) with { Text = null, HasValue = false }, Param("string", null) with { Text = "" }) && !CommandRulesModify.SameValue("text", Param("string", null) with { Text = "a" }, Param("string", null) with { Text = "b" }), "no text and empty text are the same value; different texts are not");
        Check(CommandRulesModify.SameValue("element", Param("element_id", null) with { ElementUid = LevelUid }, Param("element_id", null) with { ElementUid = LevelUid.ToUpperInvariant() }) && !CommandRulesModify.SameValue("yes_no", Param("integer", null) with { Integer = 0 }, Param("integer", null) with { Integer = 1 }), "element values compare by UniqueId; yes/no by stored integer");
    }

    static DeleteTargetFacts Target(string uid) => new(uid, true, false, false, false, false, false);

    static void DeleteRules()
    {
        Check(Thrown(() => CommandRulesModify.CheckDeletable(Target(WallUid), "args.element_uids[0]")) is null, "an ordinary element may be deleted");
        foreach (var (facts, word) in new[]
        {
            (Target(WallUid) with { Pinned = true }, "pinned"),
            (Target(WallUid) with { InGroup = true }, "group"),
            (Target(Uid) with { ProjectInformation = true }, "Project Information"),
            (Target(StoreUid) with { ReceiptStore = true }, "receipt store"),
            (Target(WallUid) with { ActiveView = true }, "active view"),
            (Target(WallUid) with { CanDelete = false }, "CanDeleteElement"),
        })
        {
            var refusal = Thrown(() => CommandRulesModify.CheckDeletable(facts, "args.element_uids[3]"));
            Check(refusal is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uids[3]" } && refusal.Detail!.Contains(word, StringComparison.Ordinal) && refusal.Detail.Contains(facts.UniqueId, StringComparison.Ordinal), "a delete target that is " + word + " is refused at its index", refusal?.Detail ?? "");
        }

        var cascade = CommandRulesModify.Cascade(new[] { WallUid }, new string?[] { WindowUid.ToUpperInvariant(), WallUid.ToUpperInvariant(), DoorUid, DoorUid });
        Check(cascade.Unidentified == 0 && cascade.Dependents.Count == 2 && string.Equals(cascade.Dependents[0], DoorUid, StringComparison.OrdinalIgnoreCase) && string.Equals(cascade.Dependents[1], WindowUid, StringComparison.OrdinalIgnoreCase),
            "the cascade is every deleted element except the targets, distinct and sorted", string.Join(",", cascade.Dependents));
        Check(CommandRulesModify.Cascade(new[] { WallUid }, new string?[] { WallUid }).Dependents.Count == 0, "a delete that removes only its target has no dependents");
        Check(CommandRulesModify.Cascade(new[] { WallUid }, new string?[] { WallUid, null, DoorUid }) is { Unidentified: 1, Dependents.Count: 1 }, "deleted ids without an element are counted as unidentified");
        Check(Thrown(() => CommandRulesModify.Cascade(new[] { WallUid, LevelUid }, new string?[] { WallUid })) is { Code: RevitCommandCodes.TransactionFailed, Field: "args.element_uids[1]" }, "a target Revit would not delete fails at its index");
        Check(Thrown(() => CommandRulesModify.CheckCascade(new DeleteCascade(new[] { DoorUid }, 0), new[] { Uid, StoreUid })) is null, "a fully named cascade that reaches nothing protected passes");
        Check(Thrown(() => CommandRulesModify.CheckCascade(new DeleteCascade(new[] { DoorUid }, 2), Array.Empty<string>())) is { Code: RevitCommandCodes.TransactionFailed }, "a cascade with unnamed elements is refused");
        var reachesStore = Thrown(() => CommandRulesModify.CheckCascade(new DeleteCascade(new[] { DoorUid, StoreUid.ToUpperInvariant() }, 0), new[] { Uid, StoreUid }));
        Check(reachesStore is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uids" } && reachesStore.Detail!.Contains("protected", StringComparison.Ordinal), "a cascade that would remove a protected element is refused", reachesStore?.Detail ?? "");

        var dependents = new[] { DoorUid, WindowUid };
        Check(Thrown(() => CommandRulesModify.CheckConfirmed(dependents, new[] { WindowUid.ToUpperInvariant(), DoorUid })) is null && CommandRulesModify.Confirmed(dependents, new[] { WindowUid, DoorUid }), "dependents confirmed exactly (any order, any hex case) pass");
        Check(Thrown(() => CommandRulesModify.CheckConfirmed(Array.Empty<string>(), Array.Empty<string>())) is null, "a delete without dependents needs no confirmation");
        var unconfirmed = Thrown(() => CommandRulesModify.CheckConfirmed(dependents, Array.Empty<string>()));
        Check(unconfirmed is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.confirm_dependents" } && unconfirmed.Detail!.Contains(DoorUid, StringComparison.Ordinal) && unconfirmed.Detail.Contains(WindowUid, StringComparison.Ordinal) && unconfirmed.Detail.Contains("dry run", StringComparison.Ordinal),
            "an unconfirmed cascade is refused naming every dependent and asking for a dry run", unconfirmed?.Detail ?? "");
        var partial = Thrown(() => CommandRulesModify.CheckConfirmed(dependents, new[] { DoorUid }));
        Check(partial is { Field: "args.confirm_dependents" } && partial.Detail!.Contains(WindowUid, StringComparison.Ordinal) && !CommandRulesModify.Confirmed(dependents, new[] { DoorUid }), "a partly confirmed cascade is refused naming the unconfirmed dependent");
        var stale = Thrown(() => CommandRulesModify.CheckConfirmed(new[] { DoorUid }, new[] { DoorUid, LevelUid }));
        Check(stale is { Field: "args.confirm_dependents" } && stale.Detail!.Contains(LevelUid, StringComparison.Ordinal), "a confirmed dependent Revit would no longer delete is refused (the model changed since the dry run)", stale?.Detail ?? "");
        var many = Enumerable.Range(1, 30).Select(Hex).ToArray();
        var long_ = Thrown(() => CommandRulesModify.CheckConfirmed(many, Array.Empty<string>()));
        Check(long_ is { Field: "args.confirm_dependents" } && long_.Detail!.Contains("30", StringComparison.Ordinal) && long_.Detail.Contains("10 more", StringComparison.Ordinal) && !long_.Detail.Contains(many[25], StringComparison.Ordinal), "a long refusal names 20 dependents and counts the rest", long_?.Detail ?? "");
        Check(Thrown(() => CommandRulesModify.CheckSameCascade(new long[] { 5, 9, 7 }, new long[] { 7, 5, 9 })) is null, "the kept delete removing the probe's ids passes");
        Check(Thrown(() => CommandRulesModify.CheckSameCascade(new long[] { 5, 9 }, new long[] { 5, 9, 11 })) is { Code: RevitCommandCodes.TransactionFailed } && Thrown(() => CommandRulesModify.CheckSameCascade(new long[] { 5, 9 }, new long[] { 5 })) is { Code: RevitCommandCodes.TransactionFailed },
            "a kept delete that removes more or fewer elements than its probe rolls back");
    }

    static void Readbacks()
    {
        var length = Param("double", LengthSpec) with { Number = 3000.0005, Unit = "mm" };
        Check(CommandReadbackModify.Parameter(new LengthValueArg(Length.FromMillimetres(3000)), length) is null, "a length read back within 0.001 mm matches");
        Check(CommandReadbackModify.Parameter(new LengthValueArg(Length.FromMillimetres(3000)), length with { Number = 3000.002 }) is { } off && off.Contains("3000.002", StringComparison.Ordinal), "a length read back 0.002 mm off is a mismatch naming the value");
        Check(CommandReadbackModify.Parameter(new LengthValueArg(Length.FromMillimetres(3000)), length with { Unit = "m2" }) is not null, "a length read back in another unit is a mismatch");
        var area = Param("double", AreaSpec) with { Number = 12.5000004, Unit = "m2" };
        Check(CommandReadbackModify.Parameter(new AreaValueArg(Area.FromSquareMetres(12.5)), area) is null && CommandReadbackModify.Parameter(new AreaValueArg(Area.FromSquareMetres(12.5)), area with { Number = 12.51 }) is not null, "areas read back within 1e-6 m2");
        var angle = Param("double", AngleSpec) with { Number = 90.0000001, Unit = "deg" };
        Check(CommandReadbackModify.Parameter(new AngleValueArg(Angle.FromDegrees(90)), angle) is null && CommandReadbackModify.Parameter(new AngleValueArg(Angle.FromDegrees(90)), angle with { Number = 90.01 }) is not null, "angles read back within 1e-6 degrees");
        var text = Param("string", null) with { Text = "Fire rated" };
        Check(CommandReadbackModify.Parameter(new TextValueArg("Fire rated"), text) is null && CommandReadbackModify.Parameter(new TextValueArg("Fire Rated"), text) is not null, "text reads back ordinally");
        Check(CommandReadbackModify.Parameter(new TextValueArg(""), Param("string", null) with { HasValue = false }) is null && CommandReadbackModify.Parameter(new TextValueArg("x"), Param("string", null) with { HasValue = false }) is not null, "a cleared text may read back without a value; any other value must be present");
        Check(CommandReadbackModify.Parameter(new IntegerValueArg(-7), Param("integer", IntegerSpec) with { Integer = -7 }) is null && CommandReadbackModify.Parameter(new IntegerValueArg(-7), Param("integer", IntegerSpec) with { Integer = 7 }) is not null, "integers read back exactly");
        Check(CommandReadbackModify.Parameter(new YesNoValueArg(true), Param("integer", YesNoSpec) with { Integer = 1 }) is null && CommandReadbackModify.Parameter(new YesNoValueArg(false), Param("integer", YesNoSpec) with { Integer = 1 }) is not null, "yes/no reads back as 1 or 0");
        Check(CommandReadbackModify.Parameter(new ElementValueArg(LevelUid), Param("element_id", null) with { ElementUid = LevelUid.ToUpperInvariant() }) is null && CommandReadbackModify.Parameter(new ElementValueArg(LevelUid), Param("element_id", null) with { ElementUid = null }) is not null, "element values read back by UniqueId");
        Check(CommandReadbackModify.Deleted(Array.Empty<string>()) is null && CommandReadbackModify.Deleted(new[] { DoorUid }) is { } present && present.Contains(DoorUid, StringComparison.Ordinal), "a delete reads back as nothing left; a surviving element is a mismatch naming it");
    }

    static void ReadbackJson()
    {
        var before = new ObservedParameter("Unconnected Height", -1001300, "WALL_USER_HEIGHT_PARAM", RevitParameterSources.Instance, "double", LengthSpec, true, false, false) { Number = 3000, Unit = "mm" };
        var report = JsonNode.Parse(CommandReadbackModify.ParameterJson(true, WallUid, "length", before, before with { Number = 3200 }, true, new[] { DoorUid }))!["set_parameter"]!;
        Check(report["dry_run"]!.GetValue<bool>() && report["element_unique_id"]!.GetValue<string>() == WallUid && report["kind"]!.GetValue<string>() == "length" && report["changed"]!.GetValue<bool>()
            && report["before"]!["kind"]!.GetValue<string>() == "length" && report["before"]!["mm"]!.GetValue<double>() == 3000 && report["after"]!["mm"]!.GetValue<double>() == 3200
            && report["parameter"]!["name"]!.GetValue<string>() == "Unconnected Height" && report["parameter"]!["built_in"]!.GetValue<string>() == "WALL_USER_HEIGHT_PARAM" && report["parameter"]!["id"]!.GetValue<long>() == -1001300
            && report["parameter"]!["storage"]!.GetValue<string>() == "double" && report["parameter"]!["spec"]!.GetValue<string>() == LengthSpec && !report["parameter"]!["shared"]!.GetValue<bool>(),
            "set_parameter change report: dry run flag, element, parameter identity, kind, before and after in millimetres, changed", report.ToJsonString());
        Check(report["also_modified"]!.AsArray().Select(uid => uid!.GetValue<string>()).SequenceEqual(new[] { DoorUid }) && report["also_modified_count"]!.GetValue<int>() == 1 && report["also_modified_listed"]!.GetValue<bool>(),
            "set_parameter change report lists the other elements the change modified", report.ToJsonString());
        var cleared = JsonNode.Parse(CommandReadbackModify.ParameterJson(false, WallUid, "text", Param("string", null) with { Text = "old" }, Param("string", null) with { HasValue = false }, true, Array.Empty<string>()))!["set_parameter"]!;
        Check(!cleared["dry_run"]!.GetValue<bool>() && cleared["before"]!["text"]!.GetValue<string>() == "old" && cleared["after"] is null && cleared["parameter"]!["built_in"] is null && cleared["parameter"]!["spec"] is null, "a value-less parameter reports null; unknown identity members are null", cleared.ToJsonString());
        var shapes = new (string Kind, ObservedParameter Observed, string Member, Func<JsonNode, bool> Holds)[]
        {
            ("area", Param("double", AreaSpec) with { Number = 12.5, Unit = "m2" }, "m2", node => node.GetValue<double>() == 12.5),
            ("angle", Param("double", AngleSpec) with { Number = 90, Unit = "deg" }, "deg", node => node.GetValue<double>() == 90),
            ("integer", Param("integer", IntegerSpec) with { Integer = -7 }, "integer", node => node.GetValue<long>() == -7),
            ("yes_no", Param("integer", YesNoSpec) with { Integer = 1 }, "yes_no", node => node.GetValue<bool>()),
            ("element", Param("element_id", null) with { ElementUid = LevelUid }, "uid", node => node.GetValue<string>() == LevelUid),
        };
        foreach (var (kind, observed, member, holds) in shapes)
        {
            var value = JsonNode.Parse(CommandReadbackModify.ParameterJson(false, WallUid, kind, observed, observed, false, Array.Empty<string>()))!["set_parameter"]!["after"]!;
            Check(value["kind"]!.GetValue<string>() == kind && value[member] is { } node && holds(node) && value.AsObject().Count == 2, kind + " values are reported in the request shape {kind, " + member + "}", value.ToJsonString());
        }
        var delete = JsonNode.Parse(CommandReadbackModify.DeleteJson(true, new[] { WallUid }, new[] { DoorUid, WindowUid }, false))!["delete"]!;
        Check(delete["dry_run"]!.GetValue<bool>() && delete["targets"]!.AsArray().Select(uid => uid!.GetValue<string>()).SequenceEqual(new[] { WallUid }) && delete["dependents"]!.AsArray().Select(uid => uid!.GetValue<string>()).SequenceEqual(new[] { DoorUid, WindowUid })
            && delete["deleted_count"]!.GetValue<int>() == 3 && !delete["confirmed"]!.GetValue<bool>(), "delete change report: dry run flag, targets, dependents, total count and whether the dependents were confirmed", delete.ToJsonString());

        Check(delete["dependents_count"]!.GetValue<int>() == 2 && delete["dependents_listed"]!.GetValue<bool>() && delete["dependents_sha256"]!.GetValue<string>() == CommandReadbackModify.DependentsSha256(new[] { WindowUid.ToUpperInvariant(), DoorUid }),
            "delete change report carries the dependents count and an order- and case-independent SHA-256", delete.ToJsonString());
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DoorUid + "\n" + WindowUid))).ToLowerInvariant();
        Check(CommandReadbackModify.DependentsSha256(new[] { WindowUid, DoorUid }) == expectedHash && CommandReadbackModify.DependentsSha256(Array.Empty<string>()) == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant(),
            "the dependents hash is SHA-256 of the lower-case UniqueIds, sorted, joined by newlines");
    }

    static CommandRequest Request(string command, JsonObject args)
    {
        var envelope = RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString());
        return new CommandRequest(envelope, envelope.RequestSha256);
    }

    static void SideEffects()
    {
        // Review item 1: the kept delete may remove nothing beyond the probe's set (pre/post id snapshot).
        Check(Thrown(() => CommandRulesModify.CheckNoExtraDeletions(new long[] { 5, 9, 11 }, new long[] { 11, 5 })) is null, "a kept delete that removed only probed ids passes");
        var extra = Thrown(() => CommandRulesModify.CheckNoExtraDeletions(new long[] { 5, 9 }, new long[] { 5, 9, 4242 }));
        Check(extra is { Code: RevitCommandCodes.TransactionFailed } && extra.Detail!.Contains("4242", StringComparison.Ordinal), "a kept delete that removed an id outside the probe set is transaction_failed naming it", extra?.Detail ?? "");

        // Review item 2: every dependent passes the same deletable facts as a target.
        Check(Thrown(() => CommandRulesModify.CheckDependents(new[] { Target(DoorUid), Target(WindowUid) })) is null, "ordinary dependents pass");
        foreach (var (facts, word) in new[]
        {
            (Target(DoorUid) with { Pinned = true }, "pinned"),
            (Target(DoorUid) with { InGroup = true }, "group"),
            (Target(DoorUid) with { ActiveView = true }, "active view"),
            (Target(DoorUid) with { CanDelete = false }, "CanDeleteElement"),
        })
        {
            var refusal = Thrown(() => CommandRulesModify.CheckDependents(new[] { Target(WindowUid), facts }));
            Check(refusal is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uids" } && refusal.Detail!.Contains(word, StringComparison.Ordinal) && refusal.Detail.Contains(DoorUid, StringComparison.Ordinal) && refusal.Detail.Contains("dependent", StringComparison.Ordinal),
                "a dependent that is " + word + " refuses the delete, naming it", refusal?.Detail ?? "");
        }

        // Review item 3: a cascade beyond what confirm_dependents can hold is refused, dry run or not.
        var tooMany = Enumerable.Range(1, RevitModifyRules.MaxConfirmedDependents + 1).Select(Hex).ToArray();
        Check(Thrown(() => CommandRulesModify.CheckCascade(new DeleteCascade(tooMany.Take(RevitModifyRules.MaxConfirmedDependents).ToArray(), 0), Array.Empty<string>())) is null, "a cascade of 1000 dependents can be confirmed");
        var huge = Thrown(() => CommandRulesModify.CheckCascade(new DeleteCascade(tooMany, 0), Array.Empty<string>()));
        Check(huge is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uids" } && huge.Detail!.Contains("1001", StringComparison.Ordinal), "a cascade of 1001 dependents is args_invalid (checked before the dry-run report)", huge?.Detail ?? "");

        // Review item 4: a parameter change may not delete anything; other modified elements are reported.
        var others = CommandRulesModify.SetParameterEffects(100, Array.Empty<long>(), new long[] { 300, 100, 200, 300 });
        Check(others.SequenceEqual(new long[] { 200, 300 }), "elements modified besides the target are reported once, sorted", string.Join(",", others));
        Check(CommandRulesModify.SetParameterEffects(100, Array.Empty<long>(), new long[] { 100 }).Count == 0, "a change that modifies only its target reports nothing else");
        var deletes = Thrown(() => CommandRulesModify.SetParameterEffects(100, new long[] { 77 }, Array.Empty<long>()));
        Check(deletes is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.value" } && deletes.Detail!.Contains("77", StringComparison.Ordinal) && deletes.Detail.Contains("delete", StringComparison.Ordinal), "a parameter change that deletes elements is refused naming them", deletes?.Detail ?? "");

        // NITs: group members are refused for set_parameter; Revit's own argument refusals become args_invalid.
        Check(Thrown(() => CommandRulesModify.CheckParameterElement(WallUid, false)) is null, "an element outside groups takes a parameter change");
        Check(Thrown(() => CommandRulesModify.CheckParameterElement(WallUid, true)) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.element_uid" } refusedGroup && refusedGroup.Detail!.Contains("group", StringComparison.Ordinal), "a group member is refused for a parameter change");
        Check(CommandRulesModify.RevitRefused("args.value", "The value is out of range.") is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.value" } refusedByRevit && refusedByRevit.Detail!.Contains("out of range", StringComparison.Ordinal), "a Revit argument or operation refusal is args_invalid with Revit's message");
    }

    // Review item 5: reply assembly and the receipt decision, Autodesk-free.
    static void Replies()
    {
        Check(CommandRulesModify.StoresReceipt(false) && !CommandRulesModify.StoresReceipt(true), "real runs store a receipt (and need room for it); dry runs never do");
        var setRequest = Request("element.set_parameter", Set(Value("length", "mm", 3000)));
        var report = CommandReadbackModify.ParameterJson(true, WallUid, "length", Param("double", LengthSpec), Param("double", LengthSpec), true, new[] { DoorUid });
        var drySet = CommandResponsesModify.SetParameter(setRequest, true, WallUid, true, new[] { DoorUid }, report, new[] { "warning" });
        Check(!drySet.Persist && drySet.Response.IsSuccess && drySet.Response.Created.Count == 0 && drySet.Response.Modified.Count == 0 && drySet.Response.Deleted.Count == 0 && drySet.Response.ReadbackJson == report && drySet.Response.Warnings.SequenceEqual(new[] { "warning" })
            && drySet.Response.Receipt == new RevitCommandReceipt("op-modify", "element.set_parameter", Uid, setRequest.RequestSha256, false), "a dry-run set_parameter reply is a success with empty lists, the report and a receipt answering the request, and is not persisted");
        var realSet = CommandResponsesModify.SetParameter(setRequest, false, WallUid, true, new[] { DoorUid }, report, Array.Empty<string>());
        Check(realSet.Persist && realSet.Response.Modified.SequenceEqual(new[] { WallUid, DoorUid }) && realSet.Response.Created.Count == 0 && realSet.Response.Deleted.Count == 0, "a real set_parameter reply lists the target and the other modified elements and is persisted");
        Check(CommandResponsesModify.SetParameter(setRequest, false, WallUid, false, Array.Empty<string>(), report, Array.Empty<string>()).Response.Modified.Count == 0, "an unchanged value modifies nothing");
        var manyOthers = Enumerable.Range(1, 500).Select(Hex).ToArray();
        var bulkSet = CommandResponsesModify.SetParameter(setRequest, false, WallUid, true, manyOthers, report, Array.Empty<string>());
        Check(bulkSet.Response.Modified.SequenceEqual(new[] { WallUid }), "beyond 100 other modified elements the reply lists only the target (count in the report)");
        var bulkReport = JsonNode.Parse(CommandReadbackModify.ParameterJson(false, WallUid, "length", Param("double", LengthSpec), Param("double", LengthSpec), true, manyOthers))!["set_parameter"]!;
        Check(bulkReport["also_modified_count"]!.GetValue<int>() == 500 && !bulkReport["also_modified_listed"]!.GetValue<bool>() && bulkReport["also_modified"]!.AsArray().Count == CommandRulesModify.MaxDependentsListed, "the report bounds the other modified elements to 100 and counts all of them");

        var deleteRequest = Request("element.delete", Delete(WallUid));
        var dryDelete = CommandResponsesModify.Delete(deleteRequest, true, new[] { WallUid }, new[] { DoorUid }, Array.Empty<string>(), Array.Empty<string>());
        Check(!dryDelete.Persist && dryDelete.Response.Deleted.Count == 0 && dryDelete.Response.Modified.Count == 0 && JsonNode.Parse(dryDelete.Response.ReadbackJson!)!["delete"]!["dependents"]![0]!.GetValue<string>() == DoorUid
            && !JsonNode.Parse(dryDelete.Response.ReadbackJson!)!["delete"]!["confirmed"]!.GetValue<bool>(), "a dry-run delete reply deletes nothing, lists the dependents, is unconfirmed and is not persisted");
        var keptDelete = CommandResponsesModify.Delete(deleteRequest, false, new[] { WallUid }, new[] { DoorUid }, new[] { DoorUid }, Array.Empty<string>());
        Check(keptDelete.Persist && keptDelete.Response.Deleted.SequenceEqual(new[] { WallUid, DoorUid }) && JsonNode.Parse(keptDelete.Response.ReadbackJson!)!["delete"]!["confirmed"]!.GetValue<bool>(), "a kept delete reply lists targets then dependents, confirmed, and is persisted");
        Check(RevitCommandResponse.Parse(keptDelete.Response.ToJson()).Deleted.SequenceEqual(new[] { WallUid, DoorUid }), "the kept delete reply round-trips through the strict response reader");

        // Receipt size: a 1000-dependent kept delete stays compact; its dry run still lists every dependent.
        var thousand = Enumerable.Range(1, RevitModifyRules.MaxConfirmedDependents).Select(Hex).ToArray();
        var targets = Enumerable.Range(5000, RevitModifyRules.MaxDeleteTargets).Select(Hex).ToArray();
        var bigKept = CommandResponsesModify.Delete(deleteRequest, false, targets, thousand, thousand, Array.Empty<string>());
        var bigJson = bigKept.Response.ToJson();
        var bigReport = JsonNode.Parse(bigKept.Response.ReadbackJson!)!["delete"]!;
        Check(bigJson.Length < 16384 && bigKept.Response.Deleted.SequenceEqual(targets) && bigReport["dependents"] is null && !bigReport["dependents_listed"]!.GetValue<bool>()
            && bigReport["dependents_count"]!.GetValue<int>() == 1000 && bigReport["dependents_sha256"]!.GetValue<string>() == CommandReadbackModify.DependentsSha256(thousand) && bigReport["deleted_count"]!.GetValue<int>() == 1100,
            "a kept delete of 100 targets and 1000 dependents replies (and stores) under 16 KiB: targets listed, dependents as count and hash", bigJson.Length + " characters");
        var bigDry = CommandResponsesModify.Delete(deleteRequest, true, targets, thousand, Array.Empty<string>(), Array.Empty<string>());
        Check(JsonNode.Parse(bigDry.Response.ReadbackJson!)!["delete"]!["dependents"]!.AsArray().Count == 1000, "the dry run of the same delete lists all 1000 dependents (never stored)");
        var hundred = thousand.Take(CommandRulesModify.MaxDependentsListed).ToArray();
        Check(CommandResponsesModify.Delete(deleteRequest, false, new[] { WallUid }, hundred, hundred, Array.Empty<string>()).Response.Deleted.Count == 101, "up to 100 dependents are listed as deleted");
        var receipts = new Dictionary<string, StoredCommandReceipt>();
        for (var i = 0; i < 200; i++) receipts["op-" + i] = CommandReceiptStore.Record(deleteRequest with { Envelope = deleteRequest.Envelope with { OperationId = "op-" + i } } , bigKept.Response with { Receipt = bigKept.Response.Receipt! with { OperationId = "op-" + i } });
        Check(Thrown(() => CommandReceiptStore.SerializeBounded(receipts)) is null, "200 maximal kept deletes fit the 4 MB receipt store (a full 1100-UniqueId list would hold about 70)");
    }
}
