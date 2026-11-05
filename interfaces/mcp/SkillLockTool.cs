#nullable enable
using System.Text.Json;

namespace Bim.Mcp;

// P5-09 (red stub): the read-only verify engine behind the CLI verb `skills-lock verify` and the MCP tool skills_lock_verify.
internal static class SkillLockTool
{
    public sealed record Outcome(bool Succeeded, string Text);

    public static string Failure(string code, string? subject, string? detail) => JsonSerializer.Serialize(new { status = "failed", code });

    public static Outcome Verify(string skillsPath, string lockPath) => new(false, Failure("not_implemented", null, null));
}
