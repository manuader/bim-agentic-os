namespace Bim.Core;

// ADR0007 cloud guard, shared by the IFC and native reconcile paths. Metadata only: enumerates a directory
// and reads attributes, never opens file contents, so no cloud-only placeholder is hydrated.
public sealed record CloudHazardFinding(string Code,string[] Entries);

public static class CloudGuard
{
    public const string ConflictPresent="cloud_conflict_present";
    public const string PlaceholderPresent="cloud_placeholder_present";
    // Windows FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_OPEN and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (cloud-only placeholders).
    // ReparsePoint alone is deliberately not a hazard: OneDrive sets it on hydrated real files with no LinkTarget (HANDOFF section 7).
    public const FileAttributes PlaceholderAttributes=FileAttributes.Offline|(FileAttributes)0x00040000|(FileAttributes)0x00400000;
    // OneDrive keeps both versions of a sync conflict by appending the device name, e.g. MyFile.txt -> MyFile-ComputerName.txt;
    // older or third-party copies use "Name (1).ext" or "Name - Copy.ext". Matching is case-insensitive like NTFS:
    // same extension, and the name continues the stem with '-' or ' '. Sources (retrieved 2026-10-23):
    // https://support.microsoft.com/en-us/onedrive/duplicate-files-in-onedrive ("your computer name added to the filename")
    // https://uwindsor.teamdynamix.com/TDClient/1975/Portal/KB/Article/29832/Known-Issue-OneDrive-duplicate-files ("MyFile-ComputerName.txt")
    // https://melbits.com.au/kb/why-does-onedrive-keep-duplicating-files ("Filename-DESKTOP-AB12.docx", "Budget (1).xlsx", "Report-Copy.docx")
    // https://sharepointmaven.com/how-onedrive-sync-resolves-sync-conflicts/ (keep both: "add the Computer/PC name to the end of the file name")
    public static bool IsConflictCopy(string name,string primary)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(primary);
        if(string.Equals(name,primary,StringComparison.Ordinal))return false;
        if(string.Equals(name,primary,StringComparison.OrdinalIgnoreCase))return true;
        var extension=Path.GetExtension(primary); var stem=Path.GetFileNameWithoutExtension(primary);
        if(extension.Length==0||!name.EndsWith(extension,StringComparison.OrdinalIgnoreCase))return false;
        var rest=name[..^extension.Length];
        return rest.Length>stem.Length&&rest.StartsWith(stem,StringComparison.OrdinalIgnoreCase)&&(rest[stem.Length]=='-'||rest[stem.Length]==' ');
    }
    // Conflict copies of any primary are reported first; then any entry carrying placeholder attributes.
    // The attribute reader is a test seam; by default attributes are read on Windows only (no such attributes elsewhere).
    public static CloudHazardFinding? Scan(string directory,IReadOnlyCollection<string> primaries,Func<string,FileAttributes>? readAttributes=null)
    {
        ArgumentNullException.ThrowIfNull(directory); ArgumentNullException.ThrowIfNull(primaries);
        var entries=new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(entry=>entry.Name,StringComparer.Ordinal).ToArray();
        var names=entries.Select(entry=>entry.Name).ToArray();
        // A case-only variant is a conflict only beside the exact-case primary; alone it is the store file itself (NTFS is case-insensitive).
        var conflicts=names.Where(name=>primaries.Any(primary=>IsConflictCopy(name,primary)&&(!string.Equals(name,primary,StringComparison.OrdinalIgnoreCase)||names.Contains(primary,StringComparer.Ordinal)))).ToArray();
        if(conflicts.Length>0)return new(ConflictPresent,conflicts);
        var placeholders=entries.Where(entry=>((readAttributes is null?(OperatingSystem.IsWindows()?entry.Attributes:default):readAttributes(entry.FullName))&PlaceholderAttributes)!=0).Select(entry=>entry.Name).ToArray();
        return placeholders.Length>0?new(PlaceholderPresent,placeholders):null;
    }
}
