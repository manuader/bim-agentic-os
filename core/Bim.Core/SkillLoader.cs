using System.Text;
namespace Bim.Core;

// Loads every skills/<id>/skill.json below a root directory. Rules:
// - Directory names must be skill ids and equal the manifest id. Any other directory, hidden ones such as .git
//   included, is refused as id_invalid; plain files directly under the root (hidden or not) and other files inside a
//   skill directory are ignored.
// - Symbolic links, junctions and other reparse points are never followed: a linked entry at the root (directory,
//   plain file or dangling link) or a linked skill.json is path_unsafe, as is any path that resolves outside the root.
// - skill.json must be a non-empty regular file. Special files (FIFO, device, socket) report size 0 and opening one can
//   block, so a zero-length entry is refused as not_regular_file without being opened; a handle that is not seekable is
//   refused the same way. At most MaxManifestBytes + 1 bytes are read from one handle, and links are re-checked after
//   the read. One leading UTF-8 byte order mark is stripped; the rest must be strict UTF-8.
// - I/O and permission failures are read_failed.
// Refusals are SkillExceptions whose File is the root-relative path ('/' separated, "." for the root).
public static class SkillLoader
{
    public const string ManifestFileName = "skill.json";
    public const int MaxManifestBytes = SkillParser.MaxInputChars;
    public const int MaxSkills = 1024;

    public static SkillCatalog LoadDirectory(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        if (!rootInfo.Exists) throw new SkillException(SkillCodes.RootMissing, null, "skills root directory not found", ".");
        var boundary = Path.TrimEndingDirectorySeparator(rootInfo.FullName) + Path.DirectorySeparatorChar;
        FileSystemInfo[] entries;
        try { entries = rootInfo.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new SkillException(SkillCodes.ReadFailed, null, error.Message, "."); }
        var skills = new List<SkillManifest>();
        foreach (var entry in entries)
        {
            var at = entry.Name;
            try
            {
                if (IsLink(entry)) throw new SkillException(SkillCodes.PathUnsafe, null, "symbolic links and junctions are not followed", at);
                if (entry is not DirectoryInfo directory) continue;
                if (!Inside(directory.FullName, boundary)) throw new SkillException(SkillCodes.PathUnsafe, null, "path leaves the skills root", at);
                if (!SkillParser.IsId(directory.Name)) throw new SkillException(SkillCodes.IdInvalid, null, "skill directory names are skill ids", at);
                if (skills.Count == MaxSkills) throw new SkillException(SkillCodes.TooManyItems, null, "at most 1024 skills", at);
                at = directory.Name + "/" + ManifestFileName;
                var manifest = LoadManifest(directory, boundary, at);
                if (manifest.Id != directory.Name) throw new SkillException(SkillCodes.IdMismatch, "id", "manifest id " + manifest.Id + " differs from directory " + directory.Name, at);
                skills.Add(manifest);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new SkillException(SkillCodes.ReadFailed, null, error.Message, at); }
        }
        return new SkillCatalog(skills);
    }

    static SkillManifest LoadManifest(DirectoryInfo directory, string boundary, string relative)
    {
        var file = new FileInfo(Path.Combine(directory.FullName, ManifestFileName));
        if (IsLink(file)) throw new SkillException(SkillCodes.PathUnsafe, null, "symbolic links are not followed", relative);
        if (!file.Exists) throw new SkillException(SkillCodes.ManifestMissing, null, "skill.json not found", relative);
        if (!Inside(file.FullName, boundary)) throw new SkillException(SkillCodes.PathUnsafe, null, "path leaves the skills root", relative);
        if (file.Length > MaxManifestBytes) throw new SkillException(SkillCodes.InputTooLarge, null, "at most 65536 bytes", relative);
        if (file.Length == 0) throw new SkillException(SkillCodes.NotRegularFile, null, "zero-length entry: an empty file, or a FIFO, device or socket, is not read", relative);
        var bytes = ReadBounded(file.FullName, relative);
        // The entry may have been swapped for a link while it was being read.
        directory.Refresh();
        file.Refresh();
        if (IsLink(directory) || IsLink(file)) throw new SkillException(SkillCodes.PathUnsafe, null, "entry became a link while being read", relative);
        if (bytes.Length > MaxManifestBytes) throw new SkillException(SkillCodes.InputTooLarge, null, "at most 65536 bytes", relative);
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException error) { throw new SkillException(SkillCodes.JsonInvalid, null, "invalid UTF-8: " + error.Message, relative); }
        try { return SkillParser.Parse(text); }
        catch (SkillException error) { throw new SkillException(error.Code, error.Field, error.Detail, relative); }
    }

    // One handle, at most MaxManifestBytes + 1 bytes, so a file that grows after the size check is still bounded.
    static byte[] ReadBounded(string path, string relative)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        if (!stream.CanSeek) throw new SkillException(SkillCodes.NotRegularFile, null, "not a regular file", relative);
        var buffer = new byte[MaxManifestBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer.AsSpan(total));
            if (read == 0) break;
            total += read;
        }
        return buffer.AsSpan(0, total).ToArray();
    }

    static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);

    static bool Inside(string fullPath, string boundary) =>
        Path.GetFullPath(fullPath).StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
