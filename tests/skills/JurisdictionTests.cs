using Bim.Core;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

// P10-06 oracle: the jurisdiction rule-pack registry. The checked-in CABA code pack and the fictitious stub pack
// xx.example.codigo-ejemplo both register through skills/<id>/jurisdiction.json; selection by code and by country and
// municipality metadata (accents, case, whitespace), typed refusals (unspecified, incomplete, invalid, unknown, ambiguous by
// duplicate claim or by shared metadata, conflict), registry issues (missing pack, non-code pack, duplicate claim), strict
// sidecar parsing and safe loading, the validation envelope (pack ids, versions, stub flag, source_confidence summary around
// the unchanged room report) and deterministic ordering and JSON. Unit-tested only.
static class JurisdictionTests
{
    const string Caba = "ar.caba.codigo-edificacion";
    const string Stub = "xx.example.codigo-ejemplo";

    static string Manifest(string id, string kind = "code", string? requires = null, string rules = "[]") =>
        "{\"manifest_version\":1,\"id\":\"" + id + "\",\"version\":\"1.2.3\",\"kind\":\"" + kind + "\",\"description\":\"fixture\",\"priority\":10"
        + (requires is null ? "" : ",\"requires\":[{\"id\":\"" + requires + "\",\"range\":\"*\"}]") + ",\"rules\":" + rules + "}";

    static JurisdictionClaim Claim(string code, string pack, string[]? countries = null, string[]? municipalities = null, bool stub = false) =>
        new(code, pack, countries ?? Array.Empty<string>(), municipalities ?? Array.Empty<string>(), stub, null);

    static SkillCatalog Catalog(params string[] manifests) => new(manifests.Select(SkillParser.Parse));

    static string Show(JurisdictionSelection selection) =>
        selection.Refusal is { } refusal ? refusal.Code + "@" + refusal.Subject + " (" + refusal.Detail + ")" : selection.Claim!.Code + " by " + selection.MatchedBy;

    static string Show(JurisdictionIssue? issue) => issue is null ? "(none)" : issue.Code + "@" + issue.Subject + " (" + issue.Detail + ")";

    static string Sidecar(Action<JsonObject>? change = null)
    {
        var node = new JsonObject
        {
            ["jurisdiction_version"] = 1,
            ["code"] = "xx.sample",
            ["pack"] = "xx.sample.code",
            ["country_names"] = new JsonArray("Sample Land"),
            ["municipality_names"] = new JsonArray("Sample Town"),
            ["stub"] = true,
        };
        change?.Invoke(node);
        return node.ToJsonString();
    }

    public static void Run(string root, Action<bool, string, string> report, Func<string> tempRoot)
    {
        void check(bool condition, string name, string detail = "") => report(condition, name, detail);
        // ---- Checked-in registry ----
        var skillsRoot = Path.Combine(root, "skills");
        var load = JurisdictionRegistry.Load(skillsRoot);
        check(load.Succeeded, "jurisdiction: skills/ loads with its jurisdiction sidecars", Show(load.Refusal));
        if (load.Registry is not { } registry) return;
        check(registry.Codes.SequenceEqual(new[] { "ar.caba", "xx.example" }) && registry.Claims.Select(claim => claim.Pack).SequenceEqual(new[] { Caba, Stub }),
            "jurisdiction: the registry holds ar.caba and the stub xx.example in ordinal order", string.Join(",", registry.Claims.Select(claim => claim.Code + "=" + claim.Pack)));
        check(registry.Issues.Count == 0 && registry.UnregisteredCodePacks.Count == 0, "jurisdiction: every checked-in code pack is registered without issues",
            string.Join("; ", registry.Issues.Select(issue => Show(issue))) + " unregistered: " + string.Join(",", registry.UnregisteredCodePacks));
        var cabaClaim = registry.Claims.FirstOrDefault(claim => claim.Code == "ar.caba");
        var stubClaim = registry.Claims.FirstOrDefault(claim => claim.Code == "xx.example");
        check(cabaClaim is { Stub: false } && stubClaim is { Stub: true } && stubClaim.Note is { } note && note.Contains("not real law", StringComparison.Ordinal),
            "jurisdiction: only the xx.example claim is marked stub and says it is not real law");
        var stubPack = registry.Catalog.Find(Stub);
        check(stubPack is { Kind: SkillKind.Code } && stubPack.Description.StartsWith("STUB / EXAMPLE ONLY", StringComparison.Ordinal) && stubPack.Description.Contains("fictitious", StringComparison.Ordinal)
            && stubPack.Rules.Count > 0 && stubPack.Rules.All(rule => SkillRoomValidator.ParseCitation(rule.Citation).Confidence == SkillRoomValidator.ConfidenceToVerify && rule.Message.StartsWith("STUB", StringComparison.Ordinal)),
            "jurisdiction: the stub pack is a code pack marked STUB / fictitious with every rule to-verify");

        // ---- Selection by code ----
        void Selects(ProjectJurisdiction project, string code, string matchedBy, string name)
        {
            var selection = registry.Select(project);
            check(selection.Succeeded && selection.Claim!.Code == code && selection.MatchedBy == matchedBy, "jurisdiction: " + name, Show(selection));
        }
        void Refuses(JurisdictionRegistry target, ProjectJurisdiction project, string code, string name)
        {
            var selection = target.Select(project);
            check(!selection.Succeeded && selection.Claim is null && selection.Refusal!.Code == code, "jurisdiction: " + name, Show(selection));
        }
        Selects(new ProjectJurisdiction("ar.caba"), "ar.caba", JurisdictionSelection.ByCode, "code ar.caba selects the CABA pack");
        Selects(new ProjectJurisdiction(" AR.Caba "), "ar.caba", JurisdictionSelection.ByCode, "codes are trimmed and lower-cased");
        Selects(new ProjectJurisdiction("xx.example"), "xx.example", JurisdictionSelection.ByCode, "code xx.example selects the stub pack");
        Selects(new ProjectJurisdiction("ar.caba", "Argentina", "CABA"), "ar.caba", JurisdictionSelection.ByCode, "a code with agreeing metadata selects by code");

        // ---- Selection by metadata ----
        Selects(new ProjectJurisdiction(null, "Argentina", "Ciudad Autónoma de Buenos Aires"), "ar.caba", JurisdictionSelection.ByMetadata, "country and municipality names select ar.caba (accents ignored)");
        Selects(new ProjectJurisdiction("", "  república   ARGENTINA ", "ciudad de buenos aires"), "ar.caba", JurisdictionSelection.ByMetadata, "metadata ignores case and whitespace runs; a blank code uses metadata");
        Selects(new ProjectJurisdiction(null, "ar", "caba"), "ar.caba", JurisdictionSelection.ByMetadata, "the code labels match as country and municipality");
        Selects(new ProjectJurisdiction(null, "Example Country", "Example City"), "xx.example", JurisdictionSelection.ByMetadata, "metadata selects the stub jurisdiction");

        // ---- Refusals ----
        Refuses(registry, new ProjectJurisdiction(), JurisdictionCodes.JurisdictionUnspecified, "nothing given is jurisdiction_unspecified");
        Refuses(registry, new ProjectJurisdiction(" ", "\t", ""), JurisdictionCodes.JurisdictionUnspecified, "blank fields are jurisdiction_unspecified");
        Refuses(registry, new ProjectJurisdiction(null, "Argentina"), JurisdictionCodes.JurisdictionIncomplete, "a country alone is jurisdiction_incomplete");
        Refuses(registry, new ProjectJurisdiction(null, null, "CABA"), JurisdictionCodes.JurisdictionIncomplete, "a municipality alone is jurisdiction_incomplete");
        Refuses(registry, new ProjectJurisdiction("caba"), JurisdictionCodes.JurisdictionInvalid, "a malformed code is jurisdiction_invalid");
        Refuses(registry, new ProjectJurisdiction("ar.caba.extra"), JurisdictionCodes.JurisdictionInvalid, "a three-label code is jurisdiction_invalid");
        Refuses(registry, new ProjectJurisdiction("ar.cordoba"), JurisdictionCodes.JurisdictionUnknown, "an unregistered code is jurisdiction_unknown");
        Refuses(registry, new ProjectJurisdiction(null, "Argentina", "Buenos Aires"), JurisdictionCodes.JurisdictionUnknown, "unmatched metadata is jurisdiction_unknown (Buenos Aires alone is not CABA)");
        Refuses(registry, new ProjectJurisdiction(null, "Example Country", "CABA"), JurisdictionCodes.JurisdictionUnknown, "country and municipality of different jurisdictions match nothing");
        Refuses(registry, new ProjectJurisdiction("ar.caba", "Example Country"), JurisdictionCodes.JurisdictionConflict, "a code with a contradicting country is jurisdiction_conflict");
        Refuses(registry, new ProjectJurisdiction("xx.example", null, "CABA"), JurisdictionCodes.JurisdictionConflict, "a code with a contradicting municipality is jurisdiction_conflict");
        var unknown = registry.Select(new ProjectJurisdiction("ar.cordoba"));
        check(unknown.Refusal is { Subject: "ar.cordoba" } refusal && refusal.Detail.EndsWith("registered: ar.caba, xx.example", StringComparison.Ordinal),
            "jurisdiction: unknown refusals name the code and list the registered jurisdictions", Show(unknown));

        // ---- Ambiguity and registry issues (in-memory catalogs) ----
        var duplicateCatalog = Catalog(Manifest("ar.caba.alpha"), Manifest("ar.caba.beta"), Manifest("xx.spring-a.code"), Manifest("xx.spring-b.code"),
            Manifest("xx.style.pack", "style"), Manifest("xx.loose.code"));
        var claims = new[]
        {
            Claim("ar.caba", "ar.caba.beta", new[] { "Argentina" }, new[] { "CABA" }),
            Claim("ar.caba", "ar.caba.alpha", new[] { "Argentina" }, new[] { "CABA" }),
            Claim("xx.spring-a", "xx.spring-a.code", new[] { "Fictionland" }, new[] { "Springfield" }),
            Claim("xx.spring-b", "xx.spring-b.code", new[] { "Fictionland" }, new[] { "Springfield" }),
            Claim("xx.style", "xx.style.pack"),
            Claim("xx.gone", "xx.gone.code"),
        };
        var ambiguous = new JurisdictionRegistry(duplicateCatalog, claims);
        check(ambiguous.Issues.Select(issue => issue.Code + "@" + issue.Subject).SequenceEqual(new[] { "claim_duplicate@ar.caba", "pack_missing@xx.gone.code", "pack_not_code@xx.style.pack" }),
            "jurisdiction: registry issues report the duplicate claim, the missing pack and the non-code pack in order", string.Join("; ", ambiguous.Issues.Select(issue => Show(issue))));
        check(ambiguous.UnregisteredCodePacks.SequenceEqual(new[] { "xx.loose.code" }) && ambiguous.Codes.SequenceEqual(new[] { "ar.caba", "xx.spring-a", "xx.spring-b" }),
            "jurisdiction: dropped claims are not registered and a code pack without a claim is listed as unregistered", string.Join(",", ambiguous.UnregisteredCodePacks) + " / " + string.Join(",", ambiguous.Codes));
        Refuses(ambiguous, new ProjectJurisdiction("ar.caba"), JurisdictionCodes.JurisdictionAmbiguous, "two packs claiming one code make it jurisdiction_ambiguous");
        Refuses(ambiguous, new ProjectJurisdiction(null, "Argentina", "CABA"), JurisdictionCodes.JurisdictionAmbiguous, "metadata reaching a duplicated code is jurisdiction_ambiguous");
        Refuses(ambiguous, new ProjectJurisdiction(null, "Fictionland", "Springfield"), JurisdictionCodes.JurisdictionAmbiguous, "metadata matching two jurisdictions is jurisdiction_ambiguous");
        Refuses(ambiguous, new ProjectJurisdiction("xx.style"), JurisdictionCodes.JurisdictionUnknown, "a claim on a non-code pack never selects");
        var ambiguousCode = ambiguous.Select(new ProjectJurisdiction("ar.caba"));
        check(ambiguousCode.Refusal?.Detail == "claimed by ar.caba.alpha, ar.caba.beta", "jurisdiction: an ambiguous refusal names the competing packs in ordinal order", Show(ambiguousCode));
        var spring = ambiguous.Select(new ProjectJurisdiction(null, "Fictionland", "Springfield"));
        check(spring.Refusal?.Detail == "matches xx.spring-a (xx.spring-a.code), xx.spring-b (xx.spring-b.code)", "jurisdiction: metadata ambiguity names every matching jurisdiction", Show(spring));
        Selects2(ambiguous, new ProjectJurisdiction("xx.spring-b"), "xx.spring-b", report);

        // ---- Determinism ----
        var reversed = new JurisdictionRegistry(duplicateCatalog, Enumerable.Reverse(claims));
        check(reversed.Claims.SequenceEqual(ambiguous.Claims) && reversed.Issues.SequenceEqual(ambiguous.Issues)
            && Show(reversed.Select(new ProjectJurisdiction("ar.caba"))) == Show(ambiguousCode) && Show(reversed.Select(new ProjectJurisdiction(null, "Fictionland", "Springfield"))) == Show(spring),
            "jurisdiction: claim order does not change the registry or its selections");

        // ---- Validation envelope ----
        var cabaManifest = registry.Catalog.Find(Caba)!;
        var cabaRooms = new[]
        {
            RoomFacts.Rectangle("r2", "Estar comedor", Length.FromMetres(3), Length.FromMetres(3.5), Length.FromMetres(2.6)),
            RoomFacts.Rectangle("r1", "Dormitorio", Length.FromMetres(2.5), Length.FromMetres(2.5), Length.FromMetres(2.6)),
            RoomFacts.Rectangle("r3", "Deposito X", Length.FromMetres(1), Length.FromMetres(1)),
        };
        var cabaResult = JurisdictionValidator.Validate(registry, new ProjectJurisdiction(null, "Argentina", "CABA"), cabaRooms);
        var direct = SkillRoomValidator.Validate(cabaManifest, cabaRooms);
        check(cabaResult.Succeeded && cabaResult.Rooms is { } cabaReport && cabaReport.Findings.SequenceEqual(direct.Findings) && cabaReport.UnmatchedRoomIds.SequenceEqual(direct.UnmatchedRoomIds),
            "jurisdiction: the envelope carries the unchanged room validation report of the selected pack", Show(cabaResult.Refusal));
        var expected = JurisdictionConfidenceSummary.Of(cabaManifest);
        check(cabaResult.Packs.Count == 1 && cabaResult.Packs[0] is { Id: Caba, Version: "0.1.0", Kind: "code", Jurisdiction: "ar.caba", Stub: false } cabaPack
            && cabaPack.SourceConfidence == expected && expected.Rules == cabaManifest.Rules.Count && expected.Cited + expected.OwnerConfirmed + expected.ToVerify == expected.Rules && expected.Cited > 0,
            "jurisdiction: the envelope reports the CABA pack id, version and a source_confidence summary that adds up to its rules",
            string.Join(",", cabaResult.Packs.Select(pack => pack.Id + "@" + pack.Version + " " + pack.SourceConfidence)));
        check(new JurisdictionConfidenceSummary(3, 1, 1, 1).Lowest == "to-verify" && new JurisdictionConfidenceSummary(2, 1, 1, 0).Lowest == "owner-confirmed"
            && new JurisdictionConfidenceSummary(2, 2, 0, 0).Lowest == "cited" && new JurisdictionConfidenceSummary(0, 0, 0, 0).Lowest == "none",
            "jurisdiction: the lowest source_confidence is to-verify, then owner-confirmed, then cited, or none without rules");
        var cabaJson = cabaResult.ToJson();
        JsonNode? cabaNode = null;
        try { cabaNode = JsonNode.Parse(cabaJson); } catch (JsonException) { }
        check(cabaNode is not null && (int?)cabaNode["envelope_version"] == 1 && (string?)cabaNode["jurisdiction"]?["code"] == "ar.caba" && (string?)cabaNode["jurisdiction"]?["matched_by"] == "metadata"
            && (string?)cabaNode["packs"]?[0]?["id"] == Caba && (string?)cabaNode["packs"]?[0]?["version"] == "0.1.0"
            && (int?)cabaNode["packs"]?[0]?["source_confidence"]?["cited"] == expected.Cited && (int?)cabaNode["packs"]?[0]?["source_confidence"]?["to-verify"] == expected.ToVerify
            && (string?)cabaNode["packs"]?[0]?["source_confidence"]?["lowest"] == expected.Lowest
            && cabaNode["rooms"]?["findings"]?.AsArray().Count == direct.Findings.Count && (string?)cabaNode["rooms"]?["unmatched_room_ids"]?[0] == "r3",
            "jurisdiction: the envelope JSON names the jurisdiction, packs, versions, confidence summary and findings", cabaJson.Length > 600 ? cabaJson[..600] : cabaJson);
        var cabaAgain = JurisdictionValidator.Validate(registry, new ProjectJurisdiction(null, "Argentina", "CABA"), Enumerable.Reverse(cabaRooms)).ToJson();
        var previous = CultureInfo.CurrentCulture;
        string germanJson;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            germanJson = JurisdictionValidator.Validate(registry, new ProjectJurisdiction(null, "Argentina", "CABA"), cabaRooms).ToJson();
        }
        finally { CultureInfo.CurrentCulture = previous; }
        check(cabaAgain == cabaJson && germanJson == cabaJson && cabaJson.Contains("\"measured\":10.5", StringComparison.Ordinal),
            "jurisdiction: envelope JSON is byte-identical across room order and culture");

        var stubRooms = new[] { RoomFacts.Rectangle("s1", "Living", Length.FromMetres(3), Length.FromMetres(3), Length.FromMetres(2.6)) };
        var stubResult = JurisdictionValidator.Validate(registry, new ProjectJurisdiction("xx.example"), stubRooms);
        check(stubResult.Succeeded && stubResult.Packs.Count == 1 && stubResult.Packs[0] is { Id: Stub, Version: "0.0.1", Jurisdiction: "xx.example", Stub: true } stubSummary
            && stubSummary.SourceConfidence == new JurisdictionConfidenceSummary(stubPack!.Rules.Count, 0, 0, stubPack.Rules.Count) && stubSummary.SourceConfidence.Lowest == "to-verify",
            "jurisdiction: the stub jurisdiction validates with its own pack, flagged stub and wholly to-verify", Show(stubResult.Refusal));
        check(stubResult.Rooms is { } stubReport && stubReport.Failures.Select(finding => finding.RuleId).SequenceEqual(new[] { Stub + "/living-area-min" })
            && stubReport.Findings.Select(finding => finding.RuleId).SequenceEqual(new[] { Stub + "/living-area-min", Stub + "/living-height-min" }) && !stubReport.Compliant,
            "jurisdiction: the stub pack's rules, not CABA's, apply to the xx.example project",
            stubResult.Rooms is null ? "" : string.Join(",", stubResult.Rooms.Findings.Select(finding => finding.RuleId + "=" + finding.Outcome)));
        var stubJson = stubResult.ToJson();
        check(stubJson.Contains("\"jurisdiction\":{\"code\":\"xx.example\",\"matched_by\":\"code\",\"pack\":\"" + Stub + "\",\"stub\":true}", StringComparison.Ordinal),
            "jurisdiction: the envelope JSON flags the stub jurisdiction", stubJson.Length > 300 ? stubJson[..300] : stubJson);

        var refusedResult = JurisdictionValidator.Validate(registry, new ProjectJurisdiction("ar.cordoba"), cabaRooms);
        check(!refusedResult.Succeeded && refusedResult.Rooms is null && refusedResult.Packs.Count == 0 && refusedResult.Refusal?.Code == JurisdictionCodes.JurisdictionUnknown
            && refusedResult.ToJson().StartsWith("{\"envelope_version\":1,\"refusal\":{\"code\":\"jurisdiction_unknown\",\"subject\":\"ar.cordoba\"", StringComparison.Ordinal),
            "jurisdiction: a refused selection validates nothing and its envelope carries the typed refusal", refusedResult.ToJson());
        var duplicateRooms = JurisdictionValidator.Validate(registry, new ProjectJurisdiction("ar.caba"), new[] { cabaRooms[0], cabaRooms[0] });
        check(duplicateRooms.Refusal?.Code == JurisdictionCodes.InputInvalid, "jurisdiction: duplicate room ids are refused as input_invalid", Show(duplicateRooms.Refusal));
        var badParameter = JurisdictionValidator.Validate(registry, new ProjectJurisdiction("ar.caba"), cabaRooms, new Dictionary<string, double> { [Caba + "/corredor_lado_min_m"] = 99 });
        check(badParameter.Refusal?.Code == JurisdictionCodes.InputInvalid, "jurisdiction: an out-of-range parameter override is refused as input_invalid", Show(badParameter.Refusal));
        var unresolvable = new JurisdictionRegistry(Catalog(Manifest("xx.needs.code", requires: "xx.absent.skill")), new[] { Claim("xx.needs", "xx.needs.code") });
        var unresolved = JurisdictionValidator.Validate(unresolvable, new ProjectJurisdiction("xx.needs"), Array.Empty<RoomFacts>());
        check(unresolved.Refusal is { Code: JurisdictionCodes.PackUnresolved, Subject: "xx.needs.code" } unresolvedIssue && unresolvedIssue.Detail.Contains(SkillCodes.DependencyMissing, StringComparison.Ordinal),
            "jurisdiction: a selected pack that does not resolve is pack_unresolved", Show(unresolved.Refusal));
        var withRequirement = new JurisdictionRegistry(Catalog(Manifest("xx.base.qa", "qa", rules: "[{\"id\":\"r\",\"severity\":\"info\",\"target\":{\"room\":\"*\"},\"check\":{\"op\":\"min\",\"property\":\"area_m2\",\"value\":1},\"message\":\"m\",\"citation\":\"A; source_confidence=cited\"}]"),
            Manifest("xx.city.code", requires: "xx.base.qa")), new[] { Claim("xx.city", "xx.city.code", stub: true) });
        var required = JurisdictionValidator.Validate(withRequirement, new ProjectJurisdiction("xx.city"), Array.Empty<RoomFacts>());
        check(required.Succeeded && required.Packs.Select(pack => pack.Id + ":" + (pack.Jurisdiction ?? "-") + ":" + pack.Stub + ":" + pack.SourceConfidence.Lowest)
                .SequenceEqual(new[] { "xx.base.qa:-:False:cited", "xx.city.code:xx.city:True:none" }),
            "jurisdiction: required packs are reported in dependency order with their own versions and confidence",
            string.Join(",", required.Packs.Select(pack => pack.Id + ":" + (pack.Jurisdiction ?? "-") + ":" + pack.Stub + ":" + pack.SourceConfidence.Lowest)) + Show(required.Refusal));

        // ---- Sidecar parsing ----
        check(JurisdictionClaim.TryParse(Sidecar(), out var parsed, out var parseIssue) && parsed is { Code: "xx.sample", Pack: "xx.sample.code", Stub: true, Note: null }
            && parsed.Country == "xx" && parsed.Locality == "sample" && parsed.CountryNames.SequenceEqual(new[] { "Sample Land" }),
            "jurisdiction: a well-formed sidecar parses", Show(parseIssue));
        void ParseRefuses(string text, string code, string subject, string name)
        {
            var ok = JurisdictionClaim.TryParse(text, out var claim, out var issue);
            check(!ok && claim is null && issue?.Code == code && issue.Subject == subject, "jurisdiction: sidecar " + name, Show(issue));
        }
        ParseRefuses("{", JurisdictionCodes.JsonInvalid, "$", "malformed JSON is json_invalid");
        ParseRefuses("[]", JurisdictionCodes.TypeInvalid, "$", "a non-object is type_invalid");
        ParseRefuses(Sidecar(node => node["extra"] = 1), JurisdictionCodes.MemberUnknown, "$.extra", "an unknown member is member_unknown");
        ParseRefuses(Sidecar().Replace("\"stub\":true", "\"stub\":true,\"stub\":false", StringComparison.Ordinal), JurisdictionCodes.MemberDuplicate, "$.stub", "a repeated member is member_duplicate");
        ParseRefuses(Sidecar(node => node.Remove("code")), JurisdictionCodes.MemberMissing, "$.code", "a missing code is member_missing");
        ParseRefuses(Sidecar(node => node.Remove("stub")), JurisdictionCodes.MemberMissing, "$.stub", "a missing stub flag is member_missing");
        ParseRefuses(Sidecar(node => node["jurisdiction_version"] = 2), JurisdictionCodes.VersionUnsupported, "$.jurisdiction_version", "version 2 is version_unsupported");
        ParseRefuses(Sidecar().Replace("\"jurisdiction_version\":1", "\"jurisdiction_version\":1.0", StringComparison.Ordinal), JurisdictionCodes.VersionUnsupported, "$.jurisdiction_version", "version 1.0 is version_unsupported");
        ParseRefuses(Sidecar(node => node["code"] = "XX.sample"), JurisdictionCodes.CodeInvalid, "$.code", "an upper-case code is code_invalid");
        ParseRefuses(Sidecar(node => node["code"] = "xxx.sample"), JurisdictionCodes.CodeInvalid, "$.code", "a three-letter country is code_invalid");
        ParseRefuses(Sidecar(node => node["pack"] = "xx.other.code"), JurisdictionCodes.PackMismatch, "$.pack", "a pack outside its code is pack_mismatch");
        ParseRefuses(Sidecar(node => node["pack"] = "Bad Id"), JurisdictionCodes.ValueInvalid, "$.pack", "a malformed pack id is value_invalid");
        ParseRefuses(Sidecar(node => node["stub"] = "yes"), JurisdictionCodes.TypeInvalid, "$.stub", "a non-boolean stub is type_invalid");
        ParseRefuses(Sidecar(node => node["country_names"] = "Sample"), JurisdictionCodes.TypeInvalid, "$.country_names", "non-array names are type_invalid");
        ParseRefuses(Sidecar(node => node["municipality_names"] = new JsonArray("Sámple Town", "sample  town")), JurisdictionCodes.EntryDuplicate, "$.municipality_names[1]", "names equal after normalisation are entry_duplicate");
        ParseRefuses(Sidecar(node => node["municipality_names"] = new JsonArray(" ")), JurisdictionCodes.ValueInvalid, "$.municipality_names[0]", "a blank name is value_invalid");
        ParseRefuses(Sidecar(node => node["note"] = ""), JurisdictionCodes.ValueInvalid, "$.note", "a blank note is value_invalid");
        ParseRefuses(Sidecar().Replace("\"stub\":true", "\"stub\":true,\"note\":\"\\ud800\"", StringComparison.Ordinal), JurisdictionCodes.ValueInvalid, "$.note", "a lone surrogate is value_invalid");
        ParseRefuses(new string(' ', JurisdictionClaim.MaxInputChars + 1), JurisdictionCodes.InputTooLarge, "$", "oversized text is input_too_large");

        // ---- Loading from a directory ----
        string Fixture(params (string Id, string Kind, string? Sidecar)[] packs)
        {
            var directory = tempRoot();
            foreach (var (id, kind, sidecar) in packs)
            {
                Directory.CreateDirectory(Path.Combine(directory, id));
                File.WriteAllText(Path.Combine(directory, id, "skill.json"), Manifest(id, kind));
                if (sidecar is not null) File.WriteAllText(Path.Combine(directory, id, "jurisdiction.json"), sidecar);
            }
            return directory;
        }
        var good = JurisdictionRegistry.Load(Fixture(("xx.sample.code", "code", Sidecar()), ("xx.plain.code", "code", null)));
        check(good.Registry is { } goodRegistry && goodRegistry.Codes.SequenceEqual(new[] { "xx.sample" }) && goodRegistry.UnregisteredCodePacks.SequenceEqual(new[] { "xx.plain.code" }),
            "jurisdiction: a fixture directory loads claims from sidecars and lists code packs without one", Show(good.Refusal));
        var misplaced = JurisdictionRegistry.Load(Fixture(("xx.sample.other", "code", Sidecar(node => node["pack"] = "xx.sample.code"))));
        check(misplaced.Refusal is { Code: JurisdictionCodes.PackMismatch, Subject: "xx.sample.other/jurisdiction.json" }, "jurisdiction: a sidecar naming another pack than its directory is pack_mismatch", Show(misplaced.Refusal));
        var badSidecar = JurisdictionRegistry.Load(Fixture(("xx.sample.code", "code", Sidecar(node => node["extra"] = true))));
        check(badSidecar.Refusal is { Code: JurisdictionCodes.MemberUnknown, Subject: "xx.sample.code/jurisdiction.json" } badIssue && badIssue.Detail.StartsWith("$.extra", StringComparison.Ordinal),
            "jurisdiction: sidecar parse refusals name the file and the member path", Show(badSidecar.Refusal));
        var emptySidecar = JurisdictionRegistry.Load(Fixture(("xx.sample.code", "code", "")));
        check(emptySidecar.Refusal?.Code == JurisdictionCodes.NotRegularFile, "jurisdiction: an empty sidecar is not_regular_file", Show(emptySidecar.Refusal));
        var nonCode = JurisdictionRegistry.Load(Fixture(("xx.sample.code", "qa", Sidecar())));
        check(nonCode.Registry is { Claims.Count: 0 } nonCodeRegistry && nonCodeRegistry.Issues.Select(issue => issue.Code).SequenceEqual(new[] { JurisdictionCodes.PackNotCode }),
            "jurisdiction: a sidecar on a non-code skill loads as a pack_not_code registry issue", Show(nonCode.Refusal));
        var badSkills = tempRoot();
        Directory.CreateDirectory(Path.Combine(badSkills, "Not An Id"));
        var badSkillsLoad = JurisdictionRegistry.Load(badSkills);
        check(badSkillsLoad.Refusal is { Code: JurisdictionCodes.SkillsInvalid } skillsIssue && skillsIssue.Detail.StartsWith(SkillCodes.IdInvalid, StringComparison.Ordinal),
            "jurisdiction: a skills directory SkillLoader refuses is skills_invalid with the loader code", Show(badSkillsLoad.Refusal));
        check(JurisdictionRegistry.Load(Path.Combine(badSkills, "missing")).Refusal is { Code: JurisdictionCodes.SkillsInvalid } missingIssue && missingIssue.Detail.StartsWith(SkillCodes.RootMissing, StringComparison.Ordinal),
            "jurisdiction: a missing skills root is skills_invalid (root_missing)");
        var linked = Fixture(("xx.sample.code", "code", null));
        var target = Path.Combine(tempRoot(), "outside.json");
        File.WriteAllText(target, Sidecar());
        var linkMade = false;
        try { File.CreateSymbolicLink(Path.Combine(linked, "xx.sample.code", "jurisdiction.json"), target); linkMade = true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
        if (linkMade)
        {
            var linkedLoad = JurisdictionRegistry.Load(linked);
            check(linkedLoad.Refusal?.Code == JurisdictionCodes.PathUnsafe, "jurisdiction: a symlinked sidecar is path_unsafe", Show(linkedLoad.Refusal));
        }
        else check(OperatingSystem.IsWindows() && !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal),
            "jurisdiction: symlink fixture available", "could not create a symbolic link");

        // ---- Checked-in registry loads deterministically ----
        var again = JurisdictionRegistry.Load(skillsRoot).Registry;
        check(again is not null && again.Claims.SequenceEqual(registry.Claims, ClaimComparer.Instance), "jurisdiction: loading skills/ twice gives the same registry");
    }

    static void Selects2(JurisdictionRegistry registry, ProjectJurisdiction project, string code, Action<bool, string, string> check)
    {
        var selection = registry.Select(project);
        check(selection.Succeeded && selection.Claim!.Code == code, "jurisdiction: an unambiguous code still selects beside ambiguous ones", Show(selection));
    }

    sealed class ClaimComparer : IEqualityComparer<JurisdictionClaim>
    {
        public static readonly ClaimComparer Instance = new();

        public bool Equals(JurisdictionClaim? left, JurisdictionClaim? right) => left is not null && right is not null && left.Code == right.Code && left.Pack == right.Pack
            && left.Stub == right.Stub && left.Note == right.Note && left.CountryNames.SequenceEqual(right.CountryNames) && left.MunicipalityNames.SequenceEqual(right.MunicipalityNames);

        public int GetHashCode(JurisdictionClaim claim) => StringComparer.Ordinal.GetHashCode(claim.Code);
    }
}
