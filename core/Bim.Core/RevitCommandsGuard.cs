namespace Bim.Core;

// Roots under which write commands are enabled (ADR0009 "copies first"): the repository default .bim/native-runs
// (where owner sessions place fixture copies) plus the entries of the owner file .bim/revit-enabled-roots.txt, which
// git ignores and only the owner edits. Neither the CLI nor an MCP tool call can add a root. This record is where the
// owner's recorded "enabled real project" list plugs in: as lines of that file. RevitCopiesGuard.Check and
// RevitLinkGuard.Check stay the single decision.
public sealed record RevitCommandPolicy(IReadOnlyList<string> EnabledRoots)
{
    public const string DefaultRelativeRoot = ".bim/native-runs";
    public const string EnabledRootsFile = ".bim/revit-enabled-roots.txt";
    public const int MaxAllowlistEntries = 256;
    public const int MaxEnabledRootsFileBytes = 65536;

    public static RevitCommandPolicy None { get; } = new(Array.Empty<string>());

    // Allowlist text: one absolute path per line (a directory root or an exact document); blank lines and lines
    // starting with # are ignored. Any malformed or too-broad entry (a drive root, "/", or a UNC path without
    // server, share and one directory) rejects the whole list.
    public static RevitCommandPolicy FromAllowlist(string? allowlistText, string? defaultRoot)
    {
        var roots = new List<string>();
        if (defaultRoot is not null)
        {
            if (Problem(defaultRoot) is { } problem) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, "default_root", problem);
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
                if (Problem(line) is { } problem) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, "line " + lineNumber, problem);
                roots.Add(line);
                if (roots.Count > MaxAllowlistEntries) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, "line " + lineNumber, "too many entries");
            }
        }
        return new RevitCommandPolicy(roots.AsReadOnly());
    }

    static string? Problem(string entry) =>
        !RevitCopiesGuard.IsAbsoluteNormalized(entry) ? "absolute path without . or .. segments required"
        : RevitCopiesGuard.IsTooBroad(entry) ? "too broad: name at least one directory below a drive, / or \\\\server\\share"
        : null;

    // The repository default plus the owner file, both found from startDirectory (the CLI or MCP binary location) by
    // walking up to global.json. Without a repository no write is enabled.
    public static RevitCommandPolicy ForRepository(string startDirectory)
    {
        var root = RepositoryRoot(startDirectory);
        if (root is null) return None;
        return FromAllowlist(ReadEnabledRootsFile(Path.Combine(root, ".bim", "revit-enabled-roots.txt")), Path.Combine(root, ".bim", "native-runs"));
    }

    // Missing file: no extra roots. A link, an oversized file or an unreadable file fails closed.
    static string? ReadEnabledRootsFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, EnabledRootsFile, "must not be a link");
            if (!info.Exists) return null;
            if (info.Length > MaxEnabledRootsFileBytes) throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, EnabledRootsFile, "larger than 65536 bytes");
            return File.ReadAllText(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RevitCommandException(RevitCommandCodes.AllowlistInvalid, EnabledRootsFile, error.GetType().Name);
        }
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
// Links and junctions are not resolved here: RevitLinkGuard checks them on this machine before forwarding, and the
// executor must check the document it actually opened again (any same-user process can write to the pipe).
public static class RevitCopiesGuard
{
    // Null when allowed, otherwise a RevitCommandCodes value. Read-only commands are allowed on any document.
    public static string? Check(bool writes, string documentPath, RevitCommandPolicy policy)
    {
        if (!IsAbsoluteNormalized(documentPath)) return RevitCommandCodes.DocumentPathInvalid;
        if (!writes) return null;
        return policy.EnabledRoots.Any(root => IsUnder(documentPath, root)) ? null : RevitCommandCodes.DocumentNotEnabled;
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

    // A drive root, "/" or a UNC share root would enable everything below it.
    public static bool IsTooBroad(string path) => Split(path) is not { } parts || parts.Segments.Length < (parts.Kind == "unc" ? 3 : 1);

    // The enabled root a write is allowed under; the one with fewest segments, so the link guard checks the most.
    public static string? MatchingRoot(string documentPath, RevitCommandPolicy policy) =>
        policy.EnabledRoots.Where(root => IsUnder(documentPath, root)).OrderBy(root => Split(root)!.Segments.Length).FirstOrDefault();

    // The root and each deeper component down to the document, spelled as in documentPath; null when not under root.
    public static IReadOnlyList<string>? ComponentsFromRoot(string documentPath, string root)
    {
        if (!IsUnder(documentPath, root) || Split(documentPath) is not { } p || Split(root) is not { } r) return null;
        var separator = p.Kind == "/" ? "/" : "\\";
        var prefix = p.Kind switch { "/" => "/", "unc" => "\\\\", _ => documentPath[..2] + "\\" };
        var components = new List<string>();
        for (var count = r.Segments.Length; count <= p.Segments.Length; count++)
            components.Add(prefix + string.Join(separator, p.Segments.Take(count)));
        return components;
    }

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
