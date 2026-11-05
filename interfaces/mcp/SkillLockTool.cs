#nullable enable
using System.Text;
using System.Text.Json;
using Bim.Core;

namespace Bim.Mcp;

// P5-09: the read-only verify engine behind the CLI verb `skills-lock verify skills-directory lock.json`
// (interfaces/cli/SkillLockCommand.cs) and the MCP tool skills_lock_verify (McpServer), whose text equals the CLI stdout
// line byte for byte. It runs the P5-08 lock-checked load (Bim.Core SkillLock.cs) and never writes: pinning and the
// explicit update are CLI-only (SkillLockCommand). The lock and the packs are read by Core's own bounded readers (lock:
// no link, regular non-empty file, at most SkillLock.MaxInputChars, strict UTF-8; packs: SkillPackHash limits).
//
// Both paths must be non-blank, without NUL, at most MaxPathChars characters and accepted by Path.GetFullPath, else
// {"status":"failed","code":"skills_lock_path_invalid","detail":"skills"|"lock"}. A lock that cannot be read or parsed
// is {"status":"failed","code":<SkillLockCodes>,"subject":<lock file name or member path>,"detail":..}.
// Otherwise the report (UTF-8, compact):
//   {"status":"succeeded"|"failed"[,"code":..],"mode":"verify","verified":bool,"lock_version":1,
//    "content_hash":"bim-skill-pack-sha256-v1","packs":[{"id","version","content_sha256","state"[,"detail"]}],
//    "issues":[{"code","subject","detail"}]}
// packs: every pinned pack in lock (ordinal id) order with the pinned version and hash; state "ok" or the code of its
// first issue (skill_lock_drift, skill_lock_missing, skills_invalid, path_unsafe, read_failed, ..) with that detail.
// issues: every refusal of the load, in Core's (subject, code) order. code (only when failed): the first issue that
// names no pinned pack (the skills directory itself was refused), else skill_lock_drift when any pack drifted, else
// skill_lock_missing when any is missing, else the first issue's code.
internal static class SkillLockTool
{
    public const int MaxPathChars = 4096;
    public const string PathInvalid = "skills_lock_path_invalid";
    public const string RequestInvalid = "skills_lock_request_invalid";
    public const string InternalError = "skills_lock_internal_error";
    public const string StateOk = "ok";

    public sealed record Outcome(bool Succeeded, string Text);

    public static string Failure(string code, string? subject, string? detail) =>
        subject is null
            ? detail is null ? JsonSerializer.Serialize(new { status = "failed", code }) : JsonSerializer.Serialize(new { status = "failed", code, detail })
            : detail is null ? JsonSerializer.Serialize(new { status = "failed", code, subject }) : JsonSerializer.Serialize(new { status = "failed", code, subject, detail });

    // Null when the path is acceptable; name is "skills" or "lock".
    public static string? PathProblem(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || path.Length > MaxPathChars) return Failure(PathInvalid, null, name);
        try { Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return Failure(PathInvalid, null, name); }
        return null;
    }

    public static Outcome Verify(string skillsPath, string lockPath)
    {
        if (PathProblem(skillsPath, "skills") is { } badSkills) return new Outcome(false, badSkills);
        if (PathProblem(lockPath, "lock") is { } badLock) return new Outcome(false, badLock);
        try
        {
            var load = SkillLocking.Load(skillsPath, lockPath);
            if (load.Lock is null)
            {
                var issue = load.Issues.Count > 0 ? load.Issues[0] : new SkillLockIssue(InternalError, "", "no lock and no issue");
                return new Outcome(false, Failure(issue.Code, issue.Subject, issue.Detail));
            }
            return Report(load.Lock, load.Issues);
        }
        catch (SkillLockException error) { return new Outcome(false, Failure(error.Code, error.Subject, error.Detail)); }
        // Anything else is a defect, never a message passed off as a code.
        catch (Exception error) { return new Outcome(false, Failure(InternalError, null, error.GetType().Name)); }
    }

    static Outcome Report(SkillLock skillLock, IReadOnlyList<SkillLockIssue> issues)
    {
        var pinned = new HashSet<string>(skillLock.Packs.Select(entry => entry.Id), StringComparer.Ordinal);
        var verified = issues.Count == 0;
        string? code = null;
        if (!verified)
            code = issues.FirstOrDefault(issue => !pinned.Contains(issue.Subject))?.Code
                ?? (issues.Any(issue => issue.Code == SkillLockCodes.SkillLockDrift) ? SkillLockCodes.SkillLockDrift
                : issues.Any(issue => issue.Code == SkillLockCodes.SkillLockMissing) ? SkillLockCodes.SkillLockMissing
                : issues[0].Code);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", verified ? "succeeded" : "failed");
            if (code is not null) writer.WriteString("code", code);
            writer.WriteString("mode", "verify");
            writer.WriteBoolean("verified", verified);
            writer.WriteNumber("lock_version", SkillLock.CurrentVersion);
            writer.WriteString("content_hash", SkillPackHash.Scheme);
            writer.WriteStartArray("packs");
            foreach (var entry in skillLock.Packs)
            {
                var issue = issues.FirstOrDefault(item => item.Subject == entry.Id);
                writer.WriteStartObject();
                WriteEntryMembers(writer, entry);
                writer.WriteString("state", issue?.Code ?? StateOk);
                if (issue is not null) writer.WriteString("detail", issue.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("issues");
            foreach (var issue in issues)
            {
                writer.WriteStartObject();
                writer.WriteString("code", issue.Code);
                writer.WriteString("subject", issue.Subject);
                writer.WriteString("detail", issue.Detail);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return new Outcome(verified, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    // "id", "version", "content_sha256" of one lock entry (shared with the CLI pin/update summaries).
    public static void WriteEntryMembers(Utf8JsonWriter writer, SkillLockEntry entry)
    {
        writer.WriteString("id", entry.Id);
        writer.WriteString("version", entry.Version.ToString());
        writer.WriteString("content_sha256", entry.ContentSha256);
    }

    public static void WriteEntry(Utf8JsonWriter writer, SkillLockEntry? entry)
    {
        if (entry is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        WriteEntryMembers(writer, entry);
        writer.WriteEndObject();
    }
}
