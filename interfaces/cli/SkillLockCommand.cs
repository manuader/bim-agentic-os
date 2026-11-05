#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bim.Core;
using Bim.Mcp;

namespace Bim.Cli;

// P5-09 `skills-lock pin|verify|update skills-directory lock.json [pack-id...]` over the P5-08 SkillLocking engine
// (core/Bim.Core/SkillLock.cs); the skills.lock.json format is P5-08's, unchanged.
//   pin skills lock id [id...]  pins the named packs at their current version and content hash and writes the lock to a
//                               NEW file (temporary file, flushed, then moved without overwrite); an existing lock is
//                               lock_exists, a link at the path path_unsafe, a directory not_regular_file.
//   verify skills lock          read-only: SkillLockTool.Verify, the engine of the MCP tool skills_lock_verify (same
//                               stdout text); exit 1 unless every pinned pack matches.
//   update skills lock [id...]  the only rewrite: SkillLocking.Update re-pins the lock's own packs (no ids; the lock must
//                               exist) or exactly the given ids (an unreadable or malformed lock is repaired).
// Bounded input: both paths pass SkillLockTool.PathProblem (skills_lock_path_invalid, detail "skills" or "lock"); at most
// SkillLock.MaxPacks pack ids, each a skill id, else skills_lock_request_invalid (checked before anything is read). Core
// refusals print {"status":"failed","code","subject","detail"}; every refusal exits 1 and writes nothing.
// pin/update success: {"status":"succeeded","mode","lock","bytes","sha256"(lowercase, of the lock bytes)
// [,"changes":[{"id","before":entry|null,"after":entry|null}] (update only)],"packs":[{"id","version","content_sha256"}]}.
internal static class SkillLockCommand
{
    public const string Usage = "usage: skills-lock pin|verify|update skills-directory lock.json [pack-id...]";
    public const string LockExists = "lock_exists";

    public static void Run(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException(Usage);
        var mode = args[1];
        var ids = args[4..];
        if (mode is not ("pin" or "verify" or "update") || (mode == "verify" && ids.Length != 0) || (mode == "pin" && ids.Length == 0)) throw new ArgumentException(Usage);
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
            if (mode == "pin") Pin(args[2], args[3], ids);
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

    static void Pin(string skills, string lockPath, string[] ids) => Succeeded("pin", lockPath, PinNew(skills, lockPath, ids, null), null);

    internal static SkillLock PinNew(string skills, string lockPath, string[] ids, Action<string>? beforeWrite)
    {
        var full = Path.GetFullPath(lockPath);
        var at = Path.GetFileName(full);
        Refuse(full, at);
        var pinned = SkillLocking.Pin(skills, ids);
        WriteNew(full, at, SkillLockFile.Serialize(pinned));
        return pinned;
    }

    // Pre-checks so nothing is hashed for a path that can never be written; WriteNew re-checks at the move.
    static void Refuse(string full, string at)
    {
        var target = new FileInfo(full);
        if (target.LinkTarget is not null) throw new SkillLockException(SkillLockCodes.PathUnsafe, at, "symbolic links are not followed");
        if (Directory.Exists(full)) throw new SkillLockException(SkillLockCodes.NotRegularFile, at, "a directory is not a lock file");
        if (target.Exists) throw new SkillLockException(LockExists, at, "a lock already exists; skills-lock update is the only rewrite");
    }

    // Never overwrites: the bytes go to a fresh temporary file beside the target (FileMode.CreateNew, flushed to disk),
    // which is then moved onto the target without overwrite, so an existing or concurrently created lock is never
    // replaced and readers never see a torn file. Any failure removes the temporary file.
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
