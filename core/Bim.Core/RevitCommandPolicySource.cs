namespace Bim.Core;

// The repository policy kept current for a long-running client (the MCP server; the CLI uses it too, once per run):
// RevitCommandPolicy.ForRepository is read again whenever .bim/revit-enabled-roots.txt or
// config/revit-content-libraries.txt changes existence, size or last-write time, so a folder the owner enables is
// usable without restarting Claude. Each call costs one stat of each file; the files are read only after a change.
// A refusal (allowlist_invalid) is remembered and thrown again until either file changes, so a malformed owner file
// enables no write at all, as before. The repository root is found once, from the binary location.
public sealed class RevitCommandPolicySource
{
    readonly object gate = new();
    readonly Func<string, RevitCommandPolicy> load;
    readonly string? enabledRootsPath;
    readonly string? librariesPath;
    (FileStamp Roots, FileStamp Libraries)? stamp;
    RevitCommandPolicy? policy;
    RevitCommandException? refusal;

    public RevitCommandPolicySource(string startDirectory) : this(startDirectory, RevitCommandPolicy.ForRepository) { }

    // load reads the policy of a repository root (RevitCommandPolicy.ForRepository; tests inject failures).
    public RevitCommandPolicySource(string startDirectory, Func<string, RevitCommandPolicy> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        this.load = load;
        RepositoryDirectory = RevitCommandPolicy.RepositoryRoot(startDirectory);
        if (RepositoryDirectory is null) return;
        enabledRootsPath = Path.Combine(RepositoryDirectory, ".bim", "revit-enabled-roots.txt");
        librariesPath = Path.Combine(RepositoryDirectory, "config", "revit-content-libraries.txt");
    }

    public string? RepositoryDirectory { get; }

    // How many loads were attempted (tests).
    public int Loads { get; private set; }

    // The current policy; throws the remembered RevitCommandException while the owner files are invalid. Outside a
    // repository nothing is enabled.
    public RevitCommandPolicy Current()
    {
        if (RepositoryDirectory is null) return RevitCommandPolicy.None;
        lock (gate)
        {
            var now = (Stamp(enabledRootsPath!), Stamp(librariesPath!));
            if (stamp != now)
            {
                Loads++;
                // The stamp, policy and refusal change together, and only after the load returned or refused: any other
                // exception propagates and leaves the previous stamp, so the next call loads again instead of serving
                // a stale or missing policy.
                RevitCommandPolicy? loaded = null;
                RevitCommandException? refused = null;
                try { loaded = load(RepositoryDirectory); }
                catch (RevitCommandException error) { refused = error; }
                if (loaded is null && refused is null) throw new InvalidOperationException("the policy loader returned null");
                stamp = now;
                policy = loaded;
                refusal = refused;
            }
            if (refusal is not null) throw new RevitCommandException(refusal.Code, refusal.Field, refusal.Detail);
            return policy!;
        }
    }

    readonly record struct FileStamp(bool Exists, long Length, long LastWriteTicks, bool Link);

    // An unreadable stat is a stamp of its own, so the next successful stat reloads.
    static FileStamp Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var link = info.LinkTarget is not null;
            return info.Exists ? new FileStamp(true, info.Length, info.LastWriteTimeUtc.Ticks, link) : new FileStamp(false, 0, 0, link);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new FileStamp(false, -1, -1, false); }
    }
}
