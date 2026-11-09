using System.Globalization;
using System.Text;
namespace Bim.Core;

// P5-10 lock-checked jurisdiction sidecars. JurisdictionRegistry.Load (unlocked) reads every skills/<id>/jurisdiction.json
// from disk and is unchanged. The locked entry points serve only the packs a skills.lock.json pins and parse each
// jurisdiction.json from the very bytes that were hashed and verified against the lock (SkillPackSnapshot), never from a
// second disk read, so there is no window between the lock check and the sidecar read:
// - LoadLocked(skillsRoot, lock) checks the skills directory with SkillLoader (skills_invalid, as Load), snapshots every
//   pinned pack once (SkillPackHash.Snapshot) and hands the snapshots to FromSnapshots.
// - FromSnapshots(lock, snapshots) copies each pinned pack's snapshot bytes, recomputes the bim-skill-pack-sha256-v1 hash
//   over the copies with SkillPackHash's own hash function (a snapshot whose bytes do not match its own ContentSha256,
//   that lists a path twice, holds a path Snapshot could never produce (control character, backslash, empty, "." or
//   ".." segment, leading or trailing '/', deeper than MaxDepth), exceeds Snapshot's MaxFiles or MaxBytes bounds, or has
//   a null hash, file list, file, path or byte array, is skill_lock_drift; the path rules keep the hash stream
//   injective, so no other split of the same bytes into files can reproduce a pinned hash), verifies the copies with SkillLocking.Verify (version or content differing from the lock:
//   skill_lock_drift; pinned pack without a snapshot: skill_lock_missing; manifest refused: skills_invalid) and parses the
//   manifests and sidecars from those copies. Later changes to the caller's arrays or to the disk change nothing loaded.
//   Snapshots of unpinned packs are ignored.
// The catalog holds the pinned packs only; sidecars of unpinned packs are never read. A sidecar of a pinned pack gets the
// same checks and refusal texts as the unlocked reader (empty: not_regular_file; a non-empty directory named
// jurisdiction.json: not_regular_file; size, UTF-8, strict parse, pack mismatch). Links anywhere in a pinned pack are
// refused by the snapshot (path_unsafe). An empty directory named jurisdiction.json is not content (the hash ignores empty
// directories) and counts as no sidecar under a lock, while unlocked Load refuses it. A top-level file or directory whose
// name equals jurisdiction.json ignoring case but not exactly (Jurisdiction.json) is path_unsafe under the lock on every
// OS (unlocked Load reads it only on a case-insensitive file system). The refusal is the first lock issue in (pack id,
// code, detail) order, with the number of further issues appended to its detail. Subjects: lock and snapshot refusals
// name the pack id (a snapshot read failure appends the offending file to the detail), where unlocked Load would name
// <id>/jurisdiction.json for a link or read failure of the sidecar; sidecar content refusals name <id>/jurisdiction.json
// in both modes, and the case refusal names <id>/<entry as cased>.
public sealed partial class JurisdictionRegistry
{
    public static JurisdictionLoad LoadLocked(string skillsRoot, string lockPath)
    {
        ArgumentNullException.ThrowIfNull(skillsRoot);
        ArgumentNullException.ThrowIfNull(lockPath);
        SkillLock skillLock;
        try { skillLock = SkillLockFile.Read(lockPath); }
        catch (SkillLockException error) { return LockRefused(new[] { error.ToIssue() }); }
        return LoadLocked(skillsRoot, skillLock);
    }

    public static JurisdictionLoad LoadLocked(string skillsRoot, SkillLock skillLock)
    {
        ArgumentNullException.ThrowIfNull(skillsRoot);
        ArgumentNullException.ThrowIfNull(skillLock);
        try { SkillLoader.LoadDirectory(skillsRoot); }
        catch (SkillException error) { return new JurisdictionLoad(null, new JurisdictionIssue(JurisdictionCodes.SkillsInvalid, error.File ?? ".", error.Code + (error.Detail is null ? "" : ": " + error.Detail))); }
        var snapshots = new List<SkillPackSnapshot>();
        var issues = new List<SkillLockIssue>();
        foreach (var entry in skillLock.Packs)
        {
            try { snapshots.Add(SkillPackHash.Snapshot(skillsRoot, entry.Id)); }
            catch (SkillLockException error) { issues.Add(new SkillLockIssue(error.Code, entry.Id, error.Detail + (error.Subject == entry.Id ? "" : " (" + error.Subject + ")"))); }
        }
        if (issues.Count == 0) return FromSnapshots(skillLock, snapshots);
        // Report the other pinned packs' drift too; a pack whose snapshot failed already has its issue.
        var unread = new HashSet<string>(issues.Select(issue => issue.Subject), StringComparer.Ordinal);
        issues.AddRange(SkillLocking.Verify(skillLock, snapshots).Issues.Where(issue => !unread.Contains(issue.Subject)));
        return LockRefused(issues);
    }

    public static JurisdictionLoad FromSnapshots(SkillLock skillLock, IEnumerable<SkillPackSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(skillLock);
        ArgumentNullException.ThrowIfNull(snapshots);
        // Last snapshot per id wins, as in SkillLocking.Verify.
        var byId = new Dictionary<string, SkillPackSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Id is not null) byId[snapshot.Id] = snapshot;
        }
        var issues = new List<SkillLockIssue>();
        var sealedSnapshots = new Dictionary<string, SkillPackSnapshot>(StringComparer.Ordinal);
        var refused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in skillLock.Packs)
        {
            if (!byId.TryGetValue(entry.Id, out var snapshot)) continue;
            var (copy, issue) = Seal(snapshot);
            if (issue is not null) { issues.Add(issue); refused.Add(entry.Id); }
            else sealedSnapshots[entry.Id] = copy!;
        }
        var verified = SkillLocking.Verify(skillLock, sealedSnapshots.Values);
        issues.AddRange(verified.Issues.Where(issue => !refused.Contains(issue.Subject)));
        if (issues.Count > 0 || verified.Catalog is not { } catalog) return LockRefused(issues);
        var claims = new List<JurisdictionClaim>();
        foreach (var skill in catalog.Skills)
        {
            var files = sealedSnapshots[skill.Id].Files;
            var relative = skill.Id + "/" + JurisdictionSidecar.FileName;
            foreach (var file in files)
            {
                var top = file.Path.Split('/')[0];
                if (string.Equals(top, JurisdictionSidecar.FileName, StringComparison.OrdinalIgnoreCase) && !string.Equals(top, JurisdictionSidecar.FileName, StringComparison.Ordinal))
                    return new JurisdictionLoad(null, new JurisdictionIssue(JurisdictionCodes.PathUnsafe, skill.Id + "/" + top, "a differently cased jurisdiction.json is refused under the lock; only a case-insensitive file system would read it"));
            }
            if (files.Any(file => file.Path.StartsWith(JurisdictionSidecar.FileName + "/", StringComparison.Ordinal)))
                return new JurisdictionLoad(null, new JurisdictionIssue(JurisdictionCodes.NotRegularFile, relative, "a directory is not a sidecar"));
            var sidecar = files.FirstOrDefault(file => file.Path == JurisdictionSidecar.FileName);
            if (sidecar is null) continue;
            if (sidecar.Bytes.Length == 0) return new JurisdictionLoad(null, new JurisdictionIssue(JurisdictionCodes.NotRegularFile, relative, "zero-length entry is not read"));
            var (claim, refusal) = JurisdictionSidecar.FromBytes(sidecar.Bytes, skill.Id);
            if (refusal is not null) return new JurisdictionLoad(null, refusal);
            claims.Add(claim!);
        }
        return new JurisdictionLoad(new JurisdictionRegistry(catalog, claims), null);
    }

    // A private copy of the snapshot's files in canonical order whose recomputed content hash must equal the snapshot's.
    static (SkillPackSnapshot? Copy, SkillLockIssue? Issue) Seal(SkillPackSnapshot snapshot)
    {
        SkillLockIssue Drift(string detail) => new(SkillLockCodes.SkillLockDrift, snapshot.Id, "snapshot " + detail + "; its bytes are not the hashed content");
        if (snapshot.Files is null) return (null, Drift("has no file list"));
        var copies = new List<SkillPackFile>();
        foreach (var file in snapshot.Files)
        {
            if (file?.Path is null || file.Bytes is null) return (null, Drift("lists a file without a path or bytes"));
            copies.Add(new SkillPackFile(file.Path, (byte[])file.Bytes.Clone()));
        }
        copies = copies.OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
        long total = 0;
        var directories = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < copies.Count; index++)
        {
            var path = copies[index].Path;
            if (SkillPackHash.PathProblem(path) is { } problem) return (null, Drift("holds a path Snapshot never produces (" + problem + ")"));
            if (index > 0 && path == copies[index - 1].Path) return (null, Drift("lists a path twice"));
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1)) directories.Add(path[..slash]);
            total += copies[index].Bytes.Length;
        }
        if (copies.Count + directories.Count > SkillPackHash.MaxFiles) return (null, Drift("holds more than 4096 files and directories"));
        if (total > SkillPackHash.MaxBytes) return (null, Drift("holds more than 64 MiB"));
        string hash;
        try { hash = SkillPackHash.Hash(copies); }
        catch (EncoderFallbackException) { return (null, Drift("holds a path that is not well-formed text")); }
        if (hash != snapshot.ContentSha256) return (null, Drift("content sha256 " + hash + " differs from its declared " + snapshot.ContentSha256));
        return (new SkillPackSnapshot(snapshot.Id, hash, copies), null);
    }

    static JurisdictionLoad LockRefused(IReadOnlyCollection<SkillLockIssue> issues)
    {
        var ordered = issues.OrderBy(issue => issue.Subject, StringComparer.Ordinal).ThenBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Detail, StringComparer.Ordinal).ToArray();
        var first = ordered[0];
        var more = ordered.Length > 1 ? " (+" + (ordered.Length - 1).ToString(CultureInfo.InvariantCulture) + " more lock issue(s))" : "";
        return new JurisdictionLoad(null, new JurisdictionIssue(first.Code, first.Subject, first.Detail + more));
    }
}
