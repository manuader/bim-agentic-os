using Bim.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
// P1-02 generic element query contract oracle: query.elements and query.inspect catalogue entries (read-only),
// argument binding, deterministic paging and the reply JSON of core/Bim.Core/RevitCommandsQuery.cs. No Revit is
// involved: a passing run is unit-tested evidence only; the add-in collector (CommandsExecutorQuery.cs) is compiled.
static class ElementQueryChecks
{
    const string Doc = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string E1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001001";
    const string E2 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001002";
    const string E3 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00001003";
    const string L1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000137";
    const string T1 = "60f91daf-3dd7-4283-a86d-24137b73f3da-00000501";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        Paging();
        ElementsReply();
        InspectReply();
        ReplyBytes();
        SpecUnits();
        Collector();
    }

    static JsonObject Envelope(string command, JsonNode args) => new() { ["protocol_version"] = 1, ["operation_id"] = "op-query", ["document"] = new JsonObject { ["path"] = RealPath, ["unique_id"] = Doc }, ["command"] = command, ["args"] = args };

    static RevitCommandArgs? Accepts(string name, string command, JsonNode args)
    {
        try { var parsed = RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString()).Args; Check(true, name); return parsed; }
        catch (RevitCommandException error) { Check(false, name, error.Code + " at " + error.Field + " (" + error.Detail + ")"); return null; }
    }

    static void Rejects(string name, string command, JsonNode args, string code, string field)
    {
        RevitCommandException? error = null;
        try { RevitCommandProtocol.Parse(Envelope(command, args).ToJsonString()); }
        catch (RevitCommandException refused) { error = refused; }
        Check(error is not null && error.Code == code && error.Field == field, name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }

    static void Catalogue()
    {
        Check(RevitCommandCatalogue.Elements.Select(spec => spec.Name).SequenceEqual(new[] { "query.elements", "query.inspect" }), "element query catalogue part lists query.elements then query.inspect");
        var throughElements = RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror).Concat(RevitCommandCatalogue.Elements).ToArray();
        Check(RevitCommandCatalogue.All.Take(throughElements.Length).SequenceEqual(throughElements), "All is v1, v2, views, snapshot, then the element queries (later catalogue parts follow)");
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        foreach (var (name, tool) in new[] { ("query.elements", "revit_query_elements"), ("query.inspect", "revit_query_inspect") })
        {
            var spec = RevitCommandCatalogue.Find(name);
            Check(spec is { Writes: false } && spec.ToolName == tool, name + " is read-only with tool " + tool);
            Check(spec is not null && spec.Description.EndsWith('.') && spec.Description.Contains("Read-only", StringComparison.Ordinal), name + " description ends with Read-only.", spec?.Description ?? "");
            Check(PermissionPolicy.RequiredLevel(name, FixturePath, copies) == PermissionLevel.Read && PermissionPolicy.RequiredLevel(name, RealPath, copies) == PermissionLevel.Read, name + " requires read on a copy and on a real project");
            string? unsupported = null;
            if (spec is not null)
            {
                using var schema = JsonDocument.Parse(spec.ArgsSchemaJson);
                try { RevitCommandSchema.CheckSupported(schema.RootElement); } catch (InvalidOperationException error) { unsupported = error.Message; }
            }
            Check(spec is not null && unsupported is null, name + " args schema uses only the enforced schema subset", unsupported ?? "");
        }
        var categories = RevitElementQueryRules.Categories;
        Check(categories.Count >= 20 && categories.Select(c => c.Name).Distinct().Count() == categories.Count && categories.Select(c => c.BuiltInCategory).Distinct().Count() == categories.Count
            && categories.All(c => System.Text.RegularExpressions.Regex.IsMatch(c.Name, @"\A[a-z]+(_[a-z]+)*\z") && System.Text.RegularExpressions.Regex.IsMatch(c.BuiltInCategory, @"\AOST_[A-Za-z][A-Za-z0-9_]*\z")),
            "friendly categories are distinct snake_case names, each for a distinct OST_ BuiltInCategory");
        Check(categories.Any(c => c == ("wall", "OST_Walls")) && categories.Any(c => c == ("door", "OST_Doors")) && categories.Any(c => c == ("window", "OST_Windows")) && categories.Any(c => c == ("room", "OST_Rooms")) && categories.Any(c => c == ("floor", "OST_Floors")),
            "wall, door, window, room and floor map to their Revit categories");
        Check(RevitElementQueryRules.DefaultParameters.SequenceEqual(new[] { "ALL_MODEL_MARK", "ALL_MODEL_INSTANCE_COMMENTS" }), "default parameters are Mark and Comments as BuiltInParameter names");
        Check(RevitElementQueryRules.MaxLimit == 5000 && RevitElementQueryRules.DefaultLimit == 1000 && RevitElementQueryRules.MaxParameterNames == 16 && RevitElementQueryRules.MaxInspectParameters == 2000 && RevitElementQueryRules.MaxStringChars == 1024, "bounds: 5000 elements (default 1000), 16 parameter names, 2000 inspected parameters, 1024-character strings");
    }

    static void Binding()
    {
        var walls = Accepts("query.elements by friendly category accepted", "query.elements", new JsonObject { ["category"] = "wall" }) as ElementQueryArgs;
        Check(walls is { Category: "wall", BuiltInCategory: "OST_Walls", Level: null, Limit: RevitElementQueryRules.DefaultLimit } && walls.Parameters.Count == 0, "a friendly category binds its OST_ name, no level, the default limit and no named parameters");
        var doors = Accepts("query.elements by OST_ category accepted", "query.elements", new JsonObject { ["category"] = "OST_Doors" }) as ElementQueryArgs;
        Check(doors is { Category: "OST_Doors", BuiltInCategory: "OST_Doors" }, "an OST_ category binds as given");
        var onLevel = Accepts("query.elements by level accepted", "query.elements", new JsonObject { ["level"] = "Level 1" }) as ElementQueryArgs;
        Check(onLevel is { Category: null, BuiltInCategory: null, Level: "Level 1" }, "a level-only filter binds no category");
        var both = Accepts("query.elements by category, level, parameters and limit accepted", "query.elements", new JsonObject { ["category"] = "door", ["level"] = L1, ["parameters"] = new JsonArray("Fire Rating", "Mark"), ["limit"] = 5000 }) as ElementQueryArgs;
        Check(both is { BuiltInCategory: "OST_Doors", Level: L1, Limit: 5000 } && both.Parameters.SequenceEqual(new[] { "Fire Rating", "Mark" }), "category, level, parameter names (in order) and the maximum limit bind");
        Rejects("query.elements without a filter", "query.elements", new JsonObject(), RevitCommandCodes.ArgsInvalid, "args");
        Rejects("query.elements with only parameters", "query.elements", new JsonObject { ["parameters"] = new JsonArray("Mark") }, RevitCommandCodes.ArgsInvalid, "args");
        Rejects("query.elements with an unknown friendly category", "query.elements", new JsonObject { ["category"] = "walls" }, RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.elements with a lower-case ost_ name", "query.elements", new JsonObject { ["category"] = "ost_walls" }, RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.elements with a comma list of categories", "query.elements", new JsonObject { ["category"] = "OST_Walls,OST_Doors" }, RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.elements with a numeric OST_ value", "query.elements", new JsonObject { ["category"] = "OST_-2000011" }, RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.elements with a bare OST_", "query.elements", new JsonObject { ["category"] = "OST_" }, RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.elements with a category that is not a string", "query.elements", new JsonObject { ["category"] = 7 }, RevitCommandCodes.ArgsInvalid, "args.category");
        Rejects("query.elements with a blank level", "query.elements", new JsonObject { ["level"] = "" }, RevitCommandCodes.ArgsInvalid, "args.level");
        Rejects("query.elements with limit 0", "query.elements", new JsonObject { ["category"] = "wall", ["limit"] = 0 }, RevitCommandCodes.ArgsInvalid, "args.limit");
        Rejects("query.elements with limit 5001", "query.elements", new JsonObject { ["category"] = "wall", ["limit"] = 5001 }, RevitCommandCodes.ArgsInvalid, "args.limit");
        Rejects("query.elements with a fractional limit", "query.elements", new JsonObject { ["category"] = "wall", ["limit"] = 1.5 }, RevitCommandCodes.ArgsInvalid, "args.limit");
        Rejects("query.elements with a huge limit", "query.elements", new JsonObject { ["category"] = "wall", ["limit"] = 1e12 }, RevitCommandCodes.ArgsInvalid, "args.limit");
        Rejects("query.elements with an empty parameter list", "query.elements", new JsonObject { ["category"] = "wall", ["parameters"] = new JsonArray() }, RevitCommandCodes.ArgsInvalid, "args.parameters");
        Rejects("query.elements with 17 parameter names", "query.elements", new JsonObject { ["category"] = "wall", ["parameters"] = new JsonArray(Enumerable.Range(0, 17).Select(i => (JsonNode?)JsonValue.Create("P" + i)).ToArray()) }, RevitCommandCodes.ArgsInvalid, "args.parameters");
        Rejects("query.elements with a duplicate parameter name", "query.elements", new JsonObject { ["category"] = "wall", ["parameters"] = new JsonArray("Mark", "Mark") }, RevitCommandCodes.ArgsInvalid, "args.parameters[1]");
        Rejects("query.elements with a blank parameter name", "query.elements", new JsonObject { ["category"] = "wall", ["parameters"] = new JsonArray("") }, RevitCommandCodes.ArgsInvalid, "args.parameters[0]");
        Rejects("query.elements with a control character in a parameter name", "query.elements", new JsonObject { ["category"] = "wall", ["parameters"] = new JsonArray("Mark\n") }, RevitCommandCodes.ArgsInvalid, "args.parameters[0]");
        Rejects("query.elements with an unexpected member", "query.elements", new JsonObject { ["category"] = "wall", ["x"] = 1 }, RevitCommandCodes.ArgsInvalid, "args.x");
        Check(RevitCommandProtocol.Parse(Envelope("query.elements", new JsonObject { ["category"] = "wall" }).ToJsonString()).Spec.Writes == false, "query.elements parses to a read-only spec");

        var inspect = Accepts("query.inspect by UniqueId accepted", "query.inspect", new JsonObject { ["unique_id"] = E1 }) as ElementInspectArgs;
        Check(inspect is { UniqueId: E1 }, "query.inspect binds the UniqueId");
        Rejects("query.inspect without a UniqueId", "query.inspect", new JsonObject(), RevitCommandCodes.ArgsInvalid, "args.unique_id");
        Rejects("query.inspect with a name instead of a UniqueId", "query.inspect", new JsonObject { ["unique_id"] = "Basic Wall" }, RevitCommandCodes.ArgsInvalid, "args.unique_id");
        Rejects("query.inspect with an unexpected member", "query.inspect", new JsonObject { ["unique_id"] = E1, ["parameters"] = new JsonArray("Mark") }, RevitCommandCodes.ArgsInvalid, "args.parameters");
    }

    static void Paging()
    {
        var (selected, truncated) = RevitElementQueryRules.Page(new long[] { 30, 10, 20, 10 }, 5);
        Check(selected.SequenceEqual(new long[] { 10, 20, 30 }) && !truncated, "paging is distinct and ascending, not truncated under the limit");
        (selected, truncated) = RevitElementQueryRules.Page(new long[] { 5, 4, 3, 2, 1 }, 2);
        Check(selected.SequenceEqual(new long[] { 1, 2 }) && truncated, "paging keeps the lowest ids and reports truncation over the limit");
        (selected, truncated) = RevitElementQueryRules.Page(Enumerable.Range(1, 5000).Select(i => (long)i).Reverse(), 5000);
        Check(selected.Count == 5000 && selected[0] == 1 && !truncated, "exactly the limit is not truncated");
        Check(Throws<ArgumentOutOfRangeException>(() => RevitElementQueryRules.Page(new long[] { 1 }, 0)) && Throws<ArgumentOutOfRangeException>(() => RevitElementQueryRules.Page(new long[] { 1 }, 5001)), "paging refuses a limit outside 1..5000");
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    static ObservedParameter Text(string name, string? text, long id = -1001) =>
        new(name, id, "ALL_MODEL_MARK", RevitParameterSources.Instance, RevitParameterStorage.String, "autodesk.spec:spec.string-2.0.0", text is not null, false, false) { Text = text, Display = text };

    static ObservedQueryElement Element(long id, string uid, params ObservedParameter[] parameters) =>
        new(id, uid, "Walls", "OST_Walls") { Level = new ObservedReference("Level 1", L1), FamilyName = "Basic Wall", TypeName = "Generic - 200mm", TypeUid = T1, Parameters = parameters };

    static ElementQueryArgs Args(int limit = 1000) => new("wall", "OST_Walls", "Level 1", Array.Empty<string>(), limit);

    static void ElementsReply()
    {
        var length = new ObservedParameter("Unconnected Height", -1001300, "WALL_USER_HEIGHT_PARAM", RevitParameterSources.Instance, RevitParameterStorage.Double, "autodesk.spec.aec:length-2.0.0", true, false, false) { Number = 3000, Unit = RevitParameterUnits.Millimetres, Display = "3000" };
        var integer = new ObservedParameter("Room Bounding", -1001200, "WALL_ATTR_ROOM_BOUNDING", RevitParameterSources.Instance, RevitParameterStorage.Integer, "autodesk.spec:spec.bool-1.0.0", true, false, false) { Integer = 1, Display = "Yes" };
        var level = new ObservedParameter("Base Constraint", -1001107, "WALL_BASE_CONSTRAINT", RevitParameterSources.Instance, RevitParameterStorage.ElementId, null, true, false, false) { ElementIdValue = 311, ElementUid = L1, Display = "Level 1" };
        var none = new ObservedParameter("Phase Demolished", -1012101, "PHASE_DEMOLISHED", RevitParameterSources.Instance, RevitParameterStorage.ElementId, null, true, true, false) { ElementIdValue = -1, ElementUid = null, Display = "None" };
        var empty = Text("Comments", null, -1010106);
        var minusZero = new ObservedParameter("Offset", 5, null, RevitParameterSources.Type, RevitParameterStorage.Double, "autodesk.spec.aec:length-2.0.0", true, false, true) { Number = -0.0, Unit = RevitParameterUnits.Millimetres };
        var nan = new ObservedParameter("Broken", 6, null, RevitParameterSources.Instance, RevitParameterStorage.Double, null, true, false, true) { Number = double.NaN, Unit = "x" };
        var rows = new[]
        {
            Element(30, E3) with { Missing = new[] { "Fire Rating" } },
            Element(10, E1, length, integer, level, none, empty, minusZero, nan),
            Element(20, E2, Text("Mark", "W-02")) with { Level = null, FamilyName = null, TypeName = null, TypeUid = null, Category = null, BuiltInCategory = null },
        };
        var json = RevitElementQueryJson.Elements(Args(), new ObservedReference("Level 1", L1), 7, true, rows);
        var root = JsonNode.Parse(json)!.AsObject();
        Check(string.Join(",", root.Select(member => member.Key)) == "category,built_in_category,level,limit,matched,returned,truncated,truncated_reason,elements", "query.elements reply members in a fixed order", json);
        Check(root["category"]!.GetValue<string>() == "wall" && root["built_in_category"]!.GetValue<string>() == "OST_Walls" && root["level"]!["unique_id"]!.GetValue<string>() == L1 && root["level"]!["name"]!.GetValue<string>() == "Level 1", "the reply echoes the category and the resolved level");
        Check(root["limit"]!.GetValue<int>() == 1000 && root["matched"]!.GetValue<int>() == 7 && root["returned"]!.GetValue<int>() == 3 && root["truncated"]!.GetValue<bool>() && root["truncated_reason"]!.GetValue<string>() == "limit", "matched, returned, truncated and truncated_reason limit report a capped reply");
        var elements = root["elements"]!.AsArray();
        Check(elements.Select(e => e!["id"]!.GetValue<long>()).SequenceEqual(new long[] { 10, 20, 30 }), "elements are ordered by ascending element id whatever the input order");
        var first = elements[0]!.AsObject();
        Check(string.Join(",", first.Select(member => member.Key)) == "id,unique_id,category,built_in_category,level,family,type,type_unique_id,parameters,missing_parameters", "element members in a fixed order", first.ToJsonString());
        Check(first["unique_id"]!.GetValue<string>() == E1 && first["category"]!.GetValue<string>() == "Walls" && first["built_in_category"]!.GetValue<string>() == "OST_Walls" && first["family"]!.GetValue<string>() == "Basic Wall" && first["type"]!.GetValue<string>() == "Generic - 200mm" && first["type_unique_id"]!.GetValue<string>() == T1 && first["level"]!["unique_id"]!.GetValue<string>() == L1, "an element reports UniqueId, category, level, family, type and type UniqueId");
        var bare = elements[1]!.AsObject();
        Check(bare["level"] is null && bare["family"] is null && bare["type"] is null && bare["type_unique_id"] is null && bare["category"] is null && bare.ContainsKey("level") && bare.ContainsKey("type"), "missing level, type and category are explicit nulls");
        Check(elements[2]!["missing_parameters"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "Fire Rating" }) && elements[2]!["parameters"]!.AsArray().Count == 0, "requested parameters an element lacks are listed as missing");
        var parameters = first["parameters"]!.AsArray();
        Check(parameters.Count == 7 && parameters.Select(p => p!["name"]!.GetValue<string>()).SequenceEqual(new[] { "Unconnected Height", "Room Bounding", "Base Constraint", "Phase Demolished", "Comments", "Offset", "Broken" }), "query.elements keeps the parameters in the order read (the request order)");
        var p0 = parameters[0]!.AsObject();
        Check(string.Join(",", p0.Select(member => member.Key)) == "name,id,built_in,source,storage_type,spec,has_value,read_only,shared,value,unit,value_unique_id,display,value_truncated", "parameter members in a fixed order", p0.ToJsonString());
        Check(p0["storage_type"]!.GetValue<string>() == "double" && p0["value"]!.GetValue<double>() == 3000 && p0["unit"]!.GetValue<string>() == "mm" && p0["built_in"]!.GetValue<string>() == "WALL_USER_HEIGHT_PARAM" && p0["id"]!.GetValue<long>() == -1001300 && p0["spec"]!.GetValue<string>() == "autodesk.spec.aec:length-2.0.0" && p0["display"]!.GetValue<string>() == "3000", "a length parameter reports its value in millimetres with unit mm, spec, id and display");
        Check(parameters[1]!["storage_type"]!.GetValue<string>() == "integer" && parameters[1]!["value"]!.GetValue<long>() == 1 && parameters[1]!["unit"] is null, "an integer parameter reports its number and no unit");
        Check(parameters[2]!["storage_type"]!.GetValue<string>() == "element_id" && parameters[2]!["value"]!.GetValue<long>() == 311 && parameters[2]!["value_unique_id"]!.GetValue<string>() == L1, "an element id parameter reports the id and the referenced UniqueId");
        Check(parameters[3]!["value"]!.GetValue<long>() == -1 && parameters[3]!["value_unique_id"] is null && parameters[3]!["read_only"]!.GetValue<bool>(), "an invalid element id is -1 with no UniqueId; read-only is reported");
        Check(parameters[4]!["has_value"]!.GetValue<bool>() == false && parameters[4]!["value"] is null && parameters[4]!.AsObject().ContainsKey("value"), "a parameter without a value has value null");
        Check(parameters[5]!["value"]!.ToJsonString() == "0" && parameters[5]!["source"]!.GetValue<string>() == "type" && parameters[5]!["shared"]!.GetValue<bool>() && parameters[5]!["built_in"] is null, "-0 is written as 0; type source, shared and non-built-in parameters are reported");
        Check(parameters[6]!["value"] is null && parameters[6]!["unit"] is null, "a non-finite double is written as null with no unit");
        Check(RevitElementQueryJson.Elements(Args(), new ObservedReference("Level 1", L1), 7, true, rows.Reverse()) == json, "the reply is byte-identical for any input order");
        var exact = RevitElementQueryJson.Elements(Args(), null, 3, false, rows);
        Check(JsonNode.Parse(exact)!["truncated"]!.GetValue<bool>() == false && JsonNode.Parse(exact)!["truncated_reason"] is null && JsonNode.Parse(exact)!.AsObject().ContainsKey("truncated_reason") && JsonNode.Parse(exact)!["level"] is null, "a reply under the limit is not truncated (truncated_reason null); no level filter is null");
        // An element that vanished between paging and reading (GetElement null) leaves fewer rows than matched; the
        // reply is truncated only when paging cut it at the limit, as documented.
        var vanished = JsonNode.Parse(RevitElementQueryJson.Elements(Args(), null, 3, false, rows.Take(2)))!;
        Check(vanished["matched"]!.GetValue<int>() == 3 && vanished["returned"]!.GetValue<int>() == 2 && !vanished["truncated"]!.GetValue<bool>() && vanished["truncated_reason"] is null, "limit 1000, matched 3, 2 rows read and no paging cut: not truncated");

        var longText = new string('a', 1023) + "\U0001F600" + new string('b', 50);
        var cut = JsonNode.Parse(RevitElementQueryJson.Elements(Args(), null, 1, false, new[] { Element(1, E1, Text("Comments", longText)) }))!["elements"]![0]!["parameters"]![0]!;
        Check(cut["value"]!.GetValue<string>() == new string('a', 1023) && cut["display"]!.GetValue<string>() == new string('a', 1023) && cut["value_truncated"]!.GetValue<bool>(), "strings over 1024 characters are cut without splitting a surrogate pair and flagged value_truncated");
        var fits = JsonNode.Parse(RevitElementQueryJson.Elements(Args(), null, 1, false, new[] { Element(1, E1, Text("Comments", new string('c', 1024))) }))!["elements"]![0]!["parameters"]![0]!;
        Check(fits["value"]!.GetValue<string>().Length == 1024 && !fits["value_truncated"]!.GetValue<bool>(), "a 1024-character string is kept whole");

        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Args(limit: 2), null, 3, true, rows)), "more rows than the limit is a collector bug and throws");
        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Args(), null, 2, false, new[] { Element(1, E1), Element(1, E2) })), "duplicate element ids throw");
        Check(Throws<ArgumentOutOfRangeException>(() => RevitElementQueryJson.Elements(Args(), null, 2, false, rows)), "matched below the rows returned throws");
        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Args(), null, 1, false, new[] { Element(1, E1, Text("Mark", "x") with { StorageType = "float" }) })), "an unknown storage type throws");
        Check(Throws<ArgumentException>(() => RevitElementQueryJson.Elements(Args(), null, 1, false, new[] { Element(1, E1, Text("Mark", "x") with { Source = "family" }) })), "an unknown parameter source throws");
        var receipt = new RevitCommandReceipt("op-query", "query.elements", Doc, new string('0', 64), false);
        var response = RevitCommandResponse.Parse(RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), json, Array.Empty<string>()).ToJson());
        Check(response.IsSuccess && response.ReadbackJson is not null && JsonNode.Parse(response.ReadbackJson)!["returned"]!.GetValue<int>() == 3 && response.Created.Count == 0 && response.Modified.Count == 0, "the reply travels as the readback of a read-only success response");
    }

    static void InspectReply()
    {
        ObservedParameter P(string name, string source, long id) => new(name, id, null, source, RevitParameterStorage.None, null, false, true, false);
        var parameters = new[] { P("Width", "type", 4), P("Area", "instance", 9), P("Width", "instance", 8), P("Width", "instance", 2), P("area", "instance", 1) };
        var json = RevitElementQueryJson.Inspect(Element(10, E1), parameters);
        var root = JsonNode.Parse(json)!.AsObject();
        Check(string.Join(",", root.Select(member => member.Key)) == "element,parameter_count,returned,truncated,truncated_reason,parameters", "query.inspect reply members in a fixed order", json);
        Check(root["element"]!["unique_id"]!.GetValue<string>() == E1 && root["element"]!["type_unique_id"]!.GetValue<string>() == T1 && !root["element"]!.AsObject().ContainsKey("parameters"), "the inspected element is described without a parameters member");
        var order = root["parameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>() + "/" + p!["source"]!.GetValue<string>() + "/" + p!["id"]!.GetValue<long>()).ToArray();
        Check(order.SequenceEqual(new[] { "Area/instance/9", "Width/instance/2", "Width/instance/8", "Width/type/4", "area/instance/1" }), "inspect parameters are ordered by name (ordinal), instance before type, then id", string.Join(" ", order));
        Check(root["parameter_count"]!.GetValue<int>() == 5 && root["returned"]!.GetValue<int>() == 5 && !root["truncated"]!.GetValue<bool>() && root["truncated_reason"] is null, "all parameters under the cap are returned");
        Check(root["parameters"]![0]!["storage_type"]!.GetValue<string>() == "none" && root["parameters"]![0]!["value"] is null, "a parameter of storage none has value null");
        Check(RevitElementQueryJson.Inspect(Element(10, E1), parameters.Reverse()) == json, "the inspect reply is byte-identical for any input order");
        var many = Enumerable.Range(0, RevitElementQueryRules.MaxInspectParameters + 5).Select(i => P("P" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), "instance", i)).Reverse().ToArray();
        var capped = JsonNode.Parse(RevitElementQueryJson.Inspect(Element(10, E1), many))!;
        Check(capped["parameter_count"]!.GetValue<int>() == 2005 && capped["returned"]!.GetValue<int>() == 2000 && capped["truncated"]!.GetValue<bool>() && capped["truncated_reason"]!.GetValue<string>() == "limit" && capped["parameters"]![0]!["name"]!.GetValue<string>() == "P00000" && capped["parameters"]![1999]!["name"]!.GetValue<string>() == "P01999", "inspect keeps the first 2000 parameters in order and reports truncation");
    }

    // Line comments are removed before any source check, so a header comment can never satisfy one. The executor
    // sources have no "//" inside string literals.
    static string StripComments(string source) => System.Text.RegularExpressions.Regex.Replace(source, @"//[^\n]*", "");

    static void ReplyBytes()
    {
        Check(RevitElementQueryRules.MaxReplyBytes == 4 * 1024 * 1024, "replies are bounded to 4 MiB of UTF-8");
        // Worst case: 5000 rows, 16 parameters each, 1024-character non-ASCII values and display texts (escaped as \uXXXX).
        var heavy = new string('\u00e9', 1024);
        var parameters = Enumerable.Range(0, 16).Select(i => Text("P\u00e9" + i, heavy, -i - 1)).ToArray();
        var rows = Enumerable.Range(1, 5000).Select(i => Element(i, "60f91daf-3dd7-4283-a86d-24137b73f3da-" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), parameters)).Reverse().ToArray();
        var json = RevitElementQueryJson.Elements(Args(limit: 5000), new ObservedReference("Level 1", L1), 9000, true, rows);
        var size = System.Text.Encoding.UTF8.GetByteCount(json);
        var root = JsonNode.Parse(json)!;
        var returned = root["returned"]!.GetValue<int>();
        Check(size <= RevitElementQueryRules.MaxReplyBytes && size > RevitElementQueryRules.MaxReplyBytes / 2, "5000 heavy rows fit in the 4 MiB budget and use most of it", size + " bytes");
        Check(root["truncated"]!.GetValue<bool>() && root["truncated_reason"]!.GetValue<string>() == "bytes" && returned > 0 && returned < 5000 && root["elements"]!.AsArray().Count == returned && root["matched"]!.GetValue<int>() == 9000, "the byte budget stops adding rows: truncated with truncated_reason bytes", returned + " rows");
        Check(root["elements"]!.AsArray().Select(e => e!["id"]!.GetValue<long>()).SequenceEqual(Enumerable.Range(1, returned).Select(i => (long)i)), "the rows kept under the byte budget are the lowest ids");
        var receipt = new RevitCommandReceipt("op-query", "query.elements", Doc, new string('0', 64), false);
        var wire = RevitCommandResponse.Success(receipt, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), json, Array.Empty<string>()).ToJson();
        Check(System.Text.Encoding.UTF8.GetByteCount(wire) <= RevitElementQueryRules.MaxReplyBytes + 4096, "the response line carrying the bounded reply stays within the budget plus the envelope");
        var light = JsonNode.Parse(RevitElementQueryJson.Elements(Args(limit: 5000), null, 5000, false, Enumerable.Range(1, 5000).Select(i => Element(i, "60f91daf-3dd7-4283-a86d-24137b73f3da-" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)))))!;
        Check(light["returned"]!.GetValue<int>() == 5000 && !light["truncated"]!.GetValue<bool>(), "5000 plain rows fit without truncation");

        var many = Enumerable.Range(0, RevitElementQueryRules.MaxInspectParameters).Select(i => Text("N" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), heavy, i)).ToArray();
        var inspect = RevitElementQueryJson.Inspect(Element(10, E1), many);
        var inspected = JsonNode.Parse(inspect)!;
        var kept = inspected["returned"]!.GetValue<int>();
        Check(System.Text.Encoding.UTF8.GetByteCount(inspect) <= RevitElementQueryRules.MaxReplyBytes && inspected["truncated"]!.GetValue<bool>() && inspected["truncated_reason"]!.GetValue<string>() == "bytes" && kept > 0 && kept < 2000 && inspected["parameter_count"]!.GetValue<int>() == 2000 && inspected["parameters"]!.AsArray().Count == kept && inspected["parameters"]![0]!["name"]!.GetValue<string>() == "N00000", "inspect stops at the byte budget: truncated with truncated_reason bytes, first parameters kept", kept + " parameters");
    }

    static void SpecUnits()
    {
        foreach (var (spec, unit) in new (string?, string?)[]
        {
            ("autodesk.spec.aec:length-2.0.0", "mm"), ("autodesk.spec.aec:length-1.0.0", "mm"), ("autodesk.spec.aec:length", "mm"),
            ("autodesk.spec.aec:area-2.0.0", "m2"), ("autodesk.spec.aec:area-1.0.1", "m2"),
            ("autodesk.spec.aec:volume-2.0.0", "m3"), ("autodesk.spec.aec:volume-1.0.0", "m3"),
            ("autodesk.spec.aec:angle-2.0.0", "deg"), ("autodesk.spec.aec:angle-1.0.0", "deg"),
            ("autodesk.spec.aec:slope-2.0.0", "display"), ("autodesk.spec.aec.hvac:airFlow-2.0.0", "display"), ("autodesk.spec:spec.string-2.0.0", "display"),
            ("autodesk.spec.aec:lengthx-2.0.0", "display"), ("autodesk.spec.aec:Length-2.0.0", "display"), ("autodesk.spec.aec.structural:length-2.0.0", "display"),
            ("", null), (null, null),
        })
            Check(RevitParameterUnits.ForSpec(spec) == unit, "spec " + (spec ?? "null") + " routes to " + (unit ?? "no unit"), RevitParameterUnits.ForSpec(spec) ?? "null");
        Check(RevitParameterUnits.Display == "display", "the display route is named display");
    }

    static void Collector()
    {
        var root = RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        string? Source(string file)
        {
            var path = root is null ? null : Path.Combine(root, "runtimes", "revit-native", file);
            return path is not null && File.Exists(path) ? StripComments(File.ReadAllText(path)) : null;
        }
        var text = Source("CommandsExecutorQuery.cs");
        var write = text is null ? null : System.Text.RegularExpressions.Regex.Match(text, @"\b(Transaction|TransactionGroup|SubTransaction)\b|\.Set\(|Regenerate|\.Delete\(|\.Create\.|ReceiptStore|ReadReceipts");
        Check(text is not null && !write!.Success, "the element query collector has no transaction, Set, Regenerate, Delete, creation call or receipt access", text is null ? "source not found" : write!.Value);
        Check(text is not null && text.Contains("ElementQueryArgs", StringComparison.Ordinal) && text.Contains("ElementInspectArgs", StringComparison.Ordinal) && text.Contains("RevitElementQueryRules.Page", StringComparison.Ordinal)
            && text.Contains("RevitElementQueryJson.Elements", StringComparison.Ordinal) && text.Contains("RevitElementQueryJson.Inspect", StringComparison.Ordinal) && text.Contains("WhereElementIsNotElementType", StringComparison.Ordinal),
            "the collector pages ids in core and writes both replies with RevitElementQueryJson");
        Check(text is not null && text.Contains("RevitParameterUnits.ForSpec(", StringComparison.Ordinal) && text.Contains("UnitTypeId.Millimeters", StringComparison.Ordinal) && text.Contains("UnitTypeId.SquareMeters", StringComparison.Ordinal) && text.Contains("UnitTypeId.CubicMeters", StringComparison.Ordinal) && text.Contains("UnitTypeId.Degrees", StringComparison.Ordinal) && !text.Contains("SpecTypeId.", StringComparison.Ordinal), "the collector routes units through RevitParameterUnits.ForSpec (code, not comments) and converts with UnitTypeId");
        Check(text is not null && text.Contains("CategoryType.Model", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(text, @"BuiltInCategory\s+is\s+null\s*&&[^;]*CategoryType\s*!=\s*CategoryType\.Model"), "a level-only query keeps model categories only (no views or annotations)");
        Check(text is not null && System.Text.RegularExpressions.Regex.IsMatch(text, @"\.Page\(matched,\s*args\.Limit\)") && System.Text.RegularExpressions.Regex.IsMatch(text, @"RevitElementQueryJson\.Elements\([^;]*truncated[^;]*rows\)"), "the collector passes the paging truncation flag to the reply");
        Check(text is not null && System.Text.RegularExpressions.Regex.IsMatch(text, @"try\s*\{[^}]*GetDataType\(\)"), "reading a parameter's data type is guarded");
        Check(text is not null && text.Contains("BuiltInCategory.INVALID ? null", StringComparison.Ordinal), "a category whose BuiltInCategory is INVALID reports null");
        var mirror = Source("Mirror.cs");
        Check(mirror is not null && System.Text.RegularExpressions.Regex.IsMatch(mirror, @"_\s*=>\s*QueryElements\(d,\s*envelope\)"), "the query dispatch chain falls through to the element queries after query.snapshot");
    }
}
