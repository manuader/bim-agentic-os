using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// STUB (red commit): signatures only. P1-08 document.save_copy: save the open test copy as a NEW BIM-Agentic-Test*.rvt file under the repository's copies
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
    { _ = ShortName; return null; }

    // Null when the open document at sourcePath may be saved as target. In order: target text and name; a repository;
    // target strictly inside the copies root; the source itself a BIM-Agentic-Test*.rvt inside the copies root (an
    // owner-enabled real project is never copied by this command); target different from the source; no symlink,
    // junction or hard link from the copies root down to the target; the target folder exists; nothing exists at the
    // target (no overwrite).
    public static RevitSaveCopyRefusal? Check(string target, string sourcePath, string? repositoryDirectory, RevitFileSystemProbe links, RevitSaveCopyProbe files)
    { return null; }

    // Lowercase hex SHA-256 of a file, streamed.
    public static string Sha256File(string path)
    { return ""; }
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
    {  }

    // Null when Revit wrote a non-empty copy at target, the open document now is that copy with no unsaved changes,
    // a SHA-256 was read and the source file was not rewritten (same existence, size and last-write time).
    public static string? Readback(string target, ObservedSaveCopy observed)
    { return null; }

    // {copy: {path, bytes, sha256, source_path, source_unchanged, had_unsaved_changes, document_path, is_modified}}
    public static string Json(string sourcePath, ObservedSaveCopy observed, bool hadUnsavedChanges)
    { return "{}"; }

    // The reply warning: later requests must name the copy (Revit re-points the open document on SaveAs).
    public static string DocumentNowCopyWarning(string target, string sourcePath) => "";
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> SaveCopySpecs = new(BuildSaveCopy);

    public static IReadOnlyList<RevitCommandSpec> SaveCopy => SaveCopySpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildSaveCopy()
    { return Array.Empty<RevitCommandSpec>(); }

    static RevitCommandArgs BindSaveCopy(JsonElement args)
    { return new DocumentSaveCopyArgs(args.GetProperty("path").GetString()!); }
}
