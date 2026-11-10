using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// P9-08 document.save_as: save the open document (an enabled test copy or an owner-enabled project) as a NEW .rvt
// file inside an enabled root: the repository's .bim/native-runs or a folder listed in .bim/revit-enabled-roots.txt
// (RevitCommandPolicy.EnabledRoots), never anywhere else and never over an existing file. Same envelope
// (protocol_version 1), schema subset, copies-only and link guards as every write; document.save, document.save_copy
// and every earlier command are unchanged. Every refusal that needs no Revit object is decided here, before the pipe
// (RevitCommandService) and again in the add-in on the document it has open (CommandsExecutorSaveAs.cs):
// save_as_refused with a stable reason prefix (RevitSaveAsGuard.Reasons). The target must be a plain local drive path
// (C:\...\Name.rvt) with backslashes only, at most 249 characters (room for Revit's .NNNN backup suffix): UNC, device-namespace, relative, drive-relative and '/'-rooted paths, '/'
// separators, '.'/'..' or empty segments, segments ending with a dot or space, 8.3 short names, wildcard characters,
// ':' streams, reserved device names (CON, NUL, COM1 ...) and names that are not *.rvt (or are Revit backups
// *.NNNN.rvt) are refused. Source and target levels are paired: a test copy under .bim/native-runs (write-copies) is
// saved as a new file only inside .bim/native-runs; only a document already in an owner-enabled folder may be saved
// into an owner-enabled folder (no escalation from a test copy into a real project folder). Revit's Document.SaveAs re-points the open document: afterwards the open document IS the
// new file (same ProjectInformation UniqueId, new path), the previous file keeps its last saved state, and later
// requests must name the returned path. Vendor-free like every RevitCommands*.cs file.

// Absolute path of the new .rvt file.
public sealed record DocumentSaveAsArgs(string Path) : RevitCommandArgs;

// One refusal: Reason is one of RevitSaveAsGuard.Reasons, Field is args.path or document.path.
public sealed record RevitSaveAsRefusal(string Reason, string Field, string Message)
{
    public string Detail => Reason + ": " + Message;
    public RevitCommandException ToException() => new(RevitCommandCodes.SaveAsRefused, Field, Detail);
}

public static class RevitSaveAsGuard
{
    public const string Command = "document.save_as";
    public const string Extension = ".rvt";
    public const int MaxFileNameChars = 128;
    // Windows MAX_PATH less the terminating NUL (259), less 10 characters so Revit's own backup name
    // (<name>.NNNN.rvt, five more characters) and similar suffixes still fit; Revit does not take long paths.
    public const int MaxPathChars = 249;

    // Stable reason prefixes of save_as_refused details.
    public const string PathUnc = "path_unc";
    public const string PathRelative = "path_relative";
    public const string SeparatorInvalid = "separator_invalid";
    public const string PathInvalid = "path_invalid";
    public const string DeviceName = "device_name";
    public const string NameInvalid = "name_invalid";
    public const string OutsideEnabledRoots = "outside_enabled_roots";
    public const string OutsideCopiesRoot = "outside_copies_root";
    public const string SourceNotEnabled = "source_not_enabled";
    public const string SameAsSource = "same_as_source";
    public const string TargetLinked = "target_linked";
    public const string FolderMissing = "folder_missing";
    public const string TargetExists = "target_exists";
    public static IReadOnlyList<string> Reasons { get; } = new[] { PathUnc, PathRelative, SeparatorInvalid, PathInvalid, DeviceName, NameInvalid, OutsideEnabledRoots, OutsideCopiesRoot, SourceNotEnabled, SameAsSource, TargetLinked, FolderMissing, TargetExists };

    // A letter or digit, then letters, combining marks, digits, space and . _ - ( ) + , & ' then .rvt (extension
    // case-insensitive, as the fixture guards compare it).
    static readonly Regex TargetName = new(@"\A[\p{L}\p{N}][\p{L}\p{M}\p{N} ._()+,&'-]*\.[rR][vV][tT]\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Revit's own backups are <name>.NNNN.rvt; a new file never takes that shape.
    static readonly Regex BackupName = new(@"\.[0-9]{4}\.[rR][vV][tT]\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    static readonly Regex ShortName = new(@"~[0-9]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Windows reserves these names in every folder, with any extension ("NUL.rvt", "com1 .rvt").
    static readonly Regex Device = new(@"\A(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    static readonly char[] Wildcards = { '<', '>', '"', '|', '?', '*' };

    // The last path segment.
    public static string FileName(string path) => path.Split('/', '\\').Last();

    // A segment Windows reads as a reserved device: the part before the first dot, without trailing spaces.
    public static bool IsDeviceName(string segment) => Device.IsMatch(segment.Split('.')[0].TrimEnd(' '));

    // A target file name: *.rvt, at most 128 characters, the allowed characters, no "..", the stem not ending with a dot
    // or space, and not a Revit backup name.
    public static bool IsTargetName(string fileName) =>
        fileName.Length <= MaxFileNameChars && TargetName.IsMatch(fileName) && !fileName.Contains("..", StringComparison.Ordinal)
        && !fileName[..^Extension.Length].EndsWith(' ') && !fileName[..^Extension.Length].EndsWith('.') && !BackupName.IsMatch(fileName);

    // Text rules the binder applies too (args_invalid there); null when acceptable.
    public static RevitSaveAsRefusal? PathProblem(string path)
    {
        const string Field = "args.path";
        if (path.Length == 0 || path.Any(char.IsControl)) return new(PathInvalid, Field, "a non-empty path without control characters is required");
        static bool Separator(char c) => c is '/' or '\\';
        if (path.Length >= 2 && Separator(path[0]) && Separator(path[1]))
            return new(PathUnc, Field, "UNC (\\\\server\\share) and device (\\\\?\\, \\\\.\\) paths are not accepted; give a local drive path such as C:\\...\\Name.rvt");
        if (!(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && Separator(path[2])))
            return new(PathRelative, Field, "an absolute local drive path such as C:\\...\\Name.rvt is required (no relative, drive-relative or /-rooted path)");
        if (path.Contains('/')) return new(SeparatorInvalid, Field, "use backslashes only: '/' and mixed separators are not accepted");
        if (path.Length > MaxPathChars) return new(PathInvalid, Field, "the path is longer than " + MaxPathChars.ToString(CultureInfo.InvariantCulture) + " characters");
        if (!RevitCopiesGuard.IsAbsoluteNormalized(path)) return new(PathInvalid, Field, "a path without . or .. segments and without ':' streams is required");
        var segments = path[3..].Split('\\');
        if (segments.Any(segment => segment.Length == 0)) return new(PathInvalid, Field, "empty folder or file names (doubled or trailing backslashes) are not accepted");
        // Windows drops trailing dots and spaces from a segment (".. " is "..", "Cabana." is "Cabana").
        if (segments.Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))) return new(PathInvalid, Field, "a folder or file name ending with a dot or space is not accepted (Windows would read another path)");
        if (segments.Any(segment => ShortName.IsMatch(segment))) return new(PathInvalid, Field, "8.3 short names (with ~ and a digit) are not accepted; give the long path");
        if (segments.Any(segment => segment.IndexOfAny(Wildcards) >= 0)) return new(PathInvalid, Field, "the characters < > \" | ? * are not accepted in a path");
        if (segments.Any(IsDeviceName)) return new(DeviceName, Field, "reserved Windows device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9) are not accepted as folder or file names");
        if (!IsTargetName(segments[^1])) return new(NameInvalid, Field, "the file name must end in " + Extension + " and start with a letter or digit, use only letters, digits, spaces and . _ - ( ) + , & ' (no '..', not a Revit backup name *.NNNN.rvt), at most " + MaxFileNameChars.ToString(CultureInfo.InvariantCulture) + " characters");
        return null;
    }

    // The copies roots of a policy: <repository>/.bim/native-runs when the repository is known, and any enabled root
    // whose last two segments are .bim and native-runs (the repository default root, even without RepositoryDirectory).
    public static IReadOnlyList<string> CopiesRoots(RevitCommandPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var roots = policy.EnabledRoots.Where(IsCopiesRootSpelling).ToList();
        if (RevitSaveCopyGuard.CopiesRoot(policy.RepositoryDirectory) is { } repositoryCopies) roots.Add(repositoryCopies);
        return roots;
    }

    static bool IsCopiesRootSpelling(string root)
    {
        var segments = root.Split('/', '\').Where(segment => segment.Length > 0).ToArray();
        return segments.Length >= 2 && string.Equals(segments[^2], ".bim", StringComparison.OrdinalIgnoreCase) && string.Equals(segments[^1], "native-runs", StringComparison.OrdinalIgnoreCase);
    }

    // Null when the open document at sourcePath may be saved as target. In order: target text and name; target inside
    // an enabled root (policy.EnabledRoots: .bim/native-runs and the owner's .bim/revit-enabled-roots.txt); the source
    // itself inside an enabled root (defence in depth: the client and add-in write admission already refuse a source
    // outside the roots with document_not_enabled; this keeps the guard sound when called alone); a test-copy source
    // (inside a copies root) only into a copies root (paired levels: write-copies never reaches an owner folder);
    // target different from the source; no symlink, junction or hard link from the
    // matched root down to the target; the target folder exists; nothing exists at the target (no overwrite).
    public static RevitSaveAsRefusal? Check(string target, string sourcePath, RevitCommandPolicy policy, RevitFileSystemProbe links, RevitSaveCopyProbe files)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (PathProblem(target) is { } problem) return problem;
        var root = RevitCopiesGuard.MatchingRoot(target, policy);
        if (root is null) return new(OutsideEnabledRoots, "args.path", "the new file must be inside .bim/native-runs or a folder listed in " + RevitCommandPolicy.EnabledRootsFile);
        if (!RevitCopiesGuard.IsAbsoluteNormalized(sourcePath) || RevitCopiesGuard.MatchingRoot(sourcePath, policy) is null)
            return new(SourceNotEnabled, "document.path", "only a document inside .bim/native-runs or a folder listed in " + RevitCommandPolicy.EnabledRootsFile + " is saved as a new file");
        var copies = CopiesRoots(policy);
        if (copies.Any(copiesRoot => RevitCopiesGuard.IsUnder(sourcePath, copiesRoot)) && !copies.Any(copiesRoot => RevitCopiesGuard.IsUnder(target, copiesRoot)))
            return new(OutsideCopiesRoot, "args.path", "a test copy under .bim/native-runs is saved as a new file only inside .bim/native-runs; owner-enabled folders take only documents already in an owner-enabled folder");
        if (RevitCopiesGuard.IsUnder(target, sourcePath) && RevitCopiesGuard.IsUnder(sourcePath, target)) return new(SameAsSource, "args.path", "the new file must differ from the open document's own path (use document.save)");
        if (RevitLinkGuard.Check(target, root, links) is not null) return new(TargetLinked, "args.path", "a symlink, junction or hard link between " + root + " and the new file");
        var folder = target[..target.LastIndexOf('\\')];
        if (folder.Length == 2) folder += "\\";
        if (!files.DirectoryExists(folder)) return new(FolderMissing, "args.path", "the folder " + folder + " does not exist");
        if (files.FileExists(target) || files.DirectoryExists(target)) return new(TargetExists, "args.path", target + " already exists; save as never overwrites, pick a new name");
        return null;
    }

    // After Document.SaveAs (a folder could have been swapped for a junction after Check): null when the written file
    // still lies inside an enabled root under its requested spelling. fullPath is Path.GetFullPath(target), finalPath
    // the target with every link on its way resolved (FinalPath) and finalRoot the matched root resolved the same way
    // (a link above the root is the owner's concern), null when they could not be resolved; the link guard is walked
    // again from the root down. Otherwise the problem, naming where the file actually is. This DETECTS a file written
    // outside the roots after the fact; it cannot prevent the write (Revit writes before the check can run).
    public static string? CheckWritten(string target, RevitCommandPolicy policy, string? fullPath, string? finalPath, string? finalRoot, RevitFileSystemProbe links)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var root = RevitCopiesGuard.MatchingRoot(target, policy);
        if (root is null) return "the new file " + target + " is not inside an enabled root";
        if (fullPath is null || finalPath is null || finalRoot is null) return "the new file " + target + " or its enabled root " + root + " could not be resolved on disk";
        if (!SamePath(fullPath, target)) return "the new file resolves to " + fullPath + ", not " + target;
        if (!RevitCopiesGuard.IsUnder(finalPath, finalRoot)) return "the new file " + target + " was written at " + finalPath + ", outside the enabled root " + root + " (" + finalRoot + ")";
        if (RevitLinkGuard.Check(target, root, links) is not null) return "a symlink, junction or hard link now lies between " + root + " and the new file " + target + " (it resolves to " + finalPath + ")";
        return null;
    }

    // The real post-save check the executor runs: CheckWritten on this machine's file system.
    public static string? WrittenProblem(string target, RevitCommandPolicy policy, RevitFileSystemProbe links)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var root = RevitCopiesGuard.MatchingRoot(target, policy);
        string? fullPath;
        try { fullPath = Path.GetFullPath(target); }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException) { fullPath = null; }
        return CheckWritten(target, policy, fullPath, FinalPath(target), root is null ? null : FinalPath(root), links);
    }

    // The path with every symlink or junction on its way resolved, walking from the volume root down: each existing
    // component that is a link is replaced by its final target before the next segment is appended (a link to a link
    // is followed to the end). Path.GetFullPath resolves none, and FileSystemInfo.ResolveLinkTarget only the last
    // component, so a junction in a parent folder would otherwise go unseen. Hard links are not paths (the link guard
    // counts them). Null when the path cannot be resolved.
    public static string? FinalPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var volume = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(volume)) return null;
            var current = volume;
            foreach (var segment in full[volume.Length..].Split('/', '\\').Where(part => part.Length > 0))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved) current = Path.GetFullPath(resolved.FullName);
            }
            return current;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    static bool SamePath(string a, string b) => RevitCopiesGuard.IsUnder(a, b) && RevitCopiesGuard.IsUnder(b, a);
}

public static class RevitSaveAsRules
{
    // The document states SaveAs is refused for, after the admission checks (which already refuse read-only, family
    // and workshared documents; repeated because the executor saves what is open).
    public static void CheckSavable(SaveCopyFacts facts, string envelopePath)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.PathName is not { Length: > 0 } path) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "the document has never been saved (use Save As in Revit for the first save)");
        if (!(RevitCopiesGuard.IsUnder(path, envelopePath) && RevitCopiesGuard.IsUnder(envelopePath, path))) throw new RevitCommandException(RevitCommandCodes.DocumentConflict, "document.path", "the open document is saved at a different path than the request names");
        if (facts.IsModelInCloud) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "cloud models are not saved by document.save_as");
        if (facts.IsWorkshared) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "workshared documents are not saved by document.save_as");
        if (facts.IsFamilyDocument) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "family documents are not saved by document.save_as");
        if (facts.IsReadOnly) throw new RevitCommandException(RevitCommandCodes.DocumentNotEnabled, "document.path", "the document is read-only");
    }

    // Null when Revit wrote a non-empty file at target, the open document now is that file with no unsaved changes,
    // a SHA-256 was read and the previous file was not rewritten (same existence, size and last-write time).
    public static string? Readback(string target, ObservedSaveCopy observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!observed.Copy.Exists || observed.Copy.Bytes <= 0) return "the new file " + target + " is missing or empty";
        if (observed.CopySha256 is not { Length: 64 }) return "the new file's SHA-256 could not be read";
        if (!(RevitCopiesGuard.IsUnder(observed.DocumentPath, target) && RevitCopiesGuard.IsUnder(target, observed.DocumentPath))) return "the open document is at " + observed.DocumentPath + ", expected the new file " + target;
        if (observed.IsModified) return "Revit still reports unsaved changes";
        if (observed.SourceBefore != observed.SourceAfter) return "the previous file changed during the save as";
        return null;
    }

    // {save_as: {path, bytes, sha256, previous_path, previous_unchanged, had_unsaved_changes, document_path, is_modified}}
    public static string Json(string previousPath, ObservedSaveCopy observed, bool hadUnsavedChanges)
    {
        ArgumentNullException.ThrowIfNull(observed);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("save_as");
            writer.WriteString("path", observed.DocumentPath);
            writer.WriteNumber("bytes", observed.Copy.Bytes);
            writer.WriteString("sha256", observed.CopySha256);
            writer.WriteString("previous_path", previousPath);
            writer.WriteBoolean("previous_unchanged", observed.SourceBefore == observed.SourceAfter);
            writer.WriteBoolean("had_unsaved_changes", hadUnsavedChanges);
            writer.WriteString("document_path", observed.DocumentPath);
            writer.WriteBoolean("is_modified", observed.IsModified);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // The failure detail when CheckWritten refuses a file Revit already wrote: the document is re-pointed regardless.
    // The post-save check detects, it cannot undo: the open document now IS that file, so nothing may continue on it.
    public static string WrittenElsewhereDetail(string problem, string documentPath) =>
        "Revit saved the document, but the post-save check found it written outside the enabled roots or through a link: " + problem + "; the open document now points to " + documentPath
        + ". Close it in Revit without sending further commands to it, and do not continue until the owner has checked that file.";

    // The reply warning: later requests must name the new path (Revit re-points the open document on SaveAs).
    public static string DocumentMovedWarning(string target, string previousPath) =>
        "the open document is now " + target + " (Revit Save As re-points it); send later requests with document.path " + target + "; " + previousPath + " keeps its last saved state";
}

public static partial class RevitCommandCatalogue
{
    // Built on first use, after every static field of every part exists (the v1 schema helpers are reused).
    static readonly Lazy<IReadOnlyList<RevitCommandSpec>> SaveAsSpecs = new(BuildSaveAs);

    public static IReadOnlyList<RevitCommandSpec> SaveAs => SaveAsSpecs.Value;

    static IReadOnlyList<RevitCommandSpec> BuildSaveAs()
    {
        var schema = Fill("""{"type":"object","properties":{"path":{"type":"string","description":"Absolute local path of the new .rvt file (C:\\...\\Name.rvt, backslashes only): inside .bim/native-runs or a folder listed in @1@, in an existing folder, not existing yet (long names, no links, no UNC or relative path)","minLength":8,"maxLength":@2@,"pattern":"@0@"}},"required":["path"],"additionalProperties":false}""",
            NoControl, RevitCommandPolicy.EnabledRootsFile, RevitSaveAsGuard.MaxPathChars.ToString(CultureInfo.InvariantCulture));
        return new[]
        {
            new RevitCommandSpec(RevitSaveAsGuard.Command, true, "Save the open document as a new .rvt file (Revit Save As, never overwriting) inside an enabled folder: the repository's .bim/native-runs or a folder the owner listed in " + RevitCommandPolicy.EnabledRootsFile + "; "
                + "a test copy under .bim/native-runs only into .bim/native-runs; refused for a folder outside those roots, an existing file, a link on the way, a UNC, relative or '/'-separated path, '..' or reserved device names, a name that is not *.rvt or is a Revit backup *.NNNN.rvt, a document outside the enabled roots, and workshared, cloud, family, read-only or never-saved documents. "
                + "Reads back the new file's absolute path, size in bytes and sha256 and that the previous file was not rewritten. Revit then keeps the new file open in place of the previous one: later requests must name the returned path. A retry after success is refused target_exists (not replayed); pick a new name.", schema, BindSaveAs),
        };
    }

    static RevitCommandArgs BindSaveAs(JsonElement args)
    {
        var path = args.GetProperty("path").GetString()!;
        if (RevitSaveAsGuard.PathProblem(path) is { } problem) throw new RevitCommandException(RevitCommandCodes.ArgsInvalid, "args.path", problem.Detail);
        return new DocumentSaveAsArgs(path);
    }
}
