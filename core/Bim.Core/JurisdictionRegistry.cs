namespace Bim.Core;

// P10-06: the set of jurisdiction claims over a skill catalog, and selection of one code pack for a project.
// Rules:
// - A claim whose pack is not in the catalog is dropped (pack_missing); one whose pack is not of kind code is dropped
//   (pack_not_code). Two claims for one code are both kept and reported (claim_duplicate); selecting that code, by code or
//   by metadata, is refused as jurisdiction_ambiguous rather than picking one.
// - Claims are ordered by code then pack id (ordinal); issues by code, subject then detail; unregistered code packs (kind
//   code without a claim) by id. Selection never depends on input order.
public sealed record JurisdictionSelection(JurisdictionClaim? Claim, string MatchedBy, JurisdictionIssue? Refusal)
{
    public const string ByCode = "code";
    public const string ByMetadata = "metadata";

    public bool Succeeded => Refusal is null;
}

// Result of loading a skills directory with its sidecars: either Registry is set or Refusal names the first problem.
public sealed record JurisdictionLoad(JurisdictionRegistry? Registry, JurisdictionIssue? Refusal)
{
    public bool Succeeded => Refusal is null;
}

public sealed class JurisdictionRegistry
{
    public JurisdictionRegistry(SkillCatalog catalog, IEnumerable<JurisdictionClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(claims);
        Catalog = catalog;
        var kept = new List<JurisdictionClaim>();
        var issues = new List<JurisdictionIssue>();
        foreach (var claim in claims)
        {
            ArgumentNullException.ThrowIfNull(claim);
            var pack = catalog.Find(claim.Pack);
            if (pack is null) { issues.Add(new JurisdictionIssue(JurisdictionCodes.PackMissing, claim.Pack, "jurisdiction " + claim.Code + " names a pack that is not in the catalog")); continue; }
            if (pack.Kind != SkillKind.Code) { issues.Add(new JurisdictionIssue(JurisdictionCodes.PackNotCode, claim.Pack, "jurisdiction " + claim.Code + " names a " + SkillParser.KindName(pack.Kind) + " skill; only code packs serve a jurisdiction")); continue; }
            kept.Add(claim);
        }
        Claims = kept.OrderBy(claim => claim.Code, StringComparer.Ordinal).ThenBy(claim => claim.Pack, StringComparer.Ordinal).ToArray();
        foreach (var group in Claims.GroupBy(claim => claim.Code, StringComparer.Ordinal).Where(group => group.Count() > 1))
            issues.Add(new JurisdictionIssue(JurisdictionCodes.ClaimDuplicate, group.Key, "claimed by " + string.Join(", ", group.Select(claim => claim.Pack))));
        Issues = issues.OrderBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Subject, StringComparer.Ordinal).ThenBy(issue => issue.Detail, StringComparer.Ordinal).ToArray();
        var claimed = new HashSet<string>(Claims.Select(claim => claim.Pack), StringComparer.Ordinal);
        UnregisteredCodePacks = catalog.Skills.Where(skill => skill.Kind == SkillKind.Code && !claimed.Contains(skill.Id)).Select(skill => skill.Id).ToArray();
    }

    public SkillCatalog Catalog { get; }
    public IReadOnlyList<JurisdictionClaim> Claims { get; }
    public IReadOnlyList<JurisdictionIssue> Issues { get; }
    public IReadOnlyList<string> UnregisteredCodePacks { get; }

    // Distinct jurisdiction codes in ordinal order.
    public IReadOnlyList<string> Codes => Claims.Select(claim => claim.Code).Distinct(StringComparer.Ordinal).ToArray();

    // Loads skills/<id>/skill.json with SkillLoader and every skills/<id>/jurisdiction.json beside them.
    public static JurisdictionLoad Load(string skillsRoot)
    {
        ArgumentNullException.ThrowIfNull(skillsRoot);
        SkillCatalog catalog;
        try { catalog = SkillLoader.LoadDirectory(skillsRoot); }
        catch (SkillException error) { return new JurisdictionLoad(null, new JurisdictionIssue(JurisdictionCodes.SkillsInvalid, error.File ?? ".", error.Code + (error.Detail is null ? "" : ": " + error.Detail))); }
        var root = Path.GetFullPath(skillsRoot);
        var claims = new List<JurisdictionClaim>();
        foreach (var skill in catalog.Skills)
        {
            var (claim, issue) = JurisdictionSidecar.Read(root, skill.Id);
            if (issue is not null) return new JurisdictionLoad(null, issue);
            if (claim is not null) claims.Add(claim);
        }
        return new JurisdictionLoad(new JurisdictionRegistry(catalog, claims), null);
    }

    // Accent-stripped, whitespace-folded, lower-cased invariant text used for every name comparison.
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SkillRoomValidator.NormalizeName(text).ToLowerInvariant();
    }

    // Code route: a non-blank Code must be a well-formed jurisdiction code (after trimming and lower-casing) with exactly
    // one claim; Country and Municipality, when also given, must match that claim (else jurisdiction_conflict).
    // Metadata route (no Code): Country and Municipality are both required (only one: jurisdiction_incomplete); a claim
    // matches when the country equals its country label or a country name and the municipality equals its locality label
    // or a municipality name. No match: jurisdiction_unknown; more than one claim: jurisdiction_ambiguous. Nothing given:
    // jurisdiction_unspecified.
    public JurisdictionSelection Select(ProjectJurisdiction project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var code = Blank(project.Code) ? null : project.Code!.Trim().ToLowerInvariant();
        var country = Blank(project.Country) ? null : Normalize(project.Country!);
        var municipality = Blank(project.Municipality) ? null : Normalize(project.Municipality!);
        if (code is not null)
        {
            if (!JurisdictionClaim.IsCode(code)) return Refuse(JurisdictionCodes.JurisdictionInvalid, code, "jurisdiction code such as ar.caba expected");
            var matches = Claims.Where(claim => claim.Code == code).ToArray();
            if (matches.Length == 0) return Refuse(JurisdictionCodes.JurisdictionUnknown, code, "no rule pack is registered for this jurisdiction; registered: " + Registered());
            if (matches.Length > 1) return Refuse(JurisdictionCodes.JurisdictionAmbiguous, code, "claimed by " + string.Join(", ", matches.Select(claim => claim.Pack)));
            var chosen = matches[0];
            if (country is not null && !CountryMatches(chosen, country)) return Refuse(JurisdictionCodes.JurisdictionConflict, code, "country " + project.Country!.Trim() + " does not match jurisdiction " + code);
            if (municipality is not null && !MunicipalityMatches(chosen, municipality)) return Refuse(JurisdictionCodes.JurisdictionConflict, code, "municipality " + project.Municipality!.Trim() + " does not match jurisdiction " + code);
            return new JurisdictionSelection(chosen, JurisdictionSelection.ByCode, null);
        }
        if (country is null && municipality is null) return Refuse(JurisdictionCodes.JurisdictionUnspecified, "", "give a jurisdiction code or a country and a municipality");
        if (country is null || municipality is null)
            return Refuse(JurisdictionCodes.JurisdictionIncomplete, country is null ? "country" : "municipality", "building codes are local: both country and municipality are required without a jurisdiction code");
        var found = Claims.Where(claim => CountryMatches(claim, country) && MunicipalityMatches(claim, municipality)).ToArray();
        var subject = country + "/" + municipality;
        if (found.Length == 0) return Refuse(JurisdictionCodes.JurisdictionUnknown, subject, "no registered jurisdiction matches; registered: " + Registered());
        if (found.Length > 1) return Refuse(JurisdictionCodes.JurisdictionAmbiguous, subject, "matches " + string.Join(", ", found.Select(claim => claim.Code + " (" + claim.Pack + ")")));
        return new JurisdictionSelection(found[0], JurisdictionSelection.ByMetadata, null);
    }

    static bool CountryMatches(JurisdictionClaim claim, string normalized) =>
        claim.Country == normalized || claim.CountryNames.Any(name => Normalize(name) == normalized);

    static bool MunicipalityMatches(JurisdictionClaim claim, string normalized) =>
        claim.Locality == normalized || claim.MunicipalityNames.Any(name => Normalize(name) == normalized);

    string Registered() => Codes.Count == 0 ? "(none)" : string.Join(", ", Codes);

    static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    static JurisdictionSelection Refuse(string code, string subject, string detail) => new(null, "", new JurisdictionIssue(code, subject, detail));
}
