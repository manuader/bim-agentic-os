using System.Text.RegularExpressions;
namespace Bim.Core;

// Owner project mode: which real project document an owner script may open in a NEW child Revit that exposes the
// command pipe. The enabled roots are exactly those of the copies-only write guard (the repository .bim/native-runs
// plus the owner file .bim/revit-enabled-roots.txt, see RevitCommandPolicy), so a document that may be opened is one
// that catalogue writes are allowed on, and nothing else. The link guard runs from the matched root down to the file.
// The CLI verb open-project-check and the add-in project bootstrap both apply this one decision.
public sealed record OwnerProjectRefusal(string Code, string Detail);

public static partial class OwnerProjectGuard
{
    public const string NotRvt = "project_not_rvt";
    public const string BackupFile = "project_backup_file";
    public const string Missing = "project_missing";

    // Null when allowed. Order: path shape, file kind, enabled root, links, existence (so a refused path is never
    // probed beyond the root and a missing file outside the roots reads as not enabled).
    public static OwnerProjectRefusal? Check(string? documentPath, RevitCommandPolicy policy, RevitFileSystemProbe probe)
    {
        if (string.IsNullOrEmpty(documentPath) || !RevitCopiesGuard.IsAbsoluteNormalized(documentPath))
            return new(RevitCommandCodes.DocumentPathInvalid, "absolute path without . or .. segments required");
        var name = documentPath.Split('/', '\\')[^1];
        if (!name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) || name.Length <= 4)
            return new(NotRvt, "a Revit project file (.rvt) is required");
        if (BackupName().IsMatch(name))
            return new(BackupFile, "this is a Revit backup copy (name.0001.rvt); open the main file instead");
        if (RevitCopiesGuard.Check(true, documentPath, policy) is { } refused)
            return new(refused, "the document is not under .bim/native-runs or a root listed in " + RevitCommandPolicy.EnabledRootsFile);
        var root = RevitCopiesGuard.MatchingRoot(documentPath, policy);
        if (root is null) return new(RevitCommandCodes.DocumentNotEnabled, "no enabled root matches");
        if (RevitLinkGuard.Check(documentPath, root, probe) is { } linked)
            return new(linked, "a symlink, junction or hard link between the enabled root and the document");
        if (!File.Exists(documentPath)) return new(Missing, "the document does not exist (or is a folder)");
        return null;
    }

    // A plain static Regex, not [GeneratedRegex]: scripts/check-native-source.ps1 compiles Core with Add-Type, which runs
    // no source generators (CS8795 on the owner's machine, 2026-11-03).
    static readonly Regex BackupNameRegex = new(@"\.[0-9]{4}\.rvt\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static Regex BackupName() => BackupNameRegex;
}
