using System.Text;
namespace Bim.Core;

// Loads every skills/<id>/skill.json below a root directory. Directory names must be skill ids and equal the manifest
// id. Symbolic links (or other reparse points) for skill directories or manifests are refused, as is any entry whose
// resolved path leaves the root. Manifests are read only after their size is checked. Plain files directly under the
// root and other files inside a skill directory are ignored. Refusals are SkillExceptions whose File is the
// root-relative path ('/' separated).
public static class SkillLoader
{
    public const string ManifestFileName = "skill.json";
    public const int MaxManifestBytes = SkillParser.MaxInputChars;
    public const int MaxSkills = 1024;

    public static SkillCatalog LoadDirectory(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        // RED STUB: load nothing.
        if (root.Length >= 0) return new SkillCatalog(Array.Empty<SkillManifest>());
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        if (!rootInfo.Exists) throw new SkillException(SkillCodes.RootMissing, null, "skills root directory not found", ".");
        var boundary = Path.TrimEndingDirectorySeparator(rootInfo.FullName) + Path.DirectorySeparatorChar;
        var entries = rootInfo.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray();
        var skills = new List<SkillManifest>();
        foreach (var entry in entries)
        {
            var relative = entry.Name;
            if (IsLink(entry)) throw new SkillException(SkillCodes.PathUnsafe, null, "symbolic links are not followed", relative);
            if (entry is not DirectoryInfo directory) continue;
            if (!Inside(directory.FullName, boundary)) throw new SkillException(SkillCodes.PathUnsafe, null, "path leaves the skills root", relative);
            if (!SkillParser.IsId(directory.Name)) throw new SkillException(SkillCodes.IdInvalid, null, "skill directory names are skill ids", relative);
            if (skills.Count == MaxSkills) throw new SkillException(SkillCodes.TooManyItems, null, "at most 1024 skills", relative);
            var manifestRelative = relative + "/" + ManifestFileName;
            var file = new FileInfo(Path.Combine(directory.FullName, ManifestFileName));
            if (IsLink(file)) throw new SkillException(SkillCodes.PathUnsafe, null, "symbolic links are not followed", manifestRelative);
            if (!file.Exists) throw new SkillException(SkillCodes.ManifestMissing, null, "skill.json not found", manifestRelative);
            if (!Inside(file.FullName, boundary)) throw new SkillException(SkillCodes.PathUnsafe, null, "path leaves the skills root", manifestRelative);
            if (file.Length > MaxManifestBytes) throw new SkillException(SkillCodes.InputTooLarge, null, "at most 65536 bytes", manifestRelative);
            var manifest = ParseFile(file.FullName, manifestRelative);
            if (manifest.Id != directory.Name) throw new SkillException(SkillCodes.IdMismatch, "id", "manifest id " + manifest.Id + " differs from directory " + directory.Name, manifestRelative);
            skills.Add(manifest);
        }
        return new SkillCatalog(skills.ToArray());
    }

    static SkillManifest ParseFile(string path, string relative)
    {
        string text;
        try
        {
            var bytes = File.ReadAllBytes(path);
            // The file may have grown between the size check and the read.
            if (bytes.Length > MaxManifestBytes) throw new SkillException(SkillCodes.InputTooLarge, null, "at most 65536 bytes", relative);
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException error) { throw new SkillException(SkillCodes.JsonInvalid, null, "invalid UTF-8: " + error.Message, relative); }
        try { return SkillParser.Parse(text); }
        catch (SkillException error) { throw new SkillException(error.Code, error.Field, error.Detail, relative); }
    }

    static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);

    static bool Inside(string fullPath, string boundary) =>
        Path.GetFullPath(fullPath).StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
