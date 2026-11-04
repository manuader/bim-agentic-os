using System.Text;
using System.Text.Json;
namespace Bim.Core;

// P10-06: validates rooms against the code pack selected for a project's jurisdiction. The existing
// SkillRoomValidator report is carried unchanged inside an envelope that also names the jurisdiction, how it was matched,
// and every resolved pack (dependency order) with its version, stub flag and a source_confidence summary of its rules.
// ToJson writes a new standalone format (envelope_version 1); no existing persisted format changes.

// Rule counts per source_confidence level (parsed from rule citations by SkillRoomValidator.ParseCitation). Lowest is
// "to-verify" when any rule is to-verify, else "owner-confirmed" when any is, else "cited" when there are rules, else "none".
public sealed record JurisdictionConfidenceSummary(int Rules, int Cited, int OwnerConfirmed, int ToVerify)
{
    public const string NoRules = "none";

    public string Lowest => ToVerify > 0 ? SkillRoomValidator.ConfidenceToVerify
        : OwnerConfirmed > 0 ? SkillRoomValidator.ConfidenceOwnerConfirmed
        : Rules > 0 ? SkillRoomValidator.ConfidenceCited : NoRules;

    public static JurisdictionConfidenceSummary Of(SkillManifest pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        int cited = 0, owner = 0, verify = 0;
        foreach (var rule in pack.Rules)
        {
            switch (SkillRoomValidator.ParseCitation(rule.Citation).Confidence)
            {
                case SkillRoomValidator.ConfidenceCited: cited++; break;
                case SkillRoomValidator.ConfidenceOwnerConfirmed: owner++; break;
                default: verify++; break;
            }
        }
        return new JurisdictionConfidenceSummary(pack.Rules.Count, cited, owner, verify);
    }
}

// Jurisdiction is the code of the registry claim on this pack (the selected one, or a required pack that is itself a
// jurisdiction's code pack), else null. Stub comes from that claim and is false without one.
public sealed record JurisdictionPackSummary(string Id, string Version, string Kind, string? Jurisdiction, bool Stub, JurisdictionConfidenceSummary SourceConfidence);

// Either Refusal is set (and Packs is empty, Rooms null) or Selection succeeded and Rooms holds the existing report.
public sealed record JurisdictionValidation(JurisdictionSelection Selection, IReadOnlyList<JurisdictionPackSummary> Packs, RoomValidationReport? Rooms, JurisdictionIssue? Refusal)
{
    public const int EnvelopeVersion = 1;

    public bool Succeeded => Refusal is null;

    // True when any resolved pack is a stub example: the findings then come (partly) from invented rules.
    public bool StubRules => Packs.Any(pack => pack.Stub);

    // Deterministic UTF-8 JSON text: members in fixed order, findings in report order, numbers round-trip invariant.
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("envelope_version", EnvelopeVersion);
            if (Refusal is not null)
            {
                writer.WriteStartObject("refusal");
                writer.WriteString("code", Refusal.Code);
                writer.WriteString("subject", Refusal.Subject);
                writer.WriteString("detail", Refusal.Detail);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            else
            {
                var claim = Selection.Claim!;
                writer.WriteStartObject("jurisdiction");
                writer.WriteString("code", claim.Code);
                writer.WriteString("matched_by", Selection.MatchedBy);
                writer.WriteString("pack", claim.Pack);
                writer.WriteBoolean("stub", claim.Stub);
                writer.WriteEndObject();
                writer.WriteStartArray("packs");
                foreach (var pack in Packs)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", pack.Id);
                    writer.WriteString("version", pack.Version);
                    writer.WriteString("kind", pack.Kind);
                    if (pack.Jurisdiction is null) writer.WriteNull("jurisdiction"); else writer.WriteString("jurisdiction", pack.Jurisdiction);
                    writer.WriteBoolean("stub", pack.Stub);
                    writer.WriteStartObject("source_confidence");
                    writer.WriteNumber("rules", pack.SourceConfidence.Rules);
                    writer.WriteNumber(SkillRoomValidator.ConfidenceCited, pack.SourceConfidence.Cited);
                    writer.WriteNumber(SkillRoomValidator.ConfidenceOwnerConfirmed, pack.SourceConfidence.OwnerConfirmed);
                    writer.WriteNumber(SkillRoomValidator.ConfidenceToVerify, pack.SourceConfidence.ToVerify);
                    writer.WriteString("lowest", pack.SourceConfidence.Lowest);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                var rooms = Rooms!;
                writer.WriteStartObject("rooms");
                writer.WriteBoolean("stub_rules", StubRules);
                writer.WriteBoolean("complete", rooms.Complete);
                writer.WriteBoolean("compliant", rooms.Compliant);
                writer.WriteStartArray("unmatched_room_ids");
                foreach (var id in rooms.UnmatchedRoomIds) writer.WriteStringValue(id);
                writer.WriteEndArray();
                writer.WriteStartArray("findings");
                foreach (var finding in rooms.Findings)
                {
                    writer.WriteStartObject();
                    writer.WriteString("room_id", finding.RoomId);
                    writer.WriteString("room_name", finding.RoomName);
                    writer.WriteString("rule_id", finding.RuleId);
                    writer.WriteString("outcome", Outcome(finding.Outcome));
                    writer.WriteString("severity", SkillParser.SeverityName(finding.Severity));
                    writer.WriteString("property", finding.Property);
                    Number(writer, "measured", finding.Measured);
                    Number(writer, "required", finding.Required);
                    writer.WriteString("article", finding.Article);
                    writer.WriteString("source_confidence", finding.SourceConfidence);
                    if (finding.Source is null) writer.WriteNull("source"); else writer.WriteString("source", finding.Source);
                    if (finding.ConfirmedBy is not null) writer.WriteString("confirmed_by", finding.ConfirmedBy);
                    if (finding.ConfirmedOn is not null) writer.WriteString("confirmed_on", finding.ConfirmedOn);
                    writer.WriteString("detail", finding.Detail);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Outcome(RoomCheckOutcome outcome) => outcome switch
    {
        RoomCheckOutcome.Pass => "pass",
        RoomCheckOutcome.Fail => "fail",
        _ => "not_evaluable",
    };

    static void Number(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number) writer.WriteNumber(name, number); else writer.WriteNull(name);
    }
}

public static class JurisdictionValidator
{
    // Selects the pack for project, resolves it with its requirements from the registry catalog and validates rooms.
    // Refusals: the selection refusal, pack_unresolved (resolver issues in Detail), input_invalid (duplicate room ids or a
    // parameter override the room validator rejects). Never throws for bad input other than null arguments.
    public static JurisdictionValidation Validate(JurisdictionRegistry registry, ProjectJurisdiction project, IEnumerable<RoomFacts> rooms, IReadOnlyDictionary<string, double>? parameters = null, bool allowStub = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(rooms);
        var selection = registry.Select(project, allowStub);
        if (!selection.Succeeded) return Refused(selection, selection.Refusal!);
        var claim = selection.Claim!;
        var resolution = SkillResolver.Resolve(registry.Catalog, new[] { new SkillRequest(claim.Pack) });
        if (!resolution.Succeeded)
            return Refused(selection, new JurisdictionIssue(JurisdictionCodes.PackUnresolved, claim.Pack, string.Join("; ", resolution.Issues.Select(issue => issue.Code + "@" + issue.Subject))));
        // Each resolved pack reports its own claim (a required pack may itself be a jurisdiction's code pack, stub or not).
        var claims = resolution.Skills.Select(skill => skill.Id == claim.Pack ? claim : registry.ClaimFor(skill.Id)).ToArray();
        if (!allowStub && claims.FirstOrDefault(candidate => candidate is { Stub: true }) is { } stubRequirement)
            return Refused(selection, new JurisdictionIssue(JurisdictionCodes.JurisdictionStubRefused, stubRequirement.Code, "pack " + claim.Pack + " requires the stub example pack " + stubRequirement.Pack + ", not real law; pass allowStub to use it"));
        var list = new List<RoomFacts>();
        foreach (var room in rooms)
        {
            if (room is null) return Refused(selection, new JurisdictionIssue(JurisdictionCodes.InputInvalid, claim.Pack, "a room is null"));
            if (!JurisdictionRegistry.WellFormed(room.Id) || !JurisdictionRegistry.WellFormed(room.Name))
                return Refused(selection, new JurisdictionIssue(JurisdictionCodes.InputInvalid, claim.Pack, "room id or name is not well-formed text (lone surrogate)"));
            list.Add(room);
        }
        if (parameters is not null && parameters.Keys.Any(key => key is null || !JurisdictionRegistry.WellFormed(key)))
            return Refused(selection, new JurisdictionIssue(JurisdictionCodes.InputInvalid, claim.Pack, "a parameter override key is not well-formed text"));
        RoomValidationReport report;
        try { report = SkillRoomValidator.Validate(resolution, list, parameters); }
        catch (ArgumentException error) { return Refused(selection, new JurisdictionIssue(JurisdictionCodes.InputInvalid, claim.Pack, error.Message)); }
        var packs = resolution.Skills.Select((skill, index) =>
            new JurisdictionPackSummary(skill.Id, skill.Version.ToString(), SkillParser.KindName(skill.Kind), claims[index]?.Code, claims[index]?.Stub ?? false, JurisdictionConfidenceSummary.Of(skill))).ToArray();
        return new JurisdictionValidation(selection, packs, report, null);
    }

    static JurisdictionValidation Refused(JurisdictionSelection selection, JurisdictionIssue refusal) =>
        new(selection, Array.Empty<JurisdictionPackSummary>(), null, refusal);
}
