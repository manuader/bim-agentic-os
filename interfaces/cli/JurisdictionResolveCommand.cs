#nullable enable
using System.Text.Json;
using Bim.Core;

namespace Bim.Cli;

// P10-21 `jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]`: the
// P10-06 JurisdictionRegistry selection on the CLI. Read-only: nothing is written, no store, no pipe, no Revit.
// Skills directory: the one SkillPackLookup serves (package-first): <package>/skills when SkillPackLookup.PackageRoot of
// the binary directory is a release package, else <repository>/skills of RevitCommandPolicy.RepositoryRoot.
// Loading: JurisdictionRegistry.Load(skills) without --lock; JurisdictionRegistry.LoadLocked(skills, lock) with it (P5-10:
// only the pinned packs, sidecars parsed from the hashed bytes). Selection: registry.Select(new ProjectJurisdiction(code,
// country, municipality), allowStub: --allow-stub).
// Success prints {"status":"succeeded","code","pack","version","stub"} (version the selected pack's x.y.z), exit 0.
// Refusals print one line, exit 1:
//   {"status":"failed","code":"jurisdiction_resolve_usage","detail":<usage>}  no selector, --code together with
//       --country/--municipality, only one of --country/--municipality, an option given twice, an unknown option or
//       positional argument, a missing, blank or "--"-prefixed value;
//   {"status":"failed","code":"skills_lock_path_invalid","detail":"lock"}  a --lock path SkillLockTool.PathProblem refuses
//       (NUL or over SkillLockTool.MaxPathChars characters; or, P10-28, a relative path Path.GetFullPath cannot
//       resolve because the working directory was deleted — FileNotFoundException on Linux/macOS; no other input is
//       rejected only by GetFullPath on .NET 8, P10-26), as skills-lock does (P10-24);
//   {"status":"failed","code":"jurisdiction_resolve_repository_not_found"}  neither a package nor a repository root;
//   {"status":"failed","code","subject","detail"}  the registry's JurisdictionIssue unchanged, from the load (e.g.
//       skills_invalid, lock_missing, skill_lock_drift, skill_lock_missing) or the selection (jurisdiction_unknown,
//       jurisdiction_ambiguous, jurisdiction_stub_refused, jurisdiction_invalid);
//   {"status":"failed","code":"jurisdiction_resolve_internal_error"}  a defect (IntentPlan.Internal, detail on stderr).
// Precedence: usage, lock path, skills directory, load, selection. An absent lock file is the load's lock_missing.
// Registry issues of a successful load (JurisdictionRegistry.Issues: claim_duplicate, alias_overlap, pack_missing, ...)
// are dropped: the success envelope never carries them (P10-24); a selection they affect refuses on its own terms.
// P10-27: Resolve is the whole handler (the envelope Run prints); the read-only MCP tool jurisdiction_resolve runs it
// (interfaces/mcp/JurisdictionResolveTool.cs, which answers UsageRefusal with -32602), so both share one call path.
internal static class JurisdictionResolveCommand
{
    public const string Verb = "jurisdiction-resolve";
    public const string Usage = "usage: jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]";
    public const string UsageCode = "jurisdiction_resolve_usage";
    public const string RepositoryNotFound = "jurisdiction_resolve_repository_not_found";

    sealed record Options(string? Code, string? Country, string? Municipality, string? Lock, bool AllowStub);

    // P10-27: the usage refusal envelope, by value (the MCP tool jurisdiction_resolve answers it with -32602).
    public static readonly IntentEnvelope UsageRefusal = new(JsonSerializer.Serialize(new { status = "failed", code = UsageCode, detail = Usage }), 1);

    // P10-27 test seam (null in production): invoked first inside Resolve's guarded block, so a test can force the typed
    // internal error through the CLI and the MCP tool alike.
    internal static Action? WorkProbe { get; set; }

    public static void Run(string[] args) => Resolve(args).Print();

    // P10-27: the verb's whole handler (args[0] is the verb): the envelope Run prints, shared by the MCP tool
    // jurisdiction_resolve (interfaces/mcp/JurisdictionResolveTool.cs). Writes nothing to the console or the exit code.
    public static IntentEnvelope Resolve(string[] args)
    {
        try { return Execute(args); }
        catch (Exception error) { return IntentPlan.Internal("jurisdiction-resolve", error); }
    }

    static IntentEnvelope Execute(string[] args)
    {
        WorkProbe?.Invoke();
        if (Parse(args) is not { } options) return UsageRefusal;
        if (options.Lock is not null && Bim.Mcp.SkillLockTool.PathProblem(options.Lock, "lock") is { } badLock) return new IntentEnvelope(badLock, 1);
        var root = SkillPackLookup.PackageRoot(AppContext.BaseDirectory) ?? RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
        if (root is null) return IntentPlan.Failed(RepositoryNotFound);
        var skills = Path.Combine(root, "skills");
        var load = options.Lock is null ? JurisdictionRegistry.Load(skills) : JurisdictionRegistry.LoadLocked(skills, options.Lock);
        if (load.Refusal is { } loadRefusal) return Refused(loadRefusal);
        var registry = load.Registry!;
        var selection = registry.Select(new ProjectJurisdiction(options.Code, options.Country, options.Municipality), options.AllowStub);
        if (selection.Refusal is { } refusal) return Refused(refusal);
        var claim = selection.Claim!;
        var pack = registry.Catalog.Find(claim.Pack) ?? throw new InvalidOperationException("selected pack " + claim.Pack + " is not in the catalog");
        return new IntentEnvelope(JsonSerializer.Serialize(new { status = "succeeded", code = claim.Code, pack = claim.Pack, version = pack.Version.ToString(), stub = claim.Stub }), 0);
    }

    static IntentEnvelope Refused(JurisdictionIssue issue) =>
        new(JsonSerializer.Serialize(new { status = "failed", code = issue.Code, subject = issue.Subject, detail = issue.Detail }), 1);

    // args[0] is the verb. Null on any usage problem.
    static Options? Parse(string[] args)
    {
        string? code = null, country = null, municipality = null, lockPath = null;
        var allowStub = false;
        for (var index = 1; index < args.Length; index++)
        {
            var name = args[index];
            if (name == "--allow-stub")
            {
                if (allowStub) return null;
                allowStub = true;
                continue;
            }
            if (name is not ("--code" or "--country" or "--municipality" or "--lock")) return null;
            if (index + 1 >= args.Length) return null;
            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)) return null;
            switch (name)
            {
                case "--code": if (code is not null) return null; code = value; break;
                case "--country": if (country is not null) return null; country = value; break;
                case "--municipality": if (municipality is not null) return null; municipality = value; break;
                default: if (lockPath is not null) return null; lockPath = value; break;
            }
        }
        if (code is not null && (country is not null || municipality is not null)) return null;
        if (code is null && (country is null || municipality is null)) return null;
        return new Options(code, country, municipality, lockPath, allowStub);
    }
}
