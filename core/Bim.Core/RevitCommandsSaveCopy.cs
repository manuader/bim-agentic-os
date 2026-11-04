using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// P1-08 document.save_copy: save the open test copy as a NEW BIM-Agentic-Test*.rvt file under the repository's copies
// root <repository>/.bim/native-runs (the root owner sessions place fixture copies in, PermissionPolicy.DefaultTestCopyRoots),
// never anywhere else and never over an existing file. Same envelope (protocol_version 1), schema subset, copies-only
// and link guards as every write; document.save and every earlier command are unchanged. Every refusal that needs no
// Revit object is decided here, before the pipe (RevitCommandService) and again in the add-in on the document it has
// open (CommandsExecutorSaveCopy.cs): save_copy_refused with a stable reason prefix (RevitSaveCopyGuard.Reasons).
// Revit's Document.SaveAs re-points the open document to the new file: after a successful save_copy the open document
// IS the copy (same ProjectInformation UniqueId, new path), the source file keeps its last saved state, and later
// requests must name the returned path. Vendor-free like every RevitCommands*.cs file.

// Absolute path of the new .rvt file.
public sealed record DocumentSaveCopyArgs(string Path) : RevitCommandArgs;

// File-system facts the target check needs, injectable for tests.
public sealed record RevitSaveCopyProbe(Func<string, bool> FileExists, Func<string, bool> DirectoryExists)
{
    public static RevitSaveCopyProbe Real { get; } = new(File.Exists, Directory.Exists);
}

// One refusal: Reason is one of RevitSaveCopyGuard.Reasons, Field is args.path or document.path.
public sealed record RevitSaveCopyRefusal(string Reason, string Field, string Message)
{
    public string Detail => Reason + ": " + Message;
    public RevitCommandException ToException() => new(RevitCommandCodes.SaveCopyRefused, Field, Detail);
}

public static class RevitSaveCopyGuard
{
    public const string Command = "document.save_copy";
    public const string FilePrefix = "BIM-Agentic-Test";
    public const string Extension = ".rvt";
    public const int MaxFileNameChars = 128;
    // Relative to the repository root (RevitCommandPolicy.RepositoryDirectory); the same folder as RevitCommandPolicy.DefaultRelativeRoot.
    public const string CopiesDirectory = RevitCommandPolicy.DefaultRelativeRoot;

    // Stable reason prefixes of save_copy_refused details.
    public const string PathInvalid = "path_invalid";
    public const string NameInvalid = "name_invalid";
    public const string NoRepository = "no_repository";
    public const string OutsideCopiesRoot = "outside_copies_root";
    public const string SourceNotTestCopy = "source_not_test_copy";
    public const string SameAsSource = "same_as_source";
    public const string TargetLinked = "target_linked";
    public const string FolderMissing = "folder_missing";
    public const string TargetExists = "target_exists";
    public static IReadOnlyList<string> Reasons { get; } = new[] { PathInvalid, NameInvalid, NoRepository, OutsideCopiesRoot, SourceNotTestCopy, SameAsSource, TargetLinked, FolderMissing, TargetExists };

    // BIM-Agentic-Test, then optionally letters, digits, '.', '_' or '-', then .rvt (extension case-insensitive, as the
    // fixture guards compare it); the prefix is ordinal like every BIM-Agentic-Test check in the repository.
    static readonly Regex TestCopyName = new(@"\ABIM-Agentic-Test[A-Za-z0-9._-]*\.[rR][vV][tT]\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex ShortName = new(@"~[0-9]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // <repository>/.bim/native-runs, null without a repository.
    public static string? CopiesRoot(string? repositoryDirectory) => repositoryDirectory is null ? null : Path.Combine(repositoryDirectory, ".bim", "native-runs");

    // The last path segment.
    public static string FileName(string path) => path.Split('/', '\\').Last();

    public static bool IsTestCopyName(string fileName) => fileName.Length <= MaxFileNameChars && TestCopyName.IsMatch(fileName) && !fileName.Contains("..", StringComparison.Ordinal);

    // Text rules the binder applies too (args_invalid there); null when acceptable.
    public static RevitSaveCopyRefusal? PathProblem(string path)
    {
        if (!RevitCopiesGuard.IsAbsoluteNormalized(path)) return new(PathInvalid, "args.path", "an absolute path without . or .. segments, device prefixes or ':' streams is required");
        if (path.Split('/', '\\').Any(segment => ShortName.IsMatch(segment))) return new(PathInvalid, "args.path", "8.3 short names (with ~ and a digit) are not accepted; give the long path");
        if (!IsTestCopyName(FileName(path))) return new(NameInvalid, "args.path", "the file name must be " + FilePrefix + "*" + Extension + " (letters, digits, '.', '_' or '-' after the prefix, no '..', at most " + MaxFileNameChars.ToString(CultureInfo.InvariantCulture) + " characters)");
        return null;
    }

    // Null when the open document at sourcePath may be saved as target. In order: target text and name; a repository;
    // target strictly inside the copies root; the source itself a BIM-Agentic-Test*.rvt inside the copies root (an
    // owner-enabled real project is never copied by this command); target different from the source; no symlink,
    // junction or hard link from the copies root down to the target; the target folder exists; nothing exists at the
    // target (no overwrite).
    public static RevitSaveCopyRefusal? Check(string target, string sourcePath, string? repositoryDirectory, RevitFileSystemProbe links, RevitSaveCopyProbe files)
    {
        if (PathProblem(target) is { } problem) return problem;
        var root = CopiesRoot(repositoryDirectory);
        if (root is null) return new(NoRepository, "args.path", "no repository folder (global.json) was found; copies are saved only under its " + CopiesDirectory);
        if (!RevitCopiesGuard.IsUnder(target, root) || RevitCopiesGuard.IsUnder(root, target)) return new(OutsideCopiesRoot, "args.path", "copies are saved only inside the repository's " + CopiesDirectory + " folder (" + root + ")");
        if (!RevitCopiesGuard.IsAbsoluteNormalized(sourcePath) || !RevitCopiesGuard.IsUnder(sourcePath, root) || !IsTestCopyName(FileName(sourcePath)))
            return new(SourceNotTestCopy, "document.path", "only an open " + FilePrefix + "*" + Extension + " test copy inside " + CopiesDirectory + " is saved as a copy; real projects are not");
        if (RevitCopiesGuard.IsUnder(target, sourcePath) && RevitCopiesGuard.IsUnder(sourcePath, target)) return new(SameAsSource, "args.path", "the copy must be a new file, not the open document's own path (use document.save)");
        if (RevitLinkGuard.Check(target, root, links) is not null) return new(TargetLinked, "args.path", "a symlink, junction or hard link between " + root + " and the copy");
        var folder = target[..Math.Max(target.LastIndexOfAny(new[] { '/', '\\' }), 1)];
        if (!files.DirectoryExists(folder)) return new(FolderMissing, "args.path", "the folder " + folder + " does not exist");
        if (files.FileExists(target) || files.DirectoryExists(target)) return new(TargetExists, "args.path", target + " already exists; copies never overwrite, pick a new name");
        return null;
    }

    // Lowercase hex SHA-256 of a file, streamed.
    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

// What the add-in reads from the active Document before SaveAs.
public sealed record SaveCopyFacts(string? PathName, bool IsReadOnly, bool IsWorkshared, bool IsModelInCloud, bool IsFamilyDocument);
// A file as the file system reports it (Exists false: missing).
public sealed record SaveCopyFileState(bool Exists, long Bytes, DateTime LastWriteUtc);
// After Document.SaveAs: the document's PathName and IsModified, the copy (with its SHA-256) and the source file.
public sealed record ObservedSaveCopy(string DocumentPath, bool IsModified, SaveCopyFileState Copy, string? CopySha256, SaveCopyFileState SourceBefore, SaveCopyFileState SourceAfter);

public static class RevitSaveCopyRules
{
    // The document states SaveAs is refused for, after the admission checks (which already refuse read-only, family
    // and workshared documents; repeated because the executor saves what is open).
    public static void CheckCopyable(SaveCopyFacts facts, string envelopePath)
    {
        if (facts.PathName is not { Length: > 0 } path) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "the document has never been saved");
        if (!(RevitCopiesGuard.IsUnder(path, envelopePath) && RevitCopiesGuard.IsUnder(envelopePath, path))) throw new RevitCommandException(RevitCommandCodes.DocumentConflict, "document.path", "the open document is saved at a different path than the request names");
        if (facts.IsModelInCloud) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "cloud models are not copied by document.save_copy");
        if (facts.IsWorkshared) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "workshared documents are not copied by document.save_copy");
        if (facts.IsFamilyDocument) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "family documents are not copied by document.save_copy");
        if (facts.IsReadOnly) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "the document is read-only");
    }

    // Null when Revit wrote a non-empty copy at target, the open document now is that copy with no unsaved changes,
    // a SHA-256 was read and the source file was not rewritten (same existence, size and last-write time).
    public static string? Readback(string target, ObservedSaveCopy observed)
    {
        if (!observed.Copy.Exists || observed.Copy.Bytes <= 0) return "the copy " + target + " is missing or empty";
        if (observed.CopySha256 is not { Length: 64 }) return "the copy's SHA-256 could not be read";
        if (!(RevitCopiesGuard.IsUnder(observed.DocumentPath, target) && RevitCopiesGuard.IsUnder(target, observed.DocumentPath))) return "the open document is at " + observed.DocumentPath + ", expected the copy " + target;
        if (observed.IsModified) return "Revit still reports unsaved changes";
        if (observed.SourceBefore != observed.SourceAfter) return "the source file changed during the copy";
        return null;
    }

    // {copy: {path, bytes, sha256, source_path, source_unchanged, had_unsaved_changes, document_path, is_modified}}
    public static string Json(string sourcePath, ObservedSaveCopy observed, bool hadUnsavedChanges)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("copy");
            writer.WriteString("path", observed.DocumentPath);
            writer.WriteNumber("bytes", observed.Copy.Bytes);
            writer.WriteString("sha256", observed.CopySha256);
            writer.WriteString("source_path", sourcePath);
            writer.WriteBoolean("source_unchanged", observed.SourceBefore == observed.SourceAfter);
            writer.WriteBoolean("had_unsaved_changes", hadUnsavedChanges);
            writer.WriteString("document_path", observed.DocumentPath);
            writer.WriteBoolean("is_modified", observed.IsModified);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The reply warning: later requests must name the copy (Revit re-points the open document on SaveAs).
    public static string DocumentNowCopyWarning(string target, string sourcePath) =>
        "the open document is now the copy " + target + " (Revit Save As re-points it); send later requests with document.path " + target + "; " + sourcePath + " keeps its last saved state";
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> SaveCopySpecs = new(BuildSaveCopy);

    public static IReadOnlyList<RevitCommandSpec> SaveCopy => SaveCopySpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildSaveCopy()
    {
        var schema = Fill("""{"type":"object","properties":{"path":{"type":"string","description":"Absolute path of the new file: inside the repository's @1@ folder, named @2@*.rvt, in an existing folder, not existing yet (long names, no links)","minLength":5,"maxLength":1024,"pattern":"@0@"}},"required":["path"],"additionalProperties":false}""",
            NoControl, RevitSaveCopyGuard.CopiesDirectory, RevitSaveCopyGuard.FilePrefix);
        return new[]
        {
            new RevitCommandSpec(RevitSaveCopyGuard.Command, true, "Save the open " + RevitSaveCopyGuard.FilePrefix + "*.rvt test copy as a new " + RevitSaveCopyGuard.FilePrefix + "*.rvt file under the repository's " + RevitSaveCopyGuard.CopiesDirectory + " folder (Revit Save As, never overwriting); "
                + "refused for another file name, a folder outside " + RevitSaveCopyGuard.CopiesDirectory + ", an existing file, a link on the way, a real project (even an owner-enabled one), and workshared, cloud, family or read-only documents. "
                + "Reads back the copy's absolute path, size in bytes and sha256 and that the source file was not rewritten. Revit then keeps the copy open in place of the source: later requests must name the returned path.", schema, BindSaveCopy),
        };
    }

    static RevitCommandArgs BindSaveCopy(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        if (RevitSaveCopyGuard.PathProblem(path) is { } problem) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.path", problem.Detail);
        return new DocumentSaveCopyArgs(path);
    }
}
