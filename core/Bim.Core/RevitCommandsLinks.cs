namespace Bim.Core;

// File-system facts the link guard needs, injectable for tests. IsLink: the path is a symlink or junction
// (a reparse point with a link target). HardLinkCount: number of hard links of an existing file, null when unknown.
public sealed record RevitFileSystemProbe(Func<string, bool> IsLink, Func<string, int?> HardLinkCount)
{
    public static RevitFileSystemProbe Real { get; } = new(_ => false, _ => null);
}

public static class RevitLinkGuard
{
    public static string? Check(string documentPath, string root, RevitFileSystemProbe probe) => null;
}
