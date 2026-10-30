namespace Bim.Core;

// Roots under which write commands are enabled (ADR0009 "copies first"). Built by the interface layer from the
// repository default (.bim/native-runs, where owner sessions place fixture copies) plus an explicitly passed
// allowlist file. This record is where the owner's recorded "enabled real project" list plugs in later: add its
// entries to EnabledRoots; RevitCopiesGuard.Check stays the single decision.
public sealed record RevitCommandPolicy(IReadOnlyList<string> EnabledRoots)
{
    public const string DefaultRelativeRoot = ".bim/native-runs";
    public const int MaxAllowlistEntries = 256;

    public static RevitCommandPolicy None { get; } = new(Array.Empty<string>());

    // Allowlist text: one absolute path per line (a directory root or an exact document); blank lines and lines
    // starting with # are ignored. Any malformed entry rejects the whole list.
    public static RevitCommandPolicy FromAllowlist(string? allowlistText, string? defaultRoot)
    {
        var roots = new List<string>();
        if (defaultRoot is not null)
        {
            if (!RevitCopiesGuard.IsAbsoluteNormalized(defaultRoot)) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, "default_root", defaultRoot);
            roots.Add(defaultRoot);
        }
        if (allowlistText is not null)
        {
            var lineNumber = 0;
            foreach (var raw in allowlistText.Split('\n'))
            {
                lineNumber++;
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (!RevitCopiesGuard.IsAbsoluteNormalized(line)) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, "line " + lineNumber, "absolute path without . or .. segments required");
                roots.Add(line);
                if (roots.Count > MaxAllowlistEntries) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, "line " + lineNumber, "too many entries");
            }
        }
        return new RevitCommandPolicy(roots.AsReadOnly());
    }

    // Repository default plus optional allowlist. Without a repository (no global.json above startDirectory)
    // only allowlist entries enable writes.
    public static RevitCommandPolicy ForRepository(string startDirectory, string? allowlistText)
    {
        var root = RepositoryRoot(startDirectory);
        return FromAllowlist(allowlistText, root is null ? null : Path.Combine(root, ".bim", "native-runs"));
    }

    public static string? RepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName;
    }
}

// Pure decision: no file system access. Paths are compared as text after splitting on both separators.
// Drive and UNC paths (Windows documents) compare case-insensitively; '/'-rooted paths compare exactly.
// A link or junction inside an enabled root is not resolved here; the executor must check the opened document.
public static class RevitCopiesGuard
{
    // Null when allowed, otherwise a RevitCommandCodes value. Read-only commands are allowed on any document.
    public static string? Check(bool writes, string documentPath, RevitCommandPolicy policy)
    {
        throw new NotImplementedException("C02 red: not implemented yet");
    }

    // True when path equals root or lies inside it (whole segments only: "native-runs-x" is not under "native-runs").
    public static bool IsUnder(string path, string root)
    {
        if (Split(path) is not { } p || Split(root) is not { } r) return false;
        if (p.Kind != r.Kind || p.Segments.Length < r.Segments.Length) return false;
        var comparison = p.Kind == "/" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = 0; i < r.Segments.Length; i++)
            if (!string.Equals(p.Segments[i], r.Segments[i], comparison)) return false;
        return true;
    }

    public static bool IsAbsoluteNormalized(string? path) => path is not null && Split(path) is not null;

    sealed record Parts(string Kind, string[] Segments);

    static Parts? Split(string path)
    {
        if (path.Length == 0 || path.Length > RevitCommandProtocol.MaxPathChars || path.Any(char.IsControl)) return null;
        static bool Separator(char c) => c is '/' or '\\';
        string kind;
        string rest;
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && Separator(path[2]))
        {
            kind = char.ToLowerInvariant(path[0]) + ":";
            rest = path[3..];
        }
        else if (path.Length >= 3 && Separator(path[0]) && Separator(path[1]))
        {
            // UNC \\server\share\...; device namespaces \\?\ and \\.\ are refused.
            if (path[2] is '?' or '.' || Separator(path[2])) return null;
            kind = "unc";
            rest = path[2..];
        }
        else if (path[0] == '/')
        {
            kind = "/";
            rest = path[1..];
        }
        else return null;
        var segments = rest.Split('/', '\\').Where(segment => segment.Length > 0).ToArray();
        foreach (var segment in segments)
        {
            if (segment is "." or "..") return null;
            // ':' outside the drive would address an alternate data stream on Windows.
            if (kind != "/" && segment.Contains(':')) return null;
        }
        if (kind == "unc" && segments.Length < 2) return null;
        return new Parts(kind, segments);
    }
}
