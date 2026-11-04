using Bim.Core;
using Bim.Revit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
// View image export and document save contract oracle (chat-export-save): view.export_image (read-only, image files
// only under the repository's .bim/exports) and document.save (a write, gated like every write). Covers the schemas,
// the typed binders, the guarded client path (fake transport), the Autodesk-free executor rules, the export folder and
// image header code against a temporary repository, and the readback checks and JSON in
// runtimes/revit-native/CommandsExportSave.cs. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorExportSave.cs is compiled, never run.
static class CatalogueExportSaveChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string ViewUid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0003e001";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string RealPath = @"C:\Projects\Real\Tower.rvt";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(string command, JsonNode args, string path = FixturePath) => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-export-save",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonObject Export() => new() { ["view"] = "{3D}" };
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

    // Minimal image headers, as Revit's PNG and JPEG writers start them.
    public static byte[] Png(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange(BigEndian(width));
        bytes.AddRange(BigEndian(height));
        bytes.AddRange(new byte[] { 8, 6, 0, 0, 0, 0x12, 0x34, 0x56, 0x78, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D' });
        return bytes.ToArray();
    }
    public static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
        bytes.AddRange(new byte[] { 0xFF, 0xDB, 0x00, 0x04, 0x00, 0x00 });
        bytes.AddRange(new byte[] { 0xFF, 0xC4, 0x00, 0x03, 0x00 });
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
        bytes.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x02, 0xFF, 0xD9 });
        return bytes.ToArray();
    }
    static byte[] BigEndian(int value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
    static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        ExportBinding();
        SaveBinding();
        ClientPath();
        AddInAdmission();
        Rules();
        Headers();
        ExportFolder();
        Readback();
    }

    static readonly string[] Names = { "view.export_image", "document.save" };

    static void Catalogue()
    {
        var names = RevitCommandCatalogue.ExportSave.Select(spec => spec.Name).ToArray();
        Check(names.SequenceEqual(Names), "export and save catalogue lists view.export_image, document.save", string.Join(",", names));
        var earlier = RevitCommandCatalogue.V1.Concat(RevitCommandCatalogue.V2).Concat(RevitCommandCatalogue.Views).Concat(RevitCommandCatalogue.Mirror).Concat(RevitCommandCatalogue.Elements)
            .Concat(RevitCommandCatalogue.Structure).Concat(RevitCommandCatalogue.Documentation).Concat(RevitCommandCatalogue.Modify).Concat(RevitCommandCatalogue.Materials)
            .Concat(RevitCommandCatalogue.Families).Concat(RevitCommandCatalogue.Site).Concat(RevitCommandCatalogue.WallProfile).Concat(RevitCommandCatalogue.MaterialTypes).ToArray();
        Check(RevitCommandCatalogue.All.Take(earlier.Length).SequenceEqual(earlier) && RevitCommandCatalogue.All.Skip(earlier.Length).SequenceEqual(RevitCommandCatalogue.ExportSave.Concat(RevitCommandCatalogue.SaveCopy).Concat(RevitCommandCatalogue.RoofExtrusion).Concat(RevitCommandCatalogue.Batch)),
            "All is every earlier part through the material type commands, then view.export_image and document.save, then document.save_copy, then roof.create_extrusion (P9-07), then command.batch");
        Check(RevitCommandCatalogue.All.Select(spec => spec.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(spec => spec.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with the export and save commands");
        Check(RevitCommandCatalogue.ExportSave.Select(spec => spec.ToolName).SequenceEqual(new[] { "revit_view_export_image", "revit_document_save" }), "export and save tool names follow revit_<command>");
        Check(RevitCommandCatalogue.Find("view.export_image") is { Writes: false } && RevitCommandCatalogue.Find("document.save") is { Writes: true }, "view.export_image is read-only (it does not change the model); document.save is a write");
        foreach (var name in Names) Check(ReferenceEquals(RevitCommandCatalogue.Find(name), RevitCommandCatalogue.ExportSave.Single(spec => spec.Name == name)), "Find resolves " + name);
        Check(RevitCommandCatalogue.IsDestructive("document.save") && !RevitCommandCatalogue.IsDestructive("view.export_image"), "document.save overwrites the saved file: destructive; view.export_image is not");
        var export = RevitCommandCatalogue.Find("view.export_image")!.Description;
        Check(export.Contains(".bim/exports", StringComparison.Ordinal) && export.Contains("sha256", StringComparison.Ordinal) && export.Contains("rolled back", StringComparison.Ordinal) && export.EndsWith('.'),
            "view.export_image states where files go, what is read back and that a visual style change is rolled back");
        var save = RevitCommandCatalogue.Find("document.save")!.Description;
        Check(save.Contains("workshared", StringComparison.Ordinal) && save.Contains("never", StringComparison.Ordinal) && save.Contains("enabled", StringComparison.Ordinal) && save.EndsWith('.'),
            "document.save states the refusals and the enabled-folder rule");
        foreach (var spec in RevitCommandCatalogue.ExportSave)
        {
            string? problem = null;
            try { using var schema = JsonDocument.Parse(spec.ArgsSchemaJson); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { problem = error.Message; }
            Check(problem is null, spec.Name + " args schema is valid JSON using only enforced keywords", problem ?? "");
        }
        var copies = new[] { @"C:\Repo\.bim\native-runs" };
        Check(PermissionPolicy.RequiredLevel("view.export_image", RealPath, copies) == PermissionLevel.Read && PermissionPolicy.RequiredLevel("view.export_image", FixturePath, copies) == PermissionLevel.Read, "view.export_image needs read on any document");
        Check(PermissionPolicy.RequiredLevel("document.save", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("document.save", RealPath, copies) == PermissionLevel.WriteRealProject, "document.save needs write-copies on a copy and write-real-project elsewhere");
    }

    static void ExportBinding()
    {
        var plain = Accepts("view.export_image with only a view accepted", Envelope("view.export_image", Export()));
        Check(plain?.Args is ViewExportImageArgs { View: "{3D}", PixelSize: 1600, Format: "png", VisualStyle: null, OutputName: null }, "defaults: pixel_size 1600, png, the view's own visual style, generated file name");
        var full = Accepts("view.export_image with every member accepted", Envelope("view.export_image", new JsonObject { ["view"] = ViewUid, ["pixel_size"] = 4096, ["format"] = "jpg", ["visual_style"] = "realistic", ["output_name"] = "fachada norte_v2.1" }));
        Check(full?.Args is ViewExportImageArgs { View: ViewUid, PixelSize: 4096, Format: "jpg", VisualStyle: "realistic", OutputName: "fachada norte_v2.1" }, "view.export_image binds view, pixel size, format, visual style and output name");
        Accepts("pixel_size 256 accepted", Envelope("view.export_image", With(Export(), "pixel_size", 256)));
        foreach (var style in new[] { "shaded", "realistic", "hidden_line", "consistent_colors" })
            Accepts("visual style " + style + " accepted", Envelope("view.export_image", With(Export(), "visual_style", style)));

        Rejects("view.export_image without view", Envelope("view.export_image", new JsonObject()), RevitCommandCodes.ArgsInvalid, "args.view");
        Rejects("pixel_size 255 refused", Envelope("view.export_image", With(Export(), "pixel_size", 255)), RevitCommandCodes.ArgsInvalid, "args.pixel_size");
        Rejects("pixel_size 4097 refused", Envelope("view.export_image", With(Export(), "pixel_size", 4097)), RevitCommandCodes.ArgsInvalid, "args.pixel_size");
        Rejects("fractional pixel_size refused", Envelope("view.export_image", With(Export(), "pixel_size", 1600.5)), RevitCommandCodes.ArgsInvalid, "args.pixel_size");
        Rejects("format bmp refused", Envelope("view.export_image", With(Export(), "format", "bmp")), RevitCommandCodes.ArgsInvalid, "args.format");
        Rejects("visual style wireframe refused", Envelope("view.export_image", With(Export(), "visual_style", "wireframe")), RevitCommandCodes.ArgsInvalid, "args.visual_style");
        Rejects("unknown member refused", Envelope("view.export_image", With(Export(), "path", "C:/x.png")), RevitCommandCodes.ArgsInvalid);
        foreach (var name in new[] { "../fuera", "a/b", @"a\b", "C:x", ".oculto", "nombre.", "nombre ", "CON", "nul.txt", "Com1", "LPT9", "CON .x", "nul .", "aux  .tar.gz", "PRN. .png", "com1 ", "a*b", "a?b", "a\"b", "", new string('a', 101) })
            Rejects("output name '" + name + "' refused", Envelope("view.export_image", With(Export(), "output_name", name)), RevitCommandCodes.ArgsInvalid, "args.output_name");
        Accepts("output name of 100 characters accepted", Envelope("view.export_image", With(Export(), "output_name", new string('a', 100))));
        Accepts("output name CONSOLA accepted (only exact device names are reserved)", Envelope("view.export_image", With(Export(), "output_name", "CONSOLA")));
    }

    static void SaveBinding()
    {
        var save = Accepts("document.save with {} accepted", Envelope("document.save", new JsonObject()));
        Check(save?.Args is DocumentSaveArgs, "document.save binds DocumentSaveArgs");
        Rejects("document.save with a path refused (no save-as)", Envelope("document.save", new JsonObject { ["path"] = @"C:\Otro\copia.rvt" }), RevitCommandCodes.ArgsInvalid);
    }

    static void ClientPath()
    {
        var policy = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" });
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        var onReal = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("view.export_image", Export(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 1 && JsonNode.Parse(onReal.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.NoResponse, "view.export_image is read-only and forwarded on a real project", onReal.Line);
        var saveReal = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("document.save", new JsonObject(), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(saveReal.IsError && !saveReal.Forwarded && JsonNode.Parse(saveReal.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 1, "document.save outside the enabled roots is document_not_enabled and never forwarded", saveReal.Line);
        RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("document.save", new JsonObject()).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 2 && forwarded[1] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope("document.save", new JsonObject()).ToJsonString())), "document.save on an enabled copy is forwarded as the canonical wire line");
        // A visual style is set in a (rolled back) transaction, so it gets the write guard: never on a real project.
        var styled = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("view.export_image", With(Export(), "visual_style", "realistic"), RealPath).ToJsonString(), policy, Transport, noLinks);
        Check(styled.IsError && !styled.Forwarded && JsonNode.Parse(styled.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentNotEnabled && forwarded.Count == 2, "view.export_image with visual_style on a real project is document_not_enabled and never forwarded", styled.Line);
        RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("view.export_image", With(Export(), "visual_style", "realistic")).ToJsonString(), policy, Transport, noLinks);
        Check(forwarded.Count == 3, "view.export_image with visual_style on an enabled copy is forwarded");
        var linked = new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null);
        var styledLinked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("view.export_image", With(Export(), "visual_style", "realistic")).ToJsonString(), policy, Transport, linked);
        Check(JsonNode.Parse(styledLinked.Line)!["code"]!.GetValue<string>() == RevitCommandCodes.DocumentLinkRefused && forwarded.Count == 3, "view.export_image with visual_style passes the link guard too", styledLinked.Line);
        var plainLinked = RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("view.export_image", Export()).ToJsonString(), policy, Transport, linked);
        Check(plainLinked.Forwarded && forwarded.Count == 4, "a plain export stays read-only (no link guard)");
        Check(!RevitCommandCatalogue.NeedsWriteGuard(RevitCommandProtocol.Parse(Envelope("view.export_image", Export()).ToJsonString()))
            && RevitCommandCatalogue.NeedsWriteGuard(RevitCommandProtocol.Parse(Envelope("view.export_image", With(Export(), "visual_style", "shaded")).ToJsonString()))
            && RevitCommandCatalogue.NeedsWriteGuard(RevitCommandProtocol.Parse(Envelope("document.save", new JsonObject()).ToJsonString()))
            && !RevitCommandCatalogue.NeedsWriteGuard(RevitCommandProtocol.Parse(Envelope("query.levels", new JsonObject()).ToJsonString())), "NeedsWriteGuard: writes and styled exports, not plain exports or queries");
        using (var styledArgs = JsonDocument.Parse("""{"view":"{3D}","visual_style":"shaded"}"""))
        using (var plainArgs = JsonDocument.Parse("""{"view":"{3D}"}"""))
        {
            var exportSpec = RevitCommandCatalogue.Find("view.export_image")!;
            Check(RevitCommandCatalogue.NeedsWriteGuard(exportSpec, styledArgs.RootElement) && !RevitCommandCatalogue.NeedsWriteGuard(exportSpec, plainArgs.RootElement)
                && RevitCommandCatalogue.NeedsWriteGuard(RevitCommandCatalogue.Find("document.save")!, plainArgs.RootElement) && !RevitCommandCatalogue.NeedsWriteGuard(RevitCommandCatalogue.Find("query.levels")!, plainArgs.RootElement),
                "NeedsWriteGuard on raw arguments (MCP): only a styled export among reads needs the policy");
        }
        var owner = new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs", @"C:\Users\Owner\Documents\ARQ" });
        RevitCommandService.Run("bim-agentic-os-2025-4242", Envelope("document.save", new JsonObject(), @"C:\Users\Owner\Documents\ARQ\Cabana\Cabana.rvt").ToJsonString(), owner, Transport, noLinks);
        Check(forwarded.Count == 5, "document.save on an owner-enabled project folder is forwarded");
    }

    // The add-in repeats the guard on the document it actually has open: a styled export needs the write admission
    // (enabled root, no link, not read-only, family or workshared); a plain export is allowed on any document.
    static void AddInAdmission()
    {
        var calls = 0;
        RevitCommandPolicy Policy() { calls++; return new RevitCommandPolicy(new[] { @"C:\Repo\.bim\native-runs" }); }
        var noLinks = new RevitFileSystemProbe(_ => false, _ => null);
        RevitCommandEnvelope ExportAt(string path, bool styled) => RevitCommandProtocol.Parse(Envelope("view.export_image", styled ? With(Export(), "visual_style", "realistic") : Export(), path).ToJsonString());
        CommandDocumentFacts Facts(string path, bool readOnly = false, bool workshared = false) => new(path, Uid, readOnly, false, workshared, false);
        Check(Thrown(() => CommandAdmission.Check(ExportAt(RealPath, false), Facts(RealPath), Policy, noLinks)) is null && calls == 0, "add-in: a plain export is allowed on the owner's production model without loading the write policy");
        Check(Thrown(() => CommandAdmission.Check(ExportAt(RealPath, true), Facts(RealPath), Policy, noLinks)) is { Code: RevitCommandCodes.DocumentNotEnabled } && calls == 1, "add-in: a styled export on a document outside the enabled roots is document_not_enabled");
        Check(Thrown(() => CommandAdmission.Check(ExportAt(FixturePath, true), Facts(FixturePath), Policy, noLinks)) is null, "add-in: a styled export on an enabled copy is allowed");
        Check(Thrown(() => CommandAdmission.Check(ExportAt(FixturePath, true), Facts(FixturePath, workshared: true), Policy, noLinks)) is { Code: RevitCommandCodes.DocumentNotEnabled } ws && ws.Detail!.Contains("workshared", StringComparison.Ordinal), "add-in: a styled export on a workshared document is refused");
        Check(Thrown(() => CommandAdmission.Check(ExportAt(FixturePath, true), Facts(FixturePath, readOnly: true), Policy, noLinks)) is { Code: RevitCommandCodes.DocumentNotEnabled } ro && ro.Detail!.Contains("read-only", StringComparison.Ordinal), "add-in: a styled export on a read-only document is refused");
        Check(Thrown(() => CommandAdmission.Check(ExportAt(FixturePath, false), Facts(FixturePath, readOnly: true, workshared: true), Policy, noLinks)) is null, "add-in: a plain export of a read-only, workshared document is allowed");
    }

    static void Rules()
    {
        foreach (var (revit, kind) in new[] { ("ThreeD", "3d"), ("FloorPlan", "floor_plan"), ("CeilingPlan", "ceiling_plan"), ("EngineeringPlan", "structural_plan"), ("AreaPlan", "area_plan"), ("Section", "section"), ("Elevation", "elevation"), ("Detail", "detail"), ("DrawingSheet", "sheet") })
            Check(CommandRulesExportSave.ExportKind(revit) == kind, "Revit ViewType." + revit + " exports as " + kind);
        foreach (var revit in new[] { "Schedule", "Legend", "DraftingView", "ProjectBrowser", "SystemBrowser", "Internal", "Undefined", "Walkthrough", "Rendering", "Report", "CostReport", "LoadsReport", "PresureLossReport", "ColumnSchedule", "PanelSchedule" })
            Check(CommandRulesExportSave.ExportKind(revit) is null, "Revit ViewType." + revit + " is not exported");
        Check(Thrown(() => CommandRulesExportSave.CheckExportView("3d", false, "ThreeD")) is null, "a 3D view can be exported");
        Check(Thrown(() => CommandRulesExportSave.CheckExportView("floor_plan", true, "FloorPlan")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view" } template && template.Detail!.Contains("template", StringComparison.Ordinal), "a view template is refused at args.view");
        Check(Thrown(() => CommandRulesExportSave.CheckExportView(null, false, "Schedule")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.view" } schedule && schedule.Detail!.Contains("Schedule", StringComparison.Ordinal), "a schedule is refused at args.view, naming its type");
        Check(Thrown(() => CommandRulesExportSave.CheckVisualStyle("sheet", "shaded")) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.visual_style" }, "a sheet has no visual style to set");
        Check(Thrown(() => CommandRulesExportSave.CheckVisualStyle("sheet", null)) is null && Thrown(() => CommandRulesExportSave.CheckVisualStyle("floor_plan", "consistent_colors")) is null, "a sheet without a visual style and a plan with one are accepted");
        foreach (var (style, revit) in new[] { ("shaded", "Shading"), ("realistic", "Realistic"), ("hidden_line", "HLR"), ("consistent_colors", "FlatColors") })
            Check(CommandRulesExportSave.RevitDisplayStyle(style) == revit && CommandRulesExportSave.VisualStyleName(revit) == style, "visual style " + style + " is Revit DisplayStyle." + revit + " and reads back as " + style);
        Check(CommandRulesExportSave.VisualStyleName("Wireframe") == "wireframe" && CommandRulesExportSave.VisualStyleName("ShadingWithEdges") == "shaded_with_edges" && CommandRulesExportSave.VisualStyleName("RealisticWithEdges") == "realistic_with_edges", "other Revit display styles read back in snake case");
        Check(CommandRulesExportSave.FitDirection(10, 5) == "Horizontal" && CommandRulesExportSave.FitDirection(5, 10) == "Vertical" && CommandRulesExportSave.FitDirection(null, null) == "Horizontal" && CommandRulesExportSave.FitDirection(7, 7) == "Horizontal",
            "the fit direction follows the longest side of the view outline (horizontal when unknown or square)");
        Check(CommandRulesExportSave.OtherDirection("Horizontal") == "Vertical" && CommandRulesExportSave.OtherDirection("Vertical") == "Horizontal", "the other fit direction is used for a retry");
        Check(CommandRulesExportSave.LongestSideMatches(new ImageSize(1600, 900), 1600) && CommandRulesExportSave.LongestSideMatches(new ImageSize(901, 1601), 1600) && !CommandRulesExportSave.LongestSideMatches(new ImageSize(1600, 2844), 1600) && !CommandRulesExportSave.LongestSideMatches(new ImageSize(1200, 800), 1600),
            "the longest side must be the requested pixel size within 2 pixels");
        Check(CommandRulesExportSave.RevitImageFileType("png") == "PNG" && CommandRulesExportSave.RevitImageFileType("jpg") == "JPEGLossless" && CommandRulesExportSave.Extension("png") == ".png" && CommandRulesExportSave.Extension("jpg") == ".jpg", "png and jpg map to Revit ImageFileType PNG and JPEGLossless");
        Check(CommandRulesExportSave.ViewReferenceName("floor_plan", "Level 1") == "floor_plan: Level 1" && CommandRulesExportSave.SheetReferenceName("A101", "Planta") == "A101 - Planta", "views can also be named kind: name and sheets number - name");

        var ok = new SaveFacts(FixturePath, false, false, false, false);
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok, FixturePath)) is null, "a saved, local, non-workshared, writable project can be saved");
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok with { PathName = "" }, FixturePath)) is { Code: RevitCommandCodes.DocumentNotEnabled, Field: "document.path" } never && never.Detail!.Contains("never been saved", StringComparison.Ordinal), "a never-saved document is refused");
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok, @"C:\Repo\.bim\native-runs\run-2\Other.rvt")) is { Code: RevitCommandCodes.DocumentConflict, Field: "document.path" }, "a document whose path is not the envelope path is document_conflict");
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok, FixturePath.ToLowerInvariant())) is null, "drive paths compare case-insensitively like the copies guard");
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok with { IsReadOnly = true }, FixturePath)) is { Code: RevitCommandCodes.DocumentNotEnabled } ro && ro.Detail!.Contains("read-only", StringComparison.Ordinal), "a read-only document is refused");
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok with { IsWorkshared = true }, FixturePath)) is { Code: RevitCommandCodes.DocumentNotEnabled } ws && ws.Detail!.Contains("workshared", StringComparison.Ordinal), "a workshared document is refused (synchronize with central is not offered)");
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok with { IsModelInCloud = true }, FixturePath)) is { Code: RevitCommandCodes.DocumentNotEnabled } cloud && cloud.Detail!.Contains("cloud", StringComparison.Ordinal), "a cloud model is refused");
        Check(Thrown(() => CommandRulesExportSave.CheckSavable(ok with { IsFamilyDocument = true }, FixturePath)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "a family document is refused");
    }

    static void Headers()
    {
        Check(ImageHeader.Png(Png(1600, 900)) == new ImageSize(1600, 900) && ImageHeader.Read(Png(256, 4096), "png") == new ImageSize(256, 4096), "PNG width and height are read from IHDR");
        Check(ImageHeader.Jpeg(Jpeg(1600, 1131)) == new ImageSize(1600, 1131) && ImageHeader.Read(Jpeg(4096, 300), "jpg") == new ImageSize(4096, 300), "JPEG width and height are read from the SOF0 frame header, skipping earlier segments");
        var progressive = Jpeg(800, 600);
        progressive[Array.IndexOf(progressive, (byte)0xC0, 20)] = 0xC2;
        Check(ImageHeader.Jpeg(progressive) == new ImageSize(800, 600), "a progressive (SOF2) JPEG is read too");
        Check(ImageHeader.Png(Jpeg(10, 10)) is null && ImageHeader.Jpeg(Png(10, 10)) is null && ImageHeader.Png(Png(10, 10)[..20]) is null && ImageHeader.Jpeg(Jpeg(10, 10)[..30]) is null && ImageHeader.Png(Array.Empty<byte>()) is null,
            "wrong signatures and truncated headers read as null");
        Check(ImageHeader.Png(Png(0, 10)) is null && ImageHeader.Read(Png(10, 10), "jpg") is null, "a zero dimension or the wrong format reads as null");
    }

    static void ExportFolder()
    {
        var repo = Path.Combine(Path.GetTempPath(), "export-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "global.json"), "{}");
        try
        {
            var utc = new DateTime(2026, 11, 4, 12, 30, 15, 123, DateTimeKind.Utc);
            var exports = Path.Combine(repo, ".bim", "exports");
            Check(CommandExportFiles.ExportsRoot(repo) == exports, "exports go to <repository>/.bim/exports");
            var first = CommandExportFiles.CreateDirectory(repo, utc);
            Check(first == Path.Combine(exports, "20261104T123015123Z") && Directory.Exists(first), "the export folder is .bim/exports/<utc yyyyMMddTHHmmssfffZ>", first);
            var second = CommandExportFiles.CreateDirectory(repo, utc);
            Check(second == first + "-2" && Directory.Exists(second), "a second export in the same millisecond gets a -2 folder", second);
            Check(Thrown(() => CommandExportFiles.CreateDirectory(null, utc)) is { Code: RevitCommandCodes.TransactionFailed } noRepo && noRepo.Detail!.Contains("repository", StringComparison.Ordinal), "without a repository nothing is exported");

            var png = Png(1600, 1131);
            File.WriteAllBytes(Path.Combine(first, "vista - 3D View - {3D}.png"), png);
            var files = CommandExportFiles.Collect(repo, first, "png", "fachada-norte");
            Check(files.Count == 1 && files[0] is { Width: 1600, Height: 1131 } file && file.Path == Path.Combine(first, "fachada-norte.png") && file.Bytes == png.Length && file.Sha256 == Sha(png) && File.Exists(file.Path) && !File.Exists(Path.Combine(first, "vista - 3D View - {3D}.png")),
                "the image Revit wrote is renamed to output_name and read back with absolute path, bytes, pixel size and sha256");
            var jpg = Jpeg(1131, 1600);
            File.WriteAllBytes(Path.Combine(second, "vista - Sheet - A101 - Planta.jpg"), jpg);
            var kept = CommandExportFiles.Collect(repo, second, "jpg", null);
            Check(kept.Count == 1 && kept[0].Path == Path.Combine(second, "vista - Sheet - A101 - Planta.jpg") && kept[0] is { Width: 1131, Height: 1600 } && Path.IsPathRooted(kept[0].Path), "without output_name Revit's file name is kept");

            var empty = CommandExportFiles.CreateDirectory(repo, utc);
            Check(Thrown(() => CommandExportFiles.Collect(repo, empty, "png", null)) is { Code: RevitCommandCodes.TransactionFailed } none && none.Detail!.Contains("no png", StringComparison.Ordinal), "an export that wrote no image is transaction_failed");
            File.WriteAllBytes(Path.Combine(empty, "falso.png"), Jpeg(10, 10));
            Check(Thrown(() => CommandExportFiles.Collect(repo, empty, "png", null)) is { Code: RevitCommandCodes.TransactionFailed } bad && bad.Detail!.Contains("falso.png", StringComparison.Ordinal), "a file that is not a readable PNG is transaction_failed, naming it");
            var outside = Path.Combine(repo, "elsewhere");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "x.png"), png);
            Check(Thrown(() => CommandExportFiles.Collect(repo, outside, "png", null)) is { Code: RevitCommandCodes.TransactionFailed } escaped && escaped.Detail!.Contains(".bim/exports", StringComparison.Ordinal), "files are never collected from outside .bim/exports");
            Check(Thrown(() => CommandExportFiles.Discard(repo, outside)) is { Code: RevitCommandCodes.TransactionFailed } && File.Exists(Path.Combine(outside, "x.png")), "nothing outside .bim/exports is ever deleted");
            CommandExportFiles.Discard(repo, empty);
            Check(!Directory.Exists(empty), "a discarded attempt folder under .bim/exports is removed");
        }
        finally { try { Directory.Delete(repo, true); } catch (IOException) { } }
    }

    static void Readback()
    {
        var exports = @"C:\Repo\.bim\exports";
        var file = new ExportedFile(@"C:\Repo\.bim\exports\20261104T123015123Z\fachada.png", 345678, 1600, 1131, new string('a', 64));
        Check(CommandReadbackExportSave.Export(1600, new[] { file }, exports) is null, "a file under .bim/exports whose longest side is the pixel size passes");
        Check(CommandReadbackExportSave.Export(1600, Array.Empty<ExportedFile>(), exports) is { } none && none.Contains("no image", StringComparison.Ordinal), "no file is a readback mismatch");
        Check(CommandReadbackExportSave.Export(1600, new[] { file with { Width = 1200, Height = 800 } }, exports) is { } small && small.Contains("1200", StringComparison.Ordinal), "a wrong longest side is a readback mismatch naming the size");
        Check(CommandReadbackExportSave.Export(1600, new[] { file with { Path = @"C:\Repo\fachada.png" } }, exports) is { } escaped && escaped.Contains(".bim/exports", StringComparison.Ordinal), "a file outside .bim/exports is a readback mismatch");
        Check(CommandReadbackExportSave.Export(1600, new[] { file with { Bytes = 0 } }, exports) is not null, "an empty file is a readback mismatch");

        var json = JsonNode.Parse(CommandReadbackExportSave.ExportJson(new ExportedView(ViewUid, "{3D}", "3d"), "png", 1600, "realistic", "shaded", "Horizontal", @"C:\Repo\.bim\exports\20261104T123015123Z", new[] { file }))!["export"]!;
        Check(json["view"]!["unique_id"]!.GetValue<string>() == ViewUid && json["view"]!["name"]!.GetValue<string>() == "{3D}" && json["view"]!["kind"]!.GetValue<string>() == "3d"
            && json["format"]!.GetValue<string>() == "png" && json["pixel_size"]!.GetValue<int>() == 1600 && json["visual_style"]!.GetValue<string>() == "realistic" && json["view_visual_style"]!.GetValue<string>() == "shaded"
            && json["fit_direction"]!.GetValue<string>() == "horizontal" && json["directory"]!.GetValue<string>() == @"C:\Repo\.bim\exports\20261104T123015123Z" && json["model_changed"]!.GetValue<bool>() == false,
            "export readback: view, format, pixel size, requested and view's own visual style, fit direction, folder, model_changed false");
        var listed = json["files"]!.AsArray();
        Check(listed.Count == 1 && listed[0]!["path"]!.GetValue<string>() == file.Path && listed[0]!["bytes"]!.GetValue<long>() == 345678 && listed[0]!["width"]!.GetValue<int>() == 1600 && listed[0]!["height"]!.GetValue<int>() == 1131 && listed[0]!["sha256"]!.GetValue<string>() == file.Sha256,
            "each exported file lists absolute path, bytes, width, height and sha256");
        var sheetJson = JsonNode.Parse(CommandReadbackExportSave.ExportJson(new ExportedView(ViewUid, "A101 - Planta", "sheet"), "jpg", 2400, null, null, "Vertical", @"C:\Repo\.bim\exports\x", new[] { file }))!["export"]!;
        Check(sheetJson["visual_style"] is null && sheetJson.AsObject().ContainsKey("visual_style") && sheetJson["view_visual_style"] is null && sheetJson["fit_direction"]!.GetValue<string>() == "vertical", "a sheet export reads back null visual styles");

        var started = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var saved = new ObservedSavedDocument(FixturePath, 12_345_678, started.AddSeconds(3), false);
        Check(CommandReadbackExportSave.Saved(FixturePath, true, started, saved) is null, "a save that rewrote the file and cleared IsModified passes");
        Check(CommandReadbackExportSave.Saved(FixturePath, true, started, saved with { IsModified = true }) is { } modified && modified.Contains("unsaved", StringComparison.Ordinal), "IsModified still true after save is a mismatch");
        Check(CommandReadbackExportSave.Saved(FixturePath, true, started, saved with { LastWriteUtc = started.AddMinutes(-5) }) is { } stale && stale.Contains("not rewritten", StringComparison.Ordinal), "an unchanged last-write time after saving changes is a mismatch");
        Check(CommandReadbackExportSave.Saved(FixturePath, false, started, saved with { LastWriteUtc = started.AddMinutes(-5) }) is null, "a document without changes may be left as it is on disk");
        Check(CommandReadbackExportSave.Saved(FixturePath, true, started, saved with { Bytes = 0 }) is { } missing && missing.Contains("missing or empty", StringComparison.Ordinal), "a missing or empty file after save is a mismatch");
        Check(CommandReadbackExportSave.Saved(FixturePath, true, started, saved with { Path = @"C:\Repo\.bim\native-runs\run-1\Otro.rvt" }) is { } moved && moved.Contains("Otro.rvt", StringComparison.Ordinal), "a different path after save is a mismatch");
        Check(CommandReadbackExportSave.Saved(FixturePath, true, started, saved with { LastWriteUtc = started.AddSeconds(-1) }) is null, "two seconds of clock tolerance on the last-write time");
        var saveJson = JsonNode.Parse(CommandReadbackExportSave.SaveJson(saved, true))!["document"]!;
        Check(saveJson["path"]!.GetValue<string>() == FixturePath && saveJson["bytes"]!.GetValue<long>() == 12_345_678 && saveJson["last_write_utc"]!.GetValue<string>() == "2026-11-04T12:00:03.0000000Z"
            && saveJson["is_modified"]!.GetValue<bool>() == false && saveJson["had_unsaved_changes"]!.GetValue<bool>() == true, "save readback: path, bytes, last-write UTC (round-trip format), is_modified false, had_unsaved_changes");
    }
}
