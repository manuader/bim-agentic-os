#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using Bim.Core;

namespace Bim.Cli;

// P5-21 `skill-list [--skills skills-directory]`: the skill packs of a skills directory on the CLI. Read-only: nothing is
// written, no store, no pipe, no Revit.
// Skills directory: --skills (made absolute against the working directory, a trailing separator trimmed) when given; else the one SkillPackLookup
// serves (package-first): <package>/skills when SkillPackLookup.PackageRoot of the binary directory is a release package,
// else <repository>/skills of RevitCommandPolicy.RepositoryRoot.
// Loading (P5-23/P5-24: the skills directory is read once on every path): one JurisdictionRegistry.Load(skills) supplies
// the catalogue (registry.Catalog), the stub flags (the claim on a pack, JurisdictionRegistry.ClaimFor; false without
// one, as in JurisdictionValidator) and, when the loader rejected the directory, its typed SkillException
// (JurisdictionLoad.LoaderError), printed unchanged. The directory is never read a second time.
// Registry issues of a successful load (JurisdictionRegistry.Issues: claim_duplicate, pack_missing, pack_not_code,
// alias_overlap, ...) are dropped (P5-23): the success envelope never carries them and its bytes are unchanged; a
// duplicated or non-code claim simply flags no pack as stub. Output is culture-invariant (ordinal order, JSON numbers).
// Success prints, exit 0, packs in ordinal id order:
//   {"status":"succeeded","skills_path":<absolute directory>,"packs":[{"id","version","kind","stub",
//    "source_confidence":{"rules","cited","owner-confirmed","to-verify","lowest"}}...]}
// (version x.y.z, kind the manifest kind name, source_confidence the JurisdictionConfidenceSummary of the pack's rules).
// Refusals print one line, exit 1:
//   {"status":"failed","code":"skill_list_usage","detail":<usage>}  an unknown option or positional argument, --skills
//       given twice, a missing, blank or "--"-prefixed value;
//   {"status":"failed","code":"skill_list_repository_not_found"}  no --skills and neither a package nor a repository root;
//   {"status":"failed","code","field","detail","file"}  the loader's SkillException unchanged (e.g. root_missing,
//       json_invalid, id_mismatch, path_unsafe), file root-relative;
//   {"status":"failed","code","subject","detail"}  a jurisdiction sidecar refusal of JurisdictionRegistry.Load unchanged;
//   {"status":"failed","code":"skill_list_internal_error"}  a defect (IntentPlan.Internal, detail on stderr).
// Precedence: usage, skills directory, loader, sidecars.
// List(skillsDirectory) is the shared handler (the MCP skill_list tool, P5-22): it never prints and never throws.
// P5-31: the internal-error diagnostic reaches stderr only through WriteDiagnostic (Run here, the MCP tool there), which
// ignores a Console.Error that throws while the line is written: the typed line and exit 1 still follow, nothing escapes.
// P5-33: Run prints the line guarded too: a Console.Out that throws while the typed internal-error, refusal or success line
// is written drops only that line: exit 1, nothing escapes Run (the GenerateWrite.cs P7-29 pattern).
internal static class SkillListCommand
{
    public const string Verb = "skill-list";
    public const string Usage = "usage: skill-list [--skills skills-directory]";
    public const string UsageCode = "skill_list_usage";
    public const string RepositoryNotFound = "skill_list_repository_not_found";

    // P5-29 test seam (null in production, never assigned under interfaces/): invoked first inside Listing, which List runs
    // in its guarded block, so a test can force the typed skill_list_internal_error through the CLI and the MCP tool alike.
    // Not named WorkProbe: P10-27's pin requires that whole word only in JurisdictionResolveCommand.
    internal static Action? ListingWorkProbe { get; set; }

    // P5-31 second test seam (null in production, never assigned under interfaces/): invoked with the load right after the
    // one JurisdictionRegistry.Load inside Listing, so a defect after the read reaches the same typed internal error.
    internal static Action<JurisdictionLoad>? LoadedProbe { get; set; }

    public static void Run(string[] args)
    {
        IntentEnvelope envelope;
        try { envelope = Execute(args); }
        catch (Exception error) { envelope = IntentPlan.Internal("skill-list", error); }
        WriteDiagnostic(envelope);
        try { (envelope with { Diagnostic = null }).Print(); }
        catch (Exception) { Environment.ExitCode = 1; }
    }

    // P5-31: the envelope's internal-error diagnostic, if any, on stderr once; a Console.Error that throws is ignored.
    internal static void WriteDiagnostic(IntentEnvelope envelope)
    {
        if (envelope.Diagnostic is not { } detail) return;
        try { Console.Error.WriteLine(detail); }
        catch (Exception) { }
    }

    static IntentEnvelope Execute(string[] args)
    {
        if (!TryParse(args, out var skills))
            return new IntentEnvelope(JsonSerializer.Serialize(new { status = "failed", code = UsageCode, detail = Usage }), 1);
        return List(skills);
    }

    // The listing envelope for skillsDirectory, or for the package-first default directory when it is null.
    public static IntentEnvelope List(string? skillsDirectory)
    {
        try { return Listing(skillsDirectory); }
        catch (Exception error) { return IntentPlan.Internal("skill-list", error); }
    }

    static IntentEnvelope Listing(string? skillsDirectory)
    {
        ListingWorkProbe?.Invoke();
        if (skillsDirectory is null)
        {
            var root = SkillPackLookup.PackageRoot(AppContext.BaseDirectory) ?? RevitCommandPolicy.RepositoryRoot(AppContext.BaseDirectory);
            if (root is null) return IntentPlan.Failed(RepositoryNotFound);
            skillsDirectory = Path.Combine(root, "skills");
        }
        var skills = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skillsDirectory));
        var load = JurisdictionRegistry.Load(skills);
        LoadedProbe?.Invoke(load);
        if (load.Refusal is { } refusal)
        {
            if (load.LoaderError is { } error)
                return new IntentEnvelope(JsonSerializer.Serialize(new { status = "failed", code = error.Code, field = error.Field, detail = error.Detail, file = error.File }), 1);
            return new IntentEnvelope(JsonSerializer.Serialize(new { status = "failed", code = refusal.Code, subject = refusal.Subject, detail = refusal.Detail }), 1);
        }
        var registry = load.Registry!;
        var packs = new JsonArray();
        foreach (var pack in registry.Catalog.Skills.OrderBy(pack => pack.Id, StringComparer.Ordinal))
        {
            var confidence = JurisdictionConfidenceSummary.Of(pack);
            packs.Add(new JsonObject
            {
                ["id"] = pack.Id,
                ["version"] = pack.Version.ToString(),
                ["kind"] = SkillParser.KindName(pack.Kind),
                ["stub"] = registry.ClaimFor(pack.Id)?.Stub ?? false,
                ["source_confidence"] = new JsonObject
                {
                    ["rules"] = confidence.Rules,
                    [SkillRoomValidator.ConfidenceCited] = confidence.Cited,
                    [SkillRoomValidator.ConfidenceOwnerConfirmed] = confidence.OwnerConfirmed,
                    [SkillRoomValidator.ConfidenceToVerify] = confidence.ToVerify,
                    ["lowest"] = confidence.Lowest,
                },
            });
        }
        var envelope = new JsonObject { ["status"] = "succeeded", ["skills_path"] = skills, ["packs"] = packs };
        return new IntentEnvelope(envelope.ToJsonString(), 0);
    }

    // args[0] is the verb. False on any usage problem; skills is null without --skills.
    static bool TryParse(string[] args, out string? skills)
    {
        skills = null;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] != "--skills" || skills is not null || index + 1 >= args.Length) return false;
            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal)) return false;
            skills = value;
        }
        return true;
    }
}
