using Bim.Core;
using Bim.Revit;
using System.Text.Json;
using System.Text.Json.Nodes;
// P9-08 document.save_as contract oracle: the catalogue entry and its schema, the typed binder (path normalisation and
// traversal refusals), every RevitSaveAsGuard refusal with its reason and field (injected probes), the guarded client
// path (nothing forwarded when refused; on Windows also against a temporary repository on disk), the add-in admission,
// the post-save location check and the Autodesk-free executor rules, readback and JSON in core RevitCommandsSaveAs.cs.
// No Revit is involved: a passing run is unit-tested evidence only; CommandsExecutorSaveAs.cs (Document.SaveAs without
// overwrite) is compiled, never run.
static class CatalogueSaveAsChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string Repo = @"C:\Repo";
    const string CopiesRoot = @"C:\Repo\.bim\native-runs";
    const string OwnerRoot = @"C:\Users\Owner\Documents\ARQ";
    const string RealPath = @"C:\Users\Owner\Documents\ARQ\Cabana\Cabana.rvt";
    const string Target = @"C:\Users\Owner\Documents\ARQ\Cabana\Cabana v2 (Zona Sur).rvt";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string Production = @"C:\Users\Owner\Documents\Casa Angel\Casa Angel.rvt";
    const string Pipe = "bim-agentic-os-2025-4242";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static RevitCommandPolicy Policy => new RevitCommandPolicy(new[] { CopiesRoot, OwnerRoot }) with { RepositoryDirectory = Repo };

    static JsonObject Envelope(JsonNode args, string path = RealPath, string command = "document.save_as") => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-save-as",
        ["document"] = new JsonObject { ["path"] = path, ["unique_id"] = Uid },
        ["command"] = command,
        ["args"] = args.DeepClone(),
    };
    static JsonObject Args(string path) => new() { ["path"] = path };

    static RevitCommandException? Refusal(string json)
    {
        try { RevitCommandProtocol.Parse(json); return null; }
        catch (RevitCommandException error) { return error; }
        catch (Exception error) { return new RevitCommandException("unexpected " + error.GetType().Name, null, error.Message); }
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }
    static string? Code(string line) => JsonNode.Parse(line)?["code"]?.GetValue<string>();
    static string? Field(string line) => JsonNode.Parse(line)?["field"]?.GetValue<string>();
    static string Detail(string line) => JsonNode.Parse(line)?["detail"]?.GetValue<string>() ?? "";

    // Folders exist unless their path ends with .rvt or names a "Missing" folder; no file exists.
    static RevitSaveCopyProbe Files(Func<string, bool>? file = null, Func<string, bool>? directory = null) =>
        new(file ?? (_ => false), directory ?? (path => !path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) && !path.Contains("Missing", StringComparison.Ordinal)));
    static readonly RevitFileSystemProbe NoLinks = new(_ => false, _ => null);

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        Guard();
        ServicePath();
        DiskPath();
        AddInAdmission();
        Rules();
        Written();
    }

    static void Catalogue()
    {
        var spec = RevitCommandCatalogue.Find("document.save_as");
        Check(spec is { Writes: true, ToolName: "revit_document_save_as" } && RevitCommandCatalogue.SaveAs.Count == 1 && ReferenceEquals(RevitCommandCatalogue.SaveAs.FirstOrDefault(), spec),
            "document.save_as is a write catalogue command with tool revit_document_save_as that Find resolves");
        var all = RevitCommandCatalogue.All.ToList();
        var at = spec is null ? -1 : all.IndexOf(spec);
        Check(at > 0 && at + 1 < all.Count && all[at - 1].Name == "document.save_copy" && all[at + 1].Name == "roof.create_extrusion", "document.save_as follows document.save_copy and precedes roof.create_extrusion in the catalogue");
        Check(all.Select(s => s.Name).Distinct().Count() == all.Count && all.Select(s => s.ToolName).Distinct().Count() == all.Count, "command and tool names stay distinct with document.save_as");
        Check(RevitCommandCatalogue.IsDestructive("document.save_as") && !RevitCommandCatalogue.WritesFiles("document.save_as"), "document.save_as is destructive (it re-points the open document and writes into owner folders)");
        Check(!RevitCommandBatch.IsStepCommand(spec) && RevitCommandBatch.ExcludedWrites.Contains("document.save_as") && !RevitCommandCatalogue.BatchStepCommands.Any(s => s.Name == "document.save_as"),
            "document.save_as is an excluded write: never a command.batch step (no save inside a transaction group)");
        var batch = RevitCommandCatalogue.Find("command.batch");
        Check(batch is not null && batch.Description.Contains("document.save_as", StringComparison.Ordinal) && batch.ArgsSchemaJson.Contains("document.save_as", StringComparison.Ordinal),
            "command.batch description and step schema name document.save_as among the excluded commands");
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec?.ArgsSchemaJson ?? "null"); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
        catch (Exception error) { problem = error.Message; }
        Check(problem is null, "document.save_as args schema is valid JSON using only enforced keywords", problem ?? "");
        var description = spec?.Description ?? "";
        Check(description.Contains(".bim/native-runs", StringComparison.Ordinal) && description.Contains(".bim/revit-enabled-roots.txt", StringComparison.Ordinal) && description.Contains("never overwriting", StringComparison.Ordinal)
            && description.Contains("sha256", StringComparison.Ordinal) && description.Contains("workshared", StringComparison.Ordinal) && description.Contains("UNC", StringComparison.Ordinal)
            && description.Contains("later requests must name the returned path", StringComparison.Ordinal) && description.Contains("target_exists", StringComparison.Ordinal) && description.EndsWith('.'),
            "document.save_as states the enabled folders, no-overwrite and path rules, the readback, the document state refusals and that the open document becomes the new file");
        Check(RevitCommandCodes.ClientSide.Contains(RevitCommandCodes.SaveAsRefused) && RevitCommandCodes.SaveAsRefused == "save_as_refused" && !RevitCommandCodes.ExecutorReserved.Contains(RevitCommandCodes.SaveAsRefused),
            "save_as_refused is a typed client-side code");
        Check(RevitSaveAsGuard.Reasons.Distinct().Count() == RevitSaveAsGuard.Reasons.Count && RevitSaveAsGuard.Reasons.Count == 12
            && RevitSaveAsGuard.Reasons.All(reason => System.Text.RegularExpressions.Regex.IsMatch(reason, @"\A[a-z]+(_[a-z]+)*\z")), "save as refusal reasons are twelve distinct snake_case tokens");
        var copies = new[] { CopiesRoot };
        Check(PermissionPolicy.RequiredLevel("document.save_as", FixturePath, copies) == PermissionLevel.WriteCopies && PermissionPolicy.RequiredLevel("document.save_as", RealPath, copies) == PermissionLevel.WriteRealProject,
            "document.save_as needs write-copies on a test copy and write-real-project on a real project");
    }

    static void Binding()
    {
        var parsed = Refusal(Envelope(Args(Target)).ToJsonString()) is null ? RevitCommandProtocol.Parse(Envelope(Args(Target)).ToJsonString()) : null;
        Check(parsed?.Args is DocumentSaveAsArgs { Path: Target }, "document.save_as binds DocumentSaveAsArgs with the path (spaces and parentheses allowed)");
        foreach (var (label, path) in new[]
        {
            ("an upper-case extension", @"C:\Users\Owner\Documents\ARQ\Cabana\Cabana-v2.RVT"),
            ("accented letters", @"C:\Users\Owner\Documents\ARQ\Cabaña\Cabaña Ñandú 2026.rvt"),
            ("dots inside the name", @"C:\Users\Owner\Documents\ARQ\Cabana\Cabana.v2.2026-11-10.rvt"),
            ("a dotted folder", @"C:\Users\ezead\OneDrive\Documentos\ARQ.ARG\Casa\Casa_v3.rvt"),
        })
            Check(Refusal(Envelope(Args(path)).ToJsonString()) is null, "document.save_as accepts " + label);
        void Rejects(string name, JsonNode envelope, string code, string? reason = null)
        {
            var error = Refusal(envelope.ToJsonString());
            Check(error is not null && error.Code == code && (reason is null || (error.Field == "args.path" && error.Detail!.StartsWith(reason + ":", StringComparison.Ordinal))), name,
                error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
        }
        Rejects("document.save_as without path is args_invalid", Envelope(new JsonObject()), RevitCommandCodes.ArgsInvalid);
        Rejects("document.save_as with another member (overwrite) is args_invalid", Envelope(new JsonObject { ["path"] = Target, ["overwrite"] = true }), RevitCommandCodes.ArgsInvalid);
        Rejects("document.save_as with a path over 259 characters is args_invalid", Envelope(Args(@"C:\Users\Owner\Documents\ARQ\" + new string('x', 230) + @"\Cabana.rvt")), RevitCommandCodes.ArgsInvalid);
        foreach (var (label, path, reason) in PathCases())
            Rejects("document.save_as with " + label + " is args_invalid " + reason + " at args.path", Envelope(Args(path)), RevitCommandCodes.ArgsInvalid, reason);
    }

    // Text refusals: the binder (args_invalid) and the guard (save_as_refused) give the same reason.
    static IEnumerable<(string Label, string Path, string Reason)> PathCases() => new[]
    {
        ("a UNC path", @"\\server\share\ARQ\Cabana-v2.rvt", RevitSaveAsGuard.PathUnc),
        ("a forward-slash UNC path", @"//server/share/ARQ/Cabana-v2.rvt", RevitSaveAsGuard.PathUnc),
        ("a \\\\?\\ device-namespace path", @"\\?\C:\Users\Owner\Documents\ARQ\Cabana-v2.rvt", RevitSaveAsGuard.PathUnc),
        ("a \\\\.\\ device-namespace path", @"\\.\C:\Users\Owner\Documents\ARQ\Cabana-v2.rvt", RevitSaveAsGuard.PathUnc),
        ("a relative path", @"ARQ\Cabana\Cabana-v2.rvt", RevitSaveAsGuard.PathRelative),
        ("a dot-relative path", @".\Cabana-v2.rvt", RevitSaveAsGuard.PathRelative),
        ("a parent-relative path", @"..\Cabana-v2.rvt", RevitSaveAsGuard.PathRelative),
        ("a drive-relative path", @"C:Cabana-v2.rvt", RevitSaveAsGuard.PathRelative),
        ("a root-relative path", @"\Users\Owner\Documents\ARQ\Cabana-v2.rvt", RevitSaveAsGuard.PathRelative),
        ("a /-rooted path", "/home/owner/ARQ/Cabana-v2.rvt", RevitSaveAsGuard.PathRelative),
        ("mixed separators", @"C:\Users\Owner/Documents\ARQ\Cabana\Cabana-v2.rvt", RevitSaveAsGuard.SeparatorInvalid),
        ("forward slashes only", "C:/Users/Owner/Documents/ARQ/Cabana/Cabana-v2.rvt", RevitSaveAsGuard.SeparatorInvalid),
        ("a .. segment", @"C:\Users\Owner\Documents\ARQ\Cabana\..\..\Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a . segment", @"C:\Users\Owner\Documents\ARQ\.\Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a '.. ' segment (Windows reads it as ..)", @"C:\Users\Owner\Documents\ARQ\Cabana\.. \Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a '...' segment", @"C:\Users\Owner\Documents\ARQ\...\Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a folder ending with a dot", @"C:\Users\Owner\Documents\ARQ.\Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a folder ending with a space", @"C:\Users\Owner\Documents\ARQ \Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a doubled backslash", @"C:\Users\Owner\Documents\ARQ\\Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a trailing backslash", @"C:\Users\Owner\Documents\ARQ\Cabana-v2.rvt\", RevitSaveAsGuard.PathInvalid),
        ("an alternate data stream", @"C:\Users\Owner\Documents\ARQ\Cabana-v2.rvt:hidden", RevitSaveAsGuard.PathInvalid),
        ("an alternate data stream with type", @"C:\Users\Owner\Documents\ARQ\Cabana-v2.rvt::$DATA", RevitSaveAsGuard.PathInvalid),
        ("an 8.3 short name", @"C:\Users\OWNER~1\Documents\ARQ\Cabana-v2.rvt", RevitSaveAsGuard.PathInvalid),
        ("a wildcard", @"C:\Users\Owner\Documents\ARQ\Cabana*.rvt", RevitSaveAsGuard.PathInvalid),
        ("a device name file", @"C:\Users\Owner\Documents\ARQ\CON.rvt", RevitSaveAsGuard.DeviceName),
        ("a lower-case device name file", @"C:\Users\Owner\Documents\ARQ\nul.rvt", RevitSaveAsGuard.DeviceName),
        ("a device name with a trailing space before the extension", @"C:\Users\Owner\Documents\ARQ\com1 .rvt", RevitSaveAsGuard.DeviceName),
        ("a device name folder", @"C:\Users\Owner\Documents\ARQ\LPT1\Cabana-v2.rvt", RevitSaveAsGuard.DeviceName),
        ("a device name folder with an extension", @"C:\Users\Owner\Documents\ARQ\aux.txt\Cabana-v2.rvt", RevitSaveAsGuard.DeviceName),
        ("a family extension", @"C:\Users\Owner\Documents\ARQ\Cabana-v2.rfa", RevitSaveAsGuard.NameInvalid),
        ("a template extension", @"C:\Users\Owner\Documents\ARQ\Cabana-v2.rte", RevitSaveAsGuard.NameInvalid),
        ("no extension", @"C:\Users\Owner\Documents\ARQ\Cabana-v2", RevitSaveAsGuard.NameInvalid),
        ("a double extension ending elsewhere", @"C:\Users\Owner\Documents\ARQ\Cabana-v2.rvt.bak", RevitSaveAsGuard.NameInvalid),
        ("only the extension", @"C:\Users\Owner\Documents\ARQ\.rvt", RevitSaveAsGuard.NameInvalid),
        ("a Revit backup name", @"C:\Users\Owner\Documents\ARQ\Cabana.0001.rvt", RevitSaveAsGuard.NameInvalid),
        ("an upper-case Revit backup name", @"C:\Users\Owner\Documents\ARQ\Cabana.0042.RVT", RevitSaveAsGuard.NameInvalid),
        ("a double dot in the name", @"C:\Users\Owner\Documents\ARQ\Cabana..v2.rvt", RevitSaveAsGuard.NameInvalid),
        ("a stem ending with a space", @"C:\Users\Owner\Documents\ARQ\Cabana .rvt", RevitSaveAsGuard.NameInvalid),
        ("a name starting with a dash", @"C:\Users\Owner\Documents\ARQ\-Cabana.rvt", RevitSaveAsGuard.NameInvalid),
        ("a semicolon in the name", @"C:\Users\Owner\Documents\ARQ\Cabana;v2.rvt", RevitSaveAsGuard.NameInvalid),
        ("a name over 128 characters", @"C:\Users\Owner\Documents\ARQ\" + new string('x', 125) + ".rvt", RevitSaveAsGuard.NameInvalid),
    };

    static RevitSaveAsRefusal? Guarded(string target, string source = RealPath, RevitCommandPolicy? policy = null, RevitFileSystemProbe? links = null, RevitSaveCopyProbe? files = null) =>
        RevitSaveAsGuard.Check(target, source, policy ?? Policy, links ?? NoLinks, files ?? Files());

    static void Refused(string name, RevitSaveAsRefusal? refusal, string reason, string field) =>
        Check(refusal is not null && refusal.Reason == reason && refusal.Field == field && refusal.Detail.StartsWith(reason + ": ", StringComparison.Ordinal)
            && refusal.ToException() is { Code: RevitCommandCodes.SaveAsRefused } error && error.Field == field && error.Detail == refusal.Detail,
            name, refusal is null ? "allowed" : refusal.Reason + " at " + refusal.Field + " (" + refusal.Message + ")");

    static void Guard()
    {
        Check(Guarded(Target) is null, "a new .rvt beside the open owner-enabled project is allowed");
        Check(Guarded(@"C:\Users\Owner\Documents\ARQ\Archivo\Cabana-2026.rvt") is null, "a new .rvt in another folder of the same enabled root is allowed");
        Check(Guarded(@"c:\users\owner\documents\arq\cabana\CABANA-V3.rvt") is null, "drive paths compare case-insensitively against the enabled roots");
        Check(Guarded(@"C:\Repo\.bim\native-runs\run-1\Cabana-copia.rvt") is null, "a real project may be saved as a new file under .bim/native-runs (an enabled root)");
        Check(Guarded(@"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test-v2.rvt", FixturePath) is null, "an enabled test copy may be saved as a new file beside it");
        Check(Guarded(@"C:\Users\Owner\Documents\ARQ\Cabana\Fixture-out.rvt", FixturePath) is null, "an enabled test copy may be saved into an owner-enabled folder");
        foreach (var (label, path, reason) in PathCases())
            Refused("guard: " + label + " is " + reason, Guarded(path), reason, "args.path");
        Refused("guard: a path over 259 characters is path_invalid", Guarded(@"C:\Users\Owner\Documents\ARQ\" + new string('x', 230) + @"\Cabana.rvt"), RevitSaveAsGuard.PathInvalid, "args.path");
        Refused("guard: an empty path is path_invalid", Guarded(""), RevitSaveAsGuard.PathInvalid, "args.path");
        Refused("guard: a control character is path_invalid", Guarded("C:\\Users\\Owner\\Documents\\ARQ\\Cab\tana.rvt"), RevitSaveAsGuard.PathInvalid, "args.path");
        Refused("a target outside every enabled root (the production model's folder) is outside_enabled_roots", Guarded(@"C:\Users\Owner\Documents\Casa Angel\Casa Angel v2.rvt"), RevitSaveAsGuard.OutsideEnabledRoots, "args.path");
        Refused("the production model itself as target is outside_enabled_roots (never overwritten)", Guarded(Production, files: Files(file: path => path == Production)), RevitSaveAsGuard.OutsideEnabledRoots, "args.path");
        Refused("a target in .bim/exports is outside_enabled_roots", Guarded(@"C:\Repo\.bim\exports\Cabana.rvt"), RevitSaveAsGuard.OutsideEnabledRoots, "args.path");
        Refused("a target in the repository outside .bim/native-runs is outside_enabled_roots", Guarded(@"C:\Repo\Cabana.rvt"), RevitSaveAsGuard.OutsideEnabledRoots, "args.path");
        Refused("a target in a sibling ARQ-x folder is outside_enabled_roots (whole segments)", Guarded(@"C:\Users\Owner\Documents\ARQ-x\Cabana.rvt"), RevitSaveAsGuard.OutsideEnabledRoots, "args.path");
        Refused("a target on another drive is outside_enabled_roots", Guarded(@"D:\Users\Owner\Documents\ARQ\Cabana.rvt"), RevitSaveAsGuard.OutsideEnabledRoots, "args.path");
        Refused("with no enabled root nothing is saved (outside_enabled_roots)", Guarded(Target, policy: RevitCommandPolicy.None), RevitSaveAsGuard.OutsideEnabledRoots, "args.path");
        Refused("an open document outside the enabled roots is source_not_enabled at document.path", Guarded(Target, Production), RevitSaveAsGuard.SourceNotEnabled, "document.path");
        Refused("an open document with a relative path is source_not_enabled", Guarded(Target, @"ARQ\Cabana.rvt"), RevitSaveAsGuard.SourceNotEnabled, "document.path");
        Refused("the open document's own path is same_as_source", Guarded(RealPath, files: Files(file: path => path == RealPath)), RevitSaveAsGuard.SameAsSource, "args.path");
        Refused("the open document's own path in another case is same_as_source", Guarded(@"c:\USERS\owner\documents\arq\cabana\cabana.RVT"), RevitSaveAsGuard.SameAsSource, "args.path");
        var probed = new List<string>();
        var linkedFolder = new RevitFileSystemProbe(path => { probed.Add(path); return path.EndsWith(@"\Cabana", StringComparison.Ordinal); }, _ => null);
        Refused("a junction or symlink between the enabled root and the target is target_linked", Guarded(Target, links: linkedFolder), RevitSaveAsGuard.TargetLinked, "args.path");
        Check(probed.Count >= 2 && RevitCopiesGuard.IsUnder(probed[0], OwnerRoot) && RevitCopiesGuard.IsUnder(OwnerRoot, probed[0]), "the link probe starts at the matched enabled root", string.Join(" | ", probed));
        Refused("a target that is a hard link is target_linked", Guarded(Target, links: new RevitFileSystemProbe(_ => false, path => path == Target ? 2 : null)), RevitSaveAsGuard.TargetLinked, "args.path");
        Refused("a target in a folder that does not exist is folder_missing", Guarded(@"C:\Users\Owner\Documents\ARQ\Missing\Cabana-v2.rvt"), RevitSaveAsGuard.FolderMissing, "args.path");
        Refused("an existing file is target_exists (never overwritten)", Guarded(Target, files: Files(file: path => path == Target)), RevitSaveAsGuard.TargetExists, "args.path");
        Refused("an existing folder of that name is target_exists", Guarded(Target, files: Files(directory: _ => true)), RevitSaveAsGuard.TargetExists, "args.path");
        Refused("another existing project in the enabled root is target_exists (never overwritten)", Guarded(@"C:\Users\Owner\Documents\ARQ\Otro\Otro.rvt", files: Files(file: path => path.EndsWith(@"\Otro.rvt", StringComparison.Ordinal))), RevitSaveAsGuard.TargetExists, "args.path");
        Check(RevitSaveAsGuard.IsDeviceName("CON") && RevitSaveAsGuard.IsDeviceName("con.rvt") && RevitSaveAsGuard.IsDeviceName("COM9") && RevitSaveAsGuard.IsDeviceName("LPT¹") && RevitSaveAsGuard.IsDeviceName("CONIN$")
            && !RevitSaveAsGuard.IsDeviceName("CONSOLA") && !RevitSaveAsGuard.IsDeviceName("Cabana") && !RevitSaveAsGuard.IsDeviceName("COM10"), "reserved device names are recognised by their part before the first dot");
    }

    // The single client path (CLI and MCP): every text, root and source refusal is decided before the pipe.
    static void ServicePath()
    {
        var forwarded = new List<string>();
        string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
        RevitCommandResult Send(string target, string source = RealPath, RevitCommandPolicy? policy = null) =>
            RevitCommandService.Run(Pipe, Envelope(Args(target), source).ToJsonString(), policy ?? Policy, Transport, NoLinks);
        void NotForwarded(string name, RevitCommandResult result, string code, string field, string? reason)
        {
            Check(result.IsError && !result.Forwarded && Code(result.Line) == code && Field(result.Line) == field && (reason is null || Detail(result.Line).StartsWith(reason + ":", StringComparison.Ordinal)) && forwarded.Count == 0, name, result.Line);
        }
        foreach (var (label, path, reason) in PathCases())
            NotForwarded("client: " + label + " is args_invalid " + reason + " before the pipe", Send(path), RevitCommandCodes.ArgsInvalid, "args.path", reason);
        NotForwarded("client: a target outside the enabled roots is save_as_refused outside_enabled_roots before the pipe", Send(@"C:\Users\Owner\Documents\Casa Angel\Casa Angel v2.rvt"), RevitCommandCodes.SaveAsRefused, "args.path", RevitSaveAsGuard.OutsideEnabledRoots);
        NotForwarded("client: the open document's own path is save_as_refused same_as_source before the pipe", Send(RealPath), RevitCommandCodes.SaveAsRefused, "args.path", RevitSaveAsGuard.SameAsSource);
        NotForwarded("client: a target in a missing folder is save_as_refused folder_missing before the pipe", Send(@"C:\Users\Owner\Documents\ARQ\Missing\Cabana-v2.rvt"), RevitCommandCodes.SaveAsRefused, "args.path", RevitSaveAsGuard.FolderMissing);
        NotForwarded("client: a save as of a document outside every enabled root is document_not_enabled before the pipe", Send(Target, Production), RevitCommandCodes.DocumentNotEnabled, "document.path", null);
        var linked = RevitCommandService.Run(Pipe, Envelope(Args(Target)).ToJsonString(), Policy, Transport, new RevitFileSystemProbe(path => path.EndsWith(@"\Cabana", StringComparison.Ordinal) && !path.EndsWith(".rvt", StringComparison.Ordinal), _ => null));
        NotForwarded("client: a linked folder between the root and the open document is document_link_refused before the pipe", linked, RevitCommandCodes.DocumentLinkRefused, "document.path", null);
        NotForwarded("client: with no enabled root a save as is document_not_enabled before the pipe", Send(Target, policy: RevitCommandPolicy.None), RevitCommandCodes.DocumentNotEnabled, "document.path", null);
        Check(RevitCommandService.Run(Pipe, Envelope(Args(Target), command: "document.save_copy").ToJsonString(), Policy, Transport, NoLinks) is { IsError: true, Forwarded: false } copy && Code(copy.Line) == RevitCommandCodes.ArgsInvalid && forwarded.Count == 0,
            "document.save_copy keeps its own BIM-Agentic-Test name rule (unchanged by document.save_as)");
    }

    // On disk (Windows only: the target must be a drive path): a temporary repository with .bim/native-runs and an
    // owner-enabled project folder; a valid target is forwarded, an existing file and a symlinked folder are refused.
    static void DiskPath()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP document.save_as on-disk client path: drive paths need Windows"); return; }
        // Under the test binary, not %TEMP%: a runner's TEMP may be an 8.3 short path (RUNNER~1), which the guard refuses.
        var repo = Path.Combine(AppContext.BaseDirectory, "bim-save-as-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = Path.Combine(repo, ".bim", "native-runs", "run-1");
            var owner = Path.Combine(repo, "owner-projects");
            var project = Path.Combine(owner, "Cabana");
            Directory.CreateDirectory(run);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(repo, "global.json"), "{}");
            var real = Path.Combine(project, "Cabana.rvt");
            File.WriteAllBytes(real, new byte[] { 4, 5, 6 });
            var existing = Path.Combine(project, "Cabana-old.rvt");
            File.WriteAllBytes(existing, new byte[] { 7 });
            var policy = RevitCommandPolicy.FromAllowlist(owner + "\n", Path.Combine(repo, ".bim", "native-runs")) with { RepositoryDirectory = repo };
            var forwarded = new List<string>();
            string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
            RevitCommandResult Send(string target) => RevitCommandService.Run(Pipe, Envelope(Args(target), real).ToJsonString(), policy, Transport, RevitFileSystemProbe.Real);
            var target = Path.Combine(project, "Cabana v2.rvt");
            var ok = Send(target);
            Check(ok.Forwarded && Code(ok.Line) == RevitCommandCodes.NoResponse && forwarded.Count == 1
                && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(Args(target), real).ToJsonString())),
                "disk: a new .rvt beside the owner-enabled project is forwarded as the canonical wire line", ok.Line);
            var refused = Send(existing);
            Check(refused.IsError && !refused.Forwarded && Code(refused.Line) == RevitCommandCodes.SaveAsRefused && Detail(refused.Line).StartsWith(RevitSaveAsGuard.TargetExists + ":", StringComparison.Ordinal) && forwarded.Count == 1,
                "disk: an existing file is save_as_refused target_exists before the pipe (never overwritten)", refused.Line);
            var missing = Send(Path.Combine(project, "nope", "Cabana.rvt"));
            Check(missing.IsError && !missing.Forwarded && Detail(missing.Line).StartsWith(RevitSaveAsGuard.FolderMissing + ":", StringComparison.Ordinal) && forwarded.Count == 1, "disk: a missing folder is folder_missing", missing.Line);
            var outside = Path.Combine(repo, "outside");
            Directory.CreateDirectory(outside);
            var linkCreated = false;
            try { Directory.CreateSymbolicLink(Path.Combine(project, "linked"), outside); linkCreated = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            if (linkCreated)
            {
                var link = Send(Path.Combine(project, "linked", "Cabana.rvt"));
                Check(link.IsError && !link.Forwarded && Detail(link.Line).StartsWith(RevitSaveAsGuard.TargetLinked + ":", StringComparison.Ordinal) && forwarded.Count == 1, "disk: a symlinked folder inside the enabled root is target_linked", link.Line);
            }
            else Console.WriteLine("SKIP document.save_as symlink case: this machine cannot create directory symlinks");
        }
        finally
        {
            try { Directory.Delete(repo, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // The add-in repeats the write admission on the document it has open, then the guard on that document's PathName.
    static void AddInAdmission()
    {
        CommandDocumentFacts Facts(string path, bool readOnly = false, bool workshared = false, bool family = false, bool modifiable = false) => new(path, Uid, readOnly, family, workshared, modifiable);
        RevitCommandEnvelope At(string path) => RevitCommandProtocol.Parse(Envelope(Args(Target), path).ToJsonString());
        Check(Thrown(() => CommandAdmission.Check(At(RealPath), Facts(RealPath), () => Policy, NoLinks)) is null, "add-in: save as of an owner-enabled project passes the write admission");
        Check(Thrown(() => CommandAdmission.Check(At(FixturePath), Facts(FixturePath), () => Policy, NoLinks)) is null, "add-in: save as of an enabled test copy passes the write admission");
        Check(Thrown(() => CommandAdmission.Check(At(Production), Facts(Production), () => Policy, NoLinks)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "add-in: the production model (outside every enabled root) is document_not_enabled");
        Check(Thrown(() => CommandAdmission.Check(At(RealPath), Facts(RealPath, workshared: true), () => Policy, NoLinks)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "add-in: a workshared document is refused at admission");
        Check(Thrown(() => CommandAdmission.Check(At(RealPath), Facts(RealPath, modifiable: true), () => Policy, NoLinks)) is { Code: RevitCommandCodes.TransactionFailed }, "add-in: an open transaction refuses the save as");
        Check(Thrown(() => CommandAdmission.Check(At(RealPath), Facts(@"C:\Users\Owner\Documents\ARQ\Cabana\Otro.rvt"), () => Policy, NoLinks)) is { Code: RevitCommandCodes.DocumentConflict }, "add-in: an open document at another path is document_conflict");
    }

    static void Rules()
    {
        var ok = new SaveCopyFacts(RealPath, false, false, false, false);
        Check(Thrown(() => RevitSaveAsRules.CheckSavable(ok, RealPath)) is null && Thrown(() => RevitSaveAsRules.CheckSavable(ok, RealPath.ToUpperInvariant())) is null, "a saved, local, non-workshared, writable project can be saved as (drive paths case-insensitive)");
        Check(Thrown(() => RevitSaveAsRules.CheckSavable(ok with { PathName = null }, RealPath)) is { Code: RevitCommandCodes.DocumentNotEnabled, Field: "document.path" }
            && Thrown(() => RevitSaveAsRules.CheckSavable(ok with { PathName = "" }, RealPath)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "a never-saved document is refused");
        Check(Thrown(() => RevitSaveAsRules.CheckSavable(ok, @"C:\Users\Owner\Documents\ARQ\Cabana\Otro.rvt")) is { Code: RevitCommandCodes.DocumentConflict, Field: "document.path" }, "an open document at another path than the request is document_conflict");
        foreach (var (label, facts, word) in new[] { ("read-only", ok with { IsReadOnly = true }, "read-only"), ("workshared", ok with { IsWorkshared = true }, "workshared"), ("cloud", ok with { IsModelInCloud = true }, "cloud"), ("family", ok with { IsFamilyDocument = true }, "family") })
            Check(Thrown(() => RevitSaveAsRules.CheckSavable(facts, RealPath)) is { Code: RevitCommandCodes.DocumentNotEnabled } error && error.Detail!.Contains(word, StringComparison.Ordinal) && (word == "read-only" || error.Detail!.Contains("document.save_as", StringComparison.Ordinal)),
                "a " + label + " document is refused by document.save_as");

        var when = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
        var previous = new SaveCopyFileState(true, 4096, when);
        var sha = new string('b', 64);
        var good = new ObservedSaveCopy(Target, false, new SaveCopyFileState(true, 5000, when.AddMinutes(1)), sha, previous, previous);
        Check(RevitSaveAsRules.Readback(Target, good) is null && RevitSaveAsRules.Readback(Target.ToUpperInvariant(), good) is null, "readback: a non-empty new file, the document re-pointed to it, no unsaved changes and an untouched previous file pass");
        foreach (var (label, observed) in new[]
        {
            ("a missing new file", good with { Copy = new SaveCopyFileState(false, 0, DateTime.MinValue) }),
            ("an empty new file", good with { Copy = good.Copy with { Bytes = 0 } }),
            ("no sha256", good with { CopySha256 = null }),
            ("a short sha256", good with { CopySha256 = "abc" }),
            ("a document still at the previous path", good with { DocumentPath = RealPath }),
            ("unsaved changes after the save", good with { IsModified = true }),
            ("a rewritten previous file", good with { SourceAfter = previous with { LastWriteUtc = when.AddMinutes(1) } }),
            ("a resized previous file", good with { SourceAfter = previous with { Bytes = 1 } }),
            ("a deleted previous file", good with { SourceAfter = new SaveCopyFileState(false, 0, DateTime.MinValue) }),
        })
            Check(RevitSaveAsRules.Readback(Target, observed) is { Length: > 0 }, "readback refuses " + label);

        var json = JsonNode.Parse(RevitSaveAsRules.Json(RealPath, good, true))!["save_as"]!;
        Check(json["path"]!.GetValue<string>() == Target && json["bytes"]!.GetValue<long>() == 5000 && json["sha256"]!.GetValue<string>() == sha && json["previous_path"]!.GetValue<string>() == RealPath
            && json["previous_unchanged"]!.GetValue<bool>() && json["had_unsaved_changes"]!.GetValue<bool>() && json["document_path"]!.GetValue<string>() == Target && !json["is_modified"]!.GetValue<bool>()
            && json.AsObject().Count == 8, "readback JSON is {save_as: {path, bytes, sha256, previous_path, previous_unchanged, had_unsaved_changes, document_path, is_modified}}", json.ToJsonString());
        var warning = RevitSaveAsRules.DocumentMovedWarning(Target, RealPath);
        Check(warning.Contains(Target, StringComparison.Ordinal) && warning.Contains(RealPath, StringComparison.Ordinal) && warning.Contains("document.path", StringComparison.Ordinal), "the reply warns that the open document moved and names the old and new paths");
    }

    // After SaveAs: the written file must resolve to the requested path inside an enabled root, with no link on the way.
    static void Written()
    {
        Check(RevitSaveAsGuard.CheckWritten(Target, Policy, Target, Target, NoLinks) is null, "post-save: a file that resolves to itself inside the enabled root passes");
        Check(RevitSaveAsGuard.CheckWritten(Target, Policy, Target.ToLowerInvariant(), Target, NoLinks) is null, "post-save: drive paths compare case-insensitively");
        Check(RevitSaveAsGuard.CheckWritten(Target, RevitCommandPolicy.None, Target, Target, NoLinks) is { } noRoot && noRoot.Contains(Target, StringComparison.Ordinal), "post-save: without an enabled root the check fails naming the file");
        Check(RevitSaveAsGuard.CheckWritten(Target, Policy, null, null, NoLinks) is { Length: > 0 }, "post-save: a path that cannot be resolved fails");
        Check(RevitSaveAsGuard.CheckWritten(Target, Policy, @"C:\Users\Owner\Documents\Cabana v2.rvt", @"C:\Users\Owner\Documents\Cabana v2.rvt", NoLinks) is { } moved && moved.Contains(@"C:\Users\Owner\Documents\Cabana v2.rvt", StringComparison.Ordinal),
            "post-save: a full path that differs from the request fails naming where the file actually is");
        Check(RevitSaveAsGuard.CheckWritten(Target, Policy, Target, Production, NoLinks) is { } escaped && escaped.Contains(Production, StringComparison.Ordinal),
            "post-save: a final link target outside the enabled root (the production model) fails naming it");
        var linkedNow = new RevitFileSystemProbe(path => path.EndsWith(@"\Cabana", StringComparison.Ordinal), _ => null);
        Check(RevitSaveAsGuard.CheckWritten(Target, Policy, Target, Target, linkedNow) is { } swapped && swapped.Contains("link", StringComparison.Ordinal) && swapped.Contains(Target, StringComparison.Ordinal),
            "post-save: a folder swapped for a junction after the check fails (TOCTOU) naming the file");
        Check(RevitSaveAsGuard.CheckWritten(Target, Policy, Target, Target, new RevitFileSystemProbe(_ => false, _ => 2)) is { Length: > 0 }, "post-save: a hard-linked file fails");
        var detail = RevitSaveAsRules.WrittenElsewhereDetail("the new file resolves to X", Target);
        Check(detail.Contains("X", StringComparison.Ordinal) && detail.Contains("document.path", StringComparison.Ordinal) && detail.Contains(Target, StringComparison.Ordinal),
            "post-save failure detail names the problem and the path the open document now points to");
    }
}
