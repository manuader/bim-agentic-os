namespace Bim.Core;

// P10-06: the set of jurisdiction claims over a skill catalog, and selection of one code pack for a project.
// Rules:
// - A claim whose pack is not in the catalog is dropped (pack_missing); one whose pack is not of kind code is dropped
//   (pack_not_code). Two claims for one code are both kept and reported (claim_duplicate); selecting that code, by code or
//   by metadata, is refused as jurisdiction_ambiguous rather than picking one.
// - A hand-built claim with a malformed code (code_invalid), a pack outside its code (pack_mismatch) or missing or
//   malformed names (value_invalid) is dropped. Distinct codes whose country and municipality aliases coincide after
//   normalisation are kept and reported (alias_overlap). Stub claims are only selected when the caller opts in.
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

public sealed partial class JurisdictionRegistry
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
            // Hand-built claims get the same checks as parsed sidecars.
            if (!JurisdictionClaim.IsCode(claim.Code)) { issues.Add(new JurisdictionIssue(JurisdictionCodes.CodeInvalid, Printable(claim.Code), "claim on pack " + Printable(claim.Pack) + " has a malformed jurisdiction code")); continue; }
            if (!SkillParser.IsId(claim.Pack) || !claim.Pack.StartsWith(claim.Code + ".", StringComparison.Ordinal)) { issues.Add(new JurisdictionIssue(JurisdictionCodes.PackMismatch, Printable(claim.Pack), "pack is not a skill id under jurisdiction " + claim.Code)); continue; }
            if (!NamesValid(claim.CountryNames) || !NamesValid(claim.MunicipalityNames)) { issues.Add(new JurisdictionIssue(JurisdictionCodes.ValueInvalid, claim.Pack, "country and municipality name lists must be present and hold non-blank well-formed names")); continue; }
            if (claim.Note is not null && !WellFormed(claim.Note)) { issues.Add(new JurisdictionIssue(JurisdictionCodes.ValueInvalid, claim.Pack, "note is not well-formed text")); continue; }
            var pack = catalog.Find(claim.Pack);
            if (pack is null) { issues.Add(new JurisdictionIssue(JurisdictionCodes.PackMissing, claim.Pack, "jurisdiction " + claim.Code + " names a pack that is not in the catalog")); continue; }
            if (pack.Kind != SkillKind.Code) { issues.Add(new JurisdictionIssue(JurisdictionCodes.PackNotCode, claim.Pack, "jurisdiction " + claim.Code + " names a " + SkillParser.KindName(pack.Kind) + " skill; only code packs serve a jurisdiction")); continue; }
            kept.Add(claim);
        }
        Claims = kept.OrderBy(claim => claim.Code, StringComparer.Ordinal).ThenBy(claim => claim.Pack, StringComparer.Ordinal).ToArray();
        foreach (var group in Claims.GroupBy(claim => claim.Code, StringComparer.Ordinal).Where(group => group.Count() > 1))
            issues.Add(new JurisdictionIssue(JurisdictionCodes.ClaimDuplicate, group.Key, "claimed by " + string.Join(", ", group.Select(claim => claim.Pack))));
        // Two jurisdictions whose country and municipality aliases coincide after normalisation cannot be told apart by
        // metadata; they stay registered (metadata selection is refused as ambiguous) and the overlap is reported here.
        for (var left = 0; left < Claims.Count; left++)
            for (var right = left + 1; right < Claims.Count; right++)
            {
                var a = Claims[left];
                var b = Claims[right];
                if (a.Code == b.Code) continue;
                var countries = CountryKeys(a).Intersect(CountryKeys(b), StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
                if (countries.Length == 0) continue;
                var municipalities = MunicipalityKeys(a).Intersect(MunicipalityKeys(b), StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
                if (municipalities.Length == 0) continue;
                issues.Add(new JurisdictionIssue(JurisdictionCodes.AliasOverlap, a.Code + " " + b.Code, "both match country " + string.Join("|", countries) + " and municipality " + string.Join("|", municipalities)));
            }
        Issues = issues.OrderBy(issue => issue.Code, StringComparer.Ordinal).ThenBy(issue => issue.Subject, StringComparer.Ordinal).ThenBy(issue => issue.Detail, StringComparer.Ordinal).ToArray();
        var claimed = new HashSet<string>(Claims.Select(claim => claim.Pack), StringComparer.Ordinal);
        UnregisteredCodePacks = catalog.Skills.Where(skill => skill.Kind == SkillKind.Code && !claimed.Contains(skill.Id)).Select(skill => skill.Id).ToArray();
    }

    public SkillCatalog Catalog { get; }
    public IReadOnlyList<JurisdictionClaim> Claims { get; }
    public IReadOnlyList<JurisdictionIssue> Issues { get; }
    public IReadOnlyList<string> UnregisteredCodePacks { get; }

    // The single claim on pack, or null when no claim (or a duplicated code) covers it.
    public JurisdictionClaim? ClaimFor(string packId)
    {
        var claim = Claims.FirstOrDefault(candidate => candidate.Pack == packId);
        return claim is not null && Claims.Count(candidate => candidate.Code == claim.Code) == 1 ? claim : null;
    }

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
    // jurisdiction_unspecified. Text that is not well-formed (a lone surrogate) is jurisdiction_invalid. A stub claim is
    // only selected when allowStub is true, else jurisdiction_stub_refused, so an example pack is never used by accident.
    public JurisdictionSelection Select(ProjectJurisdiction project, bool allowStub = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var (field, text) in new[] { ("code", project.Code), ("country", project.Country), ("municipality", project.Municipality) })
            if (text is not null && !WellFormed(text)) return Refuse(JurisdictionCodes.JurisdictionInvalid, field, field + " is not well-formed text");
        var selection = SelectAny(project);
        if (selection.Claim is { Stub: true } stub && !allowStub)
            return Refuse(JurisdictionCodes.JurisdictionStubRefused, stub.Code, "jurisdiction " + stub.Code + " is a stub example pack (" + stub.Pack + "), not real law; pass allowStub to use it");
        return selection;
    }

    JurisdictionSelection SelectAny(ProjectJurisdiction project)
    {
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

    static IEnumerable<string> CountryKeys(JurisdictionClaim claim) => claim.CountryNames.Select(Normalize).Append(claim.Country).Distinct(StringComparer.Ordinal);

    static IEnumerable<string> MunicipalityKeys(JurisdictionClaim claim) => claim.MunicipalityNames.Select(Normalize).Append(claim.Locality).Distinct(StringComparer.Ordinal);

    static bool NamesValid(IReadOnlyList<string>? names) => names is not null && names.All(name => name is not null && !string.IsNullOrWhiteSpace(name) && WellFormed(name));

    static string Printable(string? text) => text is null ? "(null)" : WellFormed(text) ? text : "(malformed)";

    // True when text has no lone UTF-16 surrogate, so it can be normalised and written as JSON.
    internal static bool WellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { index++; continue; }
            if (char.IsSurrogate(text[index])) return false;
        }
        return true;
    }

    static bool CountryMatches(JurisdictionClaim claim, string normalized) =>
        claim.Country == normalized || claim.CountryNames.Any(name => Normalize(name) == normalized);

    static bool MunicipalityMatches(JurisdictionClaim claim, string normalized) =>
        claim.Locality == normalized || claim.MunicipalityNames.Any(name => Normalize(name) == normalized);

    string Registered() => Codes.Count == 0 ? "(none)" : string.Join(", ", Codes);

    static bool Blank(string? text) => string.IsNullOrWhiteSpace(text);

    static JurisdictionSelection Refuse(string code, string subject, string detail) => new(null, "", new JurisdictionIssue(code, subject, detail));
}
