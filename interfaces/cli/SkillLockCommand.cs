#nullable enable
using Bim.Mcp;

namespace Bim.Cli;

// P5-09 (red stub): `skills-lock pin|verify|update skills-directory lock.json [pack-id...]`.
internal static class SkillLockCommand
{
    public const string Usage = "usage: skills-lock pin|verify|update skills-directory lock.json [pack-id...]";

    public static void Run(string[] args)
    {
        Console.WriteLine(SkillLockTool.Failure("not_implemented", null, null));
        Environment.ExitCode = 1;
    }
}
