#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Mcp;

namespace Bim.Cli;

// P5-09 `skills-lock pin|verify|update|unpin skills-directory lock.json [pack-id...]` over the P5-08 SkillLocking engine
// (core/Bim.Core/SkillLock.cs); the skills.lock.json format is P5-08's, unchanged.
//   pin skills lock id [id...]  pins the named packs at their current version and content hash and writes the lock to a
//                               NEW file (see WriteNew); an existing lock is lock_exists, a link or reparse point at the
//                               path path_unsafe (Core's link rule), a directory not_regular_file.
//   verify skills lock          read-only: SkillLockTool.Verify, the engine of the MCP tool skills_lock_verify (same
//                               stdout text); exit 1 unless every pinned pack matches.
//   update skills lock [id...]  a rewrite through SkillLocking.Update: no ids re-pins every pack the lock pins (the lock
//                               must exist); ids re-pin exactly those packs and keep every other pin unchanged (an
//                               unreadable or malformed regular lock file is repaired; a FIFO or other non-regular entry
//                               is not_regular_file and left alone).
//   unpin skills lock id [id...] the explicit way to drop pins (SkillLocking.Update with unpin ids): each id must be
//                               pinned (value_invalid), the lock must exist; the other pins are kept unchanged.
// update and unpin read the lock, then write it: there is no compare-and-swap, so a lock written concurrently by another
// process between the two is replaced (last writer wins).
// Bounded input: both paths pass SkillLockTool.PathProblem (skills_lock_path_invalid, detail "skills" or "lock"); at most
// SkillLock.MaxPacks pack ids, each a skill id, else skills_lock_request_invalid (checked before anything is read). A bad
// mode or argument count is {"status":"failed","code":"skills_lock_usage","detail":<Usage>}. Core refusals print
// {"status":"failed","code","subject","detail"}; every refusal exits 1 and writes nothing.
// pin/update/unpin success: {"status":"succeeded","mode","lock","bytes","sha256"(lowercase, of the lock bytes)
// [,"changes":[{"id","before":entry|null,"after":entry|null}] (update and unpin)],"packs":[{"id","version","content_sha256"}]}.
internal static class SkillLockCommand
{
    public const string Usage = "usage: skills-lock pin|verify|update|unpin skills-directory lock.json [pack-id...]";
    public const string UsageCode = "skills_lock_usage";
    public const string LockExists = "lock_exists";

    public static void Run(string[] args)
    {
        var mode = args.Length > 1 ? args[1] : "";
        var ids = args.Length > 4 ? args[4..] : Array.Empty<string>();
        if (args.Length < 4 || mode is not ("pin" or "verify" or "update" or "unpin") || (mode == "verify" && ids.Length != 0) || (mode is "pin" or "unpin" && ids.Length == 0))
        {
            Fail(SkillLockTool.Failure(UsageCode, null, Usage));
            return;
        }
        if (SkillLockTool.PathProblem(args[2], "skills") is { } badSkills) { Fail(badSkills); return; }
        if (SkillLockTool.PathProblem(args[3], "lock") is { } badLock) { Fail(badLock); return; }
        if (mode == "verify")
        {
            var outcome = SkillLockTool.Verify(args[2], args[3]);
            Console.WriteLine(outcome.Text);
            if (!outcome.Succeeded) Environment.ExitCode = 1;
            return;
        }
        if (ids.Length > SkillLock.MaxPacks) { Fail(SkillLockTool.Failure(SkillLockTool.RequestInvalid, null, "at most " + SkillLock.MaxPacks.ToString(CultureInfo.InvariantCulture) + " pack ids")); return; }
        if (!ids.All(SkillParser.IsId)) { Fail(SkillLockTool.Failure(SkillLockTool.RequestInvalid, null, "pack ids must be skill ids")); return; }
        try
        {
            if (mode == "pin") Succeeded("pin", args[3], PinNew(args[2], args[3], ids, null), null);
            else if (mode == "unpin")
            {
                var unpinned = SkillLocking.Update(args[2], args[3], Array.Empty<string>(), ids);
                Succeeded("unpin", args[3], unpinned.Lock, unpinned.Changes);
            }
            else
            {
                var update = SkillLocking.Update(args[2], args[3], ids.Length == 0 ? null : ids);
                Succeeded("update", args[3], update.Lock, update.Changes);
            }
        }
        catch (SkillLockException error) { Fail(SkillLockTool.Failure(error.Code, error.Subject, error.Detail)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Fail(SkillLockTool.Failure(SkillLockCodes.WriteFailed, null, error.GetType().Name)); }
        // Anything else is a defect, never a message passed off as a code.
        catch (Exception error) { Fail(SkillLockTool.Failure(SkillLockTool.InternalError, null, error.GetType().Name)); }
    }

    // Pin to a new lock file. beforeWrite is a test seam, called with the full lock path after the pre-checks and the
    // hashing and before the lock is written (a test creates a competing lock there).
    internal static SkillLock PinNew(string skills, string lockPath, string[] ids, Action<string>? beforeWrite)
    {
        var full = Path.GetFullPath(lockPath);
        var at = Path.GetFileName(full);
        Refuse(full, at);
        var pinned = SkillLocking.Pin(skills, ids);
        beforeWrite?.Invoke(full);
        WriteNew(full, at, SkillLockFile.Serialize(pinned));
        return pinned;
    }

    static void Refuse(string full, string at)
    {
        var target = new FileInfo(full);
        if (IsLink(target)) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "symbolic links are not followed");
        if (Directory.Exists(full)) throw new SkillLockException(SkillLockCodes.NotRegularFile, at, "a directory is not a lock file");
        if (target.Exists) throw new SkillLockException(LockExists, at, "a lock already exists; skills-lock update is the only rewrite");
    }

    // Core's link rule (SkillPackHash.IsLink, SkillLockFile): a link target, or a reparse point (junction, OneDrive
    // placeholder) on an existing entry.
    static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null || (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0);

    // Write-new: the bytes go to a fresh temporary file in the target's directory (FileMode.CreateNew, flushed to disk),
    // which is then moved onto the target with File.Move(overwrite: false); a target that appeared meanwhile is
    // lock_exists and keeps its bytes, and any failure removes the temporary file. Readers see no file or the whole new
    // one: Windows MoveFileEx without replace and, on Unix, .NET's link() + unlink() are atomic and fail on an existing
    // target. Residual: where the file system has no hard links, .NET on Unix falls back to an existence check followed
    // by rename(), so a lock created by another process inside that window can still be replaced (by whole bytes, never
    // torn). The directory is not fsynced (as in SkillLockFile.Write).
    static void WriteNew(string full, string at, byte[] bytes)
    {
        var temp = full + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            try { File.Move(temp, full, false); }
            catch (IOException) when (File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null)
            {
                throw new SkillLockException(LockExists, at, "a lock was created concurrently; it was not overwritten");
            }
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

    static void Succeeded(string mode, string lockPath, SkillLock skillLock, IReadOnlyList<SkillLockChange>? changes)
    {
        var bytes = SkillLockFile.Serialize(skillLock);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "succeeded");
            writer.WriteString("mode", mode);
            writer.WriteString("lock", lockPath);
            writer.WriteNumber("bytes", bytes.LongLength);
            writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            if (changes is not null)
            {
                writer.WriteStartArray("changes");
                foreach (var change in changes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", change.Id);
                    writer.WritePropertyName("before");
                    SkillLockTool.WriteEntry(writer, change.Before);
                    writer.WritePropertyName("after");
                    SkillLockTool.WriteEntry(writer, change.After);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteStartArray("packs");
            foreach (var entry in skillLock.Packs) SkillLockTool.WriteEntry(writer, entry);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        Console.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    static void Fail(string envelope)
    {
        Console.WriteLine(envelope);
        Environment.ExitCode = 1;
    }
}
