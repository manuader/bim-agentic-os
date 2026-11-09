namespace Bim.Core;

// P5-10 lock-checked jurisdiction sidecars (stub; implemented in the next commit).
public sealed partial class JurisdictionRegistry
{
    public static JurisdictionLoad LoadLocked(string skillsRoot, string lockPath) => NotYet();

    public static JurisdictionLoad LoadLocked(string skillsRoot, SkillLock skillLock) => NotYet();

    public static JurisdictionLoad FromSnapshots(SkillLock skillLock, IEnumerable<SkillPackSnapshot> snapshots) => NotYet();

    static JurisdictionLoad NotYet() => new(null, new JurisdictionIssue("not_implemented", "", "P5-10 stub"));
}
