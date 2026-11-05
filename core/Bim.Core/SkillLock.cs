using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Bim.Core;

// P5-08 project skill lock: skills.lock.json, a new standalone format (lock_version 1). The skill manifest, the
// jurisdiction sidecar and BIM-IR are unchanged. A lock pins, per skill pack id, the manifest version and a SHA-256 over
// the pack's content (SkillPackHash); SkillLocking.Load refuses a pack whose version or content differs from the lock
// (skill_lock_drift) or that is gone (skill_lock_missing) and exposes only pinned packs; only an explicit
// SkillLocking.Update rewrites the lock. Canonical bytes (SkillLockFile.Serialize), UTF-8 without BOM, LF line ends:
//   {
//     "lock_version": 1,
//     "content_hash": "bim-skill-pack-sha256-v1",
//     "packs": [
//       {
//         "id": "<skill id>",
//         "version": "<MAJOR.MINOR.PATCH>",
//         "content_sha256": "<64 lowercase hex digits>"
//       }
//     ]
//   }
// with packs in ordinal id order, entries separated by ",\n", "packs": [] when empty, and one trailing newline.
public sealed record SkillLockEntry(string Id, SkillVersion Version, string ContentSha256);

public sealed class SkillLock
{
    public const int CurrentVersion = 1;
    public const string FileName = "skills.lock.json";
    public const int MaxPacks = SkillLoader.MaxSkills;
    public const int MaxInputChars = 262144;

    static readonly Regex Sha256Pattern = new("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    readonly Dictionary<string, SkillLockEntry> byId = new(StringComparer.Ordinal);

    // Hand-built entries get the same checks as parsed ones; order does not matter, ids must be unique.
    public SkillLock(IEnumerable<SkillLockEntry> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);
        foreach (var entry in packs)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (!SkillParser.IsId(entry.Id)) throw new SkillLockException(SkillLockCodes.ValueInvalid, entry.Id ?? "(null)", "a skill id is required");
            if (!IsSha256(entry.ContentSha256)) throw new SkillLockException(SkillLockCodes.ValueInvalid, entry.Id, "content_sha256 must be 64 lowercase hex digits");
            if (!byId.TryAdd(entry.Id, entry)) throw new SkillLockException(SkillLockCodes.EntryDuplicate, entry.Id, "pack pinned more than once");
        }
        if (byId.Count > MaxPacks) throw new SkillLockException(SkillLockCodes.TooManyItems, "$.packs", "at most 1024 packs");
        Packs = byId.Values.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<SkillLockEntry> Packs { get; }

    public SkillLockEntry? Find(string id) => id is not null && byId.TryGetValue(id, out var entry) ? entry : null;

    public static bool IsSha256(string? text) => text is not null && text.Length == 64 && Sha256Pattern.IsMatch(text);
}

// Subject is the pack id, member path or lock-relative file the issue is about.
public sealed record SkillLockIssue(string Code, string Subject, string Detail);

public sealed class SkillLockException : Exception
{
    public SkillLockException(string code, string subject, string detail) : base(code)
    {
        Code = code;
        Subject = subject;
        Detail = detail;
    }

    public string Code { get; }
    public string Subject { get; }
    public string Detail { get; }

    public SkillLockIssue ToIssue() => new(Code, Subject, Detail);
}

public static class SkillLockCodes
{
    // Lock parse refusals (Subject is the member path).
    public const string InputTooLarge = "input_too_large";
    public const string JsonInvalid = "json_invalid";
    public const string VersionUnsupported = "version_unsupported";
    public const string MemberUnknown = "member_unknown";
    public const string MemberDuplicate = "member_duplicate";
    public const string MemberMissing = "member_missing";
    public const string TypeInvalid = "type_invalid";
    public const string ValueInvalid = "value_invalid";
    public const string EntryDuplicate = "entry_duplicate";
    public const string TooManyItems = "too_many_items";
    // File refusals (lock file or pack content).
    public const string PathUnsafe = "path_unsafe";
    public const string NotRegularFile = "not_regular_file";
    public const string ReadFailed = "read_failed";
    public const string WriteFailed = "write_failed";
    public const string LockMissing = "lock_missing";
    // The skills directory itself was refused by SkillLoader; Detail starts with the SkillCodes code.
    public const string SkillsInvalid = "skills_invalid";
    // A pinned pack's version or content differs from the lock (or changed while it was being loaded).
    public const string SkillLockDrift = "skill_lock_drift";
    // A pinned pack is not in the skills directory.
    public const string SkillLockMissing = "skill_lock_missing";
    // Pin was given a resolution that has issues.
    public const string ResolutionFailed = "resolution_failed";
}

// Either Issues is empty and Catalog (the pinned packs only) and Lock are set, or Issues lists every refusal in
// (pack id, code) order and Catalog is null.
public sealed record SkillLockLoad(SkillCatalog? Catalog, SkillLock? Lock, IReadOnlyList<SkillLockIssue> Issues)
{
    public bool Succeeded => Issues.Count == 0 && Catalog is not null;
}

// Before null: newly pinned. After null: unpinned. Both set: the version or content hash changed.
public sealed record SkillLockChange(string Id, SkillLockEntry? Before, SkillLockEntry? After);

public sealed record SkillLockUpdate(SkillLock Lock, IReadOnlyList<SkillLockChange> Changes);

// Content hash of one pack directory skills/<id>/, scheme bim-skill-pack-sha256-v1:
//   SHA-256 of the ASCII header "bim-skill-pack-sha256-v1\n" followed, for every regular file below the pack directory
//   (any depth, hidden and empty files included, empty directories ignored) in ordinal order of its relative path
//   ('/' separated, UTF-16 ordinal comparison), by: the UTF-8 path, "\n", the byte length in invariant decimal, "\n",
//   the raw file bytes (line endings unchanged, no BOM handling), "\n". Lowercase hex.
// Links (symbolic links, junctions) anywhere in the pack, file names with control characters, more than MaxFiles files,
// more than MaxBytes bytes or more than MaxDepth nested directories are refused. Zero-length entries are never opened
// (a FIFO or device reports length 0) and hash as empty files.
public static class SkillPackHash
{
    public const string Scheme = "bim-skill-pack-sha256-v1";
    public const int MaxFiles = 4096;
    public const long MaxBytes = 64L * 1024 * 1024;
    public const int MaxDepth = 32;

    public static string Compute(string skillsRoot, string packId)
    {
        ArgumentNullException.ThrowIfNull(skillsRoot);
        if (!SkillParser.IsId(packId)) throw new SkillLockException(SkillLockCodes.ValueInvalid, packId ?? "(null)", "a skill id is required");
        var rootFull = Path.GetFullPath(skillsRoot);
        var boundary = Path.TrimEndingDirectorySeparator(rootFull) + Path.DirectorySeparatorChar;
        var at = packId;
        try
        {
            var pack = new DirectoryInfo(Path.Combine(rootFull, packId));
            if (IsLink(pack)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "symbolic links and junctions are not followed");
            if (!pack.Exists) throw new SkillLockException(SkillLockCodes.SkillLockMissing, at, "pack directory not found under the skills root");
            if (!Inside(pack.FullName, boundary)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "path leaves the skills root");
            var files = new List<(string Relative, FileInfo File)>();
            var pending = new Stack<(DirectoryInfo Directory, string Prefix, int Depth)>();
            pending.Push((pack, "", 0));
            while (pending.Count > 0)
            {
                var (directory, prefix, depth) = pending.Pop();
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    var relative = prefix + entry.Name;
                    at = packId + "/" + relative;
                    if (IsLink(entry)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "symbolic links and junctions are not followed");
                    if (entry.Name.Any(char.IsControl)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "file names with control characters are not hashed");
                    if (!Inside(entry.FullName, boundary)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "path leaves the skills root");
                    if (entry is DirectoryInfo child)
                    {
                        if (depth + 1 > MaxDepth) throw new SkillLockException(SkillLockCodes.TooManyItems, at, "at most 32 nested directories");
                        pending.Push((child, relative + "/", depth + 1));
                    }
                    else if (entry is FileInfo file)
                    {
                        if (files.Count == MaxFiles) throw new SkillLockException(SkillLockCodes.TooManyItems, at, "at most 4096 files per pack");
                        files.Add((relative, file));
                    }
                }
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.ASCII.GetBytes(Scheme + "\n"));
            long total = 0;
            foreach (var (relative, file) in files.OrderBy(item => item.Relative, StringComparer.Ordinal))
            {
                at = packId + "/" + relative;
                var bytes = ReadFile(file, MaxBytes - total, at);
                total += bytes.Length;
                hash.AppendData(new UTF8Encoding(false, true).GetBytes(relative + "\n" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n"));
                hash.AppendData(bytes);
                hash.AppendData(new[] { (byte)'\n' });
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            throw new SkillLockException(SkillLockCodes.ReadFailed, at, error.Message);
        }
    }

    // One handle, at most Length + 1 bytes, so a file that changes size while being hashed is caught.
    static byte[] ReadFile(FileInfo file, long budget, string at)
    {
        var length = file.Length;
        if (length == 0) return Array.Empty<byte>();
        if (length > budget) throw new SkillLockException(SkillLockCodes.InputTooLarge, at, "a pack holds at most 64 MiB");
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
        if (!stream.CanSeek) throw new SkillLockException(SkillLockCodes.NotRegularFile, at, "not a regular file");
        var buffer = new byte[length + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer.AsSpan(total));
            if (read == 0) break;
            total += read;
        }
        if (total != length) throw new SkillLockException(SkillLockCodes.ReadFailed, at, "file changed size while being hashed");
        file.Refresh();
        if (IsLink(file)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "entry became a link while being read");
        return buffer.AsSpan(0, total).ToArray();
    }

    internal static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);

    static bool Inside(string fullPath, string boundary) =>
        Path.GetFullPath(fullPath).StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

// Canonical serialization, strict parsing and durable file I/O of skills.lock.json.
public static class SkillLockFile
{
    static readonly string[] Members = { "lock_version", "content_hash", "packs" };
    static readonly string[] EntryMembers = { "id", "version", "content_sha256" };
    static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Serialize(SkillLock skillLock)
    {
        ArgumentNullException.ThrowIfNull(skillLock);
        var text = new StringBuilder();
        text.Append("{\n  \"lock_version\": ").Append(SkillLock.CurrentVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        text.Append("  \"content_hash\": \"").Append(SkillPackHash.Scheme).Append("\",\n");
        text.Append("  \"packs\": [");
        for (var index = 0; index < skillLock.Packs.Count; index++)
        {
            var entry = skillLock.Packs[index];
            text.Append(index == 0 ? "\n" : ",\n");
            text.Append("    {\n");
            text.Append("      \"id\": \"").Append(entry.Id).Append("\",\n");
            text.Append("      \"version\": \"").Append(entry.Version.ToString()).Append("\",\n");
            text.Append("      \"content_sha256\": \"").Append(entry.ContentSha256).Append("\"\n");
            text.Append("    }");
        }
        text.Append(skillLock.Packs.Count == 0 ? "]\n" : "\n  ]\n");
        text.Append("}\n");
        return Utf8.GetBytes(text.ToString());
    }

    // Strict: an object with exactly lock_version (the integer token 1), content_hash (the scheme name) and packs (an
    // array of closed {id, version, content_sha256} objects in strictly ascending ordinal id order). Whitespace and line
    // endings are free; Serialize gives the canonical bytes back.
    public static SkillLock Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > SkillLock.MaxInputChars) throw new SkillLockException(SkillLockCodes.InputTooLarge, "$", "at most 262144 characters");
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 }); }
        catch (JsonException error) { throw new SkillLockException(SkillLockCodes.JsonInvalid, "$", error.Message); }
        using (document)
        {
            var root = document.RootElement;
            var seen = Closed(root, "$", Members);
            var version = Required(seen, "$", "lock_version");
            if (version.ValueKind != JsonValueKind.Number) throw new SkillLockException(SkillLockCodes.TypeInvalid, "$.lock_version", "an integer is required");
            if (version.GetRawText() != "1") throw new SkillLockException(SkillLockCodes.VersionUnsupported, "$.lock_version", "only lock_version 1 is supported");
            if (TextOf(Required(seen, "$", "content_hash"), "$.content_hash") != SkillPackHash.Scheme)
                throw new SkillLockException(SkillLockCodes.ValueInvalid, "$.content_hash", "only " + SkillPackHash.Scheme + " is supported");
            var packs = Required(seen, "$", "packs");
            if (packs.ValueKind != JsonValueKind.Array) throw new SkillLockException(SkillLockCodes.TypeInvalid, "$.packs", "an array is required");
            if (packs.GetArrayLength() > SkillLock.MaxPacks) throw new SkillLockException(SkillLockCodes.TooManyItems, "$.packs", "at most 1024 packs");
            var entries = new List<SkillLockEntry>();
            var index = 0;
            foreach (var item in packs.EnumerateArray())
            {
                var at = "$.packs[" + (index++).ToString(CultureInfo.InvariantCulture) + "]";
                var members = Closed(item, at, EntryMembers);
                var id = TextOf(Required(members, at, "id"), at + ".id");
                if (!SkillParser.IsId(id)) throw new SkillLockException(SkillLockCodes.ValueInvalid, at + ".id", "a skill id is required");
                var versionText = TextOf(Required(members, at, "version"), at + ".version");
                if (!SkillParser.TryParseVersion(versionText, out var packVersion)) throw new SkillLockException(SkillLockCodes.ValueInvalid, at + ".version", "MAJOR.MINOR.PATCH without leading zeros");
                var sha = TextOf(Required(members, at, "content_sha256"), at + ".content_sha256");
                if (!SkillLock.IsSha256(sha)) throw new SkillLockException(SkillLockCodes.ValueInvalid, at + ".content_sha256", "64 lowercase hex digits");
                if (entries.Count > 0)
                {
                    var order = string.CompareOrdinal(entries[^1].Id, id);
                    if (order == 0) throw new SkillLockException(SkillLockCodes.EntryDuplicate, at + ".id", "pack " + id + " is pinned more than once");
                    if (order > 0) throw new SkillLockException(SkillLockCodes.ValueInvalid, at + ".id", "packs must be in ordinal id order");
                }
                entries.Add(new SkillLockEntry(id, packVersion, sha));
            }
            return new SkillLock(entries);
        }
    }

    // Never through a link; a non-empty regular file; bounded read; strict UTF-8 with one optional byte order mark.
    public static SkillLock Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var at = Path.GetFileName(path);
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (IsLinkEntry(file)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "symbolic links are not followed");
            if (Directory.Exists(file.FullName)) throw new SkillLockException(SkillLockCodes.NotRegularFile, at, "a directory is not a lock file");
            if (!file.Exists) throw new SkillLockException(SkillLockCodes.LockMissing, at, "lock file not found");
            var limit = SkillLock.MaxInputChars + 3L;
            if (file.Length == 0) throw new SkillLockException(SkillLockCodes.NotRegularFile, at, "zero-length entry: an empty file, or a FIFO, device or socket, is not read");
            if (file.Length > limit) throw new SkillLockException(SkillLockCodes.InputTooLarge, at, "lock file too large");
            byte[] bytes;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None))
            {
                if (!stream.CanSeek) throw new SkillLockException(SkillLockCodes.NotRegularFile, at, "not a regular file");
                var buffer = new byte[limit + 1];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = stream.Read(buffer.AsSpan(total));
                    if (read == 0) break;
                    total += read;
                }
                bytes = buffer.AsSpan(0, total).ToArray();
            }
            if (bytes.Length > limit) throw new SkillLockException(SkillLockCodes.InputTooLarge, at, "lock file too large");
            var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            string text;
            try { text = Utf8.GetString(bytes, offset, bytes.Length - offset); }
            catch (DecoderFallbackException error) { throw new SkillLockException(SkillLockCodes.JsonInvalid, at, "invalid UTF-8: " + error.Message); }
            return Parse(text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SkillLockException(SkillLockCodes.ReadFailed, at, error.Message);
        }
    }

    // Durable replace: the canonical bytes go to a fresh temporary file beside the target, flushed to disk
    // (Flush(true)), then moved over the target; a failure leaves the previous lock in place and no temporary file.
    public static void Write(string path, SkillLock skillLock)
    {
        ArgumentNullException.ThrowIfNull(path);
        var bytes = Serialize(skillLock);
        var full = Path.GetFullPath(path);
        var at = Path.GetFileName(full);
        var temp = full + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            var target = new FileInfo(full);
            if (IsLinkEntry(target)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "symbolic links are not followed");
            if (Directory.Exists(full)) throw new SkillLockException(SkillLockCodes.NotRegularFile, at, "a directory is not a lock file");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, full, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SkillLockException(SkillLockCodes.WriteFailed, at, error.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static bool IsLinkEntry(FileInfo file) => SkillPackHash.IsLink(file);

    static Dictionary<string, JsonElement> Closed(JsonElement element, string path, string[] members)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new SkillLockException(SkillLockCodes.TypeInvalid, path, "an object is required");
        var seen = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!members.Contains(property.Name, StringComparer.Ordinal)) throw new SkillLockException(SkillLockCodes.MemberUnknown, path + "." + property.Name, "unknown member");
            if (!seen.TryAdd(property.Name, property.Value)) throw new SkillLockException(SkillLockCodes.MemberDuplicate, path + "." + property.Name, "member appears twice");
        }
        return seen;
    }

    static JsonElement Required(Dictionary<string, JsonElement> seen, string path, string name) =>
        seen.TryGetValue(name, out var value) ? value : throw new SkillLockException(SkillLockCodes.MemberMissing, path + "." + name, "required member");

    static string TextOf(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String) throw new SkillLockException(SkillLockCodes.TypeInvalid, path, "a string is required");
        try { return element.GetString()!; }
        catch (InvalidOperationException error) { throw new SkillLockException(SkillLockCodes.ValueInvalid, path, "not well-formed text: " + error.Message); }
    }
}

// Pin, verify and explicitly update a project's skill lock against a skills directory (skills/<id>/skill.json).
public static class SkillLocking
{
    // Pins the given packs (repeats ignored) at their current manifest version and content hash. Unknown ids are
    // skill_missing (SkillCodes); a directory SkillLoader refuses is skills_invalid. Content is hashed before and after
    // the manifests are loaded, so a pack edited meanwhile is skill_lock_drift instead of a torn pin.
    public static SkillLock Pin(string skillsRoot, IEnumerable<string> packIds)
    {
        ArgumentNullException.ThrowIfNull(skillsRoot);
        ArgumentNullException.ThrowIfNull(packIds);
        var ids = packIds.Select(id => id ?? throw new ArgumentNullException(nameof(packIds))).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var catalog = LoadCatalog(skillsRoot);
        foreach (var id in ids)
            if (catalog.Find(id) is null) throw new SkillLockException(SkillCodes.SkillMissing, id, "pack " + id + " is not in the skills directory");
        var before = ids.ToDictionary(id => id, id => SkillPackHash.Compute(skillsRoot, id), StringComparer.Ordinal);
        catalog = LoadCatalog(skillsRoot);
        var entries = new List<SkillLockEntry>();
        foreach (var id in ids)
        {
            var manifest = catalog.Find(id) ?? throw new SkillLockException(SkillLockCodes.SkillLockMissing, id, "pack disappeared while being pinned");
            var after = SkillPackHash.Compute(skillsRoot, id);
            if (after != before[id]) throw new SkillLockException(SkillLockCodes.SkillLockDrift, id, "pack content changed while being pinned");
            entries.Add(new SkillLockEntry(id, manifest.Version, after));
        }
        return new SkillLock(entries);
    }

    // Pins exactly the skills of a successful resolution; their versions must still be the ones on disk.
    public static SkillLock Pin(string skillsRoot, SkillResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded) throw new SkillLockException(SkillLockCodes.ResolutionFailed, "", "the resolution has " + resolution.Issues.Count.ToString(CultureInfo.InvariantCulture) + " issue(s); fix them before pinning");
        var pinned = Pin(skillsRoot, resolution.Skills.Select(skill => skill.Id));
        foreach (var skill in resolution.Skills)
            if (pinned.Find(skill.Id) is { } entry && entry.Version != skill.Version)
                throw new SkillLockException(SkillLockCodes.SkillLockDrift, skill.Id, "resolved " + skill.Version + " but the skills directory holds " + entry.Version);
        return pinned;
    }

    public static SkillLockLoad Load(string skillsRoot, string lockPath)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        SkillLock skillLock;
        try { skillLock = SkillLockFile.Read(lockPath); }
        catch (SkillLockException error) { return new SkillLockLoad(null, null, new[] { error.ToIssue() }); }
        return Load(skillsRoot, skillLock);
    }

    // Loads the skills directory and returns a catalog of the pinned packs only, after checking each pinned pack is
    // present (skill_lock_missing) with the pinned version and content hash (skill_lock_drift). Every problem is
    // reported; nothing is written. Content is hashed before and after the manifests are read so the catalog always
    // matches the verified bytes.
    public static SkillLockLoad Load(string skillsRoot, SkillLock skillLock)
    {
        ArgumentNullException.ThrowIfNull(skillsRoot);
        ArgumentNullException.ThrowIfNull(skillLock);
        var issues = new List<SkillLockIssue>();
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in skillLock.Packs)
        {
            try { before[entry.Id] = SkillPackHash.Compute(skillsRoot, entry.Id); }
            catch (SkillLockException error) { issues.Add(new SkillLockIssue(error.Code, entry.Id, error.Detail + (error.Subject == entry.Id ? "" : " (" + error.Subject + ")"))); }
        }
        SkillCatalog catalog;
        try { catalog = LoadCatalog(skillsRoot); }
        catch (SkillLockException error) { return new SkillLockLoad(null, skillLock, new[] { error.ToIssue() }); }
        var manifests = new List<SkillManifest>();
        foreach (var entry in skillLock.Packs)
        {
            if (!before.TryGetValue(entry.Id, out var found)) continue;
            var manifest = catalog.Find(entry.Id);
            if (manifest is null) { issues.Add(new SkillLockIssue(SkillLockCodes.SkillLockMissing, entry.Id, "pinned pack is not in the skills directory")); continue; }
            string after;
            try { after = SkillPackHash.Compute(skillsRoot, entry.Id); }
            catch (SkillLockException error) { issues.Add(new SkillLockIssue(error.Code, entry.Id, error.Detail)); continue; }
            if (after != found) { issues.Add(new SkillLockIssue(SkillLockCodes.SkillLockDrift, entry.Id, "pack content changed while being loaded")); continue; }
            if (manifest.Version != entry.Version || found != entry.ContentSha256)
            {
                issues.Add(new SkillLockIssue(SkillLockCodes.SkillLockDrift, entry.Id,
                    "pinned " + entry.Version + " sha256 " + entry.ContentSha256 + ", found " + manifest.Version + " sha256 " + found + "; an explicit lock update is required to accept it"));
                continue;
            }
            manifests.Add(manifest);
        }
        if (issues.Count > 0)
            return new SkillLockLoad(null, skillLock, issues.OrderBy(issue => issue.Subject, StringComparer.Ordinal).ThenBy(issue => issue.Code, StringComparer.Ordinal).ToArray());
        return new SkillLockLoad(new SkillCatalog(manifests), skillLock, Array.Empty<SkillLockIssue>());
    }

    // The explicit update: re-pins packIds (or, when null, the packs the existing lock pins) at their current content
    // and durably rewrites lockPath. Without packIds the lock must exist (lock_missing). Any pin failure throws before
    // the file is touched. The file is rewritten only when the pins change or its bytes are not canonical.
    public static SkillLockUpdate Update(string skillsRoot, string lockPath, IEnumerable<string>? packIds = null)
    {
        ArgumentNullException.ThrowIfNull(skillsRoot);
        ArgumentNullException.ThrowIfNull(lockPath);
        SkillLock? existing = null;
        if (packIds is null) existing = SkillLockFile.Read(lockPath);
        else if (File.Exists(lockPath)) existing = SkillLockFile.Read(lockPath);
        var ids = packIds ?? existing!.Packs.Select(entry => entry.Id);
        SkillLock updated;
        try { updated = Pin(skillsRoot, ids); }
        catch (SkillLockException error) when (error.Code == SkillCodes.SkillMissing && existing?.Find(error.Subject) is not null)
        {
            throw new SkillLockException(SkillLockCodes.SkillLockMissing, error.Subject, "pinned pack is not in the skills directory");
        }
        var changes = new List<SkillLockChange>();
        var all = updated.Packs.Select(entry => entry.Id).Concat(existing?.Packs.Select(entry => entry.Id) ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal);
        foreach (var id in all)
        {
            var before = existing?.Find(id);
            var after = updated.Find(id);
            if (before != after) changes.Add(new SkillLockChange(id, before, after));
        }
        var canonical = SkillLockFile.Serialize(updated);
        var current = File.Exists(lockPath) ? File.ReadAllBytes(lockPath) : null;
        if (changes.Count > 0 || current is null || !current.AsSpan().SequenceEqual(canonical)) SkillLockFile.Write(lockPath, updated);
        return new SkillLockUpdate(updated, changes);
    }

    static SkillCatalog LoadCatalog(string skillsRoot)
    {
        try { return SkillLoader.LoadDirectory(skillsRoot); }
        catch (SkillException error) { throw new SkillLockException(SkillLockCodes.SkillsInvalid, error.File ?? ".", error.Code + (error.Detail is null ? "" : ": " + error.Detail)); }
    }
}
