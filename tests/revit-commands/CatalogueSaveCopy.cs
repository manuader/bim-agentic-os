using Bim.Core;
using Bim.Revit;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
// P1-08 document.save_copy contract oracle: the catalogue entry and its schema, the typed binder, every
// RevitSaveCopyGuard refusal with its reason and field (injected probes), the guarded client path against a temporary
// repository on disk (nothing forwarded when refused), the add-in admission, and the Autodesk-free executor rules,
// readback and JSON in core RevitCommandsSaveCopy.cs. No Revit is involved: a passing run is unit-tested evidence only;
// CommandsExecutorSaveCopy.cs (Document.SaveAs without overwrite) is compiled, never run.
static class CatalogueSaveCopyChecks
{
    const string Uid = "60f91daf-3dd7-4283-a86d-24137b73f3da-0001fd0b";
    const string Repo = @"C:\Repo";
    const string Root = @"C:\Repo\.bim\native-runs";
    const string FixturePath = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.rvt";
    const string Target = @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test-copy-1.rvt";
    const string RealPath = @"C:\Users\Owner\Documents\ARQ\Cabana\Cabana.rvt";
    const string Pipe = "bim-agentic-os-2025-4242";

    static Action<bool, string, string> check = (_, _, _) => { };
    static void Check(bool condition, string name, string detail = "") => check(condition, name, detail);

    static JsonObject Envelope(JsonNode args, string path = FixturePath, string command = "document.save_copy") => new()
    {
        ["protocol_version"] = 1,
        ["operation_id"] = "op-save-copy",
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
    static void Rejects(string name, JsonNode envelope, string code, string? field = null)
    {
        var error = Refusal(envelope.ToJsonString());
        Check(error is not null && error.Code == code && (field is null || error.Field == field), name, error is null ? "accepted" : error.Code + " at " + error.Field + " (" + error.Detail + ")");
    }
    static RevitCommandException? Thrown(Action action)
    {
        try { action(); return null; }
        catch (RevitCommandException error) { return error; }
    }
    static string? Code(string line) => JsonNode.Parse(line)?["code"]?.GetValue<string>();
    static string? Field(string line) => JsonNode.Parse(line)?["field"]?.GetValue<string>();
    static string Detail(string line) => JsonNode.Parse(line)?["detail"]?.GetValue<string>() ?? "";

    public static void Run(Action<bool, string, string> checker)
    {
        check = checker;
        Catalogue();
        Binding();
        Guard();
        ClientPath();
        AddInAdmission();
        Rules();
        ReviewFixes();
    }

    static void Catalogue()
    {
        var spec = RevitCommandCatalogue.Find("document.save_copy");
        Check(spec is { Writes: true, ToolName: "revit_document_save_copy" } && RevitCommandCatalogue.SaveCopy.Count == 1 && ReferenceEquals(RevitCommandCatalogue.SaveCopy.FirstOrDefault(), spec),
            "document.save_copy is a write catalogue command with tool revit_document_save_copy that Find resolves");
        var all = RevitCommandCatalogue.All.ToList();
        var at = spec is null ? -1 : all.IndexOf(spec);
        Check(at > 0 && at + 1 < all.Count && all[at - 1].Name == "document.save" && all[at + 1].Name == "roof.create_extrusion", "document.save_copy follows document.save and precedes roof.create_extrusion (then command.batch) in the catalogue");
        Check(RevitCommandCatalogue.All.Select(s => s.Name).Distinct().Count() == RevitCommandCatalogue.All.Count && RevitCommandCatalogue.All.Select(s => s.ToolName).Distinct().Count() == RevitCommandCatalogue.All.Count, "command and tool names stay distinct with document.save_copy");
        Check(!RevitCommandCatalogue.IsDestructive("document.save_copy") && !RevitCommandCatalogue.WritesFiles("document.save_copy"), "document.save_copy never overwrites a file: not destructive");
        Check(!RevitCommandBatch.IsStepCommand(spec), "document.save_copy is not a command.batch step (no save inside a transaction group)");
        string? problem = null;
        try { using var schema = JsonDocument.Parse(spec?.ArgsSchemaJson ?? "null"); RevitCommandSchema.CheckSupported(schema.RootElement); if (schema.RootElement.GetProperty("type").GetString() != "object") problem = "not an object schema"; }
        catch (Exception error) { problem = error.Message; }
        Check(problem is null, "document.save_copy args schema is valid JSON using only enforced keywords", problem ?? "");
        var description = spec?.Description ?? "";
        Check(description.Contains(".bim/native-runs", StringComparison.Ordinal) && description.Contains("BIM-Agentic-Test", StringComparison.Ordinal) && description.Contains("sha256", StringComparison.Ordinal)
            && description.Contains("never overwriting", StringComparison.Ordinal) && description.Contains("real project", StringComparison.Ordinal) && description.Contains("later requests must name the returned path", StringComparison.Ordinal) && description.EndsWith('.'),
            "document.save_copy states the folder, name, no-overwrite and real-project rules, the readback and that the open document becomes the copy");
        Check(RevitCommandCodes.ClientSide.Contains(RevitCommandCodes.SaveCopyRefused) && RevitCommandCodes.SaveCopyRefused == "save_copy_refused", "save_copy_refused is a client-side code");
        Check(RevitSaveCopyGuard.Reasons.Distinct().Count() == RevitSaveCopyGuard.Reasons.Count && RevitSaveCopyGuard.Reasons.Count == 9
            && RevitSaveCopyGuard.Reasons.All(reason => System.Text.RegularExpressions.Regex.IsMatch(reason, @"\A[a-z]+(_[a-z]+)*\z")), "save copy refusal reasons are nine distinct snake_case tokens");
        Check(RevitSaveCopyGuard.CopiesDirectory == RevitCommandPolicy.DefaultRelativeRoot, "the copies root is the repository default enabled root .bim/native-runs");
    }

    static void Binding()
    {
        var parsed = Refusal(Envelope(Args(Target)).ToJsonString()) is null ? RevitCommandProtocol.Parse(Envelope(Args(Target)).ToJsonString()) : null;
        Check(parsed?.Args is DocumentSaveCopyArgs { Path: Target }, "document.save_copy binds DocumentSaveCopyArgs with the path");
        Check(Refusal(Envelope(Args(@"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test.RVT")).ToJsonString()) is null, "the .rvt extension is case-insensitive");
        Check(Refusal(Envelope(Args(@"C:\Repo\.bim\native-runs\BIM-Agentic-Test_2026-11-04.v2.rvt")).ToJsonString()) is null, "letters, digits, '.', '_' and '-' after the prefix are accepted");
        Rejects("document.save_copy without path is args_invalid", Envelope(new JsonObject()), RevitCommandCodes.ArgsInvalid);
        Rejects("document.save_copy with another member is args_invalid", Envelope(new JsonObject { ["path"] = Target, ["overwrite"] = true }), RevitCommandCodes.ArgsInvalid);
        foreach (var (label, path) in new[]
        {
            ("a relative path", @"run-1\BIM-Agentic-Test-copy.rvt"),
            ("a .. segment", @"C:\Repo\.bim\native-runs\run-1\..\BIM-Agentic-Test-copy.rvt"),
            ("an 8.3 short name", @"C:\REPO~1\.bim\native-runs\BIM-Agentic-Test-copy.rvt"),
            ("an alternate data stream", @"C:\Repo\.bim\native-runs\BIM-Agentic-Test-copy.rvt:x"),
            ("a device path", @"\\?\C:\Repo\.bim\native-runs\BIM-Agentic-Test-copy.rvt"),
        })
            Rejects("document.save_copy with " + label + " is args_invalid at args.path", Envelope(Args(path)), RevitCommandCodes.ArgsInvalid, "args.path");
        foreach (var (label, name) in new[]
        {
            ("another prefix", "Copy.rvt"), ("a lowercase prefix", "bim-agentic-test-copy.rvt"), ("a family extension", "BIM-Agentic-Test-copy.rfa"),
            ("no extension", "BIM-Agentic-Test-copy"), ("a space", "BIM-Agentic-Test copy.rvt"), ("a double dot", "BIM-Agentic-Test..rvt"),
            ("a name over 128 characters", "BIM-Agentic-Test-" + new string('x', 120) + ".rvt"),
        })
        {
            var error = Refusal(Envelope(Args(Root + @"\run-1\" + name)).ToJsonString());
            Check(error is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.path" } && error.Detail!.StartsWith(RevitSaveCopyGuard.NameInvalid + ":", StringComparison.Ordinal), "document.save_copy with " + label + " is args_invalid name_invalid at args.path", error?.Code + " " + error?.Detail);
        }
    }

    static RevitSaveCopyProbe Files(Func<string, bool>? file = null, Func<string, bool>? directory = null) =>
        new(file ?? (_ => false), directory ?? (path => path.EndsWith("run-1", StringComparison.OrdinalIgnoreCase) || path.EndsWith("native-runs", StringComparison.OrdinalIgnoreCase)));
    static readonly RevitFileSystemProbe NoLinks = new(_ => false, _ => null);

    static void Guard()
    {
        RevitSaveCopyRefusal? Guarded(string target, string source = FixturePath, string? repo = Repo, RevitFileSystemProbe? links = null, RevitSaveCopyProbe? files = null) =>
            RevitSaveCopyGuard.Check(target, source, repo, links ?? NoLinks, files ?? Files());
        void Refused(string name, RevitSaveCopyRefusal? refusal, string reason, string field) =>
            Check(refusal is not null && refusal.Reason == reason && refusal.Field == field && refusal.Detail.StartsWith(reason + ": ", StringComparison.Ordinal) && refusal.ToException() is { Code: RevitCommandCodes.SaveCopyRefused } error && error.Field == field,
                name, refusal is null ? "allowed" : refusal.Reason + " at " + refusal.Field + " (" + refusal.Message + ")");

        Check(Guarded(Target) is null, "a new BIM-Agentic-Test*.rvt beside the open test copy is allowed");
        Check(Guarded(@"c:\repo\.BIM\Native-Runs\BIM-Agentic-Test-top.rvt", files: Files(directory: path => !path.EndsWith(".rvt", StringComparison.Ordinal))) is null, "drive paths compare case-insensitively; a copy directly in the copies root is allowed");
        Check(RevitSaveCopyGuard.CopiesRoot(Repo) is { } root && RevitCopiesGuard.IsUnder(root, Root) && RevitCopiesGuard.IsUnder(Root, root) && RevitSaveCopyGuard.CopiesRoot(null) is null, "the copies root is <repository>/.bim/native-runs, none without a repository");
        Refused("a relative target is path_invalid", Guarded(@"native-runs\BIM-Agentic-Test-x.rvt"), RevitSaveCopyGuard.PathInvalid, "args.path");
        Refused("a traversal target is path_invalid", Guarded(@"C:\Repo\.bim\native-runs\..\..\BIM-Agentic-Test-x.rvt"), RevitSaveCopyGuard.PathInvalid, "args.path");
        Refused("a target named Tower.rvt is name_invalid", Guarded(@"C:\Repo\.bim\native-runs\run-1\Tower.rvt"), RevitSaveCopyGuard.NameInvalid, "args.path");
        Refused("without a repository nothing is copied (no_repository)", Guarded(Target, repo: null), RevitSaveCopyGuard.NoRepository, "args.path");
        Refused("a target in the owner's project folder is outside_copies_root", Guarded(@"C:\Users\Owner\Documents\ARQ\BIM-Agentic-Test-x.rvt"), RevitSaveCopyGuard.OutsideCopiesRoot, "args.path");
        Refused("a target in .bim/exports is outside_copies_root", Guarded(@"C:\Repo\.bim\exports\BIM-Agentic-Test-x.rvt"), RevitSaveCopyGuard.OutsideCopiesRoot, "args.path");
        Refused("a target in a sibling native-runs-x folder is outside_copies_root (whole segments)", Guarded(@"C:\Repo\.bim\native-runs-x\BIM-Agentic-Test-x.rvt"), RevitSaveCopyGuard.OutsideCopiesRoot, "args.path");
        Refused("a target on another drive is outside_copies_root", Guarded(@"D:\Repo\.bim\native-runs\BIM-Agentic-Test-x.rvt"), RevitSaveCopyGuard.OutsideCopiesRoot, "args.path");
        Refused("a real project source is source_not_test_copy at document.path", Guarded(Target, source: RealPath), RevitSaveCopyGuard.SourceNotTestCopy, "document.path");
        Refused("a source in the copies root not named BIM-Agentic-Test*.rvt is source_not_test_copy", Guarded(Target, source: @"C:\Repo\.bim\native-runs\run-1\Tower.rvt"), RevitSaveCopyGuard.SourceNotTestCopy, "document.path");
        Refused("a test-named source outside the copies root is source_not_test_copy", Guarded(Target, source: @"C:\Temp\BIM-Agentic-Test.rvt"), RevitSaveCopyGuard.SourceNotTestCopy, "document.path");
        Refused("the open document's own path is same_as_source", Guarded(@"c:\repo\.bim\native-runs\RUN-1\BIM-Agentic-Test.rvt"), RevitSaveCopyGuard.SameAsSource, "args.path");
        var probed = new List<string>();
        var linkedRun = new RevitFileSystemProbe(path => { probed.Add(path); return path.EndsWith("run-1", StringComparison.Ordinal); }, _ => null);
        Refused("a junction or symlink between the copies root and the target is target_linked", Guarded(Target, links: linkedRun), RevitSaveCopyGuard.TargetLinked, "args.path");
        Check(probed.Count >= 2 && RevitCopiesGuard.IsUnder(probed[0], Root) && RevitCopiesGuard.IsUnder(Root, probed[0]), "the link probe starts at the copies root", string.Join(" | ", probed));
        Refused("a target that is a hard link is target_linked", Guarded(Target, links: new RevitFileSystemProbe(_ => false, path => path == Target ? 2 : null)), RevitSaveCopyGuard.TargetLinked, "args.path");
        Refused("a target in a folder that does not exist is folder_missing", Guarded(@"C:\Repo\.bim\native-runs\run-9\BIM-Agentic-Test-x.rvt"), RevitSaveCopyGuard.FolderMissing, "args.path");
        Refused("an existing file is target_exists (never overwritten)", Guarded(Target, files: Files(file: path => path == Target)), RevitSaveCopyGuard.TargetExists, "args.path");
        Refused("an existing folder of that name is target_exists", Guarded(Target, files: Files(directory: _ => true)), RevitSaveCopyGuard.TargetExists, "args.path");
        Check(RevitSaveCopyGuard.IsTestCopyName("BIM-Agentic-Test.rvt") && !RevitSaveCopyGuard.IsTestCopyName("BIM-Agentic-Test.rvt.bak") && !RevitSaveCopyGuard.IsTestCopyName("XBIM-Agentic-Test.rvt"), "the owner fixture name BIM-Agentic-Test.rvt is a test copy name; suffixes and prefixes are not");
    }

    // On disk: a temporary repository with .bim/native-runs/run-1/BIM-Agentic-Test.rvt and an owner-enabled project folder.
    static void ClientPath()
    {
        var repo = Path.Combine(Path.GetTempPath(), "bim-save-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = Path.Combine(repo, ".bim", "native-runs", "run-1");
            var owner = Path.Combine(repo, "owner-projects");
            Directory.CreateDirectory(run);
            Directory.CreateDirectory(owner);
            File.WriteAllText(Path.Combine(repo, "global.json"), "{}");
            var fixture = Path.Combine(run, "BIM-Agentic-Test.rvt");
            File.WriteAllBytes(fixture, new byte[] { 1, 2, 3 });
            var real = Path.Combine(owner, "Cabana.rvt");
            File.WriteAllBytes(real, new byte[] { 4, 5, 6 });
            var policy = RevitCommandPolicy.FromAllowlist(owner + "\n", Path.Combine(repo, ".bim", "native-runs")) with { RepositoryDirectory = repo };
            var forwarded = new List<string>();
            string? Transport(string pipe, string line) { forwarded.Add(line); return null; }
            RevitCommandResult Send(string target, string source) => RevitCommandService.Run(Pipe, Envelope(Args(target), source).ToJsonString(), policy, Transport, RevitFileSystemProbe.Real);

            var ok = Send(Path.Combine(run, "BIM-Agentic-Test-copy.rvt"), fixture);
            Check(ok.Forwarded && Code(ok.Line) == RevitCommandCodes.NoResponse && forwarded.Count == 1
                && forwarded[0] == RevitCommandProtocol.WireLine(RevitCommandProtocol.Parse(Envelope(Args(Path.Combine(run, "BIM-Agentic-Test-copy.rvt")), fixture).ToJsonString())),
                "a new copy beside the open test copy is forwarded as the canonical wire line", ok.Line);
            void NotForwarded(string name, RevitCommandResult result, string code, string field, string? reason)
            {
                Check(result.IsError && !result.Forwarded && Code(result.Line) == code && Field(result.Line) == field && (reason is null || Detail(result.Line).StartsWith(reason + ":", StringComparison.Ordinal)) && forwarded.Count == 1, name, result.Line);
            }
            NotForwarded("an existing file is save_copy_refused target_exists before the pipe", Send(CreateExisting(run), fixture), RevitCommandCodes.SaveCopyRefused, "args.path", RevitSaveCopyGuard.TargetExists);
            NotForwarded("the open document's own path is save_copy_refused same_as_source before the pipe", Send(fixture, fixture), RevitCommandCodes.SaveCopyRefused, "args.path", RevitSaveCopyGuard.SameAsSource);
            NotForwarded("a copy into .bim/exports is save_copy_refused outside_copies_root before the pipe", Send(Path.Combine(repo, ".bim", "exports", "BIM-Agentic-Test-x.rvt"), fixture), RevitCommandCodes.SaveCopyRefused, "args.path", RevitSaveCopyGuard.OutsideCopiesRoot);
            NotForwarded("a copy into the owner-enabled project folder is save_copy_refused outside_copies_root", Send(Path.Combine(owner, "BIM-Agentic-Test-x.rvt"), fixture), RevitCommandCodes.SaveCopyRefused, "args.path", RevitSaveCopyGuard.OutsideCopiesRoot);
            NotForwarded("a copy of an owner-enabled real project is save_copy_refused source_not_test_copy at document.path", Send(Path.Combine(run, "BIM-Agentic-Test-real.rvt"), real), RevitCommandCodes.SaveCopyRefused, "document.path", RevitSaveCopyGuard.SourceNotTestCopy);
            NotForwarded("a copy into a missing folder is save_copy_refused folder_missing", Send(Path.Combine(run, "nope", "BIM-Agentic-Test-x.rvt"), fixture), RevitCommandCodes.SaveCopyRefused, "args.path", RevitSaveCopyGuard.FolderMissing);
            NotForwarded("a wrong file name is args_invalid at args.path before the pipe", Send(Path.Combine(run, "Copia.rvt"), fixture), RevitCommandCodes.ArgsInvalid, "args.path", RevitSaveCopyGuard.NameInvalid);
            NotForwarded("a save copy of a document outside every enabled root is document_not_enabled", Send(Path.Combine(run, "BIM-Agentic-Test-y.rvt"), Path.Combine(Path.GetTempPath(), "Elsewhere", "BIM-Agentic-Test.rvt")), RevitCommandCodes.DocumentNotEnabled, "document.path", null);
            var unrooted = RevitCommandService.Run(Pipe, Envelope(Args(Path.Combine(run, "BIM-Agentic-Test-z.rvt")), fixture).ToJsonString(), policy with { RepositoryDirectory = null }, Transport, RevitFileSystemProbe.Real);
            NotForwarded("without a known repository a save copy is save_copy_refused no_repository", unrooted, RevitCommandCodes.SaveCopyRefused, "args.path", RevitSaveCopyGuard.NoRepository);
            var outside = Path.Combine(repo, "outside");
            Directory.CreateDirectory(outside);
            var linkCreated = false;
            try { Directory.CreateSymbolicLink(Path.Combine(run, "linked"), outside); linkCreated = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
            if (linkCreated)
                NotForwarded("a copy through a symlinked folder inside the copies root is save_copy_refused target_linked", Send(Path.Combine(run, "linked", "BIM-Agentic-Test-x.rvt"), fixture), RevitCommandCodes.SaveCopyRefused, "args.path", RevitSaveCopyGuard.TargetLinked);
            else Console.WriteLine("SKIP symlink case: this machine cannot create directory symlinks");
        }
        finally
        {
            try { Directory.Delete(repo, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    static string CreateExisting(string run)
    {
        var path = Path.Combine(run, "BIM-Agentic-Test-existing.rvt");
        File.WriteAllBytes(path, new byte[] { 9 });
        return path;
    }

    // The add-in repeats the write admission on the document it has open, then the guard on that document's PathName.
    static void AddInAdmission()
    {
        RevitCommandPolicy Policy() => new RevitCommandPolicy(new[] { Root, @"C:\Users\Owner\Documents\ARQ" }) with { RepositoryDirectory = Repo };
        CommandDocumentFacts Facts(string path, bool readOnly = false, bool workshared = false, bool family = false, bool modifiable = false) => new(path, Uid, readOnly, family, workshared, modifiable);
        RevitCommandEnvelope At(string path) => RevitCommandProtocol.Parse(Envelope(Args(Target), path).ToJsonString());
        Check(Thrown(() => CommandAdmission.Check(At(FixturePath), Facts(FixturePath), Policy, NoLinks)) is null, "add-in: save copy of an enabled test copy passes the write admission");
        Check(Thrown(() => CommandAdmission.Check(At(@"C:\Projects\Real\Tower.rvt"), Facts(@"C:\Projects\Real\Tower.rvt"), Policy, NoLinks)) is { Code: RevitCommandCodes.DocumentNotEnabled }, "add-in: a document outside every enabled root is document_not_enabled");
        Check(Thrown(() => CommandAdmission.Check(At(FixturePath), Facts(FixturePath, modifiable: true), Policy, NoLinks)) is { Code: RevitCommandCodes.TransactionFailed }, "add-in: an open transaction refuses the save copy");
        Check(Thrown(() => CommandAdmission.Check(At(RealPath), Facts(RealPath), Policy, NoLinks)) is null
            && RevitSaveCopyGuard.Check(Target, RealPath, Policy().RepositoryDirectory, NoLinks, Files()) is { Reason: RevitSaveCopyGuard.SourceNotTestCopy },
            "add-in: an owner-enabled real project passes the write admission but the save copy guard refuses it (source_not_test_copy)");
    }

    static void Rules()
    {
        var ok = new SaveCopyFacts(FixturePath, false, false, false, false);
        Check(Thrown(() => RevitSaveCopyRules.CheckCopyable(ok, FixturePath)) is null && Thrown(() => RevitSaveCopyRules.CheckCopyable(ok, FixturePath.ToLowerInvariant())) is null, "a saved, local, non-workshared, writable test copy can be copied (drive paths case-insensitive)");
        Check(Thrown(() => RevitSaveCopyRules.CheckCopyable(ok with { PathName = null }, FixturePath)) is { Code: RevitCommandCodes.DocumentNotEnabled, Field: "document.path" }, "a never-saved document is refused");
        Check(Thrown(() => RevitSaveCopyRules.CheckCopyable(ok, @"C:\Repo\.bim\native-runs\run-2\BIM-Agentic-Test.rvt")) is { Code: RevitCommandCodes.DocumentConflict, Field: "document.path" }, "an open document at another path than the request is document_conflict");
        foreach (var (label, facts, word) in new[] { ("read-only", ok with { IsReadOnly = true }, "read-only"), ("workshared", ok with { IsWorkshared = true }, "workshared"), ("cloud", ok with { IsModelInCloud = true }, "cloud"), ("family", ok with { IsFamilyDocument = true }, "family") })
            Check(Thrown(() => RevitSaveCopyRules.CheckCopyable(facts, FixturePath)) is { Code: RevitCommandCodes.DocumentNotEnabled } error && error.Detail!.Contains(word, StringComparison.Ordinal), "a " + label + " document is refused");

        var when = new DateTime(2026, 11, 4, 10, 0, 0, DateTimeKind.Utc);
        var source = new SaveCopyFileState(true, 4096, when);
        var sha = new string('a', 64);
        var good = new ObservedSaveCopy(Target, false, new SaveCopyFileState(true, 5000, when.AddMinutes(1)), sha, source, source);
        Check(RevitSaveCopyRules.Readback(Target, good) is null && RevitSaveCopyRules.Readback(Target.ToUpperInvariant(), good) is null, "readback: a non-empty copy, the document re-pointed to it, no unsaved changes and an untouched source pass");
        foreach (var (label, observed) in new[]
        {
            ("a missing copy", good with { Copy = new SaveCopyFileState(false, 0, DateTime.MinValue) }),
            ("an empty copy", good with { Copy = good.Copy with { Bytes = 0 } }),
            ("no sha256", good with { CopySha256 = null }),
            ("a document still at the source path", good with { DocumentPath = FixturePath }),
            ("unsaved changes after the save", good with { IsModified = true }),
            ("a rewritten source", good with { SourceAfter = source with { LastWriteUtc = when.AddMinutes(1) } }),
            ("a resized source", good with { SourceAfter = source with { Bytes = 1 } }),
        })
            Check(RevitSaveCopyRules.Readback(Target, observed) is { Length: > 0 }, "readback refuses " + label);

        var json = JsonNode.Parse(RevitSaveCopyRules.Json(FixturePath, good, true))!["copy"]!;
        Check(json["path"]!.GetValue<string>() == Target && json["bytes"]!.GetValue<long>() == 5000 && json["sha256"]!.GetValue<string>() == sha && json["source_path"]!.GetValue<string>() == FixturePath
            && json["source_unchanged"]!.GetValue<bool>() && json["had_unsaved_changes"]!.GetValue<bool>() && json["document_path"]!.GetValue<string>() == Target && !json["is_modified"]!.GetValue<bool>()
            && json.AsObject().Count == 8, "readback JSON is {copy: {path, bytes, sha256, source_path, source_unchanged, had_unsaved_changes, document_path, is_modified}}", json.ToJsonString());
        var warning = RevitSaveCopyRules.DocumentNowCopyWarning(Target, FixturePath);
        Check(warning.Contains(Target, StringComparison.Ordinal) && warning.Contains(FixturePath, StringComparison.Ordinal) && warning.Contains("document.path", StringComparison.Ordinal), "the reply warns that the open document is now the copy and later requests name it");

        var file = Path.Combine(Path.GetTempPath(), "bim-save-copy-sha-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            var bytes = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
            File.WriteAllBytes(file, bytes);
            Check(RevitSaveCopyGuard.Sha256File(file) == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "Sha256File is the lowercase hex SHA-256 of the written file");
        }
        finally { File.Delete(file); }
    }

    // Review of P1-08: hashing a copy Revit still holds open, the post-SaveAs location check, backup names, Windows
    // segment normalisation and the stated retry rule.
    static void ReviewFixes()
    {
        var file = Path.Combine(Path.GetTempPath(), "bim-save-copy-held-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            var bytes = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 13)).ToArray();
            File.WriteAllBytes(file, bytes);
            string? hash = null;
            string? error = null;
            using (var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                try { hash = RevitSaveCopyGuard.Sha256File(file); }
                catch (IOException failure) { error = failure.GetType().Name + ": " + failure.Message; }
            }
            Check(hash == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "Sha256File reads a copy that another handle (Revit) still holds open for read and write", error ?? hash ?? "");
        }
        finally { File.Delete(file); }

        // After SaveAs: the written file must resolve to the requested path inside the copies root, with no link on the way.
        var linksNone = new RevitFileSystemProbe(_ => false, _ => null);
        Check(RevitSaveCopyGuard.CheckWritten(Target, Repo, Target, Target, linksNone) is null, "post-save: a copy that resolves to itself inside the copies root passes");
        Check(RevitSaveCopyGuard.CheckWritten(Target, Repo, Target.ToLowerInvariant(), Target, linksNone) is null, "post-save: drive paths compare case-insensitively");
        Check(RevitSaveCopyGuard.CheckWritten(Target, null, Target, Target, linksNone) is { } noRepo && noRepo.Contains(Target, StringComparison.Ordinal), "post-save: without a repository the check fails naming the copy");
        Check(RevitSaveCopyGuard.CheckWritten(Target, Repo, null, null, linksNone) is { Length: > 0 }, "post-save: a path that cannot be resolved fails");
        Check(RevitSaveCopyGuard.CheckWritten(Target, Repo, @"C:\Repo\.bim\BIM-Agentic-Test-copy-1.rvt", @"C:\Repo\.bim\BIM-Agentic-Test-copy-1.rvt", linksNone) is { } moved && moved.Contains(@"C:\Repo\.bim\BIM-Agentic-Test-copy-1.rvt", StringComparison.Ordinal),
            "post-save: a full path that differs from the request fails naming where the file actually is");
        Check(RevitSaveCopyGuard.CheckWritten(Target, Repo, Target, @"D:\Production\Tower-copy.rvt", linksNone) is { } escaped && escaped.Contains(@"D:\Production\Tower-copy.rvt", StringComparison.Ordinal),
            "post-save: a final link target outside the copies root fails naming it");
        var linkedNow = new RevitFileSystemProbe(path => path.EndsWith("run-1", StringComparison.Ordinal), _ => null);
        Check(RevitSaveCopyGuard.CheckWritten(Target, Repo, Target, Target, linkedNow) is { } swapped && swapped.Contains("link", StringComparison.Ordinal) && swapped.Contains(Target, StringComparison.Ordinal),
            "post-save: a folder swapped for a junction after the check fails (TOCTOU) naming the copy");
        Check(RevitSaveCopyGuard.CheckWritten(Target, Repo, Target, Target, new RevitFileSystemProbe(_ => false, _ => 2)) is { Length: > 0 }, "post-save: a hard-linked copy fails");
        var warning = RevitSaveCopyRules.WrittenElsewhereDetail("the copy resolves to X", @"C:\Repo\.bim\native-runs\run-1\BIM-Agentic-Test-copy-1.rvt");
        Check(warning.Contains("X", StringComparison.Ordinal) && warning.Contains("document.path", StringComparison.Ordinal) && warning.Contains(@"BIM-Agentic-Test-copy-1.rvt", StringComparison.Ordinal),
            "post-save failure detail names the problem and the path the open document now points to");

        RevitSaveCopyRefusal? Guarded(string target, string source = FixturePath) =>
            RevitSaveCopyGuard.Check(target, source, Repo, linksNone, Files(directory: path => !path.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)));
        foreach (var name in new[] { "BIM-Agentic-Test.0001.rvt", "BIM-Agentic-Test-copy.0042.RVT" })
            Check(Guarded(Root + @"\run-1\" + name) is { Reason: RevitSaveCopyGuard.NameInvalid }, "a Revit backup name " + name + " is name_invalid");
        Check(Guarded(Root + @"\run-1\BIM-Agentic-Test-copy.001.rvt") is null && Guarded(Root + @"\run-1\BIM-Agentic-Test-2026.rvt") is null, "names that are not the backup .NNNN.rvt pattern stay allowed");
        Check(Guarded(@"\\server\share\Repo\.bim\native-runs\BIM-Agentic-Test-x.rvt") is { Reason: RevitSaveCopyGuard.OutsideCopiesRoot }, "a UNC target with a drive repository is outside_copies_root");
        foreach (var (label, path) in new[]
        {
            ("a root segment with a trailing dot", @"C:\Repo\.bim\native-runs.\BIM-Agentic-Test-x.rvt"),
            ("a root segment with a trailing space", @"C:\Repo\.bim\native-runs \BIM-Agentic-Test-x.rvt"),
            ("a '.. ' segment (Windows reads it as ..)", @"C:\Repo\.bim\native-runs\run-1\.. \BIM-Agentic-Test-x.rvt"),
            ("a '...' segment", @"C:\Repo\.bim\native-runs\...\BIM-Agentic-Test-x.rvt"),
        })
        {
            Check(Guarded(path) is { Reason: RevitSaveCopyGuard.PathInvalid }, "a target with " + label + " is path_invalid");
            Check(Refusal(Envelope(Args(path)).ToJsonString()) is { Code: RevitCommandCodes.ArgsInvalid, Field: "args.path" }, "the binder refuses a target with " + label);
        }
        Check(Guarded(FixturePath, @"C:\REPO\.BIM\NATIVE-RUNS\RUN-1\BIM-Agentic-Test.rvt") is { Reason: RevitSaveCopyGuard.SameAsSource }, "a source that differs only in folder case is the same file (same_as_source)");
        Check(Guarded(@"C:\Repo/.bim\native-runs/run-1\BIM-Agentic-Test-mixed.rvt") is null, "mixed separators inside the copies root are allowed");
        Check(Guarded(@"C:\Repo/.bim/exports\BIM-Agentic-Test-mixed.rvt") is { Reason: RevitSaveCopyGuard.OutsideCopiesRoot }, "mixed separators outside the copies root are outside_copies_root");

        var description = RevitCommandCatalogue.Find("document.save_copy")?.Description ?? "";
        Check(description.Contains("retry", StringComparison.Ordinal) && description.Contains("target_exists", StringComparison.Ordinal) && description.Contains("not replayed", StringComparison.Ordinal),
            "document.save_copy states that a retry after success is refused target_exists, not replayed");
        var batch = RevitCommandCatalogue.Find("command.batch");
        Check(batch is not null && batch.Description.Contains("document.save_copy", StringComparison.Ordinal) && batch.ArgsSchemaJson.Contains("document.save_copy", StringComparison.Ordinal),
            "command.batch description and step schema name document.save_copy among the excluded commands");
    }
}
