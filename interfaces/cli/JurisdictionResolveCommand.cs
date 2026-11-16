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
//   {"status":"failed","code":"jurisdiction_resolve_repository_not_found"}  neither a package nor a repository root;
//   {"status":"failed","code","subject","detail"}  the registry's JurisdictionIssue unchanged, from the load (e.g.
//       skills_invalid, lock_missing, skill_lock_drift, skill_lock_missing) or the selection (jurisdiction_unknown,
//       jurisdiction_ambiguous, jurisdiction_stub_refused, jurisdiction_invalid);
//   {"status":"failed","code":"jurisdiction_resolve_internal_error"}  a defect (IntentPlan.Internal, detail on stderr).
// Precedence: usage, skills directory, load, selection.
internal static class JurisdictionResolveCommand
{
    public const string Verb = "jurisdiction-resolve";
    public const string Usage = "usage: jurisdiction-resolve --code c | --country cc --municipality m [--lock skills.lock.json] [--allow-stub]";
    public const string UsageCode = "jurisdiction_resolve_usage";
    public const string RepositoryNotFound = "jurisdiction_resolve_repository_not_found";

    sealed record Options(string? Code, string? Country, string? Municipality, string? Lock, bool AllowStub);

    public static void Run(string[] args)
    {
        IntentEnvelope envelope;
        try { envelope = Execute(args); }
        catch (Exception error) { envelope = IntentPlan.Internal("jurisdiction-resolve", error); }
        envelope.Print();
    }

    static IntentEnvelope Execute(string[] args)
    {
        if (Parse(args) is not { } options)
            return new IntentEnvelope(JsonSerializer.Serialize(new { status = "failed", code = UsageCode, detail = Usage }), 1);
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
